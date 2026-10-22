using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpAdbClient;
using SharpAdbClient.DeviceCommands;

namespace SyncDroidWinUI.Services
{
    public class AdbSyncService
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        private AdbClient _client;
        private AdbServer _server;
        private readonly Dictionary<string, List<string>> _directoryCache = new();
        private readonly object _cacheLock = new();

        public static AdbSyncService Instance { get; } = new AdbSyncService();

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

        public static string? ResolveAdbExecutablePath(AppSettings settings)
        {
            if (settings.UseCustomAdbPath)
            {
                var custom = settings.CustomAdbDirectory?.Trim() ?? "";
                if (string.IsNullOrEmpty(custom)) return null;

                if (custom.EndsWith("adb.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(custom))
                {
                    return custom;
                }

                var candidate = Path.Combine(custom, "adb.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                return null;
            }
            else
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim(), "adb.exe");
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch { }
                }
                return null;
            }
        }

        public void StartServerAndConnect()
        {
            var settings = SettingsService.Load();
            var adbExePath = ResolveAdbExecutablePath(settings);

            if (settings.UseCustomAdbPath && string.IsNullOrEmpty(adbExePath))
            {
                throw new Exception($"adb.exe not found in specified custom directory: '{settings.CustomAdbDirectory}'");
            }

            try
            {
                if (!string.IsNullOrEmpty(adbExePath))
                {
                    _server.StartServer(adbExePath, restartServerIfNewer: false);
                }

                var devices = _client.GetDevices();
                CurrentDevice = devices.FirstOrDefault(d => d.State == DeviceState.Online);
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
                return _client.GetDevices().ToList();
            }
            catch
            {
                return new List<DeviceData>();
            }
        }

        public async Task SyncPcToAndroidAsync(string pcPath, string androidPath, Action<int> progressCallback, Action<string> logCallback, CancellationToken token)
        {
            if (CurrentDevice == null) throw new Exception("No Android device connected.");

            pcPath = Path.GetFullPath(pcPath.Trim());
            androidPath = androidPath.Replace('\\', '/').TrimEnd('/');

            logCallback($"Scanning PC directory: {pcPath}");
            var localFiles = Directory.GetFiles(pcPath, "*.*", SearchOption.AllDirectories);

            logCallback($"Scanning Android directory: {androidPath}");
            var remoteFilesInfo = GetRemoteDirectoryTree(androidPath);

            int totalFiles = localFiles.Length;
            int processed = 0;

            // 1. Push only new or size-changed files from PC to Android; leave identical files completely untouched
            foreach (var localFile in localFiles)
            {
                token.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(pcPath, localFile).Replace('\\', '/');
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
                }

                processed++;
                progressCallback((processed * 50) / (totalFiles > 0 ? totalFiles : 1));
            }

            // 2. Delete remote files that don't exist on PC
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

            logCallback("Sync (PC to Android) completed successfully.");
            progressCallback(100);
        }

        public async Task SyncAndroidToPcAsync(string androidPath, string pcPath, Action<int> progressCallback, Action<string> logCallback, CancellationToken token)
        {
            if (CurrentDevice == null) throw new Exception("No Android device connected.");

            pcPath = Path.GetFullPath(pcPath.Trim());
            androidPath = androidPath.Replace('\\', '/').TrimEnd('/');

            logCallback($"Scanning Android directory: {androidPath}");
            var remoteFilesInfo = GetRemoteDirectoryTree(androidPath);

            logCallback($"Scanning PC directory: {pcPath}");
            var localFiles = Directory.Exists(pcPath) ? Directory.GetFiles(pcPath, "*.*", SearchOption.AllDirectories) : Array.Empty<string>();
            var localFilesInfo = localFiles.ToDictionary(f => f, f => new FileInfo(f), StringComparer.OrdinalIgnoreCase);

            int totalFiles = remoteFilesInfo.Count;
            int processed = 0;

            // 1. Pull only new or size-changed files from Android to PC; leave identical files completely untouched
            foreach (var kvp in remoteFilesInfo)
            {
                token.ThrowIfCancellationRequested();
                var remoteFile = kvp.Key;
                var relativePath = remoteFile.Substring(androidPath.Length).TrimStart('/');
                var targetLocalPath = Path.Combine(pcPath, relativePath.Replace('/', '\\'));

                bool shouldPull = true;
                if (localFilesInfo.TryGetValue(targetLocalPath, out var localStat))
                {
                    if (localStat.Length == kvp.Value.Size)
                    {
                        shouldPull = false;
                        logCallback($"Skipped (Identical): {relativePath}");
                    }
                }

                if (shouldPull)
                {
                    logCallback($"Pulling: {relativePath}");
                    Directory.CreateDirectory(Path.GetDirectoryName(targetLocalPath)!);
                    await PullFileAsync(remoteFile, targetLocalPath, kvp.Value.Time.DateTime, token);
                }

                processed++;
                progressCallback((processed * 50) / (totalFiles > 0 ? totalFiles : 1));
            }

            // 2. Delete local files that don't exist on Android
            var remoteRelativePaths = new HashSet<string>(
                remoteFilesInfo.Keys.Select(r => Path.Combine(pcPath, r.Substring(androidPath.Length).TrimStart('/').Replace('/', '\\'))),
                StringComparer.OrdinalIgnoreCase);

            var filesToDelete = localFiles.Where(local => !remoteRelativePaths.Contains(local)).ToList();

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

            logCallback("Sync (Android to PC) completed successfully.");
            progressCallback(100);
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
                catch (Exception)
                {
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
                    catch (Exception fallbackEx)
                    {
                        throw new Exception($"Direct Push and Temp-Dir Fallback both failed. Error: {fallbackEx.Message}");
                    }
                }
            }, token);
        }

        private Task PullFileAsync(string remotePath, string localPath, DateTime remoteTime, CancellationToken token)
        {
            return Task.Run(() =>
            {
                try
                {
                    using var stream = File.Create(localPath);
                    using var syncService = new SyncService(_client, CurrentDevice);
                    syncService.Pull(ToAdbString(remotePath), stream, null, token);
                }
                catch
                {
                    string tempPath = $"/data/local/tmp/pull_{Guid.NewGuid():N}.tmp";
                    var qRemote = EscapeShellSingleQuotes(remotePath);
                    var qTemp = EscapeShellSingleQuotes(tempPath);
                    ExecuteUtf8ShellScript($"cp -f {qRemote} {qTemp}");
                    try
                    {
                        using var stream = File.Create(localPath);
                        using var syncService = new SyncService(_client, CurrentDevice);
                        syncService.Pull(tempPath, stream, null, token);
                    }
                    finally
                    {
                        ExecuteUtf8ShellScript($"rm -f {qTemp}");
                    }
                }

                try
                {
                    File.SetLastWriteTime(localPath, remoteTime);
                }
                catch { }
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
