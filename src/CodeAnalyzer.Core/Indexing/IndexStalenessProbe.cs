using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Indexing;

/// <summary>
/// How far the indexed files have drifted from what is on disk.
/// <para>
/// There is no "added" count, deliberately. This compares the files the index already knows
/// about and nothing else, so a file created since the last run is invisible to it — finding
/// those means a full crawl, which is too much to spend before answering a query. Every
/// rendering therefore says <em>indexed files</em>, which is the exact scope of the claim.
/// </para>
/// </summary>
/// <param name="Changed">Indexed files whose size or timestamp no longer match.</param>
/// <param name="Removed">Indexed files that are no longer on disk.</param>
/// <param name="Examined">How many indexed files were compared.</param>
/// <param name="Complete">False when counting stopped at the cap, so the counts are floors.</param>
public sealed record IndexStaleness(int Changed, int Removed, int Examined, bool Complete)
{
    public int Total => Changed + Removed;

    public bool IsStale => Total > 0;
}

/// <summary>
/// Answers "how much has moved since this index was built" cheaply enough to run before a
/// query. The build date alone is a fact a reader has to interpret; a count is one they can
/// act on.
/// </summary>
public static class IndexStalenessProbe
{
    /// <summary>
    /// Counting stops here. The number is only ever used to say "this index is behind", and
    /// past a few hundred files a larger figure changes nothing a reader would do about it.
    /// </summary>
    public const int MaxCounted = 500;

    /// <summary>
    /// Compares every indexed file against disk using the same size-and-timestamp screen the
    /// incremental indexer uses to choose what to re-parse — so a file counted here is
    /// exactly a file the next index run would re-read. Content is never hashed: this runs
    /// on the read path, and the screen is the cheap half of that decision by design.
    /// </summary>
    public static IndexStaleness Compare(
        SqliteConnection connection,
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var changed = 0;
        var removed = 0;
        var examined = 0;
        var complete = true;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rel_path, size, mtime FROM file";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (changed + removed >= MaxCounted)
            {
                complete = false;
                break;
            }

            examined++;

            try
            {
                var info = new FileInfo(Path.Combine(rootPath, reader.GetString(0)));

                if (!info.Exists)
                {
                    removed++;
                    continue;
                }

                var stamp = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                if (info.Length != reader.GetInt64(1) || stamp != reader.GetInt64(2))
                {
                    changed++;
                }
            }
            catch (Exception e)
                when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A file we cannot stat is not evidence of a change, and a provenance line is
                // never worth failing a query over.
            }
        }

        return new IndexStaleness(changed, removed, examined, complete);
    }
}
