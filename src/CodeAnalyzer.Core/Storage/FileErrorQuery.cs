using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Storage;

/// <summary>
/// Every file whose last parse was imperfect, counted against the workspace it came from.
/// The denominator travels with the list because the list is meaningless without it: forty
/// files is a rounding error in one workspace and a third of another.
/// </summary>
public sealed record ParseErrorReport(int TotalFiles, IReadOnlyList<FileErrorRecord> Files);

/// <summary>
/// The imperfect-parse list, read straight off a connection.
/// <para>
/// It lives here rather than on <see cref="SqliteIndexStore"/> because two different
/// callers need it through two different connections — the GUI holds a writable store, the
/// CLI a read-only session — and the question has one right answer. Asking it in two places
/// is how the two would drift.
/// </para>
/// </summary>
public static class FileErrorQuery
{
    public static ParseErrorReport Read(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rel_path, language, error,
                   (SELECT COUNT(*) FROM symbol s WHERE s.file_id = file.id),
                   error_line, error_text
            FROM file
            WHERE status <> 0
            ORDER BY rel_path
            """;

        var files = new List<FileErrorRecord>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                files.Add(new FileErrorRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        using var total = connection.CreateCommand();
        total.CommandText = "SELECT COUNT(*) FROM file";
        var totalFiles = Convert.ToInt32(total.ExecuteScalar() ?? 0);

        return new ParseErrorReport(totalFiles, files);
    }
}
