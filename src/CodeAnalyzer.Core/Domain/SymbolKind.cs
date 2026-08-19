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

    /// <summary>
    /// A keyed resource: a XAML <c>x:Key</c>, the name a <c>{StaticResource …}</c> is
    /// written against. Separate from <see cref="MarkupElement"/> because the two are
    /// addressed by different things and live in different namespaces — <c>x:Name</c>
    /// names an element for code-behind and <c>FindName</c>, <c>x:Key</c> names an entry
    /// in a resource dictionary — and one kind for both let
    /// <c>Style="{StaticResource SearchBox}"</c> land on the TextBox named SearchBox
    /// instead of the Style keyed SearchBox two files away.
    /// </summary>
    ResourceKey = 20,
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

    /// <summary>
    /// A markup event handler: the <c>OnAccept</c> of <c>Click="OnAccept"</c>, naming a
    /// method on the code-behind class this file compiles into. Its own kind because what
    /// it may resolve to is narrower than anything else here — not merely a method, but a
    /// method on <em>this file's</em> <c>x:Class</c> — and that narrowness is what makes it
    /// safe to guess at. XAML marks event attributes with no syntax at all, so the pack
    /// nominates candidates by convention and the resolver throws out the ones that cannot
    /// be handlers, which is nearly all of them.
    /// </summary>
    Handler = 10,
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

/// <summary>
/// How the result of a call is consumed at the site it is written. Persisted; keep
/// values stable and append-only.
/// <para>
/// Read off the call node's ancestors at parse time, so it is a claim about syntax, not
/// about execution: <c>var x = Foo();</c> assigns whatever <c>Foo</c> returns, including
/// nothing useful. Stored on the reference because the call-flow view's whole narrative —
/// "and the value came back and was stored as <c>cfg</c>" — is unanswerable without it,
/// and a column is the difference between answering and re-reading source per step.
/// </para>
/// <para>
/// <see cref="Unknown"/> is a real value, distinct from the column being NULL: a call
/// site whose consumption the walker makes no claim about stores 0; a reference that is
/// not a call site at all stores NULL. The trace query filters on that invariant.
/// </para>
/// </summary>
public enum ResultFate
{
    /// <summary>
    /// A call site, but no claim about its result — an unsupported language, or an
    /// ancestor shape the walker does not read (<c>Foo().Bar()</c>, a lambda body,
    /// a comprehension).
    /// </summary>
    Unknown = 0,

    /// <summary>The result lands in a name: an initializer or an assignment.</summary>
    Assigned = 1,

    /// <summary>The call is its own statement; the result is not consumed.</summary>
    Discarded = 2,

    /// <summary>The result is returned (or yielded) by the enclosing callable.</summary>
    Returned = 3,

    /// <summary>The result is written directly inside another call's argument list.</summary>
    PassedAsArgument = 4,

    /// <summary>The result is tested: an if/while/ternary condition, an assert.</summary>
    Tested = 5,
}
