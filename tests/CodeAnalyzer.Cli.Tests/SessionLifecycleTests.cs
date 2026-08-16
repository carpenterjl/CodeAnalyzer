using CodeAnalyzer.Cli.Session;
using CodeAnalyzer.Core.Storage;
using CodeAnalyzer.Core.Workspaces;
using CodeAnalyzer.Parsing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeAnalyzer.Cli.Tests;

public class SessionLifecycleTests
{
    [Fact]
    public void AWorkspaceWithoutACacheSaysSoAndNamesTheFix()
    {
        var root = Path.Combine(Path.GetTempPath(), "codeanalyzer-nocache-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        try
        {
            var result = ReadOnlyIndexSession.TryOpen(root);

            Assert.Equal(IndexOpenStatus.NoCache, result.Status);
            Assert.Null(result.Session);
            Assert.Contains("codeanalyzer index", result.Problem);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ACacheFromAnotherSchemaVersionIsReportedNotRebuilt()
    {
        var root = Path.Combine(Path.GetTempPath(), "codeanalyzer-mismatch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "one.c"), "int one(void) { return 1; }");

        try
        {
            using (var session = WorkspaceSession.Open(root, new TreeSitterAnalyzerFactory()))
            {
                await session.IndexAsync([]);
            }

            // Doctor the version the way a future build would find it.
            var databasePath = WorkspacePaths.GetDatabasePath(root);
            using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE meta SET value = '9999' WHERE key = 'schema_version'";
                command.ExecuteNonQuery();
            }

            var stampBefore = File.GetLastWriteTimeUtc(databasePath);
            var result = ReadOnlyIndexSession.TryOpen(root);

            Assert.Equal(IndexOpenStatus.SchemaMismatch, result.Status);
            Assert.Null(result.Session);
            Assert.Contains("v9999", result.Problem);
            Assert.Contains("codeanalyzer index", result.Problem);

            // Reported, not repaired: a read command must never rebuild the cache.
            Assert.Equal(stampBefore, File.GetLastWriteTimeUtc(databasePath));
        }
        finally
        {
            try
            {
                Directory.Delete(WorkspacePaths.GetWorkspaceDirectory(root), recursive: true);
            }
            catch (IOException)
            {
            }

            Directory.Delete(root, recursive: true);
        }
    }
}
