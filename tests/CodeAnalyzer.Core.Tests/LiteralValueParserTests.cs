using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// The parser's whole job is to be certain or silent, so the table below is as much about
/// what it refuses as what it reads.
/// </summary>
public class LiteralValueParserTests
{
    [Theory]
    // Radix prefixes, in every language that spells them.
    [InlineData("0xA5", LanguageNames.C, 165)]
    [InlineData("0XA5", LanguageNames.CSharp, 165)]
    [InlineData("0b1010", LanguageNames.CSharp, 10)]
    [InlineData("0B1010", LanguageNames.Cpp, 10)]
    [InlineData("0o755", LanguageNames.Python, 493)]
    // Plain decimals.
    [InlineData("165", LanguageNames.C, 165)]
    [InlineData("0", LanguageNames.C, 0)]
    [InlineData("00", LanguageNames.C, 0)]
    [InlineData("9600", LanguageNames.Python, 9600)]
    // Digit separators, each in a language that has one.
    [InlineData("1_000_000", LanguageNames.CSharp, 1_000_000)]
    [InlineData("1_000_000", LanguageNames.Python, 1_000_000)]
    [InlineData("1'000'000", LanguageNames.Cpp, 1_000_000)]
    [InlineData("0xFF_FF", LanguageNames.CSharp, 65535)]
    // Integer suffixes.
    [InlineData("0xFUL", LanguageNames.C, 15)]
    [InlineData("100u", LanguageNames.C, 100)]
    [InlineData("100L", LanguageNames.CSharp, 100)]
    // Brackets and signs, in the combinations a macro is written with.
    [InlineData("(0xA5)", LanguageNames.C, 165)]
    [InlineData("((42))", LanguageNames.C, 42)]
    [InlineData("(-1)", LanguageNames.C, -1)]
    [InlineData("-(1)", LanguageNames.C, -1)]
    [InlineData("- 0x10", LanguageNames.C, -16)]
    [InlineData("+7", LanguageNames.CSharp, 7)]
    // Verilog sized literals.
    [InlineData("8'hA5", LanguageNames.Verilog, 165)]
    [InlineData("'hA5", LanguageNames.Verilog, 165)]
    [InlineData("4'b1010", LanguageNames.Verilog, 10)]
    [InlineData("8'b1010_1010", LanguageNames.Verilog, 170)]
    [InlineData("16'd9600", LanguageNames.Verilog, 9600)]
    [InlineData("8'shA5", LanguageNames.Verilog, 165)]
    [InlineData("32'hFFFFFFFF", LanguageNames.Verilog, 4294967295)]
    public void AReadableIntegerIsRead(string verbatim, string language, long expected)
    {
        var value = LiteralValueParser.Parse(verbatim, language);

        Assert.Equal(expected, value.Number);
        Assert.Null(value.Text);
    }

    [Fact]
    public void ALeadingZeroMeansOctalInCAndDecimalInCSharp()
    {
        // The one notation whose value depends on which language wrote it. A parser that
        // ignored the language would state one of these as fact while the source meant
        // the other — so the language is a parameter, not a convenience.
        Assert.Equal(42, LiteralValueParser.Parse("052", LanguageNames.C).Number);
        Assert.Equal(42, LiteralValueParser.Parse("052", LanguageNames.Cpp).Number);
        Assert.Equal(52, LiteralValueParser.Parse("052", LanguageNames.CSharp).Number);
        Assert.Equal(52, LiteralValueParser.Parse("052", LanguageNames.Verilog).Number);

        // Python 3 rejects it outright, so whatever the slice is, it is not an integer.
        Assert.Null(LiteralValueParser.Parse("052", LanguageNames.Python).Number);
    }

    [Theory]
    // Floats: cross-language float equality is a claim about representation, not notation.
    [InlineData("1.5", LanguageNames.C)]
    [InlineData("1.5f", LanguageNames.CSharp)]
    [InlineData("1e5", LanguageNames.Python)]
    [InlineData("1m", LanguageNames.CSharp)]
    [InlineData(".5", LanguageNames.C)]
    // Character literals: calling 'A' 65 asserts an encoding the source never states.
    [InlineData("'A'", LanguageNames.C)]
    [InlineData("'\\n'", LanguageNames.Cpp)]
    // Expressions. The .scm packs capture arbitrary initializers, which is exactly why
    // anything left over after the literal has to fail.
    [InlineData("Foo() + 1", LanguageNames.CSharp)]
    [InlineData("BASE | 0x10", LanguageNames.C)]
    [InlineData("0xA5 // the read command", LanguageNames.C)]
    [InlineData("sizeof(int)", LanguageNames.C)]
    [InlineData("(a) + (b)", LanguageNames.C)]
    // Notation the language does not have.
    [InlineData("0o755", LanguageNames.C)]
    [InlineData("1_000", LanguageNames.C)]
    [InlineData("1'000", LanguageNames.C)]
    [InlineData("100L", LanguageNames.Python)]
    // Separators out of place are invalid wherever they are allowed at all.
    [InlineData("_100", LanguageNames.CSharp)]
    [InlineData("100_", LanguageNames.CSharp)]
    [InlineData("1__000", LanguageNames.CSharp)]
    // Past Int64 there is no stored form, so it is refused rather than wrapped.
    [InlineData("0xFFFFFFFFFFFFFFFF", LanguageNames.C)]
    [InlineData("99999999999999999999", LanguageNames.CSharp)]
    // Unknown bits are not a value.
    [InlineData("8'hxx", LanguageNames.Verilog)]
    [InlineData("8'bz", LanguageNames.Verilog)]
    [InlineData("'0", LanguageNames.Verilog)]
    // A literal wider than its declared width is truncated by the language; applying that
    // truncation here would state a value the notation alone does not give.
    [InlineData("4'hFF", LanguageNames.Verilog)]
    public void AnythingNotCertainIsRefused(string verbatim, string language)
    {
        var value = LiteralValueParser.Parse(verbatim, language);

        Assert.False(value.HasValue);
    }

    [Theory]
    [InlineData("\"COM3\"", LanguageNames.CSharp, "COM3")]
    [InlineData("\"COM3\"", LanguageNames.C, "COM3")]
    [InlineData("@\"COM3\"", LanguageNames.CSharp, "COM3")]
    [InlineData("'COM3'", LanguageNames.Python, "COM3")]
    [InlineData("b'COM3'", LanguageNames.Python, "COM3")]
    [InlineData("r\"C:\\dev\"", LanguageNames.Python, "C:\\dev")]
    [InlineData("\"a\\tb\"", LanguageNames.C, "a\tb")]
    [InlineData("\"say \\\"hi\\\"\"", LanguageNames.CSharp, "say \"hi\"")]
    [InlineData("@\"say \"\"hi\"\"\"", LanguageNames.CSharp, "say \"hi\"")]
    public void APlainQuotedStringIsRead(string verbatim, string language, string expected)
    {
        var value = LiteralValueParser.Parse(verbatim, language);

        Assert.Equal(expected, value.Text);
        Assert.Null(value.Number);
    }

    [Theory]
    // Interpolation is not a literal: the value is not in the source.
    [InlineData("$\"port {n}\"", LanguageNames.CSharp)]
    [InlineData("f\"port {n}\"", LanguageNames.Python)]
    // Concatenation is more than one literal.
    [InlineData("\"COM\" \"3\"", LanguageNames.C)]
    [InlineData("\"COM\" + Port", LanguageNames.CSharp)]
    // An escape this parser does not know would leave a partly decoded string, which is
    // worse than no answer.
    [InlineData("\"\\u0041\"", LanguageNames.CSharp)]
    [InlineData("\"unterminated", LanguageNames.C)]
    // A single quote delimits a character everywhere except Python.
    [InlineData("'COM3'", LanguageNames.CSharp)]
    public void AStringThatIsNotOnePlainLiteralIsRefused(string verbatim, string language)
    {
        Assert.False(LiteralValueParser.Parse(verbatim, language).HasValue);
    }

    [Fact]
    public void AByteEscapeIsReadBecauseThatIsHowAProtocolByteIsWritten()
    {
        var value = LiteralValueParser.Parse("b'\\xA5'", LanguageNames.Python);

        Assert.Equal("\u00a5", value.Text);
    }

    [Fact]
    public void AStringTooLongToBeASharedConstantIsNotStored()
    {
        var long_ = "\"" + new string('x', 300) + "\"";

        Assert.False(LiteralValueParser.Parse(long_, LanguageNames.CSharp).HasValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingInMeansNothingOut(string? verbatim)
    {
        Assert.False(LiteralValueParser.Parse(verbatim, LanguageNames.C).HasValue);
    }

    [Theory]
    // The search box belongs to no language, so it takes the union of the notations.
    [InlineData("0xA5", 165)]
    [InlineData("165", 165)]
    [InlineData("0b1010", 10)]
    [InlineData("0o755", 493)]
    [InlineData("8'hA5", 165)]
    [InlineData("9_600", 9600)]
    [InlineData("100UL", 100)]
    [InlineData("-1", -1)]
    // Settled explicitly rather than inherited: a leading zero is decimal here, because
    // one of C and C# would otherwise be silently overruled.
    [InlineData("052", 52)]
    public void AQueryTakesTheUnionOfTheNotations(string typed, long expected)
    {
        Assert.Equal(expected, LiteralValueParser.ParseQuery(typed).Number);
    }

    [Fact]
    public void AQuotedQueryLooksForText()
    {
        Assert.Equal("COM3", LiteralValueParser.ParseQuery("\"COM3\"").Text);
    }

    [Fact]
    public void AQueryThatIsNotAValueFindsNothing()
    {
        // The prefix says "match by value"; if the rest is not one, saying so beats
        // quietly falling back to a name search the user did not ask for.
        Assert.False(LiteralValueParser.ParseQuery("uart_init").HasValue);
        Assert.False(LiteralValueParser.ParseQuery("0xZZ").HasValue);
    }
}
