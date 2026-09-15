using System.IO;
using System.Text;
using INTViewer.Models;

namespace INTViewer.Services;

public static class TextFileReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    static TextFileReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<TextFileContent>ReadAsync(string filePath)
    {
        filePath = Path.GetFullPath(filePath);

        if (!string.Equals(Path.GetExtension(filePath), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("현재는 .txt 파일만 열 수 있습니다.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(filePath);
        (Encoding encoding, int preambleLength, string displayName) = DetectEncoding(bytes);
        string text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);

        return new TextFileContent(filePath, text, displayName, bytes.LongLength);
    }

    private static (Encoding Encoding, int PreambleLength, string DisplayName) DetectEncoding(byte[] bytes)
    {
        if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
        {
            return (Encoding.UTF8, 3, "UTF-8 (BOM)");
        }

        if (StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00))
        {
            return (new UTF32Encoding(false, true), 4, "UTF-32 LE");
        }

        if (StartsWith(bytes, 0x00, 0x00, 0xFE, 0xFF))
        {
            return (new UTF32Encoding(true, true), 4, "UTF-32 BE");
        }

        if (StartsWith(bytes, 0xFF, 0xFE))
        {
            return (Encoding.Unicode, 2, "UTF-16 LE");
        }

        if (StartsWith(bytes, 0xFE, 0xFF))
        {
            return (Encoding.BigEndianUnicode, 2, "UTF-16 BE");
        }

        if (LooksLikeBomlessUtf16(bytes, littleEndian: true))
        {
            return (Encoding.Unicode, 0, "UTF-16 LE (추정)");
        }

        if (LooksLikeBomlessUtf16(bytes, littleEndian: false))
        {
            return (Encoding.BigEndianUnicode, 0, "UTF-16 BE (추정)");
        }

        try
        {
            _ = StrictUtf8.GetString(bytes);
            return (StrictUtf8, 0, "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            Encoding cp949 = Encoding.GetEncoding(
                949,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            return (cp949, 0, "CP949 (추정)");
        }
    }

    private static bool StartsWith(byte[] bytes, params byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeBomlessUtf16(byte[] bytes, bool littleEndian)
    {
        if (bytes.Length < 4)
        {
            return false;
        }

        int pairs = Math.Min(bytes.Length / 2, 2048);
        int expectedNulls = 0;
        int oppositeNulls = 0;

        for (int i = 0; i < pairs * 2; i += 2)
        {
            byte first = bytes[i];
            byte second = bytes[i + 1];

            if (littleEndian)
            {
                if (second == 0) expectedNulls++;
                if (first == 0) oppositeNulls++;
            }
            else
            {
                if (first == 0) expectedNulls++;
                if (second == 0) oppositeNulls++;
            }
        }

        return expectedNulls > pairs * 0.3 && oppositeNulls < pairs * 0.1;
    }
}
