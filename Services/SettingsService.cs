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
    }

    public static class SettingsService
    {
        private const int MaxHistoryCount = 15;

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
    }
}
