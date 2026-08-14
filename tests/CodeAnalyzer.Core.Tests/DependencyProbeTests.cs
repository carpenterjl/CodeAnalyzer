using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Resolution;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// The probe decides what an include or import goes looking for. An empty probe means
/// "this cannot name a workspace file", and nothing with an empty probe is ever matched,
/// so these cases are the line between a resolved dependency and an invented one.
/// </summary>
public class DependencyProbeTests
{
    [Theory]
    [InlineData("uart.h", "uart.h")]
    [InlineData("drivers/uart.h", "drivers/uart.h")]
    [InlineData("./local.h", "local.h")]
    [InlineData("drivers\\uart.h", "drivers/uart.h")]
    public void CIncludesAreAlreadyPaths(string include, string expected) =>
        Assert.Equal(expected, DependencyProbe.For(LanguageNames.C, "app/main.c", include));

    [Theory]
    [InlineData("collections", "collections.py")]
    [InlineData("os.path", "os/path.py")]
    [InlineData("pkg.sub.mod", "pkg/sub/mod.py")]
    public void AnAbsolutePythonImportBecomesAPathSuffix(string import, string expected) =>
        Assert.Equal(expected, DependencyProbe.For(LanguageNames.Python, "app/main.py", import));

    [Theory]
    // One dot is the importing file's own package.
    [InlineData("pkg/sub/mod.py", ".device", "pkg/sub/device.py")]
    // Each further dot climbs a level.
    [InlineData("pkg/sub/mod.py", "..shared", "pkg/shared.py")]
    [InlineData("pkg/sub/mod.py", "..util.io", "pkg/util/io.py")]
    [InlineData("mod.py", ".device", "device.py")]
    public void ARelativePythonImportIsResolvedAgainstTheImportingFile(
        string from, string import, string expected) =>
        Assert.Equal(expected, DependencyProbe.For(LanguageNames.Python, from, import));

    [Theory]
    // Climbing past the workspace root points outside anything we indexed.
    [InlineData("mod.py", "...outside")]
    // `from . import x` names the package directory, not a file.
    [InlineData("pkg/mod.py", ".")]
    public void APythonImportThatNamesNoFileGetsNoProbe(string from, string import) =>
        Assert.Equal(string.Empty, DependencyProbe.For(LanguageNames.Python, from, import));

    [Theory]
    [InlineData("System")]
    [InlineData("System.Collections.Generic")]
    public void ACSharpUsingNamesANamespaceAndSoHasNoProbe(string import)
    {
        // C# does not require a namespace to match a file layout, so turning one into a
        // path would be a guess dressed up as a fact.
        Assert.Equal(string.Empty, DependencyProbe.For(LanguageNames.CSharp, "src/Device.cs", import));
    }

    [Theory]
    [InlineData("css/app.css", "css/app.css")]
    [InlineData("./js/app.js", "js/app.js")]
    [InlineData("/js/app.js", "js/app.js")]
    [InlineData("detail.html?v=2", "detail.html")]
    [InlineData("detail.html#top", "detail.html")]
    public void MarkupResourcesKeepOnlyTheirPath(string href, string expected) =>
        Assert.Equal(expected, DependencyProbe.For(LanguageNames.Html, "index.html", href));

    [Theory]
    [InlineData("https://example.com/app.css")]
    [InlineData("//cdn.example.com/app.js")]
    [InlineData("#section")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("data:text/css,body{}")]
    public void MarkupResourcesOutsideTheWorkspaceGetNoProbe(string href) =>
        Assert.Equal(string.Empty, DependencyProbe.For(LanguageNames.Html, "index.html", href));

    [Theory]
    [InlineData("app/main.c", "../drivers/uart.h", "drivers/uart.h")]
    [InlineData("src/app/main.c", "../../lib/hal.h", "lib/hal.h")]
    [InlineData("src/app/main.c", "../app/./util.h", "src/app/util.h")]
    public void AnIncludeThatClimbsIsResolvedAgainstTheIncludingFile(
        string from, string include, string expected) =>
        Assert.Equal(expected, DependencyProbe.For(LanguageNames.C, from, include));

    [Theory]
    [InlineData("drivers/uart.h")]
    [InlineData("common.h")]
    public void AnIncludeWithoutDotsIsLeftExactlyAsWritten(string include) =>
        // The resolver tries it against the including file's own directory first, which is
        // the rule C actually states. Rewriting it here would take that pass away.
        Assert.Equal(include, DependencyProbe.For(LanguageNames.C, "app/main.c", include));

    [Fact]
    public void ClimbingPastTheWorkspaceRootLeavesNothingToLookFor()
    {
        // Whatever ../../../vendor/x.h names, it is not in this workspace, and matching it
        // on filename alone would bind it to something that merely shares the name.
        Assert.Equal(string.Empty, DependencyProbe.For(LanguageNames.C, "app/main.c", "../../vendor/x.h"));
        Assert.Equal(string.Empty, DependencyProbe.For(LanguageNames.Html, "index.html", "../shared.css"));
    }

    [Fact]
    public void TheBaseNameIsTheSeekKeyAndIsEmptyWhenThereIsNoProbe()
    {
        Assert.Equal("uart.h", DependencyProbe.BaseNameOf("drivers/uart.h"));
        Assert.Equal("mod.py", DependencyProbe.BaseNameOf("pkg/sub/mod.py"));
        Assert.Equal(string.Empty, DependencyProbe.BaseNameOf(string.Empty));
    }
}
