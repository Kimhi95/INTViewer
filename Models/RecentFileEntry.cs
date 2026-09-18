using System.IO;
using System.Text.Json.Serialization;

namespace INTViewer.Models;

public sealed record RecentFileEntry(
    string FilePath,
    string EncodingName,
    long ByteLength,
    DateTimeOffset LastOpenedAt,
    double ReadProgress = 0,
    int ReadPosition = 0,
    DateTime LastWriteTimeUtc = default,
    bool IsPinned = false,
    int[]? BookmarkPositions = null)
{
    [JsonIgnore]
    public string FileName => Path.GetFileName(FilePath);

    [JsonIgnore]
    public string Location => Path.GetDirectoryName(FilePath) ?? FilePath;

    [JsonIgnore]
    public string StatusText => !File.Exists(FilePath)
        ? "파일을 찾을 수 없음"
        : HasChanged ? $"{EncodingName} · 파일 변경됨" : EncodingName;

    [JsonIgnore]
    public bool HasChanged
    {
        get
        {
            try
            {
                return File.Exists(FilePath) && LastWriteTimeUtc != default &&
                       Math.Abs((File.GetLastWriteTimeUtc(FilePath) - LastWriteTimeUtc).TotalSeconds) > 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    [JsonIgnore]
    public string PinText => IsPinned ? "★" : string.Empty;

    [JsonIgnore]
    public string BookmarkText => BookmarkPositions is { Length: > 0 } ? $"책갈피 {BookmarkPositions.Length}" : string.Empty;

    [JsonIgnore]
    public string ProgressText => $"{Math.Clamp(ReadProgress, 0, 100):0}% 읽음";
}
