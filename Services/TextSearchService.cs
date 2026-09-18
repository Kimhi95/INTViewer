namespace INTViewer.Services;

public static class TextSearchService
{
    public static List<int> FindAll(string text, string query, bool caseSensitive, bool wholeWord)
    {
        var matches = new List<int>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return matches;
        StringComparison comparison = caseSensitive ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
        for (int start = 0; start <= text.Length - query.Length;)
        {
            int match = text.IndexOf(query, start, comparison);
            if (match < 0) break;
            if (!wholeWord || IsWholeWord(text, match, query.Length)) matches.Add(match);
            start = match + Math.Max(1, query.Length);
        }
        return matches;
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
