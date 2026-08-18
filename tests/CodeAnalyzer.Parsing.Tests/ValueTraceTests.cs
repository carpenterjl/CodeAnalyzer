using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Workspaces;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// One indexed workspace holding the same protocol agreement written four ways: the C#
/// that sends a command byte, the C firmware that receives it, the Python tool that pokes
/// at it and the RTL that decodes it. No reference connects any of them — the only thing
/// they share is the value, which is exactly what this milestone traces.
/// </summary>
public sealed class ValueTraceFixture : IDisposable
{
    public ValueTraceFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "codeanalyzer-values", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        WriteFile("software/Protocol.cs", """
            namespace Software;

            public static class Protocol
            {
                public const byte CmdRead = 0xA5;
                public const int LegacyCommand = 165;
                public const string PortName = "COM3";
                public const int Baud = 115200;
                public const int Retries = 3;
            }
            """);

        WriteFile("firmware/protocol.h", """
            #define CMD_READ 0xA5
            #define CMD_WRITE (0x5A)
            #define UART_BAUD 115200
            #define BUFFER_LEN 8
            #define FRAME_MARK 052
            """);

        WriteFile("tools/probe.py", """
            CMD_READ = 0xA5
            PORT_NAME = "COM3"
            BAUD = 115_200
            TIMEOUT = 1.5
            """);

        // A registration table, the shape JGraph writes its builtins in: the key is a
        // string literal handed to a call, never a declaration, and the same key given
        // twice from two files is a shadowed entry with the later one silently winning.
        WriteFile("software/Builtins.cs", """
            namespace Software;

            public static class Builtins
            {
                public static void Register()
                {
                    Define("contour", Contour);
                    Define("surf", Surface);
                }
            }
            """);

        WriteFile("software/Builtins.Extra.cs", """
            namespace Software;

            public static class BuiltinsExtra
            {
                public static void RegisterMore()
                {
                    DefineSilent("contour", ContourAgain);
                }
            }
            """);

        WriteFile("rtl/decoder.v", """
            module decoder;
              parameter CMD_READ = 8'hA5;
              parameter WIDTH = 8;
            endmodule
            """);

        Session = WorkspaceSession.Open(Root, new TreeSitterAnalyzerFactory());
        Session.IndexAsync([string.Empty]).GetAwaiter().GetResult();
    }

    public string Root { get; }

    public WorkspaceSession Session { get; }

    /// <summary>The definition of <paramref name="name"/> in the file whose path contains
    /// <paramref name="pathPart"/> — the fixture deliberately reuses names across languages.</summary>
    public long SymbolId(string name, string pathPart)
    {
        var hits = Session.Read(() => Session.Search.Search(name, new SymbolSearchOptions { Limit = 50 }));
        var hit = hits.Single(h => h.Name == name && h.RelativePath.Contains(pathPart, StringComparison.Ordinal));
        return hit.SymbolId;
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        Session.Dispose();
        WorkspaceCacheCleanup.Delete(Root);
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failures are not test failures.
        }
    }
}

public class ValueTraceTests(ValueTraceFixture fixture) : IClassFixture<ValueTraceFixture>
{
    private ValueMatchSet? SameValue(long symbolId) =>
        fixture.Session.Read(() => fixture.Session.Values.GetSameValue(symbolId));

    [Fact]
    public void OneCommandByteIsFoundInFourLanguagesThroughFourNotations()
    {
        var set = SameValue(fixture.SymbolId("CMD_READ", "firmware/"));

        Assert.NotNull(set);
        Assert.Equal("165", set.Canonical);

        // 0xA5 in C#, 165 written plainly, 0xA5 in Python, 8'hA5 in Verilog. Not one of
        // these references any other.
        Assert.Contains(set.Matches, m => m.Name == "CmdRead" && m.Language == LanguageNames.CSharp);
        Assert.Contains(set.Matches, m => m.Name == "LegacyCommand" && m.Verbatim == "165");
        Assert.Contains(set.Matches, m => m.Name == "CMD_READ" && m.Language == LanguageNames.Python);
        Assert.Contains(set.Matches, m => m.Name == "CMD_READ" && m.Language == LanguageNames.Verilog);

        // The symbol being asked about is not part of its own answer.
        Assert.DoesNotContain(set.Matches, m => m.RelativePath.Contains("firmware/", StringComparison.Ordinal));
    }

    [Fact]
    public void TheNoteStatesBothFormsAndWhatIsBeingClaimed()
    {
        var set = SameValue(fixture.SymbolId("CMD_READ", "firmware/"));

        var verilog = Assert.Single(set!.Matches, m => m.Language == LanguageNames.Verilog);
        Assert.Equal("8'hA5 = 165 — numerically equal", verilog.EqualityNote);

        var legacy = Assert.Single(set.Matches, m => m.Name == "LegacyCommand");
        Assert.Equal("165 — numerically equal", legacy.EqualityNote);
    }

    [Fact]
    public void MatchesFromOtherLanguagesComeFirst()
    {
        // They are the ones no other view in the tool can find, which is the whole reason
        // this query exists.
        var set = SameValue(fixture.SymbolId("Baud", "software/"));

        Assert.NotNull(set);
        Assert.NotEqual(LanguageNames.CSharp, set.Matches[0].Language);
        Assert.Contains(LanguageNames.C, set.OtherLanguages);
        Assert.Contains(LanguageNames.Python, set.OtherLanguages);
    }

    [Fact]
    public void ASeparatedLiteralIsTheSameValueAsAPlainOne()
    {
        var set = SameValue(fixture.SymbolId("UART_BAUD", "firmware/"));

        Assert.Contains(set!.Matches, m => m.Name == "BAUD" && m.Verbatim == "115_200");
    }

    [Fact]
    public void AStringConstantCrossesLanguagesToo()
    {
        var set = SameValue(fixture.SymbolId("PortName", "software/"));

        Assert.NotNull(set);
        Assert.Equal("\"COM3\"", set.Canonical);

        var python = Assert.Single(set.Matches);
        Assert.Equal("PORT_NAME", python.Name);
        Assert.Equal("\"COM3\" — identical text", python.EqualityNote);
    }

    [Fact]
    public void AValueNothingElseCarriesReportsNoMatchesRatherThanAnEmptyList()
    {
        // Nothing else in the workspace is 3, and "no other definition has this value" is
        // a different answer from "this symbol has no value".
        Assert.Null(SameValue(fixture.SymbolId("Retries", "software/")));
    }

    [Fact]
    public void ASymbolWithNoCertifiableLiteralDoesNotParticipate()
    {
        // A float is deliberately excluded: cross-language float equality is a claim about
        // representation, not about notation.
        Assert.Null(SameValue(fixture.SymbolId("TIMEOUT", "tools/")));

        // Neither does a symbol that is not a constant at all.
        Assert.Null(SameValue(fixture.SymbolId("decoder", "rtl/")));
    }

    [Fact]
    public void AStringHandedToACallIsFoundWhereNoDefinitionCarriesIt()
    {
        // JGraph's third report ranked "flag two definitions registering the same key"
        // second. There are no definitions here to flag — a registration key is an
        // ARGUMENT — and the tool answered "no definition carries the value" while the
        // argument text sat indexed. Both sites, no heuristic, no guess about which is dead.
        var set = fixture.Session.Read(() => fixture.Session.Values.FindByValue("\"contour\"", 20));

        Assert.NotNull(set);
        Assert.Empty(set.Matches);
        Assert.Equal(2, set.ArgumentSites.Count);
        Assert.Contains(set.ArgumentSites, a => a.CalleeName == "Define");
        Assert.Contains(set.ArgumentSites, a => a.CalleeName == "DefineSilent");

        // Two files is what makes it a shadow rather than a sequence; the reader is given
        // the paths and judges, because the tool cannot see which insert wins.
        Assert.Equal(2, set.ArgumentSites.Select(a => a.RelativePath).Distinct().Count());
    }

    [Fact]
    public void AKeyRegisteredOnceIsListedOnce()
    {
        var set = fixture.Session.Read(() => fixture.Session.Values.FindByValue("\"surf\"", 20));

        Assert.NotNull(set);
        Assert.Single(set.ArgumentSites);
    }

    [Fact]
    public void ANumberIsNotChasedThroughArgumentLists()
    {
        // A number in an argument list is arithmetic, not a key. Listing every call that
        // passes 165 would bury the five definitions that are the actual answer.
        var set = fixture.Session.Read(() => fixture.Session.Values.FindByValue("165", 20));

        Assert.NotNull(set);
        Assert.Empty(set.ArgumentSites);
    }

    [Fact]
    public void ALeadingZeroIsReadByTheLanguageThatWroteIt()
    {
        // 052 is octal in C, so FRAME_MARK is 42 — and the C# constant 165 must not
        // become a match for it just because both start with a digit.
        var set = fixture.Session.Read(() => fixture.Session.Values.FindByValue("42", 20));

        Assert.NotNull(set);
        Assert.Contains(set.Matches, m => m.Name == "FRAME_MARK");
    }

    [Fact]
    public void TheSearchQueryFindsByValueInAnyNotation()
    {
        foreach (var typed in new[] { "0xA5", "165", "8'hA5" })
        {
            var set = fixture.Session.Read(() => fixture.Session.Values.FindByValue(typed, 20));

            Assert.NotNull(set);
            Assert.Equal("165", set.Canonical);

            // All five definitions, including the two C# ones — a query by value excludes
            // nothing, unlike the same-value lookup which leaves out its own subject.
            Assert.Equal(5, set.Matches.Count);
        }
    }

    [Fact]
    public void AQueryThatIsNotAValueIsRefusedRatherThanGuessedAt()
    {
        Assert.Null(fixture.Session.Read(() => fixture.Session.Values.FindByValue("CMD_READ", 20)));
    }

    [Fact]
    public void ATightLimitSaysThatItCut()
    {
        var set = fixture.Session.Read(() => fixture.Session.Values.FindByValue("0xA5", 2));

        Assert.NotNull(set);
        Assert.Equal(2, set.Matches.Count);
        Assert.True(set.Truncated);
    }

    [Fact]
    public void SharedValuesAreRankedByHowManyLanguagesAgreeOnThem()
    {
        var groups = fixture.Session.Read(() => fixture.Session.Values.GetSharedValues());

        // 165 spans four languages; 115200 spans three; "COM3" and 8 span two.
        Assert.Equal("165", groups[0].Canonical);
        Assert.Equal(4, groups[0].Languages.Count);

        // Five definitions across those four languages: C# writes the byte twice, once as
        // 0xA5 and once as 165. Ranking counts languages, the total counts definitions,
        // and the two are deliberately different numbers.
        Assert.Equal(5, groups[0].TotalCount);

        // A value carried by only one language is not a crossing and is not listed.
        Assert.DoesNotContain(groups, g => g.Canonical == "3");
        Assert.DoesNotContain(groups, g => g.Canonical == "42");
    }

    [Fact]
    public void TrivialValuesAreExcludedOnlyWhenAsked()
    {
        // WIDTH = 8 in Verilog and BUFFER_LEN = 8 in C: 8 is not trivial and stays either
        // way. 0 and 1 are the ones a caller can ask to drop, and never silently.
        var kept = fixture.Session.Read(() => fixture.Session.Values.GetSharedValues(includeTrivial: true));

        Assert.Contains(kept, g => g.Canonical == "8");
    }
}
