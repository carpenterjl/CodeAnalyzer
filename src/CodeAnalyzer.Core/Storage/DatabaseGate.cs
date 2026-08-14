namespace CodeAnalyzer.Core.Storage;

/// <summary>
/// The one lock in front of the index connection.
/// <para>
/// A workspace has a single <c>SqliteConnection</c>, and a SQLite connection is not safe to
/// use from two threads at once. Until live updates arrived that was only theoretically a
/// problem: writes happened while the user watched a progress bar. Now the indexer can run
/// unprompted while someone is clicking around, so every read and every write goes through
/// here.
/// </para>
/// <para>
/// The cost is that a query and a write cannot overlap. It is bounded because the lock is
/// taken per operation, not per run: the writer commits in batches and releases between
/// them, so the longest a query can be made to wait is one batch or one resolve.
/// </para>
/// <para>
/// Monitor, deliberately, because it is re-entrant — an index run holds the gate around a
/// resolve that itself reloads the search table through the same gate.
/// </para>
/// </summary>
public sealed class DatabaseGate
{
    private readonly object _sync = new();

    public T Read<T>(Func<T> operation)
    {
        lock (_sync)
        {
            return operation();
        }
    }

    public void Run(Action operation)
    {
        lock (_sync)
        {
            operation();
        }
    }
}
