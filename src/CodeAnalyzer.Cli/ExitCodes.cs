namespace CodeAnalyzer.Cli;

/// <summary>
/// Process exit codes, stable so scripts and agents can branch on them.
/// </summary>
internal static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>Bad arguments, unknown command, or a query that could not be answered.</summary>
    public const int Error = 1;

    /// <summary>No cached index exists for the workspace. The message names the fix.</summary>
    public const int NoIndex = 2;

    /// <summary>Another process is rebuilding the index right now; retry shortly.</summary>
    public const int IndexBusy = 3;
}
