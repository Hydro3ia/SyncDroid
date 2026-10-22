using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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
        public bool UseCustomAdbPath { get; set; } = false;
        public string CustomAdbDirectory { get; set; } = "";
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
            if (string.IsNullOrWhiteSpace(settings.Language))
            {
                settings.Language = "en-US";
            }
            settings.CustomAdbDirectory ??= "";

            // Ensure previously saved single paths are also present in history
            AddToHistoryList(settings.PcPathHistory, settings.AndroidToPc_PcPath, ignoreCase: true);
            AddToHistoryList(settings.PcPathHistory, settings.PcToAndroid_PcPath, ignoreCase: true);
            AddToHistoryList(settings.AndroidPathHistory, settings.AndroidToPc_AndroidPath, ignoreCase: false);
            AddToHistoryList(settings.AndroidPathHistory, settings.PcToAndroid_AndroidPath, ignoreCase: false);

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
