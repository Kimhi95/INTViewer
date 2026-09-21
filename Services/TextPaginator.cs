using System.Windows.Media;

namespace INTViewer.Services;

public sealed record TextPage(int Start, int Length);

public static class TextPaginator
{
    public static List<TextPage> Paginate(string text, double width, double height, Typeface typeface,
        double fontSize, double lineHeight, CancellationToken token)
    {
        if (text.Length == 0) return [new TextPage(0, 0)];
        typeface.TryGetGlyphTypeface(out GlyphTypeface? glyphTypeface);
        var widthCache = new double[char.MaxValue + 1];
        // WPF can round the arranged text height up and its shaping/fallback can wrap one
        // line earlier than the fast glyph-width estimate. Reserve one complete line plus
        // a small device-pixel margin so the final line is never clipped by the page border.
        double usableHeight = Math.Max(1, height - 2);
        int linesPerPage = Math.Max(1, (int)Math.Floor(usableHeight / Math.Max(1, lineHeight)) - 1);
        var pages = new List<TextPage>();
        int pageStart = 0;
        int lineStart = 0;
        int lastWordBreak = -1;
        int linesOnPage = 1;
        double currentWidth = 0;

        for (int index = 0; index < text.Length; index++)
        {
            if ((index & 0x1FFF) == 0) token.ThrowIfCancellationRequested();
            char character = text[index];
            if (character == '\r') continue;
            if (character == '\n')
            {
                if (linesOnPage >= linesPerPage)
                {
                    AddPage(pages, pageStart, index + 1);
                    pageStart = index + 1;
                    linesOnPage = 1;
                }
                else linesOnPage++;
                lineStart = index + 1;
                lastWordBreak = -1;
                currentWidth = 0;
                continue;
            }

            double characterWidth = GetCharacterWidth(character, currentWidth, fontSize, glyphTypeface, widthCache);
            if (currentWidth > 0 && currentWidth + characterWidth > width)
            {
                int wrapAt = lastWordBreak >= lineStart ? lastWordBreak + 1 : index;
                if (linesOnPage >= linesPerPage)
                {
                    AddPage(pages, pageStart, wrapAt);
                    pageStart = wrapAt;
                    linesOnPage = 1;
                }
                else linesOnPage++;
                lineStart = wrapAt;
                lastWordBreak = -1;
                currentWidth = MeasureRange(text, wrapAt, index, fontSize, glyphTypeface, widthCache);
                characterWidth = GetCharacterWidth(character, currentWidth, fontSize, glyphTypeface, widthCache);
            }

            currentWidth += characterWidth;
            if (char.IsWhiteSpace(character)) lastWordBreak = index;
        }

        if (pageStart < text.Length) AddPage(pages, pageStart, text.Length);
        return pages.Count == 0 ? [new TextPage(0, text.Length)] : pages;
    }

    private static double MeasureRange(string text, int start, int end, double fontSize, GlyphTypeface? glyphTypeface, double[] widthCache)
    {
        double width = 0;
        for (int index = start; index < end; index++)
            width += GetCharacterWidth(text[index], width, fontSize, glyphTypeface, widthCache);
        return width;
    }

    private static double GetCharacterWidth(char character, double currentWidth, double fontSize, GlyphTypeface? glyphTypeface, double[] widthCache)
    {
        if (character == '\t')
        {
            double tabWidth = fontSize * 2.4;
            return tabWidth - currentWidth % tabWidth;
        }
        if (widthCache[character] > 0) return widthCache[character];
        if (glyphTypeface is not null && glyphTypeface.CharacterToGlyphMap.TryGetValue(character, out ushort glyphIndex) &&
            glyphTypeface.AdvanceWidths.TryGetValue(glyphIndex, out double advance))
            return widthCache[character] = Math.Max(fontSize * 0.2, advance * fontSize);
        return widthCache[character] = character >= 0x2E80 ? fontSize : fontSize * 0.56;
    }

    private static void AddPage(List<TextPage> pages, int start, int end)
    {
        int length = Math.Max(0, end - start);
        if (length > 0) pages.Add(new TextPage(start, length));
    }
}
