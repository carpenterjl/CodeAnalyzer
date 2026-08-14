using CodeAnalyzer.Core.Resolution;
using CodeAnalyzer.Core.Storage;
using CodeAnalyzer.Core.Watching;
using CodeAnalyzer.Core.Workspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Applies change batches straight to a session, skipping the watcher.
/// <para>
/// The watcher's job is to describe what changed and it is tested on its own; these tests are
/// about what the index does with that description. Feeding batches directly also makes the
/// awkward cases — a file deleted while a caller in an untouched file still points at it —
/// something a test can simply state, rather than something it has to provoke.
/// </para>
/// </summary>
public class IncrementalUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "codeanalyzer-live", Guid.NewGuid().ToString("N"));

    private WorkspaceSession? _session;

    public IncrementalUpdateTests()
    {
        Directory.CreateDirectory(_root);

        Write("drivers/uart.h", """
            #define UART_BAUD 115200
            int uart_init(void);
            int uart_write(int byte);
            """);

        Write("drivers/uart.c", """
            #include "uart.h"

            static int uart_configure(int baud) {
                return baud == UART_BAUD;
            }

            int uart_init(void) {
                return uart_configure(UART_BAUD);
            }

            int uart_write(int byte) {
                return byte;
            }
            """);

        Write("app/main.c", """
            #include "../drivers/uart.h"

            int app_entry(void) {
                uart_init();
                return uart_write(7);
            }
            """);
    }

    public void Dispose()
    {
        _session?.Dispose();
        WorkspaceCacheCleanup.Delete(_root);

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failures are not test failures.
        }
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void Delete(string relativePath) =>
        File.Delete(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private async Task<WorkspaceSession> IndexedSessionAsync()
    {
        _session = WorkspaceSession.Open(_root, new TreeSitterAnalyzerFactory());
        await _session.IndexAsync([string.Empty]);
        return _session;
    }

    private static WorkspaceChangeBatch Changed(params string[] paths) =>
        new() { ChangedFiles = paths };

    private static WorkspaceChangeBatch Removed(params string[] paths) =>
        new() { RemovedPaths = paths };

    // ---- What a live update sees -------------------------------------------

    [Fact]
    public async Task AnEditedFileGainsAndLosesSymbols()
    {
        var session = await IndexedSessionAsync();

        Assert.NotEmpty(session.Search.Search("uart_configure"));

        Write("drivers/uart.c", """
            #include "uart.h"

            int uart_init(void) {
                return 0;
            }

            int uart_reset(void) {
                return 1;
            }
            """);

        var result = await session.ApplyChangesAsync(Changed("drivers/uart.c"));

        Assert.Equal(1, result.FilesParsed);
        Assert.Single(session.Search.Search("uart_reset"), h => h.Name == "uart_reset");
        Assert.DoesNotContain(session.Search.Search("uart_configure"), h => h.Name == "uart_configure");
    }

    [Fact]
    public async Task ABodyOnlyEditTakesTheIncrementalPath()
    {
        var session = await IndexedSessionAsync();

        Write("drivers/uart.c", """
            #include "uart.h"

            static int uart_configure(int baud) {
                return baud == UART_BAUD;
            }

            int uart_init(void) {
                return uart_configure(UART_BAUD) + 1;
            }

            int uart_write(int byte) {
                return byte + 1;
            }
            """);

        var result = await session.ApplyChangesAsync(Changed("drivers/uart.c"));

        // The include graph did not move, so nothing outside the dirty set could have shifted.
        Assert.False(result.FullResolve);
    }

    [Fact]
    public async Task AddingAnIncludeFallsBackToAFullResolve()
    {
        var session = await IndexedSessionAsync();

        // Reach is transitive: a new include can promote a reference two hops away that this
        // batch never mentions, so the fast path is not sound here and must not be taken.
        Write("app/main.c", """
            #include "../drivers/uart.h"
            #include "../drivers/uart.c"

            int app_entry(void) {
                uart_init();
                return uart_write(7);
            }
            """);

        var result = await session.ApplyChangesAsync(Changed("app/main.c"));

        Assert.True(result.FullResolve);
    }

    [Fact]
    public async Task DeletingAFileRemovesItsSymbolsAndStrandsItsCallers()
    {
        var session = await IndexedSessionAsync();

        var before = session.Graph.GetDetail(FindId(session, "app_entry"));
        Assert.Contains(before!.Callees, c => c.Name == "uart_init");

        Delete("drivers/uart.c");
        Delete("drivers/uart.h");

        var result = await session.ApplyChangesAsync(Removed("drivers/uart.c", "drivers/uart.h"));

        Assert.Equal(2, result.FilesRemoved);
        Assert.Empty(session.Search.Search("uart_configure"));

        // The caller lives in a file nobody touched. Its call has to stop claiming a target
        // that no longer exists, and reappear as an unresolved name instead.
        var after = session.Graph.GetDetail(FindId(session, "app_entry"));
        Assert.DoesNotContain(after!.Callees, c => c.Name == "uart_init");
        Assert.Contains(after.UnresolvedReferences, r => r.Name == "uart_init");
    }

    [Fact]
    public async Task ADeletedDirectoryTakesTheFilesUnderItWithIt()
    {
        var session = await IndexedSessionAsync();

        Directory.Delete(Path.Combine(_root, "drivers"), recursive: true);

        // The watcher reports the directory only; the files under it are never mentioned.
        var result = await session.ApplyChangesAsync(Removed("drivers"));

        Assert.Equal(2, result.FilesRemoved);
        Assert.Empty(session.Search.Search("uart_write"));
    }

    [Fact]
    public async Task ANewFileResolvesAnExistingUnresolvedCall()
    {
        Write("app/main.c", """
            int app_entry(void) {
                return watchdog_kick();
            }
            """);

        var session = await IndexedSessionAsync();

        var before = session.Graph.GetDetail(FindId(session, "app_entry"));
        Assert.Contains(before!.UnresolvedReferences, r => r.Name == "watchdog_kick");

        Write("drivers/watchdog.c", "int watchdog_kick(void) { return 0; }");

        await session.ApplyChangesAsync(Changed("drivers/watchdog.c"));

        // app/main.c was not in the batch, but the name it was calling just came into
        // existence, so its edge has to appear.
        var after = session.Graph.GetDetail(FindId(session, "app_entry"));
        Assert.Contains(after!.Callees, c => c.Name == "watchdog_kick");
    }

    [Fact]
    public async Task ARenameMovesTheSymbolsToTheNewPath()
    {
        var session = await IndexedSessionAsync();

        File.Move(
            Path.Combine(_root, "drivers", "uart.c"),
            Path.Combine(_root, "drivers", "serial.c"));

        var result = await session.ApplyChangesAsync(new WorkspaceChangeBatch
        {
            ChangedFiles = ["drivers/serial.c"],
            RemovedPaths = ["drivers/uart.c"],
        });

        Assert.Equal(1, result.FilesParsed);
        Assert.Equal(1, result.FilesRemoved);

        var hit = Assert.Single(session.Search.Search("uart_configure"), h => h.Name == "uart_configure");
        Assert.Equal("drivers/serial.c", hit.RelativePath);
    }

    [Fact]
    public async Task AnUnchangedFileInTheBatchCostsNothing()
    {
        var session = await IndexedSessionAsync();

        // Editors touch files without changing them, and the size-and-stamp gate is what
        // keeps that from turning into work.
        var result = await session.ApplyChangesAsync(Changed("drivers/uart.c"));

        Assert.Equal(0, result.FilesParsed);
        Assert.False(result.ChangedAnything);
    }

    [Fact]
    public async Task ALocalNameOfTheWrongKindDoesNotShieldACallFromReResolution()
    {
        // The incremental pass skips references that already resolve inside their own file,
        // and "inside their own file" has to mean a definition a call could actually bind to.
        // Here main.c has a *variable* named reset and calls reset() from elsewhere; treating
        // the variable as a local match would leave this call pointing at a deleted function.
        Write("app/main.c", """
            int reset = 0;

            int app_entry(void) {
                return reset();
            }
            """);

        Write("drivers/reset.c", "int reset(void) { return 0; }");

        var session = await IndexedSessionAsync();

        var before = session.Graph.GetDetail(FindId(session, "app_entry"));
        Assert.Contains(before!.Callees, c => c.Name == "reset" && c.RelativePath == "drivers/reset.c");

        Write("drivers/reset.c", "int reboot(void) { return 0; }");
        Assert.False((await session.ApplyChangesAsync(Changed("drivers/reset.c"))).FullResolve);

        var after = session.Graph.GetDetail(FindId(session, "app_entry"));
        Assert.DoesNotContain(after!.Callees, c => c.RelativePath == "drivers/reset.c");
        Assert.Contains(after.UnresolvedReferences, r => r.Name == "reset");
    }

    // ---- The fast path must agree with the slow one ------------------------

    [Fact]
    public async Task IncrementalResolutionMatchesAFullResolve()
    {
        var session = await IndexedSessionAsync();

        // Structural, so this one is expected to fall back. It exists to set up the file the
        // later steps move around.
        Write("app/log.c", """
            int log_write(int level) {
                return level;
            }
            """);
        Assert.True((await session.ApplyChangesAsync(Changed("app/log.c"))).FullResolve);

        // From here on, every step must take the fast path. Asserting that is what stops this
        // test going quietly vacuous: if the fallback ever started firing for everything, the
        // comparison below would be a full resolve against a full resolve and prove nothing.
        Write("app/main.c", """
            #include "../drivers/uart.h"

            int app_entry(void) {
                uart_init();
                log_write(1);
                return uart_write(7);
            }
            """);
        Assert.False((await session.ApplyChangesAsync(Changed("app/main.c"))).FullResolve);

        // A definition renamed out from under a caller in a file this batch never mentions.
        Write("app/log.c", """
            int log_emit(int level) {
                return level;
            }
            """);
        Assert.False((await session.ApplyChangesAsync(Changed("app/log.c"))).FullResolve);

        // A definition removed, leaving only the header's declaration behind.
        Write("drivers/uart.c", """
            #include "uart.h"

            static int uart_configure(int baud) {
                return baud == UART_BAUD;
            }

            int uart_init(void) {
                return uart_configure(UART_BAUD);
            }
            """);
        Assert.False((await session.ApplyChangesAsync(Changed("drivers/uart.c"))).FullResolve);

        // A second definition of a name that is already resolved elsewhere, which is what
        // moves a reference between tiers rather than merely on or off.
        Write("app/log.c", """
            int log_emit(int level) {
                return level;
            }

            int log_flush(void) {
                return log_emit(0);
            }
            """);
        Assert.False((await session.ApplyChangesAsync(Changed("app/log.c"))).FullResolve);

        var live = ReadEdges();

        // Same database, resolved from scratch. Any disagreement is the incremental path
        // having missed something, which is the whole risk this milestone introduces.
        var rebuilt = RebuildEdges();

        Assert.Equal(rebuilt, live);
    }

    [Fact]
    public async Task RepeatedUpdatesToOneFileStayConsistent()
    {
        var session = await IndexedSessionAsync();

        for (var i = 0; i < 12; i++)
        {
            Write("drivers/uart.c", $$"""
                #include "uart.h"

                static int uart_configure(int baud) {
                    return baud == UART_BAUD + {{i}};
                }

                int uart_init(void) {
                    return uart_configure(UART_BAUD);
                }

                int uart_write(int byte) {
                    return byte + {{i}};
                }
                """);

            await session.ApplyChangesAsync(Changed("drivers/uart.c"));
        }

        Assert.Equal(RebuildEdges(), ReadEdges());
    }

    // ---- Helpers -----------------------------------------------------------

    private static long FindId(WorkspaceSession session, string name) =>
        Assert.Single(session.Search.Search(name), h => h.Name == name).SymbolId;

    /// <summary>
    /// Edges as (from file:line, target file:line, confidence). Ids are useless for
    /// comparison — re-parsing reassigns them — so the identity of an edge has to be
    /// expressed in terms that survive.
    /// </summary>
    private List<string> ReadEdges()
    {
        using var connection = OpenSecondConnection();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rf.rel_path || ':' || r.line || ':' || r.name
                   || ' -> ' || tf.rel_path || ':' || t.start_line || ':' || t.name
                   || ' (' || e.confidence || ')'
            FROM edge e
            JOIN ref r ON r.id = e.ref_id
            JOIN file rf ON rf.id = r.file_id
            JOIN symbol t ON t.id = e.target_symbol_id
            JOIN file tf ON tf.id = t.file_id
            """;

        var rows = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private List<string> RebuildEdges()
    {
        using (var connection = OpenSecondConnection())
        {
            new ReferenceResolver(connection).ResolveAll();
        }

        return ReadEdges();
    }

    /// <summary>
    /// A second connection to the same index file, the way an external tool would read it.
    /// <para>
    /// The session keeps its connection to itself, which is right — everything that touches
    /// it has to go through one lock. Opening another is honest here because the test is
    /// standing outside the session, and it is what <c>tools/CodeAnalyzer.Bench</c> already
    /// does. Reaching in for the private field instead would tie the test to a field name.
    /// </para>
    /// </summary>
    private SqliteConnection OpenSecondConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = WorkspacePaths.GetDatabasePath(_root),
            Mode = SqliteOpenMode.ReadWrite,
            // Without this, the pool keeps the file locked after Dispose and the fixture
            // cannot delete the cache directory it is about to clean up.
            Pooling = false,
        }.ToString());

        connection.Open();
        Schema.ExecuteScript(connection, Schema.ConnectionPragmas);
        return connection;
    }
}
