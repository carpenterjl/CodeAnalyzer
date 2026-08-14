using CodeAnalyzer.Core.Analysis;
using CodeAnalyzer.Core.Domain;
using TreeSitter;
using TsLanguage = TreeSitter.Language;

namespace CodeAnalyzer.Parsing;

/// <summary>
/// Query-driven symbol and reference extraction. One instance per worker thread: it owns
/// a native parser and compiled queries, which are not documented as thread-safe.
/// </summary>
public sealed class TreeSitterAnalyzer : ILanguageAnalyzer, IDisposable
{
    private readonly LanguageDefinition _definition;
    private readonly TsLanguage _language;
    private readonly Parser _parser;
    private readonly Query _symbolQuery;
    private readonly Query? _referenceQuery;

    private bool _disposed;

    public TreeSitterAnalyzer(LanguageDefinition definition)
    {
        _definition = definition;
        var pack = QueryPack.Load(definition.QueryPackName);

        _language = new TsLanguage(definition.GrammarId);
        _parser = new Parser(_language);
        _symbolQuery = new Query(_language, pack.SymbolQuery);
        _referenceQuery = string.IsNullOrWhiteSpace(pack.ReferenceQuery)
            ? null
            : new Query(_language, pack.ReferenceQuery);
    }

    public string Language => _definition.Name;

    public ParseResult Analyze(string relativePath, string source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var tree = _parser.Parse(source);
            if (tree is null)
            {
                return Failed(relativePath, "Parser returned no tree.");
            }

            var symbols = ExtractSymbols(tree, cancellationToken);
            AssignContainers(symbols);

            var references = ExtractReferences(tree, symbols, cancellationToken);

            return new ParseResult
            {
                RelativePath = relativePath,
                Language = _definition.Name,
                ContentHash = [],
                Symbols = symbols.Select(s => s.Record).ToList(),
                References = references,
                // tree-sitter recovers from syntax errors, so a partial tree still yields
                // usable symbols. The status records that the file was imperfect.
                Status = tree.RootNode.HasError ? FileStatus.ParseError : FileStatus.Ok,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(relativePath, ex.Message);
        }
    }

    private ParseResult Failed(string relativePath, string message) => new()
    {
        RelativePath = relativePath,
        Language = _definition.Name,
        ContentHash = [],
        Status = FileStatus.ParseError,
        ErrorMessage = message,
    };

    /// <summary>A symbol plus the span of its name, which reference filtering needs.</summary>
    private sealed record ExtractedSymbol(SymbolRecord Record, int NameStartOffset, int NameEndOffset);

    private List<ExtractedSymbol> ExtractSymbols(Tree tree, CancellationToken cancellationToken)
    {
        var results = new List<ExtractedSymbol>();

        // Name offset to index in `results`. Several patterns declaring the same name at the
        // same place is deliberate — see CaptureNames.Specificity(SymbolKind) — so the list
        // keeps one entry per position and the strongest claim wins. An index rather than a
        // dictionary of records because container linkage and reference attribution both
        // address symbols by list position.
        var byNameOffset = new Dictionary<int, int>();

        // Modifier keywords per name offset, keyed by their own offset so the same keyword
        // contributed by several overlapping patterns is kept once, in source order. They
        // accumulate beside the records rather than on them because tree-sitter reports a
        // declaration with N modifiers as N separate matches — no single match sees them
        // all — and because the winning pattern for a position must not depend on which
        // modifier its match happened to carry.
        var modifiersByName = new Dictionary<int, SortedDictionary<int, string>>();

        using var cursor = _symbolQuery.Execute(tree.RootNode);

        // Matches (not captures) keep a pattern's name/value/type captures grouped together.
        foreach (var match in cursor.Matches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Node? definitionNode = null;
            var kind = SymbolKind.Unknown;
            var isDefinition = true;
            Node? nameNode = null;
            Node? valueNode = null;
            Node? typeNode = null;
            Node? parametersNode = null;
            List<Node>? modifierNodes = null;

            foreach (var capture in match.Captures)
            {
                if (CaptureNames.TryGetSymbolKind(capture.Name, out var capturedKind, out var capturedIsDefinition))
                {
                    definitionNode = capture.Node;
                    kind = capturedKind;
                    isDefinition = capturedIsDefinition;
                    continue;
                }

                switch (capture.Name)
                {
                    case CaptureNames.Name:
                        nameNode = capture.Node;
                        break;
                    case CaptureNames.Value:
                        valueNode = capture.Node;
                        break;
                    case CaptureNames.Type:
                        typeNode = capture.Node;
                        break;
                    case CaptureNames.Parameters:
                        parametersNode = capture.Node;
                        break;
                    case CaptureNames.Modifier:
                        // A list, unlike the slots above: one pattern can capture several
                        // modifier nodes in a single match.
                        (modifierNodes ??= []).Add(capture.Node);
                        break;
                }
            }

            if (definitionNode is null)
            {
                continue;
            }

            // Single-node patterns capture the definition and the name on the same node.
            nameNode ??= definitionNode;

            var name = nameNode.Text;
            if (string.IsNullOrWhiteSpace(name) || IsUntrustworthy(definitionNode, nameNode))
            {
                continue;
            }

            if (modifierNodes is not null)
            {
                if (!modifiersByName.TryGetValue(nameNode.StartIndex, out var accumulated))
                {
                    modifiersByName[nameNode.StartIndex] = accumulated = [];
                }

                foreach (var modifier in modifierNodes)
                {
                    accumulated[modifier.StartIndex] = modifier.Text;
                }
            }

            var record = new SymbolRecord
            {
                Name = name,
                Kind = kind,
                Language = _definition.Name,
                Span = ToSpan(definitionNode),
                IsDefinition = isDefinition,
                Signature = BuildSignature(typeNode, name, parametersNode),
                Value = valueNode?.Text.Trim(),
                TypeText = typeNode?.Text.Trim(),
                ParameterCount = CountNamed(parametersNode),
                ParameterText = TruncateText(parametersNode, MaxParameterTextLength),
            };

            var extracted = new ExtractedSymbol(record, nameNode.StartIndex, nameNode.EndIndex);

            if (!byNameOffset.TryGetValue(extracted.NameStartOffset, out var existingIndex))
            {
                byNameOffset[extracted.NameStartOffset] = results.Count;
                results.Add(extracted);
                continue;
            }

            if (Rank(record) > Rank(results[existingIndex].Record))
            {
                // Replaced in place rather than appended, so the list stays in document
                // order: container nesting is derived from it, and the UI lists members
                // in the order they are written.
                results[existingIndex] = extracted;
            }
        }

        // Stamp the accumulated modifiers onto whichever record won each position.
        for (var i = 0; i < results.Count; i++)
        {
            if (modifiersByName.TryGetValue(results[i].NameStartOffset, out var accumulated))
            {
                results[i] = results[i] with
                {
                    Record = results[i].Record with { Modifiers = string.Join(' ', accumulated.Values) },
                };
            }
        }

        return results;
    }

    /// <summary>
    /// Whether a declaration was read out of source the grammar could not make sense of.
    /// <para>
    /// This matters where a grammar mis-parses one construct as another. The bundled
    /// Verilog grammar reads a bare <c>do_thing();</c> statement as a variable declaration
    /// followed by an error, and recording that would invent a variable named after the
    /// task being called.
    /// </para>
    /// <para>
    /// What is checked is the declaration's own shape, not its whole subtree: an error
    /// buried in a body or an initializer leaves the name, the kind and the span intact, so
    /// the declaration is still a fact. The bundled C# grammar predates collection
    /// expressions and fails on every <c>= []</c>; refusing the enclosing declaration would
    /// erase a member from most modern C# files, this project's own included.
    /// </para>
    /// </summary>
    private static bool IsUntrustworthy(Node definitionNode, Node nameNode)
    {
        // The common case by far, and free.
        if (!definitionNode.HasError)
        {
            return false;
        }

        if (nameNode.HasError)
        {
            return true;
        }

        // An error sitting directly among the declaration's own parts means the parser did
        // not actually recognise this declaration, whatever it ended up calling it.
        foreach (var child in definitionNode.Children)
        {
            if (child.IsError || child.IsMissing)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How good a claim about one declaration is, in three tiers.
    /// <para>
    /// Kind specificity first. Then whether this is the real definition: a C# pack matches
    /// every method with one rule and only bodied ones with another, so the bodied match has
    /// to beat the bodiless one that landed on the same name. Then how much detail the
    /// pattern read, because packs carry a bare and an initializer-bearing variant of the
    /// same rule and the one that captured the value is the one worth keeping.
    /// </para>
    /// </summary>
    private static int Rank(SymbolRecord record)
    {
        var facts =
            (record.Value is not null ? 1 : 0) +
            (record.TypeText is not null ? 1 : 0) +
            (record.Signature is not null ? 1 : 0) +
            (record.ParameterCount is not null ? 1 : 0);

        return (CaptureNames.Specificity(record.Kind) * 16)
            + (record.IsDefinition ? 8 : 0)
            + facts;
    }

    /// <summary>
    /// Links each symbol to the innermost other symbol that encloses it, which is what
    /// turns a flat capture list into struct-to-member and class-to-method relationships.
    /// </summary>
    private static void AssignContainers(List<ExtractedSymbol> symbols)
    {
        if (symbols.Count < 2)
        {
            return;
        }

        // Sorting by start, then by descending length, puts every container immediately
        // before the symbols it contains.
        var order = Enumerable.Range(0, symbols.Count)
            .OrderBy(i => symbols[i].Record.Span.StartOffset)
            .ThenByDescending(i => symbols[i].Record.Span.Length)
            .ToArray();

        var stack = new Stack<int>();

        foreach (var index in order)
        {
            var span = symbols[index].Record.Span;

            while (stack.Count > 0 && symbols[stack.Peek()].Record.Span.EndOffset <= span.StartOffset)
            {
                stack.Pop();
            }

            if (stack.Count > 0)
            {
                var containerIndex = stack.Peek();
                symbols[index] = symbols[index] with
                {
                    Record = symbols[index].Record with { ContainerLocalIndex = containerIndex },
                };
            }

            stack.Push(index);
        }
    }

    private List<ReferenceRecord> ExtractReferences(
        Tree tree,
        List<ExtractedSymbol> symbols,
        CancellationToken cancellationToken)
    {
        if (_referenceQuery is null)
        {
            return [];
        }

        // Declaration names are not references to themselves.
        var declarationNameOffsets = symbols.Select(s => s.NameStartOffset).ToHashSet();

        // Symbols that can own a reference, widest last so the innermost wins.
        var callerCandidates = symbols
            .Select((symbol, index) => (symbol, index))
            .Where(x => _definition.CallerKinds.Contains(x.symbol.Record.Kind))
            .OrderBy(x => x.symbol.Record.Span.Length)
            .ToList();

        // Inherit references attribute to the enclosing type instead: a base list sits in
        // the class declaration, not in any method, so under CallerKinds it would have no
        // owner at all — leaving the composition inspector unable to answer "what does
        // this class inherit". Deliberately narrower than adding types to CallerKinds,
        // which would re-attribute every reference inside a class body to the class.
        var typeCandidates = symbols
            .Select((symbol, index) => (symbol, index))
            .Where(x => x.symbol.Record.Kind is SymbolKind.Class or SymbolKind.Struct
                or SymbolKind.Interface or SymbolKind.Enum or SymbolKind.Union or SymbolKind.Module)
            .OrderBy(x => x.symbol.Record.Span.Length)
            .ToList();

        // Keyed by start offset so two patterns matching one site collapse to the
        // most specific kind rather than producing duplicate edges.
        var byOffset = new Dictionary<int, (ReferenceRecord Record, int Specificity)>();

        using var cursor = _referenceQuery.Execute(tree.RootNode);

        foreach (var match in cursor.Matches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Node? referenceNode = null;
            var kind = ReferenceKind.Unknown;
            Node? nameNode = null;
            Node? argumentsNode = null;

            foreach (var capture in match.Captures)
            {
                if (CaptureNames.TryGetReferenceKind(capture.Name, out var capturedKind))
                {
                    referenceNode = capture.Node;
                    kind = capturedKind;
                    continue;
                }

                switch (capture.Name)
                {
                    case CaptureNames.Name:
                        nameNode = capture.Node;
                        break;
                    case CaptureNames.Arguments:
                        argumentsNode = capture.Node;
                        break;
                }
            }

            if (referenceNode is null)
            {
                continue;
            }

            nameNode ??= referenceNode;

            // Same rule as declarations: a reference read out of a region the grammar could
            // not parse is a guess, and guesses do not belong in the graph.
            if (IsUntrustworthy(referenceNode, nameNode))
            {
                continue;
            }
            var nameStart = nameNode.StartIndex;

            if (declarationNameOffsets.Contains(nameStart))
            {
                continue;
            }

            var name = kind == ReferenceKind.Include
                ? TrimIncludePath(nameNode.Text)
                : nameNode.Text;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var specificity = CaptureNames.Specificity(kind);
            if (byOffset.TryGetValue(nameStart, out var existing) && existing.Specificity >= specificity)
            {
                continue;
            }

            var position = nameNode.StartPosition;
            byOffset[nameStart] = (
                new ReferenceRecord
                {
                    Name = name,
                    Kind = kind,
                    ArgumentCount = CountNamed(argumentsNode),
                    ArgumentText = TruncateText(argumentsNode, MaxArgumentTextLength),
                    Position = new SourcePosition(position.Row + 1, position.Column),
                    FromSymbolLocalIndex = FindEnclosingCaller(
                        kind == ReferenceKind.Inherit ? typeCandidates : callerCandidates,
                        nameStart),
                },
                specificity);
        }

        return byOffset.Values.Select(v => v.Record).ToList();
    }

    private static int? FindEnclosingCaller(
        List<(ExtractedSymbol Symbol, int Index)> callerCandidates,
        int offset)
    {
        // Candidates are ordered narrowest first, so the first hit is the innermost.
        foreach (var (symbol, index) in callerCandidates)
        {
            if (symbol.Record.Span.Contains(offset))
            {
                return index;
            }
        }

        return null;
    }

    private static string TrimIncludePath(string raw) =>
        raw.Trim().Trim('"', '<', '>');

    private static SourceSpan ToSpan(Node node)
    {
        var start = node.StartPosition;
        var end = node.EndPosition;

        return new SourceSpan(
            new SourcePosition(start.Row + 1, start.Column),
            new SourcePosition(end.Row + 1, end.Column),
            node.StartIndex,
            node.EndIndex);
    }

    private static int? CountNamed(Node? node) =>
        node?.NamedChildren.Count;

    /// <summary>
    /// Longest argument-list slice worth storing: refs is the largest table in the index,
    /// and an edge popover does not need a screenful per call site.
    /// </summary>
    private const int MaxArgumentTextLength = 200;

    /// <summary>
    /// Longest parameter-list slice worth storing. Shorter than the argument cap because
    /// this one is drawn on a graph node, where anything near it has already stopped being
    /// readable, and it is carried for every callable in the workspace rather than only
    /// where a call was written.
    /// </summary>
    private const int MaxParameterTextLength = 120;

    /// <summary>
    /// The node's source slice, cut to <paramref name="maxLength"/>. The ellipsis marks the
    /// cut, so a truncated slice is never mistaken for a complete one.
    /// </summary>
    private static string? TruncateText(Node? node, int maxLength)
    {
        if (node is null)
        {
            return null;
        }

        var text = node.Text;

        return text.Length <= maxLength
            ? text
            : text[..maxLength] + "…";
    }

    private static string? BuildSignature(Node? typeNode, string name, Node? parametersNode)
    {
        if (parametersNode is null && typeNode is null)
        {
            return null;
        }

        var type = typeNode?.Text.Trim();
        var parameters = parametersNode?.Text.Trim();

        return (type, parameters) switch
        {
            (null, null) => null,
            (null, not null) => $"{name}{parameters}",
            (not null, null) => $"{type} {name}",
            _ => $"{type} {name}{parameters}",
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _referenceQuery?.Dispose();
        _symbolQuery.Dispose();
        _parser.Dispose();
        _language.Dispose();
    }
}
