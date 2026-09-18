namespace INTViewer.Models;

public sealed record TextFileContent(
    string FilePath,
    string Text,
    string EncodingName,
    string EncodingKey,
    long ByteLength,
    DateTime LastWriteTimeUtc);
