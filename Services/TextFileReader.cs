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

    public static IReadOnlyList<KeyValuePair<string, string>> SupportedEncodings { get; } =
    [
        new("auto", "자동 감지"), new("utf-8", "UTF-8"), new("utf-8-bom", "UTF-8 (BOM)"),
        new("utf-16-le", "UTF-16 LE"), new("utf-16-be", "UTF-16 BE"), new("utf-32-le", "UTF-32 LE"),
        new("utf-32-be", "UTF-32 BE"), new("cp949", "CP949")
    ];

    public static async Task<TextFileContent>ReadAsync(string filePath, string encodingKey = "auto")
    {
        filePath = Path.GetFullPath(filePath);

        if (!string.Equals(Path.GetExtension(filePath), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("현재는 .txt 파일만 열 수 있습니다.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(filePath);
        (Encoding encoding, int preambleLength, string displayName, string detectedKey) = encodingKey == "auto"
            ? DetectEncoding(bytes)
            : GetRequestedEncoding(encodingKey, bytes);
        string text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);

        return new TextFileContent(filePath, text, displayName, detectedKey, bytes.LongLength, File.GetLastWriteTimeUtc(filePath));
    }

    private static (Encoding Encoding, int PreambleLength, string DisplayName, string Key) DetectEncoding(byte[] bytes)
    {
        if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
        {
            return (Encoding.UTF8, 3, "UTF-8 (BOM)", "utf-8-bom");
        }

        if (StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00))
        {
            return (new UTF32Encoding(false, true), 4, "UTF-32 LE", "utf-32-le");
        }

        if (StartsWith(bytes, 0x00, 0x00, 0xFE, 0xFF))
        {
            return (new UTF32Encoding(true, true), 4, "UTF-32 BE", "utf-32-be");
        }

        if (StartsWith(bytes, 0xFF, 0xFE))
        {
            return (Encoding.Unicode, 2, "UTF-16 LE", "utf-16-le");
        }

        if (StartsWith(bytes, 0xFE, 0xFF))
        {
            return (Encoding.BigEndianUnicode, 2, "UTF-16 BE", "utf-16-be");
        }

        if (LooksLikeBomlessUtf16(bytes, littleEndian: true))
        {
            return (Encoding.Unicode, 0, "UTF-16 LE (추정)", "utf-16-le");
        }

        if (LooksLikeBomlessUtf16(bytes, littleEndian: false))
        {
            return (Encoding.BigEndianUnicode, 0, "UTF-16 BE (추정)", "utf-16-be");
        }

        try
        {
            _ = StrictUtf8.GetString(bytes);
            return (StrictUtf8, 0, "UTF-8", "utf-8");
        }
        catch (DecoderFallbackException)
        {
            Encoding cp949 = Encoding.GetEncoding(
                949,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            return (cp949, 0, "CP949 (추정)", "cp949");
        }
    }

    private static (Encoding Encoding, int PreambleLength, string DisplayName, string Key) GetRequestedEncoding(string key, byte[] bytes)
    {
        return key switch
        {
            "utf-8" => (StrictUtf8, StartsWith(bytes, 0xEF, 0xBB, 0xBF) ? 3 : 0, "UTF-8", key),
            "utf-8-bom" => (Encoding.UTF8, StartsWith(bytes, 0xEF, 0xBB, 0xBF) ? 3 : 0, "UTF-8 (BOM)", key),
            "utf-16-le" => (Encoding.Unicode, StartsWith(bytes, 0xFF, 0xFE) ? 2 : 0, "UTF-16 LE", key),
            "utf-16-be" => (Encoding.BigEndianUnicode, StartsWith(bytes, 0xFE, 0xFF) ? 2 : 0, "UTF-16 BE", key),
            "utf-32-le" => (new UTF32Encoding(false, true), StartsWith(bytes, 0xFF, 0xFE, 0, 0) ? 4 : 0, "UTF-32 LE", key),
            "utf-32-be" => (new UTF32Encoding(true, true), StartsWith(bytes, 0, 0, 0xFE, 0xFF) ? 4 : 0, "UTF-32 BE", key),
            "cp949" => (Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), 0, "CP949", key),
            _ => throw new NotSupportedException("지원하지 않는 인코딩입니다.")
        };
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
