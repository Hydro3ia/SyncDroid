using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SyncDroidWinUI.Models;
using SyncDroidWinUI.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace SyncDroidWinUI.Views
{
    public sealed partial class ReceivePage : Page
    {
        public LocalizationService Loc => LocalizationService.Instance;

        public ObservableCollection<ReceivedFileItem> ReceivedFiles { get; } = new();
        public ObservableCollection<string> Logs { get; } = new();

        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
        private TaskCompletionSource<bool>? _activeConfirmTcs;
        private string _currentSaveDir = "";
        private string _activeSessionId = "";

        public event Action? ConfirmationResolvedFromBanner;

        public ReceivePage()
        {
            this.InitializeComponent();
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            this.DataContext = this;

            var settings = SettingsService.Load();
            _currentSaveDir = settings.LocalSendReceiveDirectory;
            SaveDirText.Text = _currentSaveDir;

            ReceivedFiles.CollectionChanged += (_, _) => UpdateEmptyState();
            Logs.CollectionChanged += Logs_CollectionChanged;

            LocalSendService.Instance.IncomingTransferRequested += OnIncomingTransferRequested;
            LocalSendService.Instance.IncomingFileProgressUpdated += OnIncomingFileProgressUpdated;
            LocalSendService.Instance.IncomingFileCompleted += OnIncomingFileCompleted;
            LocalSendService.Instance.ServerActivityLogged += OnServerActivityLogged;

            CompanionSyncService.Instance.IncomingTransferRequested += OnIncomingTransferRequested;
            CompanionSyncService.Instance.IncomingFileProgressUpdated += OnIncomingFileProgressUpdated;
            CompanionSyncService.Instance.IncomingFileCompleted += OnIncomingFileCompleted;
            CompanionSyncService.Instance.ServerActivityLogged += OnServerActivityLogged;

            UpdateEmptyState();
        }

        private void OnServerActivityLogged(string msg)
        {
            _dispatcherQueue?.TryEnqueue(() => AddLog(msg));
        }

        private void OnIncomingTransferRequested(IncomingTransferSessionEventArgs args)
        {
            _dispatcherQueue?.TryEnqueue(() => HandleIncomingSessionOnUi(args));
        }

        public void HandleIncomingSessionOnUi(IncomingTransferSessionEventArgs args)
        {
            bool isSameSession = string.Equals(_activeSessionId, args.SessionId, StringComparison.Ordinal);
            _activeSessionId = args.SessionId;

            string senderDisplay = string.IsNullOrWhiteSpace(args.SenderIp) || args.SenderName.Contains(args.SenderIp, StringComparison.OrdinalIgnoreCase)
                ? args.SenderName
                : $"{args.SenderName} ({args.SenderIp})";
            SenderDeviceText.Text = senderDisplay;

            ProtocolBadgeText.Text = args.Protocol;
            ProtocolBadgeBorder.Visibility = Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(args.SaveDirectory))
            {
                _currentSaveDir = args.SaveDirectory;
                SaveDirText.Text = _currentSaveDir;
            }

            if (!args.AppendToExistingSession || !isSameSession)
            {
                if (!args.AppendToExistingSession)
                {
                    ReceivedFiles.Clear();
                }
            }

            foreach (var f in args.Files)
            {
                if (!ReceivedFiles.Any(existing => string.Equals(existing.Id, f.Id, StringComparison.Ordinal)))
                {
                    ReceivedFiles.Add(f);
                }
            }

            if (ReceivedFiles.Count > 0)
            {
                ReceivedFilesListView.ScrollIntoView(ReceivedFiles[ReceivedFiles.Count - 1]);
            }

            if (args.RequiresConfirmation && args.ConfirmationTcs != null)
            {
                _activeConfirmTcs = args.ConfirmationTcs;
                PendingConfirmText.Text = string.Format(Loc.ReceivePendingConfirmBannerFormat, senderDisplay, args.Files.Count);
                PendingConfirmBanner.Visibility = Visibility.Visible;
                ReceiveStatusText.Text = Loc.ReceiveStatusPending;
                SetProgress(0);
            }
            else
            {
                _activeConfirmTcs = null;
                PendingConfirmBanner.Visibility = Visibility.Collapsed;
                ReceiveStatusText.Text = Loc.ReceiveStatusReceiving;
            }
        }

        public void SyncConfirmationDecision(bool accepted)
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                PendingConfirmBanner.Visibility = Visibility.Collapsed;
                _activeConfirmTcs = null;

                foreach (var item in ReceivedFiles)
                {
                    if (item.StatusText == Loc.ReceiveStatusPending)
                    {
                        item.StatusText = accepted ? Loc.ReceiveStatusWaitingUpload : Loc.ReceiveStatusDeclined;
                    }
                }

                ReceiveStatusText.Text = accepted ? Loc.ReceiveStatusReceiving : Loc.ReceiveStatusDeclined;
            });
        }

        private void OnIncomingFileProgressUpdated(string sessionId, string fileId, double percent, string speed)
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                var item = ReceivedFiles.FirstOrDefault(f => string.Equals(f.Id, fileId, StringComparison.Ordinal));
                if (item != null)
                {
                    item.ProgressPercent = percent;
                    item.StatusText = string.IsNullOrWhiteSpace(speed)
                        ? $"{Loc.ReceiveStatusReceiving} ({percent:F0}%)"
                        : $"{Loc.ReceiveStatusReceiving} ({percent:F0}% • {speed})";
                }

                ReceiveStatusText.Text = Loc.ReceiveStatusReceiving;
                UpdateOverallProgress();
            });
        }

        private void OnIncomingFileCompleted(string sessionId, string fileId, string savedPath, bool skipped, bool failed)
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                var item = ReceivedFiles.FirstOrDefault(f => string.Equals(f.Id, fileId, StringComparison.Ordinal));
                if (item != null)
                {
                    if (!string.IsNullOrWhiteSpace(savedPath))
                    {
                        item.SavedPath = savedPath;
                    }

                    if (failed)
                    {
                        item.StatusText = Loc.ReceiveStatusFailed;
                    }
                    else if (skipped)
                    {
                        item.ProgressPercent = 100;
                        item.StatusText = Loc.ReceiveStatusSkipped;
                    }
                    else
                    {
                        item.ProgressPercent = 100;
                        item.StatusText = Loc.ReceiveStatusCompleted;
                    }
                }

                UpdateOverallProgress();
            });
        }

        private void UpdateOverallProgress()
        {
            if (ReceivedFiles.Count == 0)
            {
                SetProgress(0);
                return;
            }

            double avg = ReceivedFiles.Average(f => f.ProgressPercent);
            int rounded = (int)Math.Round(Math.Clamp(avg, 0, 100));
            SetProgress(rounded);

            bool allFinished = ReceivedFiles.All(f =>
                f.ProgressPercent >= 100 ||
                f.StatusText == Loc.ReceiveStatusCompleted ||
                f.StatusText == Loc.ReceiveStatusSkipped ||
                f.StatusText == Loc.ReceiveStatusFailed ||
                f.StatusText == Loc.ReceiveStatusDeclined);

            if (allFinished && ReceivedFiles.Count > 0)
            {
                bool anyFailed = ReceivedFiles.Any(f => f.StatusText == Loc.ReceiveStatusFailed);
                ReceiveStatusText.Text = anyFailed ? Loc.ReceiveStatusFailed : Loc.StatusTransferCompleted;
                if (!anyFailed) SetProgress(100);
            }
        }

        private void AcceptTransfer_Click(object sender, RoutedEventArgs e)
        {
            var tcs = _activeConfirmTcs;
            SyncConfirmationDecision(accepted: true);
            tcs?.TrySetResult(true);
            ConfirmationResolvedFromBanner?.Invoke();
        }

        private void DeclineTransfer_Click(object sender, RoutedEventArgs e)
        {
            var tcs = _activeConfirmTcs;
            SyncConfirmationDecision(accepted: false);
            tcs?.TrySetResult(false);
            ConfirmationResolvedFromBanner?.Invoke();
        }

        private void OpenReceiveFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = !string.IsNullOrWhiteSpace(_currentSaveDir)
                    ? _currentSaveDir
                    : SettingsService.Load().LocalSendReceiveDirectory;
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                AddLog($"Failed to open folder: {ex.Message}");
            }
        }

        private void ClearReceiveList_Click(object sender, RoutedEventArgs e)
        {
            ReceivedFiles.Clear();
            SetProgress(0);
            ReceiveStatusText.Text = Loc.StatusReady;
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            Logs.Clear();
        }

        private void CopyLogs_Click(object sender, RoutedEventArgs e)
        {
            var allLogs = string.Join(Environment.NewLine, Logs);
            var pkg = new DataPackage();
            pkg.SetText(allLogs);
            Clipboard.SetContent(pkg);
            AddLog("Logs copied to clipboard.");
        }

        private void UpdateEmptyState()
        {
            ReceiveFileCountText.Text = ReceivedFiles.Count.ToString();
            EmptyReceiveHintPanel.Visibility = ReceivedFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && Logs.Count > 0)
            {
                ReceiveLogListView.ScrollIntoView(Logs[Logs.Count - 1]);
            }
        }

        private void SetProgress(int value)
        {
            ReceiveProgressBar.Value = value;
            ReceiveProgressText.Text = $"{value}%";
        }

        private void AddLog(string message)
        {
            Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (Logs.Count > 1000)
            {
                Logs.RemoveAt(0);
            }
        }
    }
}
