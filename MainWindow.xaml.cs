using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using INTViewer.Models;
using INTViewer.Services;
using Microsoft.Win32;
using DrawingColor = System.Drawing.Color;
using DataFormats = System.Windows.DataFormats;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using IDataObject = System.Windows.IDataObject;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaFontFamily = System.Windows.Media.FontFamily;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Size = System.Windows.Size;
using WinForms = System.Windows.Forms;

namespace INTViewer;

public partial class MainWindow : Window
{
    private sealed record PageSlice(int Start, int Length, string Text);

    private readonly ObservableCollection<RecentFileEntry> _recentFiles = [];
    private readonly List<PageSlice> _pages = [];
    private readonly List<int> _lineStarts = [];
    private readonly Dictionary<string, MediaFontFamily> _readerFonts = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly DispatcherTimer _repaginationTimer;
    private readonly DispatcherTimer _windowSizeSaveTimer;
    private ViewerSettings _viewerSettings = ViewerSettings.Default;
    private string _currentText = string.Empty;
    private string _lastSearchQuery = string.Empty;
    private int _searchIndex = -1;
    private int _currentPageIndex;
    private bool _isInitializing = true;
    private CancellationTokenSource? _paginationCancellation;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

    public MainWindow()
    {
        InitializeComponent();
        ViewerView.Children.Remove(SettingsPanel);
        RootGrid.Children.Add(SettingsPanel);
        RecentFilesList.ItemsSource = _recentFiles;
        LoadReaderFonts();
        ContentTextBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ContentTextBox_ScrollChanged));
        Loaded += MainWindow_Loaded;
        SourceInitialized += (_, _) => ApplyTitleBarTheme(BrushFromHex(_viewerSettings.BackgroundColor, Colors.White).Color);

        _repaginationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _repaginationTimer.Tick += (_, _) =>
        {
            _repaginationTimer.Stop();
            if (IsPageMode) StartPagination();
        };

        _windowSizeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _windowSizeSaveTimer.Tick += async (_, _) =>
        {
            _windowSizeSaveTimer.Stop();
            CaptureWindowSize();
            await SaveViewerSettingsAsync();
        };
        Closing += MainWindow_Closing;
    }

    private bool IsPageMode => PageModeToggle.IsChecked == true;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SetRecentFiles(await HistoryStore.LoadAsync());
        _viewerSettings = await ViewerSettingsStore.LoadAsync();
        if (_viewerSettings.FontSize <= 0)
        {
            _viewerSettings = _viewerSettings with
            {
                MarginTop = ViewerSettings.Default.MarginTop,
                MarginBottom = ViewerSettings.Default.MarginBottom,
                MarginLeft = ViewerSettings.Default.MarginLeft,
                MarginRight = ViewerSettings.Default.MarginRight,
                FontSize = ViewerSettings.Default.FontSize
            };
        }
        if (_viewerSettings.SettingsVersion < 2)
        {
            _viewerSettings = _viewerSettings with { MarginBottom = 12, SettingsVersion = 2 };
        }
        if (_viewerSettings.SettingsVersion < 3 || string.IsNullOrWhiteSpace(_viewerSettings.FontFamily))
        {
            _viewerSettings = _viewerSettings with
            {
                MarginBottom = 12,
                FontFamily = ViewerSettings.Default.FontFamily,
                Bold = false,
                Italic = false,
                SettingsVersion = 3
            };
        }
        if (_readerFonts.Count > 0 && !_readerFonts.ContainsKey(_viewerSettings.FontFamily))
        {
            _viewerSettings = _viewerSettings with { FontFamily = _readerFonts.Keys.First() };
        }
        RestoreWindowSize();
        ApplyViewerSettings();
        PageModeToggle.IsChecked = _viewerSettings.PageMode;
        _isInitializing = false;
        UpdateViewMode();

        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) await OpenFileAsync(args[1]);
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "텍스트 파일 열기",
            Filter = "텍스트 파일 (*.txt)|*.txt",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) await OpenFileAsync(dialog.FileName);
    }

    private async void OpenSelected_Click(object sender, RoutedEventArgs e) => await OpenSelectedFileAsync();

    private async void RecentFileItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: RecentFileEntry entry }) return;
        RecentFilesList.SelectedItem = entry;
        e.Handled = true;
        await OpenSelectedFileAsync();
    }

    private void RecentFilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool hasSelection = RecentFilesList.SelectedItem is RecentFileEntry;
        OpenSelectedButton.IsEnabled = hasSelection;
        RemoveSelectedButton.IsEnabled = hasSelection;
    }

    private async Task OpenSelectedFileAsync()
    {
        if (RecentFilesList.SelectedItem is not RecentFileEntry selected) return;

        if (!File.Exists(selected.FilePath))
        {
            MessageBoxResult result = MessageBox.Show(this,
                "원래 위치에서 파일을 찾을 수 없습니다. 최근 목록에서 제거할까요?",
                "파일을 찾을 수 없음", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes) await RemoveHistoryEntryAsync(selected);
            return;
        }

        await OpenFileAsync(selected.FilePath);
    }

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (RecentFilesList.SelectedItem is RecentFileEntry selected) await RemoveHistoryEntryAsync(selected);
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_recentFiles.Count == 0) return;
        MessageBoxResult result = MessageBox.Show(this,
            "최근 파일 기록을 모두 지울까요? 원본 텍스트 파일은 삭제되지 않습니다.",
            "최근 기록 지우기", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            await HistoryStore.ClearAsync();
            SetRecentFiles([]);
        }
        catch (IOException ex) { ShowHistoryError(ex); }
        catch (UnauthorizedAccessException ex) { ShowHistoryError(ex); }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsPanel.Visibility == Visibility.Visible)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        if (ViewerView.Visibility != Visibility.Visible) return;

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            SearchPanel.Visibility = Visibility.Visible;
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (SearchPanel.Visibility == Visibility.Visible)
            {
                SearchPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowLibrary();
            }
            e.Handled = true;
            return;
        }

        if (IsPageMode && SettingsPanel.Visibility != Visibility.Visible && SearchPanel.Visibility != Visibility.Visible)
        {
            if (e.Key is Key.Right or Key.Down or Key.PageDown or Key.Space)
            {
                ShowPage(_currentPageIndex + 1);
                e.Handled = true;
            }
            else if (e.Key is Key.Left or Key.Up or Key.PageUp)
            {
                ShowPage(_currentPageIndex - 1);
                e.Handled = true;
            }
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsPageMode || ViewerView.Visibility != Visibility.Visible ||
            SettingsPanel.Visibility == Visibility.Visible || SearchPanel.Visibility == Visibility.Visible) return;
        ShowPage(_currentPageIndex + (e.Delta < 0 ? 1 : -1));
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasSingleTextFile(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!HasSingleTextFile(e.Data)) return;
        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        await OpenFileAsync(files[0]);
    }

    private static bool HasSingleTextFile(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        string[]? files = data.GetData(DataFormats.FileDrop) as string[];
        return files is { Length: 1 }
            && string.Equals(Path.GetExtension(files[0]), ".txt", StringComparison.OrdinalIgnoreCase);
    }

    private async Task OpenFileAsync(string filePath)
    {
        try
        {
            var content = await TextFileReader.ReadAsync(filePath);
            _currentText = content.Text;
            BuildLineStarts();
            _searchIndex = -1;
            _lastSearchQuery = string.Empty;
            if (IsPageMode)
            {
                ContentTextBox.Clear();
            }
            else
            {
                ContentTextBox.Text = content.Text;
                ContentTextBox.ScrollToHome();
            }
            Title = "INTViewer";
            ShowViewer();
            UpdateCurrentPosition();

            var entry = new RecentFileEntry(content.FilePath, content.EncodingName,
                content.ByteLength, DateTimeOffset.Now);
            try { SetRecentFiles(await HistoryStore.AddOrUpdateAsync(entry)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or NotSupportedException)
        {
            MessageBox.Show(this, ex.Message, "파일 열기 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = Visibility.Collapsed;

    private void BackToLibrary_Click(object sender, RoutedEventArgs e) => ShowLibrary();

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = SearchPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (SearchPanel.Visibility == Visibility.Visible) SearchTextBox.Focus();
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e) => SearchPanel.Visibility = Visibility.Collapsed;

    private async void WhiteMode_Click(object sender, RoutedEventArgs e)
    {
        _viewerSettings = _viewerSettings with { BackgroundColor = "#FFFFFF", TextColor = "#111827" };
        ApplyViewerSettings();
        await SaveViewerSettingsAsync();
    }

    private async void DarkMode_Click(object sender, RoutedEventArgs e)
    {
        _viewerSettings = _viewerSettings with { BackgroundColor = "#111827", TextColor = "#E5E7EB" };
        ApplyViewerSettings();
        await SaveViewerSettingsAsync();
    }

    private async void ChooseBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        string? selected = ChooseColor(_viewerSettings.BackgroundColor);
        if (selected is null) return;
        _viewerSettings = _viewerSettings with { BackgroundColor = selected };
        ApplyViewerSettings();
        await SaveViewerSettingsAsync();
    }

    private async void ChooseTextColor_Click(object sender, RoutedEventArgs e)
    {
        string? selected = ChooseColor(_viewerSettings.TextColor);
        if (selected is null) return;
        _viewerSettings = _viewerSettings with { TextColor = selected };
        ApplyViewerSettings();
        await SaveViewerSettingsAsync();
    }

    private string? ChooseColor(string currentColor)
    {
        SolidColorBrush currentBrush = BrushFromHex(currentColor, Colors.White);
        using var dialog = new WinForms.ColorDialog
        {
            FullOpen = true,
            Color = DrawingColor.FromArgb(currentBrush.Color.R, currentBrush.Color.G, currentBrush.Color.B)
        };
        return dialog.ShowDialog() == WinForms.DialogResult.OK
            ? $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}"
            : null;
    }

    private async void ViewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _viewerSettings = _viewerSettings with { PageMode = IsPageMode };
        UpdateViewMode();
        await SaveViewerSettingsAsync();
    }

    private void ReaderLayoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;

        _viewerSettings = _viewerSettings with
        {
            MarginTop = MarginTopSlider.Value,
            MarginBottom = MarginBottomSlider.Value,
            MarginLeft = MarginLeftSlider.Value,
            MarginRight = MarginRightSlider.Value,
            FontSize = FontSizeSlider.Value
        };
        ApplyReaderLayout();
        QueueReaderLayoutUpdate();
    }

    private void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || FontFamilyComboBox.SelectedItem is not string fontFamily) return;
        _viewerSettings = _viewerSettings with { FontFamily = fontFamily };
        ApplyReaderLayout();
        QueueReaderLayoutUpdate();
    }

    private void LoadReaderFonts()
    {
        string fontRoot = Path.Combine(AppContext.BaseDirectory, "font");
        if (Directory.Exists(fontRoot))
        {
            foreach (string fontPath in Directory.EnumerateFiles(fontRoot, "*.*", SearchOption.AllDirectories)
                         .Where(path => string.Equals(Path.GetExtension(path), ".ttf", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(Path.GetExtension(path), ".otf", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var glyphTypeface = new GlyphTypeface(new Uri(fontPath, UriKind.Absolute));
                    string? familyName = GetLocalizedFontName(glyphTypeface.FamilyNames)
                                         ?? GetLocalizedFontName(glyphTypeface.Win32FamilyNames);
                    if (string.IsNullOrWhiteSpace(familyName) || _readerFonts.ContainsKey(familyName)) continue;

                    string directory = Path.GetDirectoryName(fontPath)! + Path.DirectorySeparatorChar;
                    _readerFonts[familyName] = new MediaFontFamily(
                        new Uri(directory, UriKind.Absolute), $"./#{familyName}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or FileFormatException or NotSupportedException)
                {
                    // 손상되었거나 지원하지 않는 폰트 파일은 목록에서 제외합니다.
                }
            }
        }

        List<string> fontNames = _readerFonts.Keys
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        FontFamilyComboBox.ItemsSource = fontNames;
        FontFamilyComboBox.IsEnabled = fontNames.Count > 0;
        FontFamilyComboBox.ToolTip = fontNames.Count > 0
            ? $"실행 파일 옆 font 폴더에서 {fontNames.Count}개 글꼴을 찾았습니다."
            : "실행 파일 옆 font 폴더에 .ttf 또는 .otf 파일을 넣어 주세요.";
    }

    private static string? GetLocalizedFontName(IDictionary<CultureInfo, string> names)
    {
        CultureInfo korean = CultureInfo.GetCultureInfo("ko-KR");
        CultureInfo english = CultureInfo.GetCultureInfo("en-US");
        if (names.TryGetValue(korean, out string? koreanName)) return koreanName;
        if (names.TryGetValue(english, out string? englishName)) return englishName;
        return names.Values.FirstOrDefault();
    }

    private void FontStyleToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _viewerSettings = _viewerSettings with
        {
            Bold = BoldToggle.IsChecked == true,
            Italic = ItalicToggle.IsChecked == true
        };
        ApplyReaderLayout();
        QueueReaderLayoutUpdate();
    }

    private void QueueReaderLayoutUpdate()
    {
        _windowSizeSaveTimer.Stop();
        _windowSizeSaveTimer.Start();
        if (IsPageMode && !_repaginationTimer.IsEnabled) _repaginationTimer.Start();
    }

    private void UpdateViewMode()
    {
        if (IsPageMode)
        {
            ContentTextBox.Visibility = Visibility.Collapsed;
            PageView.Visibility = Visibility.Visible;
            ModeDescriptionText.Text = "페이지로 보기";
            MoveDescriptionText.Text = "이동할 페이지 번호";
            Dispatcher.BeginInvoke(StartPagination, DispatcherPriority.Loaded);
        }
        else
        {
            PageView.Visibility = Visibility.Collapsed;
            if (!string.Equals(ContentTextBox.Text, _currentText, StringComparison.Ordinal))
            {
                ContentTextBox.Text = _currentText;
            }
            ContentTextBox.Visibility = Visibility.Visible;
            ModeDescriptionText.Text = "스크롤로 보기";
            MoveDescriptionText.Text = "이동할 줄 번호";
        }
        MoveTargetTextBox.Clear();
        UpdateCurrentPosition();
    }

    private void ViewerView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isInitializing)
        {
            _windowSizeSaveTimer.Stop();
            _windowSizeSaveTimer.Start();
        }

        if (IsPageMode && ViewerView.Visibility == Visibility.Visible && !_repaginationTimer.IsEnabled)
        {
            _repaginationTimer.Start();
        }
    }

    private async void StartPagination()
    {
        int preservedCharacterIndex = _pages.Count > 0 && _currentPageIndex < _pages.Count
            ? _pages[_currentPageIndex].Start
            : 0;
        double availableWidth = Math.Max(100, PageContentArea.ActualWidth);
        double availableHeight = Math.Max(100, PageContentArea.ActualHeight);
        double fontSize = PageTextBlock.FontSize;
        double lineHeight = double.IsNaN(PageTextBlock.LineHeight) ? fontSize * 1.35 : PageTextBlock.LineHeight;
        double characterWidthFactor = 0.62 + (_viewerSettings.Bold ? 0.05 : 0) + (_viewerSettings.Italic ? 0.02 : 0);
        string text = _currentText;

        _paginationCancellation?.Cancel();
        _paginationCancellation?.Dispose();
        _paginationCancellation = new CancellationTokenSource();
        CancellationToken token = _paginationCancellation.Token;

        try
        {
            List<PageSlice> calculatedPages = await Task.Run(
                () => CalculatePages(text, availableWidth, availableHeight, fontSize, lineHeight, characterWidthFactor, token),
                token);

            if (token.IsCancellationRequested || !IsPageMode) return;
            _pages.Clear();
            _pages.AddRange(calculatedPages);
            _currentPageIndex = FindPageContaining(preservedCharacterIndex);
            RenderCurrentPage();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static List<PageSlice> CalculatePages(
        string text,
        double width,
        double height,
        double fontSize,
        double lineHeight,
        double characterWidthFactor,
        CancellationToken token)
    {
        var pages = new List<PageSlice>();
        if (text.Length == 0)
        {
            pages.Add(new PageSlice(0, 0, string.Empty));
            return pages;
        }

        int columnsPerLine = Math.Max(8, (int)(width / (fontSize * characterWidthFactor)));
        int linesPerPage = Math.Max(1, (int)(height / lineHeight));
        int pageStart = 0;
        int lineStart = 0;
        int lastWordBreak = -1;
        int currentColumn = 0;
        int linesOnPage = 1;

        for (int index = 0; index < text.Length; index++)
        {
            if ((index & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
            char character = text[index];

            if (character == '\r') continue;
            if (character == '\n')
            {
                currentColumn = 0;
                lineStart = index + 1;
                lastWordBreak = -1;
                if (linesOnPage >= linesPerPage)
                {
                    AddPage(pages, text, pageStart, index + 1);
                    pageStart = index + 1;
                    linesOnPage = 1;
                }
                else
                {
                    linesOnPage++;
                }
                continue;
            }

            int characterCells = EstimateCharacterCells(character, currentColumn);
            if (currentColumn > 0 && currentColumn + characterCells > columnsPerLine)
            {
                int wrapAt = lastWordBreak >= lineStart ? lastWordBreak + 1 : index;
                if (linesOnPage >= linesPerPage)
                {
                    AddPage(pages, text, pageStart, wrapAt);
                    pageStart = wrapAt;
                    linesOnPage = 1;
                }
                else
                {
                    linesOnPage++;
                }
                lineStart = wrapAt;
                lastWordBreak = -1;
                currentColumn = MeasureCharacterCells(text, wrapAt, index);
                characterCells = EstimateCharacterCells(character, currentColumn);
            }

            currentColumn += characterCells;
            if (char.IsWhiteSpace(character)) lastWordBreak = index;
        }

        if (pageStart < text.Length) AddPage(pages, text, pageStart, text.Length);
        if (pages.Count == 0) pages.Add(new PageSlice(0, text.Length, text));
        return pages;
    }

    private static int MeasureCharacterCells(string text, int start, int end)
    {
        int cells = 0;
        for (int index = start; index < end; index++)
        {
            cells += EstimateCharacterCells(text[index], cells);
        }
        return cells;
    }

    private static void AddPage(List<PageSlice> pages, string text, int start, int end)
    {
        int length = Math.Max(0, end - start);
        pages.Add(new PageSlice(start, length, text.Substring(start, length)));
    }

    private static int EstimateCharacterCells(char character, int currentColumn)
    {
        if (character == '\t') return 4 - (currentColumn % 4);
        if (character >= 0x1100 &&
            (character <= 0x115F || character == 0x2329 || character == 0x232A ||
             (character >= 0x2E80 && character <= 0xA4CF) ||
             (character >= 0xAC00 && character <= 0xD7A3) ||
             (character >= 0xF900 && character <= 0xFAFF) ||
             (character >= 0xFE10 && character <= 0xFE6F) ||
             (character >= 0xFF00 && character <= 0xFF60) ||
             (character >= 0xFFE0 && character <= 0xFFE6)))
        {
            return 2;
        }
        return 1;
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e) => ShowPage(_currentPageIndex - 1);

    private void NextPage_Click(object sender, RoutedEventArgs e) => ShowPage(_currentPageIndex + 1);

    private void ShowPage(int index)
    {
        if (_pages.Count == 0) return;
        _currentPageIndex = Math.Clamp(index, 0, _pages.Count - 1);
        RenderCurrentPage();
    }

    private void RenderCurrentPage()
    {
        if (_pages.Count == 0) return;
        PageSlice page = _pages[_currentPageIndex];
        PageTextBlock.Inlines.Clear();

        string query = SearchTextBox.Text;
        if (_searchIndex >= page.Start && _searchIndex < page.Start + page.Length && !string.IsNullOrEmpty(query))
        {
            int localStart = _searchIndex - page.Start;
            int highlightLength = Math.Min(query.Length, page.Text.Length - localStart);
            PageTextBlock.Inlines.Add(new Run(page.Text[..localStart]));
            PageTextBlock.Inlines.Add(new Run(page.Text.Substring(localStart, highlightLength))
            {
                Background = MediaBrushes.Gold,
                Foreground = MediaBrushes.Black
            });
            PageTextBlock.Inlines.Add(new Run(page.Text[(localStart + highlightLength)..]));
        }
        else
        {
            PageTextBlock.Text = page.Text;
        }

        PageIndicatorText.Text = $"{_currentPageIndex + 1:N0} / {_pages.Count:N0}";
        UpdateCurrentPosition();
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindText(forward: true);

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindText(forward: false);

    private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        FindText(forward: Keyboard.Modifiers != ModifierKeys.Shift);
        e.Handled = true;
    }

    private void FindText(bool forward)
    {
        string query = SearchTextBox.Text;
        if (string.IsNullOrEmpty(query) || _currentText.Length == 0)
        {
            SearchResultText.Text = "검색어를 입력하세요.";
            return;
        }

        if (!string.Equals(query, _lastSearchQuery, StringComparison.CurrentCulture))
        {
            _lastSearchQuery = query;
            _searchIndex = -1;
        }

        if (forward)
        {
            int start = _searchIndex >= 0 ? _searchIndex + 1 : 0;
            _searchIndex = _currentText.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
            if (_searchIndex < 0) _searchIndex = _currentText.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        }
        else
        {
            int start = _searchIndex > 0 ? _searchIndex - 1 : _currentText.Length - 1;
            _searchIndex = _currentText.LastIndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
            if (_searchIndex < 0) _searchIndex = _currentText.LastIndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        }

        if (_searchIndex < 0)
        {
            SearchResultText.Text = "일치하는 문자가 없습니다.";
            return;
        }

        SearchResultText.Text = $"문자 위치 {_searchIndex + 1:N0}";
        if (IsPageMode)
        {
            _currentPageIndex = FindPageContaining(_searchIndex);
            RenderCurrentPage();
        }
        else
        {
            ContentTextBox.Focus();
            ContentTextBox.Select(_searchIndex, query.Length);
            int visualLine = ContentTextBox.GetLineIndexFromCharacterIndex(_searchIndex);
            ContentTextBox.ScrollToLine(Math.Max(0, visualLine - 2));
        }
    }

    private void MoveToTarget_Click(object sender, RoutedEventArgs e) => MoveToTarget();

    private void MoveTargetTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        MoveToTarget();
        e.Handled = true;
    }

    private void MoveToTarget()
    {
        if (!int.TryParse(MoveTargetTextBox.Text, out int target) || target < 1)
        {
            MessageBox.Show(this, "1 이상의 숫자를 입력하세요.", "이동", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsPageMode)
        {
            if (target > _pages.Count)
            {
                MessageBox.Show(this, $"페이지는 1부터 {_pages.Count:N0}까지 있습니다.", "이동", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ShowPage(target - 1);
        }
        else
        {
            int characterIndex = FindLogicalLineStart(target);
            if (characterIndex < 0)
            {
                MessageBox.Show(this, $"줄은 1부터 {_lineStarts.Count:N0}까지 있습니다.", "이동", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ContentTextBox.Focus();
            ContentTextBox.Select(characterIndex, 0);
            int visualLine = ContentTextBox.GetLineIndexFromCharacterIndex(characterIndex);
            ContentTextBox.ScrollToLine(Math.Max(0, visualLine - 2));
        }
    }

    private int FindLogicalLineStart(int targetLine)
    {
        return targetLine >= 1 && targetLine <= _lineStarts.Count ? _lineStarts[targetLine - 1] : -1;
    }

    private void BuildLineStarts()
    {
        _lineStarts.Clear();
        if (_currentText.Length == 0) return;
        _lineStarts.Add(0);
        for (int index = 0; index < _currentText.Length; index++)
        {
            if (_currentText[index] == '\n' && index + 1 < _currentText.Length) _lineStarts.Add(index + 1);
        }
    }

    private void ContentTextBox_SelectionChanged(object sender, RoutedEventArgs e) => UpdateCurrentPosition();

    private void ContentTextBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!IsPageMode && e.VerticalChange != 0) UpdateCurrentPosition();
    }

    private void UpdateCurrentPosition()
    {
        if (IsPageMode)
        {
            CurrentPositionText.Text = _pages.Count == 0
                ? "0 / 0"
                : $"{_currentPageIndex + 1:N0} / {_pages.Count:N0}";
            return;
        }

        if (_lineStarts.Count == 0)
        {
            CurrentPositionText.Text = "0 / 0";
            return;
        }

        int characterIndex = ContentTextBox.SelectionStart;
        int firstVisibleLine = ContentTextBox.GetFirstVisibleLineIndex();
        if (firstVisibleLine >= 0)
        {
            int firstVisibleCharacter = ContentTextBox.GetCharacterIndexFromLineIndex(firstVisibleLine);
            if (firstVisibleCharacter >= 0) characterIndex = firstVisibleCharacter;
        }

        int lineIndex = _lineStarts.BinarySearch(characterIndex);
        if (lineIndex < 0) lineIndex = ~lineIndex - 1;
        CurrentPositionText.Text = $"{Math.Max(0, lineIndex) + 1:N0} / {_lineStarts.Count:N0}";
    }

    private int FindPageContaining(int characterIndex)
    {
        for (int index = 0; index < _pages.Count; index++)
        {
            PageSlice page = _pages[index];
            if (characterIndex < page.Start + page.Length) return index;
        }
        return Math.Max(0, _pages.Count - 1);
    }

    private void ApplyViewerSettings()
    {
        SolidColorBrush background = BrushFromHex(_viewerSettings.BackgroundColor, Colors.White);
        SolidColorBrush foreground = BrushFromHex(_viewerSettings.TextColor, MediaColor.FromRgb(17, 24, 39));
        ViewerBackground.Background = background;
        ContentTextBox.Foreground = foreground;
        ContentTextBox.CaretBrush = foreground;
        PageTextBlock.Foreground = foreground;
        BackgroundColorPreview.Background = background;
        TextColorPreview.Background = foreground;
        ApplyLibraryTheme(background.Color);
        ApplyTitleBarTheme(background.Color);
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        MarginTopSlider.Value = Math.Clamp(_viewerSettings.MarginTop, 0, 120);
        MarginBottomSlider.Value = Math.Clamp(_viewerSettings.MarginBottom, 0, 120);
        MarginLeftSlider.Value = Math.Clamp(_viewerSettings.MarginLeft, 0, 120);
        MarginRightSlider.Value = Math.Clamp(_viewerSettings.MarginRight, 0, 120);
        FontSizeSlider.Value = Math.Clamp(_viewerSettings.FontSize, 10, 36);
        FontFamilyComboBox.SelectedItem = _viewerSettings.FontFamily;
        BoldToggle.IsChecked = _viewerSettings.Bold;
        ItalicToggle.IsChecked = _viewerSettings.Italic;
        _isInitializing = wasInitializing;
        ApplyReaderLayout();
    }

    private void ApplyReaderLayout()
    {
        double top = Math.Clamp(_viewerSettings.MarginTop, 0, 120);
        double bottom = Math.Clamp(_viewerSettings.MarginBottom, 0, 120);
        double left = Math.Clamp(_viewerSettings.MarginLeft, 0, 120);
        double right = Math.Clamp(_viewerSettings.MarginRight, 0, 120);
        double fontSize = Math.Clamp(_viewerSettings.FontSize, 10, 36);

        ContentTextBox.Padding = new Thickness(left, top, right, bottom);
        PageContentArea.Margin = new Thickness(left, top, right, bottom);
        ContentTextBox.FontSize = fontSize;
        PageTextBlock.FontSize = fontSize;
        MediaFontFamily readerFont = _readerFonts.TryGetValue(_viewerSettings.FontFamily, out MediaFontFamily? bundledFont)
            ? bundledFont
            : new MediaFontFamily(ViewerSettings.Default.FontFamily);
        ContentTextBox.FontFamily = readerFont;
        PageTextBlock.FontFamily = readerFont;
        ContentTextBox.FontWeight = _viewerSettings.Bold ? FontWeights.Bold : FontWeights.Normal;
        PageTextBlock.FontWeight = _viewerSettings.Bold ? FontWeights.Bold : FontWeights.Normal;
        ContentTextBox.FontStyle = _viewerSettings.Italic ? FontStyles.Italic : FontStyles.Normal;
        PageTextBlock.FontStyle = _viewerSettings.Italic ? FontStyles.Italic : FontStyles.Normal;
        double lineHeight = fontSize * 1.35;
        TextBlock.SetLineHeight(ContentTextBox, lineHeight);
        PageTextBlock.LineHeight = lineHeight;
        MarginTopValueText.Text = $"{top:0}";
        MarginBottomValueText.Text = $"{bottom:0}";
        MarginLeftValueText.Text = $"{left:0}";
        MarginRightValueText.Text = $"{right:0}";
        FontSizeValueText.Text = $"{fontSize:0}";
    }

    private void ApplyLibraryTheme(MediaColor viewerBackground)
    {
        bool dark = (0.2126 * viewerBackground.R + 0.7152 * viewerBackground.G + 0.0722 * viewerBackground.B) / 255 < 0.5;
        SetThemeBrush("WindowBackgroundBrush", dark ? "#0B0F14" : "#F7F8FA");
        SetThemeBrush("PanelBrush", dark ? "#151B24" : "#FFFFFF");
        SetThemeBrush("PrimaryTextBrush", dark ? "#F4F7FB" : "#101828");
        SetThemeBrush("MutedTextBrush", dark ? "#98A2B3" : "#667085");
        SetThemeBrush("BorderBrush", dark ? "#293241" : "#E4E7EC");
        SetThemeBrush("SecondaryButtonBrush", dark ? "#222B38" : "#E5E7EB");
        SetThemeBrush("SecondaryButtonTextBrush", dark ? "#E5E7EB" : "#344054");
        SetThemeBrush("ItemHoverBrush", dark ? "#1C2532" : "#F2F4F7");
        SetThemeBrush("ItemSelectedBrush", dark ? "#1D3454" : "#EAF2FF");
        SetThemeBrush("ItemSelectedBorderBrush", dark ? "#3B82F6" : "#93B4F5");
    }

    private static void SetThemeBrush(string resourceKey, string colorValue)
    {
        System.Windows.Application.Current.Resources[resourceKey] = BrushFromHex(colorValue, Colors.Transparent);
    }

    private void ApplyTitleBarTheme(MediaColor backgroundColor)
    {
        IntPtr windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero) return;

        double luminance = (0.2126 * backgroundColor.R + 0.7152 * backgroundColor.G + 0.0722 * backgroundColor.B) / 255;
        int useDarkMode = luminance < 0.5 ? 1 : 0;
        int captionColor = useDarkMode == 1 ? 0x000000 : 0x00FFFFFF;
        int captionTextColor = useDarkMode == 1 ? 0x00FFFFFF : 0x00000000;

        try
        {
            _ = DwmSetWindowAttribute(windowHandle, 20, ref useDarkMode, sizeof(int));
            _ = DwmSetWindowAttribute(windowHandle, 35, ref captionColor, sizeof(int));
            _ = DwmSetWindowAttribute(windowHandle, 36, ref captionTextColor, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
    }

    private static SolidColorBrush BrushFromHex(string value, MediaColor fallback)
    {
        try
        {
            return new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(value));
        }
        catch (FormatException)
        {
            return new SolidColorBrush(fallback);
        }
    }

    private async Task SaveViewerSettingsAsync()
    {
        try { await ViewerSettingsStore.SaveAsync(_viewerSettings); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RestoreWindowSize()
    {
        double maximumWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width);
        double maximumHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height);
        double savedWidth = double.IsFinite(_viewerSettings.WindowWidth) && _viewerSettings.WindowWidth > 0
            ? _viewerSettings.WindowWidth
            : ViewerSettings.Default.WindowWidth;
        double savedHeight = double.IsFinite(_viewerSettings.WindowHeight) && _viewerSettings.WindowHeight > 0
            ? _viewerSettings.WindowHeight
            : ViewerSettings.Default.WindowHeight;

        Width = Math.Clamp(savedWidth, MinWidth, maximumWidth);
        Height = Math.Clamp(savedHeight, MinHeight, maximumHeight);
    }

    private void CaptureWindowSize()
    {
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
        {
            _viewerSettings = _viewerSettings with
            {
                WindowWidth = bounds.Width,
                WindowHeight = bounds.Height
            };
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _paginationCancellation?.Cancel();
        CaptureWindowSize();
        try { ViewerSettingsStore.Save(_viewerSettings); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task RemoveHistoryEntryAsync(RecentFileEntry entry)
    {
        try { SetRecentFiles(await HistoryStore.RemoveAsync(entry.FilePath)); }
        catch (IOException ex) { ShowHistoryError(ex); }
        catch (UnauthorizedAccessException ex) { ShowHistoryError(ex); }
    }

    private void SetRecentFiles(IEnumerable<RecentFileEntry> entries)
    {
        _recentFiles.Clear();
        foreach (RecentFileEntry entry in entries) _recentFiles.Add(entry);
        EmptyHistoryPanel.Visibility = _recentFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowLibrary()
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Collapsed;
        ViewerView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
        Title = "INTViewer";
    }

    private void ShowViewer()
    {
        LibraryView.Visibility = Visibility.Collapsed;
        UpdateViewMode();
        ViewerView.Visibility = Visibility.Visible;
    }

    private void ShowHistoryError(Exception exception) => MessageBox.Show(this, exception.Message,
        "기록 저장 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
}
