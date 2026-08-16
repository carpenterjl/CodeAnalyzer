namespace CodeAnalyzer.Cli.Commands;

/// <summary>
/// The flag scanner behind every subcommand. Positionals in order, <c>--name value</c>
/// options, bare <c>--name</c> switches.
/// <para>
/// Each command declares which option names it accepts, and anything undeclared is an
/// error rather than a silently ignored typo — <c>--dept 3</c> quietly meaning
/// "unbounded" is exactly the kind of lie this tool exists to avoid.
/// </para>
/// </summary>
internal sealed class ArgReader
{
    /// <summary>
    /// Switches every command accepts, whatever else it declares. <c>--quiet</c> is here
    /// rather than in ten separate lists for the reason the rest of this repo keeps one
    /// vocabulary in one place: an option restated at every call site is an option that
    /// will eventually be missing from the eleventh. Every command honours it — it means
    /// "print nothing that is not the answer", and each command knows what that excludes.
    /// </summary>
    public static readonly string[] GlobalSwitches = ["quiet"];

    private readonly List<string> _positionals = [];
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _switches = new(StringComparer.OrdinalIgnoreCase);

    private ArgReader()
    {
    }

    /// <summary>Parse failure text, null when the arguments were well-formed.</summary>
    public string? Error { get; private set; }

    public IReadOnlyList<string> Positionals => _positionals;

    public static ArgReader Parse(
        IReadOnlyList<string> args,
        IReadOnlyCollection<string> valueOptions,
        IReadOnlyCollection<string> switchOptions)
    {
        var reader = new ArgReader();

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                reader._positionals.Add(token);
                continue;
            }

            var name = token[2..];

            if (valueOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                {
                    reader.Error = $"--{name} needs a value";
                    return reader;
                }

                reader._values[name] = args[++i];
            }
            else if (switchOptions.Contains(name, StringComparer.OrdinalIgnoreCase)
                     || GlobalSwitches.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                reader._switches.Add(name);
            }
            else
            {
                reader.Error = $"unknown option --{name}";
                return reader;
            }
        }

        return reader;
    }

    public string? Value(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public bool Switch(string name) => _switches.Contains(name);

    /// <summary>An integer option, or the default when absent. Sets <see cref="Error"/> on garbage.</summary>
    public int IntValue(string name, int fallback)
    {
        var text = Value(name);
        if (text is null)
        {
            return fallback;
        }

        if (int.TryParse(text, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        Error ??= $"--{name} expects a positive integer, got '{text}'";
        return fallback;
    }
}
