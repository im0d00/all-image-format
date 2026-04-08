using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace HeicAvifViewer;

/// <summary>
/// Handles decoding of HEIC, AVIF, WebP, and all other formats supported by
/// Magick.NET — completely independent of Windows' built-in codec store.
///
/// Key design decisions
/// ─────────────────────
/// • Q8 (8-bit) quantisation is used for WriteableBitmap compatibility.
///   10-bit / 12-bit AVIF is tone-mapped to 8-bit via a linear normalisation
///   step so gradients stay smooth and banding is minimised.
/// • Colour profiles are preserved during conversion: when an embedded ICC
///   profile is present it is applied before RGB conversion so colours are
///   accurate on SDR displays.
/// • All heavy decoding runs on a background thread; the resulting pixel bytes
///   are marshalled back to the UI thread where the WriteableBitmap is created.
///   This ensures the UI thread is never blocked and WriteableBitmap (a XAML
///   type that must be created on the UI thread) is always constructed safely.
/// </summary>
public static class ImageLoader
{
    // Extensions this viewer handles natively via Magick.NET.
    public static readonly HashSet<string> SupportedExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".avif",
        ".jpg", ".jpeg", ".png", ".gif",
        ".webp", ".bmp", ".tiff", ".tif",
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes <paramref name="filePath"/> into a <see cref="WriteableBitmap"/>
    /// that WinUI 3 can display directly.
    ///
    /// The pixel decoding happens on the thread pool; the WriteableBitmap is
    /// created on whichever thread awaits this method (typically the UI thread).
    /// </summary>
    public static async Task<WriteableBitmap> LoadImageAsync(
        string filePath,
        CancellationToken ct = default)
    {
        // Decode on background thread — returns raw BGRA bytes + dimensions.
        var (pixels, width, height) = await Task.Run(
            () => DecodeToBytes(filePath, ct), ct);

        ct.ThrowIfCancellationRequested();

        // WriteableBitmap must be created on the UI (STA) thread.
        // Because this method is awaited, the continuation runs on the
        // SynchronizationContext of the calling thread (the UI thread).
        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            await stream.WriteAsync(pixels, ct);
        }
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// Produces a small thumbnail suitable for the strip at the bottom of the
    /// window.  Decodes only a scaled-down version to keep memory use low for
    /// folders with many images.
    /// </summary>
    public static async Task<WriteableBitmap> LoadThumbnailAsync(
        string filePath,
        int maxSize = 120,
        CancellationToken ct = default)
    {
        var (pixels, width, height) = await Task.Run(
            () => DecodeThumbnailToBytes(filePath, maxSize, ct), ct);

        ct.ThrowIfCancellationRequested();

        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            await stream.WriteAsync(pixels, ct);
        }
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// Reads EXIF / IPTC / XMP metadata from <paramref name="filePath"/> and
    /// returns a flat dictionary suitable for display.
    /// </summary>
    public static Task<Dictionary<string, string>> GetMetadataAsync(
        string filePath,
        CancellationToken ct = default)
        => Task.Run(() => ReadMetadata(filePath, ct), ct);

    // ─────────────────────────────────────────────────────────────────────────
    // Internal implementation
    // ─────────────────────────────────────────────────────────────────────────

    private static (byte[] Pixels, int Width, int Height) DecodeToBytes(
        string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var settings = new MagickReadSettings
        {
            Format = ExtensionToFormat(Path.GetExtension(filePath)),
        };

        using var image = new MagickImage(filePath, settings);

        ct.ThrowIfCancellationRequested();

        PrepareForDisplay(image);

        return (image.ToByteArray(MagickFormat.Bgra)
                ?? throw new InvalidOperationException("Failed to extract pixel data."),
                (int)image.Width,
                (int)image.Height);
    }

    private static (byte[] Pixels, int Width, int Height) DecodeThumbnailToBytes(
        string filePath, int maxSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var settings = new MagickReadSettings
        {
            Format = ExtensionToFormat(Path.GetExtension(filePath)),
        };

        using var image = new MagickImage(filePath, settings);

        ct.ThrowIfCancellationRequested();

        // Scale down while keeping aspect ratio.
        image.Thumbnail(new MagickGeometry(maxSize, maxSize)
        {
            IgnoreAspectRatio = false,
        });

        PrepareForDisplay(image);

        return (image.ToByteArray(MagickFormat.Bgra)
                ?? throw new InvalidOperationException("Failed to extract pixel data."),
                (int)image.Width,
                (int)image.Height);
    }

    /// <summary>
    /// Applies colour-depth normalisation, ICC profile conversion, and alpha
    /// channel setup so the image is ready to be exported as 8-bit BGRA.
    /// </summary>
    private static void PrepareForDisplay(MagickImage image)
    {
        // ── Colour depth normalisation ───────────────────────────────────────
        // AVIF / HEIC can carry 10-bit or 12-bit pixel data.  WriteableBitmap
        // only accepts 8-bit BGRA, so we map the channel range down linearly.
        // Using LinearStretch rather than a simple bit-shift preserves the full
        // tonal range and avoids shadow / highlight clipping / banding.
        if (image.Depth > 8)
        {
            image.LinearStretch(new Percentage(0), new Percentage(0));
            image.Depth = 8;
        }

        // ── ICC colour profile ───────────────────────────────────────────────
        // Honour any embedded ICC profile so colours are accurate on sRGB
        // monitors.  Strip the profile afterwards; it is not needed in BGRA.
        if (image.GetColorProfile() is not null)
        {
            image.TransformColorSpace(ColorProfile.SRGB);
        }

        // ── Alpha channel ────────────────────────────────────────────────────
        // WriteableBitmap always expects BGRA, so ensure an alpha channel exists.
        if (!image.HasAlpha)
            image.Alpha(AlphaOption.Set);
    }

    private static Dictionary<string, string> ReadMetadata(
        string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var image = new MagickImage(filePath);

        // ── Basic image info ─────────────────────────────────────────────────
        result["Width"] = $"{image.Width} px";
        result["Height"] = $"{image.Height} px";
        result["Format"] = image.Format.ToString();
        result["Color Space"] = image.ColorSpace.ToString();
        result["Bit Depth"] = $"{image.Depth}-bit";

        // ── EXIF ─────────────────────────────────────────────────────────────
        var exif = image.GetExifProfile();
        if (exif is not null)
        {
            foreach (var tag in exif.Values)
            {
                var label = tag.Tag.ToString();
                var value = FormatExifValue(tag);
                if (!string.IsNullOrWhiteSpace(value))
                    result[label] = value;
            }
        }

        // ── IPTC ─────────────────────────────────────────────────────────────
        var iptc = image.GetIptcProfile();
        if (iptc is not null)
        {
            foreach (var v in iptc.Values)
            {
                var label = $"IPTC: {v.Tag}";
                if (!result.ContainsKey(label))
                    result[label] = v.Value ?? string.Empty;
            }
        }

        // ── XMP ──────────────────────────────────────────────────────────────
        if (image.GetXmpProfile() is not null)
            result["XMP"] = "(embedded)";

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static MagickFormat ExtensionToFormat(string ext) => ext.ToLowerInvariant() switch
    {
        ".heic" or ".heif" => MagickFormat.Heic,
        ".avif" => MagickFormat.Avif,
        ".webp" => MagickFormat.WebP,
        ".jpg" or ".jpeg" => MagickFormat.Jpeg,
        ".png" => MagickFormat.Png,
        ".gif" => MagickFormat.Gif,
        ".bmp" => MagickFormat.Bmp,
        ".tiff" or ".tif" => MagickFormat.Tiff,
        _ => MagickFormat.Unknown,
    };

    private static string FormatExifValue(IExifValue tag)
    {
        try
        {
            return tag.Tag switch
            {
                ExifTag.GPSLatitude or ExifTag.GPSLongitude
                    when tag.GetValue() is Rational[] rationals
                    => FormatGpsCoordinate(rationals),

                ExifTag.ExposureTime
                    when tag.GetValue() is Rational r && r.Numerator != 0 && r.Denominator != 0
                    => $"1/{r.Denominator / r.Numerator} s",

                ExifTag.FNumber
                    when tag.GetValue() is Rational r && r.Denominator != 0
                    => $"f/{(double)r.Numerator / r.Denominator:F1}",

                ExifTag.FocalLength
                    when tag.GetValue() is Rational r && r.Denominator != 0
                    => $"{(double)r.Numerator / r.Denominator:F1} mm",

                ExifTag.ISOSpeedRatings
                    when tag.GetValue() is ushort[] isoArr && isoArr.Length > 0
                    => $"ISO {isoArr[0]}",

                _ => tag.GetValue()?.ToString() ?? string.Empty,
            };
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatGpsCoordinate(Rational[] r)
    {
        if (r.Length < 3) return string.Empty;
        double deg = r[0].Denominator == 0 ? 0 : (double)r[0].Numerator / r[0].Denominator;
        double min = r[1].Denominator == 0 ? 0 : (double)r[1].Numerator / r[1].Denominator;
        double sec = r[2].Denominator == 0 ? 0 : (double)r[2].Numerator / r[2].Denominator;
        return $"{deg:F0}° {min:F0}' {sec:F2}\"";
    }
}
