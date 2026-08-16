using CodeAnalyzer.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Indexing;

/// <summary>How much index a workspace is carrying: files in, links out.</summary>
public sealed record IndexDensity(int Files, long Links)
{
    /// <summary>Links per indexed file — the shape-of-the-workspace number, not a timing.</summary>
    public double LinksPerFile => Files == 0 ? 0 : (double)Links / Files;
}

/// <summary>One file and the number of links resolved out of it.</summary>
public sealed record HeaviestFile(string RelativePath, long Links);

/// <summary>
/// A run's cost next to the last one's, and — when the jump is worth reporting — the
/// files that account for most of it.
/// </summary>
public sealed record IndexCostReport(
    IndexDensity Current,
    IndexDensity? Previous,
    IReadOnlyList<HeaviestFile> Heaviest)
{
    /// <summary>
    /// True when each file is now producing several times the links it used to. Deliberately
    /// not "the run took longer": see <see cref="IndexCostProbe"/>.
    /// </summary>
    public bool IsSurprising =>
        Previous is { Files: > 0, Links: > 0 }
        && Current.Files > 0
        && Current.LinksPerFile >= Previous.LinksPerFile * IndexCostProbe.SurpriseFactor;
}

/// <summary>
/// Answers "did this run cost far more than the last one, and if so what did it?".
/// <para>
/// The measure is <em>links per indexed file</em>, compared against the same figure stored
/// after the previous run. It is deliberately not elapsed time: this machine routinely
/// runs every stage 2–4× slow under unrelated load — the benchmark notes throw whole runs
/// away for it — so a clock-based alarm would fire on a busy afternoon and be trained away
/// long before it ever caught anything. Density is a property of the workspace and the
/// analyzer, so it says the same thing on a quiet machine and a loaded one, and it moves
/// for exactly one reason: something was indexed whose text is not the kind of source a
/// caller list means anything for.
/// </para>
/// <para>
/// A workspace that honestly doubles in size doubles both numbers and says nothing, which
/// is the point — the alarm is for a shape change, not for growth. The threshold is a
/// judgement call rather than a derived constant, and it is set where it is because the run
/// that earned this probe multiplied density twenty-seven-fold; three is far enough below
/// that to catch a smaller version of the same accident and far enough above normal drift
/// to stay quiet otherwise.
/// </para>
/// <para>
/// Nothing here fails a run or changes what is indexed. It prints a sentence.
/// </para>
/// </summary>
public static class IndexCostProbe
{
    /// <summary>How many times denser an index may get before the run says so.</summary>
    public const double SurpriseFactor = 3.0;

    /// <summary>Files named as the biggest contributors when the jump is reported.</summary>
    public const int HeaviestCount = 3;

    public const string MetaLastRunFiles = "last_run_files";
    public const string MetaLastRunLinks = "last_run_links";

    /// <summary>
    /// Reads the current totals and the previous run's, and — only when the comparison is
    /// worth reporting — the heaviest contributing files. The heaviest query aggregates
    /// every edge in the workspace, so it is not run unless there is something to explain.
    /// </summary>
    public static IndexCostReport Measure(SqliteConnection connection)
    {
        var current = new IndexDensity(
            Count(connection, "SELECT COUNT(*) FROM file"),
            Count(connection, "SELECT COUNT(*) FROM edge"));

        var previous = ReadPrevious(connection);
        var report = new IndexCostReport(current, previous, []);

        return report.IsSurprising
            ? report with { Heaviest = ReadHeaviest(connection) }
            : report;
    }

    /// <summary>
    /// Stores this run's totals as the baseline the next run is measured against. Written
    /// whatever the verdict was: the new shape is the truth from here on, so an accepted
    /// jump must not keep re-reporting itself every run afterwards.
    /// </summary>
    public static void Record(SqliteConnection connection, IndexDensity density)
    {
        Schema.WriteMeta(connection, MetaLastRunFiles, density.Files.ToString());
        Schema.WriteMeta(connection, MetaLastRunLinks, density.Links.ToString());
    }

    /// <summary>
    /// The sentence to print, or null when there is nothing to report. Says what changed,
    /// names what caused it, and states what it might mean without deciding — the files it
    /// names may be perfectly legitimate, and only the reader knows.
    /// </summary>
    public static string? Describe(IndexCostReport report)
    {
        if (!report.IsSurprising || report.Previous is null)
        {
            return null;
        }

        var lines = new List<string>
        {
            $"note: this index now holds {report.Current.LinksPerFile:N0} links per file, "
            + $"up from {report.Previous.LinksPerFile:N0} "
            + $"({report.Current.Files:N0} files, {report.Current.Links:N0} links).",
        };

        if (report.Heaviest.Count > 0)
        {
            lines.Add("      most of them come from:");
            foreach (var file in report.Heaviest)
            {
                lines.Add($"        {file.Links,9:N0}  {file.RelativePath}");
            }
        }

        lines.Add(
            "      a jump this size usually means a file was indexed whose names are not "
            + "worth searching —");
        lines.Add(
            "      generated, vendored or minified source. If those files belong, nothing "
            + "is wrong and this");
        lines.Add("      will not be said again.");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The same finding in one sentence, for a status bar that has one line. Names the
    /// single heaviest file rather than three, because that is the one worth looking at
    /// first and the rest are a command away.
    /// </summary>
    public static string? DescribeShort(IndexCostReport report)
    {
        if (!report.IsSurprising || report.Previous is null)
        {
            return null;
        }

        var blame = report.Heaviest.Count > 0
            ? $", mostly {Path.GetFileName(report.Heaviest[0].RelativePath)}"
            : string.Empty;

        return $"Links per file jumped from {report.Previous.LinksPerFile:N0} "
            + $"to {report.Current.LinksPerFile:N0}{blame}.";
    }

    private static IndexDensity? ReadPrevious(SqliteConnection connection)
    {
        var files = Schema.ReadMeta(connection, MetaLastRunFiles);
        var links = Schema.ReadMeta(connection, MetaLastRunLinks);

        // A cache written before this probe existed simply has no baseline: the first run
        // after an upgrade reports nothing and records one.
        return int.TryParse(files, out var fileCount) && long.TryParse(links, out var linkCount)
            ? new IndexDensity(fileCount, linkCount)
            : null;
    }

    private static IReadOnlyList<HeaviestFile> ReadHeaviest(SqliteConnection connection)
    {
        // edge.src_file_id is denormalised (schema v8), so this never touches ref or symbol.
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.rel_path, COUNT(*) AS links
            FROM edge e
            JOIN file f ON f.id = e.src_file_id
            GROUP BY e.src_file_id
            ORDER BY links DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", HeaviestCount);

        var results = new List<HeaviestFile>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new HeaviestFile(reader.GetString(0), reader.GetInt64(1)));
        }

        return results;
    }

    private static int Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }
}
