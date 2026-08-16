using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Resolution;
using CodeAnalyzer.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Parameters as captured symbols, and the resolver behaviour that justifies them. This
/// class used to pin the opposite decision — M21.3 removed <see cref="SymbolKind.Parameter"/>
/// from the resolver's referencable set because no pack produced one, and its guard test
/// said whoever switches the capability on must put the kind back with a test. M22.3
/// switched it on for the languages whose parameters carry a declared type (C#, C, C++),
/// and these are that test.
/// </summary>
public class ParameterCaptureTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "codeanalyzer-params", Guid.NewGuid().ToString("N"));

    private SqliteIndexStore? _store;

    public ParameterCaptureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _store?.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failures are not test failures.
        }
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<SqliteConnection> IndexAsync()
    {
        _store ??= SqliteIndexStore.Open(Path.Combine(_root, ".index", "index.db"), _root);
        _store.BeginRun();

        var factory = new TreeSitterAnalyzerFactory();
        var orchestrator = new IndexOrchestrator(new FileCrawler(factory.IsSupportedExtension), factory);

        await orchestrator.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), _store);
        new ReferenceResolver(_store.Connection).ResolveAll();

        return _store.Connection;
    }

    private static List<(string Name, string? Type)> ParametersIn(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, type_text FROM symbol "
            + $"WHERE kind = {(int)SymbolKind.Parameter} ORDER BY name";

        var parameters = new List<(string, string?)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            parameters.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return parameters;
    }

    [Fact]
    public async Task ATypedParameterIsCapturedWithItsDeclaredType()
    {
        WriteFile("Device.cs", """
            class Device {
                void Send(int code, string label) { }
            }
            """);
        WriteFile("driver.c", "void probe(struct device *dev) { }");

        var connection = await IndexAsync();

        Assert.Equal(
            [("code", "int"), ("dev", "struct device"), ("label", "string")],
            ParametersIn(connection));
    }

    [Fact]
    public async Task AReceiverNamingAParameterIsTypedByItsDeclaration()
    {
        // The measured point of the milestone: obj.Member where obj is a parameter used
        // to leave the candidate set untouched; now the declared type settles it.
        WriteFile("Port.cs", """
            class Port {
                public void Open() { }
            }
            class Rig {
                public void Open() { }
                void Drive(Port port) {
                    port.Open();
                }
            }
            """);

        var connection = await IndexAsync();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT owner.name
            FROM ref r
            JOIN edge e ON e.ref_id = r.id
            JOIN symbol target ON target.id = e.target_symbol_id
            JOIN symbol owner ON owner.id = target.container_id
            WHERE r.name = 'Open' AND r.receiver_text = 'port'
            """;

        var owners = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                owners.Add(reader.GetString(0));
            }
        }

        // One edge, to Port.Open — not an ambiguous pair with Rig's own Open.
        Assert.Equal(["Port"], owners);
    }

    [Fact]
    public async Task AnUntypedLanguageStillCapturesNoParameters()
    {
        // Python and JavaScript parameters carry no declared type, so a captured row would
        // add a bare name with nothing to say and one more candidate for every use of it.
        WriteFile("script.py", "def run(speed):\n    return speed\n");
        WriteFile("app.js", "function run(speed) { return speed; }");

        var connection = await IndexAsync();

        Assert.Empty(ParametersIn(connection));
    }
}
