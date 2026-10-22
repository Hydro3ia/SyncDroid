# SyncDroid WinUI — Agent Handover & Developer Guide (`agent-readme.md`)

This document provides complete context for AI coding agents and developers continuing work on **SyncDroid WinUI** in future sessions.

---

## 1. Project Overview
- **Project Path**: `C:\Users\Yiren\SyncDroidWinUI`
- **Target Framework**: `.NET 10.0` (`net10.0-windows10.0.26100.0`)
- **UI Framework**: **WinUI 3** (Windows App SDK 2.x, Mica backdrop, Windows 11 card-based layout)
- **Current Version**: `0.1.1.1 Release` (Manifest version: `0.1.1.1`)
- **Core Purpose**: Mirror-synchronize folders (`robocopy /MIR` equivalent) between a Windows PC and an Android device over **ADB** in two directions:
  1. **Tab 1 (`PC ➔ Android`)**: Push mirror (copies new/modified files to Android, skips identical files without touching them, deletes extra files on Android not present on PC).
  2. **Tab 2 (`Android ➔ PC`)**: Pull mirror (pulls new/modified files to PC, skips identical files without touching them, deletes extra files on PC not present on Android).

---

## 2. Directory & File Structure

```text
C:\Users\Yiren\SyncDroidWinUI\
├── SyncDroidWinUI.csproj                # Project file (.NET 10 WinUI 3)
├── Package.appxmanifest                 # MSIX package manifest (Identity: YirenLi.SyncDroid)
├── app.manifest                         # Win32 application manifest
├── App.xaml / App.xaml.cs               # Application entry & static MainWindow reference
├── MainWindow.xaml / MainWindow.xaml.cs # Custom Mica title bar ("SyncDroid" + "0.1.1.1 Release" badge)
├── Views/
│   ├── MainPage.xaml(.cs)               # Top device status/selector bar, Settings toggle, TabView
│   ├── SyncPage.xaml(.cs)               # Reusable 3-card sync view (used for both tabs)
│   ├── SettingsPage.xaml(.cs)           # Language selector & ADB path configuration page
│   └── AndroidFolderPickerDialog.xaml(.cs) # TreeView modal dialog for browsing Android /storage
├── ViewModels/
│   └── SyncViewModel.cs                 # MVVM logic, shared path histories, sync commands & logs
├── Services/
│   ├── AdbSyncService.cs                # Core ADB connection, CJK-safe sync, /storage scanner
│   ├── SettingsService.cs               # JSON persistence (%LocalAppData%\SyncDroidWinUI\settings.json)
│   └── LocalizationService.cs           # Real-time localization (en-US, zh-CN, zh-TW)
├── Helpers/
│   └── FolderPickerHelper.cs            # Native Win32 COM IFileDialog folder picker (unpackaged safe)
├── Converters/
│   └── BoolToVisibilityConverter.cs     # XAML boolean-to-visibility converter
└── ReleasePackages/                     # Signed MSIX outputs, test certificate (.cer/.pfx), install bat
```

---

## 3. Key Technical Decisions & Resolved Pitfalls (Important!)

### 3.1 Running Locally (Unpackaged Debug Mode) vs MSIX Packaging
- Calling `dotnet run` directly invokes the `winapp` CLI hook for packaged apps and may fail if Windows Developer Mode is off.
- **To build and run locally during development**:
  1. Ensure `<WindowsPackageType Condition="'$(WindowsPackageType)' == ''">None</WindowsPackageType>` remains in `SyncDroidWinUI.csproj`.
  2. Kill any running instance before building:
     ```powershell
     taskkill /IM SyncDroidWinUI.exe /F 2>$null
     dotnet build -c Debug "C:\Users\Yiren\SyncDroidWinUI\SyncDroidWinUI.csproj"
     Start-Process -FilePath "C:\Users\Yiren\SyncDroidWinUI\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SyncDroidWinUI.exe"
     ```
- **Trimming**: Keep `<PublishTrimmed>False</PublishTrimmed>` and `<PublishReadyToRun>False</PublishReadyToRun>` in `SyncDroidWinUI.csproj` because XAML `{Binding}`, `System.Text.Json`, and COM interop (`[ComImport]`) rely on runtime reflection/marshaling.

### 3.2 Native PC Folder Picker (`Helpers/FolderPickerHelper.cs`)
- `Windows.Storage.Pickers.FolderPicker` fails with a blank COM exception in unpackaged WinUI 3 apps on this environment.
- We implemented `FolderPickerHelper.PickFolder(IntPtr hwnd)` using native COM `CLSID_FileOpenDialog` (`DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7`) cast to:
  - `IFileDialog` (`Guid("42f85136-db7e-439c-85f1-e4075d135fc8")`) with `FOS_PICKFOLDERS`
  - `IShellItem` (`Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")`)
- **Do not change those two interface GUIDs** (`IFileOpenDialog` fails `QI` in this context, whereas `IFileDialog` + `IShellItem` works 100%).

### 3.3 CJK (Chinese/Japanese/Korean) & Unicode Filename Support (`Services/AdbSyncService.cs`)
- `SharpAdbClient` (v2.3.23) defaults `AdbClient.Encoding` to `ISO-8859-1` (`Latin1`) and uses `string.Length` (UTF-16 char count) as the byte-length header in `SendSyncRequest` and `SendAdbRequest`.
- To make CJK paths work losslessly without modifying `SharpAdbClient.dll`:
  1. **`ToAdbString(string utf8)` / `FromAdbString(string adbStr)`**: Bridges `UTF-8` bytes through `ISO-8859-1` 1:1 byte-to-char mapping so `string.Length` equals the exact UTF-8 byte count and `SharpAdbClient` sends/receives raw UTF-8 bytes for `GetDirectoryListing`, `Push`, and `Pull`.
  2. **`ExecuteUtf8ShellScript(string script)`**: Encodes shell scripts (`mkdir -p`, `mv`, `cp`, `rm -f`, `ls`) as Base64 and runs `echo <base64> | base64 -d | sh` with `Encoding.UTF8` output decoding.
  3. **Scoped Storage Fallback**: If direct `SyncService.Push` to `/sdcard/...` is blocked by Android Scoped Storage, `PushFileAsync` pushes to a pure-ASCII temp file (`/data/local/tmp/sync_<GUID>.tmp`) and moves it to the target path via `ExecuteUtf8ShellScript`.

### 3.4 Mirror Comparison Rule (`/MIR` Skip Logic)
- Android's FUSE filesystem (`/sdcard` / `/storage/emulated/0`) often resets file `mtime` to the transfer timestamp when files are moved from `/data/local/tmp`.
- Therefore, **do NOT compare modification timestamps** to decide whether to overwrite.
- **Comparison rule**: If a file exists at the same relative path (case-insensitive via `StringComparer.OrdinalIgnoreCase`) and has the **exact same byte size (`Size`)**, `shouldPush` / `shouldPull` is `false` — log `Skipped (Identical)` and **do not touch or re-transmit the file**.

### 3.5 Android `/storage` Browser (`Views/AndroidFolderPickerDialog.xaml.cs`)
- When an online ADB device is selected (`AdbSyncService.Instance.CurrentDevice`), `PreScanStorageAsync()` pre-scans `/storage`, `/storage/emulated`, and `/storage/emulated/0` in the background and caches results in `_directoryCache`.
- Because Android 11+ restricts `readdir` directly on `/storage` and `/storage/emulated`, `GetRemoteSubdirectoriesAsync` explicitly ensures `"emulated"` is listed under `"/storage"` and `"0"` is listed under `"/storage/emulated"`, while using `SyncService.GetDirectoryListing` + `ls -1 -p` fallback for all other directories.

---

## 4. Settings & Localization
- **Settings File**: `%LocalAppData%\SyncDroidWinUI\settings.json`
- Stores:
  - Last used paths per tab (`PcToAndroid_PcPath`, `PcToAndroid_AndroidPath`, `AndroidToPc_PcPath`, `AndroidToPc_AndroidPath`)
  - Shared dropdown histories (`PcPathHistory`, `AndroidPathHistory`, max 15 items, deduplicated)
  - `Language` (`"en-US"` default, `"zh-CN"`, `"zh-TW"`) — managed reactively via `LocalizationService.Instance`
  - `UseCustomAdbPath` (`bool`) and `CustomAdbDirectory` (`string`)

---

## 5. MSIX Packaging & Signing Instructions

### Manifest Identity (`Package.appxmanifest`)
- `Identity Name`: `YirenLi.SyncDroid`
- `Identity Publisher`: `CN=6DA4D603-0AFF-4F5E-9C1D-66177B450C0B`
- `PublisherDisplayName`: `Yiren Li`
- `Version`: `0.1.1.1`

### Building & Signing All 4 MSIX Packages (`x64` / `arm64` × `SelfContained` / `FrameworkDependent`)
```powershell
$outDir = "C:\Users\Yiren\SyncDroidWinUI\ReleasePackages"
$pfx = "$outDir\SyncDroidWinUI.pfx"
$signtool = "C:\Users\Yiren\.nuget\packages\microsoft.windows.sdk.buildtools\10.0.28000.2705\bin\10.0.28000.0\x64\signtool.exe"
$dirSc = "$outDir\1_SelfContained_WithDotNet"
$dirFd = "$outDir\2_FrameworkDependent_NoDotNet"

# Example: Build x64 Self-Contained MSIX
Remove-Item "C:\Users\Yiren\SyncDroidWinUI\obj\AppPackages" -Recurse -Force -ErrorAction SilentlyContinue
dotnet build "C:\Users\Yiren\SyncDroidWinUI\SyncDroidWinUI.csproj" -c Release -r win-x64 /p:Platform=x64 /p:WindowsPackageType=MSIX /p:GenerateAppxPackageOnBuild=true /p:SelfContained=true /p:WindowsAppSDKSelfContained=true /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageDir="C:\Users\Yiren\SyncDroidWinUI\obj\AppPackages\"
Copy-Item "C:\Users\Yiren\SyncDroidWinUI\obj\AppPackages\SyncDroidWinUI_0.1.1.1_x64_Test\SyncDroidWinUI_0.1.1.1_x64.msix" "$dirSc\SyncDroid_0.1.1.1_Release_x64_SelfContained.msix" -Force
& $signtool sign /fd SHA256 /f $pfx /p "SyncDroid123" "$dirSc\SyncDroid_0.1.1.1_Release_x64_SelfContained.msix"
```
*(For `arm64`, use `-r win-arm64 /p:Platform=ARM64`; for Framework-Dependent, set `/p:SelfContained=false`.)*
