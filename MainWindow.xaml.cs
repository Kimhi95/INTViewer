using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using INTViewer.Models;
using INTViewer.Services;
using Microsoft.Win32;
using Registry = Microsoft.Win32.Registry;
using DataFormats = System.Windows.DataFormats;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using IDataObject = System.Windows.IDataObject;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaFontFamily = System.Windows.Media.FontFamily;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Size = System.Windows.Size;

namespace INTViewer;

public partial class MainWindow : Window
{
    private sealed record PageSlice(int Start, int Length);
    private sealed record BookmarkListItem(int Position, string PositionLabel, string Preview);

    private readonly ObservableCollection<RecentFileEntry> _recentFiles = [];
    private readonly ObservableCollection<BookmarkListItem> _bookmarkItems = [];
    private readonly List<PageSlice> _pages = [];
    private readonly List<int> _lineStarts = [];
    private readonly Dictionary<string, MediaFontFamily> _readerFonts = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly Dictionary<string, int> _readerFontPriorities = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly DispatcherTimer _repaginationTimer;
    private readonly DispatcherTimer _windowSizeSaveTimer;
    private readonly DispatcherTimer _readingProgressSaveTimer;
    private readonly DispatcherTimer _scrollUiTimer;
    private readonly DispatcherTimer _readerLayoutApplyTimer;
    private readonly SemaphoreSlim _paginationGate = new(1, 1);
    private ViewerSettings _viewerSettings = ViewerSettings.Default;
    private string _currentText = string.Empty;
    private string _lastSearchQuery = string.Empty;
    private int _searchIndex = -1;
    private int _currentPageIndex;
    private int _currentPositionValue;
    private int _totalPositionValue;
    private string? _currentFilePath;
    private double _currentReadProgress;
    private string? _pendingProgressFilePath;
    private double _pendingProgressValue;
    private double _pendingScrollProgress;
    private int _pendingReadPosition;
    private int[] _pendingBookmarks = [];
    private double? _pendingRestoreProgress;
    private int? _pendingRestorePosition;
    private string _currentEncodingKey = "auto";
    private DateTime _currentLastWriteTimeUtc;
    private DateTime _ignoredWriteTimeUtc;
    private readonly List<int> _bookmarks = [];
    private readonly List<int> _searchMatches = [];
    private int _searchMatchListIndex = -1;
    private bool _isInitializing = true;
    private bool _isUpdatingColorPicker;
    private bool _isEditingBackgroundColor;
    private bool _isPickingColorSpectrum;
    private bool _isCommittingPosition;
    private bool _isRestoringProgress;
    private bool _isFullScreen;
    private bool _storageWarningShown;
    private WindowStyle _savedWindowStyle;
    private WindowState _savedWindowState;
    private CancellationTokenSource? _paginationCancellation;
    private int _paginationGeneration;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

    public MainWindow()
    {
        InitializeComponent();
        ViewerView.Children.Remove(SettingsPanel);
        RootGrid.Children.Add(SettingsPanel);
        RecentFilesList.ItemsSource = _recentFiles;
        BookmarkListBox.ItemsSource = _bookmarkItems;
        EncodingComboBox.ItemsSource = TextFileReader.SupportedEncodings;
        EncodingComboBox.SelectedValue = "auto";
        LoadReaderFonts();
        ContentTextBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ContentTextBox_ScrollChanged));
        Loaded += MainWindow_Loaded;
        Deactivated += (_, _) => ColorPickerPopup.IsOpen = false;
        Activated += MainWindow_Activated;
        SourceInitialized += (_, _) => ApplyTitleBarTheme(BrushFromHex(_viewerSettings.BackgroundColor, Colors.White).Color);

        _repaginationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
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

        _readingProgressSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _readingProgressSaveTimer.Tick += async (_, _) =>
        {
            _readingProgressSaveTimer.Stop();
            string? filePath = _pendingProgressFilePath;
            double progress = _pendingProgressValue;
            if (string.IsNullOrWhiteSpace(filePath)) return;
            try { await HistoryStore.UpdateReadingStateAsync(filePath, progress, _pendingReadPosition, _pendingBookmarks); }
            catch (IOException ex) { ShowStorageWarningOnce(ex); }
            catch (UnauthorizedAccessException ex) { ShowStorageWarningOnce(ex); }
        };

        _scrollUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _scrollUiTimer.Tick += (_, _) =>
        {
            _scrollUiTimer.Stop();
            if (IsPageMode) return;
            UpdateCurrentPosition();
            RecordReadingProgress(_pendingScrollProgress, GetCurrentCharacterPosition());
        };

        _readerLayoutApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _readerLayoutApplyTimer.Tick += (_, _) =>
        {
            _readerLayoutApplyTimer.Stop();
            ApplyReaderLayout();
            QueueReaderLayoutUpdate();
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
        if (_viewerSettings.SettingsVersion < 4)
        {
            _viewerSettings = _viewerSettings with { LineSpacing = 1.35, SettingsVersion = 4 };
        }
        if (_readerFonts.Count > 0 && !_readerFonts.ContainsKey(_viewerSettings.FontFamily))
        {
            string normalizedFontName = NormalizeKnownFontStyleSuffix(_viewerSettings.FontFamily);
            _viewerSettings = _viewerSettings with
            {
                FontFamily = _readerFonts.ContainsKey(normalizedFontName) ? normalizedFontName : _readerFonts.Keys.First()
            };
        }
        RestoreWindowSize();
        ApplyViewerSettings();
        PageModeToggle.IsChecked = _viewerSettings.PageMode;
        SearchCaseSensitiveCheckBox.IsChecked = _viewerSettings.SearchCaseSensitive;
        SearchWholeWordCheckBox.IsChecked = _viewerSettings.SearchWholeWord;
        AutoOpenLastFileCheckBox.IsChecked = _viewerSettings.AutoOpenLastFile;
        _isInitializing = false;
        UpdateViewMode();

        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) await OpenFileAsync(args[1]);
        else if (_viewerSettings.AutoOpenLastFile && _recentFiles.Where(entry => File.Exists(entry.FilePath))
                     .OrderByDescending(entry => entry.LastOpenedAt).FirstOrDefault() is { } lastFile)
            await OpenFileAsync(lastFile.FilePath);
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e) => await OpenFileFromDialogAsync();

    private async Task OpenFileFromDialogAsync()
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
        PinSelectedButton.IsEnabled = hasSelection;
    }

    private async Task OpenSelectedFileAsync()
    {
        if (RecentFilesList.SelectedItem is not RecentFileEntry selected) return;

        if (!File.Exists(selected.FilePath))
        {
            MessageBoxResult result = ModernDialog.Show(this,
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
        MessageBoxResult result = ModernDialog.Show(this,
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
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _isFullScreen)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.B && ViewerView.Visibility == Visibility.Visible)
        {
            ToggleBookmark();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F2 && ViewerView.Visibility == Visibility.Visible)
        {
            GoToNextBookmark();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            _ = OpenFileFromDialogAsync();
            e.Handled = true;
            return;
        }

        if (LibraryView.Visibility == Visibility.Visible && Keyboard.Modifiers == ModifierKeys.Control &&
            e.Key is Key.F or Key.L)
        {
            RecentFilterTextBox.Focus();
            RecentFilterTextBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (LibraryView.Visibility == Visibility.Visible && e.Key == Key.Escape &&
            !string.IsNullOrEmpty(RecentFilterTextBox.Text))
        {
            RecentFilterTextBox.Clear();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && PositionInputTextBox.Visibility == Visibility.Visible)
        {
            EndPositionEditing();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && SettingsPanel.Visibility == Visibility.Visible)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && BookmarksPanel.Visibility == Visibility.Visible)
        {
            BookmarksPanel.Visibility = Visibility.Collapsed;
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

        if (IsPageMode && SettingsPanel.Visibility != Visibility.Visible && SearchPanel.Visibility != Visibility.Visible
            && BookmarksPanel.Visibility != Visibility.Visible
            && PositionInputTextBox.Visibility != Visibility.Visible)
        {
            if (e.Key == Key.Home)
            {
                ShowPage(0);
                e.Handled = true;
            }
            else if (e.Key == Key.End)
            {
                ShowPage(_pages.Count - 1);
                e.Handled = true;
            }
            else if (e.Key is Key.Right or Key.Down or Key.PageDown or Key.Space)
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
            SettingsPanel.Visibility == Visibility.Visible || SearchPanel.Visibility == Visibility.Visible ||
            BookmarksPanel.Visibility == Visibility.Visible ||
            PositionInputTextBox.Visibility == Visibility.Visible) return;
        ShowPage(_currentPageIndex + (e.Delta < 0 ? 1 : -1));
        e.Handled = true;
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (PositionInputTextBox.Visibility == Visibility.Visible && !PositionInputTextBox.IsMouseOver)
        {
            MoveToTarget(showValidationMessage: false);
        }
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

    private async Task OpenFileAsync(string filePath, string encodingKey = "auto")
    {
        try
        {
            var content = await TextFileReader.ReadAsync(filePath, encodingKey);
            RecentFileEntry? previousEntry = _recentFiles.FirstOrDefault(entry =>
                string.Equals(entry.FilePath, content.FilePath, StringComparison.OrdinalIgnoreCase));
            bool resetChangedFilePosition = false;
            if (previousEntry is { HasChanged: true, ReadPosition: > 0 })
            {
                MessageBoxResult keepPosition = ModernDialog.Show(this,
                    "원본 파일이 이전에 읽었을 때와 달라졌습니다. 기존 읽기 위치와 책갈피를 유지할까요?",
                    "파일 변경 감지", MessageBoxButton.YesNo, MessageBoxImage.Question);
                resetChangedFilePosition = keepPosition == MessageBoxResult.No;
            }
            _currentText = content.Text;
            _currentFilePath = content.FilePath;
            _currentReadProgress = resetChangedFilePosition ? 0 : previousEntry?.ReadProgress ?? 0;
            _pendingRestorePosition = !resetChangedFilePosition && previousEntry is { ReadPosition: > 0 } ? previousEntry.ReadPosition : null;
            _pendingRestoreProgress = _currentReadProgress;
            _bookmarks.Clear();
            if (!resetChangedFilePosition && previousEntry?.BookmarkPositions is { } savedBookmarks)
                _bookmarks.AddRange(savedBookmarks.Where(position => position >= 0 && position < content.Text.Length));
            _currentEncodingKey = content.EncodingKey;
            _currentLastWriteTimeUtc = content.LastWriteTimeUtc;
            _ignoredWriteTimeUtc = default;
            EncodingComboBox.SelectedValue = content.EncodingKey;
            _pages.Clear();
            _currentPageIndex = 0;
            BuildLineStarts();
            _searchIndex = -1;
            _lastSearchQuery = string.Empty;
            if (IsPageMode)
            {
                ContentTextBox.Clear();
            }
            else
            {
                _isRestoringProgress = _pendingRestoreProgress.HasValue;
                ContentTextBox.Text = content.Text;
                ContentTextBox.ScrollToHome();
            }
            Title = "INTViewer";
            ShowViewer();
            UpdateCurrentPosition();

            var entry = new RecentFileEntry(content.FilePath, content.EncodingName,
                content.ByteLength, DateTimeOffset.Now, _currentReadProgress,
                resetChangedFilePosition ? 0 : previousEntry?.ReadPosition ?? 0, content.LastWriteTimeUtc, previousEntry?.IsPinned ?? false,
                _bookmarks.ToArray());
            try { SetRecentFiles(await HistoryStore.AddOrUpdateAsync(entry)); }
            catch (IOException ex) { ShowStorageWarningOnce(ex); }
            catch (UnauthorizedAccessException ex) { ShowStorageWarningOnce(ex); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or NotSupportedException)
        {
            ModernDialog.Show(this, ex.Message, "파일 열기 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        BookmarksPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = Visibility.Collapsed;

    private void BackToLibrary_Click(object sender, RoutedEventArgs e) => ShowLibrary();

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        BookmarksPanel.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = SearchPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (SearchPanel.Visibility == Visibility.Visible) SearchTextBox.Focus();
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e) => SearchPanel.Visibility = Visibility.Collapsed;

    private void BookmarksButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Collapsed;
        bool open = BookmarksPanel.Visibility != Visibility.Visible;
        BookmarksPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) RefreshBookmarkList();
    }

    private void CloseBookmarks_Click(object sender, RoutedEventArgs e) => BookmarksPanel.Visibility = Visibility.Collapsed;

    private void WhiteMode_Click(object sender, RoutedEventArgs e)
    {
        _viewerSettings = _viewerSettings with { BackgroundColor = "#FFFFFF", TextColor = "#111827" };
        ApplyViewerSettings();
        QueueViewerSettingsSave();
    }

    private void DarkMode_Click(object sender, RoutedEventArgs e)
    {
        _viewerSettings = _viewerSettings with { BackgroundColor = "#111827", TextColor = "#E5E7EB" };
        ApplyViewerSettings();
        QueueViewerSettingsSave();
    }

    private void ChooseBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        OpenColorPicker(sender as UIElement, true, _viewerSettings.BackgroundColor);
    }

    private void ChooseTextColor_Click(object sender, RoutedEventArgs e)
    {
        OpenColorPicker(sender as UIElement, false, _viewerSettings.TextColor);
    }

    private void OpenColorPicker(UIElement? placementTarget, bool backgroundColor, string currentColor)
    {
        if (placementTarget is null) return;

        _isEditingBackgroundColor = backgroundColor;
        MediaColor color = BrushFromHex(currentColor, Colors.White).Color;
        _isUpdatingColorPicker = true;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        UpdateColorPickerPreview(color);
        ColorPickerTitleText.Text = backgroundColor ? "배경색 선택" : "글자색 선택";
        _isUpdatingColorPicker = false;
        ColorPickerPopup.PlacementTarget = placementTarget;
        ColorPickerPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => PositionColorSpectrumMarker(color), DispatcherPriority.Loaded);
    }

    private void ColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingColorPicker || !ColorPickerPopup.IsOpen) return;

        MediaColor color = MediaColor.FromRgb(
            (byte)Math.Round(RedSlider.Value),
            (byte)Math.Round(GreenSlider.Value),
            (byte)Math.Round(BlueSlider.Value));
        ApplyColorPickerColor(color);
    }

    private void ApplyColorPickerColor(MediaColor color)
    {
        string colorValue = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _viewerSettings = _isEditingBackgroundColor
            ? _viewerSettings with { BackgroundColor = colorValue }
            : _viewerSettings with { TextColor = colorValue };
        UpdateColorPickerPreview(color);
        ApplyViewerSettings();
        QueueViewerSettingsSave();
    }

    private void UpdateColorPickerPreview(MediaColor color)
    {
        ColorPickerPreview.Background = new SolidColorBrush(color);
        ColorHexText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        RedValueText.Text = color.R.ToString();
        GreenValueText.Text = color.G.ToString();
        BlueValueText.Text = color.B.ToString();
        PositionColorSpectrumMarker(color);
    }

    private void ColorSpectrum_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPickingColorSpectrum = true;
        Mouse.Capture(ColorSpectrum);
        ApplyColorSpectrumPoint(e.GetPosition(ColorSpectrum));
        e.Handled = true;
    }

    private void ColorSpectrum_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPickingColorSpectrum || e.LeftButton != MouseButtonState.Pressed) return;
        ApplyColorSpectrumPoint(e.GetPosition(ColorSpectrum));
    }

    private void ColorSpectrum_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPickingColorSpectrum) return;
        ApplyColorSpectrumPoint(e.GetPosition(ColorSpectrum));
        _isPickingColorSpectrum = false;
        Mouse.Capture(null);
        e.Handled = true;
    }

    private void ApplyColorSpectrumPoint(Point point)
    {
        double width = Math.Max(1, ColorSpectrum.ActualWidth);
        double height = Math.Max(1, ColorSpectrum.ActualHeight);
        double x = Math.Clamp(point.X, 0, width);
        double y = Math.Clamp(point.Y, 0, height);
        MediaColor color = ColorFromHsv(360d * x / width, x / width, 1d - y / height);

        _isUpdatingColorPicker = true;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        _isUpdatingColorPicker = false;
        ApplyColorPickerColor(color);
    }

    private void PositionColorSpectrumMarker(MediaColor color)
    {
        if (ColorSpectrum.ActualWidth <= 0 || ColorSpectrum.ActualHeight <= 0) return;
        (double hue, double saturation, double value) = ColorToHsv(color);
        double x = saturation <= 0.001 ? 0 : hue / 360d * ColorSpectrum.ActualWidth;
        double y = (1d - value) * ColorSpectrum.ActualHeight;
        Canvas.SetLeft(ColorSpectrumMarker, Math.Clamp(x - 8, -2, ColorSpectrum.ActualWidth - 14));
        Canvas.SetTop(ColorSpectrumMarker, Math.Clamp(y - 8, -2, ColorSpectrum.ActualHeight - 14));
    }

    private static MediaColor ColorFromHsv(double hue, double saturation, double value)
    {
        double chroma = value * saturation;
        double segment = hue / 60d;
        double secondary = chroma * (1 - Math.Abs(segment % 2 - 1));
        (double red, double green, double blue) = segment switch
        {
            < 1 => (chroma, secondary, 0d),
            < 2 => (secondary, chroma, 0d),
            < 3 => (0d, chroma, secondary),
            < 4 => (0d, secondary, chroma),
            < 5 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary)
        };
        double match = value - chroma;
        return MediaColor.FromRgb((byte)Math.Round((red + match) * 255),
            (byte)Math.Round((green + match) * 255), (byte)Math.Round((blue + match) * 255));
    }

    private static (double Hue, double Saturation, double Value) ColorToHsv(MediaColor color)
    {
        double red = color.R / 255d;
        double green = color.G / 255d;
        double blue = color.B / 255d;
        double maximum = Math.Max(red, Math.Max(green, blue));
        double minimum = Math.Min(red, Math.Min(green, blue));
        double delta = maximum - minimum;
        double hue = delta <= 0 ? 0
            : maximum == red ? 60 * (((green - blue) / delta) % 6)
            : maximum == green ? 60 * ((blue - red) / delta + 2)
            : 60 * ((red - green) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, maximum <= 0 ? 0 : delta / maximum, maximum);
    }

    private async void ColorPickerPopup_Closed(object sender, EventArgs e)
    {
        _windowSizeSaveTimer.Stop();
        CaptureWindowSize();
        await SaveViewerSettingsAsync();
    }

    private void ViewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _viewerSettings = _viewerSettings with { PageMode = IsPageMode };
        if (!string.IsNullOrWhiteSpace(_currentFilePath)) _pendingRestoreProgress = _currentReadProgress;
        if (!IsPageMode && _pendingRestoreProgress.HasValue) _isRestoringProgress = true;
        UpdateViewMode();
        QueueViewerSettingsSave();
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
            FontSize = FontSizeSlider.Value,
            LineSpacing = LineSpacingSlider.Value
        };
        if (!_readerLayoutApplyTimer.IsEnabled) _readerLayoutApplyTimer.Start();
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
                    if (string.IsNullOrWhiteSpace(familyName)) continue;

                    string displayName = GetFontDisplayName(familyName, glyphTypeface.FaceNames.Values);
                    int priority = Math.Abs(glyphTypeface.Weight.ToOpenTypeWeight() - FontWeights.Normal.ToOpenTypeWeight());
                    if (_readerFontPriorities.TryGetValue(displayName, out int existingPriority) && existingPriority <= priority)
                        continue;

                    string directory = Path.GetDirectoryName(fontPath)! + Path.DirectorySeparatorChar;
                    _readerFonts[displayName] = new MediaFontFamily(
                        new Uri(directory, UriKind.Absolute), $"./#{familyName}");
                    _readerFontPriorities[displayName] = priority;
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

    private static string GetFontDisplayName(string familyName, IEnumerable<string> faceNames)
    {
        foreach (string faceName in faceNames.Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderByDescending(name => name.Length))
        {
            string suffix = " " + faceName.Trim();
            if (familyName.EndsWith(suffix, StringComparison.CurrentCultureIgnoreCase) && familyName.Length > suffix.Length)
                return familyName[..^suffix.Length].TrimEnd();
        }
        return familyName.Trim();
    }

    private static string NormalizeKnownFontStyleSuffix(string familyName)
    {
        string[] suffixes = [" Regular", " Bold", " Medium", " Light", " SemiBold", " Semibold", " Thin", " Black"];
        foreach (string suffix in suffixes)
        {
            if (familyName.EndsWith(suffix, StringComparison.CurrentCultureIgnoreCase) && familyName.Length > suffix.Length)
                return familyName[..^suffix.Length].TrimEnd();
        }
        return familyName;
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
        QueueViewerSettingsSave();
        if (IsPageMode) QueuePagination();
    }

    private void QueueViewerSettingsSave()
    {
        _windowSizeSaveTimer.Stop();
        _windowSizeSaveTimer.Start();
    }

    private void UpdateViewMode()
    {
        if (IsPageMode)
        {
            ContentTextBox.Visibility = Visibility.Collapsed;
            PageView.Visibility = Visibility.Visible;
            ModeDescriptionText.Text = "페이지로 보기";
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
            if (_pendingRestoreProgress.HasValue)
            {
                Dispatcher.BeginInvoke(RestoreScrollProgress, DispatcherPriority.ContextIdle);
            }
        }
        PreviousPageButton.Visibility = IsPageMode ? Visibility.Visible : Visibility.Hidden;
        NextPageButton.Visibility = IsPageMode ? Visibility.Visible : Visibility.Hidden;
        EndPositionEditing();
        UpdateCurrentPosition();
        RefreshBookmarkList();
    }

    private void ViewerView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isInitializing)
        {
            QueueViewerSettingsSave();
        }

        if (IsPageMode && ViewerView.Visibility == Visibility.Visible) QueuePagination();
    }

    private void LibraryView_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout(RootGrid.ActualWidth);

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0 || double.IsNaN(width)) return;

        bool compact = width < 620;
        LibraryView.Margin = compact ? new Thickness(16, 18, 16, 16) : new Thickness(32);
        LibraryTopGap.Height = new GridLength(compact ? 16 : 24);
        LibraryBottomGap.Height = new GridLength(compact ? 12 : 20);

        Grid.SetRow(LibraryHeaderActions, compact ? 1 : 0);
        Grid.SetColumn(LibraryHeaderActions, compact ? 0 : 1);
        Grid.SetColumnSpan(LibraryHeaderActions, compact ? 2 : 1);
        LibraryHeaderActions.HorizontalAlignment = compact ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        LibraryHeaderActions.Margin = compact ? new Thickness(0, 14, 0, 0) : new Thickness(0);

        Grid.SetRow(LibraryIntroText, compact ? 2 : 1);
        LibraryIntroText.Margin = compact ? new Thickness(0, 10, 0, 0) : new Thickness(0, 10, 0, 0);
        RecentFilterContainer.Width = compact ? 138 : 180;
        RecentHeader.Margin = compact ? new Thickness(14, 12, 14, 10) : new Thickness(20, 14, 20, 14);

        Grid.SetRow(ClearHistoryButton, compact ? 1 : 0);
        Grid.SetColumn(ClearHistoryButton, compact ? 0 : 1);
        Grid.SetColumnSpan(ClearHistoryButton, compact ? 2 : 1);
        ClearHistoryButton.HorizontalAlignment = compact ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        ClearHistoryButton.Margin = compact ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        Grid.SetRow(HistoryDescriptionText, compact ? 2 : 1);
        HistoryDescriptionText.Margin = compact ? new Thickness(0, 9, 0, 0) : new Thickness(0, 7, 0, 0);

        double overlayWidth = Math.Max(300, Math.Min(360, width - (compact ? 24 : 40)));
        SettingsPanel.Width = overlayWidth;
        SettingsPanel.Margin = compact ? new Thickness(12, 58, 12, 12) : new Thickness(0, 64, 20, 20);
        SearchPanel.Width = overlayWidth;
        SearchPanel.Margin = compact ? new Thickness(12, 58, 12, 0) : new Thickness(0, 64, 20, 0);
        BookmarksPanel.Width = overlayWidth;
        BookmarksPanel.Margin = compact ? new Thickness(12, 58, 12, 12) : new Thickness(0, 64, 20, 20);

        ReaderToolbar.Width = Math.Min(310, Math.Max(280, width - 24));
        double pageZoneWidth = Math.Min(140, Math.Max(72, width * 0.25));
        PreviousPageArea.Width = pageZoneWidth;
        NextPageArea.Width = pageZoneWidth;
    }

    private void QueuePagination()
    {
        _paginationCancellation?.Cancel();
        _repaginationTimer.Stop();
        _repaginationTimer.Start();
    }

    private async void StartPagination()
    {
        int generation = ++_paginationGeneration;
        int preservedCharacterIndex = _pages.Count > 0 && _currentPageIndex < _pages.Count
            ? _pages[_currentPageIndex].Start
            : 0;
        double availableWidth = Math.Max(100, PageContentArea.ActualWidth);
        double availableHeight = Math.Max(100, PageContentArea.ActualHeight);
        double fontSize = PageTextBlock.FontSize;
        double lineHeight = double.IsNaN(PageTextBlock.LineHeight) ? fontSize * 1.35 : PageTextBlock.LineHeight;
        var typeface = new Typeface(PageTextBlock.FontFamily, PageTextBlock.FontStyle, PageTextBlock.FontWeight, PageTextBlock.FontStretch);
        string text = _currentText;

        _paginationCancellation?.Cancel();
        _paginationCancellation?.Dispose();
        _paginationCancellation = new CancellationTokenSource();
        CancellationToken token = _paginationCancellation.Token;

        bool gateEntered = false;
        try
        {
            await _paginationGate.WaitAsync(token);
            gateEntered = true;
            List<TextPage> calculatedPages = await Task.Run(
                () => TextPaginator.Paginate(text, availableWidth, availableHeight, typeface, fontSize, lineHeight, token),
                token);

            if (token.IsCancellationRequested || generation != _paginationGeneration || !IsPageMode) return;
            _pages.Clear();
            _pages.AddRange(calculatedPages.Select(page => new PageSlice(page.Start, page.Length)));
            if (_pendingRestorePosition is int restorePosition)
            {
                _currentPageIndex = FindPageContaining(Math.Clamp(restorePosition, 0, Math.Max(0, _currentText.Length - 1)));
                _pendingRestorePosition = null;
                _pendingRestoreProgress = null;
            }
            else if (_pendingRestoreProgress is double restoreProgress)
            {
                _currentPageIndex = restoreProgress <= 0
                    ? 0
                    : Math.Clamp((int)Math.Ceiling(restoreProgress / 100d * _pages.Count) - 1, 0, _pages.Count - 1);
                _pendingRestoreProgress = null;
            }
            else
            {
                _currentPageIndex = FindPageContaining(preservedCharacterIndex);
            }
            RenderCurrentPage();
            if (BookmarksPanel.Visibility == Visibility.Visible) RefreshBookmarkList();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (gateEntered) _paginationGate.Release();
        }
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e) => ShowPage(_currentPageIndex - 1);

    private void NextPage_Click(object sender, RoutedEventArgs e) => ShowPage(_currentPageIndex + 1);

    private void ShowPage(int index)
    {
        if (_pages.Count == 0) return;
        if (index >= _pages.Count && _currentPageIndex == _pages.Count - 1)
        {
            ShowLibrary();
            return;
        }
        _currentPageIndex = Math.Clamp(index, 0, _pages.Count - 1);
        RenderCurrentPage();
        RecordReadingProgress(100d * (_currentPageIndex + 1) / _pages.Count, _pages[_currentPageIndex].Start);
    }

    private void RenderCurrentPage()
    {
        if (_pages.Count == 0) return;
        PageSlice page = _pages[_currentPageIndex];
        string pageText = _currentText.Substring(page.Start, page.Length);
        PageTextBlock.Inlines.Clear();

        string query = SearchTextBox.Text;
        if (_searchIndex >= page.Start && _searchIndex < page.Start + page.Length && !string.IsNullOrEmpty(query))
        {
            int localStart = _searchIndex - page.Start;
            int highlightLength = Math.Min(query.Length, pageText.Length - localStart);
            PageTextBlock.Inlines.Add(new Run(pageText[..localStart]));
            PageTextBlock.Inlines.Add(new Run(pageText.Substring(localStart, highlightLength))
            {
                Background = MediaBrushes.Gold,
                Foreground = MediaBrushes.Black
            });
            PageTextBlock.Inlines.Add(new Run(pageText[(localStart + highlightLength)..]));
        }
        else
        {
            PageTextBlock.Text = pageText;
        }

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

        if (!string.Equals(query, _lastSearchQuery, StringComparison.CurrentCulture) || _searchMatches.Count == 0)
        {
            _lastSearchQuery = query;
            BuildSearchMatches(query);
            _searchMatchListIndex = forward ? -1 : 0;
        }

        if (_searchMatches.Count == 0)
        {
            SearchResultText.Text = "일치하는 문자가 없습니다.";
            return;
        }

        _searchMatchListIndex = forward
            ? (_searchMatchListIndex + 1) % _searchMatches.Count
            : (_searchMatchListIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        _searchIndex = _searchMatches[_searchMatchListIndex];

        SearchResultText.Text = $"{_searchMatchListIndex + 1:N0} / {_searchMatches.Count:N0} · 문자 위치 {_searchIndex + 1:N0}";
        if (IsPageMode)
        {
            _currentPageIndex = FindPageContaining(_searchIndex);
            RenderCurrentPage();
            RecordReadingProgress(100d * (_currentPageIndex + 1) / Math.Max(1, _pages.Count), _searchIndex);
        }
        else
        {
            ContentTextBox.Focus();
            ContentTextBox.Select(_searchIndex, query.Length);
            int visualLine = ContentTextBox.GetLineIndexFromCharacterIndex(_searchIndex);
            ContentTextBox.ScrollToLine(Math.Max(0, visualLine - 2));
        }
    }

    private void BuildSearchMatches(string query)
    {
        _searchMatches.Clear();
        _searchMatchListIndex = -1;
        _searchMatches.AddRange(TextSearchService.FindAll(_currentText, query,
            SearchCaseSensitiveCheckBox.IsChecked == true, SearchWholeWordCheckBox.IsChecked == true));
    }

    private void CurrentPositionButton_Click(object sender, RoutedEventArgs e)
    {
        PositionInputTextBox.Text = Math.Max(1, _currentPositionValue).ToString();
        CurrentPositionButton.Visibility = Visibility.Collapsed;
        PositionInputTextBox.Visibility = Visibility.Visible;
        PositionInputTextBox.Focus();
        PositionInputTextBox.SelectAll();
    }

    private void PositionInputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            MoveToTarget(showValidationMessage: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndPositionEditing();
            e.Handled = true;
        }
    }

    private void PositionInputTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_isCommittingPosition && PositionInputTextBox.Visibility == Visibility.Visible)
        {
            MoveToTarget(showValidationMessage: false);
        }
    }

    private void EndPositionEditing()
    {
        PositionInputTextBox.Visibility = Visibility.Collapsed;
        CurrentPositionButton.Visibility = Visibility.Visible;
    }

    private void MoveToTarget(bool showValidationMessage)
    {
        if (_isCommittingPosition) return;

        string unitName = IsPageMode ? "페이지" : "줄";
        if (!int.TryParse(PositionInputTextBox.Text, out int target) || target < 1 || target > _totalPositionValue)
        {
            if (showValidationMessage)
            {
                _isCommittingPosition = true;
                ModernDialog.Show(this, $"{unitName} 번호는 1부터 {_totalPositionValue:N0}까지 입력할 수 있습니다.",
                    "이동", MessageBoxButton.OK, MessageBoxImage.Information);
                _isCommittingPosition = false;
                Dispatcher.BeginInvoke(() =>
                {
                    PositionInputTextBox.Focus();
                    PositionInputTextBox.SelectAll();
                }, DispatcherPriority.Input);
            }
            else
            {
                EndPositionEditing();
            }
            return;
        }

        _isCommittingPosition = true;
        EndPositionEditing();
        try
        {
            if (IsPageMode)
            {
                ShowPage(target - 1);
            }
            else
            {
                int characterIndex = FindLogicalLineStart(target);
                if (characterIndex < 0) return;
                ContentTextBox.Focus();
                ContentTextBox.Select(characterIndex, 0);
                int visualLine = ContentTextBox.GetLineIndexFromCharacterIndex(characterIndex);
                ContentTextBox.ScrollToLine(Math.Max(0, visualLine - 2));
            }
        }
        finally
        {
            _isCommittingPosition = false;
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
        if (!IsPageMode && e.VerticalChange != 0)
        {
            if (_isRestoringProgress) return;
            double scrollableHeight = Math.Max(0, e.ExtentHeight - e.ViewportHeight);
            _pendingScrollProgress = scrollableHeight <= 0 ? 100 : 100d * e.VerticalOffset / scrollableHeight;
            if (!_scrollUiTimer.IsEnabled) _scrollUiTimer.Start();
        }
    }

    private void RestoreScrollProgress()
    {
        if (IsPageMode || string.IsNullOrEmpty(_currentText)) return;
        double progress = _pendingRestoreProgress ?? 0;
        _pendingRestoreProgress = null;
        _isRestoringProgress = true;
        ContentTextBox.UpdateLayout();
        int targetVisualLine;
        if (_pendingRestorePosition is int restorePosition)
        {
            targetVisualLine = ContentTextBox.GetLineIndexFromCharacterIndex(Math.Clamp(restorePosition, 0, Math.Max(0, _currentText.Length - 1)));
            _pendingRestorePosition = null;
        }
        else
        {
            int lineCount = Math.Max(1, ContentTextBox.LineCount);
            targetVisualLine = Math.Clamp((int)Math.Round((lineCount - 1) * progress / 100d), 0, lineCount - 1);
        }
        ContentTextBox.ScrollToLine(targetVisualLine);
        Dispatcher.BeginInvoke(() =>
        {
            _isRestoringProgress = false;
            UpdateCurrentPosition();
        }, DispatcherPriority.Background);
    }

    private void UpdateCurrentPosition()
    {
        if (IsPageMode)
        {
            SetPositionDisplay(_pages.Count == 0 ? 0 : _currentPageIndex + 1, _pages.Count);
            UpdateBookmarkUi();
            return;
        }

        if (_lineStarts.Count == 0)
        {
            SetPositionDisplay(0, 0);
            UpdateBookmarkUi();
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
        SetPositionDisplay(Math.Max(0, lineIndex) + 1, _lineStarts.Count);
        UpdateBookmarkUi();
    }

    private void SetPositionDisplay(int current, int total)
    {
        _currentPositionValue = current;
        _totalPositionValue = total;
        CurrentPositionButton.Content = current.ToString("N0");
        TotalPositionText.Text = total.ToString("N0");
    }

    private int GetCurrentCharacterPosition()
    {
        if (IsPageMode && _pages.Count > 0) return _pages[Math.Clamp(_currentPageIndex, 0, _pages.Count - 1)].Start;
        int firstVisibleLine = ContentTextBox.GetFirstVisibleLineIndex();
        int position = firstVisibleLine >= 0 ? ContentTextBox.GetCharacterIndexFromLineIndex(firstVisibleLine) : ContentTextBox.SelectionStart;
        return Math.Clamp(position, 0, Math.Max(0, _currentText.Length - 1));
    }

    private void RecordReadingProgress(double progress, int? readPosition = null)
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath)) return;
        progress = Math.Clamp(progress, 0, 100);
        int position = readPosition ?? GetCurrentCharacterPosition();
        if (Math.Abs(progress - _currentReadProgress) < 0.05 && position == _pendingReadPosition &&
            _pendingBookmarks.SequenceEqual(_bookmarks)) return;
        _currentReadProgress = progress;
        _pendingReadPosition = position;

        _pendingProgressFilePath = _currentFilePath;
        _pendingProgressValue = progress;
        _pendingBookmarks = _bookmarks.ToArray();
        _readingProgressSaveTimer.Stop();
        _readingProgressSaveTimer.Start();
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
        UpdateThemeModeSelection();
        ApplyLibraryTheme(background.Color);
        ApplyTitleBarTheme(background.Color);
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        MarginTopSlider.Value = Math.Clamp(_viewerSettings.MarginTop, 0, 120);
        MarginBottomSlider.Value = Math.Clamp(_viewerSettings.MarginBottom, 0, 120);
        MarginLeftSlider.Value = Math.Clamp(_viewerSettings.MarginLeft, 0, 120);
        MarginRightSlider.Value = Math.Clamp(_viewerSettings.MarginRight, 0, 120);
        FontSizeSlider.Value = Math.Clamp(_viewerSettings.FontSize, 10, 36);
        LineSpacingSlider.Value = Math.Clamp(_viewerSettings.LineSpacing, 1.0, 2.2);
        FontFamilyComboBox.SelectedItem = _viewerSettings.FontFamily;
        BoldToggle.IsChecked = _viewerSettings.Bold;
        ItalicToggle.IsChecked = _viewerSettings.Italic;
        _isInitializing = wasInitializing;
        ApplyReaderLayout();
    }

    private void UpdateThemeModeSelection()
    {
        bool white = string.Equals(_viewerSettings.BackgroundColor, "#FFFFFF", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(_viewerSettings.TextColor, "#111827", StringComparison.OrdinalIgnoreCase);
        bool dark = string.Equals(_viewerSettings.BackgroundColor, "#111827", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_viewerSettings.TextColor, "#E5E7EB", StringComparison.OrdinalIgnoreCase);
        WhiteModeToggle.IsChecked = white;
        DarkModeToggle.IsChecked = dark;
        WhiteModeToggle.Content = white ? "✓ 화이트 모드" : "화이트 모드";
        DarkModeToggle.Content = dark ? "✓ 다크 모드" : "다크 모드";
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
        FontPreviewText.FontFamily = readerFont;
        ContentTextBox.FontWeight = _viewerSettings.Bold ? FontWeights.Bold : FontWeights.Normal;
        PageTextBlock.FontWeight = _viewerSettings.Bold ? FontWeights.Bold : FontWeights.Normal;
        FontPreviewText.FontWeight = _viewerSettings.Bold ? FontWeights.Bold : FontWeights.Normal;
        ContentTextBox.FontStyle = _viewerSettings.Italic ? FontStyles.Italic : FontStyles.Normal;
        PageTextBlock.FontStyle = _viewerSettings.Italic ? FontStyles.Italic : FontStyles.Normal;
        FontPreviewText.FontStyle = _viewerSettings.Italic ? FontStyles.Italic : FontStyles.Normal;
        double lineHeight = fontSize * Math.Clamp(_viewerSettings.LineSpacing, 1.0, 2.2);
        TextBlock.SetLineHeight(ContentTextBox, lineHeight);
        PageTextBlock.LineHeight = lineHeight;
        MarginTopValueText.Text = $"{top:0}";
        MarginBottomValueText.Text = $"{bottom:0}";
        MarginLeftValueText.Text = $"{left:0}";
        MarginRightValueText.Text = $"{right:0}";
        FontSizeValueText.Text = $"{fontSize:0}";
        LineSpacingValueText.Text = $"{_viewerSettings.LineSpacing:0.00}";
    }

    private void ApplyLibraryTheme(MediaColor viewerBackground)
    {
        bool dark = (0.2126 * viewerBackground.R + 0.7152 * viewerBackground.G + 0.0722 * viewerBackground.B) / 255 < 0.5;
        SetThemeBrush("WindowBackgroundBrush", dark ? "#0B0F14" : "#F5F7FB");
        SetThemeBrush("PanelBrush", dark ? "#151B24" : "#FFFFFF");
        SetThemeBrush("PrimaryTextBrush", dark ? "#F4F7FB" : "#101828");
        SetThemeBrush("MutedTextBrush", dark ? "#98A2B3" : "#667085");
        SetThemeBrush("BorderBrush", dark ? "#293241" : "#E4E7EC");
        SetThemeBrush("SecondaryButtonBrush", dark ? "#222B38" : "#E5E7EB");
        SetThemeBrush("SecondaryButtonTextBrush", dark ? "#E5E7EB" : "#344054");
        SetThemeBrush("ItemHoverBrush", dark ? "#1C2532" : "#F2F4F7");
        SetThemeBrush("ItemSelectedBrush", dark ? "#1D3454" : "#EAF2FF");
        SetThemeBrush("ItemSelectedBorderBrush", dark ? "#3B82F6" : "#93B4F5");
        SetThemeBrush("ScrollThumbBrush", dark ? "#4B5565" : "#AAB4C3");
        SetThemeBrush("ScrollThumbHoverBrush", dark ? "#718096" : "#667085");
        SetThemeBrush("InputBackgroundBrush", dark ? "#0F141C" : "#FFFFFF");
        SetThemeBrush("InputTextBrush", dark ? "#F4F7FB" : "#101828");
        SetThemeBrush("InputBorderBrush", dark ? "#344054" : "#CBD5E1");
        SetThemeBrush("InputHoverBrush", dark ? "#667085" : "#94A3B8");
        SetThemeBrush("InputTrackBrush", dark ? "#344054" : "#E2E8F0");
        SetThemeBrush("InputPlaceholderBrush", dark ? "#7F8B9D" : "#98A2B3");
        SetThemeBrush("ToggleBackgroundBrush", dark ? "#222B38" : "#F1F5F9");
        SetThemeBrush("ToggleTextBrush", dark ? "#E5E7EB" : "#344054");
        SetThemeBrush("ToggleCheckedBackgroundBrush", dark ? "#312E81" : "#EEF2FF");
        SetThemeBrush("ToggleCheckedTextBrush", dark ? "#E0E7FF" : "#4338CA");
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
        catch (IOException ex) { ShowStorageWarningOnce(ex); }
        catch (UnauthorizedAccessException ex) { ShowStorageWarningOnce(ex); }
    }

    private void ShowStorageWarningOnce(Exception exception)
    {
        if (_storageWarningShown) return;
        _storageWarningShown = true;
        ModernDialog.Show(this, $"EXE 폴더에 설정이나 기록을 저장하지 못했습니다.\n\n{exception.Message}",
            "저장 권한 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        _repaginationTimer.Stop();
        _readerLayoutApplyTimer.Stop();
        _scrollUiTimer.Stop();
        _windowSizeSaveTimer.Stop();
        _readingProgressSaveTimer.Stop();
        if (!string.IsNullOrWhiteSpace(_pendingProgressFilePath))
        {
            try
            {
                Task.Run(() => HistoryStore.UpdateReadingStateAsync(
                        _pendingProgressFilePath, _pendingProgressValue, _pendingReadPosition, _pendingBookmarks))
                    .GetAwaiter().GetResult();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
        string? selectedPath = (RecentFilesList.SelectedItem as RecentFileEntry)?.FilePath;
        _recentFiles.Clear();
        foreach (RecentFileEntry entry in entries.OrderByDescending(item => item.IsPinned).ThenByDescending(item => item.LastOpenedAt))
            _recentFiles.Add(entry);
        ICollectionView? view = CollectionViewSource.GetDefaultView(RecentFilesList.ItemsSource);
        view?.Refresh();
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            RecentFileEntry? restoredSelection = _recentFiles.FirstOrDefault(entry =>
                string.Equals(entry.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (restoredSelection is not null && (view?.Contains(restoredSelection) ?? true))
            {
                RecentFilesList.SelectedItem = restoredSelection;
                RecentFilesList.ScrollIntoView(restoredSelection);
            }
        }
        UpdateRecentEmptyState(view, RecentFilterTextBox.Text.Trim());
    }

    private void RecentFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (RecentFilesList.ItemsSource is null) return;
        ICollectionView view = CollectionViewSource.GetDefaultView(RecentFilesList.ItemsSource);
        string query = RecentFilterTextBox.Text.Trim();
        view.Filter = item => item is RecentFileEntry entry &&
            (query.Length == 0 || entry.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             entry.Location.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        view.Refresh();
        if (RecentFilesList.SelectedItem is RecentFileEntry selected && !view.Contains(selected))
            RecentFilesList.SelectedItem = null;
        UpdateRecentEmptyState(view, query);
    }

    private void UpdateRecentEmptyState(ICollectionView? view, string query)
    {
        bool hasHistory = _recentFiles.Count > 0;
        bool hasNoFilterResults = hasHistory && query.Length > 0 && (view?.IsEmpty ?? true);
        EmptyHistoryPanel.Visibility = hasHistory ? Visibility.Collapsed : Visibility.Visible;
        NoFilterResultsPanel.Visibility = hasNoFilterResults ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearRecentFilter_Click(object sender, RoutedEventArgs e)
    {
        RecentFilterTextBox.Clear();
        RecentFilterTextBox.Focus();
    }

    private async void RecentFilterTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && RecentFilterTextBox.Text.Length > 0)
        {
            RecentFilterTextBox.Clear();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;
        ICollectionView view = CollectionViewSource.GetDefaultView(RecentFilesList.ItemsSource);
        RecentFileEntry? firstMatch = view.Cast<object>().OfType<RecentFileEntry>().FirstOrDefault();
        if (firstMatch is null) return;
        RecentFilesList.SelectedItem = firstMatch;
        e.Handled = true;
        await OpenSelectedFileAsync();
    }

    private async void PinSelected_Click(object sender, RoutedEventArgs e)
    {
        if (RecentFilesList.SelectedItem is not RecentFileEntry selected) return;
        try { SetRecentFiles(await HistoryStore.SetPinnedAsync(selected.FilePath, !selected.IsPinned)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowHistoryError(ex); }
    }

    private void PreviousPageArea_Click(object sender, MouseButtonEventArgs e) => ShowPage(_currentPageIndex - 1);
    private void NextPageArea_Click(object sender, MouseButtonEventArgs e) => ShowPage(_currentPageIndex + 1);

    private void ToggleBookmark_Click(object sender, RoutedEventArgs e) => ToggleBookmark();

    private void ToggleBookmark()
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath)) return;
        int position = GetCurrentCharacterPosition();
        int existing = _bookmarks.FindIndex(saved => Math.Abs(saved - position) <= 2);
        if (existing >= 0) _bookmarks.RemoveAt(existing);
        else _bookmarks.Add(position);
        _bookmarks.Sort();
        RecordReadingProgress(_currentReadProgress, position);
        RefreshBookmarkList();
        UpdateBookmarkUi();
    }

    private void UpdateBookmarkUi()
    {
        bool isBookmarked = !string.IsNullOrWhiteSpace(_currentFilePath) &&
                            _bookmarks.Any(position => Math.Abs(position - GetCurrentCharacterPosition()) <= 2);
        BookmarkButton.Content = isBookmarked ? "★" : "☆";
        BookmarkButton.Foreground = isBookmarked
            ? new SolidColorBrush(MediaColor.FromRgb(251, 191, 36))
            : MediaBrushes.White;
        BookmarkButton.ToolTip = isBookmarked
            ? "현재 위치 책갈피 제거 (Ctrl+B)"
            : "현재 위치 책갈피 추가 (Ctrl+B)";
    }

    private void RefreshBookmarkList()
    {
        int? selectedPosition = (BookmarkListBox.SelectedItem as BookmarkListItem)?.Position;
        _bookmarkItems.Clear();
        foreach (int position in _bookmarks.OrderBy(value => value))
        {
            int lineNumber = GetLogicalLineNumber(position);
            string pagePart = IsPageMode && _pages.Count > 0 ? $"페이지 {FindPageContaining(position) + 1:N0} · " : string.Empty;
            _bookmarkItems.Add(new BookmarkListItem(position,
                $"{pagePart}줄 {lineNumber:N0} · 문자 {position + 1:N0}", GetBookmarkPreview(position)));
        }

        BookmarkCountText.Text = $"{_bookmarkItems.Count:N0}개 저장됨";
        EmptyBookmarksPanel.Visibility = _bookmarkItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearBookmarksButton.IsEnabled = _bookmarkItems.Count > 0;
        int? preferredPosition = selectedPosition;
        if (!preferredPosition.HasValue && !string.IsNullOrWhiteSpace(_currentFilePath))
        {
            int currentPosition = GetCurrentCharacterPosition();
            int currentBookmarkIndex = _bookmarks.FindIndex(position => Math.Abs(position - currentPosition) <= 2);
            if (currentBookmarkIndex >= 0) preferredPosition = _bookmarks[currentBookmarkIndex];
        }
        if (preferredPosition.HasValue)
            BookmarkListBox.SelectedItem = _bookmarkItems.FirstOrDefault(item => item.Position == preferredPosition.Value);
        UpdateBookmarkSelectionButtons();
    }

    private int GetLogicalLineNumber(int characterPosition)
    {
        if (_lineStarts.Count == 0) return 1;
        int index = _lineStarts.BinarySearch(Math.Clamp(characterPosition, 0, Math.Max(0, _currentText.Length - 1)));
        if (index < 0) index = Math.Max(0, ~index - 1);
        return index + 1;
    }

    private string GetBookmarkPreview(int characterPosition)
    {
        if (string.IsNullOrEmpty(_currentText)) return "본문 미리보기가 없습니다.";
        int position = Math.Clamp(characterPosition, 0, _currentText.Length - 1);
        int lineIndex = _lineStarts.BinarySearch(position);
        if (lineIndex < 0) lineIndex = Math.Max(0, ~lineIndex - 1);
        int start = _lineStarts.Count > 0 ? _lineStarts[lineIndex] : 0;
        int end = _currentText.IndexOf('\n', start);
        if (end < 0) end = _currentText.Length;
        string preview = string.Join(' ', _currentText[start..end]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (preview.Length == 0) preview = "빈 줄";
        return preview.Length > 90 ? preview[..90] + "…" : preview;
    }

    private void BookmarkListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBookmarkSelectionButtons();

    private void UpdateBookmarkSelectionButtons()
    {
        bool selected = BookmarkListBox.SelectedItem is BookmarkListItem;
        GoToBookmarkButton.IsEnabled = selected;
        RemoveBookmarkButton.IsEnabled = selected;
    }

    private void BookmarkListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => GoToSelectedBookmark();

    private void GoToSelectedBookmark_Click(object sender, RoutedEventArgs e) => GoToSelectedBookmark();

    private void GoToSelectedBookmark()
    {
        if (BookmarkListBox.SelectedItem is not BookmarkListItem item) return;
        NavigateToCharacter(item.Position);
        BookmarksPanel.Visibility = Visibility.Collapsed;
    }

    private void RemoveSelectedBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarkListBox.SelectedItem is not BookmarkListItem item) return;
        _bookmarks.Remove(item.Position);
        RecordReadingProgress(_currentReadProgress);
        RefreshBookmarkList();
        UpdateBookmarkUi();
    }

    private void ClearBookmarks_Click(object sender, RoutedEventArgs e)
    {
        if (_bookmarks.Count == 0) return;
        MessageBoxResult result = ModernDialog.Show(this, "현재 파일의 책갈피를 모두 삭제할까요?",
            "책갈피 전체 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        _bookmarks.Clear();
        RecordReadingProgress(_currentReadProgress);
        RefreshBookmarkList();
        UpdateBookmarkUi();
    }

    private void NextBookmark_Click(object sender, RoutedEventArgs e) => GoToNextBookmark();

    private void GoToNextBookmark()
    {
        if (_bookmarks.Count == 0) return;
        int current = GetCurrentCharacterPosition();
        int target = _bookmarks.FirstOrDefault(position => position > current);
        if (target <= current) target = _bookmarks[0];
        NavigateToCharacter(target);
    }

    private void NavigateToCharacter(int characterIndex)
    {
        characterIndex = Math.Clamp(characterIndex, 0, Math.Max(0, _currentText.Length - 1));
        if (IsPageMode)
        {
            _currentPageIndex = FindPageContaining(characterIndex);
            RenderCurrentPage();
        }
        else
        {
            ContentTextBox.Select(characterIndex, 0);
            int visualLine = ContentTextBox.GetLineIndexFromCharacterIndex(characterIndex);
            ContentTextBox.ScrollToLine(Math.Max(0, visualLine - 1));
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _savedWindowStyle = WindowStyle;
            _savedWindowState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _isFullScreen = true;
        }
        else
        {
            WindowStyle = _savedWindowStyle;
            WindowState = _savedWindowState;
            _isFullScreen = false;
        }
    }

    private void ReloadFonts_Click(object sender, RoutedEventArgs e)
    {
        string selected = NormalizeKnownFontStyleSuffix(_viewerSettings.FontFamily);
        _readerFonts.Clear();
        _readerFontPriorities.Clear();
        LoadReaderFonts();
        if (_readerFonts.ContainsKey(selected)) FontFamilyComboBox.SelectedItem = selected;
        else if (_readerFonts.Count > 0) FontFamilyComboBox.SelectedIndex = 0;
    }

    private async void ReopenWithEncoding_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath) || EncodingComboBox.SelectedValue is not string key) return;
        int position = GetCurrentCharacterPosition();
        RecordReadingProgress(_currentReadProgress, position);
        await OpenFileAsync(_currentFilePath, key);
    }

    private void AutoOpenLastFile_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _viewerSettings = _viewerSettings with { AutoOpenLastFile = AutoOpenLastFileCheckBox.IsChecked == true };
        QueueViewerSettingsSave();
    }

    private void SearchOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _viewerSettings = _viewerSettings with
        {
            SearchCaseSensitive = SearchCaseSensitiveCheckBox.IsChecked == true,
            SearchWholeWord = SearchWholeWordCheckBox.IsChecked == true
        };
        _lastSearchQuery = string.Empty;
        _searchMatches.Clear();
        SearchResultText.Text = string.Empty;
        QueueViewerSettingsSave();
    }

    private void OpenFileAssociationSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string executablePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "INTViewer.exe");
            using RegistryKey appKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\INTViewer.exe");
            appKey.SetValue("FriendlyAppName", "INTViewer");
            using RegistryKey commandKey = appKey.CreateSubKey(@"shell\open\command");
            commandKey.SetValue(string.Empty, $"\"{executablePath}\" \"%1\"");
            using RegistryKey supportedTypes = appKey.CreateSubKey("SupportedTypes");
            supportedTypes.SetValue(".txt", string.Empty);
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        {
            ModernDialog.Show(this, ex.Message, "파일 연결 설정", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath) || !File.Exists(_currentFilePath)) return;
        DateTime writeTime = File.GetLastWriteTimeUtc(_currentFilePath);
        if (writeTime == _currentLastWriteTimeUtc || writeTime == _ignoredWriteTimeUtc) return;
        _ignoredWriteTimeUtc = writeTime;
        MessageBoxResult result = ModernDialog.Show(this, "원본 파일이 변경되었습니다. 지금 다시 불러올까요?",
            "파일 변경 감지", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes) await OpenFileAsync(_currentFilePath, _currentEncodingKey);
    }

    private void ShowLibrary()
    {
        _scrollUiTimer.Stop();
        RefreshCurrentRecentEntry();
        SettingsPanel.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Collapsed;
        BookmarksPanel.Visibility = Visibility.Collapsed;
        ViewerView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
        CollectionViewSource.GetDefaultView(RecentFilesList.ItemsSource)?.Refresh();
        Title = "INTViewer";
    }

    private void RefreshCurrentRecentEntry()
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath)) return;
        for (int index = 0; index < _recentFiles.Count; index++)
        {
            RecentFileEntry entry = _recentFiles[index];
            if (!string.Equals(entry.FilePath, _currentFilePath, StringComparison.OrdinalIgnoreCase)) continue;
            _recentFiles[index] = entry with
            {
                ReadProgress = _currentReadProgress,
                ReadPosition = _pendingReadPosition,
                BookmarkPositions = _bookmarks.ToArray()
            };
            break;
        }
    }

    private void ShowViewer()
    {
        LibraryView.Visibility = Visibility.Collapsed;
        UpdateViewMode();
        ViewerView.Visibility = Visibility.Visible;
    }

    private void ShowHistoryError(Exception exception) => ModernDialog.Show(this, exception.Message,
        "기록 저장 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
}
