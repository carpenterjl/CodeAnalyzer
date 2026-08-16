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

    /// <summary>
    /// A markup binding: the path of a <c>{Binding …}</c> or the key of a
    /// <c>{StaticResource …}</c>. Distinct from <see cref="Use"/> because what it can
    /// resolve to is different — a property or field on some type the markup never names,
    /// or a keyed resource element — and because the plain-identifier scope restriction
    /// that keeps loop counters out of the graph would strangle it: a binding crosses
    /// files by design.
    /// </summary>
    Binding = 8,

    /// <summary>
    /// A resource lookup: the key of a <c>{StaticResource …}</c> or
    /// <c>{DynamicResource …}</c>. Separate from <see cref="Binding"/> because the two
    /// resolve into different worlds — a key names a markup element, a path names a
    /// property on a type — and one kind for both let a binding path land on an element
    /// name and vice versa.
    /// </summary>
    Resource = 9,
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
