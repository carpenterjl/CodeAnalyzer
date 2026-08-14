namespace CodeAnalyzer.Core.Search;

/// <summary>
/// Subsequence scorer in the style of an editor's "go to symbol": every query character
/// must appear in order, and matches score higher when they land on word boundaries or
/// run consecutively. So <c>uwr</c> ranks <c>uart_write</c> above <c>outer_wrapper</c>.
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
    /// Returns a score, or null when <paramref name="query"/> is not a subsequence of
    /// <paramref name="candidate"/>. Higher is better.
    /// </summary>
    public static int? Score(string query, string candidate)
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

            if (i == 0)
            {
                score += ScoreStartOfName;
            }
            else if (previousMatchIndex == i - 1)
            {
                score += ScoreConsecutive;
            }
            else if (IsWordBoundary(candidate, i))
            {
                score += ScoreWordBoundary;
            }

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
