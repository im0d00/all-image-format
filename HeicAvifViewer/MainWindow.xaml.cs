using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace HeicAvifViewer;

/// <summary>
/// View-model for a single entry in the thumbnail strip.
/// Implements <see cref="INotifyPropertyChanged"/> so the binding updates
/// automatically when the thumbnail finishes loading in the background.
/// </summary>
public sealed class ThumbnailItem : INotifyPropertyChanged
{
    public string FilePath { get; init; } = string.Empty;

    private WriteableBitmap? _thumbnail;
    public WriteableBitmap? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    private bool _isLoading = true;
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>
/// Main application window.
///
/// Responsibilities
/// ─────────────────
/// • Extend the title bar into the client area for a borderless / Mica feel.
/// • Handle file and folder opening (picker + drag-and-drop).
/// • Navigate through images (prev/next, keyboard, thumbnail-click).
/// • Lazy-load thumbnails — only visible tiles trigger a Magick.NET decode,
///   preventing memory exhaustion in folders with hundreds of HEIC/AVIF files.
/// • Show a loading overlay (ProgressRing) while a full image is decoding,
///   giving visual feedback for large AVIF files.
/// • Toggle the slide-out <see cref="ExifMetadataPanel"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    // ─────────────────────────────────────────────────────────────────────────
    // Constants / config
    // ─────────────────────────────────────────────────────────────────────────

    // Max thumbnails decoded concurrently to avoid pegging the CPU on open.
    private const int MaxConcurrentThumbnails = 4;

    // Batch size for progressive thumbnail loading.
    private const int ThumbnailBatchSize = 20;

    // ─────────────────────────────────────────────────────────────────────────
    // State
    // ─────────────────────────────────────────────────────────────────────────

    private readonly ObservableCollection<ThumbnailItem> _thumbnails = [];
    private List<string> _folderFiles = [];
    private int _currentIndex = -1;

    // Cancellation for the in-flight full-resolution image load.
    private CancellationTokenSource? _imageCts;

    // Semaphore that limits parallel thumbnail decodes.
    private readonly SemaphoreSlim _thumbSemaphore = new(MaxConcurrentThumbnails);

    // Tracks whether thumbnail background loading is still in progress.
    private CancellationTokenSource? _thumbBatchCts;

    // ─────────────────────────────────────────────────────────────────────────
    // Construction / initialisation
    // ─────────────────────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();

        ThumbnailList.ItemsSource = _thumbnails;

        SetupTitleBar();
        SetupKeyboardShortcuts();
    }

    /// <summary>Called by <see cref="App"/> when the app is file-activated.</summary>
    public void OpenFileOnLaunch(string filePath)
        => _ = OpenFileAsync(filePath);

    // ─────────────────────────────────────────────────────────────────────────
    // Title bar — extend into client area for a borderless look
    // ─────────────────────────────────────────────────────────────────────────

    private void SetupTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        var appWindow = GetAppWindow();
        if (appWindow?.TitleBar is { } tb)
        {
            tb.ExtendsContentIntoTitleBar = true;
            // Reserve space for the system caption buttons (close/min/max).
            tb.SetDragRectangles([new Windows.Graphics.RectInt32(
                0, 0, (int)TitleBar.ActualWidth, 48)]);
        }
    }

    private AppWindow? GetAppWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var winId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(winId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Keyboard shortcuts
    // ─────────────────────────────────────────────────────────────────────────

    private void SetupKeyboardShortcuts()
    {
        // We attach to the CoreWindow keyboard events via the XAML root.
        RootGrid.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Left:
                    NavigatePrev();
                    break;
                case Windows.System.VirtualKey.Right:
                    NavigateNext();
                    break;
                case Windows.System.VirtualKey.Add:
                case Windows.System.VirtualKey.OEM_Plus:
                    ZoomIn();
                    break;
                case Windows.System.VirtualKey.Subtract:
                case Windows.System.VirtualKey.OEM_Minus:
                    ZoomOut();
                    break;
                case Windows.System.VirtualKey.F:
                    FitToWindow();
                    break;
                case Windows.System.VirtualKey.M:
                    MetadataPanelToggle.IsChecked = !MetadataPanelToggle.IsChecked;
                    break;
            }
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Open file / folder
    // ─────────────────────────────────────────────────────────────────────────

    private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        foreach (var ext in ImageLoader.SupportedExtensions)
            picker.FileTypeFilter.Add(ext);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            await OpenFileAsync(file.Path);
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            await OpenFolderAsync(folder.Path);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Drag and drop
    // ─────────────────────────────────────────────────────────────────────────

    private void ImageArea_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void ImageArea_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        var first = items.OfType<StorageFile>().FirstOrDefault();
        if (first is not null &&
            ImageLoader.SupportedExtensions.Contains(Path.GetExtension(first.Path)))
        {
            await OpenFileAsync(first.Path);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Core: open a single file
    // ─────────────────────────────────────────────────────────────────────────

    private async Task OpenFileAsync(string filePath)
    {
        // If this file is already in the loaded folder list, just navigate to
        // that position.  Otherwise set up a single-file view.
        int existingIdx = _folderFiles.IndexOf(filePath);
        if (existingIdx >= 0)
        {
            await NavigateToIndexAsync(existingIdx);
            return;
        }

        // Set up a folder context from the file's directory so the user can
        // navigate siblings with arrow keys / thumbnail strip.
        var dir = Path.GetDirectoryName(filePath);
        if (dir is null) return;
        await OpenFolderAsync(dir, filePath);
    }

    private async Task OpenFolderAsync(string folderPath, string? initialFile = null)
    {
        // Cancel any outstanding thumbnail batch.
        _thumbBatchCts?.Cancel();
        _thumbBatchCts = new CancellationTokenSource();

        // Collect all supported files in the folder, sorted by name.
        _folderFiles = Directory
            .EnumerateFiles(folderPath)
            .Where(f => ImageLoader.SupportedExtensions.Contains(
                Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_folderFiles.Count == 0) return;

        // Build thumbnail items (no bitmaps yet — lazy loaded below).
        _thumbnails.Clear();
        foreach (var f in _folderFiles)
            _thumbnails.Add(new ThumbnailItem { FilePath = f });

        ThumbnailStrip.Visibility = Visibility.Visible;

        // Navigate to the initial file or the first one.
        int startIdx = initialFile is not null
            ? Math.Max(0, _folderFiles.IndexOf(initialFile))
            : 0;

        await NavigateToIndexAsync(startIdx);

        // Start loading thumbnails in the background — in batches so the UI
        // stays responsive even for folders with 500+ images.
        _ = LoadThumbnailsBatchAsync(_thumbBatchCts.Token);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Navigation
    // ─────────────────────────────────────────────────────────────────────────

    private void PrevButton_Click(object sender, RoutedEventArgs e) => NavigatePrev();
    private void NextButton_Click(object sender, RoutedEventArgs e) => NavigateNext();

    private void NavigatePrev()
    {
        if (_currentIndex > 0)
            _ = NavigateToIndexAsync(_currentIndex - 1);
    }

    private void NavigateNext()
    {
        if (_currentIndex < _folderFiles.Count - 1)
            _ = NavigateToIndexAsync(_currentIndex + 1);
    }

    private async void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbnailList.SelectedIndex >= 0 &&
            ThumbnailList.SelectedIndex != _currentIndex)
        {
            await NavigateToIndexAsync(ThumbnailList.SelectedIndex);
        }
    }

    private async Task NavigateToIndexAsync(int index)
    {
        if (index < 0 || index >= _folderFiles.Count) return;

        _currentIndex = index;

        // Sync thumbnail strip selection without re-triggering SelectionChanged.
        if (ThumbnailList.SelectedIndex != index)
        {
            ThumbnailList.SelectedIndex = index;
            ThumbnailList.ScrollIntoView(ThumbnailList.SelectedItem);
        }

        UpdateNavigationButtons();

        var filePath = _folderFiles[index];
        await LoadFullImageAsync(filePath);

        // If the metadata panel is open, refresh it for the new file.
        if (MetadataPanel.IsOpen)
            await MetadataPanel.LoadMetadataAsync(filePath);
    }

    private void UpdateNavigationButtons()
    {
        PrevButton.Visibility = _currentIndex > 0
            ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _currentIndex < _folderFiles.Count - 1
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Full-resolution image loading
    // ─────────────────────────────────────────────────────────────────────────

    private async Task LoadFullImageAsync(string filePath)
    {
        // Cancel any previously in-flight decode.
        _imageCts?.Cancel();
        _imageCts = new CancellationTokenSource();
        var ct = _imageCts.Token;

        ShowLoading(Path.GetFileName(filePath));

        try
        {
            var bitmap = await ImageLoader.LoadImageAsync(filePath, ct);

            ct.ThrowIfCancellationRequested();

            MainImage.Source = bitmap;
            ImageScrollViewer.Visibility = Visibility.Visible;
            DropHint.Visibility = Visibility.Collapsed;

            FitToWindow();
            UpdateStatusBar(filePath, bitmap.PixelWidth, bitmap.PixelHeight);
        }
        catch (OperationCanceledException)
        {
            // A newer image was requested — silently discard.
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            HideLoading();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lazy thumbnail loading
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads thumbnails in batches so the UI is never blocked and a
    /// CancellationToken allows the job to be abandoned when the folder changes.
    /// </summary>
    private async Task LoadThumbnailsBatchAsync(CancellationToken ct)
    {
        ThumbnailProgress.Visibility = Visibility.Visible;

        try
        {
            // Process thumbnails in small batches to let the UI breathe between
            // groups, prioritising items near the current view position.
            for (int start = 0; start < _thumbnails.Count; start += ThumbnailBatchSize)
            {
                ct.ThrowIfCancellationRequested();

                int end = Math.Min(start + ThumbnailBatchSize, _thumbnails.Count);
                var batch = _thumbnails.Skip(start).Take(end - start).ToList();

                var tasks = batch.Select(item => LoadOneThumbnailAsync(item, ct));
                await Task.WhenAll(tasks);

                // Brief yield between batches to keep the UI responsive.
                await Task.Delay(10, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Folder changed — stop silently.
        }
        finally
        {
            ThumbnailProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadOneThumbnailAsync(ThumbnailItem item, CancellationToken ct)
    {
        await _thumbSemaphore.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            item.Thumbnail = await ImageLoader.LoadThumbnailAsync(item.FilePath, 104, ct);
            item.IsLoading = false;
        }
        catch (OperationCanceledException) { }
        catch
        {
            item.IsLoading = false; // Show blank rather than a spinner forever.
        }
        finally
        {
            _thumbSemaphore.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Zoom
    // ─────────────────────────────────────────────────────────────────────────

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => ZoomIn();
    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => ZoomOut();
    private void FitButton_Click(object sender, RoutedEventArgs e) => FitToWindow();

    private void ZoomIn() =>
        ImageScrollViewer.ChangeView(null, null,
            Math.Min(ImageScrollViewer.ZoomFactor * 1.25f, 32f));

    private void ZoomOut() =>
        ImageScrollViewer.ChangeView(null, null,
            Math.Max(ImageScrollViewer.ZoomFactor / 1.25f, 0.05f));

    private void FitToWindow()
    {
        if (MainImage.Source is not BitmapSource bmp) return;

        double scaleX = ImageScrollViewer.ActualWidth / bmp.PixelWidth;
        double scaleY = ImageScrollViewer.ActualHeight / bmp.PixelHeight;
        float fit = (float)Math.Min(scaleX, scaleY);
        fit = Math.Clamp(fit, 0.05f, 32f);

        ImageScrollViewer.ChangeView(null, null, fit);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Metadata panel
    // ─────────────────────────────────────────────────────────────────────────

    private async void MetadataPanelToggle_Checked(object sender, RoutedEventArgs e)
    {
        MetadataPanel.IsOpen = true;
        MetadataColumnDef.Width = new GridLength(320);

        if (_currentIndex >= 0 && _currentIndex < _folderFiles.Count)
            await MetadataPanel.LoadMetadataAsync(_folderFiles[_currentIndex]);
    }

    private void MetadataPanelToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        MetadataPanel.IsOpen = false;
        MetadataColumnDef.Width = new GridLength(0);
    }

    private void MetadataPanel_CloseRequested(object? sender, EventArgs e)
    {
        MetadataPanelToggle.IsChecked = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UI helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void ShowLoading(string filename)
    {
        LoadingText.Text = $"Loading {filename}…";
        LoadingOverlay.Visibility = Visibility.Visible;
        StatusBar.Visibility = Visibility.Collapsed;
    }

    private void HideLoading()
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private void UpdateStatusBar(string filePath, int width, int height)
    {
        FilenameText.Text = Path.GetFileName(filePath);
        DimensionsText.Text = $"{width} × {height}";
        IndexText.Text = _folderFiles.Count > 1
            ? $"{_currentIndex + 1} / {_folderFiles.Count}"
            : string.Empty;
        StatusBar.Visibility = Visibility.Visible;
    }

    private async void ShowError(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Could not open image",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
