namespace SyncDroidWinUI.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public partial class SyncViewModel : ObservableObject
{
    private static readonly ObservableCollection<string> _sharedPcPathHistory = new();
    private static readonly ObservableCollection<string> _sharedAndroidPathHistory = new();
    private static bool _isHistoryInitialized;

    public Services.LocalizationService Loc => Services.LocalizationService.Instance;

    public ObservableCollection<string> PcPathHistory => _sharedPcPathHistory;
    public ObservableCollection<string> AndroidPathHistory => _sharedAndroidPathHistory;

    [ObservableProperty]
    public partial string PcPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AndroidPath { get; set; } = string.Empty;

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

    private readonly bool _isPcToAndroid;
    private CancellationTokenSource? _syncCts;

    public bool IsPcToAndroid => _isPcToAndroid;
    public bool IsAndroidToPc => !_isPcToAndroid;

    public SyncViewModel(bool isPcToAndroid)
    {
        _isPcToAndroid = isPcToAndroid;
        StatusText = Loc.StatusReady;
        Loc.LanguageChanged += (_, _) =>
        {
            if (_syncCts == null && (StatusText == "Ready" || StatusText == "就绪" || StatusText == "就緒"))
            {
                StatusText = Loc.StatusReady;
            }
        };

        var settings = Services.SettingsService.Load();

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
        }
        else
        {
            PcPath = settings.AndroidToPc_PcPath;
            AndroidPath = settings.AndroidToPc_AndroidPath;
        }
    }

    public void AddPcPathToHistory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var trimmed = path.Trim();
        UpdateObservableHistory(_sharedPcPathHistory, trimmed, ignoreCase: true);

        var settings = Services.SettingsService.Load();
        if (_isPcToAndroid)
            settings.PcToAndroid_PcPath = trimmed;
        else
            settings.AndroidToPc_PcPath = trimmed;

        Services.SettingsService.AddToHistoryList(settings.PcPathHistory, trimmed, ignoreCase: true);
        Services.SettingsService.Save(settings);
    }

    public void AddAndroidPathToHistory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var trimmed = path.Trim();
        UpdateObservableHistory(_sharedAndroidPathHistory, trimmed, ignoreCase: false);

        var settings = Services.SettingsService.Load();
        if (_isPcToAndroid)
            settings.PcToAndroid_AndroidPath = trimmed;
        else
            settings.AndroidToPc_AndroidPath = trimmed;

        Services.SettingsService.AddToHistoryList(settings.AndroidPathHistory, trimmed, ignoreCase: false);
        Services.SettingsService.Save(settings);
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
            Log("Sync is already running or cancelling...");
            return;
        }

        Logs.Clear();
        Progress = 0;
        StatusText = Loc.StatusSyncing;

        var currentPcPath = PcPath?.Trim() ?? string.Empty;
        var currentAndroidPath = AndroidPath?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(currentPcPath) || string.IsNullOrWhiteSpace(currentAndroidPath))
        {
            Log("Error: Paths cannot be empty.");
            StatusText = Loc.StatusInvalidPaths;
            return;
        }

        PcPath = currentPcPath;
        AndroidPath = currentAndroidPath;

        _syncCts = new CancellationTokenSource();
        var token = _syncCts.Token;

        try
        {
            // Save settings and update shared history
            AddPcPathToHistory(currentPcPath);
            AddAndroidPathToHistory(currentAndroidPath);

            var syncService = Services.AdbSyncService.Instance;

            Action<int> progressCallback = p =>
            {
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() => Progress = p);
            };

            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            Action<string> logCallback = msg =>
            {
                dispatcher?.TryEnqueue(() => Log(msg));
            };
            progressCallback = p =>
            {
                dispatcher?.TryEnqueue(() => Progress = p);
            };

            if (_isPcToAndroid)
            {
                await syncService.SyncPcToAndroidAsync(currentPcPath, currentAndroidPath, progressCallback, logCallback, token);
            }
            else
            {
                await syncService.SyncAndroidToPcAsync(currentAndroidPath, currentPcPath, progressCallback, logCallback, token);
            }
            StatusText = Loc.StatusCompleted;
            Progress = 100;
        }
        catch (OperationCanceledException)
        {
            Log("Sync was cancelled.");
            StatusText = Loc.StatusCancelled;
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
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
        Log("Logs copied to clipboard.");
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

    private void Log(string message)
    {
        Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");

        if (Logs.Count > 1000)
        {
            Logs.RemoveAt(0);
        }
    }
}
