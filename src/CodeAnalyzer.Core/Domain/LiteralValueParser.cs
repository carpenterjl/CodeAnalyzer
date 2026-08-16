using System.Globalization;
using System.Text;

namespace CodeAnalyzer.Core.Domain;

/// <summary>
/// The value a literal denotes, where the literal's own grammar says so certainly.
/// Both members null means this slice is not a literal this parser can certify.
/// </summary>
/// <param name="Number">The integer the literal denotes, or null.</param>
/// <param name="Text">The characters a string literal denotes, or null.</param>
public readonly record struct LiteralValue(long? Number, string? Text)
{
    public static LiteralValue None => default;

    public bool HasValue => Number is not null || Text is not null;
}

/// <summary>
/// Reads the value out of a verbatim literal slice, per the source language's own grammar.
/// <para>
/// The stored <see cref="SymbolRecord.Value"/> stays the displayed fact — this only answers
/// "what number does <c>0xA5</c> denote", which is the language's rule about its own
/// notation, not an interpretation of the program. Where the grammar does not settle it,
/// the answer is <see cref="LiteralValue.None"/> and the symbol simply does not participate
/// in value matching. The <c>.scm</c> packs capture arbitrary initializer expressions, so
/// <b>failing strictly is the correctness mechanism</b>, not a limitation to work around.
/// </para>
/// <para>
/// Deliberately excluded: floating-point literals (cross-language float equality is a claim
/// about representation, not about notation) and character literals (treating <c>'A'</c> as
/// 65 asserts an encoding the source never states).
/// </para>
/// </summary>
public static class LiteralValueParser
{
    /// <summary>
    /// Longest string literal worth storing for matching. A slice this long is prose or a
    /// generated blob, not a shared constant, and the column is written for every symbol.
    /// </summary>
    private const int MaxTextLength = 200;

    /// <summary>
    /// Longest slice worth examining at all. Nothing this parser can certify comes close:
    /// the widest integer notation is a 64-digit binary literal, and a string past
    /// <see cref="MaxTextLength"/> is refused anyway.
    /// </summary>
    private const int MaxSliceLength = 512;

    /// <summary>
    /// Which notations a dialect actually has. The table is the point: <c>052</c> is 42 in
    /// C and 52 in C#, so every rule that differs between languages is a field here rather
    /// than a branch buried in the parsing code.
    /// </summary>
    private readonly record struct Notation
    {
        /// <summary>Digit separator, or '\0' where the language has none.</summary>
        public char Separator { get; init; }

        /// <summary>A leading zero introduces an octal integer (C and C++ only).</summary>
        public bool LeadingZeroIsOctal { get; init; }

        /// <summary>A leading zero on a non-zero decimal is a syntax error (Python 3).</summary>
        public bool LeadingZeroIsInvalid { get; init; }

        /// <summary><c>0o755</c>.</summary>
        public bool OctalPrefix { get; init; }

        /// <summary>Trailing <c>u</c>/<c>l</c>/<c>z</c> size and sign suffixes.</summary>
        public bool IntegerSuffix { get; init; }

        /// <summary>Verilog sized literals: <c>8'hA5</c>.</summary>
        public bool SizedLiterals { get; init; }

        /// <summary>Single quotes delimit a string rather than a character.</summary>
        public bool SingleQuotedStrings { get; init; }

        /// <summary>C# verbatim strings: <c>@"C:\dev"</c>.</summary>
        public bool VerbatimStrings { get; init; }

        /// <summary>Python string prefixes: <c>r"…"</c>, <c>b'…'</c>, <c>u"…"</c>.</summary>
        public bool StringPrefixes { get; init; }
    }

    private static readonly Notation CNotation = new()
    {
        LeadingZeroIsOctal = true,
        IntegerSuffix = true,
    };

    private static readonly Notation CppNotation = CNotation with { Separator = '\'' };

    private static readonly Notation CSharpNotation = new()
    {
        Separator = '_',
        IntegerSuffix = true,
        VerbatimStrings = true,
    };

    private static readonly Notation PythonNotation = new()
    {
        Separator = '_',
        LeadingZeroIsInvalid = true,
        OctalPrefix = true,
        SingleQuotedStrings = true,
        StringPrefixes = true,
    };

    private static readonly Notation VerilogNotation = new()
    {
        Separator = '_',
        SizedLiterals = true,
    };

    /// <summary>
    /// What the search box accepts. It is not a source file, so it takes the union of the
    /// notations above with the two places they contradict each other settled explicitly:
    /// a leading zero means decimal (write <c>0o</c> for octal), and a single quote is the
    /// Verilog base marker rather than a string delimiter (write <c>"…"</c> for text).
    /// </summary>
    private static readonly Notation QueryNotation = new()
    {
        Separator = '_',
        OctalPrefix = true,
        IntegerSuffix = true,
        SizedLiterals = true,
    };

    /// <summary>
    /// Reads <paramref name="verbatim"/> as a literal of <paramref name="language"/>.
    /// The language matters: <c>052</c> is 42 in C and 52 in C#, so a parser that ignored
    /// it would state one of those as fact while the source meant the other.
    /// </summary>
    public static LiteralValue Parse(string? verbatim, string language) =>
        Parse(verbatim, NotationFor(language));

    /// <summary>
    /// Reads a value typed into the search box, which belongs to no language.
    /// See <see cref="QueryNotation"/> for the two rules that had to be chosen rather than
    /// inherited.
    /// </summary>
    public static LiteralValue ParseQuery(string? text) => Parse(text, QueryNotation);

    private static Notation NotationFor(string language) => language switch
    {
        LanguageNames.C => CNotation,
        LanguageNames.Cpp => CppNotation,
        LanguageNames.CSharp => CSharpNotation,
        LanguageNames.Python => PythonNotation,
        LanguageNames.Verilog => VerilogNotation,
        // HTML, and anything added later without a rule of its own: plain decimals and
        // plain double-quoted strings, which every notation here agrees about.
        _ => default,
    };

    private static LiteralValue Parse(string? verbatim, Notation notation)
    {
        // Runs for every symbol of every indexed file, so a slice far past any literal
        // worth matching is dismissed before any of the work below.
        if (string.IsNullOrWhiteSpace(verbatim) || verbatim.Length > MaxSliceLength)
        {
            return LiteralValue.None;
        }

        var text = verbatim.Trim();

        // A C macro is habitually written `(0xA5)`, and a sign can sit either side of the
        // brackets: `-(1)` and `(-1)` are the same literal wearing different clothes.
        var negative = false;
        while (true)
        {
            var unwrapped = StripOuterParentheses(text);
            if (!ReferenceEquals(unwrapped, text))
            {
                text = unwrapped;
                continue;
            }

            if (text.Length > 1 && (text[0] == '-' || text[0] == '+'))
            {
                negative ^= text[0] == '-';
                text = text[1..].TrimStart();
                continue;
            }

            break;
        }

        if (text.Length == 0)
        {
            return LiteralValue.None;
        }

        var stringValue = ParseString(text, notation);
        if (stringValue is not null)
        {
            // A signed string is not a literal at all — something else was captured.
            return negative ? LiteralValue.None : new LiteralValue(null, stringValue);
        }

        var number = ParseNumber(text, notation);
        if (number is null)
        {
            return LiteralValue.None;
        }

        // long.MinValue is unreachable this way: the magnitude would have to exceed
        // long.MaxValue, which ParseDigits refuses.
        return new LiteralValue(negative ? -number.Value : number.Value, null);
    }

    /// <summary>
    /// Removes one layer of brackets when the first genuinely closes at the last. Returns
    /// the same instance when it does not, so the caller can test with ReferenceEquals:
    /// <c>(a) + (b)</c> must not become <c>a) + (b</c>.
    /// </summary>
    private static string StripOuterParentheses(string text)
    {
        if (text.Length < 2 || text[0] != '(' || text[^1] != ')')
        {
            return text;
        }

        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i == text.Length - 1 ? text[1..^1].Trim() : text;
                }
            }
        }

        return text;
    }

    private static long? ParseNumber(string text, Notation notation)
    {
        if (notation.SizedLiterals && text.Contains('\''))
        {
            return ParseVerilogSized(text, notation);
        }

        var digits = text;
        var radix = 10;

        if (digits.Length > 2 && digits[0] == '0')
        {
            switch (digits[1])
            {
                case 'x' or 'X':
                    radix = 16;
                    digits = digits[2..];
                    break;
                case 'b' or 'B':
                    radix = 2;
                    digits = digits[2..];
                    break;
                case 'o' or 'O' when notation.OctalPrefix:
                    radix = 8;
                    digits = digits[2..];
                    break;
            }
        }

        digits = StripIntegerSuffix(digits, notation, radix);
        if (digits is null)
        {
            return null;
        }

        digits = RemoveSeparators(digits, notation);
        if (digits is null || digits.Length == 0)
        {
            return null;
        }

        if (radix == 10 && digits.Length > 1 && digits[0] == '0')
        {
            // The one place where the same characters mean different numbers in different
            // languages, so it is decided by the notation table rather than by convention.
            if (notation.LeadingZeroIsOctal)
            {
                radix = 8;
                digits = digits.TrimStart('0');
                if (digits.Length == 0)
                {
                    return 0;
                }
            }
            else if (notation.LeadingZeroIsInvalid && digits.Trim('0').Length > 0)
            {
                return null;
            }
        }

        return ParseDigits(digits, radix);
    }

    /// <summary>
    /// Reads a Verilog sized literal: <c>8'hA5</c>, <c>'hA5</c>, <c>4'b1010</c>, <c>16'd80</c>.
    /// The signed flag is read and ignored — a two's-complement reinterpretation of the bits
    /// is a claim about width semantics, not about the notation, so only the base value is
    /// stated. <c>x</c> and <c>z</c> digits mean the value is unknown, which is a refusal.
    /// </summary>
    private static long? ParseVerilogSized(string text, Notation notation)
    {
        var quote = text.IndexOf('\'');
        var sizeText = RemoveSeparators(text[..quote].Trim(), notation);
        if (sizeText is null)
        {
            return null;
        }

        int? width = null;
        if (sizeText.Length > 0)
        {
            if (!int.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedWidth)
                || parsedWidth <= 0)
            {
                return null;
            }

            width = parsedWidth;
        }

        var rest = text[(quote + 1)..].TrimStart();
        if (rest.Length > 0 && (rest[0] == 's' || rest[0] == 'S'))
        {
            rest = rest[1..];
        }

        if (rest.Length < 2)
        {
            // `'0` / `'1` fill literals take their width from context rather than stating
            // one, and a bare base with no digits is not a value.
            return null;
        }

        var radix = rest[0] switch
        {
            'h' or 'H' => 16,
            'd' or 'D' => 10,
            'o' or 'O' => 8,
            'b' or 'B' => 2,
            _ => 0,
        };

        if (radix == 0)
        {
            return null;
        }

        var digits = RemoveSeparators(rest[1..].Trim(), notation);
        if (digits is null || digits.Length == 0)
        {
            return null;
        }

        var value = ParseDigits(digits, radix);
        if (value is null)
        {
            return null;
        }

        // A literal wider than its declared width is truncated by the language. Applying
        // that truncation here would state a value the notation alone does not give, so
        // the slice is refused instead.
        if (width is not { } bits || bits >= 63)
        {
            return value;
        }

        return value.Value < (1L << bits) ? value : null;
    }

    private static long? ParseDigits(string digits, int radix)
    {
        long value = 0;

        foreach (var c in digits)
        {
            var digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };

            if (digit < 0 || digit >= radix)
            {
                return null;
            }

            // Anything past Int64 has no stored form, so it is refused rather than wrapped.
            if (value > (long.MaxValue - digit) / radix)
            {
                return null;
            }

            value = value * radix + digit;
        }

        return value;
    }

    /// <summary>
    /// Removes the integer size/sign suffix the language allows. Float suffixes are not in
    /// the set, so <c>1.5f</c> and <c>1m</c> fall through and fail, which is intended.
    /// </summary>
    private static string? StripIntegerSuffix(string digits, Notation notation, int radix)
    {
        if (!notation.IntegerSuffix)
        {
            return digits;
        }

        var end = digits.Length;
        while (end > 0)
        {
            var c = digits[end - 1];

            // In hexadecimal these letters are digits, not suffixes: 0xFUL ends in a
            // suffix, but 0xF does not.
            var isSuffix = c is 'u' or 'U' or 'l' or 'L' or 'z' or 'Z';
            if (radix == 16 && Uri.IsHexDigit(c))
            {
                isSuffix = false;
            }

            if (!isSuffix)
            {
                break;
            }

            end--;
        }

        return end == 0 ? null : digits[..end];
    }

    /// <summary>
    /// Drops the digit separator. A separator that is not between two digits is invalid in
    /// every language that has one, so it fails the whole slice.
    /// </summary>
    private static string? RemoveSeparators(string digits, Notation notation)
    {
        var separator = notation.Separator;

        if (separator == '\0' || !digits.Contains(separator))
        {
            return digits;
        }

        var builder = new StringBuilder(digits.Length);

        for (var i = 0; i < digits.Length; i++)
        {
            if (digits[i] != separator)
            {
                builder.Append(digits[i]);
                continue;
            }

            if (i == 0 || i == digits.Length - 1 || digits[i - 1] == separator)
            {
                return null;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads a single plain quoted string. Interpolation, concatenation and anything with
    /// an escape this parser does not know all fail: a partially decoded string presented
    /// as the literal's value would be worse than no answer.
    /// </summary>
    private static string? ParseString(string text, Notation notation)
    {
        var index = 0;
        var raw = false;

        // Prefixes that change how the quotes are read but not what characters result.
        // An interpolated string is not a literal and is left to fail.
        if (notation.VerbatimStrings && text[0] == '@')
        {
            raw = true;
            index = 1;
        }
        else if (notation.StringPrefixes)
        {
            while (index < text.Length && text[index] is 'r' or 'R' or 'b' or 'B' or 'u' or 'U')
            {
                raw |= text[index] is 'r' or 'R';
                index++;
            }

            if (index > 2)
            {
                return null;
            }
        }

        if (index >= text.Length)
        {
            return null;
        }

        var quote = text[index];

        // Single quotes delimit a character literal everywhere except Python, and a
        // character is deliberately not a value this parser states.
        if (quote != '"' && !(quote == '\'' && notation.SingleQuotedStrings))
        {
            return null;
        }

        var body = text[(index + 1)..];
        if (body.Length == 0 || body[^1] != quote)
        {
            return null;
        }

        body = body[..^1];

        if (raw)
        {
            // A C# verbatim string writes a quote as "" and takes everything else as-is.
            if (notation.VerbatimStrings)
            {
                var parts = body.Split("\"\"");
                return parts.Any(part => part.Contains('"')) ? null : Capped(string.Join("\"", parts));
            }

            return body.Contains(quote) ? null : Capped(body);
        }

        var builder = new StringBuilder(body.Length);

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];

            if (c == quote)
            {
                // Two literals side by side, or a stray terminator: not one string.
                return null;
            }

            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            if (i + 1 >= body.Length)
            {
                return null;
            }

            var escape = body[++i];
            switch (escape)
            {
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case '0': builder.Append('\0'); break;
                case '\\': builder.Append('\\'); break;
                case '\'': builder.Append('\''); break;
                case '"': builder.Append('"'); break;
                case 'x' when TryReadHexEscape(body, ref i, out var hex): builder.Append(hex); break;
                default: return null;
            }
        }

        return Capped(builder.ToString());
    }

    /// <summary>Reads <c>\xNN</c>, the escape an embedded protocol byte is usually written as.</summary>
    private static bool TryReadHexEscape(string body, ref int index, out char value)
    {
        value = default;

        var start = index + 1;
        var end = start;
        while (end < body.Length && end - start < 2 && Uri.IsHexDigit(body[end]))
        {
            end++;
        }

        if (end == start)
        {
            return false;
        }

        value = (char)Convert.ToInt32(body[start..end], 16);
        index = end - 1;
        return true;
    }

    private static string? Capped(string text) => text.Length > MaxTextLength ? null : text;
}
