using System.Reflection;
using CodeAnalyzer.Core.Storage;

namespace CodeAnalyzer.Cli.Commands;

/// <summary>
/// Which build of the tool this is, and which index schema it writes.
/// <para>
/// Written because three field reports in a row carried the line
/// <c>Tool build | schema v26</c> while the tool was on 27, then 29, then 30. Nothing was
/// careless about that: there was no command that would say, <c>cache</c> does not print
/// it, and <c>--version</c> answered <c>unknown command</c>, so each report copied the
/// previous sitting's header. A number a reader cannot obtain is a number they will
/// inherit.
/// </para>
/// <para>
/// The schema version is the useful half. The assembly version is 1.0.0 on every build and
/// says nothing, so what identifies a build here is the schema it writes plus the
/// timestamp on the binary — which is also exactly what distinguishes a stale field copy
/// from a fresh one, the failure that produced those three headers.
/// </para>
/// </summary>
internal static class VersionCommand
{
    public static CommandSpec Spec { get; } = new(
        "version",
        "version [--json]",
        "which build of the tool this is, and which index schema it writes",
        (args, ct) => Run(args, ct));

    /// <summary>
    /// One line, in the form a field report's <c>Tool build</c> row wants — so the row can
    /// be pasted rather than recalled.
    /// </summary>
    public static string Line()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var built = BuiltUtc(assembly);

        return $"codeanalyzer {Version(assembly)} · writes index schema v{Schema.Version}"
            + (built is { } utc ? $" · binary built {utc:yyyy-MM-dd HH:mm}Z" : string.Empty);
    }

    private static string Version(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    /// The binary's own write time. Deterministic builds zero the PE header timestamp, so
    /// this reads the file rather than the header — the question being asked is "is the
    /// copy I am running the one that was published", and that is a filesystem fact.
    /// </summary>
    private static DateTime? BuiltUtc(Assembly assembly)
    {
        try
        {
            var path = assembly.Location;
            return string.IsNullOrEmpty(path) ? null : File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Task<int> Run(string[] rawArgs, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var args = ArgReader.Parse(rawArgs, [], ["json"]);

        if (args.Error is not null)
        {
            Console.Error.WriteLine(args.Error);
            return Task.FromResult(ExitCodes.Error);
        }

        var assembly = Assembly.GetExecutingAssembly();

        Console.WriteLine(args.Switch("json")
            ? System.Text.Json.JsonSerializer.Serialize(new
            {
                version = Version(assembly),
                schemaVersion = Schema.Version,
                binaryBuiltUtc = BuiltUtc(assembly),
                binaryPath = assembly.Location,
            })
            : Line() + Environment.NewLine + assembly.Location);

        return Task.FromResult(ExitCodes.Ok);
    }
}
