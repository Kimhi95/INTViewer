using System.IO;
using System.Text.Json.Serialization;

namespace INTViewer.Models;

public sealed record RecentFileEntry(
    string FilePath,
    string EncodingName,
    long ByteLength,
    DateTimeOffset LastOpenedAt)
{
    [JsonIgnore]
    public string FileName => Path.GetFileName(FilePath);

    [JsonIgnore]
    public string Location => Path.GetDirectoryName(FilePath) ?? FilePath;

    [JsonIgnore]
    public string StatusText => File.Exists(FilePath) ? EncodingName : "파일을 찾을 수 없음";
}
