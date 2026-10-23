using System;
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
    }
}
