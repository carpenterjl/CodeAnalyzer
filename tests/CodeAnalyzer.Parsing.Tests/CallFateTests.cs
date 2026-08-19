using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Parsing;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// What happens to a call's result — the column the call-flow view narrates from.
/// <para>
/// The classifier walks ancestors and errs toward silence: every claim tested here is a
/// shape it reads deliberately, and the last tests pin the degradations — a chained call
/// makes no claim, an unsupported language makes no claim, and a reference that is not a
/// call site stores nothing at all. That NULL-versus-Unknown split is an invariant the
/// trace query filters on, so it is tested as hard as the happy paths.
/// </para>
/// </summary>
public class CallFateTests : IDisposable
{
    private readonly TreeSitterAnalyzer _csharp =
        new(LanguageRegistry.ForName(LanguageRegistry.CSharp)!);
    private readonly TreeSitterAnalyzer _python =
        new(LanguageRegistry.ForName(LanguageRegistry.Python)!);
    private readonly TreeSitterAnalyzer _c =
        new(LanguageRegistry.ForName(LanguageRegistry.C)!);

    public void Dispose()
    {
        _csharp.Dispose();
        _python.Dispose();
        _c.Dispose();
    }

    private static ReferenceRecord Ref(
        ParseResult result, string name, ReferenceKind kind = ReferenceKind.Call) =>
        result.References.First(r => r.Name == name && r.Kind == kind);

    private ParseResult CSharp(string body) =>
        _csharp.Analyze("f.cs", $$"""
            public class C
            {
                {{body}}
            }
            """, CancellationToken.None);

    // ---- C# ----

    [Fact]
    public void ALocalInitializerIsAnAssignmentToItsName()
    {
        var result = CSharp("void M() { var cfg = Load(); }");
        var reference = Ref(result, "Load");

        Assert.Equal(ResultFate.Assigned, reference.Fate);
        Assert.Equal("cfg", reference.FateName);
    }

    [Fact]
    public void AFieldInitializerIsAnAssignmentToTheField()
    {
        var result = CSharp("private int _n = Next();");
        var reference = Ref(result, "Next");

        Assert.Equal(ResultFate.Assigned, reference.Fate);
        Assert.Equal("_n", reference.FateName);
    }

    [Fact]
    public void AnAssignmentTargetMayBeAMemberAccess()
    {
        var result = CSharp("void M() { this.Total = Sum(); }");
        var reference = Ref(result, "Sum");

        Assert.Equal(ResultFate.Assigned, reference.Fate);
        Assert.Equal("this.Total", reference.FateName);
    }

    [Fact]
    public void ABareStatementDiscardsItsResult()
    {
        var result = CSharp("void M() { Tick(); }");

        Assert.Equal(ResultFate.Discarded, Ref(result, "Tick").Fate);
    }

    [Fact]
    public void AssigningToTheDiscardIsADiscardNotAnAssignment()
    {
        var result = CSharp("void M() { _ = Tick(); }");
        var reference = Ref(result, "Tick");

        Assert.Equal(ResultFate.Discarded, reference.Fate);
        Assert.Null(reference.FateName);
    }

    [Fact]
    public void AReturnedCallIsReturned()
    {
        var result = CSharp("int M() { return Compute(); }");

        Assert.Equal(ResultFate.Returned, Ref(result, "Compute").Fate);
    }

    [Fact]
    public void AnExpressionBodiedMemberReturnsItsCall()
    {
        var result = CSharp("int P => Compute();");

        Assert.Equal(ResultFate.Returned, Ref(result, "Compute").Fate);
    }

    [Fact]
    public void ACallInsideAnotherCallsArgumentsIsPassedOn()
    {
        var result = CSharp("void M() { Log(Compute()); }");

        Assert.Equal(ResultFate.PassedAsArgument, Ref(result, "Compute").Fate);

        // The enclosing call still carries its own fate.
        Assert.Equal(ResultFate.Discarded, Ref(result, "Log").Fate);
    }

    [Fact]
    public void AConditionIsTestedEvenThroughANegation()
    {
        var result = CSharp("void M() { if (Ready()) { } while (!Done()) { } }");

        Assert.Equal(ResultFate.Tested, Ref(result, "Ready").Fate);
        Assert.Equal(ResultFate.Tested, Ref(result, "Done").Fate);
    }

    [Fact]
    public void AnAwaitedCallKeepsTheFateAroundIt()
    {
        var result = CSharp("async Task M() { var x = await LoadAsync(); }");
        var reference = Ref(result, "LoadAsync");

        Assert.Equal(ResultFate.Assigned, reference.Fate);
        Assert.Equal("x", reference.FateName);
    }

    [Fact]
    public void ATernaryBranchValueFlowsToTheAssignment()
    {
        var result = CSharp("void M(bool f) { var y = f ? Compute() : 0; }");
        var reference = Ref(result, "Compute");

        Assert.Equal(ResultFate.Assigned, reference.Fate);
        Assert.Equal("y", reference.FateName);
    }

    [Fact]
    public void AChainedCallMakesNoClaim()
    {
        var result = CSharp("void M() { Load().Validate(); }");

        Assert.Equal(ResultFate.Unknown, Ref(result, "Load").Fate);
    }

    [Fact]
    public void AConstructionCarriesFateOnItsTypeUse()
    {
        var result = CSharp("void M() { var f = new Frame(1); }");
        var reference = Ref(result, "Frame", ReferenceKind.TypeUse);

        Assert.Equal(ResultFate.Assigned, reference.Fate);
        Assert.Equal("f", reference.FateName);
    }

    [Fact]
    public void AGenericConstructionCarriesFateThroughItsGenericName()
    {
        // The winning capture for `new List<int>()` is the generic_name, not the
        // creation expression — the analyzer finds the creation one level up.
        var result = CSharp("void M() { var items = new List<int>(); }");
        var reference = Ref(result, "List", ReferenceKind.TypeUse);

        Assert.Equal(ResultFate.Assigned, reference.Fate);
        Assert.Equal("items", reference.FateName);
    }

    [Fact]
    public void AGenericTypeOutsideACreationStaysSilent()
    {
        var result = CSharp("private List<int> _items;");
        var reference = Ref(result, "List", ReferenceKind.TypeUse);

        Assert.Null(reference.Fate);
    }

    // ---- Python ----

    [Fact]
    public void PythonAssignmentAndAugmentedAssignmentNameTheirTarget()
    {
        var result = _python.Analyze("f.py", """
            x = load()
            x += more()
            """, CancellationToken.None);

        Assert.Equal(ResultFate.Assigned, Ref(result, "load").Fate);
        Assert.Equal("x", Ref(result, "load").FateName);
        Assert.Equal(ResultFate.Assigned, Ref(result, "more").Fate);
    }

    [Fact]
    public void PythonWalrusIsTheNearerAssignment()
    {
        var result = _python.Analyze("f.py", """
            if (y := load()):
                pass
            """, CancellationToken.None);
        var reference = Ref(result, "load");

        Assert.Equal(ResultFate.Assigned, reference.Fate);
        Assert.Equal("y", reference.FateName);
    }

    [Fact]
    public void PythonStatementReturnArgumentAndConditionsClassify()
    {
        var result = _python.Analyze("f.py", """
            def run():
                tick()
                log(compute())
                if ready():
                    pass
                while not done():
                    pass
                assert valid()
                return finish()
            """, CancellationToken.None);

        Assert.Equal(ResultFate.Discarded, Ref(result, "tick").Fate);
        Assert.Equal(ResultFate.PassedAsArgument, Ref(result, "compute").Fate);
        Assert.Equal(ResultFate.Tested, Ref(result, "ready").Fate);
        Assert.Equal(ResultFate.Tested, Ref(result, "done").Fate);
        Assert.Equal(ResultFate.Tested, Ref(result, "valid").Fate);
        Assert.Equal(ResultFate.Returned, Ref(result, "finish").Fate);
    }

    [Fact]
    public void PythonKeywordArgumentIsPassedOn()
    {
        var result = _python.Analyze("f.py", """
            configure(mode=detect())
            """, CancellationToken.None);

        Assert.Equal(ResultFate.PassedAsArgument, Ref(result, "detect").Fate);
    }

    [Fact]
    public void PythonAwaitKeepsTheFateAroundIt()
    {
        var result = _python.Analyze("f.py", """
            async def go():
                data = await fetch()
            """, CancellationToken.None);
        var reference = Ref(result, "fetch");

        Assert.Equal(ResultFate.Assigned, reference.Fate);
        Assert.Equal("data", reference.FateName);
    }

    [Fact]
    public void PythonWithAsAssignsToTheAlias()
    {
        var result = _python.Analyze("f.py", """
            with open(path) as fh:
                pass
            """, CancellationToken.None);
        var reference = Ref(result, "open");

        Assert.Equal(ResultFate.Assigned, reference.Fate);
        Assert.Equal("fh", reference.FateName);
    }

    // ---- Degradation: the NULL-versus-Unknown split ----

    [Fact]
    public void AnUnsupportedLanguageStoresUnknownOnCallsNotNull()
    {
        var result = _c.Analyze("f.c", """
            void run(void) { int x = next(); }
            """, CancellationToken.None);

        Assert.Equal(ResultFate.Unknown, Ref(result, "next").Fate);
    }

    [Fact]
    public void AReferenceThatIsNotACallSiteStoresNothing()
    {
        var result = CSharp("private Frame _frame;");
        var reference = Ref(result, "Frame", ReferenceKind.TypeUse);

        Assert.Null(reference.Fate);
        Assert.Null(reference.FateName);
    }
}
