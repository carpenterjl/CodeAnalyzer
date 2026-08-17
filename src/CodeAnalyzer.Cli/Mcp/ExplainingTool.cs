using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeAnalyzer.Cli.Mcp;

/// <summary>
/// Wraps a tool so that a call it cannot even bind gets an answer instead of a shrug.
/// <para>
/// The SDK deserializes arguments into the method's parameters before the method runs, and
/// when that fails it returns the string <c>An error occurred invoking '&lt;tool&gt;'.</c> and
/// nothing else. Measured over stdio against this server: a wrong parameter name, no
/// arguments at all, and a value of the wrong type all produce exactly that sentence, and
/// the caller cannot tell which of the three happened. A field report spent three round
/// trips and a schema fetch on it, guessing <c>path</c> and then <c>file</c> for what is
/// actually <c>rel_path</c>.
/// </para>
/// <para>
/// The wrapper does not parse the exception — its wording is the SDK's to change. It
/// compares what the caller sent against the schema the tool itself publishes, which is the
/// same document the caller was working from, and reports the difference. Anything it
/// cannot explain that way falls through to the exception's own message, which is still
/// more than the generic sentence carries: a type mismatch says which value would not
/// convert.
/// </para>
/// <para>
/// An unknown parameter alone is not an error — measured: <c>file_outline</c> with a valid
/// <c>rel_path</c> plus a junk <c>depth</c> answers normally, because the binder ignores
/// what it does not recognise. So the unknown names are reported as context for a real
/// failure, never as one.
/// </para>
/// </summary>
internal sealed class ExplainingTool(McpServerTool inner) : DelegatingMcpServerTool(inner)
{
    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = Explain(request, e) }],
            };
        }
    }

    private string Explain(RequestContext<CallToolRequestParams> request, Exception failure)
    {
        var name = ProtocolTool.Name;
        var sent = request.Params?.Arguments;
        var (declared, requiredKeys) = ReadSchema();

        // No schema to compare against means no better answer than the exception's own.
        if (declared.Count == 0)
        {
            return $"{name} failed: {failure.Message}";
        }

        var passed = sent is null
            ? []
            : sent.Keys.ToList();

        var unknown = passed.Where(k => !declared.ContainsKey(k)).ToList();
        var missing = requiredKeys.Where(r => !passed.Contains(r)).ToList();

        var text = new StringBuilder();
        if (missing.Count > 0)
        {
            text.Append($"{name} needs {Quote(missing)}");

            // The nearest thing the caller did send, when there is one. `path` for
            // `rel_path` is the case that prompted this, and a substring test catches it
            // without pretending to be a spell checker.
            var suggestions = missing
                .Select(m => (miss: m, sent: unknown.FirstOrDefault(u => Related(u, m))))
                .Where(pair => pair.sent is not null)
                .Select(pair => $"'{pair.sent}' is not '{pair.miss}'")
                .ToList();

            if (suggestions.Count > 0)
            {
                text.Append($" — {string.Join(", ", suggestions)}");
            }
            else if (unknown.Count > 0)
            {
                text.Append($" — you passed {Quote(unknown)}, which {(unknown.Count == 1 ? "is not a parameter" : "are not parameters")} of {name}");
            }
        }
        else
        {
            // Everything required was present, so the binder choked on a value rather than
            // on a name. The exception says which one and why; the SDK's generic sentence
            // is what threw that away.
            text.Append($"{name} could not read the arguments it was given: {failure.Message}");
            if (unknown.Count > 0)
            {
                text.Append($" (it also ignored {Quote(unknown)})");
            }
        }

        // The exception's own message ends in a full stop; the missing-parameter sentence
        // does not. Appending one unconditionally produced "BytePositionInLine: 8..".
        if (text.Length > 0 && text[^1] is not ('.' or '!' or '?'))
        {
            text.Append('.');
        }

        text.Append($" {name} takes: {string.Join("; ", declared.Select(Describe))}");
        return text.ToString();
    }

    private static bool Related(string sent, string wanted) =>
        wanted.Contains(sent, StringComparison.OrdinalIgnoreCase)
        || sent.Contains(wanted, StringComparison.OrdinalIgnoreCase);

    private static string Describe(KeyValuePair<string, string> parameter) =>
        parameter.Value.Length == 0
            ? parameter.Key
            : $"{parameter.Key} ({Trim(parameter.Value)})";

    /// <summary>
    /// One clause of the parameter's own description. The full text is a paragraph on some
    /// tools, and an error message that runs longer than the answer would have is its own
    /// kind of unhelpful.
    /// <para>
    /// The separator has to be followed by a space or end the string. Cutting at every '.'
    /// truncated file_outline's description inside its own example — "any unambiguous
    /// suffix of it (uart" — because the dot in <c>uart.c</c> is a dot like any other.
    /// </para>
    /// </summary>
    private static string Trim(string description)
    {
        var clause = description;
        for (var i = 0; i < description.Length; i++)
        {
            if (description[i] is not ('.' or ';'))
            {
                continue;
            }

            if (i + 1 >= description.Length || char.IsWhiteSpace(description[i + 1]))
            {
                clause = description[..i];
                break;
            }
        }

        return clause.Length > 80 ? clause[..80].TrimEnd() + "…" : clause;
    }

    private static string Quote(IReadOnlyList<string> names) =>
        names.Count switch
        {
            1 => $"'{names[0]}'",
            _ => string.Join(" and ", new[]
            {
                string.Join(", ", names.Take(names.Count - 1).Select(n => $"'{n}'")),
                $"'{names[^1]}'",
            }),
        };

    /// <summary>
    /// The tool's published parameter names with their descriptions, and which are required
    /// — read from the schema the caller was given, so the two cannot disagree.
    /// </summary>
    // NB: the local below is deliberately not called `required`. C# 11 made that word a
    // member modifier, and the vendored grammar reads it as one wherever it appears, so a
    // local of that name is unparseable as a receiver — measured this session on seven
    // contextual keywords, and `required` is the only one that breaks. The tool found this
    // in its own new code, which is the best argument for it there is.
    private (Dictionary<string, string> Declared, List<string> Required) ReadSchema()
    {
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        var requiredKeys = new List<string>();

        var schema = ProtocolTool.InputSchema;
        if (schema.ValueKind is not JsonValueKind.Object)
        {
            return (declared, requiredKeys);
        }

        if (schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind is JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                declared[property.Name] =
                    property.Value.ValueKind is JsonValueKind.Object
                    && property.Value.TryGetProperty("description", out var description)
                    && description.ValueKind is JsonValueKind.String
                        ? description.GetString() ?? string.Empty
                        : string.Empty;
            }
        }

        if (schema.TryGetProperty("required", out var requiredNames)
            && requiredNames.ValueKind is JsonValueKind.Array)
        {
            requiredKeys.AddRange(requiredNames.EnumerateArray()
                .Where(element => element.ValueKind is JsonValueKind.String)
                .Select(element => element.GetString()!));
        }

        return (declared, requiredKeys);
    }
}
