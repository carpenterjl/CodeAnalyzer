using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAnalyzer.Core.Storage;

namespace CodeAnalyzer.Core.Workspaces;

/// <summary>
/// What the shell was showing when it last closed.
/// <para>
/// Deliberately small. Anything derived from the workspace — the directory selection, the
/// index itself — already lives in that workspace's database, so duplicating it here would
/// give two answers that could disagree.
/// </para>
/// </summary>
public sealed record SessionState
{
    public string? WorkspaceRoot { get; init; }

    public bool DarkTheme { get; init; } = true;

    /// <summary>Stored by name so a reordered enum cannot silently change which view opens.</summary>
    public string ViewMode { get; init; } = "Graph";

    /// <summary>Workspace-relative directory the treemap was showing. Empty is the root.</summary>
    public string TreemapPath { get; init; } = "";

    public bool LiveUpdates { get; init; } = true;

    /// <summary>
    /// Font size of the graph legend, in CSS pixels. A reading preference rather than
    /// anything about the workspace, which is why it lives here and not in the index.
    /// Clamped by <see cref="LegendFontSizes"/> on the way in and out.
    /// </summary>
    public double LegendFontSize { get; init; } = LegendFontSizes.Default;

    /// <summary>
    /// Whether graph nodes draw their parameter list and their descriptor as well as their
    /// name. On by default: the extra lines are what tell two overloads apart, and someone
    /// who finds them crowded can say so once and have it stick.
    /// </summary>
    public bool GraphNodeDetails { get; init; } = true;

    /// <summary>
    /// The selected symbol, recorded by where it is rather than by id. Ids are reassigned
    /// whenever a file is re-parsed, so a stored id would routinely name something else.
    /// </summary>
    public string? FocusedRelativePath { get; init; }

    public string? FocusedSymbolName { get; init; }

    public int FocusedLine { get; init; }
}

/// <summary>
/// The range the legend font size is allowed to take.
/// <para>
/// Bounds exist because the value survives a restart: a stored 0 or 400 would leave the
/// legend unreadable or covering the canvas, with no obvious way back.
/// </para>
/// </summary>
public static class LegendFontSizes
{
    public const double Minimum = 9;

    public const double Maximum = 22;

    /// <summary>Matches the stylesheet's own default, so an unset preference changes nothing.</summary>
    public const double Default = 10.5;

    public const double Step = 1.5;

    public static double Clamp(double size) =>
        double.IsFinite(size) ? Math.Clamp(size, Minimum, Maximum) : Default;
}

/// <summary>
/// Reads and writes <see cref="SessionState"/> next to the workspace databases.
/// <para>
/// Every failure is swallowed on purpose: a session file is a convenience, and no state of
/// it — missing, truncated, written by a future build — is worth refusing to start over.
/// </para>
/// </summary>
public static class SessionStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string GetFilePath() =>
        Path.Combine(Path.GetDirectoryName(WorkspacePaths.GetRootDirectory())!, "session.json");

    /// <param name="filePath">
    /// Overrides the default location. Only tests pass this — the real app has exactly one
    /// session file, and a test writing to it would overwrite the user's own.
    /// </param>
    public static SessionState Load(string? filePath = null)
    {
        try
        {
            var path = filePath ?? GetFilePath();
            if (!File.Exists(path))
            {
                return new SessionState();
            }

            return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path), Options)
                ?? new SessionState();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SessionState();
        }
    }

    /// <param name="filePath">See <see cref="Load"/>.</param>
    public static void Save(SessionState state, string? filePath = null)
    {
        try
        {
            var path = filePath ?? GetFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Losing the session is not worth surfacing.
        }
    }
}
