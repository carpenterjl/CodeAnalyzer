namespace CodeAnalyzer.Core.Domain;

/// <summary>
/// Kind of a declared symbol. Values are persisted in SQLite, so existing
/// members must keep their numeric values; append new kinds at the end.
/// </summary>
public enum SymbolKind
{
    Unknown = 0,
    Function = 1,
    Method = 2,
    Class = 3,
    Struct = 4,
    Union = 5,
    Enum = 6,
    EnumMember = 7,
    Field = 8,
    Variable = 9,
    Constant = 10,
    Macro = 11,
    Typedef = 12,
    Namespace = 13,
    Interface = 14,
    Property = 15,
    Parameter = 16,

    /// <summary>Verilog/SystemVerilog module, or a Python/C# module-like unit.</summary>
    Module = 17,

    /// <summary>Verilog port declaration (input/output/inout).</summary>
    Port = 18,

    /// <summary>An element carrying an id attribute in markup.</summary>
    MarkupElement = 19,
}

/// <summary>
/// Kind of a reference from a source location to a name. Persisted; keep values stable.
/// </summary>
public enum ReferenceKind
{
    Unknown = 0,

    /// <summary>A call expression: <c>foo(...)</c>.</summary>
    Call = 1,

    /// <summary>A read/write of an identifier.</summary>
    Use = 2,

    /// <summary>A name used in type position.</summary>
    TypeUse = 3,

    /// <summary>C/C++ <c>#include</c>.</summary>
    Include = 4,

    /// <summary>Python <c>import</c>, C# <c>using</c>, etc.</summary>
    Import = 5,

    /// <summary>Base class / interface implementation.</summary>
    Inherit = 6,

    /// <summary>Verilog module instantiation.</summary>
    Instantiate = 7,
}

/// <summary>
/// How much the resolver trusts an edge from a reference to a definition.
/// Resolution is syntactic, so this is surfaced in the UI rather than hidden.
/// </summary>
public enum EdgeConfidence
{
    /// <summary>Exactly one candidate definition matched.</summary>
    Unique = 0,

    /// <summary>Several definitions share the name; all candidates are recorded.</summary>
    Ambiguous = 1,

    /// <summary>Matched only by name across a language boundary.</summary>
    Weak = 2,
}
