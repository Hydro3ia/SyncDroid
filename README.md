# SyncDroid

**SyncDroid** is a modern **WinUI 3** desktop utility for synchronizing folders (`/MIR` Mirror Sync & Incremental Merge) and transferring files/folders between a Windows PC and an Android device over **USB ADB** or **Wireless Companion (LAN / Wi-Fi)**.

- **Current Version**: `0.2.0.0 beta`
- **Privacy Policy**: [PRIVACY.md](./PRIVACY.md)

## Download & Installation

Prebuilt MSIX packages for **x64** and **arm64** — in both *self-contained* and *framework-dependent* flavours — are published on the [Releases](https://github.com/Hydro3ia/SyncDroid/releases) page.

1. Download `SyncDroidWinUI.cer`, `Install-Certificate.bat` and the `.msix` package matching your PC architecture.
2. Right-click `Install-Certificate.bat` → **Run as administrator** (trusts the self-signed certificate).
3. Double-click the `.msix` package to install, or run `Add-AppxPackage -Path .\SyncDroid_<version>_x64_SelfContained.msix` in PowerShell.

> **Self-contained** packages bundle .NET and the Windows App SDK and need no extra runtime. **Framework-dependent** packages are much smaller but require the [Windows App SDK Runtime](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).

## Key Features
- **Dual-Channel Connectivity (USB ADB + Wireless Companion)**:
  - **ADB Mode**: Connect any Android device with USB debugging enabled.
  - **Wireless Companion Mode**: Connect to `SyncDroid-on-Android` over LAN/Wi-Fi (UDP auto-discovery on port `45820` + HTTP streaming on port `45821`) without requiring `adb.exe` or a USB cable. Also runs a background listener on PC so the Android app can discover and sync with the PC directly.
- **Two-Way Folder Synchronization (`/MIR` Mirror & Incremental Merge)**:
  - **PC ➔ Android (Push Sync)** & **Android ➔ PC (Pull Sync)**.
  - Switch between **Mirror Sync (`/MIR` — deletes extra files on target)** and **Incremental Merge (keeps existing extra files on target)**.
- **Direct Transfer Tab (Files & Folders)**:
  - Drag & drop files or folders into the transfer queue or add via native Win32 multi-file/folder pickers.
  - Configurable conflict resolution policies: *Skip if identical size*, *Overwrite existing*, or *Keep both (auto-rename)*.
  - Supports both pushing to Android and pulling individual files/folders from Android to PC.
- **Overlong Filename (`> 255 Bytes`) Detection & Auto-Truncation**:
  - Automatically scans PC source directories before syncing/transferring to detect filenames whose UTF-8 byte length (including extension) exceeds Android's `255`-byte filesystem limit.
  - Offers one-click local filename truncation (`< 255` bytes while preserving file extensions and Unicode grapheme clusters) or skipping overlong files.
- **Full CJK & Unicode Path Support**: Losslessly handles Chinese, Japanese, Korean, emoji, and special characters in folder and file names.
- **Interactive Android `/storage` Browser**: Browse and create folders directly on the connected Android device over either ADB or Wireless Companion.
- **Multi-Language Interface**: Instant switching between English (`en-US`), Simplified Chinese (`zh-CN`), and Traditional Chinese (`zh-TW`).

## Building from Source
Requirements:
- Windows 10 (1809+) or Windows 11
- .NET 10 SDK (`net10.0-windows10.0.26100.0`)

```powershell
dotnet build -c Release
```

## Releasing

Releases are built and published by GitHub Actions (`.github/workflows/build-release.yml`). Bump `Version` / `AssemblyVersion` in `SyncDroidWinUI.csproj` **and** `Identity Version` in `Package.appxmanifest`, commit, then push a matching tag:

```powershell
git tag v0.1.6.0
git push origin v0.1.6.0
```

The workflow builds and signs the x64 and arm64 packages (self-contained + framework-dependent) and attaches them to a GitHub Release. You can also run it manually from the **Actions** tab without publishing a release — the packages are then available as workflow artifacts.
