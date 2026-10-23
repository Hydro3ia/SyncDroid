namespace SyncDroidWinUI.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SyncDroidWinUI.Models;
using SyncDroidWinUI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

public partial class SyncViewModel : ObservableObject
{
    private static readonly ObservableCollection<string> _sharedPcPathHistory = new();
    private static readonly ObservableCollection<string> _sharedAndroidPathHistory = new();
    private static bool _isHistoryInitialized;

    public LocalizationService Loc => LocalizationService.Instance;

    public ObservableCollection<string> PcPathHistory => _sharedPcPathHistory;
    public ObservableCollection<string> AndroidPathHistory => _sharedAndroidPathHistory;

    [ObservableProperty]
    public partial string PcPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AndroidPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsMirrorMode { get; set; } = true;

    public bool IsIncrementalMode
    {
        get => !IsMirrorMode;
        set
        {
            if (IsMirrorMode == value)
            {
                IsMirrorMode = !value;
            }
        }
    }

    public string CurrentSyncModeBanner => IsMirrorMode ? Loc.MirrorSyncBanner : Loc.IncrementalSyncBanner;
    public string CurrentStartSyncButtonText => IsMirrorMode ? Loc.StartSyncButton : Loc.StartIncrementalSyncButton;

    partial void OnIsMirrorModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIncrementalMode));
        OnPropertyChanged(nameof(CurrentSyncModeBanner));
        OnPropertyChanged(nameof(CurrentStartSyncButtonText));

        var settings = SettingsService.Load();
        settings.IsMirModeDefault = value;
        if (_isPcToAndroid)
            settings.PcToAndroid_IsMirMode = value;
        else
            settings.AndroidToPc_IsMirMode = value;
        SettingsService.Save(settings);
    }

    [ObservableProperty]
    public partial int Progress { get; set; } = 0;

    public string ProgressText => $"{Progress}%";

    partial void OnProgressChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressText));
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    public ObservableCollection<string> Logs { get; } = new();

    public bool SkipOverlongFiles { get; set; }
    public HashSet<string> AcknowledgedOverlongFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _isPcToAndroid;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private CancellationTokenSource? _syncCts;

    public bool IsPcToAndroid => _isPcToAndroid;
    public bool IsAndroidToPc => !_isPcToAndroid;

    public SyncViewModel(bool isPcToAndroid)
    {
        _isPcToAndroid = isPcToAndroid;
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        StatusText = Loc.StatusReady;

        Loc.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CurrentSyncModeBanner));
            OnPropertyChanged(nameof(CurrentStartSyncButtonText));
            if (_syncCts == null && (StatusText == "Ready" || StatusText == "就绪" || StatusText == "就緒"))
            {
                StatusText = Loc.StatusReady;
            }
        };

        CompanionSyncService.Instance.ServerActivityLogged += msg =>
        {
            _dispatcherQueue?.TryEnqueue(() => AddLog(msg));
        };

        LocalSendService.Instance.ServerActivityLogged += msg =>
        {
            _dispatcherQueue?.TryEnqueue(() => AddLog(msg));
        };

        var settings = SettingsService.Load();

        if (!_isHistoryInitialized)
        {
            _isHistoryInitialized = true;
            foreach (var p in settings.PcPathHistory)
                _sharedPcPathHistory.Add(p);
            foreach (var p in settings.AndroidPathHistory)
                _sharedAndroidPathHistory.Add(p);
        }

        if (_isPcToAndroid)
        {
            PcPath = settings.PcToAndroid_PcPath;
            AndroidPath = settings.PcToAndroid_AndroidPath;
            IsMirrorMode = settings.PcToAndroid_IsMirMode;
        }
        else
        {
            PcPath = settings.AndroidToPc_PcPath;
            AndroidPath = settings.AndroidToPc_AndroidPath;
            IsMirrorMode = settings.AndroidToPc_IsMirMode;
        }
    }

    public void AddPcPathToHistory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var trimmed = path.Trim();
        UpdateObservableHistory(_sharedPcPathHistory, trimmed, ignoreCase: true);

        var settings = SettingsService.Load();
        if (_isPcToAndroid)
            settings.PcToAndroid_PcPath = trimmed;
        else
            settings.AndroidToPc_PcPath = trimmed;

        SettingsService.AddToHistoryList(settings.PcPathHistory, trimmed, ignoreCase: true);
        SettingsService.Save(settings);
    }

    public void AddAndroidPathToHistory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var trimmed = path.Trim();
        UpdateObservableHistory(_sharedAndroidPathHistory, trimmed, ignoreCase: false);

        var settings = SettingsService.Load();
        if (_isPcToAndroid)
            settings.PcToAndroid_AndroidPath = trimmed;
        else
            settings.AndroidToPc_AndroidPath = trimmed;

        SettingsService.AddToHistoryList(settings.AndroidPathHistory, trimmed, ignoreCase: false);
        SettingsService.Save(settings);
    }

    private static void UpdateObservableHistory(ObservableCollection<string> collection, string path, bool ignoreCase)
    {
        var comparer = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (collection.Count > 0 && string.Equals(collection[0], path, comparer))
        {
            return;
        }

        for (int i = collection.Count - 1; i >= 0; i--)
        {
            if (string.Equals(collection[i], path, comparer))
            {
                collection.RemoveAt(i);
            }
        }

        collection.Insert(0, path);
        while (collection.Count > 15)
        {
            collection.RemoveAt(collection.Count - 1);
        }
    }

    [RelayCommand]
    private async Task StartSyncAsync()
    {
        if (_syncCts != null)
        {
            AddLog("Sync is already running or cancelling...");
            return;
        }

        Progress = 0;
        StatusText = Loc.StatusSyncing;

        var currentPcPath = PcPath?.Trim() ?? string.Empty;
        var currentAndroidPath = AndroidPath?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(currentPcPath) || string.IsNullOrWhiteSpace(currentAndroidPath))
        {
            AddLog("Error: Paths cannot be empty.");
            StatusText = Loc.StatusInvalidPaths;
            return;
        }

        PcPath = currentPcPath;
        AndroidPath = currentAndroidPath;

        _syncCts = new CancellationTokenSource();
        var token = _syncCts.Token;

        try
        {
            AddPcPathToHistory(currentPcPath);
            AddAndroidPathToHistory(currentAndroidPath);

            var dispatcher = _dispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            Action<string> logCallback = msg =>
            {
                dispatcher?.TryEnqueue(() => AddLog(msg));
            };
            Action<int> progressCallback = p =>
            {
                dispatcher?.TryEnqueue(() => Progress = p);
            };

            var companion = CompanionSyncService.Instance;
            var adb = AdbSyncService.Instance;

            if (companion.ActiveConnectionMode == ConnectionMode.LocalSend && LocalSendService.Instance.CurrentPeer != null)
            {
                AddLog(Loc.LocalSendSyncTabNotSupported);
                StatusText = Loc.StatusFailed;
                return;
            }

            bool useWireless = companion.ActiveConnectionMode == ConnectionMode.WirelessCompanion && companion.CurrentPeer != null;
            if (!useWireless && (adb.CurrentDevice == null || adb.CurrentDevice.State != SharpAdbClient.DeviceState.Online))
            {
                try { adb.StartServerAndConnect(); } catch { }
                if (adb.CurrentDevice == null && companion.CurrentPeer != null)
                {
                    useWireless = true;
                }
            }

            if (useWireless)
            {
                if (_isPcToAndroid)
                {
                    await companion.SyncPcToAndroidAsync(currentPcPath, currentAndroidPath, IsMirrorMode, SkipOverlongFiles, progressCallback, logCallback, token);
                }
                else
                {
                    await companion.SyncAndroidToPcAsync(currentAndroidPath, currentPcPath, IsMirrorMode, progressCallback, logCallback, token);
                }
            }
            else
            {
                if (_isPcToAndroid)
                {
                    await adb.SyncPcToAndroidAsync(currentPcPath, currentAndroidPath, progressCallback, logCallback, token, IsMirrorMode, SkipOverlongFiles);
                }
                else
                {
                    await adb.SyncAndroidToPcAsync(currentAndroidPath, currentPcPath, progressCallback, logCallback, token, IsMirrorMode);
                }
            }

            StatusText = Loc.StatusCompleted;
            Progress = 100;
        }
        catch (OperationCanceledException)
        {
            AddLog("Sync was cancelled.");
            StatusText = Loc.StatusCancelled;
        }
        catch (Exception ex)
        {
            AddLog($"Error: {ex.Message}");
            StatusText = Loc.StatusFailed;
        }
        finally
        {
            _syncCts?.Dispose();
            _syncCts = null;
        }
    }

    [RelayCommand]
    private void CopyLogs()
    {
        var allLogs = string.Join(Environment.NewLine, Logs);
        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(allLogs);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        AddLog("Logs copied to clipboard.");
    }

    [RelayCommand]
    private void ClearLogs()
    {
        Logs.Clear();
    }

    [RelayCommand]
    private void CancelSync()
    {
        _syncCts?.Cancel();
    }

    public void AddLog(string message)
    {
        Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");

        if (Logs.Count > 1000)
        {
            Logs.RemoveAt(0);
        }
    }
}
