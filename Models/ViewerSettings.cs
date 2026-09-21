namespace INTViewer.Models;

public sealed record ViewerSettings(
    string BackgroundColor,
    string TextColor,
    bool PageMode,
    double WindowWidth = 960,
    double WindowHeight = 680,
    double MarginTop = 28,
    double MarginBottom = 12,
    double MarginLeft = 32,
    double MarginRight = 32,
    double FontSize = 14,
    string FontFamily = "Malgun Gothic",
    bool Bold = false,
    bool Italic = false,
    double LineSpacing = 1.35,
    bool AutoOpenLastFile = false,
    bool SearchCaseSensitive = false,
    bool SearchWholeWord = false,
    int SettingsVersion = 5)
{
    public static ViewerSettings Default { get; } = new(
        "#FFFFFF", "#111827", false, 960, 680, 28, 12, 32, 32, 14,
        "Malgun Gothic", false, false, 1.35, false, false, false, 5);
}
