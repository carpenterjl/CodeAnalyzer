using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Cli.Session;

namespace CodeAnalyzer.Cli.Mcp;

/// <summary>
/// The MCP server's one session, opened lazily and reopened after a reindex.
/// <para>
/// Lazily, because the server must start and answer <c>tools/list</c> even when no cache
/// exists yet — every tool then answers with the way out (<c>reindex</c>) instead of the
/// process refusing to boot. Tool calls arrive concurrently, hence the lock.
/// </para>
/// </summary>
internal sealed class McpSessionHolder(string rootPath) : IDisposable
{
    private readonly object _sync = new();
    private ReadOnlyIndexSession? _session;
    private AgentToolset? _toolset;

    public string RootPath { get; } = rootPath;

    /// <summary>The toolset, or the sentence explaining why there is none yet.</summary>
    public (AgentToolset? Toolset, string? Problem) Get()
    {
        lock (_sync)
        {
            if (_toolset is not null)
            {
                return (_toolset, null);
            }

            var opened = ReadOnlyIndexSession.TryOpen(RootPath);
            if (opened.Session is null)
            {
                var hint = opened.Status == IndexOpenStatus.NoCache
                    ? " — call the reindex tool to build it"
                    : string.Empty;
                return (null, opened.Problem + hint);
            }

            _session = opened.Session;
            _toolset = new AgentToolset(_session);
            return (_toolset, null);
        }
    }

    /// <summary>
    /// Drops the open session so the next call reopens it. After a reindex the old
    /// connection would still work for data (WAL snapshots advance), but reopening also
    /// survives a schema-version rebuild, which no live connection does.
    /// </summary>
    public void Invalidate()
    {
        lock (_sync)
        {
            _session?.Dispose();
            _session = null;
            _toolset = null;
        }
    }

    public void Dispose() => Invalidate();
}
