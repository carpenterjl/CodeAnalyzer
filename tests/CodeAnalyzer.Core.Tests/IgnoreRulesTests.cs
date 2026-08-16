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
    [InlineData("site-packages")]
    [InlineData("TestResults")]
    [InlineData("x64")]
    [InlineData("Win32")]
    [InlineData("ARM64")]
    public void IsIgnoredDirectoryName_ExcludesBuildAndVcsDirectories(string name) =>
        Assert.True(IgnoreRules.IsIgnoredDirectoryName(name));

    [Theory]
    [InlineData("src")]
    [InlineData("drivers")]
    [InlineData("rtl")]
    [InlineData("include")]
    [InlineData("Debug")]        // deliberately kept: a built-in cannot be un-ignored,
    [InlineData("Release")]      // and source directories with these names exist
    public void IsIgnoredDirectoryName_KeepsSourceDirectories(string name) =>
        Assert.False(IgnoreRules.IsIgnoredDirectoryName(name));

    [Fact]
    public void IsEnvironmentRoot_RecognisesAVenvByItsOwnConfigFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "codeanalyzer-tests", Guid.NewGuid().ToString("N"));
        try
        {
            // The reported workspace's venv was named "Environment" — no name rule can
            // catch that. pyvenv.cfg is the venv's own declaration of what it is.
            var venv = Path.Combine(root, "Environment");
            Directory.CreateDirectory(venv);
            File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), "home = C:\\Python312");

            var conda = Path.Combine(root, "tools-env");
            Directory.CreateDirectory(Path.Combine(conda, "conda-meta"));

            // An unrelated .cfg is not a declaration.
            var plain = Path.Combine(root, "src");
            Directory.CreateDirectory(plain);
            File.WriteAllText(Path.Combine(plain, "setup.cfg"), "[metadata]");

            Assert.True(IgnoreRules.IsEnvironmentRoot(venv));
            Assert.True(IgnoreRules.IsEnvironmentRoot(conda));
            Assert.False(IgnoreRules.IsEnvironmentRoot(plain));
            Assert.False(IgnoreRules.IsEnvironmentRoot(root));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

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

    [Theory]
    [InlineData("cytoscape.min.js")]
    [InlineData("d3.min.js")]
    [InlineData("bundle.min.mjs")]
    public void IsMinifiedBundle_SkipsGeneratedBundles(string fileName) =>
        Assert.True(IgnoreRules.IsMinifiedBundle(fileName));

    [Theory]
    [InlineData("graph.js")]
    [InlineData("cose-base.js")]
    // The rule is the naming convention and nothing else: a file that merely has "min" in
    // its name is someone's source, and refusing it would lose real code silently.
    [InlineData("minify.js")]
    [InlineData("admin.js")]
    public void IsMinifiedBundle_KeepsHandWrittenSource(string fileName) =>
        Assert.False(IgnoreRules.IsMinifiedBundle(fileName));
}
