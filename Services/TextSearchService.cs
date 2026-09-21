namespace INTViewer.Services;

public sealed record TextSearchResult(List<int> Matches, bool IsTruncated);

public static class TextSearchService
{
    public static List<int> FindAll(string text, string query, bool caseSensitive, bool wholeWord)
        => FindLimited(text, query, caseSensitive, wholeWord, int.MaxValue).Matches;

    public static TextSearchResult FindLimited(string text, string query, bool caseSensitive, bool wholeWord,
        int maximumResults)
    {
        var matches = new List<int>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query) || maximumResults <= 0)
            return new TextSearchResult(matches, false);
        StringComparison comparison = caseSensitive ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
        for (int start = 0; start <= text.Length - query.Length;)
        {
            int match = text.IndexOf(query, start, comparison);
            if (match < 0) break;
            if (!wholeWord || IsWholeWord(text, match, query.Length))
            {
                if (matches.Count >= maximumResults) return new TextSearchResult(matches, true);
                matches.Add(match);
            }
            start = match + Math.Max(1, query.Length);
        }
        return new TextSearchResult(matches, false);
    }

    private static bool IsWholeWord(string text, int start, int length)
    {
        bool left = start == 0 || !IsWordCharacter(text[start - 1]);
        int end = start + length;
        bool right = end >= text.Length || !IsWordCharacter(text[end]);
        return left && right;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}
