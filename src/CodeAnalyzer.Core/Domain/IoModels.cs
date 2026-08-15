namespace CodeAnalyzer.Core.Domain;

/// <summary>
/// Which way data crosses an I/O boundary.
/// <para>
/// Direction is the one deliberate step beyond source facts in this tool, and it is
/// always labelled with where it came from: a catalog entry carries the API's documented
/// contract, a user mark carries the user's own assertion. The syntax alone cannot say
/// which way <c>HAL_UART_Transmit</c> moves bytes.
/// </para>
/// Values are persisted (settings JSON), so keep them stable.
/// </summary>
public enum IoDirection
{
    /// <summary>No direction — on a user mark this suppresses a catalog match entirely.</summary>
    None = 0,

    /// <summary>External data arriving into the workspace's code.</summary>
    Input = 1,

    /// <summary>Data leaving the workspace's code.</summary>
    Output = 2,

    /// <summary>One call that both sends and receives, e.g. <c>HAL_SPI_TransmitReceive</c>.</summary>
    InOut = 3,
}

/// <summary>Whether a boundary site came from the shipped catalog or from the user.</summary>
public enum IoMatchOrigin
{
    Catalog = 0,
    UserMark = 1,
}

/// <summary>
/// The one wording for directions, consumed by the settings dialog, the detail pane and
/// the JSON payload — the <see cref="KindLabels"/> idiom, because a second copy would
/// eventually disagree.
/// </summary>
public static class IoDirectionLabels
{
    public static string For(IoDirection direction) => direction switch
    {
        IoDirection.Input => "input",
        IoDirection.Output => "output",
        IoDirection.InOut => "input/output",
        IoDirection.None => "suppressed",
        _ => "unknown",
    };

    /// <summary>Wire token for the page: "in", "out" or "inout".</summary>
    public static string TokenFor(IoDirection direction) => direction switch
    {
        IoDirection.Input => "in",
        IoDirection.InOut => "inout",
        _ => "out",
    };
}

/// <summary>
/// A user's own statement that calls to a named function are an I/O boundary.
/// <para>
/// Stored per workspace in the settings JSON. A mark always outranks the catalog for the
/// calls it covers — the user's statement about their own code beats the shipped list —
/// and a mark with <see cref="IoDirection.None"/> suppresses a catalog match without
/// adding anything.
/// </para>
/// </summary>
public sealed record IoMark
{
    /// <summary>The called name exactly as it appears at call sites.</summary>
    public required string Name { get; init; }

    public IoDirection Direction { get; init; }

    /// <summary>
    /// Optional workspace-relative path (file or directory, forward slashes) confining the
    /// mark. Null matches the called name workspace-wide.
    /// </summary>
    public string? Scope { get; init; }
}

/// <summary>
/// One call site where data crosses into or out of the workspace.
/// <para>
/// Everything here except <see cref="Direction"/> is a stored fact: the reference row, its
/// verbatim argument text, its position. The match itself is honest about being a name
/// match — <see cref="GateNote"/> states the co-occurrence rule that admitted a generic
/// member name like <c>Write</c>, and it travels with the site so the UI can always say it.
/// </para>
/// </summary>
public sealed record IoBoundarySite
{
    public long RefId { get; init; }

    /// <summary>The innermost symbol containing the call. Null at file scope.</summary>
    public long? CallerSymbolId { get; init; }

    /// <summary>The calling symbol's name, for rows that list sites away from the graph.</summary>
    public string? CallerName { get; init; }

    /// <summary>The called name as written at the site.</summary>
    public required string Name { get; init; }

    public IoDirection Direction { get; init; }

    public IoMatchOrigin Origin { get; init; }

    /// <summary>Catalog family ("STM32 HAL", ".NET SerialPort"); null for a user mark.</summary>
    public string? Family { get; init; }

    /// <summary>
    /// The co-occurrence rule that gated the match, e.g. "in a file that references
    /// SerialPort". Null when the name alone was distinctive enough to ship ungated.
    /// </summary>
    public string? GateNote { get; init; }

    public required string RelativePath { get; init; }

    public int Line { get; init; }

    /// <summary>Verbatim argument list at the call site, as stored on the reference.</summary>
    public string? ArgumentText { get; init; }
}

/// <summary>
/// One I/O boundary block as the graph draws it: every call from one function to one
/// boundary API, merged the same way edges are — one stub per API per function, with the
/// individual sites listed when the stub is asked about.
/// </summary>
public sealed record IoStub
{
    /// <summary>The function the stub hangs off. Sites without a caller draw nowhere.</summary>
    public required long CallerSymbolId { get; init; }

    /// <summary>The called API name.</summary>
    public required string Name { get; init; }

    public IoDirection Direction { get; init; }

    public IoMatchOrigin Origin { get; init; }

    /// <summary>Catalog families that matched, " / "-joined; null for a user mark.</summary>
    public string? Family { get; init; }

    public string? GateNote { get; init; }

    /// <summary>The first site's verbatim argument list — the data crossing the boundary.</summary>
    public string? ArgumentText { get; init; }

    /// <summary>The reference rows behind this stub, for the per-site detail on click.</summary>
    public required IReadOnlyList<long> RefIds { get; init; }
}

/// <summary>One row of a stub's per-site detail: where the call is and what it passed.</summary>
public sealed record IoSiteDetail(
    long RefId,
    string Name,
    string RelativePath,
    string Language,
    int Line,
    string? ArgumentText,
    long? CallerSymbolId,
    string? CallerName);

/// <summary>One member of a frame layout: a field of the struct crossing the boundary.</summary>
public sealed record IoFrameMember(string Name, string? TypeText, string? Value);

/// <summary>
/// One argument identifier at a boundary call site, chained as far as the stored facts
/// reach: the identifier resolves to a definition, the definition declares a type, and
/// when that type's name matches a workspace struct, the struct's members are the frame
/// layout. Every hop carries its own uncertainty note, because a wrong-but-confident
/// frame layout is exactly what this tool must never show.
/// </summary>
public sealed record IoFrameArgument
{
    /// <summary>The identifier as it appears in the argument list.</summary>
    public required string Token { get; init; }

    /// <summary>"one of N name matches" when the identifier resolved ambiguously; null when unique.</summary>
    public string? ResolutionNote { get; init; }

    /// <summary>True when no definition in the workspace matched the identifier.</summary>
    public bool IsUnresolved { get; init; }

    /// <summary>The resolved definition's declared type, verbatim; null where none is stored.</summary>
    public string? DeclaredType { get; init; }

    /// <summary>Workspace struct/class whose name appears in the declared type; null when none does.</summary>
    public string? StructName { get; init; }

    /// <summary>"one of N types with this name" when several definitions share it; null when unique.</summary>
    public string? StructNote { get; init; }

    /// <summary>The struct's members — the packet framing. Empty when the chain ended earlier.</summary>
    public IReadOnlyList<IoFrameMember> Members { get; init; } = [];
}
