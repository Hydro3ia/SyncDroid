using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SyncDroidWinUI.Helpers;
using SyncDroidWinUI.Models;

namespace SyncDroidWinUI.Services
{
    public class AppSettings
    {
        public string PcToAndroid_PcPath { get; set; } = "";
        public string PcToAndroid_AndroidPath { get; set; } = "";
        public string AndroidToPc_PcPath { get; set; } = "";
        public string AndroidToPc_AndroidPath { get; set; } = "";

        public List<string> PcPathHistory { get; set; } = new();
        public List<string> AndroidPathHistory { get; set; } = new();

        public string Language { get; set; } = "en-US";
        public int AdbSourceMode { get; set; } = (int)Models.AdbSourceMode.BuiltInOrRunning;
        public bool UseCustomAdbPath { get; set; } = false;
        public string CustomAdbDirectory { get; set; } = "";
        public bool KillBuiltInAdbOnExit { get; set; } = true;
        public bool VerifyHashAfterTransfer { get; set; } = true;

        // Wireless Companion & Sync Mode settings (v0.1.3.0+)
        public int CompanionPort { get; set; } = 45821;
        public bool CompanionServerEnabled { get; set; } = true;
        public string LastCompanionIpEndpoint { get; set; } = "";
        public List<string> CompanionEndpointHistory { get; set; } = new();

        public bool IsMirModeDefault { get; set; } = true;
        public bool PcToAndroid_IsMirMode { get; set; } = true;
        public bool AndroidToPc_IsMirMode { get; set; } = true;

        public string DirectTransfer_AndroidPath { get; set; } = "/storage/emulated/0/Download/SyncDroid";
        public string DirectTransfer_PcPath { get; set; } = "";
        public int DirectTransfer_ConflictPolicy { get; set; } = 0;

        // LocalSend Protocol settings (v0.1.4.0+)
        public bool LocalSendEnabled { get; set; } = true;
        public int LocalSendPort { get; set; } = 53317;
        public string LocalSendAlias { get; set; } = "";
        public string LocalSendReceiveDirectory { get; set; } = "";
        public string LastLocalSendIpEndpoint { get; set; } = "";

        // Authenticated / Trusted Devices
        public List<TrustedDevice> TrustedDevices { get; set; } = new();
    }

    public static class SettingsService
    {
        private const int MaxHistoryCount = 15;

        public static event EventHandler? TrustedDevicesChanged;

        private static readonly string SettingsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncDroidWinUI", "settings.json");

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }

            settings.PcPathHistory ??= new List<string>();
            settings.AndroidPathHistory ??= new List<string>();
            settings.CompanionEndpointHistory ??= new List<string>();
            settings.TrustedDevices ??= new List<TrustedDevice>();
            if (string.IsNullOrWhiteSpace(settings.Language))
            {
                settings.Language = "en-US";
            }
            settings.CustomAdbDirectory ??= "";
            if (!Enum.IsDefined(typeof(AdbSourceMode), settings.AdbSourceMode))
            {
                settings.AdbSourceMode = (int)AdbSourceMode.BuiltInOrRunning;
            }
            if (settings.UseCustomAdbPath && settings.AdbSourceMode == (int)AdbSourceMode.BuiltInOrRunning && !string.IsNullOrWhiteSpace(settings.CustomAdbDirectory))
            {
                settings.AdbSourceMode = (int)AdbSourceMode.CustomDirectory;
            }
            settings.UseCustomAdbPath = settings.AdbSourceMode == (int)AdbSourceMode.CustomDirectory;

            settings.LastCompanionIpEndpoint ??= "";
            if (FileHashHelper.IsLoopbackOrDisallowedEndpoint(settings.LastCompanionIpEndpoint))
            {
                settings.LastCompanionIpEndpoint = "";
            }
            if (settings.CompanionPort <= 0 || settings.CompanionPort > 65535)
            {
                settings.CompanionPort = 45821;
            }
            if (string.IsNullOrWhiteSpace(settings.DirectTransfer_AndroidPath))
            {
                settings.DirectTransfer_AndroidPath = "/storage/emulated/0/Download/SyncDroid";
            }
            settings.DirectTransfer_PcPath ??= "";

            if (settings.LocalSendPort <= 0 || settings.LocalSendPort > 65535)
            {
                settings.LocalSendPort = 53317;
            }
            if (string.IsNullOrWhiteSpace(settings.LocalSendAlias))
            {
                settings.LocalSendAlias = $"{Environment.MachineName} (SyncDroid)";
            }
            if (string.IsNullOrWhiteSpace(settings.LocalSendReceiveDirectory))
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                settings.LocalSendReceiveDirectory = Path.Combine(
                    string.IsNullOrWhiteSpace(userProfile) ? @"C:\" : userProfile,
                    "Downloads",
                    "SyncDroid");
            }
            settings.LastLocalSendIpEndpoint ??= "";
            if (FileHashHelper.IsLoopbackOrDisallowedEndpoint(settings.LastLocalSendIpEndpoint))
            {
                settings.LastLocalSendIpEndpoint = "";
            }

            settings.CompanionEndpointHistory.RemoveAll(FileHashHelper.IsLoopbackOrDisallowedEndpoint);

            // Ensure previously saved single paths are also present in history
            AddToHistoryList(settings.PcPathHistory, settings.AndroidToPc_PcPath, ignoreCase: true);
            AddToHistoryList(settings.PcPathHistory, settings.PcToAndroid_PcPath, ignoreCase: true);
            AddToHistoryList(settings.AndroidPathHistory, settings.AndroidToPc_AndroidPath, ignoreCase: false);
            AddToHistoryList(settings.AndroidPathHistory, settings.PcToAndroid_AndroidPath, ignoreCase: false);
            AddToHistoryList(settings.CompanionEndpointHistory, settings.LastCompanionIpEndpoint, ignoreCase: true);
            AddToHistoryList(settings.CompanionEndpointHistory, settings.LastLocalSendIpEndpoint, ignoreCase: true);

            return settings;
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsFile);
                if (dir != null) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);
            }
            catch { }
        }

        public static void AddToHistoryList(List<string> list, string? path, bool ignoreCase)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var trimmed = path.Trim();

            var comparer = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            list.RemoveAll(item => string.Equals(item, trimmed, comparer));
            list.Insert(0, trimmed);

            if (list.Count > MaxHistoryCount)
            {
                list.RemoveRange(MaxHistoryCount, list.Count - MaxHistoryCount);
            }
        }

        public static string NormalizeHostIp(string? endpointOrIp)
        {
            if (string.IsNullOrWhiteSpace(endpointOrIp)) return "";
            string trimmed = endpointOrIp.Trim();
            if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring("https://".Length);
            else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring("http://".Length);
            trimmed = trimmed.TrimEnd('/');

            int colonIdx = trimmed.LastIndexOf(':');
            if (colonIdx > 0 && int.TryParse(trimmed.Substring(colonIdx + 1), out _))
            {
                trimmed = trimmed.Substring(0, colonIdx);
            }
            return trimmed;
        }

        public static bool IsDeviceTrusted(string deviceType, string? fingerprint, string? ipOrEndpoint, string? deviceName = null)
        {
            var settings = Load();
            string hostIp = NormalizeHostIp(ipOrEndpoint);

            foreach (var d in settings.TrustedDevices)
            {
                if (!string.Equals(d.DeviceType, deviceType, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(fingerprint) &&
                    !string.IsNullOrWhiteSpace(d.Fingerprint) &&
                    string.Equals(d.Fingerprint, fingerprint.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string savedHostIp = NormalizeHostIp(d.IpAddress);
                if (!string.IsNullOrWhiteSpace(hostIp) &&
                    string.Equals(savedHostIp, hostIp, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static void AddTrustedDevice(string deviceType, string name, string ipOrEndpoint, string? fingerprint = null)
        {
            var settings = Load();
            string hostIp = NormalizeHostIp(ipOrEndpoint);
            string cleanFp = fingerprint?.Trim() ?? "";
            string cleanName = string.IsNullOrWhiteSpace(name) ? (string.IsNullOrWhiteSpace(hostIp) ? "Device" : hostIp) : name.Trim();

            settings.TrustedDevices.RemoveAll(d =>
                string.Equals(d.DeviceType, deviceType, StringComparison.OrdinalIgnoreCase) &&
                ((!string.IsNullOrWhiteSpace(cleanFp) && string.Equals(d.Fingerprint, cleanFp, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrWhiteSpace(hostIp) && string.Equals(NormalizeHostIp(d.IpAddress), hostIp, StringComparison.OrdinalIgnoreCase))));

            string idKey = !string.IsNullOrWhiteSpace(cleanFp) ? cleanFp : hostIp;
            settings.TrustedDevices.Insert(0, new TrustedDevice
            {
                Id = $"{deviceType.ToLowerInvariant()}:{idKey}",
                Name = cleanName,
                IpAddress = string.IsNullOrWhiteSpace(ipOrEndpoint) ? hostIp : ipOrEndpoint.Trim(),
                DeviceType = deviceType,
                Fingerprint = cleanFp,
                AuthenticatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });

            Save(settings);
            TrustedDevicesChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void RemoveTrustedDevice(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var settings = Load();
            int removed = settings.TrustedDevices.RemoveAll(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                Save(settings);
                TrustedDevicesChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static void ClearTrustedDevices()
        {
            var settings = Load();
            if (settings.TrustedDevices.Count > 0)
            {
                settings.TrustedDevices.Clear();
                Save(settings);
                TrustedDevicesChanged?.Invoke(null, EventArgs.Empty);
            }
        }
    }
}
