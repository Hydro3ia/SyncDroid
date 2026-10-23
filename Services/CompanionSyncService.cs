using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SyncDroidWinUI.Helpers;
using SyncDroidWinUI.Models;

namespace SyncDroidWinUI.Services
{
    public class CompanionSyncService
    {
        public const int UdpDiscoveryPort = 45820;
        public const int DefaultTcpPort = 45821;
        public const string AppVersion = "0.1.5.0";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public static CompanionSyncService Instance { get; } = new CompanionSyncService();

        public event Action<ConnectionMode>? ActiveConnectionModeChanged;

        private ConnectionMode _activeConnectionMode = ConnectionMode.Adb;
        public ConnectionMode ActiveConnectionMode
        {
            get => _activeConnectionMode;
            set
            {
                if (_activeConnectionMode != value)
                {
                    _activeConnectionMode = value;
                    ActiveConnectionModeChanged?.Invoke(value);
                }
            }
        }

        private CompanionPeer? _currentPeer;
        public CompanionPeer? CurrentPeer
        {
            get => _currentPeer;
            set
            {
                bool changed = _currentPeer?.Endpoint != value?.Endpoint;
                _currentPeer = value;
                if (changed && _currentPeer != null)
                {
                    _ = PreScanStorageAsync();
                }
            }
        }

        public bool IsServerRunning { get; private set; }
        public int CurrentListeningPort { get; private set; } = DefaultTcpPort;
        public string? LastServerError { get; private set; }

        public event Action<string>? ServerActivityLogged;

        private readonly HttpClient _httpClient;
        private readonly HttpClient _probeHttpClient;
        private readonly Dictionary<string, List<string>> _directoryCache = new();
        private readonly object _cacheLock = new();
        private readonly ConcurrentDictionary<string, CompanionPeer> _discoveredPeers = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _serverCts;
        private TcpListener? _tcpListener;
        private UdpClient? _udpResponder;

        private CompanionSyncService()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(30)
            };
            _probeHttpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(2200)
            };

            StartOrRestartServerFromSettings();
        }

        private void LogServerActivity(string message)
        {
            ServerActivityLogged?.Invoke($"[Companion Server] {message}");
        }

        public static List<string> GetLocalLanIpAddresses()
        {
            var ips = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    var props = ni.GetIPProperties();
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        {
                            string ipStr = ua.Address.ToString();
                            if (!ipStr.StartsWith("169.254.", StringComparison.Ordinal))
                            {
                                ips.Add(ipStr);
                            }
                        }
                    }
                }
            }
            catch { }

            return ips.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        #region Server Lifecycle (UDP 45820 Responder + TCP/HTTP 45821 Server)

        public void StartOrRestartServerFromSettings()
        {
            StopServer();

            var settings = SettingsService.Load();
            CurrentListeningPort = settings.CompanionPort > 0 && settings.CompanionPort <= 65535
                ? settings.CompanionPort
                : DefaultTcpPort;

            if (!settings.CompanionServerEnabled)
            {
                IsServerRunning = false;
                LastServerError = null;
                return;
            }

            _serverCts = new CancellationTokenSource();
            var ct = _serverCts.Token;

            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, CurrentListeningPort);
                _tcpListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _tcpListener.Start();
                IsServerRunning = true;
                LastServerError = null;

                _ = Task.Run(() => RunTcpAcceptLoopAsync(_tcpListener, ct), ct);
            }
            catch (Exception ex)
            {
                IsServerRunning = false;
                LastServerError = ex.Message;
            }

            try
            {
                _udpResponder = new UdpClient();
                _udpResponder.ExclusiveAddressUse = false;
                _udpResponder.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpResponder.EnableBroadcast = true;
                _udpResponder.Client.Bind(new IPEndPoint(IPAddress.Any, UdpDiscoveryPort));

                _ = Task.Run(() => RunUdpResponderLoopAsync(_udpResponder, ct), ct);
            }
            catch
            {
                // Ignore if UDP 45820 is already bound by another local tool
            }
        }

        public void StopServer()
        {
            try
            {
                _serverCts?.Cancel();
                _serverCts?.Dispose();
            }
            catch { }
            _serverCts = null;

            try { _tcpListener?.Stop(); } catch { }
            _tcpListener = null;

            try { _udpResponder?.Close(); _udpResponder?.Dispose(); } catch { }
            _udpResponder = null;

            IsServerRunning = false;
        }

        private async Task RunUdpResponderLoopAsync(UdpClient udp, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(ct);
                    string msg = Encoding.UTF8.GetString(result.Buffer).Trim();

                    if (msg.StartsWith("SYNCDROID_DISCOVER_V1|", StringComparison.Ordinal))
                    {
                        var senderIp = result.RemoteEndPoint.Address.ToString();
                        var parts = msg.Split('|');
                        if (!FileHashHelper.IsLoopbackOrDisallowedEndpoint(senderIp) &&
                            parts.Length >= 5 &&
                            string.Equals(parts[2], "Android", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(parts[4], out int peerPort))
                        {
                            var peer = new CompanionPeer
                            {
                                DeviceName = parts[1],
                                Platform = parts[2],
                                Version = parts[3],
                                IpAddress = senderIp,
                                Port = peerPort
                            };
                            _discoveredPeers[peer.Endpoint] = peer;
                        }

                        if (IsServerRunning)
                        {
                            string reply = $"SYNCDROID_HERE_V1|{Environment.MachineName}|Windows|{AppVersion}|{CurrentListeningPort}";
                            byte[] replyBytes = Encoding.UTF8.GetBytes(reply);
                            await udp.SendAsync(replyBytes, replyBytes.Length, result.RemoteEndPoint);
                        }
                    }
                    else if (msg.StartsWith("SYNCDROID_HERE_V1|", StringComparison.Ordinal))
                    {
                        TryParseAndStorePeer(msg, result.RemoteEndPoint.Address.ToString());
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
            }
        }

        private void TryParseAndStorePeer(string packet, string senderIp)
        {
            if (FileHashHelper.IsLoopbackOrDisallowedEndpoint(senderIp))
            {
                return;
            }

            var parts = packet.Split('|');
            if (parts.Length >= 5 && int.TryParse(parts[4], out int port))
            {
                string deviceName = parts[1];
                string platform = parts[2];
                string version = parts[3];

                var localIps = GetLocalLanIpAddresses();
                if (string.Equals(platform, "Windows", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(deviceName, Environment.MachineName, StringComparison.OrdinalIgnoreCase) &&
                    localIps.Contains(senderIp, StringComparer.OrdinalIgnoreCase) &&
                    port == CurrentListeningPort)
                {
                    return; // Ignore self
                }

                var peer = new CompanionPeer
                {
                    DeviceName = deviceName,
                    Platform = platform,
                    Version = version,
                    IpAddress = senderIp,
                    Port = port
                };
                _discoveredPeers[peer.Endpoint] = peer;
            }
        }

        private async Task RunTcpAcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => HandleHttpClientAsync(client, ct), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    if (ct.IsCancellationRequested) break;
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleHttpClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                client.ReceiveTimeout = 600000;
                client.SendTimeout = 600000;
                using var netStream = client.GetStream();

                var headerRead = await ReadHttpHeadersAsync(netStream, ct);
                if (headerRead == null) return;

                string headerText = headerRead.Value.HeaderText;
                byte[] leftoverBytes = headerRead.Value.LeftoverBytes;

                var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) return;

                var reqParts = lines[0].Split(' ');
                if (reqParts.Length < 2) return;

                string method = reqParts[0].ToUpperInvariant();
                string rawTarget = reqParts[1];

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrEmpty(line)) continue;
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                    }
                }

                if (headers.TryGetValue("Expect", out var expectVal) &&
                    expectVal.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] continueBytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    await netStream.WriteAsync(continueBytes, 0, continueBytes.Length, ct);
                    await netStream.FlushAsync(ct);
                }

                string routePath = rawTarget;
                string queryString = "";
                int qIdx = rawTarget.IndexOf('?');
                if (qIdx >= 0)
                {
                    routePath = rawTarget.Substring(0, qIdx);
                    queryString = rawTarget.Substring(qIdx + 1);
                }

                var query = ParseQueryString(queryString);
                long contentLength = -1;
                if (headers.TryGetValue("Content-Length", out var clStr) && long.TryParse(clStr, out long parsedCl))
                {
                    contentLength = parsedCl;
                }
                bool isChunked = headers.TryGetValue("Transfer-Encoding", out var teStr) &&
                                 teStr.Contains("chunked", StringComparison.OrdinalIgnoreCase);

                using var combinedBodyStream = new PreBufferedNetworkStream(leftoverBytes, netStream);

                try
                {
                    if (method == "GET" && string.Equals(routePath, "/api/v1/info", StringComparison.OrdinalIgnoreCase))
                    {
                        var info = BuildLocalCompanionInfo();
                        await WriteJsonResponseAsync(netStream, 200, "OK", info, ct);
                    }
                    else if (method == "GET" && string.Equals(routePath, "/api/v1/storage/list", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = query.TryGetValue("path", out var p) ? p : "";
                        bool includeFiles = query.TryGetValue("includeFiles", out var inc) &&
                                            string.Equals(inc, "true", StringComparison.OrdinalIgnoreCase);
                        var listing = BuildLocalDirectoryListing(path, includeFiles);
                        await WriteJsonResponseAsync(netStream, 200, "OK", listing, ct);
                    }
                    else if (method == "GET" && string.Equals(routePath, "/api/v1/storage/tree", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = query.TryGetValue("path", out var p) ? p : "";
                        var manifest = BuildLocalTreeManifest(path);
                        LogServerActivity($"Scanned PC directory tree: {path} ({manifest.Files.Count} files)");
                        await WriteJsonResponseAsync(netStream, 200, "OK", manifest, ct);
                    }
                    else if (method == "POST" && string.Equals(routePath, "/api/v1/storage/mkdir", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = NormalizeWindowsLocalPath(query.TryGetValue("path", out var p) ? p : "");
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            await WriteJsonResponseAsync(netStream, 400, "Bad Request", new { error = "Path is required" }, ct);
                            return;
                        }
                        Directory.CreateDirectory(path);
                        LogServerActivity($"Created folder: {path}");
                        await WriteJsonResponseAsync(netStream, 200, "OK", new { ok = true, path }, ct);
                    }
                    else if (method == "GET" && string.Equals(routePath, "/api/v1/file/hash", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = NormalizeWindowsLocalPath(query.TryGetValue("path", out var p) ? p : "");
                        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        {
                            await WriteJsonResponseAsync(netStream, 404, "Not Found", new { error = "File not found" }, ct);
                            return;
                        }

                        var fi = new FileInfo(path);
                        string sha256 = await FileHashHelper.ComputeFileSha256Async(path, ct);
                        await WriteJsonResponseAsync(netStream, 200, "OK", new { ok = true, sha256, size = fi.Length }, ct);
                    }
                    else if (method == "GET" && string.Equals(routePath, "/api/v1/file/pull", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = NormalizeWindowsLocalPath(query.TryGetValue("path", out var p) ? p : "");
                        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        {
                            await WriteJsonResponseAsync(netStream, 404, "Not Found", new { error = "File not found" }, ct);
                            return;
                        }

                        var fileInfo = new FileInfo(path);
                        string sha256 = SettingsService.Load().VerifyHashAfterTransfer
                            ? await FileHashHelper.ComputeFileSha256Async(path, ct)
                            : "";
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
                        string responseHeader =
                            "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: application/octet-stream\r\n" +
                            $"Content-Length: {fs.Length}\r\n" +
                            $"X-LastWriteTimeUtcTicks: {fileInfo.LastWriteTimeUtc.Ticks}\r\n" +
                            (!string.IsNullOrEmpty(sha256) ? $"X-File-Sha256: {sha256}\r\n" : "") +
                            "Connection: close\r\n\r\n";
                        byte[] respHeaderBytes = Encoding.ASCII.GetBytes(responseHeader);
                        await netStream.WriteAsync(respHeaderBytes, 0, respHeaderBytes.Length, ct);
                        await fs.CopyToAsync(netStream, 65536, ct);
                        await netStream.FlushAsync(ct);
                        LogServerActivity($"Sent file to Android: {path}");
                    }
                    else if (method == "POST" && string.Equals(routePath, "/api/v1/file/push", StringComparison.OrdinalIgnoreCase))
                    {
                        string rawPath = query.TryGetValue("path", out var p) ? p : "";
                        long mtimeTicks = query.TryGetValue("mtimeTicks", out var mt) && long.TryParse(mt, out long parsedMt) ? parsedMt : 0;
                        int conflictInt = query.TryGetValue("conflict", out var cf) && int.TryParse(cf, out int parsedCf) ? parsedCf : 1;
                        string? expectedSha256 = query.TryGetValue("sha256", out var qSha) && !string.IsNullOrWhiteSpace(qSha)
                            ? qSha.Trim()
                            : (headers.TryGetValue("X-File-Sha256", out var hSha) ? hSha.Trim() : null);
                        var conflict = Enum.IsDefined(typeof(ConflictPolicy), conflictInt)
                            ? (ConflictPolicy)conflictInt
                            : ConflictPolicy.Overwrite;

                        await HandleServerFilePushAsync(netStream, combinedBodyStream, rawPath, mtimeTicks, conflict, contentLength, isChunked, expectedSha256, ct);
                    }
                    else if (method == "POST" && string.Equals(routePath, "/api/v1/file/delete", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = NormalizeWindowsLocalPath(query.TryGetValue("path", out var p) ? p : "");
                        if (string.IsNullOrWhiteSpace(path) || IsProtectedRootPath(path))
                        {
                            await WriteJsonResponseAsync(netStream, 400, "Bad Request", new { error = "Cannot delete root or empty path" }, ct);
                            return;
                        }

                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            LogServerActivity($"Deleted file: {path}");
                        }
                        await WriteJsonResponseAsync(netStream, 200, "OK", new { ok = true }, ct);
                    }
                    else
                    {
                        await WriteJsonResponseAsync(netStream, 404, "Not Found", new { error = "Endpoint not found" }, ct);
                    }
                }
                catch (Exception ex)
                {
                    LogServerActivity($"Error handling {method} {routePath}: {ex.Message}");
                    try
                    {
                        await WriteJsonResponseAsync(netStream, 500, "Internal Server Error", new { error = ex.Message }, ct);
                    }
                    catch { }
                }
            }
        }

        private async Task HandleServerFilePushAsync(
            NetworkStream netStream,
            Stream bodyStream,
            string rawPath,
            long mtimeTicks,
            ConflictPolicy conflict,
            long contentLength,
            bool isChunked,
            string? expectedSha256,
            CancellationToken ct)
        {
            string normalizedTarget = SanitizeWindowsFullPath(NormalizeWindowsLocalPath(rawPath));
            if (string.IsNullOrWhiteSpace(normalizedTarget))
            {
                await WriteJsonResponseAsync(netStream, 400, "Bad Request", new { error = "Invalid target path" }, ct);
                return;
            }

            string parentDir = Path.GetDirectoryName(normalizedTarget) ?? @"C:\";
            Directory.CreateDirectory(parentDir);

            string finalTarget = normalizedTarget;
            if (File.Exists(finalTarget))
            {
                if (conflict == ConflictPolicy.SkipIdenticalSize && contentLength >= 0)
                {
                    var existingInfo = new FileInfo(finalTarget);
                    if (existingInfo.Length == contentLength)
                    {
                        await DrainBodyStreamAsync(bodyStream, contentLength, isChunked, ct);
                        LogServerActivity($"Skipped (Identical): {finalTarget}");
                        await WriteJsonResponseAsync(netStream, 200, "OK", new { ok = true, skipped = true, path = finalTarget }, ct);
                        return;
                    }
                }
                else if (conflict == ConflictPolicy.KeepBothRename)
                {
                    finalTarget = GetUniqueKeepBothLocalPath(finalTarget);
                }
            }

            string tempPartPath = Path.Combine(parentDir, $".syncdroid_{Guid.NewGuid():N}.part");
            long bytesWritten = 0;

            try
            {
                using (var fs = new FileStream(tempPartPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    if (isChunked)
                    {
                        bytesWritten = await CopyChunkedBodyToStreamAsync(bodyStream, fs, ct);
                    }
                    else if (contentLength >= 0)
                    {
                        bytesWritten = await CopyExactBytesAsync(bodyStream, fs, contentLength, ct);
                        if (bytesWritten != contentLength)
                        {
                            throw new IOException($"Incomplete upload stream: expected {contentLength} bytes, got {bytesWritten} bytes.");
                        }
                    }
                    else
                    {
                        await bodyStream.CopyToAsync(fs, 65536, ct);
                        bytesWritten = fs.Length;
                    }
                }

                if (conflict == ConflictPolicy.SkipIdenticalSize && File.Exists(finalTarget))
                {
                    var existingInfo = new FileInfo(finalTarget);
                    if (existingInfo.Length == bytesWritten)
                    {
                        try { File.Delete(tempPartPath); } catch { }
                        LogServerActivity($"Skipped (Identical): {finalTarget}");
                        await WriteJsonResponseAsync(netStream, 200, "OK", new { ok = true, skipped = true, path = finalTarget }, ct);
                        return;
                    }
                }

                if (SettingsService.Load().VerifyHashAfterTransfer && !string.IsNullOrWhiteSpace(expectedSha256))
                {
                    string actualSha256 = await FileHashHelper.ComputeFileSha256Async(tempPartPath, ct);
                    if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(tempPartPath); } catch { }
                        throw new IOException($"Hash verification failed (SHA-256) for '{finalTarget}'.");
                    }
                }

                File.Move(tempPartPath, finalTarget, overwrite: true);

                if (mtimeTicks > 0)
                {
                    try
                    {
                        File.SetLastWriteTimeUtc(finalTarget, new DateTime(mtimeTicks, DateTimeKind.Utc));
                    }
                    catch { }
                }

                LogServerActivity($"Received file from Android: {finalTarget}");
                await WriteJsonResponseAsync(netStream, 200, "OK", new { ok = true, path = finalTarget, size = bytesWritten }, ct);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPartPath)) File.Delete(tempPartPath);
                }
                catch { }
                throw;
            }
        }

        private CompanionInfoResponse BuildLocalCompanionInfo()
        {
            long freeBytes = 0;
            string defaultRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(defaultRoot)) defaultRoot = @"C:\";
            try
            {
                string rootDrive = Path.GetPathRoot(defaultRoot) ?? @"C:\";
                var di = new DriveInfo(rootDrive);
                if (di.IsReady) freeBytes = di.AvailableFreeSpace;
            }
            catch { }

            return new CompanionInfoResponse
            {
                DeviceName = Environment.MachineName,
                Platform = "Windows",
                Version = AppVersion,
                ListeningPort = CurrentListeningPort,
                FreeSpaceBytes = freeBytes,
                DefaultStorageRoot = defaultRoot
            };
        }

        private DirectoryListingResponse BuildLocalDirectoryListing(string rawPath, bool includeFiles)
        {
            string trimmed = rawPath?.Trim() ?? "";
            if (string.IsNullOrEmpty(trimmed) || trimmed == "/" || trimmed == "\\")
            {
                var driveResp = new DirectoryListingResponse
                {
                    CurrentPath = "",
                    ParentPath = "",
                    Entries = new List<RemoteEntryDto>()
                };

                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady) continue;
                        driveResp.Entries.Add(new RemoteEntryDto
                        {
                            Name = drive.Name,
                            FullPath = drive.RootDirectory.FullName,
                            RelativePath = drive.Name,
                            IsDirectory = true,
                            Size = 0,
                            LastWriteTimeUtcTicks = drive.RootDirectory.LastWriteTimeUtc.Ticks
                        });
                    }
                    catch { }
                }

                return driveResp;
            }

            string normalized = NormalizeWindowsLocalPath(trimmed);
            var parentDir = Directory.GetParent(normalized);
            var response = new DirectoryListingResponse
            {
                CurrentPath = normalized,
                ParentPath = parentDir?.FullName ?? "",
                Entries = new List<RemoteEntryDto>()
            };

            if (!Directory.Exists(normalized))
            {
                return response;
            }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(normalized).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var di = new DirectoryInfo(dir);
                        response.Entries.Add(new RemoteEntryDto
                        {
                            Name = di.Name,
                            FullPath = di.FullName,
                            RelativePath = di.Name,
                            IsDirectory = true,
                            Size = 0,
                            LastWriteTimeUtcTicks = di.LastWriteTimeUtc.Ticks
                        });
                    }
                    catch { }
                }
            }
            catch { }

            if (includeFiles)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(normalized).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            if (fi.Name.StartsWith(".syncdroid_", StringComparison.OrdinalIgnoreCase) &&
                                fi.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            response.Entries.Add(new RemoteEntryDto
                            {
                                Name = fi.Name,
                                FullPath = fi.FullName,
                                RelativePath = fi.Name,
                                IsDirectory = false,
                                Size = fi.Length,
                                LastWriteTimeUtcTicks = fi.LastWriteTimeUtc.Ticks
                            });
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return response;
        }

        private TreeManifestResponse BuildLocalTreeManifest(string rawRootPath)
        {
            string rootPath = NormalizeWindowsLocalPath(rawRootPath);
            var manifest = new TreeManifestResponse
            {
                RootPath = rootPath,
                Files = new List<RemoteEntryDto>()
            };

            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return manifest;
            }

            var stack = new Stack<string>();
            stack.Push(rootPath);

            while (stack.Count > 0)
            {
                string current = stack.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(current))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            if (fi.Name.StartsWith(".syncdroid_", StringComparison.OrdinalIgnoreCase) &&
                                fi.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            manifest.Files.Add(new RemoteEntryDto
                            {
                                Name = fi.Name,
                                FullPath = fi.FullName,
                                RelativePath = Path.GetRelativePath(rootPath, fi.FullName).Replace('\\', '/'),
                                IsDirectory = false,
                                Size = fi.Length,
                                LastWriteTimeUtcTicks = fi.LastWriteTimeUtc.Ticks
                            });
                        }
                        catch { }
                    }
                }
                catch { }

                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(current))
                    {
                        stack.Push(subDir);
                    }
                }
                catch { }
            }

            return manifest;
        }

        #endregion

        #region Discovery & Peer Connection (Client)

        public async Task<List<CompanionPeer>> ScanLanForPeersAsync(CancellationToken ct = default)
        {
            using var scanUdp = new UdpClient();
            scanUdp.EnableBroadcast = true;

            string probe = $"SYNCDROID_DISCOVER_V1|{Environment.MachineName}|Windows|{AppVersion}|{CurrentListeningPort}";
            byte[] probeBytes = Encoding.UTF8.GetBytes(probe);

            var broadcastTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "255.255.255.255"
            };

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        {
                            var ipBytes = ua.Address.GetAddressBytes();
                            broadcastTargets.Add($"{ipBytes[0]}.{ipBytes[1]}.{ipBytes[2]}.255");

                            if (ua.IPv4Mask != null)
                            {
                                var maskBytes = ua.IPv4Mask.GetAddressBytes();
                                if (ipBytes.Length == 4 && maskBytes.Length == 4)
                                {
                                    byte b0 = (byte)(ipBytes[0] | ~maskBytes[0]);
                                    byte b1 = (byte)(ipBytes[1] | ~maskBytes[1]);
                                    byte b2 = (byte)(ipBytes[2] | ~maskBytes[2]);
                                    byte b3 = (byte)(ipBytes[3] | ~maskBytes[3]);
                                    broadcastTargets.Add($"{b0}.{b1}.{b2}.{b3}");
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            foreach (var targetIp in broadcastTargets)
            {
                try
                {
                    if (IPAddress.TryParse(targetIp, out var ip))
                    {
                        await scanUdp.SendAsync(probeBytes, probeBytes.Length, new IPEndPoint(ip, UdpDiscoveryPort));
                    }
                }
                catch { }
            }

            using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            receiveCts.CancelAfter(750);

            while (!receiveCts.IsCancellationRequested)
            {
                try
                {
                    var res = await scanUdp.ReceiveAsync(receiveCts.Token);
                    string text = Encoding.UTF8.GetString(res.Buffer).Trim();
                    if (text.StartsWith("SYNCDROID_HERE_V1|", StringComparison.Ordinal))
                    {
                        TryParseAndStorePeer(text, res.RemoteEndPoint.Address.ToString());
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }

            // Also probe saved last endpoint and current peer to verify online status
            var settings = SettingsService.Load();
            if (!string.IsNullOrWhiteSpace(settings.LastCompanionIpEndpoint) &&
                !FileHashHelper.IsLoopbackOrDisallowedEndpoint(settings.LastCompanionIpEndpoint))
            {
                var savedPeer = await ProbePeerEndpointAsync(settings.LastCompanionIpEndpoint, ct);
                if (savedPeer != null)
                {
                    _discoveredPeers[savedPeer.Endpoint] = savedPeer;
                }
            }

            // Verify all cached peers are still reachable
            var verifyTasks = _discoveredPeers.Values.Select(async p =>
            {
                if (FileHashHelper.IsLoopbackOrDisallowedEndpoint(p.Endpoint))
                {
                    _discoveredPeers.TryRemove(p.Endpoint, out _);
                    return null;
                }
                var verified = await ProbePeerEndpointAsync(p.Endpoint, ct);
                if (verified != null)
                {
                    return verified;
                }
                _discoveredPeers.TryRemove(p.Endpoint, out _);
                return null;
            });

            var verifiedList = await Task.WhenAll(verifyTasks);
            return verifiedList
                .Where(p => p != null)
                .Select(p => p!)
                .OrderBy(p => p.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<CompanionPeer?> ProbePeerEndpointAsync(string rawEndpoint, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(rawEndpoint)) return null;
            if (FileHashHelper.IsLoopbackOrDisallowedEndpoint(rawEndpoint)) return null;

            string trimmed = rawEndpoint.Trim();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring("http://".Length);
            if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring("https://".Length);
            trimmed = trimmed.TrimEnd('/');

            string host = trimmed;
            int port = DefaultTcpPort;
            int colonIdx = trimmed.LastIndexOf(':');
            if (colonIdx > 0 && int.TryParse(trimmed.Substring(colonIdx + 1), out int parsedPort) && parsedPort > 0 && parsedPort <= 65535)
            {
                host = trimmed.Substring(0, colonIdx);
                port = parsedPort;
            }

            try
            {
                string url = $"http://{host}:{port}/api/v1/info";
                using var response = await _probeHttpClient.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync(ct);
                var info = JsonSerializer.Deserialize<CompanionInfoResponse>(json, JsonOptions);
                if (info == null) return null;

                var peer = new CompanionPeer
                {
                    DeviceName = string.IsNullOrWhiteSpace(info.DeviceName) ? host : info.DeviceName,
                    Platform = string.IsNullOrWhiteSpace(info.Platform) ? "Android" : info.Platform,
                    Version = info.Version ?? "",
                    IpAddress = host,
                    Port = port
                };

                _discoveredPeers[peer.Endpoint] = peer;
                return peer;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Remote Storage Browsing & Sync Operations (PC -> Android & Android -> PC)

        public void ClearDirectoryCache()
        {
            lock (_cacheLock)
            {
                _directoryCache.Clear();
            }
        }

        public void DisconnectCurrentPeer()
        {
            var peer = CurrentPeer;
            CurrentPeer = null;
            if (peer != null)
            {
                _discoveredPeers.TryRemove(peer.Endpoint, out _);
            }

            var settings = SettingsService.Load();
            if (!string.IsNullOrEmpty(settings.LastCompanionIpEndpoint))
            {
                settings.LastCompanionIpEndpoint = "";
                SettingsService.Save(settings);
            }
        }

        public async Task PreScanStorageAsync(bool forceRefresh = false)
        {
            var peer = CurrentPeer;
            if (peer == null) return;

            try
            {
                if (forceRefresh) ClearDirectoryCache();
                if (string.Equals(peer.Platform, "Windows", StringComparison.OrdinalIgnoreCase))
                {
                    await GetRemoteWindowsDrivesAsync(forceRefresh);
                }
                else
                {
                    await GetRemoteSubdirectoriesAsync("/storage/emulated/0", forceRefresh);
                }
            }
            catch { }
        }

        public static bool IsWindowsDrivePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string trimmed = path.Trim();
            if (trimmed.Length >= 3 && trimmed[0] == '/' && char.IsLetter(trimmed[1]) && trimmed[2] == ':')
            {
                trimmed = trimmed.Substring(1);
            }
            return trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':' &&
                   (trimmed.Length == 2 || trimmed[2] == '\\' || trimmed[2] == '/');
        }

        public static string NormalizeRemoteTargetPath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return "";
            string trimmed = rawPath.Trim();
            if (trimmed == "/" || trimmed == "\\") return "/";

            if (IsWindowsDrivePath(trimmed))
            {
                if (trimmed[0] == '/') trimmed = trimmed.Substring(1);
                string win = trimmed.Replace('/', '\\').TrimEnd('\\');
                if (win.Length == 2 && char.IsLetter(win[0]) && win[1] == ':')
                {
                    return char.ToUpperInvariant(win[0]) + ":\\";
                }
                return char.ToUpperInvariant(win[0]) + win.Substring(1);
            }

            string posix = trimmed.Replace('\\', '/').TrimEnd('/');
            return string.IsNullOrEmpty(posix) ? "/" : posix;
        }

        public static string CombineRemotePath(string baseRemotePath, string relativePath)
        {
            string rel = relativePath.TrimStart('\\', '/');
            if (IsWindowsDrivePath(baseRemotePath))
            {
                string cleanBase = NormalizeRemoteTargetPath(baseRemotePath).TrimEnd('\\');
                return string.IsNullOrEmpty(rel)
                    ? cleanBase + "\\"
                    : $"{cleanBase}\\{rel.Replace('/', '\\')}";
            }
            else
            {
                string cleanBase = baseRemotePath.Trim().Replace('\\', '/').TrimEnd('/');
                return string.IsNullOrEmpty(cleanBase)
                    ? $"/{rel.Replace('\\', '/')}"
                    : $"{cleanBase}/{rel.Replace('\\', '/')}";
            }
        }

        public async Task<List<string>> GetRemoteWindowsDrivesAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            var peer = CurrentPeer;
            if (peer == null) return new List<string>();

            string cacheKey = $"{peer.Endpoint}:__WIN_DRIVES__";
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

            var drives = new List<string>();
            try
            {
                var listing = await GetRemoteDirectoryListingAsync("/", includeFiles: false, ct);
                foreach (var entry in listing.Entries)
                {
                    string candidate = !string.IsNullOrWhiteSpace(entry.FullPath) ? entry.FullPath : entry.Name;
                    if (entry.IsDirectory && IsWindowsDrivePath(candidate))
                    {
                        string normalizedDrive = NormalizeRemoteTargetPath(candidate);
                        if (!drives.Contains(normalizedDrive, StringComparer.OrdinalIgnoreCase))
                        {
                            drives.Add(normalizedDrive);
                        }
                    }
                }
            }
            catch { }

            lock (_cacheLock)
            {
                _directoryCache[cacheKey] = drives;
            }
            return drives;
        }

        public async Task<DirectoryListingResponse> GetRemoteDirectoryListingAsync(string remotePath, bool includeFiles, CancellationToken ct = default)
        {
            var peer = CurrentPeer ?? throw new InvalidOperationException("No wireless companion device connected.");
            string normalized = string.IsNullOrWhiteSpace(remotePath) ? "/" : NormalizeRemoteTargetPath(remotePath);

            string url = $"http://{peer.Endpoint}/api/v1/storage/list?path={Uri.EscapeDataString(normalized)}&includeFiles={includeFiles.ToString().ToLowerInvariant()}";
            using var resp = await _httpClient.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            string json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<DirectoryListingResponse>(json, JsonOptions) ?? new DirectoryListingResponse();
        }

        public async Task<List<string>> GetRemoteSubdirectoriesAsync(string remotePath, bool forceRefresh = false)
        {
            var peer = CurrentPeer;
            if (peer == null) return new List<string>();

            string normalized = string.IsNullOrWhiteSpace(remotePath) ? "/storage" : NormalizeRemoteTargetPath(remotePath);

            string cacheKey = $"{peer.Endpoint}:{normalized}";
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

            var dirs = new HashSet<string>(IsWindowsDrivePath(normalized) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (normalized == "/storage")
            {
                dirs.Add("emulated");
            }
            else if (normalized == "/storage/emulated")
            {
                dirs.Add("0");
            }

            try
            {
                var listing = await GetRemoteDirectoryListingAsync(normalized, includeFiles: false);
                foreach (var entry in listing.Entries)
                {
                    if (entry.IsDirectory && !string.IsNullOrWhiteSpace(entry.Name) && entry.Name != "." && entry.Name != "..")
                    {
                        dirs.Add(entry.Name.Trim('/', '\\'));
                    }
                }
            }
            catch { }

            var sorted = dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            lock (_cacheLock)
            {
                _directoryCache[cacheKey] = sorted;
            }
            return sorted;
        }

        public async Task<string> CreateRemoteDirectoryAsync(string parentPath, string newFolderName, CancellationToken ct = default)
        {
            var peer = CurrentPeer ?? throw new InvalidOperationException("No wireless companion device connected.");
            string cleanParent = string.IsNullOrWhiteSpace(parentPath) ? "/storage/emulated/0" : NormalizeRemoteTargetPath(parentPath);
            string cleanName = newFolderName.Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(cleanName))
            {
                throw new InvalidOperationException("Folder name cannot be empty.");
            }

            string fullPath = CombineRemotePath(cleanParent, cleanName);
            await CreateRemoteDirectoryFullPathAsync(peer, fullPath, ct);

            lock (_cacheLock)
            {
                _directoryCache.Remove($"{peer.Endpoint}:{cleanParent}");
            }

            return fullPath;
        }

        private async Task CreateRemoteDirectoryFullPathAsync(CompanionPeer peer, string fullRemotePath, CancellationToken ct)
        {
            string url = $"http://{peer.Endpoint}/api/v1/storage/mkdir?path={Uri.EscapeDataString(fullRemotePath)}";
            using var content = new ByteArrayContent(Array.Empty<byte>());
            using var resp = await _httpClient.PostAsync(url, content, ct);
            resp.EnsureSuccessStatusCode();
        }

        public async Task<TreeManifestResponse> GetRemoteTreeAsync(CompanionPeer peer, string remoteRootPath, CancellationToken ct)
        {
            string url = $"http://{peer.Endpoint}/api/v1/storage/tree?path={Uri.EscapeDataString(remoteRootPath)}";
            using var resp = await _httpClient.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<TreeManifestResponse>(json, JsonOptions) ?? new TreeManifestResponse();
        }

        public async Task<string?> GetRemoteCompanionFileSha256Async(CompanionPeer peer, string remoteFullPath, CancellationToken ct)
        {
            try
            {
                string hashUrl = $"http://{peer.Endpoint}/api/v1/file/hash?path={Uri.EscapeDataString(remoteFullPath)}";
                using var hashResp = await _httpClient.GetAsync(hashUrl, ct);
                if (hashResp.IsSuccessStatusCode)
                {
                    string json = await hashResp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("sha256", out var shaProp) && shaProp.ValueKind == JsonValueKind.String)
                    {
                        var val = shaProp.GetString();
                        if (!string.IsNullOrWhiteSpace(val)) return val.ToLowerInvariant();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch { }

            string pullUrl = $"http://{peer.Endpoint}/api/v1/file/pull?path={Uri.EscapeDataString(remoteFullPath)}";
            using var resp = await _httpClient.GetAsync(pullUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            if (resp.Headers.TryGetValues("X-File-Sha256", out var headerVals))
            {
                var h = headerVals.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(h))
                {
                    return h.Trim().ToLowerInvariant();
                }
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            byte[] hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        public async Task<(long BytesTransferred, double ElapsedSeconds)> PushFileToPeerAsync(
            CompanionPeer peer,
            FileInfo localFile,
            string remoteFullPath,
            ConflictPolicy conflict,
            CancellationToken ct,
            Action<string>? logCallback = null,
            Action<long, string>? onProgressCallback = null)
        {
            bool verifyHash = SettingsService.Load().VerifyHashAfterTransfer;
            string? localSha256 = verifyHash ? await FileHashHelper.ComputeFileSha256Async(localFile.FullName, ct) : null;

            long mtimeTicks = localFile.LastWriteTimeUtc.Ticks;
            string url = $"http://{peer.Endpoint}/api/v1/file/push?path={Uri.EscapeDataString(remoteFullPath)}&mtimeTicks={mtimeTicks}&conflict={(int)conflict}" +
                         (!string.IsNullOrEmpty(localSha256) ? $"&sha256={localSha256}" : "");

            long bytesSent = 0;
            double elapsedSec = 0;
            using (var rawFs = new FileStream(localFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true))
            using (var trackedStream = new ProgressTrackingStream(rawFs, onProgressCallback))
            using (var streamContent = new StreamContent(trackedStream, 65536))
            {
                streamContent.Headers.ContentLength = localFile.Length;
                streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                if (!string.IsNullOrEmpty(localSha256))
                {
                    streamContent.Headers.TryAddWithoutValidation("X-File-Sha256", localSha256);
                }

                using var resp = await _httpClient.PostAsync(url, streamContent, ct);
                resp.EnsureSuccessStatusCode();
                bytesSent = trackedStream.TotalBytesTransferred;
                elapsedSec = trackedStream.ElapsedSeconds;
            }

            if (verifyHash && !string.IsNullOrEmpty(localSha256) && conflict != ConflictPolicy.KeepBothRename)
            {
                string? remoteSha256 = await GetRemoteCompanionFileSha256Async(peer, remoteFullPath, ct);
                if (!string.Equals(localSha256, remoteSha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { await DeleteRemoteFileAsync(peer, remoteFullPath, ct); } catch { }
                    throw new IOException($"Hash verification failed (SHA-256) for '{remoteFullPath}' (Local: {localSha256}, Remote: {remoteSha256 ?? "unavailable"}). Corrupted remote file has been removed.");
                }
                logCallback?.Invoke($"Hash verified (SHA-256): {Path.GetFileName(remoteFullPath)}");
            }

            return (bytesSent, elapsedSec);
        }

        public async Task<(long BytesTransferred, double ElapsedSeconds)> PullFileFromPeerAsync(
            CompanionPeer peer,
            string remoteFullPath,
            string localFullPath,
            long fallbackMtimeTicks,
            CancellationToken ct,
            Action<string>? logCallback = null,
            Action<long, string>? onProgressCallback = null)
        {
            bool verifyHash = SettingsService.Load().VerifyHashAfterTransfer;
            string url = $"http://{peer.Endpoint}/api/v1/file/pull?path={Uri.EscapeDataString(remoteFullPath)}";
            using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long? expectedLen = resp.Content.Headers.ContentLength;
            long mtimeTicks = fallbackMtimeTicks;
            if (resp.Headers.TryGetValues("X-LastWriteTimeUtcTicks", out var tickVals))
            {
                var first = tickVals.FirstOrDefault();
                if (long.TryParse(first, out long parsedTicks) && parsedTicks > 0)
                {
                    mtimeTicks = parsedTicks;
                }
            }

            string? headerSha256 = null;
            if (resp.Headers.TryGetValues("X-File-Sha256", out var shaVals))
            {
                headerSha256 = shaVals.FirstOrDefault()?.Trim();
            }

            string parentDir = Path.GetDirectoryName(localFullPath) ?? @"C:\";
            Directory.CreateDirectory(parentDir);

            string tempPartPath = Path.Combine(parentDir, $".syncdroid_{Guid.NewGuid():N}.part");
            long bytesPulled = 0;
            double elapsedSec = 0;
            try
            {
                using (var netStream = await resp.Content.ReadAsStreamAsync(ct))
                using (var rawFs = new FileStream(tempPartPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                using (var trackedFs = new ProgressTrackingStream(rawFs, onProgressCallback))
                {
                    await netStream.CopyToAsync(trackedFs, 65536, ct);
                    await trackedFs.FlushAsync(ct);

                    bytesPulled = trackedFs.TotalBytesTransferred;
                    elapsedSec = trackedFs.ElapsedSeconds;

                    if (expectedLen.HasValue && expectedLen.Value >= 0 && bytesPulled != expectedLen.Value)
                    {
                        throw new IOException($"Incomplete download for '{remoteFullPath}': expected {expectedLen.Value} bytes, got {bytesPulled} bytes.");
                    }
                }

                if (verifyHash)
                {
                    string localSha256 = await FileHashHelper.ComputeFileSha256Async(tempPartPath, ct);
                    string? remoteSha256 = !string.IsNullOrWhiteSpace(headerSha256)
                        ? headerSha256
                        : await GetRemoteCompanionFileSha256Async(peer, remoteFullPath, ct);

                    if (!string.Equals(localSha256, remoteSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(tempPartPath); } catch { }
                        throw new IOException($"Hash verification failed (SHA-256) for '{remoteFullPath}' (Local: {localSha256}, Remote: {remoteSha256 ?? "unavailable"}).");
                    }
                    logCallback?.Invoke($"Hash verified (SHA-256): {Path.GetFileName(localFullPath)}");
                }

                File.Move(tempPartPath, localFullPath, overwrite: true);

                if (mtimeTicks > 0)
                {
                    try
                    {
                        File.SetLastWriteTimeUtc(localFullPath, new DateTime(mtimeTicks, DateTimeKind.Utc));
                    }
                    catch { }
                }

                return (bytesPulled, elapsedSec);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPartPath)) File.Delete(tempPartPath);
                }
                catch { }
                throw;
            }
        }

        public async Task DeleteRemoteFileAsync(CompanionPeer peer, string remoteFullPath, CancellationToken ct)
        {
            string url = $"http://{peer.Endpoint}/api/v1/file/delete?path={Uri.EscapeDataString(remoteFullPath)}";
            using var content = new ByteArrayContent(Array.Empty<byte>());
            using var resp = await _httpClient.PostAsync(url, content, ct);
            resp.EnsureSuccessStatusCode();
        }

        public async Task SyncPcToAndroidAsync(
            string pcPath,
            string androidPath,
            bool isMirrorMode,
            bool skipOverlongFiles,
            Action<int> progressCallback,
            Action<string> logCallback,
            CancellationToken token)
        {
            var peer = CurrentPeer ?? throw new InvalidOperationException("No wireless companion device connected.");

            pcPath = Path.GetFullPath(pcPath.Trim());
            androidPath = NormalizeRemoteTargetPath(androidPath);

            logCallback($"[Wireless Companion: {peer.DeviceName}] Scanning PC directory: {pcPath}");
            var localFiles = Directory.GetFiles(pcPath, "*.*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var name = Path.GetFileName(f);
                    return !(name.StartsWith(".syncdroid_", StringComparison.OrdinalIgnoreCase) &&
                             name.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
                })
                .ToArray();

            await CreateRemoteDirectoryFullPathAsync(peer, androidPath, token);

            logCallback($"[Wireless Companion: {peer.DeviceName}] Scanning Target directory: {androidPath}");
            var remoteTree = await GetRemoteTreeAsync(peer, androidPath, token);

            var remoteFilesByRelPath = new Dictionary<string, RemoteEntryDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in remoteTree.Files)
            {
                string rel = !string.IsNullOrWhiteSpace(entry.RelativePath)
                    ? entry.RelativePath.Replace('\\', '/').TrimStart('/')
                    : (entry.FullPath.StartsWith(androidPath, StringComparison.OrdinalIgnoreCase)
                        ? entry.FullPath.Substring(androidPath.Length).Replace('\\', '/').TrimStart('/')
                        : entry.Name);
                if (!string.IsNullOrEmpty(rel))
                {
                    remoteFilesByRelPath[rel] = entry;
                }
            }

            int totalFiles = localFiles.Length;
            long totalBytes = localFiles.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            long completedBytes = 0;
            int processed = 0;
            int phase1Weight = isMirrorMode ? 50 : 100;

            foreach (var localFile in localFiles)
            {
                token.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(pcPath, localFile).Replace('\\', '/');
                var fileInfo = new FileInfo(localFile);

                if (FileNameLengthHelper.IsFileNameOver255Bytes(localFile))
                {
                    processed++;
                    completedBytes += fileInfo.Length;
                    double skipPct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, totalFiles);
                    logCallback($"Skipped (Filename > 255 bytes): {relativePath} | Overall: {skipPct:F1}% ({processed}/{totalFiles})");
                    progressCallback((int)Math.Round((skipPct * phase1Weight) / 100.0));
                    continue;
                }

                var targetRemotePath = CombineRemotePath(androidPath, relativePath);
                bool shouldPush = true;

                if (remoteFilesByRelPath.TryGetValue(relativePath, out var remoteStat))
                {
                    if (remoteStat.Size == fileInfo.Length)
                    {
                        shouldPush = false;
                        processed++;
                        completedBytes += fileInfo.Length;
                        double skipPct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, totalFiles);
                        logCallback($"Skipped (Identical): {relativePath} | Overall: {skipPct:F1}% ({processed}/{totalFiles})");
                        progressCallback((int)Math.Round((skipPct * phase1Weight) / 100.0));
                    }
                }

                if (shouldPush)
                {
                    long baseCompleted = completedBytes;
                    int nextIdx = processed + 1;
                    logCallback($"Pushing: {relativePath} ({FileHashHelper.FormatByteSize(fileInfo.Length)})");
                    var (bytesSent, elapsedSec) = await PushFileToPeerAsync(
                        peer,
                        fileInfo,
                        targetRemotePath,
                        ConflictPolicy.Overwrite,
                        token,
                        logCallback,
                        (inFlightBytes, liveSpeed) =>
                        {
                            double livePct = FileHashHelper.CalculateOverallPercent(baseCompleted + inFlightBytes, totalBytes, processed, totalFiles);
                            progressCallback((int)Math.Round((livePct * phase1Weight) / 100.0));
                            logCallback($"Transferring: {relativePath} | Speed: {liveSpeed} | Overall: {livePct:F1}% ({nextIdx}/{totalFiles})");
                        });

                    processed++;
                    completedBytes += fileInfo.Length;
                    double donePct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, totalFiles);
                    string avgSpeed = FileHashHelper.FormatTransferSpeed(bytesSent > 0 ? bytesSent : fileInfo.Length, elapsedSec);
                    logCallback($"Transferred: {relativePath} ({FileHashHelper.FormatByteSize(fileInfo.Length)}) | Speed: {avgSpeed} | Overall: {donePct:F1}% ({processed}/{totalFiles})");
                    progressCallback((int)Math.Round((donePct * phase1Weight) / 100.0));
                }
            }

            if (isMirrorMode)
            {
                var localRelSet = new HashSet<string>(
                    localFiles.Select(f => Path.GetRelativePath(pcPath, f).Replace('\\', '/')),
                    StringComparer.OrdinalIgnoreCase);

                var filesToDelete = remoteFilesByRelPath
                    .Where(kvp => !localRelSet.Contains(kvp.Key))
                    .Select(kvp => !string.IsNullOrWhiteSpace(kvp.Value.FullPath) ? kvp.Value.FullPath : CombineRemotePath(androidPath, kvp.Key))
                    .ToList();

                int deleteTotal = filesToDelete.Count;
                int deleteProcessed = 0;

                foreach (var remoteFile in filesToDelete)
                {
                    token.ThrowIfCancellationRequested();
                    logCallback($"Deleting remote extra file: {remoteFile}");
                    await DeleteRemoteFileAsync(peer, remoteFile, token);
                    deleteProcessed++;
                    progressCallback(50 + (deleteProcessed * 50) / (deleteTotal > 0 ? deleteTotal : 1));
                }
            }
            else
            {
                logCallback("Incremental Merge mode: Keeping existing extra files on Target.");
            }

            logCallback("Push Sync completed successfully.");
            progressCallback(100);
        }

        public async Task SyncAndroidToPcAsync(
            string androidPath,
            string pcPath,
            bool isMirrorMode,
            Action<int> progressCallback,
            Action<string> logCallback,
            CancellationToken token)
        {
            var peer = CurrentPeer ?? throw new InvalidOperationException("No wireless companion device connected.");

            pcPath = Path.GetFullPath(pcPath.Trim());
            androidPath = NormalizeRemoteTargetPath(androidPath);
            Directory.CreateDirectory(pcPath);

            logCallback($"[Wireless Companion: {peer.DeviceName}] Scanning Target directory: {androidPath}");
            var remoteTree = await GetRemoteTreeAsync(peer, androidPath, token);

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

            int totalFiles = remoteTree.Files.Count;
            long totalBytes = remoteTree.Files.Sum(f => f.Size);
            long completedBytes = 0;
            int processed = 0;
            int phase1Weight = isMirrorMode ? 50 : 100;
            var expectedLocalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in remoteTree.Files)
            {
                token.ThrowIfCancellationRequested();
                string rawRelPath = !string.IsNullOrWhiteSpace(entry.RelativePath)
                    ? entry.RelativePath.Replace('\\', '/').TrimStart('/')
                    : (entry.FullPath.StartsWith(androidPath, StringComparison.OrdinalIgnoreCase)
                        ? entry.FullPath.Substring(androidPath.Length).Replace('\\', '/').TrimStart('/')
                        : entry.Name);

                string sanitizedRelPath = SanitizeRelativePathForWindows(rawRelPath);
                string targetLocalPath = Path.Combine(pcPath, sanitizedRelPath.Replace('/', '\\'));
                expectedLocalPaths.Add(targetLocalPath);

                bool shouldPull = true;
                if (localFilesInfo.TryGetValue(targetLocalPath, out var localStat))
                {
                    if (localStat.Length == entry.Size)
                    {
                        shouldPull = false;
                        processed++;
                        completedBytes += entry.Size;
                        double skipPct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, totalFiles);
                        logCallback($"Skipped (Identical): {sanitizedRelPath} | Overall: {skipPct:F1}% ({processed}/{totalFiles})");
                        progressCallback((int)Math.Round((skipPct * phase1Weight) / 100.0));
                    }
                }

                if (shouldPull)
                {
                    long baseCompleted = completedBytes;
                    int nextIdx = processed + 1;
                    logCallback($"Pulling: {sanitizedRelPath} ({FileHashHelper.FormatByteSize(entry.Size)})");
                    string remoteFullPath = !string.IsNullOrWhiteSpace(entry.FullPath)
                        ? entry.FullPath
                        : CombineRemotePath(androidPath, rawRelPath);
                    var (bytesPulled, elapsedSec) = await PullFileFromPeerAsync(
                        peer,
                        remoteFullPath,
                        targetLocalPath,
                        entry.LastWriteTimeUtcTicks,
                        token,
                        logCallback,
                        (inFlightBytes, liveSpeed) =>
                        {
                            double livePct = FileHashHelper.CalculateOverallPercent(baseCompleted + inFlightBytes, totalBytes, processed, totalFiles);
                            progressCallback((int)Math.Round((livePct * phase1Weight) / 100.0));
                            logCallback($"Transferring: {sanitizedRelPath} | Speed: {liveSpeed} | Overall: {livePct:F1}% ({nextIdx}/{totalFiles})");
                        });

                    processed++;
                    completedBytes += entry.Size;
                    double donePct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, totalFiles);
                    string avgSpeed = FileHashHelper.FormatTransferSpeed(bytesPulled > 0 ? bytesPulled : entry.Size, elapsedSec);
                    logCallback($"Transferred: {sanitizedRelPath} ({FileHashHelper.FormatByteSize(entry.Size)}) | Speed: {avgSpeed} | Overall: {donePct:F1}% ({processed}/{totalFiles})");
                    progressCallback((int)Math.Round((donePct * phase1Weight) / 100.0));
                }
            }

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

            logCallback("Pull Sync completed successfully.");
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
            var peer = CurrentPeer ?? throw new InvalidOperationException("No wireless companion device connected.");
            targetAndroidDir = NormalizeRemoteTargetPath(targetAndroidDir);

            await CreateRemoteDirectoryFullPathAsync(peer, targetAndroidDir, token);

            var filePlan = ExpandQueueItemsToFiles(queueItems);
            int total = filePlan.Count;
            if (total == 0)
            {
                logCallback("No files found in the selected queue items.");
                progressCallback(100);
                return;
            }

            long totalBytes = filePlan.Sum(x => x.File.Length);
            long completedBytes = 0;
            int processed = 0;
            foreach (var (fileInfo, relativeDestPath) in filePlan)
            {
                token.ThrowIfCancellationRequested();

                if (FileNameLengthHelper.IsFileNameOver255Bytes(fileInfo.FullName))
                {
                    processed++;
                    completedBytes += fileInfo.Length;
                    double skipPct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, total);
                    logCallback($"Skipped (Filename > 255 bytes): {relativeDestPath} | Overall: {skipPct:F1}% ({processed}/{total})");
                    progressCallback((int)Math.Round(skipPct));
                    continue;
                }

                long baseCompleted = completedBytes;
                int nextIdx = processed + 1;
                string remoteFullPath = CombineRemotePath(targetAndroidDir, relativeDestPath);
                logCallback($"Pushing ({conflictPolicy}): {relativeDestPath} ({FileHashHelper.FormatByteSize(fileInfo.Length)})");
                var (bytesSent, elapsedSec) = await PushFileToPeerAsync(
                    peer,
                    fileInfo,
                    remoteFullPath,
                    conflictPolicy,
                    token,
                    logCallback,
                    (inFlightBytes, liveSpeed) =>
                    {
                        double livePct = FileHashHelper.CalculateOverallPercent(baseCompleted + inFlightBytes, totalBytes, processed, total);
                        progressCallback((int)Math.Round(livePct));
                        logCallback($"Transferring: {relativeDestPath} | Speed: {liveSpeed} | Overall: {livePct:F1}% ({nextIdx}/{total})");
                    });

                processed++;
                completedBytes += fileInfo.Length;
                double donePct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, total);
                string avgSpeed = FileHashHelper.FormatTransferSpeed(bytesSent > 0 ? bytesSent : fileInfo.Length, elapsedSec);
                logCallback($"Transferred: {relativeDestPath} ({FileHashHelper.FormatByteSize(fileInfo.Length)}) | Speed: {avgSpeed} | Overall: {donePct:F1}% ({processed}/{total})");
                progressCallback((int)Math.Round(donePct));
            }

            logCallback($"Direct transfer to device completed ({processed} file(s) processed | Overall: 100%).");
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
            var peer = CurrentPeer ?? throw new InvalidOperationException("No wireless companion device connected.");
            remoteItemPath = NormalizeRemoteTargetPath(remoteItemPath);
            targetPcDir = Path.GetFullPath(targetPcDir.Trim());
            Directory.CreateDirectory(targetPcDir);

            string trimmedItem = remoteItemPath.TrimEnd('\\', '/');
            string itemName = Path.GetFileName(trimmedItem);
            if (string.IsNullOrWhiteSpace(itemName) && IsWindowsDrivePath(remoteItemPath))
            {
                itemName = $"Drive_{char.ToUpperInvariant(remoteItemPath.TrimStart('/')[0])}";
            }

            if (!isDirectory)
            {
                string sanitizedName = SanitizeRelativePathForWindows(itemName);
                string localTarget = Path.Combine(targetPcDir, sanitizedName);
                if (File.Exists(localTarget) && conflictPolicy == ConflictPolicy.KeepBothRename)
                {
                    localTarget = GetUniqueKeepBothLocalPath(localTarget);
                }

                logCallback($"Pulling file: {remoteItemPath}");
                var (bytesPulled, elapsedSec) = await PullFileFromPeerAsync(
                    peer,
                    remoteItemPath,
                    localTarget,
                    0,
                    token,
                    logCallback,
                    (inFlightBytes, liveSpeed) =>
                    {
                        logCallback($"Transferring: {sanitizedName} ({FileHashHelper.FormatByteSize(inFlightBytes)}) | Speed: {liveSpeed}");
                    });
                string avgSpeed = FileHashHelper.FormatTransferSpeed(bytesPulled, elapsedSec);
                logCallback($"Transferred: {sanitizedName} ({FileHashHelper.FormatByteSize(bytesPulled)}) | Speed: {avgSpeed} | Overall: 100.0% (1/1)");
                progressCallback(100);
                logCallback($"Pulled to: {localTarget}");
                return;
            }

            logCallback($"Scanning device folder: {remoteItemPath}");
            var tree = await GetRemoteTreeAsync(peer, remoteItemPath, token);
            int total = tree.Files.Count;
            long totalBytes = tree.Files.Sum(f => f.Size);
            long completedBytes = 0;
            int processed = 0;
            string rootFolderName = SanitizeRelativePathForWindows(itemName);

            foreach (var entry in tree.Files)
            {
                token.ThrowIfCancellationRequested();
                string rawRel = !string.IsNullOrWhiteSpace(entry.RelativePath)
                    ? entry.RelativePath.Replace('\\', '/').TrimStart('/')
                    : entry.Name;
                string sanitizedRel = SanitizeRelativePathForWindows(rawRel);
                string localTarget = Path.Combine(targetPcDir, rootFolderName, sanitizedRel.Replace('/', '\\'));

                if (File.Exists(localTarget))
                {
                    if (conflictPolicy == ConflictPolicy.SkipIdenticalSize && new FileInfo(localTarget).Length == entry.Size)
                    {
                        processed++;
                        completedBytes += entry.Size;
                        double skipPct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, total);
                        logCallback($"Skipped (Identical): {rootFolderName}/{sanitizedRel} | Overall: {skipPct:F1}% ({processed}/{total})");
                        progressCallback((int)Math.Round(skipPct));
                        continue;
                    }
                    if (conflictPolicy == ConflictPolicy.KeepBothRename)
                    {
                        localTarget = GetUniqueKeepBothLocalPath(localTarget);
                    }
                }

                long baseCompleted = completedBytes;
                int nextIdx = processed + 1;
                string remoteFull = !string.IsNullOrWhiteSpace(entry.FullPath)
                    ? entry.FullPath
                    : CombineRemotePath(remoteItemPath, rawRel);
                logCallback($"Pulling: {rootFolderName}/{sanitizedRel} ({FileHashHelper.FormatByteSize(entry.Size)})");
                var (bytesPulled, elapsedSec) = await PullFileFromPeerAsync(
                    peer,
                    remoteFull,
                    localTarget,
                    entry.LastWriteTimeUtcTicks,
                    token,
                    logCallback,
                    (inFlightBytes, liveSpeed) =>
                    {
                        double livePct = FileHashHelper.CalculateOverallPercent(baseCompleted + inFlightBytes, totalBytes, processed, total);
                        progressCallback((int)Math.Round(livePct));
                        logCallback($"Transferring: {rootFolderName}/{sanitizedRel} | Speed: {liveSpeed} | Overall: {livePct:F1}% ({nextIdx}/{total})");
                    });

                processed++;
                completedBytes += entry.Size;
                double donePct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, total);
                string avgSpeed = FileHashHelper.FormatTransferSpeed(bytesPulled > 0 ? bytesPulled : entry.Size, elapsedSec);
                logCallback($"Transferred: {rootFolderName}/{sanitizedRel} ({FileHashHelper.FormatByteSize(entry.Size)}) | Speed: {avgSpeed} | Overall: {donePct:F1}% ({processed}/{total})");
                progressCallback((int)Math.Round(donePct));
            }

            logCallback($"Direct pull from device completed ({processed} file(s) | Overall: 100%).");
            progressCallback(100);
        }

        public static List<(FileInfo File, string RelativeDestPath)> ExpandQueueItemsToFiles(IReadOnlyList<TransferQueueItem> queueItems)
        {
            var list = new List<(FileInfo, string)>();
            foreach (var item in queueItems)
            {
                if (string.IsNullOrWhiteSpace(item.LocalPath)) continue;
                string full = Path.GetFullPath(item.LocalPath);

                if (File.Exists(full))
                {
                    var fi = new FileInfo(full);
                    list.Add((fi, fi.Name));
                }
                else if (Directory.Exists(full))
                {
                    string folderName = Path.GetFileName(full.TrimEnd('\\', '/'));
                    if (string.IsNullOrWhiteSpace(folderName) && IsWindowsDrivePath(full))
                    {
                        folderName = $"Drive_{char.ToUpperInvariant(full[0])}";
                    }
                    foreach (var file in Directory.GetFiles(full, "*.*", SearchOption.AllDirectories))
                    {
                        var name = Path.GetFileName(file);
                        if (name.StartsWith(".syncdroid_", StringComparison.OrdinalIgnoreCase) &&
                            name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        var fi = new FileInfo(file);
                        string rel = $"{folderName}/{Path.GetRelativePath(full, file).Replace('\\', '/')}";
                        list.Add((fi, rel));
                    }
                }
            }
            return list;
        }

        public async Task SyncWindowsPathAsync(
            string sourcePath,
            string targetPath,
            bool isMirrorMode,
            Action<int> progressCallback,
            Action<string> logCallback,
            CancellationToken token)
        {
            bool verifyHash = SettingsService.Load().VerifyHashAfterTransfer;
            sourcePath = Path.GetFullPath(NormalizeRemoteTargetPath(sourcePath));
            targetPath = Path.GetFullPath(NormalizeRemoteTargetPath(targetPath));

            if (!Directory.Exists(sourcePath))
            {
                throw new DirectoryNotFoundException($"Source directory does not exist: {sourcePath}");
            }
            Directory.CreateDirectory(targetPath);

            logCallback($"Scanning Source directory: {sourcePath}");
            var sourceFiles = Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var name = Path.GetFileName(f);
                    return !(name.StartsWith(".syncdroid_", StringComparison.OrdinalIgnoreCase) &&
                             name.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
                })
                .ToArray();

            logCallback($"Scanning Target directory: {targetPath}");
            var targetFiles = Directory.Exists(targetPath)
                ? Directory.GetFiles(targetPath, "*.*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var name = Path.GetFileName(f);
                        return !(name.StartsWith(".syncdroid_", StringComparison.OrdinalIgnoreCase) &&
                                 name.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
                    })
                    .ToArray()
                : Array.Empty<string>();
            var targetFilesInfo = targetFiles.ToDictionary(f => f, f => new FileInfo(f), StringComparer.OrdinalIgnoreCase);

            int totalFiles = sourceFiles.Length;
            long totalBytes = sourceFiles.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            long completedBytes = 0;
            int processed = 0;
            int phase1Weight = isMirrorMode ? 50 : 100;
            var expectedTargetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var srcFile in sourceFiles)
            {
                token.ThrowIfCancellationRequested();
                var relPath = Path.GetRelativePath(sourcePath, srcFile);
                var destFile = Path.Combine(targetPath, relPath);
                expectedTargetPaths.Add(destFile);

                var srcInfo = new FileInfo(srcFile);
                bool shouldCopy = true;
                if (targetFilesInfo.TryGetValue(destFile, out var destInfo) && destInfo.Length == srcInfo.Length)
                {
                    shouldCopy = false;
                    processed++;
                    completedBytes += srcInfo.Length;
                    double skipPct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, totalFiles);
                    logCallback($"Skipped (Identical): {relPath.Replace('\\', '/')} | Overall: {skipPct:F1}% ({processed}/{totalFiles})");
                    progressCallback((int)Math.Round((skipPct * phase1Weight) / 100.0));
                }

                if (shouldCopy)
                {
                    long baseCompleted = completedBytes;
                    int nextIdx = processed + 1;
                    string displayRel = relPath.Replace('\\', '/');
                    logCallback($"Copying: {displayRel} ({FileHashHelper.FormatByteSize(srcInfo.Length)})");
                    var (bytesCopied, elapsedSec) = await CopyWindowsFileWithVerificationAsync(
                        srcFile,
                        destFile,
                        verifyHash,
                        logCallback,
                        token,
                        (inFlightBytes, liveSpeed) =>
                        {
                            double livePct = FileHashHelper.CalculateOverallPercent(baseCompleted + inFlightBytes, totalBytes, processed, totalFiles);
                            progressCallback((int)Math.Round((livePct * phase1Weight) / 100.0));
                            logCallback($"Transferring: {displayRel} | Speed: {liveSpeed} | Overall: {livePct:F1}% ({nextIdx}/{totalFiles})");
                        });

                    processed++;
                    completedBytes += srcInfo.Length;
                    double donePct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, totalFiles);
                    string avgSpeed = FileHashHelper.FormatTransferSpeed(bytesCopied > 0 ? bytesCopied : srcInfo.Length, elapsedSec);
                    logCallback($"Transferred: {displayRel} ({FileHashHelper.FormatByteSize(srcInfo.Length)}) | Speed: {avgSpeed} | Overall: {donePct:F1}% ({processed}/{totalFiles})");
                    progressCallback((int)Math.Round((donePct * phase1Weight) / 100.0));
                }
            }

            if (isMirrorMode)
            {
                var filesToDelete = targetFiles.Where(f => !expectedTargetPaths.Contains(f)).ToList();
                int deleteTotal = filesToDelete.Count;
                int deleteProcessed = 0;
                foreach (var extra in filesToDelete)
                {
                    token.ThrowIfCancellationRequested();
                    logCallback($"Deleting extra file: {extra}");
                    File.Delete(extra);
                    deleteProcessed++;
                    progressCallback(50 + (deleteProcessed * 50) / (deleteTotal > 0 ? deleteTotal : 1));
                }
            }
            else
            {
                logCallback("Incremental Merge mode: Keeping existing extra files on Target.");
            }

            logCallback("Sync completed successfully.");
            progressCallback(100);
        }

        public async Task ExecuteDirectTransferToWindowsPathAsync(
            IReadOnlyList<TransferQueueItem> queueItems,
            string targetDir,
            ConflictPolicy conflictPolicy,
            Action<int> progressCallback,
            Action<string> logCallback,
            CancellationToken token)
        {
            bool verifyHash = SettingsService.Load().VerifyHashAfterTransfer;
            targetDir = Path.GetFullPath(NormalizeRemoteTargetPath(targetDir));
            Directory.CreateDirectory(targetDir);

            var filePlan = ExpandQueueItemsToFiles(queueItems);
            int total = filePlan.Count;
            if (total == 0)
            {
                logCallback("No files found in the selected queue items.");
                progressCallback(100);
                return;
            }

            long totalBytes = filePlan.Sum(x => x.File.Length);
            long completedBytes = 0;
            int processed = 0;
            foreach (var (fileInfo, relativeDestPath) in filePlan)
            {
                token.ThrowIfCancellationRequested();
                string destFullPath = Path.Combine(targetDir, relativeDestPath.Replace('/', '\\'));

                if (File.Exists(destFullPath))
                {
                    if (conflictPolicy == ConflictPolicy.SkipIdenticalSize && new FileInfo(destFullPath).Length == fileInfo.Length)
                    {
                        processed++;
                        completedBytes += fileInfo.Length;
                        double skipPct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, total);
                        logCallback($"Skipped (Identical): {relativeDestPath} | Overall: {skipPct:F1}% ({processed}/{total})");
                        progressCallback((int)Math.Round(skipPct));
                        continue;
                    }
                    if (conflictPolicy == ConflictPolicy.KeepBothRename)
                    {
                        destFullPath = GetUniqueKeepBothLocalPath(destFullPath);
                    }
                }

                long baseCompleted = completedBytes;
                int nextIdx = processed + 1;
                logCallback($"Transferring: {relativeDestPath} ({FileHashHelper.FormatByteSize(fileInfo.Length)})");
                var (bytesCopied, elapsedSec) = await CopyWindowsFileWithVerificationAsync(
                    fileInfo.FullName,
                    destFullPath,
                    verifyHash,
                    logCallback,
                    token,
                    (inFlightBytes, liveSpeed) =>
                    {
                        double livePct = FileHashHelper.CalculateOverallPercent(baseCompleted + inFlightBytes, totalBytes, processed, total);
                        progressCallback((int)Math.Round(livePct));
                        logCallback($"Transferring: {relativeDestPath} | Speed: {liveSpeed} | Overall: {livePct:F1}% ({nextIdx}/{total})");
                    });

                processed++;
                completedBytes += fileInfo.Length;
                double donePct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, total);
                string avgSpeed = FileHashHelper.FormatTransferSpeed(bytesCopied > 0 ? bytesCopied : fileInfo.Length, elapsedSec);
                logCallback($"Transferred: {relativeDestPath} ({FileHashHelper.FormatByteSize(fileInfo.Length)}) | Speed: {avgSpeed} | Overall: {donePct:F1}% ({processed}/{total})");
                progressCallback((int)Math.Round(donePct));
            }

            logCallback($"Direct transfer completed ({processed} file(s) processed | Overall: 100%).");
            progressCallback(100);
        }

        public async Task ExecuteDirectPullFromWindowsPathAsync(
            string sourceItemPath,
            bool isDirectory,
            string targetPcDir,
            ConflictPolicy conflictPolicy,
            Action<int> progressCallback,
            Action<string> logCallback,
            CancellationToken token)
        {
            bool verifyHash = SettingsService.Load().VerifyHashAfterTransfer;
            sourceItemPath = Path.GetFullPath(NormalizeRemoteTargetPath(sourceItemPath));
            targetPcDir = Path.GetFullPath(targetPcDir.Trim());
            Directory.CreateDirectory(targetPcDir);

            string itemName = Path.GetFileName(sourceItemPath.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(itemName) && IsWindowsDrivePath(sourceItemPath))
            {
                itemName = $"Drive_{char.ToUpperInvariant(sourceItemPath[0])}";
            }

            if (!isDirectory)
            {
                string destFile = Path.Combine(targetPcDir, itemName);
                if (File.Exists(destFile) && conflictPolicy == ConflictPolicy.KeepBothRename)
                {
                    destFile = GetUniqueKeepBothLocalPath(destFile);
                }

                long singleSize = new FileInfo(sourceItemPath).Length;
                logCallback($"Pulling file: {sourceItemPath} ({FileHashHelper.FormatByteSize(singleSize)})");
                var (bytesCopied, elapsedSec) = await CopyWindowsFileWithVerificationAsync(
                    sourceItemPath,
                    destFile,
                    verifyHash,
                    logCallback,
                    token,
                    (inFlightBytes, liveSpeed) =>
                    {
                        double livePct = FileHashHelper.CalculateOverallPercent(inFlightBytes, singleSize, 0, 1);
                        progressCallback((int)Math.Round(livePct));
                        logCallback($"Transferring: {itemName} | Speed: {liveSpeed} | Overall: {livePct:F1}% (1/1)");
                    });
                string avgSpeed = FileHashHelper.FormatTransferSpeed(bytesCopied > 0 ? bytesCopied : singleSize, elapsedSec);
                logCallback($"Transferred: {itemName} ({FileHashHelper.FormatByteSize(singleSize)}) | Speed: {avgSpeed} | Overall: 100.0% (1/1)");
                progressCallback(100);
                logCallback($"Pulled to: {destFile}");
                return;
            }

            var files = Directory.GetFiles(sourceItemPath, "*.*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var name = Path.GetFileName(f);
                    return !(name.StartsWith(".syncdroid_", StringComparison.OrdinalIgnoreCase) &&
                             name.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
                })
                .ToArray();

            int total = files.Length;
            long totalBytes = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            long completedBytes = 0;
            int processed = 0;
            foreach (var srcFile in files)
            {
                token.ThrowIfCancellationRequested();
                var srcInfo = new FileInfo(srcFile);
                string rel = Path.GetRelativePath(sourceItemPath, srcFile);
                string displayRel = $"{itemName}/{rel.Replace('\\', '/')}";
                string destFile = Path.Combine(targetPcDir, itemName, rel);

                if (File.Exists(destFile))
                {
                    if (conflictPolicy == ConflictPolicy.SkipIdenticalSize && new FileInfo(destFile).Length == srcInfo.Length)
                    {
                        processed++;
                        completedBytes += srcInfo.Length;
                        double skipPct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, total);
                        logCallback($"Skipped (Identical): {displayRel} | Overall: {skipPct:F1}% ({processed}/{total})");
                        progressCallback((int)Math.Round(skipPct));
                        continue;
                    }
                    if (conflictPolicy == ConflictPolicy.KeepBothRename)
                    {
                        destFile = GetUniqueKeepBothLocalPath(destFile);
                    }
                }

                long baseCompleted = completedBytes;
                int nextIdx = processed + 1;
                logCallback($"Pulling: {displayRel} ({FileHashHelper.FormatByteSize(srcInfo.Length)})");
                var (bytesCopied, elapsedSec) = await CopyWindowsFileWithVerificationAsync(
                    srcFile,
                    destFile,
                    verifyHash,
                    logCallback,
                    token,
                    (inFlightBytes, liveSpeed) =>
                    {
                        double livePct = FileHashHelper.CalculateOverallPercent(baseCompleted + inFlightBytes, totalBytes, processed, total);
                        progressCallback((int)Math.Round(livePct));
                        logCallback($"Transferring: {displayRel} | Speed: {liveSpeed} | Overall: {livePct:F1}% ({nextIdx}/{total})");
                    });

                processed++;
                completedBytes += srcInfo.Length;
                double donePct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, total);
                string avgSpeed = FileHashHelper.FormatTransferSpeed(bytesCopied > 0 ? bytesCopied : srcInfo.Length, elapsedSec);
                logCallback($"Transferred: {displayRel} ({FileHashHelper.FormatByteSize(srcInfo.Length)}) | Speed: {avgSpeed} | Overall: {donePct:F1}% ({processed}/{total})");
                progressCallback((int)Math.Round(donePct));
            }

            logCallback($"Direct pull from device completed ({processed} file(s) | Overall: 100%).");
            progressCallback(100);
        }

        private static async Task<(long BytesTransferred, double ElapsedSeconds)> CopyWindowsFileWithVerificationAsync(
            string sourceFile,
            string destFile,
            bool verifyHash,
            Action<string> logCallback,
            CancellationToken token,
            Action<long, string>? onProgressCallback = null)
        {
            string parentDir = Path.GetDirectoryName(destFile) ?? @"C:\";
            Directory.CreateDirectory(parentDir);
            string tempPart = Path.Combine(parentDir, $".syncdroid_{Guid.NewGuid():N}.part");
            long bytesCopied = 0;
            double elapsedSec = 0;

            try
            {
                await using (var srcStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true))
                await using (var rawDst = new FileStream(tempPart, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                await using (var trackedDst = new ProgressTrackingStream(rawDst, onProgressCallback))
                {
                    await srcStream.CopyToAsync(trackedDst, 65536, token);
                    bytesCopied = trackedDst.TotalBytesTransferred;
                    elapsedSec = trackedDst.ElapsedSeconds;
                }

                string? dstHash = null;
                if (verifyHash)
                {
                    string srcHash = await FileHashHelper.ComputeFileSha256Async(sourceFile, token);
                    dstHash = await FileHashHelper.ComputeFileSha256Async(tempPart, token);
                    if (!string.Equals(srcHash, dstHash, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(tempPart); } catch { }
                        throw new IOException($"Hash verification failed (SHA-256) for '{Path.GetFileName(destFile)}'.");
                    }
                }

                File.Move(tempPart, destFile, overwrite: true);
                try
                {
                    File.SetLastWriteTimeUtc(destFile, File.GetLastWriteTimeUtc(sourceFile));
                }
                catch { }

                if (verifyHash && dstHash != null)
                {
                    logCallback($"Hash verified (SHA-256): {Path.GetFileName(destFile)}");
                }

                return (bytesCopied, elapsedSec);
            }
            catch
            {
                try { if (File.Exists(tempPart)) File.Delete(tempPart); } catch { }
                throw;
            }
        }

        #endregion

        #region Path & HTTP Stream Helpers

        public static string SanitizeRelativePathForWindows(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return relativePath;
            char[] invalid = new[] { ':', '*', '?', '"', '<', '>', '|' };
            var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                var sb = new StringBuilder(segments[i].Length);
                foreach (char c in segments[i])
                {
                    sb.Append(invalid.Contains(c) ? '_' : c);
                }
                string cleaned = sb.ToString().TrimEnd(' ', '.');
                segments[i] = string.IsNullOrEmpty(cleaned) ? "_" : cleaned;
            }
            return string.Join("/", segments);
        }

        private static string NormalizeWindowsLocalPath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return "";
            string p = rawPath.Trim();
            if (p.Length >= 3 && p[0] == '/' && char.IsLetter(p[1]) && p[2] == ':')
            {
                p = p.Substring(1);
            }
            p = p.Replace('/', '\\');
            if (p.Length == 2 && char.IsLetter(p[0]) && p[1] == ':')
            {
                p += "\\";
            }
            return p;
        }

        private static string SanitizeWindowsFullPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return "";
            string root = Path.GetPathRoot(fullPath) ?? "";
            string rest = fullPath.Substring(root.Length);
            string sanitizedRest = SanitizeRelativePathForWindows(rest).Replace('/', '\\');
            return Path.Combine(root, sanitizedRest);
        }

        private static bool IsProtectedRootPath(string path)
        {
            string trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length <= 2 && trimmed.EndsWith(":", StringComparison.Ordinal)) return true;
            if (string.Equals(trimmed, "/storage", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "/storage/emulated", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "/storage/emulated/0", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        public static string GetUniqueKeepBothLocalPath(string originalPath)
        {
            if (!File.Exists(originalPath)) return originalPath;
            string dir = Path.GetDirectoryName(originalPath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(originalPath);
            string ext = Path.GetExtension(originalPath);
            int idx = 1;
            while (true)
            {
                string candidate = Path.Combine(dir, $"{stem} ({idx}){ext}");
                if (!File.Exists(candidate)) return candidate;
                idx++;
            }
        }

        private static Dictionary<string, string> ParseQueryString(string queryString)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(queryString)) return dict;

            foreach (var part in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0)
                {
                    string key = Uri.UnescapeDataString(part.Substring(0, eq));
                    string val = Uri.UnescapeDataString(part.Substring(eq + 1));
                    dict[key] = val;
                }
                else
                {
                    dict[Uri.UnescapeDataString(part)] = "";
                }
            }
            return dict;
        }

        private static async Task<(string HeaderText, byte[] LeftoverBytes)?> ReadHttpHeadersAsync(NetworkStream stream, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            byte[] buffer = new byte[4096];
            const int maxHeaderSize = 65536;

            while (ms.Length < maxHeaderSize)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read <= 0) return null;

                ms.Write(buffer, 0, read);
                byte[] all = ms.ToArray();
                int headerEnd = FindHeaderDelimiter(all);
                if (headerEnd >= 0)
                {
                    string headerText = Encoding.UTF8.GetString(all, 0, headerEnd);
                    int bodyStart = headerEnd + 4;
                    int leftoverLen = all.Length - bodyStart;
                    byte[] leftover = new byte[leftoverLen];
                    if (leftoverLen > 0)
                    {
                        Buffer.BlockCopy(all, bodyStart, leftover, 0, leftoverLen);
                    }
                    return (headerText, leftover);
                }
            }

            return null;
        }

        private static int FindHeaderDelimiter(byte[] data)
        {
            for (int i = 0; i <= data.Length - 4; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private static async Task WriteJsonResponseAsync<T>(NetworkStream stream, int statusCode, string statusText, T payload, CancellationToken ct)
        {
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
            string header =
                $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct);
            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, ct);
            await stream.FlushAsync(ct);
        }

        private static async Task<long> CopyExactBytesAsync(Stream source, Stream destination, long count, CancellationToken ct)
        {
            byte[] buffer = new byte[65536];
            long remaining = count;
            long totalWritten = 0;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = await source.ReadAsync(buffer, 0, toRead, ct);
                if (read <= 0) break;

                await destination.WriteAsync(buffer, 0, read, ct);
                remaining -= read;
                totalWritten += read;
            }

            return totalWritten;
        }

        private static async Task<long> CopyChunkedBodyToStreamAsync(Stream source, Stream destination, CancellationToken ct)
        {
            long total = 0;
            byte[] buffer = new byte[65536];

            while (true)
            {
                string? sizeLine = await ReadAsciiLineAsync(source, ct);
                if (string.IsNullOrWhiteSpace(sizeLine)) break;

                int semi = sizeLine.IndexOf(';');
                if (semi > 0) sizeLine = sizeLine.Substring(0, semi);

                if (!long.TryParse(sizeLine.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long chunkSize) || chunkSize <= 0)
                {
                    // Consume trailing CRLF
                    await ReadAsciiLineAsync(source, ct);
                    break;
                }

                long remaining = chunkSize;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = await source.ReadAsync(buffer, 0, toRead, ct);
                    if (read <= 0) throw new EndOfStreamException("Unexpected EOF in chunked body.");
                    await destination.WriteAsync(buffer, 0, read, ct);
                    remaining -= read;
                    total += read;
                }

                // Consume CRLF after chunk data
                await ReadAsciiLineAsync(source, ct);
            }

            return total;
        }

        private static async Task<string?> ReadAsciiLineAsync(Stream stream, CancellationToken ct)
        {
            var sb = new StringBuilder();
            byte[] b = new byte[1];
            while (true)
            {
                int r = await stream.ReadAsync(b, 0, 1, ct);
                if (r <= 0) return sb.Length > 0 ? sb.ToString() : null;
                char c = (char)b[0];
                if (c == '\n')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
                        sb.Length--;
                    return sb.ToString();
                }
                sb.Append(c);
            }
        }

        private static async Task DrainBodyStreamAsync(Stream bodyStream, long contentLength, bool isChunked, CancellationToken ct)
        {
            if (isChunked)
            {
                await CopyChunkedBodyToStreamAsync(bodyStream, Stream.Null, ct);
            }
            else if (contentLength > 0)
            {
                await CopyExactBytesAsync(bodyStream, Stream.Null, contentLength, ct);
            }
        }

        private sealed class PreBufferedNetworkStream : Stream
        {
            private readonly byte[] _prefix;
            private int _prefixOffset;
            private readonly Stream _inner;

            public PreBufferedNetworkStream(byte[] prefix, Stream inner)
            {
                _prefix = prefix;
                _inner = inner;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_prefixOffset < _prefix.Length)
                {
                    int toCopy = Math.Min(count, _prefix.Length - _prefixOffset);
                    Buffer.BlockCopy(_prefix, _prefixOffset, buffer, offset, toCopy);
                    _prefixOffset += toCopy;
                    return toCopy;
                }
                return _inner.Read(buffer, offset, count);
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_prefixOffset < _prefix.Length)
                {
                    int toCopy = Math.Min(count, _prefix.Length - _prefixOffset);
                    Buffer.BlockCopy(_prefix, _prefixOffset, buffer, offset, toCopy);
                    _prefixOffset += toCopy;
                    return toCopy;
                }
                return await _inner.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        #endregion
    }
}
