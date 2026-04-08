using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HeicAvifViewer;

/// <summary>
/// A key/value pair used as the data model for each metadata row.
/// </summary>
public sealed class MetadataEntry
{
    public string Key { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

/// <summary>
/// Slide-out panel that displays EXIF, IPTC and basic image information
/// extracted by <see cref="ImageLoader.GetMetadataAsync"/>.
/// </summary>
public sealed partial class ExifMetadataPanel : UserControl
{
    // ── Dependency Properties ────────────────────────────────────────────────

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(
            nameof(IsOpen), typeof(bool), typeof(ExifMetadataPanel),
            new PropertyMetadata(false, OnIsOpenChanged));

    /// <summary>Controls whether the panel is visible.</summary>
    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>Raised when the user clicks the close button inside the panel.</summary>
    public event EventHandler? CloseRequested;

    // ── Private fields ───────────────────────────────────────────────────────

    private readonly ObservableCollection<MetadataEntry> _entries = [];
    private CancellationTokenSource? _cts;

    // ── Construction ─────────────────────────────────────────────────────────

    public ExifMetadataPanel()
    {
        InitializeComponent();
        MetadataItems.ItemsSource = _entries;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Asynchronously loads metadata for <paramref name="filePath"/> and
    /// populates the panel.
    /// </summary>
    public async Task LoadMetadataAsync(string filePath)
    {
        // Cancel any in-flight load.
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _entries.Clear();
        NoDataText.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;

        try
        {
            var meta = await ImageLoader.GetMetadataAsync(filePath, ct);

            ct.ThrowIfCancellationRequested();

            foreach (var (key, value) in meta)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    _entries.Add(new MetadataEntry { Key = key, Value = value });
            }

            NoDataText.Visibility = _entries.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // Silently swallow — a new image was selected.
        }
        catch (Exception ex)
        {
            _entries.Clear();
            _entries.Add(new MetadataEntry
            {
                Key = "Error",
                Value = ex.Message,
            });
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (ExifMetadataPanel)d;
        panel.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);
}
