using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpAdbClient;
using SharpAdbClient.DeviceCommands;
using SyncDroidWinUI.Helpers;
using SyncDroidWinUI.Models;

namespace SyncDroidWinUI.Services
{
    public class AdbSyncService
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        private AdbClient _client;
        private AdbServer _server;
        private readonly Dictionary<string, List<string>> _directoryCache = new();
        private readonly object _cacheLock = new();

        private CancellationTokenSource? _monitorCts;
        private readonly object _monitorLock = new();
        private readonly HashSet<string> _notifiedUnauthorizedSerials = new(StringComparer.OrdinalIgnoreCase);
        private string _lastTrackedSnapshot = "";

        public static AdbSyncService Instance { get; } = new AdbSyncService();

        /// <summary>
        /// Indicates whether the currently active ADB server was launched from (or matches) the app's built-in ADB binary.
        /// </summary>
        public bool IsUsingBuiltInAdb { get; private set; }

        /// <summary>
        /// Fired when `adb track-devices` detects a device in `unauthorized` state.
        /// </summary>
        public event Action<string>? UnauthorizedDeviceDetected;

        /// <summary>
        /// Fired when `adb track-devices` detects any change in connected ADB devices or their states.
        /// </summary>
        public event Action? TrackedDevicesChanged;

        private DeviceData? _currentDevice;
        public DeviceData? CurrentDevice
        {
            get => _currentDevice;
            set
            {
                bool changed = _currentDevice?.Serial != value?.Serial;
                _currentDevice = value;
                if (changed && _currentDevice != null && _currentDevice.State == DeviceState.Online)
                {
                    _ = PreScanStorageAsync();
                }
            }
        }

        private AdbSyncService()
        {
            _client = new AdbClient();
            _server = new AdbServer();
        }

        /// <summary>
        /// SharpAdbClient 2.3.23 uses String.Length as the byte length header and encodes with ISO-8859-1.
        /// Converting UTF-8 bytes into an ISO-8859-1 string makes String.Length equal to the exact UTF-8 byte count
        /// and causes SharpAdbClient to transmit the exact UTF-8 byte sequence for CJK / Unicode paths.
        /// </summary>
        private static string ToAdbString(string utf8Text)
        {
            if (string.IsNullOrEmpty(utf8Text)) return utf8Text;
            return Latin1.GetString(Encoding.UTF8.GetBytes(utf8Text));
        }

        /// <summary>
        /// Decodes an ISO-8859-1 string returned by SharpAdbClient back into a proper UTF-8 C# string.
        /// </summary>
        private static string FromAdbString(string adbText)
        {
            if (string.IsNullOrEmpty(adbText)) return adbText;
            return Encoding.UTF8.GetString(Latin1.GetBytes(adbText));
        }

        /// <summary>
        /// Escapes single quotes for safe inclusion inside single-quoted POSIX shell arguments.
        /// </summary>
        private static string EscapeShellSingleQuotes(string text)
        {
            return "'" + text.Replace("'", "'\"'\"'") + "'";
        }

        /// <summary>
        /// Executes a UTF-8 shell script on the Android device via Base64 decoding so CJK characters
        /// and special symbols are never corrupted or truncated by SharpAdbClient's request framing.
        /// </summary>
        private string ExecuteUtf8ShellScript(string utf8Script)
        {
            if (CurrentDevice == null) throw new Exception("No Android device connected.");
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(utf8Script));
            var receiver = new ConsoleOutputReceiver();
            _client.ExecuteRemoteCommand($"echo {base64} | base64 -d | sh", CurrentDevice, receiver, Encoding.UTF8);
            return receiver.ToString();
        }

        public static string? GetBuiltInAdbExecutablePath()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string[] candidates =
                {
                    Path.Combine(baseDir, "Assets", "platform-tools", "adb.exe"),
                    Path.Combine(baseDir, "platform-tools", "adb.exe"),
                    Path.Combine(baseDir, "adb.exe")
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
            catch { }

            return null;
        }

        public static string? FindRunningAdbProcessPath()
        {
            try
            {
                var processes = Process.GetProcessesByName("adb");
                foreach (var proc in processes)
                {
                    try
                    {
                        string? fileName = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(fileName) && File.Exists(fileName))
                        {
                            return Path.GetFullPath(fileName);
                        }
                    }
                    catch { }
                    finally
                    {
                        try { proc.Dispose(); } catch { }
                    }
                }
            }
            catch { }

            return null;
        }

        public static bool IsAnyAdbProcessRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName("adb");
                bool any = processes.Length > 0;
                foreach (var p in processes)
                {
                    try { p.Dispose(); } catch { }
                }
                return any;
            }
            catch
            {
                return false;
            }
        }

        public static string? FindSystemPathAdb()
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "adb.exe");
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch { }
            }
            return null;
        }

        public static string? ResolveCustomAdbPath(string? customDirOrFile)
        {
            var custom = customDirOrFile?.Trim() ?? "";
            if (string.IsNullOrEmpty(custom)) return null;

            if (custom.EndsWith("adb.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(custom))
            {
                return Path.GetFullPath(custom);
            }

            var candidate = Path.Combine(custom, "adb.exe");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            return null;
        }

        private static bool IsPathBuiltInAdb(string? adbPath, string? builtInPath)
        {
            if (string.IsNullOrWhiteSpace(adbPath)) return false;
            try
            {
                string fullAdb = Path.GetFullPath(adbPath);
                if (!string.IsNullOrWhiteSpace(builtInPath) &&
                    string.Equals(fullAdb, Path.GetFullPath(builtInPath), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string baseDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return fullAdb.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string? ResolveAdbExecutablePath(AppSettings settings)
        {
            var mode = Enum.IsDefined(typeof(AdbSourceMode), settings.AdbSourceMode)
                ? (AdbSourceMode)settings.AdbSourceMode
                : (settings.UseCustomAdbPath ? AdbSourceMode.CustomDirectory : AdbSourceMode.BuiltInOrRunning);

            switch (mode)
            {
                case AdbSourceMode.BuiltInOrRunning:
                {
                    var running = FindRunningAdbProcessPath();
                    if (!string.IsNullOrEmpty(running))
                    {
                        return running;
                    }

                    var builtIn = GetBuiltInAdbExecutablePath();
                    if (!string.IsNullOrEmpty(builtIn))
                    {
                        return builtIn;
                    }

                    return FindSystemPathAdb();
                }

                case AdbSourceMode.SystemPath:
                    return FindSystemPathAdb();

                case AdbSourceMode.CustomDirectory:
                    return ResolveCustomAdbPath(settings.CustomAdbDirectory);

                default:
                    return GetBuiltInAdbExecutablePath() ?? FindSystemPathAdb();
            }
        }

        public void StartServerAndConnect()
        {
            var settings = SettingsService.Load();
            var mode = Enum.IsDefined(typeof(AdbSourceMode), settings.AdbSourceMode)
                ? (AdbSourceMode)settings.AdbSourceMode
                : (settings.UseCustomAdbPath ? AdbSourceMode.CustomDirectory : AdbSourceMode.BuiltInOrRunning);

            string? builtInPath = GetBuiltInAdbExecutablePath();
            string? runningPath = FindRunningAdbProcessPath();
            bool anyProcessRunning = !string.IsNullOrEmpty(runningPath) || IsAnyAdbProcessRunning();
            bool serverRunning = false;
            try
            {
                serverRunning = _server.GetStatus().IsRunning;
            }
            catch { }

            if (mode == AdbSourceMode.CustomDirectory)
            {
                var customExe = ResolveCustomAdbPath(settings.CustomAdbDirectory);
                if (string.IsNullOrEmpty(customExe))
                {
                    throw new Exception($"adb.exe not found in specified custom directory: '{settings.CustomAdbDirectory}'");
                }

                try
                {
                    _server.StartServer(customExe, restartServerIfNewer: false);
                    IsUsingBuiltInAdb = IsPathBuiltInAdb(customExe, builtInPath);
                }
                catch (Exception ex)
                {
                    throw new Exception($"ADB connection failed ({ex.Message}). Please check ADB configuration in Settings.");
                }
            }
            else if (mode == AdbSourceMode.SystemPath)
            {
                var pathExe = FindSystemPathAdb();
                try
                {
                    if (!string.IsNullOrEmpty(pathExe))
                    {
                        _server.StartServer(pathExe, restartServerIfNewer: false);
                        IsUsingBuiltInAdb = IsPathBuiltInAdb(pathExe, builtInPath);
                    }
                    else if (serverRunning)
                    {
                        IsUsingBuiltInAdb = IsPathBuiltInAdb(runningPath, builtInPath);
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"ADB connection failed ({ex.Message}). Please check ADB configuration in Settings.");
                }
            }
            else
            {
                // Highest Priority (Default): Prioritize calling already-running ADB, otherwise launch built-in ADB
                try
                {
                    if (serverRunning || anyProcessRunning)
                    {
                        if (!serverRunning && !string.IsNullOrEmpty(runningPath))
                        {
                            _server.StartServer(runningPath, restartServerIfNewer: false);
                        }
                        IsUsingBuiltInAdb = IsPathBuiltInAdb(runningPath, builtInPath);
                    }
                    else
                    {
                        var targetExe = builtInPath ?? FindSystemPathAdb();
                        if (!string.IsNullOrEmpty(targetExe))
                        {
                            _server.StartServer(targetExe, restartServerIfNewer: false);
                            IsUsingBuiltInAdb = IsPathBuiltInAdb(targetExe, builtInPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"ADB connection failed ({ex.Message}). Please check ADB configuration in Settings.");
                }
            }

            try
            {
                var rawDevices = _client.GetDevices()
                    .Where(d => !FileHashHelper.IsLoopbackOrDisallowedEndpoint(d.Serial))
                    .ToList();

                foreach (var unauthorized in rawDevices.Where(d => d.State == DeviceState.Unauthorized))
                {
                    NotifyUnauthorizedIfNeeded(unauthorized.Serial);
                }

                CurrentDevice = rawDevices.FirstOrDefault(d => d.State == DeviceState.Online);
                EnsureTrackDevicesMonitorRunning();
            }
            catch (Exception ex)
            {
                throw new Exception($"ADB connection failed ({ex.Message}). Please check ADB configuration in Settings.");
            }
        }

        public List<DeviceData> GetDevices()
        {
            try
            {
                var list = _client.GetDevices()
                    .Where(d => !FileHashHelper.IsLoopbackOrDisallowedEndpoint(d.Serial))
                    .ToList();

                foreach (var unauthorized in list.Where(d => d.State == DeviceState.Unauthorized))
                {
                    NotifyUnauthorizedIfNeeded(unauthorized.Serial);
                }

                return list;
            }
            catch
            {
                return new List<DeviceData>();
            }
        }

        #region adb track-devices Real-Time Monitor & Exit Cleanup

        private void NotifyUnauthorizedIfNeeded(string? serial)
        {
            if (string.IsNullOrWhiteSpace(serial) || FileHashHelper.IsLoopbackOrDisallowedEndpoint(serial))
                return;

            bool shouldNotify = false;
            lock (_monitorLock)
            {
                if (_notifiedUnauthorizedSerials.Add(serial))
                {
                    shouldNotify = true;
                }
            }

            if (shouldNotify)
            {
                UnauthorizedDeviceDetected?.Invoke(serial);
            }
        }

        public void ResetUnauthorizedPromptState()
        {
            lock (_monitorLock)
            {
                _notifiedUnauthorizedSerials.Clear();
            }
        }

        public void EnsureTrackDevicesMonitorRunning()
        {
            lock (_monitorLock)
            {
                if (_monitorCts != null && !_monitorCts.IsCancellationRequested)
                {
                    return;
                }

                _monitorCts = new CancellationTokenSource();
                var ct = _monitorCts.Token;
                _ = Task.Run(() => RunTrackDevicesLoopAsync(ct), ct);
            }
        }

        public void StopTrackDevicesMonitor()
        {
            lock (_monitorLock)
            {
                try
                {
                    _monitorCts?.Cancel();
                    _monitorCts?.Dispose();
                }
                catch { }
                _monitorCts = null;
            }
        }

        private async Task RunTrackDevicesLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var tcp = new TcpClient();
                    var endPoint = _client.EndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.Loopback, 5037);
                    await tcp.ConnectAsync(endPoint.Address, endPoint.Port, ct).ConfigureAwait(false);

                    using var stream = tcp.GetStream();
                    byte[] cmdBytes = Encoding.ASCII.GetBytes("0012host:track-devices");
                    await stream.WriteAsync(cmdBytes, 0, cmdBytes.Length, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);

                    string? status = await ReadExactAsciiAsync(stream, 4, ct).ConfigureAwait(false);
                    if (!string.Equals(status, "OKAY", StringComparison.Ordinal))
                    {
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                        continue;
                    }

                    while (!ct.IsCancellationRequested)
                    {
                        string? lenHex = await ReadExactAsciiAsync(stream, 4, ct).ConfigureAwait(false);
                        if (lenHex == null) break;

                        if (!int.TryParse(lenHex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int payloadLen) || payloadLen < 0)
                        {
                            break;
                        }

                        string payload = "";
                        if (payloadLen > 0)
                        {
                            byte[] buf = new byte[payloadLen];
                            int readTotal = 0;
                            while (readTotal < payloadLen)
                            {
                                int r = await stream.ReadAsync(buf, readTotal, payloadLen - readTotal, ct).ConfigureAwait(false);
                                if (r <= 0) break;
                                readTotal += r;
                            }
                            if (readTotal < payloadLen) break;
                            payload = Encoding.UTF8.GetString(buf, 0, payloadLen);
                        }

                        ProcessTrackDevicesPayload(payload);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // ADB server may not be started yet or restarted; retry after short delay
                    try
                    {
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private static async Task<string?> ReadExactAsciiAsync(NetworkStream stream, int length, CancellationToken ct)
        {
            byte[] buf = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int r = await stream.ReadAsync(buf, offset, length - offset, ct).ConfigureAwait(false);
                if (r <= 0) return null;
                offset += r;
            }
            return Encoding.ASCII.GetString(buf, 0, length);
        }

        private void ProcessTrackDevicesPayload(string payload)
        {
            var lines = payload.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var currentUnauthorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalizedLines = new List<string>();

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                string serial = parts[0].Trim();
                string state = parts[1].Trim();

                if (FileHashHelper.IsLoopbackOrDisallowedEndpoint(serial))
                {
                    continue;
                }

                normalizedLines.Add($"{serial}\t{state}");

                if (string.Equals(state, "unauthorized", StringComparison.OrdinalIgnoreCase))
                {
                    currentUnauthorized.Add(serial);
                    NotifyUnauthorizedIfNeeded(serial);
                }
            }

            lock (_monitorLock)
            {
                _notifiedUnauthorizedSerials.RemoveWhere(s => !currentUnauthorized.Contains(s));
            }

            string snapshot = string.Join("\n", normalizedLines);
            if (!string.Equals(_lastTrackedSnapshot, snapshot, StringComparison.Ordinal))
            {
                _lastTrackedSnapshot = snapshot;
                TrackedDevicesChanged?.Invoke();
            }
        }

        /// <summary>
        /// Stops the track-devices monitor and kills the built-in ADB process on application exit
        /// when using built-in ADB and the setting is enabled (Default: true).
        /// </summary>
        public void ShutdownOnAppExit()
        {
            StopTrackDevicesMonitor();

            try
            {
                var settings = SettingsService.Load();
                if (!settings.KillBuiltInAdbOnExit)
                {
                    return;
                }

                string? builtInPath = GetBuiltInAdbExecutablePath();
                foreach (var proc in Process.GetProcessesByName("adb"))
                {
                    try
                    {
                        bool isBuiltInProcess = IsUsingBuiltInAdb;
                        if (!isBuiltInProcess && !string.IsNullOrEmpty(builtInPath))
                        {
                            string? procFile = null;
                            try { procFile = proc.MainModule?.FileName; } catch { }
                            if (IsPathBuiltInAdb(procFile, builtInPath))
                            {
                                isBuiltInProcess = true;
                            }
                        }

                        if (isBuiltInProcess)
                        {
                            proc.Kill(entireProcessTree: true);
                        }
                    }
                    catch { }
                    finally
                    {
                        try { proc.Dispose(); } catch { }
                    }
                }
            }
            catch { }
        }

        #endregion

        #region Post-Transfer Hash Verification

        public Task<(bool IsMatch, string Algorithm, string LocalHash, string RemoteHash)> VerifyTransferredFileHashAsync(
            string localFilePath,
            string remoteFilePath,
            CancellationToken token)
        {
            return Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                var qRemote = EscapeShellSingleQuotes(remoteFilePath.Replace('\\', '/'));
                string rawOutput = ExecuteUtf8ShellScript($"sha256sum {qRemote} 2>/dev/null || md5sum {qRemote} 2>/dev/null");
                string? remoteHash = FileHashHelper.ParseShellHashOutput(rawOutput);

                if (!string.IsNullOrEmpty(remoteHash) && remoteHash.Length == 32)
                {
                    string localMd5 = await FileHashHelper.ComputeFileMd5Async(localFilePath, token).ConfigureAwait(false);
                    bool match = string.Equals(localMd5, remoteHash, StringComparison.OrdinalIgnoreCase);
                    return (match, "MD5", localMd5, remoteHash);
                }
                else
                {
                    string localSha256 = await FileHashHelper.ComputeFileSha256Async(localFilePath, token).ConfigureAwait(false);
                    bool match = !string.IsNullOrEmpty(remoteHash) &&
                                 string.Equals(localSha256, remoteHash, StringComparison.OrdinalIgnoreCase);
                    return (match, "SHA-256", localSha256, remoteHash ?? "unavailable");
                }
            }, token);
        }

        #endregion

        public async Task SyncPcToAndroidAsync(
            string pcPath,
            string androidPath,
            Action<int> progressCallback,
            Action<string> logCallback,
            CancellationToken token,
            bool isMirrorMode = true,
            bool skipOverlongFiles = false)
        {
            if (CurrentDevice == null) throw new Exception("No Android device connected.");

            bool verifyHash = SettingsService.Load().VerifyHashAfterTransfer;
            pcPath = Path.GetFullPath(pcPath.Trim());
            androidPath = androidPath.Replace('\\', '/').TrimEnd('/');

            logCallback($"Scanning PC directory: {pcPath}");
            var localFiles = Directory.GetFiles(pcPath, "*.*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var name = Path.GetFileName(f);
                    return !(name.StartsWith(".syncdroid_", StringComparison.OrdinalIgnoreCase) &&
                             name.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
                })
                .ToArray();

            logCallback($"Scanning Android directory: {androidPath}");
            var remoteFilesInfo = GetRemoteDirectoryTree(androidPath);

            int totalFiles = localFiles.Length;
            int processed = 0;
            int phase1Weight = isMirrorMode ? 50 : 100;

            // 1. Push only new or size-changed files from PC to Android; leave identical files completely untouched
            foreach (var localFile in localFiles)
            {
                token.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(pcPath, localFile).Replace('\\', '/');

                if (FileNameLengthHelper.IsFileNameOver255Bytes(localFile))
                {
                    logCallback($"Skipped (Filename > 255 bytes): {relativePath}");
                    processed++;
                    progressCallback((processed * phase1Weight) / (totalFiles > 0 ? totalFiles : 1));
                    continue;
                }

                var targetRemotePath = $"{androidPath}/{relativePath}";

                var fileInfo = new FileInfo(localFile);
                bool shouldPush = true;

                if (remoteFilesInfo.TryGetValue(targetRemotePath, out var remoteStat))
                {
                    if (remoteStat.Size == (uint)fileInfo.Length)
                    {
                        shouldPush = false;
                        logCallback($"Skipped (Identical): {relativePath}");
                    }
                }

                if (shouldPush)
                {
                    logCallback($"Pushing: {relativePath}");
                    await PushFileAsync(fileInfo, targetRemotePath, token);

                    if (verifyHash)
                    {
                        var (isMatch, algo, localHash, remoteHash) = await VerifyTransferredFileHashAsync(fileInfo.FullName, targetRemotePath, token);
                        if (!isMatch)
                        {
                            try { DeleteRemoteFile(targetRemotePath); } catch { }
                            throw new IOException($"Hash verification failed ({algo}) for '{relativePath}' (Local: {localHash}, Remote: {remoteHash}). Interrupted or corrupted remote file has been removed.");
                        }
                        logCallback($"Hash verified ({algo}): {relativePath}");
                    }
                }

                processed++;
                progressCallback((processed * phase1Weight) / (totalFiles > 0 ? totalFiles : 1));
            }

            // 2. Delete remote files that don't exist on PC (only in /MIR Mirror mode)
            if (isMirrorMode)
            {
                var localRelativePaths = new HashSet<string>(
                    localFiles.Select(f => $"{androidPath}/{Path.GetRelativePath(pcPath, f).Replace('\\', '/')}"),
                    StringComparer.OrdinalIgnoreCase);

                var filesToDelete = remoteFilesInfo.Keys.Where(remote => !localRelativePaths.Contains(remote)).ToList();

                int deleteTotal = filesToDelete.Count;
                int deleteProcessed = 0;

                foreach (var remoteFile in filesToDelete)
                {
                    token.ThrowIfCancellationRequested();
                    logCallback($"Deleting remote extra file: {remoteFile}");
                    DeleteRemoteFile(remoteFile);
                    deleteProcessed++;
                    progressCallback(50 + (deleteProcessed * 50) / (deleteTotal > 0 ? deleteTotal : 1));
                }
            }
            else
            {
                logCallback("Incremental Merge mode: Keeping existing extra files on Android.");
            }

            logCallback("Sync (PC to Android) completed successfully.");
            progressCallback(100);
        }

        public async Task SyncAndroidToPcAsync(
            string androidPath,
            string pcPath,
            Action<int> progressCallback,
            Action<string> logCallback,
            CancellationToken token,
            bool isMirrorMode = true)
        {
            if (CurrentDevice == null) throw new Exception("No Android device connected.");

            bool verifyHash = SettingsService.Load().VerifyHashAfterTransfer;
            pcPath = Path.GetFullPath(pcPath.Trim());
            androidPath = androidPath.Replace('\\', '/').TrimEnd('/');

            logCallback($"Scanning Android directory: {androidPath}");
            var remoteFilesInfo = GetRemoteDirectoryTree(androidPath);

            logCallback($"Scanning PC directory: {pcPath}");
            var localFiles = Directory.Exists(pcPath)
                ? Directory.GetFiles(pcPath, "*.*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var name = Path.GetFileName(f);
                        return !(name.StartsWith(".syncdroid_", StringComparison.OrdinalIgnoreCase) &&
                                 name.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
                    })
                    .ToArray()
                : Array.Empty<string>();
            var localFilesInfo = localFiles.ToDictionary(f => f, f => new FileInfo(f), StringComparer.OrdinalIgnoreCase);

            int totalFiles = remoteFilesInfo.Count;
            int processed = 0;
            int phase1Weight = isMirrorMode ? 50 : 100;
            var expectedLocalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Pull only new or size-changed files from Android to PC; leave identical files completely untouched
            foreach (var kvp in remoteFilesInfo)
            {
                token.ThrowIfCancellationRequested();
                var remoteFile = kvp.Key;
                var rawRelativePath = remoteFile.Substring(androidPath.Length).TrimStart('/');
                var sanitizedRelPath = CompanionSyncService.SanitizeRelativePathForWindows(rawRelativePath);
                var targetLocalPath = Path.Combine(pcPath, sanitizedRelPath.Replace('/', '\\'));
                expectedLocalPaths.Add(targetLocalPath);

                bool shouldPull = true;
                if (localFilesInfo.TryGetValue(targetLocalPath, out var localStat))
                {
                    if (localStat.Length == kvp.Value.Size)
                    {
                        shouldPull = false;
                        logCallback($"Skipped (Identical): {sanitizedRelPath}");
                    }
                }

                if (shouldPull)
                {
                    logCallback($"Pulling: {sanitizedRelPath}");
                    Directory.CreateDirectory(Path.GetDirectoryName(targetLocalPath)!);
                    await PullFileAsync(remoteFile, targetLocalPath, kvp.Value.Time.DateTime, token);

                    if (verifyHash)
                    {
                        var (isMatch, algo, localHash, remoteHash) = await VerifyTransferredFileHashAsync(targetLocalPath, remoteFile, token);
                        if (!isMatch)
                        {
                            try { if (File.Exists(targetLocalPath)) File.Delete(targetLocalPath); } catch { }
                            throw new IOException($"Hash verification failed ({algo}) for '{sanitizedRelPath}' (Local: {localHash}, Remote: {remoteHash}). Interrupted or corrupted local file has been removed.");
                        }
                        logCallback($"Hash verified ({algo}): {sanitizedRelPath}");
                    }
                }

                processed++;
                progressCallback((processed * phase1Weight) / (totalFiles > 0 ? totalFiles : 1));
            }

            // 2. Delete local files that don't exist on Android (only in /MIR Mirror mode)
            if (isMirrorMode)
            {
                var filesToDelete = localFiles.Where(local => !expectedLocalPaths.Contains(local)).ToList();

                int deleteTotal = filesToDelete.Count;
                int deleteProcessed = 0;

                foreach (var localFile in filesToDelete)
                {
                    token.ThrowIfCancellationRequested();
                    logCallback($"Deleting local extra file: {localFile}");
                    File.Delete(localFile);
                    deleteProcessed++;
                    progressCallback(50 + (deleteProcessed * 50) / (deleteTotal > 0 ? deleteTotal : 1));
                }
            }
            else
            {
                logCallback("Incremental Merge mode: Keeping existing extra files on PC.");
            }

            logCallback("Sync (Android to PC) completed successfully.");
            progressCallback(100);
        }

        public async Task ExecuteDirectTransferToAndroidAsync(
            IReadOnlyList<TransferQueueItem> queueItems,
            string targetAndroidDir,
            ConflictPolicy conflictPolicy,
            Action<int> progressCallback,
            Action<string> logCallback,
            CancellationToken token)
        {
            if (CurrentDevice == null) throw new Exception("No Android device connected.");
            bool verifyHash = SettingsService.Load().VerifyHashAfterTransfer;
            targetAndroidDir = targetAndroidDir.Replace('\\', '/').TrimEnd('/');

            var filePlan = CompanionSyncService.ExpandQueueItemsToFiles(queueItems);
            int total = filePlan.Count;
            if (total == 0)
            {
                logCallback("No files found in the selected queue items.");
                progressCallback(100);
                return;
            }

            Dictionary<string, FileStatistics>? remoteTree = null;
            if (conflictPolicy != ConflictPolicy.Overwrite)
            {
                logCallback($"Scanning target device directory: {targetAndroidDir}");
                remoteTree = await Task.Run(() => GetRemoteDirectoryTree(targetAndroidDir), token);
            }

            int processed = 0;
            foreach (var (fileInfo, relativeDestPath) in filePlan)
            {
                token.ThrowIfCancellationRequested();

                if (FileNameLengthHelper.IsFileNameOver255Bytes(fileInfo.FullName))
                {
                    logCallback($"Skipped (Filename > 255 bytes): {relativeDestPath}");
                    processed++;
                    progressCallback((processed * 100) / total);
                    continue;
                }

                string remoteFullPath = $"{targetAndroidDir}/{relativeDestPath}";

                if (conflictPolicy == ConflictPolicy.SkipIdenticalSize &&
                    remoteTree != null &&
                    remoteTree.TryGetValue(remoteFullPath, out var existingStat) &&
                    existingStat.Size == (uint)fileInfo.Length)
                {
                    logCallback($"Skipped (Identical): {relativeDestPath}");
                    processed++;
                    progressCallback((processed * 100) / total);
                    continue;
                }

                if (conflictPolicy == ConflictPolicy.KeepBothRename &&
                    remoteTree != null &&
                    remoteTree.ContainsKey(remoteFullPath))
                {
                    string parent = remoteFullPath.Substring(0, remoteFullPath.LastIndexOf('/'));
                    string fileName = Path.GetFileName(remoteFullPath);
                    string stem = Path.GetFileNameWithoutExtension(fileName);
                    string ext = Path.GetExtension(fileName);
                    int idx = 1;
                    while (true)
                    {
                        string candidate = $"{parent}/{stem} ({idx}){ext}";
                        if (!remoteTree.ContainsKey(candidate))
                        {
                            remoteFullPath = candidate;
                            break;
                        }
                        idx++;
                    }
                }

                logCallback($"Pushing: {relativeDestPath}");
                await PushFileAsync(fileInfo, remoteFullPath, token);

                if (verifyHash)
                {
                    var (isMatch, algo, localHash, remoteHash) = await VerifyTransferredFileHashAsync(fileInfo.FullName, remoteFullPath, token);
                    if (!isMatch)
                    {
                        try { DeleteRemoteFile(remoteFullPath); } catch { }
                        throw new IOException($"Hash verification failed ({algo}) for '{relativeDestPath}' (Local: {localHash}, Remote: {remoteHash}). Interrupted or corrupted remote file has been removed.");
                    }
                    logCallback($"Hash verified ({algo}): {relativeDestPath}");
                }

                processed++;
                progressCallback((processed * 100) / total);
            }

            logCallback($"Direct transfer to device completed ({processed} file(s) processed).");
            progressCallback(100);
        }

        public async Task ExecuteDirectPullFromAndroidAsync(
            string remoteItemPath,
            bool isDirectory,
            string targetPcDir,
            ConflictPolicy conflictPolicy,
            Action<int> progressCallback,
            Action<string> logCallback,
            CancellationToken token)
        {
            if (CurrentDevice == null) throw new Exception("No Android device connected.");
            bool verifyHash = SettingsService.Load().VerifyHashAfterTransfer;
            remoteItemPath = remoteItemPath.Replace('\\', '/').TrimEnd('/');
            targetPcDir = Path.GetFullPath(targetPcDir.Trim());
            Directory.CreateDirectory(targetPcDir);

            string itemName = Path.GetFileName(remoteItemPath);

            if (!isDirectory)
            {
                string sanitizedName = CompanionSyncService.SanitizeRelativePathForWindows(itemName);
                string localTarget = Path.Combine(targetPcDir, sanitizedName);
                if (File.Exists(localTarget) && conflictPolicy == ConflictPolicy.KeepBothRename)
                {
                    localTarget = CompanionSyncService.GetUniqueKeepBothLocalPath(localTarget);
                }

                logCallback($"Pulling file: {remoteItemPath}");
                await PullFileAsync(remoteItemPath, localTarget, DateTime.Now, token);

                if (verifyHash)
                {
                    var (isMatch, algo, localHash, remoteHash) = await VerifyTransferredFileHashAsync(localTarget, remoteItemPath, token);
                    if (!isMatch)
                    {
                        try { if (File.Exists(localTarget)) File.Delete(localTarget); } catch { }
                        throw new IOException($"Hash verification failed ({algo}) for '{sanitizedName}' (Local: {localHash}, Remote: {remoteHash}). Interrupted or corrupted local file has been removed.");
                    }
                    logCallback($"Hash verified ({algo}): {sanitizedName}");
                }

                progressCallback(100);
                logCallback($"Pulled to: {localTarget}");
                return;
            }

            logCallback($"Scanning device folder: {remoteItemPath}");
            var remoteFiles = await Task.Run(() => GetRemoteDirectoryTree(remoteItemPath), token);
            int total = remoteFiles.Count;
            int processed = 0;
            string rootFolderName = CompanionSyncService.SanitizeRelativePathForWindows(itemName);

            foreach (var kvp in remoteFiles)
            {
                token.ThrowIfCancellationRequested();
                string rawRel = kvp.Key.Substring(remoteItemPath.Length).TrimStart('/');
                string sanitizedRel = CompanionSyncService.SanitizeRelativePathForWindows(rawRel);
                string localTarget = Path.Combine(targetPcDir, rootFolderName, sanitizedRel.Replace('/', '\\'));

                if (File.Exists(localTarget))
                {
                    if (conflictPolicy == ConflictPolicy.SkipIdenticalSize && new FileInfo(localTarget).Length == kvp.Value.Size)
                    {
                        logCallback($"Skipped (Identical): {rootFolderName}/{sanitizedRel}");
                        processed++;
                        progressCallback((processed * 100) / (total > 0 ? total : 1));
                        continue;
                    }
                    if (conflictPolicy == ConflictPolicy.KeepBothRename)
                    {
                        localTarget = CompanionSyncService.GetUniqueKeepBothLocalPath(localTarget);
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(localTarget)!);
                logCallback($"Pulling: {rootFolderName}/{sanitizedRel}");
                await PullFileAsync(kvp.Key, localTarget, kvp.Value.Time.DateTime, token);

                if (verifyHash)
                {
                    var (isMatch, algo, localHash, remoteHash) = await VerifyTransferredFileHashAsync(localTarget, kvp.Key, token);
                    if (!isMatch)
                    {
                        try { if (File.Exists(localTarget)) File.Delete(localTarget); } catch { }
                        throw new IOException($"Hash verification failed ({algo}) for '{rootFolderName}/{sanitizedRel}' (Local: {localHash}, Remote: {remoteHash}). Interrupted or corrupted local file has been removed.");
                    }
                    logCallback($"Hash verified ({algo}): {rootFolderName}/{sanitizedRel}");
                }

                processed++;
                progressCallback((processed * 100) / (total > 0 ? total : 1));
            }

            logCallback($"Direct pull from device completed ({processed} file(s)).");
            progressCallback(100);
        }

        public Task<List<RemoteEntryDto>> GetRemoteEntriesAsync(string remotePath)
        {
            return Task.Run(() =>
            {
                var device = CurrentDevice;
                if (device == null || device.State != DeviceState.Online)
                {
                    return new List<RemoteEntryDto>();
                }

                string normalized = string.IsNullOrWhiteSpace(remotePath) ? "/storage/emulated/0" : remotePath.Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrEmpty(normalized)) normalized = "/";

                var list = new List<RemoteEntryDto>();
                try
                {
                    using var syncService = new SyncService(_client, device);
                    var listings = syncService.GetDirectoryListing(ToAdbString(normalized));
                    foreach (var entry in listings)
                    {
                        var decodedName = FromAdbString(entry.Path);
                        if (string.IsNullOrWhiteSpace(decodedName) || decodedName == "." || decodedName == "..")
                            continue;

                        bool isDir = ((int)entry.FileMode & 61440) == 16384;
                        list.Add(new RemoteEntryDto
                        {
                            Name = decodedName,
                            FullPath = $"{normalized.TrimEnd('/')}/{decodedName}",
                            RelativePath = decodedName,
                            IsDirectory = isDir,
                            Size = entry.Size,
                            LastWriteTimeUtcTicks = entry.Time.UtcDateTime.Ticks
                        });
                    }
                }
                catch { }

                return list
                    .OrderByDescending(e => e.IsDirectory)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });
        }

        private Dictionary<string, FileStatistics> GetRemoteDirectoryTree(string remotePath)
        {
            var result = new Dictionary<string, FileStatistics>(StringComparer.OrdinalIgnoreCase);

            using var syncService = new SyncService(_client, CurrentDevice);

            void Traverse(string currentPath)
            {
                try
                {
                    var listings = syncService.GetDirectoryListing(ToAdbString(currentPath));
                    foreach (var entry in listings)
                    {
                        var decodedName = FromAdbString(entry.Path);
                        if (string.IsNullOrEmpty(decodedName) || decodedName == "." || decodedName == "..") continue;
                        if (decodedName.StartsWith(".syncdroid_", StringComparison.OrdinalIgnoreCase) &&
                            decodedName.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        entry.Path = decodedName;
                        var fullPath = $"{currentPath.TrimEnd('/')}/{decodedName}";

                        bool isDir = ((int)entry.FileMode & 61440) == 16384;

                        if (isDir)
                        {
                            Traverse(fullPath);
                        }
                        else
                        {
                            result[fullPath] = entry;
                        }
                    }
                }
                catch
                {
                    // Ignore permission denied or non-existent paths
                }
            }

            Traverse(remotePath);
            return result;
        }

        private Task PushFileAsync(FileInfo localFileInfo, string remotePath, CancellationToken token)
        {
            return Task.Run(() =>
            {
                string localPath = localFileInfo.FullName;
                remotePath = remotePath.Replace('\\', '/');

                var parentDir = remotePath.Substring(0, remotePath.LastIndexOf('/'));
                ExecuteUtf8ShellScript($"mkdir -p {EscapeShellSingleQuotes(parentDir)}");

                try
                {
                    using var stream = File.OpenRead(localPath);
                    using var syncService = new SyncService(_client, CurrentDevice);
                    syncService.Push(stream, ToAdbString(remotePath), 420, localFileInfo.LastWriteTime, null, token);
                }
                catch (OperationCanceledException)
                {
                    try { DeleteRemoteFile(remotePath); } catch { }
                    throw;
                }
                catch (Exception)
                {
                    try { DeleteRemoteFile(remotePath); } catch { }

                    // Fallback to push using intermediate pure-ASCII temp location /data/local/tmp
                    string tempPath = $"/data/local/tmp/sync_{Guid.NewGuid():N}.tmp";

                    try
                    {
                        using var stream = File.OpenRead(localPath);
                        using var syncService = new SyncService(_client, CurrentDevice);
                        syncService.Push(stream, tempPath, 420, localFileInfo.LastWriteTime, null, token);

                        var qTemp = EscapeShellSingleQuotes(tempPath);
                        var qTarget = EscapeShellSingleQuotes(remotePath);
                        ExecuteUtf8ShellScript($"mv -f {qTemp} {qTarget} || (cp -f {qTemp} {qTarget} && rm -f {qTemp})");
                    }
                    catch (OperationCanceledException)
                    {
                        try { ExecuteUtf8ShellScript($"rm -f {EscapeShellSingleQuotes(tempPath)} {EscapeShellSingleQuotes(remotePath)}"); } catch { }
                        throw;
                    }
                    catch (Exception fallbackEx)
                    {
                        try { ExecuteUtf8ShellScript($"rm -f {EscapeShellSingleQuotes(tempPath)} {EscapeShellSingleQuotes(remotePath)}"); } catch { }
                        throw new Exception($"Direct Push and Temp-Dir Fallback both failed. Error: {fallbackEx.Message}");
                    }
                }
            }, token);
        }

        private Task PullFileAsync(string remotePath, string localPath, DateTime remoteTime, CancellationToken token)
        {
            return Task.Run(() =>
            {
                string parentDir = Path.GetDirectoryName(localPath) ?? @"C:\";
                Directory.CreateDirectory(parentDir);
                string tempLocalPart = Path.Combine(parentDir, $".syncdroid_{Guid.NewGuid():N}.part");

                try
                {
                    try
                    {
                        using var stream = File.Create(tempLocalPart);
                        using var syncService = new SyncService(_client, CurrentDevice);
                        syncService.Pull(ToAdbString(remotePath), stream, null, token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        try { if (File.Exists(tempLocalPart)) File.Delete(tempLocalPart); } catch { }

                        string tempPath = $"/data/local/tmp/pull_{Guid.NewGuid():N}.tmp";
                        var qRemote = EscapeShellSingleQuotes(remotePath);
                        var qTemp = EscapeShellSingleQuotes(tempPath);
                        ExecuteUtf8ShellScript($"cp -f {qRemote} {qTemp}");
                        try
                        {
                            using var stream = File.Create(tempLocalPart);
                            using var syncService = new SyncService(_client, CurrentDevice);
                            syncService.Pull(tempPath, stream, null, token);
                        }
                        finally
                        {
                            try { ExecuteUtf8ShellScript($"rm -f {qTemp}"); } catch { }
                        }
                    }

                    File.Move(tempLocalPart, localPath, overwrite: true);

                    try
                    {
                        File.SetLastWriteTime(localPath, remoteTime);
                    }
                    catch { }
                }
                catch
                {
                    try
                    {
                        if (File.Exists(tempLocalPart)) File.Delete(tempLocalPart);
                    }
                    catch { }
                    throw;
                }
            }, token);
        }

        private void DeleteRemoteFile(string remotePath)
        {
            ExecuteUtf8ShellScript($"rm -f {EscapeShellSingleQuotes(remotePath)}");
        }

        public void ClearDirectoryCache()
        {
            lock (_cacheLock)
            {
                _directoryCache.Clear();
            }
        }

        public async Task PreScanStorageAsync(bool forceRefresh = false)
        {
            if (CurrentDevice == null || CurrentDevice.State != DeviceState.Online) return;

            try
            {
                if (forceRefresh)
                {
                    ClearDirectoryCache();
                }

                var storageDirs = await GetRemoteSubdirectoriesAsync("/storage", forceRefresh);
                foreach (var dir in storageDirs)
                {
                    if (dir == "self") continue;
                    var subPath = $"/storage/{dir}";
                    var level2 = await GetRemoteSubdirectoriesAsync(subPath, forceRefresh);
                    if (dir == "emulated")
                    {
                        foreach (var userDir in level2)
                        {
                            await GetRemoteSubdirectoriesAsync($"/storage/emulated/{userDir}", forceRefresh);
                        }
                    }
                }
            }
            catch
            {
                // Ignore background pre-scan errors
            }
        }

        public Task<List<string>> GetRemoteSubdirectoriesAsync(string remotePath, bool forceRefresh = false)
        {
            return Task.Run(() =>
            {
                var device = CurrentDevice;
                if (device == null || device.State != DeviceState.Online)
                {
                    return new List<string>();
                }

                string normalized = string.IsNullOrWhiteSpace(remotePath) ? "/storage" : remotePath.Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrEmpty(normalized)) normalized = "/";

                string cacheKey = $"{device.Serial}:{normalized}";
                if (!forceRefresh)
                {
                    lock (_cacheLock)
                    {
                        if (_directoryCache.TryGetValue(cacheKey, out var cached))
                        {
                            return cached.ToList();
                        }
                    }
                }

                var dirs = new HashSet<string>(StringComparer.Ordinal);

                if (normalized == "/storage")
                {
                    dirs.Add("emulated");
                }
                else if (normalized == "/storage/emulated")
                {
                    dirs.Add("0");
                }

                // 1. Try SyncService directory listing with UTF-8 <-> Latin1 bridging
                try
                {
                    using var syncService = new SyncService(_client, device);
                    var listings = syncService.GetDirectoryListing(ToAdbString(normalized));
                    foreach (var entry in listings)
                    {
                        var decodedName = FromAdbString(entry.Path);
                        if (string.IsNullOrWhiteSpace(decodedName) || decodedName == "." || decodedName == "..")
                            continue;

                        int modeType = (int)entry.FileMode & 61440;
                        bool isDir = modeType == 16384;
                        if (isDir)
                        {
                            dirs.Add(decodedName);
                        }
                    }
                }
                catch { }

                // 2. Fallback to UTF-8 shell 'ls -1 -p' if SyncService returned nothing
                if (dirs.Count == 0 || normalized == "/storage")
                {
                    try
                    {
                        var output = ExecuteUtf8ShellScript($"ls -1 -p {EscapeShellSingleQuotes(normalized + "/")}");
                        foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var line = rawLine.Trim();
                            if (line.EndsWith("/") && !line.StartsWith("ls:"))
                            {
                                var folderName = line.TrimEnd('/');
                                if (!string.IsNullOrWhiteSpace(folderName) && folderName != "." && folderName != "..")
                                {
                                    dirs.Add(folderName);
                                }
                            }
                        }
                    }
                    catch { }
                }

                var sorted = dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
                lock (_cacheLock)
                {
                    _directoryCache[cacheKey] = sorted;
                }
                return sorted;
            });
        }

        public Task<string> CreateRemoteDirectoryAsync(string parentPath, string newFolderName)
        {
            return Task.Run(() =>
            {
                var device = CurrentDevice ?? throw new Exception("No Android device connected.");
                var cleanParent = string.IsNullOrWhiteSpace(parentPath) ? "/storage/emulated/0" : parentPath.Replace('\\', '/').TrimEnd('/');
                var cleanName = newFolderName.Replace('\\', '/').Trim('/');
                if (string.IsNullOrWhiteSpace(cleanName))
                {
                    throw new Exception("Folder name cannot be empty.");
                }

                var fullPath = $"{cleanParent}/{cleanName}";
                var output = ExecuteUtf8ShellScript($"mkdir -p {EscapeShellSingleQuotes(fullPath)}").Trim();
                if (!string.IsNullOrEmpty(output) && (output.Contains("denied", StringComparison.OrdinalIgnoreCase) || output.Contains("mkdir:", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new Exception(output);
                }

                lock (_cacheLock)
                {
                    _directoryCache.Remove($"{device.Serial}:{cleanParent}");
                }

                return fullPath;
            });
        }
    }
}
