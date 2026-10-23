using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SyncDroidWinUI.Helpers;
using SyncDroidWinUI.Models;

namespace SyncDroidWinUI.Services
{
    public class LocalSendService
    {
        public const string MulticastGroupIp = "224.0.0.167";
        public const int DefaultPort = 53317;
        public const string ProtocolVersion = "2.1";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public static LocalSendService Instance { get; } = new LocalSendService();

        public LocalSendPeer? CurrentPeer { get; set; }
        public bool IsServerRunning { get; private set; }
        public int CurrentListeningPort { get; private set; } = DefaultPort;
        public string? LastServerError { get; private set; }
        public bool IsAccessForbiddenError { get; private set; }

        public void DisconnectCurrentPeer()
        {
            var peer = CurrentPeer;
            CurrentPeer = null;
            if (peer != null)
            {
                _discoveredPeers.TryRemove(peer.Endpoint, out _);
            }

            var settings = SettingsService.Load();
            if (!string.IsNullOrEmpty(settings.LastLocalSendIpEndpoint))
            {
                settings.LastLocalSendIpEndpoint = "";
                SettingsService.Save(settings);
            }
        }

        public event Action<string>? ServerActivityLogged;
        public event Action<IncomingTransferSessionEventArgs>? IncomingTransferRequested;
        public event Action<string, string, double, string>? IncomingFileProgressUpdated;
        public event Action<string, string, string, bool, bool>? IncomingFileCompleted;

        private readonly HttpClient _httpClient;
        private readonly HttpClient _probeHttpClient;
        private readonly X509Certificate2 _serverCertificate;
        private readonly string _fingerprint;
        private readonly ConcurrentDictionary<string, LocalSendPeer> _discoveredPeers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ReceiveSession> _receiveSessions = new(StringComparer.Ordinal);

        private CancellationTokenSource? _serverCts;
        private TcpListener? _tcpListener;
        private UdpClient? _udpListener;

        private sealed class ReceiveSession
        {
            public string SessionId { get; set; } = "";
            public string SenderAlias { get; set; } = "";
            public Dictionary<string, LocalSendFileDto> Files { get; set; } = new();
            public Dictionary<string, string> Tokens { get; set; } = new();
        }

        private LocalSendService()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(30)
            };

            var probeHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            _probeHttpClient = new HttpClient(probeHandler)
            {
                Timeout = TimeSpan.FromMilliseconds(2200)
            };

            _serverCertificate = CreateSelfSignedCertificate(out _fingerprint);
            StartOrRestartServerFromSettings();
        }

        private static X509Certificate2 CreateSelfSignedCertificate(out string fingerprintHex)
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=SyncDroid LocalSend", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            byte[] pfxBytes = ephemeral.Export(X509ContentType.Pfx);
            var cert = X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.Exportable);
            byte[] hash = SHA256.HashData(cert.RawData);
            fingerprintHex = Convert.ToHexString(hash).ToLowerInvariant();
            return cert;
        }

        private void LogServerActivity(string message)
        {
            ServerActivityLogged?.Invoke($"[LocalSend] {message}");
        }

        private LocalSendInfoDto BuildLocalInfoDto(bool? announce = null)
        {
            var settings = SettingsService.Load();
            string alias = string.IsNullOrWhiteSpace(settings.LocalSendAlias)
                ? $"{Environment.MachineName} (SyncDroid)"
                : settings.LocalSendAlias.Trim();

            return new LocalSendInfoDto
            {
                Alias = alias,
                Version = ProtocolVersion,
                DeviceModel = "Windows PC",
                DeviceType = "desktop",
                Fingerprint = _fingerprint,
                Port = CurrentListeningPort,
                Protocol = "https",
                Download = false,
                Announce = announce
            };
        }

        #region Server Lifecycle (UDP Multicast 224.0.0.167:53317 + Dual HTTP/HTTPS TCP 53317)

        public void StartOrRestartServerFromSettings()
        {
            StopServer();

            var settings = SettingsService.Load();
            CurrentListeningPort = settings.LocalSendPort > 0 && settings.LocalSendPort <= 65535
                ? settings.LocalSendPort
                : DefaultPort;

            if (!settings.LocalSendEnabled)
            {
                IsServerRunning = false;
                LastServerError = null;
                IsAccessForbiddenError = false;
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
                IsAccessForbiddenError = false;

                _ = Task.Run(() => RunTcpAcceptLoopAsync(_tcpListener, ct), ct);
            }
            catch (Exception ex)
            {
                IsServerRunning = false;
                LastServerError = ex.Message;
                IsAccessForbiddenError =
                    (ex is SocketException sockEx && (sockEx.SocketErrorCode == SocketError.AccessDenied || sockEx.SocketErrorCode == SocketError.AddressAlreadyInUse)) ||
                    ex.Message.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("访问权限", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("存取權限", StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                _udpListener = new UdpClient();
                _udpListener.ExclusiveAddressUse = false;
                _udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpListener.EnableBroadcast = true;
                _udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, DefaultPort));

                var multicastAddr = IPAddress.Parse(MulticastGroupIp);
                try
                {
                    _udpListener.JoinMulticastGroup(multicastAddr);
                }
                catch { }

                foreach (var localIp in CompanionSyncService.GetLocalLanIpAddresses())
                {
                    if (IPAddress.TryParse(localIp, out var ifaceIp))
                    {
                        try
                        {
                            _udpListener.JoinMulticastGroup(multicastAddr, ifaceIp);
                        }
                        catch { }
                    }
                }

                _ = Task.Run(() => RunUdpMulticastLoopAsync(_udpListener, ct), ct);
                _ = Task.Run(() => BroadcastMulticastAnnouncementAsync(announce: true), ct);
            }
            catch
            {
                // Ignore if UDP 53317 is already bound by standalone LocalSend app
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

            try { _udpListener?.Close(); _udpListener?.Dispose(); } catch { }
            _udpListener = null;

            IsServerRunning = false;
        }

        private async Task RunUdpMulticastLoopAsync(UdpClient udp, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(ct);
                    string json = Encoding.UTF8.GetString(result.Buffer).Trim();
                    if (!json.StartsWith("{", StringComparison.Ordinal)) continue;

                    var info = JsonSerializer.Deserialize<LocalSendInfoDto>(json, JsonOptions);
                    if (info == null) continue;

                    string senderIp = result.RemoteEndPoint.Address.ToString();
                    if (FileHashHelper.IsLoopbackOrDisallowedEndpoint(senderIp)) continue;
                    if (IsSelfAnnouncement(info, senderIp)) continue;

                    var peer = StorePeerFromInfo(info, senderIp);

                    if (info.Announce == true && IsServerRunning)
                    {
                        _ = Task.Run(async () =>
                        {
                            bool registered = await SendRegisterToPeerAsync(peer, ct);
                            if (!registered)
                            {
                                await BroadcastMulticastAnnouncementAsync(announce: false);
                            }
                        }, ct);
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

        private bool IsSelfAnnouncement(LocalSendInfoDto info, string senderIp)
        {
            if (!string.IsNullOrEmpty(info.Fingerprint) &&
                string.Equals(info.Fingerprint, _fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var localIps = CompanionSyncService.GetLocalLanIpAddresses();
            if (localIps.Contains(senderIp, StringComparer.OrdinalIgnoreCase) &&
                info.Port == CurrentListeningPort)
            {
                return true;
            }

            return false;
        }

        private LocalSendPeer StorePeerFromInfo(LocalSendInfoDto info, string senderIp, int? fallbackPort = null, string? fallbackProtocol = null)
        {
            int port = info.Port > 0 ? info.Port : (fallbackPort ?? DefaultPort);
            string protocol = !string.IsNullOrWhiteSpace(info.Protocol)
                ? info.Protocol.ToLowerInvariant()
                : (fallbackProtocol ?? "https");

            var peer = new LocalSendPeer
            {
                Alias = string.IsNullOrWhiteSpace(info.Alias) ? senderIp : info.Alias,
                Version = string.IsNullOrWhiteSpace(info.Version) ? ProtocolVersion : info.Version,
                DeviceModel = info.DeviceModel ?? "",
                DeviceType = info.DeviceType ?? "",
                Fingerprint = info.Fingerprint ?? "",
                IpAddress = senderIp,
                Port = port,
                Protocol = protocol
            };

            _discoveredPeers[peer.Endpoint] = peer;
            return peer;
        }

        private async Task<bool> SendRegisterToPeerAsync(LocalSendPeer peer, CancellationToken ct)
        {
            try
            {
                var myInfo = BuildLocalInfoDto(announce: null);
                string bodyJson = JsonSerializer.Serialize(myInfo, JsonOptions);
                using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                string url = $"{peer.Protocol}://{peer.IpAddress}:{peer.Port}/api/localsend/v2/register";
                using var resp = await _probeHttpClient.PostAsync(url, content, ct);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task BroadcastMulticastAnnouncementAsync(bool announce)
        {
            try
            {
                var dto = BuildLocalInfoDto(announce);
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto, JsonOptions));

                using var sender = new UdpClient();
                sender.EnableBroadcast = true;
                var multicastEp = new IPEndPoint(IPAddress.Parse(MulticastGroupIp), DefaultPort);
                await sender.SendAsync(bytes, bytes.Length, multicastEp);

                var broadcastEp = new IPEndPoint(IPAddress.Broadcast, DefaultPort);
                await sender.SendAsync(bytes, bytes.Length, broadcastEp);
            }
            catch { }
        }

        private async Task RunTcpAcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => HandleTcpClientAsync(client, ct), ct);
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

        private async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                client.ReceiveTimeout = 600000;
                client.SendTimeout = 600000;

                string remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
                using var netStream = client.GetStream();

                // Read first byte to auto-detect TLS (0x16) vs plain HTTP
                byte[] firstByteBuf = new byte[1];
                int firstRead = await netStream.ReadAsync(firstByteBuf, 0, 1, ct);
                if (firstRead <= 0) return;

                using var peekedNetStream = new PreBufferedStream(firstByteBuf, netStream);
                Stream transportStream = peekedNetStream;
                SslStream? sslStream = null;

                try
                {
                    if (firstByteBuf[0] == 0x16) // TLS Handshake ClientHello
                    {
                        sslStream = new SslStream(peekedNetStream, leaveInnerStreamOpen: true);
                        await sslStream.AuthenticateAsServerAsync(_serverCertificate, clientCertificateRequired: false, checkCertificateRevocation: false);
                        transportStream = sslStream;
                    }

                    await ProcessHttpRequestOnStreamAsync(transportStream, remoteIp, ct);
                }
                catch
                {
                    // Ignore malformed or aborted probe connections
                }
                finally
                {
                    if (sslStream != null)
                    {
                        try { await sslStream.DisposeAsync(); } catch { }
                    }
                }
            }
        }

        private async Task ProcessHttpRequestOnStreamAsync(Stream stream, string remoteIp, CancellationToken ct)
        {
            var headerRead = await ReadHttpHeadersAsync(stream, ct);
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
                await stream.WriteAsync(continueBytes, 0, continueBytes.Length, ct);
                await stream.FlushAsync(ct);
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

            using var bodyStream = new PreBufferedStream(leftoverBytes, stream);

            if (method == "GET" &&
                (string.Equals(routePath, "/api/localsend/v2/info", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(routePath, "/api/localsend/v1/info", StringComparison.OrdinalIgnoreCase)))
            {
                var info = BuildLocalInfoDto(announce: null);
                await WriteJsonResponseAsync(stream, 200, "OK", info, ct);
            }
            else if (method == "POST" &&
                     (string.Equals(routePath, "/api/localsend/v2/register", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(routePath, "/api/localsend/v1/register", StringComparison.OrdinalIgnoreCase)))
            {
                string reqJson = await ReadBodyAsStringAsync(bodyStream, contentLength, isChunked, ct);
                if (!string.IsNullOrWhiteSpace(reqJson))
                {
                    try
                    {
                        var peerInfo = JsonSerializer.Deserialize<LocalSendInfoDto>(reqJson, JsonOptions);
                        if (peerInfo != null && !FileHashHelper.IsLoopbackOrDisallowedEndpoint(remoteIp) && !IsSelfAnnouncement(peerInfo, remoteIp))
                        {
                            StorePeerFromInfo(peerInfo, remoteIp);
                        }
                    }
                    catch { }
                }

                var myInfo = BuildLocalInfoDto(announce: null);
                await WriteJsonResponseAsync(stream, 200, "OK", myInfo, ct);
            }
            else if (method == "POST" &&
                     (string.Equals(routePath, "/api/localsend/v2/prepare-upload", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(routePath, "/api/localsend/v1/prepare-upload", StringComparison.OrdinalIgnoreCase)))
            {
                string reqJson = await ReadBodyAsStringAsync(bodyStream, contentLength, isChunked, ct);
                var prepReq = JsonSerializer.Deserialize<LocalSendPrepareUploadRequestDto>(reqJson, JsonOptions);
                if (prepReq == null || prepReq.Files == null || prepReq.Files.Count == 0)
                {
                    await WriteJsonResponseAsync(stream, 400, "Bad Request", new { error = "Invalid prepare-upload payload" }, ct);
                    return;
                }

                if (prepReq.Info != null && !FileHashHelper.IsLoopbackOrDisallowedEndpoint(remoteIp) && !IsSelfAnnouncement(prepReq.Info, remoteIp))
                {
                    StorePeerFromInfo(prepReq.Info, remoteIp);
                }

                string senderAlias = string.IsNullOrWhiteSpace(prepReq.Info?.Alias) ? remoteIp : prepReq.Info!.Alias;
                string senderFp = prepReq.Info?.Fingerprint ?? "";
                int senderPort = (prepReq.Info?.Port > 0) ? prepReq.Info.Port : DefaultPort;
                bool isTrusted = SettingsService.IsDeviceTrusted("LocalSend", senderFp, remoteIp, senderAlias);
                string receiveRoot = SettingsService.Load().LocalSendReceiveDirectory;
                var loc = LocalizationService.Instance;

                string sessionId = Guid.NewGuid().ToString("N");
                var receivedItems = new List<ReceivedFileItem>();
                foreach (var kvp in prepReq.Files)
                {
                    string sanitizedRel = CompanionSyncService.SanitizeRelativePathForWindows(kvp.Value.FileName);
                    string targetPath = Path.Combine(receiveRoot, sanitizedRel.Replace('/', '\\'));
                    receivedItems.Add(new ReceivedFileItem
                    {
                        Id = kvp.Key,
                        FileName = kvp.Value.FileName,
                        SenderName = senderAlias,
                        Protocol = "LocalSend",
                        Size = kvp.Value.Size,
                        SizeDisplay = FileHashHelper.FormatByteSize(kvp.Value.Size),
                        SavedPath = targetPath,
                        StatusText = isTrusted ? loc.ReceiveStatusWaitingUpload : loc.ReceiveStatusPending,
                        ProgressPercent = 0
                    });
                }

                var tcs = !isTrusted ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
                var sessionArgs = new IncomingTransferSessionEventArgs
                {
                    SessionId = sessionId,
                    SenderName = senderAlias,
                    SenderIp = remoteIp,
                    Protocol = "LocalSend",
                    SaveDirectory = receiveRoot,
                    RequiresConfirmation = !isTrusted,
                    Files = receivedItems,
                    ConfirmationTcs = tcs
                };

                IncomingTransferRequested?.Invoke(sessionArgs);

                if (!isTrusted && tcs != null)
                {
                    LogServerActivity($"Incoming transfer request from '{senderAlias}' ({prepReq.Files.Count} file(s)) — waiting for user confirmation...");
                    bool accepted = false;
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
                    try
                    {
                        using (timeoutCts.Token.Register(() => tcs.TrySetResult(false)))
                        {
                            accepted = await tcs.Task;
                        }
                    }
                    catch
                    {
                        accepted = false;
                    }

                    if (!accepted)
                    {
                        LogServerActivity($"Declined incoming transfer request from '{senderAlias}'.");
                        await WriteJsonResponseAsync(stream, 403, "Forbidden", new { error = "Declined by user" }, ct);
                        return;
                    }

                    SettingsService.AddTrustedDevice("LocalSend", senderAlias, $"{remoteIp}:{senderPort}", senderFp);
                }

                var session = new ReceiveSession
                {
                    SessionId = sessionId,
                    SenderAlias = senderAlias,
                    Files = prepReq.Files
                };

                var responseTokens = new Dictionary<string, string>();
                foreach (var kvp in prepReq.Files)
                {
                    string token = Guid.NewGuid().ToString("N");
                    session.Tokens[kvp.Key] = token;
                    responseTokens[kvp.Key] = token;
                }

                _receiveSessions[sessionId] = session;
                LogServerActivity($"Accepted incoming transfer session from '{session.SenderAlias}' ({prepReq.Files.Count} file(s))");

                var prepResp = new LocalSendPrepareUploadResponseDto
                {
                    SessionId = sessionId,
                    Files = responseTokens
                };
                await WriteJsonResponseAsync(stream, 200, "OK", prepResp, ct);
            }
            else if (method == "POST" &&
                     (string.Equals(routePath, "/api/localsend/v2/upload", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(routePath, "/api/localsend/v1/upload", StringComparison.OrdinalIgnoreCase)))
            {
                string sessionId = query.TryGetValue("sessionId", out var sid) ? sid : "";
                string fileId = query.TryGetValue("fileId", out var fid) ? fid : "";
                string token = query.TryGetValue("token", out var tok) ? tok : "";

                if (!_receiveSessions.TryGetValue(sessionId, out var session) ||
                    !session.Files.TryGetValue(fileId, out var fileDto) ||
                    !session.Tokens.TryGetValue(fileId, out var expectedToken) ||
                    !string.Equals(token, expectedToken, StringComparison.Ordinal))
                {
                    await WriteJsonResponseAsync(stream, 403, "Forbidden", new { error = "Invalid session, fileId, or token" }, ct);
                    return;
                }

                var settings = SettingsService.Load();
                string receiveRoot = settings.LocalSendReceiveDirectory;
                var conflict = Enum.IsDefined(typeof(ConflictPolicy), settings.DirectTransfer_ConflictPolicy)
                    ? (ConflictPolicy)settings.DirectTransfer_ConflictPolicy
                    : ConflictPolicy.SkipIdenticalSize;

                string sanitizedRel = CompanionSyncService.SanitizeRelativePathForWindows(fileDto.FileName);
                string finalTarget = Path.Combine(receiveRoot, sanitizedRel.Replace('/', '\\'));
                string parentDir = Path.GetDirectoryName(finalTarget) ?? receiveRoot;
                Directory.CreateDirectory(parentDir);

                if (File.Exists(finalTarget))
                {
                    if (conflict == ConflictPolicy.SkipIdenticalSize && new FileInfo(finalTarget).Length == fileDto.Size)
                    {
                        await DrainBodyStreamAsync(bodyStream, contentLength, isChunked, ct);
                        LogServerActivity($"Skipped (Identical): {sanitizedRel}");
                        IncomingFileCompleted?.Invoke(sessionId, fileId, finalTarget, true, false);
                        await WriteJsonResponseAsync(stream, 200, "OK", new { ok = true, skipped = true }, ct);
                        return;
                    }
                    if (conflict == ConflictPolicy.KeepBothRename)
                    {
                        finalTarget = CompanionSyncService.GetUniqueKeepBothLocalPath(finalTarget);
                    }
                }

                string tempPart = Path.Combine(parentDir, $".syncdroid_{Guid.NewGuid():N}.part");
                try
                {
                    IncomingFileProgressUpdated?.Invoke(sessionId, fileId, 0, "");
                    long bytesReceived = 0;
                    double elapsedSec = 0;
                    using (var rawFs = new FileStream(tempPart, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                    using (var trackedFs = new ProgressTrackingStream(rawFs, (inFlightBytes, liveSpeed) =>
                    {
                        double livePct = FileHashHelper.CalculateOverallPercent(inFlightBytes, fileDto.Size, 0, 1);
                        IncomingFileProgressUpdated?.Invoke(sessionId, fileId, livePct, liveSpeed);
                        LogServerActivity($"Receiving: {sanitizedRel} | Speed: {liveSpeed} | Overall: {livePct:F1}%");
                    }))
                    {
                        if (isChunked)
                        {
                            await CopyChunkedBodyToStreamAsync(bodyStream, trackedFs, ct);
                        }
                        else if (contentLength >= 0)
                        {
                            await CopyExactBytesAsync(bodyStream, trackedFs, contentLength, ct);
                        }
                        else
                        {
                            await bodyStream.CopyToAsync(trackedFs, 65536, ct);
                        }
                        bytesReceived = trackedFs.TotalBytesTransferred;
                        elapsedSec = trackedFs.ElapsedSeconds;
                    }

                    if (settings.VerifyHashAfterTransfer && !string.IsNullOrWhiteSpace(fileDto.Sha256))
                    {
                        string receivedSha256 = await FileHashHelper.ComputeFileSha256Async(tempPart, ct);
                        if (!string.Equals(receivedSha256, fileDto.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(tempPart); } catch { }
                            throw new IOException($"Hash verification failed (SHA-256) for incoming file '{sanitizedRel}'.");
                        }
                        LogServerActivity($"Hash verified (SHA-256): {sanitizedRel}");
                    }

                    File.Move(tempPart, finalTarget, overwrite: true);

                    if (!string.IsNullOrWhiteSpace(fileDto.Metadata?.Modified) &&
                        DateTimeOffset.TryParse(fileDto.Metadata.Modified, out var parsedModified))
                    {
                        try
                        {
                            File.SetLastWriteTimeUtc(finalTarget, parsedModified.UtcDateTime);
                        }
                        catch { }
                    }

                    string avgSpeed = FileHashHelper.FormatTransferSpeed(bytesReceived, elapsedSec);
                    IncomingFileCompleted?.Invoke(sessionId, fileId, finalTarget, false, false);
                    LogServerActivity($"Received file from '{session.SenderAlias}': {finalTarget} ({FileHashHelper.FormatByteSize(bytesReceived)}) | Speed: {avgSpeed} | Overall: 100%");
                    await WriteJsonResponseAsync(stream, 200, "OK", new { ok = true }, ct);
                }
                catch
                {
                    IncomingFileCompleted?.Invoke(sessionId, fileId, finalTarget, false, true);
                    try { if (File.Exists(tempPart)) File.Delete(tempPart); } catch { }
                    throw;
                }
            }
            else if (method == "POST" &&
                     (string.Equals(routePath, "/api/localsend/v2/cancel", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(routePath, "/api/localsend/v1/cancel", StringComparison.OrdinalIgnoreCase)))
            {
                string sessionId = query.TryGetValue("sessionId", out var sid) ? sid : "";
                if (!string.IsNullOrEmpty(sessionId) && _receiveSessions.TryRemove(sessionId, out var removed))
                {
                    LogServerActivity($"Transfer session cancelled by '{removed.SenderAlias}'.");
                }
                await WriteJsonResponseAsync(stream, 200, "OK", new { ok = true }, ct);
            }
            else
            {
                await WriteJsonResponseAsync(stream, 404, "Not Found", new { error = "Not Found" }, ct);
            }
        }

        #endregion

        #region Client Discovery & File Transfer (PC -> LocalSend Device)

        public async Task<List<LocalSendPeer>> ScanLanForPeersAsync(CancellationToken ct = default)
        {
            await BroadcastMulticastAnnouncementAsync(announce: true);
            await Task.Delay(650, ct).ConfigureAwait(false);

            var settings = SettingsService.Load();
            if (!string.IsNullOrWhiteSpace(settings.LastLocalSendIpEndpoint) &&
                !FileHashHelper.IsLoopbackOrDisallowedEndpoint(settings.LastLocalSendIpEndpoint))
            {
                var saved = await ProbePeerEndpointAsync(settings.LastLocalSendIpEndpoint, ct);
                if (saved != null)
                {
                    _discoveredPeers[saved.Endpoint] = saved;
                }
            }

            var verifyTasks = _discoveredPeers.Values.Select(async p =>
            {
                if (FileHashHelper.IsLoopbackOrDisallowedEndpoint(p.Endpoint))
                {
                    _discoveredPeers.TryRemove(p.Endpoint, out _);
                    return null;
                }
                var verified = await ProbePeerEndpointAsync(p.Endpoint, ct, p.Protocol);
                if (verified != null) return verified;
                _discoveredPeers.TryRemove(p.Endpoint, out _);
                return null;
            });

            var verifiedPeers = await Task.WhenAll(verifyTasks);
            return verifiedPeers
                .Where(p => p != null)
                .Select(p => p!)
                .OrderBy(p => p.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<LocalSendPeer?> ProbePeerEndpointAsync(string rawEndpoint, CancellationToken ct = default, string? preferredProtocol = null)
        {
            if (string.IsNullOrWhiteSpace(rawEndpoint)) return null;
            if (FileHashHelper.IsLoopbackOrDisallowedEndpoint(rawEndpoint)) return null;

            string trimmed = rawEndpoint.Trim();
            string? explicitScheme = null;
            if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                explicitScheme = "https";
                trimmed = trimmed.Substring("https://".Length);
            }
            else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                explicitScheme = "http";
                trimmed = trimmed.Substring("http://".Length);
            }
            trimmed = trimmed.TrimEnd('/');

            string host = trimmed;
            int port = DefaultPort;
            int colonIdx = trimmed.LastIndexOf(':');
            if (colonIdx > 0 && int.TryParse(trimmed.Substring(colonIdx + 1), out int parsedPort) && parsedPort > 0 && parsedPort <= 65535)
            {
                host = trimmed.Substring(0, colonIdx);
                port = parsedPort;
            }

            string[] schemes = explicitScheme != null
                ? new[] { explicitScheme }
                : (string.Equals(preferredProtocol, "http", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "http", "https" }
                    : new[] { "https", "http" });

            foreach (var scheme in schemes)
            {
                foreach (var versionSegment in new[] { "v2", "v1" })
                {
                    try
                    {
                        string url = $"{scheme}://{host}:{port}/api/localsend/{versionSegment}/info";
                        using var resp = await _probeHttpClient.GetAsync(url, ct);
                        if (!resp.IsSuccessStatusCode) continue;

                        string json = await resp.Content.ReadAsStringAsync(ct);
                        var info = JsonSerializer.Deserialize<LocalSendInfoDto>(json, JsonOptions);
                        if (info == null || string.IsNullOrWhiteSpace(info.Alias)) continue;

                        if (IsSelfAnnouncement(info, host)) return null;

                        return StorePeerFromInfo(info, host, port, scheme);
                    }
                    catch
                    {
                        // Try next scheme/version
                    }
                }
            }

            return null;
        }

        public async Task ExecuteDirectTransferToLocalSendAsync(
            IReadOnlyList<TransferQueueItem> queueItems,
            Action<int> progressCallback,
            Action<string> logCallback,
            CancellationToken token)
        {
            var peer = CurrentPeer ?? throw new InvalidOperationException("No LocalSend device selected.");

            var filePlan = CompanionSyncService.ExpandQueueItemsToFiles(queueItems);
            if (filePlan.Count == 0)
            {
                logCallback("No files found in the selected queue items.");
                progressCallback(100);
                return;
            }

            var validPlan = new List<(string FileId, FileInfo FileInfo, string RelativeDestPath)>();
            int idx = 0;
            foreach (var (fi, relPath) in filePlan)
            {
                if (FileNameLengthHelper.IsFileNameOver255Bytes(fi.FullName))
                {
                    logCallback($"Skipped (Filename > 255 bytes): {relPath}");
                    continue;
                }
                validPlan.Add(($"file_{idx++}", fi, relPath));
            }

            if (validPlan.Count == 0)
            {
                logCallback("No valid files remaining to transfer.");
                progressCallback(100);
                return;
            }

            bool verifyHash = SettingsService.Load().VerifyHashAfterTransfer;
            var prepReq = new LocalSendPrepareUploadRequestDto
            {
                Info = BuildLocalInfoDto(announce: null),
                Files = new Dictionary<string, LocalSendFileDto>()
            };

            foreach (var item in validPlan)
            {
                token.ThrowIfCancellationRequested();
                string? sha256 = verifyHash ? await FileHashHelper.ComputeFileSha256Async(item.FileInfo.FullName, token) : null;
                prepReq.Files[item.FileId] = new LocalSendFileDto
                {
                    Id = item.FileId,
                    FileName = item.RelativeDestPath,
                    Size = item.FileInfo.Length,
                    FileType = GuessMimeType(item.FileInfo.Extension),
                    Sha256 = sha256,
                    Metadata = new LocalSendFileMetadataDto
                    {
                        Modified = item.FileInfo.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                    }
                };
            }

            string baseUrl = $"{peer.Protocol}://{peer.Endpoint}";
            logCallback($"[LocalSend] Requesting transfer to '{peer.Alias}' ({peer.Endpoint}) — waiting for receiver confirmation...");

            string prepJson = JsonSerializer.Serialize(prepReq, JsonOptions);
            HttpResponseMessage? prepHttpResp = null;

            try
            {
                using var prepContent = new StringContent(prepJson, Encoding.UTF8, "application/json");
                prepHttpResp = await _httpClient.PostAsync($"{baseUrl}/api/localsend/v2/prepare-upload", prepContent, token);
            }
            catch
            {
                // Fallback scheme if peer switched between http and https
                string altScheme = string.Equals(peer.Protocol, "https", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
                baseUrl = $"{altScheme}://{peer.Endpoint}";
                using var prepContent = new StringContent(prepJson, Encoding.UTF8, "application/json");
                prepHttpResp = await _httpClient.PostAsync($"{baseUrl}/api/localsend/v2/prepare-upload", prepContent, token);
                peer.Protocol = altScheme;
            }

            using (prepHttpResp)
            {
                if (prepHttpResp.StatusCode == HttpStatusCode.NoContent)
                {
                    logCallback($"[LocalSend] Receiver '{peer.Alias}' completed with 204 No Content (no files needed).");
                    progressCallback(100);
                    return;
                }
                if (prepHttpResp.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException($"Transfer request was declined by '{peer.Alias}' (403 Forbidden).");
                }
                if (prepHttpResp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException($"Target device '{peer.Alias}' requires a PIN code (401 Unauthorized).");
                }

                prepHttpResp.EnsureSuccessStatusCode();

                string respBody = await prepHttpResp.Content.ReadAsStringAsync(token);
                var prepResp = JsonSerializer.Deserialize<LocalSendPrepareUploadResponseDto>(respBody, JsonOptions);
                if (prepResp == null || string.IsNullOrWhiteSpace(prepResp.SessionId))
                {
                    throw new InvalidOperationException("Invalid prepare-upload response from LocalSend receiver.");
                }

                string sessionId = prepResp.SessionId;
                int total = validPlan.Count;
                long totalBytes = validPlan.Sum(x => x.FileInfo.Length);
                long completedBytes = 0;
                int processed = 0;

                try
                {
                    foreach (var item in validPlan)
                    {
                        token.ThrowIfCancellationRequested();

                        if (prepResp.Files == null ||
                            !prepResp.Files.TryGetValue(item.FileId, out var fileToken) ||
                            string.IsNullOrEmpty(fileToken))
                        {
                            processed++;
                            completedBytes += item.FileInfo.Length;
                            double skipPct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, total);
                            logCallback($"Skipped by receiver: {item.RelativeDestPath} | Overall: {skipPct:F1}% ({processed}/{total})");
                            progressCallback((int)Math.Round(skipPct));
                            continue;
                        }

                        long baseCompleted = completedBytes;
                        int nextIdx = processed + 1;
                        logCallback($"Pushing [LocalSend]: {item.RelativeDestPath} ({FileHashHelper.FormatByteSize(item.FileInfo.Length)})");
                        string uploadUrl =
                            $"{baseUrl}/api/localsend/v2/upload?sessionId={Uri.EscapeDataString(sessionId)}" +
                            $"&fileId={Uri.EscapeDataString(item.FileId)}" +
                            $"&token={Uri.EscapeDataString(fileToken)}";

                        long bytesSent = 0;
                        double elapsedSec = 0;
                        using (var rawFs = new FileStream(item.FileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true))
                        using (var trackedStream = new ProgressTrackingStream(rawFs, (inFlightBytes, liveSpeed) =>
                        {
                            double livePct = FileHashHelper.CalculateOverallPercent(baseCompleted + inFlightBytes, totalBytes, processed, total);
                            progressCallback((int)Math.Round(livePct));
                            logCallback($"Transferring [LocalSend]: {item.RelativeDestPath} | Speed: {liveSpeed} | Overall: {livePct:F1}% ({nextIdx}/{total})");
                        }))
                        using (var streamContent = new StreamContent(trackedStream, 65536))
                        {
                            streamContent.Headers.ContentLength = item.FileInfo.Length;
                            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                            using var uploadResp = await _httpClient.PostAsync(uploadUrl, streamContent, token);
                            uploadResp.EnsureSuccessStatusCode();
                            bytesSent = trackedStream.TotalBytesTransferred;
                            elapsedSec = trackedStream.ElapsedSeconds;
                        }

                        processed++;
                        completedBytes += item.FileInfo.Length;
                        double donePct = FileHashHelper.CalculateOverallPercent(completedBytes, totalBytes, processed, total);
                        string avgSpeed = FileHashHelper.FormatTransferSpeed(bytesSent > 0 ? bytesSent : item.FileInfo.Length, elapsedSec);
                        logCallback($"Transferred [LocalSend]: {item.RelativeDestPath} ({FileHashHelper.FormatByteSize(item.FileInfo.Length)}) | Speed: {avgSpeed} | Overall: {donePct:F1}% ({processed}/{total})");
                        progressCallback((int)Math.Round(donePct));
                    }
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        using var cancelContent = new ByteArrayContent(Array.Empty<byte>());
                        await _probeHttpClient.PostAsync($"{baseUrl}/api/localsend/v2/cancel?sessionId={Uri.EscapeDataString(sessionId)}", cancelContent);
                    }
                    catch { }
                    throw;
                }

                logCallback($"Direct transfer to device '{peer.Alias}' completed ({processed} file(s) processed | Overall: 100%).");
                progressCallback(100);
            }
        }

        private static string GuessMimeType(string ext)
        {
            return (ext?.ToLowerInvariant()) switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".mp4" => "video/mp4",
                ".mkv" => "video/x-matroska",
                ".mp3" => "audio/mpeg",
                ".flac" => "audio/flac",
                ".wav" => "audio/wav",
                ".pdf" => "application/pdf",
                ".txt" or ".md" or ".log" => "text/plain",
                ".json" => "application/json",
                ".zip" => "application/zip",
                ".apk" => "application/vnd.android.package-archive",
                _ => "application/octet-stream"
            };
        }

        #endregion

        #region HTTP Stream Helpers

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

        private static async Task<(string HeaderText, byte[] LeftoverBytes)?> ReadHttpHeadersAsync(Stream stream, CancellationToken ct)
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

        private static async Task<string> ReadBodyAsStringAsync(Stream bodyStream, long contentLength, bool isChunked, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            if (isChunked)
            {
                await CopyChunkedBodyToStreamAsync(bodyStream, ms, ct);
            }
            else if (contentLength > 0)
            {
                await CopyExactBytesAsync(bodyStream, ms, contentLength, ct);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static async Task WriteJsonResponseAsync<T>(Stream stream, int statusCode, string statusText, T payload, CancellationToken ct)
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

        private sealed class PreBufferedStream : Stream
        {
            private readonly byte[] _prefix;
            private int _prefixOffset;
            private readonly Stream _inner;

            public PreBufferedStream(byte[] prefix, Stream inner)
            {
                _prefix = prefix;
                _inner = inner;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

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

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_prefixOffset < _prefix.Length)
                {
                    int toCopy = Math.Min(buffer.Length, _prefix.Length - _prefixOffset);
                    _prefix.AsMemory(_prefixOffset, toCopy).CopyTo(buffer);
                    _prefixOffset += toCopy;
                    return toCopy;
                }
                return await _inner.ReadAsync(buffer, cancellationToken);
            }

            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.WriteAsync(buffer, offset, count, cancellationToken);
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        #endregion
    }
}
