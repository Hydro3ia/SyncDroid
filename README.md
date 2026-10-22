# SyncDroid

**SyncDroid** is a modern **WinUI 3** desktop utility for mirror-synchronizing folders (`robocopy /MIR` equivalent) between a Windows PC and an Android device over **Android Debug Bridge (ADB)**.

- **Current Version**: `0.1.2.0 Release`
- **Privacy Policy**: [PRIVACY.md](./PRIVACY.md)

## Key Features
- **Two-Way Mirror Synchronization (`/MIR`)**:
  - **PC ➔ Android (Push Mirror)**: Pushes new and modified files from PC to Android, skips identical files without re-transmitting, and removes extra files on Android not present in the PC source folder.
  - **Android ➔ PC (Pull Mirror)**: Pulls new and modified files from Android to PC, skips identical files, and removes extra files on PC not present in the Android source folder.
- **Full CJK & Unicode Path Support**: Losslessly handles Chinese, Japanese, Korean, emoji, and special characters in folder and file names over ADB.
- **Interactive Android `/storage` Browser**: Browse and create folders directly on the connected Android device.
- **Multi-Language Interface**: Instant switching between English (`en-US`), Simplified Chinese (`zh-CN`), and Traditional Chinese (`zh-TW`).
- **Flexible ADB Configuration**: Use `adb.exe` from the system `PATH` or specify a custom `platform-tools` directory.

## Building from Source
Requirements:
- Windows 10 (1809+) or Windows 11
- .NET 10 SDK (`net10.0-windows10.0.26100.0`)

```powershell
dotnet build -c Release
```
