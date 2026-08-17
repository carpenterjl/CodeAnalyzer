namespace CodeAnalyzer.Core.Search;

/// <summary>
/// Subsequence scorer in the style of an editor's "go to symbol": every query character
/// must appear in order, and matches score higher when they land on word boundaries or
/// run consecutively. So <c>uwr</c> ranks <c>uart_write</c> above <c>outer_wrapper</c>.
/// <para>
/// A candidate is scanned twice. The first pass takes the leftmost occurrence of each
/// query character; the second may take only <em>structured</em> positions — the start of
/// the name, a word hump, or the character right after the previous match. The higher of
/// the two stands. The second pass exists because leftmost-first buries exactly the query
/// shape this matcher advertises: <c>WST</c> against <c>WorkspaceSettingsTests</c> lands
/// its <c>S</c> on the <c>s</c> inside <c>Workspace</c> and its <c>T</c> on the <c>t</c>
/// inside <c>Settings</c>, then pays the gap penalty for both, scoring 9 where reading the
/// three humps scores 35. Measured over 400 names' worth of whole-name, lower-case,
/// initials, hump-prefix, dropped-hump and prefix queries, the worst such query went from
/// 2.71 points per character to 10.29, while a junk population of 3,000 names borrowed
/// from another workspace did not move at all — the second pass can only find structure
/// that is there.
/// </para>
/// </summary>
public static class FuzzyMatcher
{
    private const int ScoreConsecutive = 12;
    private const int ScoreWordBoundary = 14;
    private const int ScoreStartOfName = 18;
    private const int ScoreExactCaseBonus = 2;
    private const int PenaltyPerGapChar = 1;
    private const int PenaltyMaxGap = 12;

    /// <summary>Candidates longer than this are truncated when scanning, to bound worst-case cost.</summary>
    private const int MaxScannedLength = 256;

    /// <summary>
    /// Points per query character below which a match is a coincidence rather than an
    /// answer, and says so. A score is not comparable across queries — a 22-character
    /// query scores an order of magnitude above a 4-character one for the same quality of
    /// match — so the bar scales with the query.
    /// <para>
    /// Ten is where the two populations sit apart. With the structured pass in place, no
    /// constructed positive on this workspace falls below 10.29 and the 1st percentile on
    /// a second workspace is 11.00; the junk that this exists to catch scores 2.33
    /// (<c>McpServer</c>) and 2.70 (<c>OptionSpec</c>), both taken from real transcripts.
    /// The band between is empty, so the exact value is not load-bearing — what matters is
    /// that it sits inside a gap somebody measured rather than at a number somebody liked.
    /// </para>
    /// </summary>
    public const int StrongScorePerCharacter = 10;

    /// <summary>
    /// The score <paramref name="query"/> must reach for a hit to be presented as an
    /// answer. Below it the letters merely appear in order.
    /// </summary>
    public static int StrongScoreFloor(string query) =>
        StrongScorePerCharacter * query.Length;

    /// <summary>
    /// Returns a score, or null when <paramref name="query"/> is not a subsequence of
    /// <paramref name="candidate"/>. Higher is better.
    /// </summary>
    public static int? Score(string query, string candidate)
    {
        var leftmost = Scan(query, candidate, structuredOnly: false);
        if (leftmost is null)
        {
            // Not a subsequence at all. The structured pass is a restriction of this one,
            // so it cannot match where this fails.
            return null;
        }

        var structured = Scan(query, candidate, structuredOnly: true);
        return structured > leftmost ? structured : leftmost;
    }

    /// <summary>
    /// One pass over the candidate. <paramref name="structuredOnly"/> is the whole
    /// difference between the two: when set, a character may be taken only where it starts
    /// the name, sits at a word hump, or continues the previous match, and an occurrence
    /// anywhere else is stepped over as if it did not match.
    /// </summary>
    private static int? Scan(string query, string candidate, bool structuredOnly)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (candidate.Length == 0 || query.Length > candidate.Length)
        {
            return null;
        }

        var scanLength = Math.Min(candidate.Length, MaxScannedLength);

        var score = 0;
        var queryIndex = 0;
        var previousMatchIndex = -1;

        for (var i = 0; i < scanLength && queryIndex < query.Length; i++)
        {
            var candidateChar = candidate[i];
            var queryChar = query[queryIndex];

            if (char.ToLowerInvariant(candidateChar) != char.ToLowerInvariant(queryChar))
            {
                continue;
            }

            int? placement =
                i == 0 ? ScoreStartOfName
                : previousMatchIndex == i - 1 ? ScoreConsecutive
                : IsWordBoundary(candidate, i) ? ScoreWordBoundary
                : null;

            if (placement is null && structuredOnly)
            {
                continue;
            }

            score += placement ?? 0;

            if (candidateChar == queryChar)
            {
                score += ScoreExactCaseBonus;
            }

            if (previousMatchIndex >= 0)
            {
                var gap = i - previousMatchIndex - 1;
                score -= Math.Min(gap, PenaltyMaxGap) * PenaltyPerGapChar;
            }

            previousMatchIndex = i;
            queryIndex++;
        }

        if (queryIndex < query.Length)
        {
            return null;
        }

        // Shorter names containing the same match are the better answer.
        score -= candidate.Length / 8;

        if (candidate.Length == query.Length)
        {
            score += ScoreStartOfName;
        }

        return score;
    }

    /// <summary>
    /// True at a camelCase hump, or after a separator. This is what makes <c>uw</c> find
    /// both <c>uart_write</c> and <c>uartWrite</c>.
    /// </summary>
    private static bool IsWordBoundary(string text, int index)
    {
        if (index <= 0)
        {
            return true;
        }

        var previous = text[index - 1];
        var current = text[index];

        if (previous is '_' or '-' or '.' or ':' or '/' or ' ')
        {
            return true;
        }

        return char.IsLower(previous) && char.IsUpper(current);
    }
}
