using System.Text;
using CodeAnalyzer.Core.Crawling;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

public class IgnoreRulesTests
{
    [Theory]
    [InlineData(".git")]
    [InlineData("node_modules")]
    [InlineData("obj")]
    [InlineData("BIN")]          // matching is case-insensitive
    [InlineData("__pycache__")]
    [InlineData(".hidden")]      // any dot-prefixed directory
    public void IsIgnoredDirectoryName_ExcludesBuildAndVcsDirectories(string name) =>
        Assert.True(IgnoreRules.IsIgnoredDirectoryName(name));

    [Theory]
    [InlineData("src")]
    [InlineData("drivers")]
    [InlineData("rtl")]
    [InlineData("include")]
    public void IsIgnoredDirectoryName_KeepsSourceDirectories(string name) =>
        Assert.False(IgnoreRules.IsIgnoredDirectoryName(name));

    [Theory]
    [InlineData(".exe")]
    [InlineData(".PNG")]
    [InlineData(".pyc")]
    public void IsIgnoredFileExtension_ExcludesBinaries(string extension) =>
        Assert.True(IgnoreRules.IsIgnoredFileExtension(extension));

    [Theory]
    [InlineData(".c")]
    [InlineData(".sv")]
    [InlineData(".py")]
    [InlineData(".cs")]
    public void IsIgnoredFileExtension_KeepsSourceExtensions(string extension) =>
        Assert.False(IgnoreRules.IsIgnoredFileExtension(extension));

    [Fact]
    public void LooksBinary_DetectsNulByte() =>
        Assert.True(IgnoreRules.LooksBinary(new byte[] { 0x7F, 0x45, 0x00, 0x01 }));

    [Fact]
    public void LooksBinary_AcceptsPlainText() =>
        Assert.False(IgnoreRules.LooksBinary(Encoding.UTF8.GetBytes("int main(void) { return 0; }")));
}
