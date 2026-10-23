using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SyncDroidWinUI.Helpers
{
    public static class FileHashHelper
    {
        public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct = default)
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
            byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static async Task<string> ComputeFileMd5Async(string filePath, CancellationToken ct = default)
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
            byte[] hash = await MD5.HashDataAsync(fs, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string? ParseShellHashOutput(string? rawOutput)
        {
            if (string.IsNullOrWhiteSpace(rawOutput)) return null;

            foreach (var rawLine in rawOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                int spaceIdx = line.IndexOfAny(new[] { ' ', '\t', '*' });
                string token = (spaceIdx > 0 ? line.Substring(0, spaceIdx) : line).Trim();

                if (token.Length == 64 || token.Length == 32)
                {
                    bool allHex = true;
                    foreach (char c in token)
                    {
                        bool isHex = (c >= '0' && c <= '9') ||
                                     (c >= 'a' && c <= 'f') ||
                                     (c >= 'A' && c <= 'F');
                        if (!isHex)
                        {
                            allHex = false;
                            break;
                        }
                    }

                    if (allHex)
                    {
                        return token.ToLowerInvariant();
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Checks whether an endpoint, host, or ADB device serial refers to a loopback address.
        /// </summary>
        public static bool IsLoopbackOrDisallowedEndpoint(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            string trimmed = value.Trim();
            bool hadScheme = false;
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring("http://".Length);
                hadScheme = true;
            }
            else if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring("https://".Length);
                hadScheme = true;
            }
            trimmed = trimmed.TrimEnd('/');

            string host = trimmed;
            bool hasPort = false;
            int colonIdx = trimmed.LastIndexOf(':');
            if (colonIdx > 0 && int.TryParse(trimmed.Substring(colonIdx + 1), out _))
            {
                host = trimmed.Substring(0, colonIdx).Trim('[', ']');
                hasPort = true;
            }

            if (IPAddress.TryParse(host, out var ip))
            {
                if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                {
                    return true;
                }
                return false;
            }

            // If it has a port or HTTP scheme (network endpoint) but host is not a valid IP (e.g. loopback hostname), disallow it
            if (hasPort || hadScheme)
            {
                return true;
            }

            return false;
        }

        public static string FormatByteSize(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:F1} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F2} MB";
            double gb = mb / 1024.0;
            return $"{gb:F2} GB";
        }

        public static string FormatTransferSpeed(long bytesTransferred, double elapsedSeconds)
        {
            if (bytesTransferred <= 0) return "0 B/s";
            if (elapsedSeconds < 0.001) elapsedSeconds = 0.001;
            double bytesPerSec = bytesTransferred / elapsedSeconds;
            if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
            double kbPerSec = bytesPerSec / 1024.0;
            if (kbPerSec < 1024) return $"{kbPerSec:F1} KB/s";
            double mbPerSec = kbPerSec / 1024.0;
            if (mbPerSec < 1024) return $"{mbPerSec:F2} MB/s";
            double gbPerSec = mbPerSec / 1024.0;
            return $"{gbPerSec:F2} GB/s";
        }

        public static double CalculateOverallPercent(long completedBytes, long totalBytes, int processedFiles, int totalFiles)
        {
            if (totalBytes > 0)
            {
                return Math.Clamp((completedBytes * 100.0) / totalBytes, 0.0, 100.0);
            }
            if (totalFiles > 0)
            {
                return Math.Clamp((processedFiles * 100.0) / totalFiles, 0.0, 100.0);
            }
            return 100.0;
        }
    }

    public sealed class ProgressTrackingStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long, string>? _throttledCallback;
        private readonly Stopwatch _totalSw = Stopwatch.StartNew();
        private readonly Stopwatch _windowSw = Stopwatch.StartNew();
        private long _windowBytes;

        public long TotalBytesTransferred { get; private set; }
        public double ElapsedSeconds => _totalSw.Elapsed.TotalSeconds;

        public ProgressTrackingStream(Stream inner, Action<long, string>? throttledCallback = null)
        {
            _inner = inner;
            _throttledCallback = throttledCallback;
        }

        private void OnBytesTransferred(int count)
        {
            if (count <= 0) return;
            TotalBytesTransferred += count;
            _windowBytes += count;

            if (_throttledCallback != null && _windowSw.ElapsedMilliseconds >= 1500)
            {
                double winSec = _windowSw.Elapsed.TotalSeconds;
                string speedStr = FileHashHelper.FormatTransferSpeed(_windowBytes, winSec);
                _windowBytes = 0;
                _windowSw.Restart();
                _throttledCallback(TotalBytesTransferred, speedStr);
            }
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            OnBytesTransferred(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            OnBytesTransferred(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            OnBytesTransferred(read);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            OnBytesTransferred(count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            OnBytesTransferred(count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            OnBytesTransferred(buffer.Length);
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _totalSw.Stop();
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
