using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SyncDroidWinUI.Helpers;
using SyncDroidWinUI.Models;
using SyncDroidWinUI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace SyncDroidWinUI.Views
{
    public sealed partial class DirectTransferPage : Page
    {
        public LocalizationService Loc => LocalizationService.Instance;

        public ObservableCollection<TransferQueueItem> QueueItems { get; } = new();
        public ObservableCollection<string> AndroidPathHistory { get; } = new();
        public ObservableCollection<string> Logs { get; } = new();

        public string TargetAndroidPath { get; set; } = "/storage/emulated/0/Download/SyncDroid";

        private ConflictPolicy _selectedConflictPolicy = ConflictPolicy.SkipIdenticalSize;
        private bool _isInitializing = true;
        private CancellationTokenSource? _transferCts;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

        public DirectTransferPage()
        {
            this.InitializeComponent();
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            this.DataContext = this;

            var settings = SettingsService.Load();
            TargetAndroidPath = string.IsNullOrWhiteSpace(settings.DirectTransfer_AndroidPath)
                ? "/storage/emulated/0/Download/SyncDroid"
                : settings.DirectTransfer_AndroidPath;
            TargetAndroidComboBox.Text = TargetAndroidPath;

            foreach (var p in settings.AndroidPathHistory)
            {
                AndroidPathHistory.Add(p);
            }
            if (!AndroidPathHistory.Contains(TargetAndroidPath))
            {
                AndroidPathHistory.Insert(0, TargetAndroidPath);
            }

            _selectedConflictPolicy = Enum.IsDefined(typeof(ConflictPolicy), settings.DirectTransfer_ConflictPolicy)
                ? (ConflictPolicy)settings.DirectTransfer_ConflictPolicy
                : ConflictPolicy.SkipIdenticalSize;

            switch (_selectedConflictPolicy)
            {
                case ConflictPolicy.Overwrite:
                    ConflictOverwriteRadio.IsChecked = true;
                    break;
                case ConflictPolicy.KeepBothRename:
                    ConflictKeepBothRadio.IsChecked = true;
                    break;
                default:
                    ConflictSkipRadio.IsChecked = true;
                    break;
            }

            QueueItems.CollectionChanged += (_, _) => UpdateQueueEmptyState();
            Logs.CollectionChanged += Logs_CollectionChanged;

            CompanionSyncService.Instance.ServerActivityLogged += msg =>
            {
                _dispatcherQueue?.TryEnqueue(() => AddLog(msg));
            };

            LocalSendService.Instance.ServerActivityLogged += msg =>
            {
                _dispatcherQueue?.TryEnqueue(() => AddLog(msg));
            };

            CompanionSyncService.Instance.ActiveConnectionModeChanged += _ =>
            {
                _dispatcherQueue?.TryEnqueue(UpdateTargetPathBarState);
            };

            Loc.LanguageChanged += (_, _) =>
            {
                _dispatcherQueue?.TryEnqueue(UpdateTargetPathBarState);
            };

            UpdateQueueEmptyState();
            UpdateTargetPathBarState();
            _isInitializing = false;
        }

        private void UpdateTargetPathBarState()
        {
            bool isLocalSend = CompanionSyncService.Instance.ActiveConnectionMode == ConnectionMode.LocalSend &&
                               LocalSendService.Instance.CurrentPeer != null;

            if (isLocalSend)
            {
                TargetAndroidComboBox.IsEnabled = false;
                BrowseTargetDirButton.IsEnabled = false;
                TargetAndroidComboBox.Text = Loc.LocalSendReceiverControlledDirHint;
            }
            else
            {
                if (!TargetAndroidComboBox.IsEnabled)
                {
                    TargetAndroidComboBox.IsEnabled = true;
                    BrowseTargetDirButton.IsEnabled = true;
                    TargetAndroidComboBox.Text = TargetAndroidPath;
                }
            }
        }

        private void UpdateQueueEmptyState()
        {
            QueueCountText.Text = QueueItems.Count.ToString();
            EmptyQueueHintPanel.Visibility = QueueItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && Logs.Count > 0)
            {
                TransferLogListView.ScrollIntoView(Logs[Logs.Count - 1]);
            }
        }

        private void AddPathsToQueue(IEnumerable<string> paths)
        {
            foreach (var raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string full = Path.GetFullPath(raw.Trim());

                if (QueueItems.Any(i => string.Equals(i.LocalPath, full, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (File.Exists(full))
                {
                    var fi = new FileInfo(full);
                    QueueItems.Add(new TransferQueueItem
                    {
                        LocalPath = full,
                        Name = fi.Name,
                        IsDirectory = false,
                        Size = fi.Length,
                        SizeDisplay = FormatByteSize(fi.Length)
                    });
                }
                else if (Directory.Exists(full))
                {
                    var di = new DirectoryInfo(full);
                    QueueItems.Add(new TransferQueueItem
                    {
                        LocalPath = full,
                        Name = di.Name,
                        IsDirectory = true,
                        Size = 0,
                        SizeDisplay = "DIR"
                    });
                }
            }
        }

        private static string FormatByteSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:F1} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F1} MB";
            double gb = mb / 1024.0;
            return $"{gb:F2} GB";
        }

        private void QueueCard_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
            }
        }

        private async void QueueCard_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p));
                AddPathsToQueue(paths);
            }
            catch (Exception ex)
            {
                AddLog($"Drop error: {ex.Message}");
            }
        }

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = App.MainWindow != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow) : IntPtr.Zero;
                var files = FolderPickerHelper.PickFiles(hwnd);
                if (files.Count > 0)
                {
                    AddPathsToQueue(files);
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error picking files: {ex.Message}");
            }
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = App.MainWindow != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow) : IntPtr.Zero;
                var folder = FolderPickerHelper.PickFolder(hwnd);
                if (!string.IsNullOrEmpty(folder))
                {
                    AddPathsToQueue(new[] { folder });
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error picking folder: {ex.Message}");
            }
        }

        private void RemoveQueueItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path)
            {
                var item = QueueItems.FirstOrDefault(i => string.Equals(i.LocalPath, path, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    QueueItems.Remove(item);
                }
            }
        }

        private void ClearQueue_Click(object sender, RoutedEventArgs e)
        {
            QueueItems.Clear();
        }

        private void ConflictRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            if (ConflictOverwriteRadio.IsChecked == true)
                _selectedConflictPolicy = ConflictPolicy.Overwrite;
            else if (ConflictKeepBothRadio.IsChecked == true)
                _selectedConflictPolicy = ConflictPolicy.KeepBothRename;
            else
                _selectedConflictPolicy = ConflictPolicy.SkipIdenticalSize;

            var settings = SettingsService.Load();
            settings.DirectTransfer_ConflictPolicy = (int)_selectedConflictPolicy;
            SettingsService.Save(settings);
        }

        private bool EnsureDeviceConnected(out ConnectionMode resolvedMode)
        {
            var companion = CompanionSyncService.Instance;
            var localSend = LocalSendService.Instance;
            var adb = AdbSyncService.Instance;

            if (companion.ActiveConnectionMode == ConnectionMode.LocalSend && localSend.CurrentPeer != null)
            {
                resolvedMode = ConnectionMode.LocalSend;
                return true;
            }

            if (companion.ActiveConnectionMode == ConnectionMode.WirelessCompanion && companion.CurrentPeer != null)
            {
                resolvedMode = ConnectionMode.WirelessCompanion;
                return true;
            }

            if (adb.CurrentDevice == null || adb.CurrentDevice.State != SharpAdbClient.DeviceState.Online)
            {
                try { adb.StartServerAndConnect(); } catch { }
            }

            if (adb.CurrentDevice != null && adb.CurrentDevice.State == SharpAdbClient.DeviceState.Online)
            {
                resolvedMode = ConnectionMode.Adb;
                return true;
            }

            if (companion.CurrentPeer != null)
            {
                resolvedMode = ConnectionMode.WirelessCompanion;
                return true;
            }

            if (localSend.CurrentPeer != null)
            {
                resolvedMode = ConnectionMode.LocalSend;
                return true;
            }

            resolvedMode = ConnectionMode.Adb;
            return false;
        }

        private async void BrowseTargetAndroid_Click(object sender, RoutedEventArgs e)
        {
            EnsureDeviceConnected(out var mode);
            if (mode == ConnectionMode.LocalSend && LocalSendService.Instance.CurrentPeer != null)
            {
                return;
            }

            try
            {
                var dialog = new AndroidFolderPickerDialog(TargetAndroidComboBox.Text, allowFileSelection: false)
                {
                    XamlRoot = this.XamlRoot
                };

                var res = await dialog.ShowAsync();
                if (res == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    TargetAndroidPath = dialog.SelectedPath;
                    TargetAndroidComboBox.Text = TargetAndroidPath;
                    SaveTargetAndroidPath(TargetAndroidPath);
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error browsing device folder: {ex.Message}");
            }
        }

        private void SaveTargetAndroidPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string trimmed = path.Trim();
            var settings = SettingsService.Load();
            settings.DirectTransfer_AndroidPath = trimmed;
            SettingsService.AddToHistoryList(settings.AndroidPathHistory, trimmed, ignoreCase: false);
            SettingsService.Save(settings);

            if (!AndroidPathHistory.Contains(trimmed))
            {
                AndroidPathHistory.Insert(0, trimmed);
            }
        }

        private async void StartDirectPush_Click(object sender, RoutedEventArgs e)
        {
            if (_transferCts != null)
            {
                AddLog("A transfer is already in progress...");
                return;
            }

            if (QueueItems.Count == 0)
            {
                TransferStatusText.Text = Loc.StatusEmptyQueue;
                return;
            }

            string targetDeviceDir = TargetAndroidComboBox.Text?.Trim() ?? "";
            bool hasDevice = EnsureDeviceConnected(out var mode);
            bool isWinTarget = CompanionSyncService.IsWindowsDrivePath(targetDeviceDir);

            if (!hasDevice && !isWinTarget)
            {
                TransferStatusText.Text = Loc.NoDeviceConnectedError;
                AddLog(Loc.NoDeviceConnectedError);
                return;
            }

            if (mode != ConnectionMode.LocalSend)
            {
                if (string.IsNullOrWhiteSpace(targetDeviceDir))
                {
                    TransferStatusText.Text = Loc.StatusInvalidPaths;
                    return;
                }
                TargetAndroidPath = targetDeviceDir;
            }

            // Check for > 255 byte filenames in queue items before pushing
            var queuedPaths = QueueItems.Select(q => q.LocalPath).ToList();
            var overlongFiles = await Task.Run(() => FileNameLengthHelper.ScanPathsForOverlongFileNames(queuedPaths));
            if (overlongFiles.Count > 0)
            {
                var dialogResult = await SyncPage.ShowOverlongFilesDialogAsync(this.XamlRoot, overlongFiles);
                if (dialogResult == ContentDialogResult.Primary)
                {
                    var renameResults = await Task.Run(() => FileNameLengthHelper.RenameOverlongFiles("", overlongFiles));
                    foreach (var r in renameResults)
                    {
                        if (r.Success)
                        {
                            AddLog(string.Format(Loc.OverlongFileRenamedLog, r.OldRelativePath, r.NewFileName));
                            var matchedItem = QueueItems.FirstOrDefault(q => !q.IsDirectory && string.Equals(q.LocalPath, r.OldFullPath, StringComparison.OrdinalIgnoreCase));
                            if (matchedItem != null)
                            {
                                int idx = QueueItems.IndexOf(matchedItem);
                                QueueItems[idx] = new TransferQueueItem
                                {
                                    LocalPath = r.NewFullPath,
                                    Name = r.NewFileName,
                                    IsDirectory = false,
                                    Size = matchedItem.Size,
                                    SizeDisplay = matchedItem.SizeDisplay
                                };
                            }
                        }
                        else
                        {
                            AddLog(string.Format(Loc.OverlongFileRenameFailedLog, r.OldRelativePath, r.ErrorMessage));
                        }
                    }
                }
                else if (dialogResult == ContentDialogResult.Secondary)
                {
                    AddLog(string.Format(Loc.OverlongFileSkipSelectedLog, overlongFiles.Count));
                }
                else
                {
                    return;
                }
            }

            if (mode != ConnectionMode.LocalSend)
            {
                SaveTargetAndroidPath(targetDeviceDir);
            }

            _transferCts = new CancellationTokenSource();
            var token = _transferCts.Token;

            SetProgress(0);
            TransferStatusText.Text = Loc.StatusTransferring;

            try
            {
                var snapshot = QueueItems.ToList();
                Action<int> progressCb = p => _dispatcherQueue?.TryEnqueue(() => SetProgress(p));
                Action<string> logCb = msg => _dispatcherQueue?.TryEnqueue(() => AddLog(msg));

                if (mode == ConnectionMode.LocalSend)
                {
                    await LocalSendService.Instance.ExecuteDirectTransferToLocalSendAsync(
                        snapshot, progressCb, logCb, token);
                }
                else if (mode == ConnectionMode.WirelessCompanion)
                {
                    await CompanionSyncService.Instance.ExecuteDirectTransferToAndroidAsync(
                        snapshot, targetDeviceDir, _selectedConflictPolicy, progressCb, logCb, token);
                }
                else if (isWinTarget)
                {
                    await CompanionSyncService.Instance.ExecuteDirectTransferToWindowsPathAsync(
                        snapshot, targetDeviceDir, _selectedConflictPolicy, progressCb, logCb, token);
                }
                else
                {
                    await AdbSyncService.Instance.ExecuteDirectTransferToAndroidAsync(
                        snapshot, targetDeviceDir, _selectedConflictPolicy, progressCb, logCb, token);
                }

                SetProgress(100);
                TransferStatusText.Text = Loc.StatusTransferCompleted;
            }
            catch (OperationCanceledException)
            {
                AddLog("Direct transfer cancelled.");
                TransferStatusText.Text = Loc.StatusCancelled;
            }
            catch (Exception ex)
            {
                AddLog($"Error: {ex.Message}");
                TransferStatusText.Text = Loc.StatusFailed;
            }
            finally
            {
                _transferCts?.Dispose();
                _transferCts = null;
            }
        }

        private async void PullFromAndroid_Click(object sender, RoutedEventArgs e)
        {
            if (_transferCts != null)
            {
                AddLog("A transfer is already in progress...");
                return;
            }

            bool hasDevice = EnsureDeviceConnected(out var mode);

            if (hasDevice && mode == ConnectionMode.LocalSend)
            {
                AddLog(Loc.LocalSendPullNotSupported);
                var infoDialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "LocalSend",
                    Content = Loc.LocalSendPullNotSupported,
                    CloseButtonText = "OK"
                };
                await infoDialog.ShowAsync();
                return;
            }

            try
            {
                var picker = new AndroidFolderPickerDialog(TargetAndroidPath, allowFileSelection: true)
                {
                    XamlRoot = this.XamlRoot
                };

                var res = await picker.ShowAsync();
                if (res != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(picker.SelectedPath))
                {
                    return;
                }

                string remotePath = picker.SelectedPath;
                bool isDir = picker.SelectedIsDirectory;
                bool isWinPath = CompanionSyncService.IsWindowsDrivePath(remotePath);

                if (!hasDevice && !isWinPath)
                {
                    TransferStatusText.Text = Loc.NoDeviceConnectedError;
                    AddLog(Loc.NoDeviceConnectedError);
                    return;
                }

                var hwnd = App.MainWindow != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow) : IntPtr.Zero;
                var pcTargetDir = FolderPickerHelper.PickFolder(hwnd);
                if (string.IsNullOrWhiteSpace(pcTargetDir))
                {
                    return;
                }

                _transferCts = new CancellationTokenSource();
                var token = _transferCts.Token;

                SetProgress(0);
                TransferStatusText.Text = Loc.StatusTransferring;

                Action<int> progressCb = p => _dispatcherQueue?.TryEnqueue(() => SetProgress(p));
                Action<string> logCb = msg => _dispatcherQueue?.TryEnqueue(() => AddLog(msg));

                if (hasDevice && mode == ConnectionMode.WirelessCompanion)
                {
                    await CompanionSyncService.Instance.ExecuteDirectPullFromAndroidAsync(
                        remotePath, isDir, pcTargetDir, _selectedConflictPolicy, progressCb, logCb, token);
                }
                else if (isWinPath)
                {
                    await CompanionSyncService.Instance.ExecuteDirectPullFromWindowsPathAsync(
                        remotePath, isDir, pcTargetDir, _selectedConflictPolicy, progressCb, logCb, token);
                }
                else
                {
                    await AdbSyncService.Instance.ExecuteDirectPullFromAndroidAsync(
                        remotePath, isDir, pcTargetDir, _selectedConflictPolicy, progressCb, logCb, token);
                }

                SetProgress(100);
                TransferStatusText.Text = Loc.StatusTransferCompleted;
            }
            catch (OperationCanceledException)
            {
                AddLog("Pull cancelled.");
                TransferStatusText.Text = Loc.StatusCancelled;
            }
            catch (Exception ex)
            {
                AddLog($"Error pulling from device: {ex.Message}");
                TransferStatusText.Text = Loc.StatusFailed;
            }
            finally
            {
                _transferCts?.Dispose();
                _transferCts = null;
            }
        }

        private void CancelTransfer_Click(object sender, RoutedEventArgs e)
        {
            _transferCts?.Cancel();
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            Logs.Clear();
        }

        private void CopyLogs_Click(object sender, RoutedEventArgs e)
        {
            var allLogs = string.Join(Environment.NewLine, Logs);
            var dataPackage = new DataPackage();
            dataPackage.SetText(allLogs);
            Clipboard.SetContent(dataPackage);
            AddLog("Logs copied to clipboard.");
        }

        private void SetProgress(int value)
        {
            TransferProgressBar.Value = value;
            TransferProgressText.Text = $"{value}%";
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
