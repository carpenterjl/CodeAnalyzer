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

    /// <summary>
    /// Default bound on tree-sitter's concurrently in-progress query matches, which is
    /// what grows without limit on pathologically nested machine-generated files. Far
    /// above anything hand-written source produces; hitting it is reported on the file,
    /// never silent.
    /// </summary>
    public const int DefaultMaxMatchesInProgress = 100_000;

    /// <summary>
    /// Overridable so a test can trip the cap without a hundred-thousand-node fixture.
    /// The factory never sets it.
    /// </summary>
    public int MaxMatchesInProgress { get; init; } = DefaultMaxMatchesInProgress;

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

            var symbols = ExtractSymbols(tree, cancellationToken, out var symbolMatchesDropped);
            AssignContainers(symbols);

            var references = ExtractReferences(tree, symbols, cancellationToken, out var referenceMatchesDropped);

            // The cap only trips on deeply nested pattern state — the shape of a
            // pathological machine-generated file — never on sequential declarations
            // (pinned by MatchLimit_BoundsInProgressMatchesAndReportsExceeding). When it
            // does, everything extracted is kept and the incompleteness is stated.
            var matchesDropped = symbolMatchesDropped || referenceMatchesDropped;
            (Node Site, string? Text)? firstError =
                tree.RootNode.HasError ? FirstError(tree.RootNode) : null;

            return new ParseResult
            {
                RelativePath = relativePath,
                Language = _definition.Name,
                ContentHash = [],
                Symbols = symbols.Select(s => s.Record).ToList(),
                References = references,
                // tree-sitter recovers from syntax errors, so a partial tree still yields
                // usable symbols. The status records that the file was imperfect.
                Status = tree.RootNode.HasError || matchesDropped ? FileStatus.ParseError : FileStatus.Ok,
                ErrorMessage = matchesDropped
                    ? $"tree-sitter dropped query matches (limit {MaxMatchesInProgress:N0}) — the symbol and reference lists for this file may be incomplete"
                    : null,
                ErrorLine = firstError is { } error ? error.Site.StartPosition.Row + 1 : null,
                ErrorText = firstError?.Text,
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

    /// <summary>
    /// Longest error snippet kept. A snippet is meant to be recognised at a glance, and an
    /// error node can span the rest of a file.
    /// </summary>
    private const int MaxErrorSnippet = 48;

    /// <summary>
    /// Where the grammar first lost its footing: the position of the innermost node still
    /// reporting an error, and the nearest text around it worth quoting.
    /// <para>
    /// A count of imperfect files is not actionable on its own — it says a share of the
    /// workspace is wrong without saying what, which invites the reader to assume the worst
    /// about their own source. Usually the truth is narrower and duller: on this project
    /// every one of the 53 flagged C# files trips on the same construct the bundled grammar
    /// predates, and only a position makes that visible.
    /// </para>
    /// <para>
    /// <c>HasError</c> is the only signal used, because it is the only one that holds. A
    /// token the grammar expected and did not find arrives as an ordinary childless node —
    /// an empty <c>identifier</c>, for a collection expression — whose <c>IsMissing</c> and
    /// <c>IsError</c> are both false and whose <c>HasError</c> is true. Descending on those
    /// two flags walks off the end of the tree and reports nothing, which is what the first
    /// cut of this did. <c>MissingToken_SurfacesOnlyThroughHasError</c> pins it.
    /// </para>
    /// <para>
    /// The site itself is usually empty, so the quotable text is carried down the descent:
    /// the last ancestor with any extent is the innermost construct that contains the
    /// failure, and it is what a reader recognises. For <c>= []</c> the site is a
    /// zero-width node inside the brackets and the quote is <c>[]</c>.
    /// </para>
    /// </summary>
    private static (Node Site, string? Text) FirstError(Node root)
    {
        var node = root;
        var quotable = root;

        while (true)
        {
            if (!string.IsNullOrWhiteSpace(node.Text))
            {
                quotable = node;
            }

            Node? next = null;
            foreach (var child in node.Children)
            {
                if (child.HasError)
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                return (node, Snippet(quotable));
            }

            node = next;
        }
    }

    /// <summary>
    /// The first line of what a node covers, trimmed and capped. Null when there is nothing
    /// to quote, which leaves the position as the whole fact.
    /// </summary>
    private static string? Snippet(Node node)
    {
        var text = node.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var span = text.AsSpan();
        var newline = span.IndexOfAny('\r', '\n');
        if (newline >= 0)
        {
            span = span[..newline];
        }

        var trimmed = span.Trim();
        return trimmed.Length <= MaxErrorSnippet
            ? trimmed.ToString()
            : string.Concat(trimmed[..MaxErrorSnippet], "…");
    }

    /// <summary>A symbol plus the span of its name, which reference filtering needs.</summary>
    private sealed record ExtractedSymbol(SymbolRecord Record, int NameStartOffset, int NameEndOffset);

    private List<ExtractedSymbol> ExtractSymbols(Tree tree, CancellationToken cancellationToken, out bool matchLimitExceeded)
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

        using var cursor = _symbolQuery.Execute(tree.RootNode, new QueryOptions { MatchLimit = (uint)MaxMatchesInProgress });

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

        matchLimitExceeded = cursor.IsMatchLimitExceeded;

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
        CancellationToken cancellationToken,
        out bool matchLimitExceeded)
    {
        matchLimitExceeded = false;

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

        // A reference written in a member's initialiser has no callable around it, so under
        // CallerKinds alone it has no owner at all. The JGraph field report found the
        // consequence the hard way: `private static readonly OptionSpec Sort = new(...)` is
        // the dominant idiom in that codebase, every one of those references is recorded,
        // and `get_callers OptionSpec` could not list a single one of them. Measured on this
        // repo: 3,129 such references, 2,250 of them C# and 873 JavaScript.
        //
        // This is a fallback rather than an addition to CallerKinds, and the difference is
        // the difference between gaining owners and moving them. Adding Variable to
        // CallerKinds gains 962 and re-attributes 12,827 — every `var x = Foo();` inside a
        // method would answer "who calls Foo" with the local `x` instead of the method that
        // runs it. Because this list is consulted only when no callable encloses the
        // reference, a method keeps everything inside its body by construction.
        var memberCandidates = symbols
            .Select((symbol, index) => (symbol, index))
            .Where(x => x.symbol.Record.Kind is SymbolKind.Field or SymbolKind.Property
                or SymbolKind.Constant or SymbolKind.Variable)
            .OrderBy(x => x.symbol.Record.Span.Length)
            .ToList();

        // Keyed by start offset so two patterns matching one site collapse to the
        // most specific kind rather than producing duplicate edges.
        var byOffset = new Dictionary<int, (ReferenceRecord Record, int Specificity)>();

        // Binding-context spans (M25.2), collected alongside the references they will
        // re-scope. Keyed by start offset because a typed and an untyped context pattern
        // both match a DataTemplate that declares its DataType, and the typed claim wins.
        var contextByOffset = new Dictionary<int, (int End, string? TypeName)>();

        // Bindings that declare where they read from, by the offset of the name they
        // refer to. The enclosing context is not their receiver — see the stamping pass.
        var selfSourced = new HashSet<int>();

        using var cursor = _referenceQuery.Execute(tree.RootNode, new QueryOptions { MatchLimit = (uint)MaxMatchesInProgress });

        foreach (var match in cursor.Matches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Node? referenceNode = null;
            var kind = ReferenceKind.Unknown;
            Node? nameNode = null;
            Node? argumentsNode = null;
            Node? receiverNode = null;
            Node? contextNode = null;
            Node? contextTypeNode = null;

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
                    case CaptureNames.Context:
                        contextNode = capture.Node;
                        break;
                    case CaptureNames.ContextType:
                        contextTypeNode = capture.Node;
                        break;
                    case CaptureNames.Arguments:
                        argumentsNode = capture.Node;
                        break;
                    case CaptureNames.Receiver:
                        receiverNode = capture.Node;
                        break;
                }
            }

            // A context match carries a span and possibly a declared type, never a
            // reference. A typeless entry still lands in the table: an undeclared
            // template blocks the context outside it rather than passing it through.
            if (contextNode is not null)
            {
                var typeName = contextTypeNode is null
                    ? null
                    : MarkupExtensionPath.ContextType(contextTypeNode.Text);
                var start = contextNode.StartIndex;

                if (!contextByOffset.TryGetValue(start, out var known) || known.TypeName is null)
                {
                    contextByOffset[start] = (contextNode.EndIndex, typeName ?? known.TypeName);
                }

                continue;
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
            var name = kind == ReferenceKind.Include
                ? TrimIncludePath(nameNode.Text)
                : nameNode.Text;
            var startPosition = nameNode.StartPosition;
            var position = new SourcePosition(startPosition.Row + 1, startPosition.Column);
            var receiverText = TruncateText(receiverNode, MaxReceiverTextLength);
            var argumentText = TruncateText(argumentsNode, MaxArgumentTextLength);

            // A dotted type reference is a qualified one — XAML's
            // x:Class="CodeAnalyzer.App.Views.MainWindow" — and resolution matches names
            // whole, so left intact it matches nothing. The last segment is what a
            // definition can carry as its name; the qualifier goes in the receiver slot,
            // where it means what a receiver means everywhere else: the locating was done
            // in the source, not by the file the reference sits in. The offset moves to
            // the segment too, which is both the truer position and what keeps this
            // reference from being dropped as a declaration naming itself when the same
            // attribute also declares the markup root below.
            if (kind == ReferenceKind.TypeUse)
            {
                var lastDot = name.LastIndexOf('.');
                if (lastDot > 0 && lastDot < name.Length - 1)
                {
                    receiverText = TruncateForStorage(name[..lastDot], MaxReceiverTextLength);
                    position = Advance(position, name.AsSpan(0, lastDot + 1));
                    name = name[(lastDot + 1)..];
                    nameStart += lastDot + 1;
                }
            }

            // A binding reference arrives as the whole markup extension — the grammar
            // hands "{Binding SearchQuery, Mode=TwoWay}" over as one token — and the
            // pack can only say what it is, not read inside it. The parser pulls out the
            // one name the extension refers to, and which world it resolves into: a
            // resource key becomes a Resource reference, a path stays Binding. A value
            // the parser cannot read with certainty stays what it always was, a verbatim
            // attribute making no claim. The whole extension rides along as the argument
            // text, so a call-site listing shows the claim's evidence verbatim.
            if (kind == ReferenceKind.Binding)
            {
                if (MarkupExtensionPath.Extract(name) is not { } extension)
                {
                    continue;
                }

                argumentText = TruncateForStorage(name, MaxArgumentTextLength);
                kind = extension.IsResource ? ReferenceKind.Resource : ReferenceKind.Binding;
                position = Advance(position, name.AsSpan(0, extension.Offset));
                name = extension.Name;
                nameStart += extension.Offset;

                if (extension.NamesItsOwnSource)
                {
                    selfSourced.Add(nameStart);
                }
            }

            if (declarationNameOffsets.Contains(nameStart))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var specificity = CaptureNames.Specificity(kind);
            if (byOffset.TryGetValue(nameStart, out var existing) && existing.Specificity >= specificity)
            {
                continue;
            }

            byOffset[nameStart] = (
                new ReferenceRecord
                {
                    Name = name,
                    Kind = kind,
                    ArgumentCount = CountNamed(argumentsNode),
                    ArgumentText = argumentText,
                    ReceiverText = receiverText,
                    Position = position,
                    // A member only ever claims what no callable would: its span cannot
                    // contain a method body, and a base list cannot sit inside a field.
                    FromSymbolLocalIndex = FindEnclosingCaller(
                            kind == ReferenceKind.Inherit ? typeCandidates : callerCandidates,
                            nameStart)
                        ?? FindEnclosingCaller(memberCandidates, nameStart),
                },
                specificity);
        }

        matchLimitExceeded = cursor.IsMatchLimitExceeded;

        // Each binding resolves against its innermost enclosing context (M25.2): the
        // template's declared DataType, or the design-time DataContext for anything
        // outside every template. The type name goes in the receiver slot verbatim,
        // where the resolver's "the receiver is a type name" rank already knows what to
        // do with it. Innermost-wins is what makes an undeclared template a wall: it is
        // the closest context, and it has no type to give.
        if (contextByOffset.Count > 0)
        {
            foreach (var (offset, entry) in byOffset)
            {
                if (entry.Record.Kind != ReferenceKind.Binding)
                {
                    continue;
                }

                // A binding that names its own source never reads the ambient context, so
                // the enclosing type is not its receiver. Stamping one anyway is the same
                // invented claim an undeclared template is a wall to avoid, one level down:
                // `{Binding DataContext.FocusSymbolCommand, RelativeSource={RelativeSource
                // AncestorType=Window}}` sits inside a DataTemplate typed RelatedSymbolItem
                // and deliberately reaches past it to the Window. Seventeen references in
                // this corpus were stamped that way, and none of them produced a false edge
                // — only because no viewmodel here happens to define `PlacementTarget` or
                // `DataContext`. That is luck, and the guard is what makes it a rule.
                if (selfSourced.Contains(offset))
                {
                    continue;
                }

                (int Start, int End, string? TypeName)? innermost = null;
                foreach (var (start, (end, typeName)) in contextByOffset)
                {
                    if (start <= offset && offset < end
                        && (innermost is null || start > innermost.Value.Start))
                    {
                        innermost = (start, end, typeName);
                    }
                }

                if (innermost is { TypeName: { } contextType })
                {
                    byOffset[offset] = entry with
                    {
                        Record = entry.Record with { ReceiverText = contextType },
                    };
                }
            }
        }

        return byOffset.Values.Select(v => v.Record).ToList();
    }

    /// <summary>
    /// Where a position lands after walking over <paramref name="prefix"/> — the line and
    /// column arithmetic behind reporting a reference at its segment rather than at the
    /// start of the token it was cut from.
    /// </summary>
    private static SourcePosition Advance(SourcePosition start, ReadOnlySpan<char> prefix)
    {
        var line = start.Line;
        var column = start.Column;

        foreach (var ch in prefix)
        {
            if (ch == '\n')
            {
                line++;
                column = 0;
            }
            else
            {
                column++;
            }
        }

        return new SourcePosition(line, column);
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
    /// Longest receiver slice worth storing. Shorter than the argument cap because what the
    /// resolver reads is presence and the self-receiver keywords, and what a caller list
    /// displays is a prefix — a chained receiver's tail says nothing either consumer needs.
    /// </summary>
    private const int MaxReceiverTextLength = 100;

    /// <summary>
    /// The node's source slice, cut to <paramref name="maxLength"/>. The ellipsis marks the
    /// cut, so a truncated slice is never mistaken for a complete one.
    /// </summary>
    private static string? TruncateText(Node? node, int maxLength) =>
        node is null ? null : TruncateForStorage(node.Text, maxLength);

    private static string TruncateForStorage(string text, int maxLength) =>
        text.Length <= maxLength
            ? text
            : text[..maxLength] + "…";

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
