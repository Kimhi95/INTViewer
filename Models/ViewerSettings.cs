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
    int SettingsVersion = 3)
{
    public static ViewerSettings Default { get; } = new(
        "#FFFFFF", "#111827", false, 960, 680, 28, 12, 32, 32, 14,
        "Malgun Gothic", false, false, 3);
}
