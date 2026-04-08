# all-image-format

A **Windows 11 Image Viewer** built with **WinUI 3 (Windows App SDK)** that natively displays HEIC, AVIF, HEIF, WebP, and all other common image formats **without** requiring the Microsoft Store HEVC codec extension.

---

## Features

| Feature | Details |
|---|---|
| **Zero-dependency codec** | Decodes HEIC/AVIF/WebP via [Magick.NET](https://github.com/dlemstra/Magick.NET) — no store extension needed |
| **10-bit / 12-bit colour** | AVIF wide-gamut images are linearly tone-mapped to 8-bit to minimise banding |
| **ICC colour profiles** | Embedded profiles are honoured and converted to sRGB before display |
| **Lazy folder loading** | Folders with 500+ images are handled via batched, semaphore-limited thumbnail loading |
| **Loading spinner** | ProgressRing overlays cover large AVIF/HEIC decodes so the UI stays responsive |
| **EXIF slide-out panel** | Shows GPS coordinates, camera model, aperture, exposure, ISO, and more |
| **File association** | Registered as default handler for `.heic`, `.heif`, `.avif`, `.webp`, `.jpg`, `.png`, … |
| **Borderless UI** | Custom title bar extends into the client area (Mica-ready) |
| **Keyboard navigation** | ← / → arrows, `+`/`-` zoom, `F` fit-to-window, `M` metadata panel |
| **Drag and drop** | Drop any supported file onto the window to open it |
| **Store-ready manifest** | Targets Windows 10 1903+, supports x64 and ARM64 |

---

## Supported Formats

`.heic` · `.heif` · `.avif` · `.jpg` · `.jpeg` · `.png` · `.gif` · `.webp` · `.bmp` · `.tiff`

---

## Technical Stack

- **Language**: C# 12 / .NET 8
- **UI Framework**: WinUI 3 (Windows App SDK 1.5)
- **Core Library**: [Magick.NET-Q8-AnyCPU](https://www.nuget.org/packages/Magick.NET-Q8-AnyCPU) — open-source ImageMagick binding for all codec work
- **Target**: Windows 10 version 1903 (build 18362) and later, including Windows 11

---

## Project Structure

```
HeicAvifViewer.sln
HeicAvifViewer/
├── App.xaml / App.xaml.cs          — Application entry point; handles file-activation launches
├── MainWindow.xaml / .xaml.cs      — Borderless main window: image view, thumbnails, navigation
├── ExifMetadataPanel.xaml / .cs    — Slide-out EXIF/IPTC metadata panel
├── ImageLoader.cs                  — Magick.NET decoding: full-res + thumbnails + metadata
├── BoolToVisibilityConverter.cs    — XAML helper converter
├── Package.appxmanifest            — MSIX manifest with file-type associations
├── app.manifest                    — Win32 DPI/long-path aware manifest
└── Assets/                         — App icons (replace with real artwork before Store upload)
```

---

## Building

Prerequisites: [Visual Studio 2022](https://visualstudio.microsoft.com/) with the **Windows App SDK** workload, or the .NET 8 SDK with the `Microsoft.WindowsAppSDK` NuGet package.

```bash
dotnet restore HeicAvifViewer.sln
dotnet build  HeicAvifViewer.sln -c Release
```

For a side-loadable MSIX package:

```bash
dotnet publish HeicAvifViewer/HeicAvifViewer.csproj -c Release -r win-x64
```

---

## Microsoft Store Submission Notes

1. **Architecture**: Publish for `x64` and `ARM64`.
2. **Minimum version**: Windows 10 build 18362 (version 1903).
3. **Licensing**: Magick.NET uses the Apache 2.0 licence. Mention it in your *Third Party Notices* file.
4. **Privacy policy**: The app processes images locally and never uploads data to the cloud.

---

## Licence

This project is provided under the [MIT Licence](LICENSE).
