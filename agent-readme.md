# SyncDroid WinUI — Agent Handover & Developer Guide (`agent-readme.md`)

This document provides complete context for AI coding agents and developers continuing work on **SyncDroid WinUI** in future sessions.

---

## 1. Project Overview
- **Project Path**: `C:\Users\Yiren\devext\PRTS\SyncDroidWinUI`
- **Target Framework**: `.NET 10.0` (`net10.0-windows10.0.26100.0`)
- **UI Framework**: **WinUI 3** (Windows App SDK 2.x, Mica backdrop, Windows 11 card-based layout)
- **Current Version**: `0.1.5.0 Release` (Manifest version: `0.1.5.0`)
- **Core Purpose**: Mirror-synchronize folders (`/MIR` Mirror Sync & Incremental Merge) and transfer files/folders between a Windows PC and any target device (Android or Windows PC) over **USB ADB**, **Wireless Companion (LAN / Wi-Fi)**, or **LocalSend (Protocol v2.1)**:
  1. **Tab 1 (`Push (/MIR mode)`)**: Push sync (copies new/modified files to target, skips identical files without touching them, deletes extra files on target in `/MIR` mode or keeps them in Incremental Merge mode).
  2. **Tab 2 (`Pull (/MIR mode)`)**: Pull sync (pulls new/modified files from target to PC, skips identical files without touching them, deletes extra files on PC in `/MIR` mode or keeps them in Incremental Merge mode).
  3. **Tab 3 (`Direct Transfer (Files / Folders)`)**: Drag-and-drop multi-file/folder queue transfer (Push to target or Pull individual items from target) supporting ADB, Wireless Companion, Windows drive targets, and LocalSend.

---

## 2. Directory & File Structure

```text
C:\Users\Yiren\devext\PRTS\SyncDroidWinUI\
├── SyncDroidWinUI.csproj                # Project file (.NET 10 WinUI 3, v0.1.5.0)
├── Package.appxmanifest                 # MSIX package manifest (Identity: YirenLi.SyncDroid, v0.1.5.0)
├── app.manifest                         # Win32 application manifest
├── App.xaml / App.xaml.cs               # Application entry, static MainWindow reference & ProcessExit cleanup
├── MainWindow.xaml / MainWindow.xaml.cs # Custom Mica title bar ("SyncDroid" + "0.1.5.0 Release" badge) & exit hooks
├── Assets/
│   ├── AppIcon.ico / AppIcon.png        # Application icons & MSIX visual assets
│   └── platform-tools/                  # Built-in official Android platform-tools (adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll)
├── Models/
│   ├── CompanionModels.cs               # ConnectionMode, AdbSourceMode, ConflictPolicy, Companion & Tree DTOs
│   └── LocalSendModels.cs               # LocalSend Protocol v2.1 DTOs & LocalSendPeer model
├── Views/
│   ├── MainPage.xaml(.cs)               # Top device status/selector bar, Wireless Connect dialog, Settings toggle, TabView
│   ├── SyncPage.xaml(.cs)               # Reusable 3-card sync view (used for both Push and Pull tabs)
│   ├── DirectTransferPage.xaml(.cs)     # Drag-and-drop file/folder direct transfer queue view
│   ├── SettingsPage.xaml(.cs)           # Language, Wireless Companion, LocalSend, ADB & Hash Verification settings
│   └── AndroidFolderPickerDialog.xaml(.cs) # TreeView modal dialog supporting both Android /storage and Windows Drives (C:\, D:\, ...)
├── ViewModels/
│   └── SyncViewModel.cs                 # MVVM logic, shared path histories, sync commands & logs
├── Services/
│   ├── AdbSyncService.cs                # Built-in/running ADB resolution, adb track-devices monitor, CJK-safe sync & hash check
│   ├── CompanionSyncService.cs          # UDP 45820 discovery + TCP 45821 HTTP server/client & Windows drive path sync
│   ├── LocalSendService.cs              # UDP multicast 224.0.0.167:53317 + dual HTTP/HTTPS TCP 53317 LocalSend v2.1 service
│   ├── SettingsService.cs               # JSON persistence (%LocalAppData%\SyncDroidWinUI\settings.json)
│   └── LocalizationService.cs           # Real-time localization (en-US, zh-CN, zh-TW)
├── Helpers/
│   ├── FolderPickerHelper.cs            # Native Win32 COM IFileDialog folder & multi-file picker (unpackaged safe)
│   ├── FileNameLengthHelper.cs          # > 255 UTF-8 byte filename scanner & grapheme-safe truncator
│   └── FileHashHelper.cs                # SHA-256 / MD5 hash verifier, ProgressTrackingStream (speed & overall %) & loopback filter
├── Converters/
│   └── BoolToVisibilityConverter.cs     # XAML boolean-to-visibility converter
└── ReleasePackages/                     # Signed MSIX outputs (x64/arm64 × SelfContained/FrameworkDependent), .cer/.pfx
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
     dotnet build -c Debug "C:\Users\Yiren\devext\PRTS\SyncDroidWinUI\SyncDroidWinUI.csproj"
     Start-Process -FilePath "C:\Users\Yiren\devext\PRTS\SyncDroidWinUI\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SyncDroidWinUI.exe"
     ```
- **Trimming**: Keep `<PublishTrimmed>False</PublishTrimmed>` and `<PublishReadyToRun>False</PublishReadyToRun>` in `SyncDroidWinUI.csproj` because XAML `{Binding}`, `System.Text.Json`, and COM interop (`[ComImport]`) rely on runtime reflection/marshaling.

### 3.2 Native PC Folder & File Picker (`Helpers/FolderPickerHelper.cs`)
- `Windows.Storage.Pickers.FolderPicker` fails with a blank COM exception in unpackaged WinUI 3 apps on this environment.
- We implemented `FolderPickerHelper.PickFolder(IntPtr hwnd)` and `FolderPickerHelper.PickFiles(IntPtr hwnd)` using native COM `CLSID_FileOpenDialog` (`DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7`).

### 3.3 Built-in ADB Priority, Exit Cleanup & `adb track-devices` Monitor (`Services/AdbSyncService.cs`)
- **Highest-Priority Default (`AdbSourceMode.BuiltInOrRunning = 0`)**:
  - First checks if an `adb` server or `adb.exe` process is already running on the system and reuses it.
  - Otherwise launches the bundled `Assets\platform-tools\adb.exe` and records `IsUsingBuiltInAdb = true`.
- **Auto-Kill Built-in ADB on Exit (`KillBuiltInAdbOnExit = true`)**:
  - On `MainWindow.Closed` and `AppDomain.CurrentDomain.ProcessExit`, `AdbSyncService.Instance.ShutdownOnAppExit()` stops the `track-devices` monitor and terminates the `adb.exe` process tree if `IsUsingBuiltInAdb` is true (leaving external user-started ADB processes untouched).
- **`adb track-devices` Real-Time Monitor**:
  - Connects to the ADB server (`host:track-devices`) and streams device state updates in real time.
  - When a device state is read as `unauthorized`, raises `UnauthorizedDeviceDetected` so `MainPage` displays a localized `ContentDialog` prompting the user to allow USB debugging on their phone; auto-closes the dialog and refreshes the device selector as soon as the user authorizes.
- **MS Store 10.1.2.10 Compliance (Zero Loopback / WSA Traces)**:
  - `FileHashHelper.IsLoopbackOrDisallowedEndpoint` filters out any loopback (`IPAddress.IsLoopback`) or non-LAN host/serial without leaving any literal loopback IP or port strings in source or binary.

### 3.4 CJK & Unicode Support, Post-Transfer Hash Verification & Speed/Progress Logging
- **CJK Path Bridging**: `ToAdbString` / `FromAdbString` (UTF-8 ↔ ISO-8859-1) and Base64-encoded shell scripts (`ExecuteUtf8ShellScript`) ensure lossless CJK/emoji paths.
- **Post-Transfer Hash Verification (`VerifyHashAfterTransfer = true`)**:
  - After transferring each file over ADB (`sha256sum` / `md5sum`), Wireless Companion (`/api/v1/file/hash` & `X-File-Sha256`), Windows Drive sync, or LocalSend (`Sha256`), computes and compares SHA-256 hashes. If interrupted or mismatched, automatically deletes the corrupted target file and throws an error.
- **Real-Time Speed & Overall Completion Percentage (`Helpers/FileHashHelper.cs`)**:
  - `ProgressTrackingStream` wraps file transfer streams across ADB, Wireless Companion, Windows Drives, and LocalSend, logging live transfer speed (`Speed: XX.XX MB/s`) and byte-weighted overall completion percentage (`Overall: XX.X% (current/total)`) both every 1.5 seconds during large file transfers and upon each file's completion.

### 3.5 Target Path Selector with `/storage` & Windows Drive Support (`Views/AndroidFolderPickerDialog.xaml.cs`)
- Supports both Android `/storage` and Windows drive letters (`C:\`, `D:\`, etc.) via a top `RootDriveComboBox` and expandable drive/root nodes in `StorageTreeView`.
- When connected to a Windows Companion peer, queries `/` to list remote Windows drives; otherwise lists local Windows drives alongside `/storage` (when an Android device is connected).

---

## 4. Settings & Localization
- **Settings File**: `%LocalAppData%\SyncDroidWinUI\settings.json`
- Stores:
  - Last used paths per tab (`PcToAndroid_PcPath`, `PcToAndroid_AndroidPath`, `AndroidToPc_PcPath`, `AndroidToPc_AndroidPath`)
  - Shared dropdown histories (`PcPathHistory`, `AndroidPathHistory`, `CompanionEndpointHistory`, max 15 items, deduplicated)
  - `Language` (`"en-US"` default, `"zh-CN"`, `"zh-TW"`) — managed reactively via `LocalizationService.Instance`
  - `AdbSourceMode` (`0` = Built-in / Running ADB [Default], `1` = System PATH, `2` = Custom Directory), `UseCustomAdbPath`, `CustomAdbDirectory`
  - `KillBuiltInAdbOnExit` (`bool`, default `true`)
  - `VerifyHashAfterTransfer` (`bool`, default `true`)
  - Wireless Companion (`CompanionPort = 45821`, `CompanionServerEnabled = true`) & LocalSend (`LocalSendEnabled = true`, `LocalSendPort = 53317`, `LocalSendReceiveDirectory`)

---

## 5. MSIX Packaging & Signing Instructions

### Manifest Identity (`Package.appxmanifest`)
- `Identity Name`: `YirenLi.SyncDroid`
- `Identity Publisher`: `CN=6DA4D603-0AFF-4F5E-9C1D-66177B450C0B`
- `PublisherDisplayName`: `Yiren Li`
- `Version`: `0.1.5.0`

### Building & Signing All 4 MSIX Packages (`x64` / `arm64` × `SelfContained` / `FrameworkDependent`)
```powershell
$proj = "C:\Users\Yiren\devext\PRTS\SyncDroidWinUI\SyncDroidWinUI.csproj"
$appPkgDir = "C:\Users\Yiren\devext\PRTS\SyncDroidWinUI\obj\AppPackages\"
$outDir = "C:\Users\Yiren\devext\PRTS\SyncDroidWinUI\ReleasePackages"
$pfx = "$outDir\SyncDroidWinUI.pfx"
$signtool = "C:\Users\Yiren\.nuget\packages\microsoft.windows.sdk.buildtools\10.0.28000.2705\bin\10.0.28000.0\x64\signtool.exe"
$dirSc = "$outDir\1_SelfContained_WithDotNet"
$dirFd = "$outDir\2_FrameworkDependent_NoDotNet"

# Example: Build x64 Self-Contained MSIX
Remove-Item $appPkgDir -Recurse -Force -ErrorAction SilentlyContinue
dotnet build $proj -c Release -r win-x64 /p:Platform=x64 /p:WindowsPackageType=MSIX /p:GenerateAppxPackageOnBuild=true /p:SelfContained=true /p:WindowsAppSDKSelfContained=true /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageDir=$appPkgDir
Copy-Item "$appPkgDir\SyncDroidWinUI_0.1.5.0_x64_Test\SyncDroidWinUI_0.1.5.0_x64.msix" "$dirSc\SyncDroid_0.1.5.0_Release_x64_SelfContained.msix" -Force
& $signtool sign /fd SHA256 /f $pfx /p "SyncDroid123" "$dirSc\SyncDroid_0.1.5.0_Release_x64_SelfContained.msix"
```
*(For `arm64`, use `-r win-arm64 /p:Platform=ARM64`; for Framework-Dependent, set `/p:SelfContained=false`.)*
