using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SyncDroidWinUI.Helpers;
using SyncDroidWinUI.Models;
using SyncDroidWinUI.Services;
using SyncDroidWinUI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SyncDroidWinUI.Views
{
    public sealed partial class SyncPage : Page
    {
        public SyncViewModel? ViewModel { get; set; }

        private bool _isCheckingOverlongFiles;
        private string _lastCheckedPcPath = "";

        public SyncPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is SyncViewModel vm)
            {
                ViewModel = vm;
                this.DataContext = ViewModel;

                if (vm.IsPcToAndroid)
                {
                    Grid.SetRow(PcPathPanel, 0);
                    Grid.SetRow(AndroidPathPanel, 2);
                }
                else
                {
                    Grid.SetRow(AndroidPathPanel, 0);
                    Grid.SetRow(PcPathPanel, 2);
                }

                UpdateRoleLabels();
                LocalizationService.Instance.LanguageChanged += (_, _) => UpdateRoleLabels();

                PcPathComboBox.Text = vm.PcPath ?? string.Empty;
                AndroidPathComboBox.Text = vm.AndroidPath ?? string.Empty;
                _lastCheckedPcPath = vm.PcPath ?? string.Empty;

                vm.Logs.CollectionChanged += Logs_CollectionChanged;
            }
        }

        private void UpdateRoleLabels()
        {
            if (ViewModel == null) return;
            var loc = LocalizationService.Instance;
            if (ViewModel.IsPcToAndroid)
            {
                PcRoleText.Text = loc.SourceBadge;
                AndroidRoleText.Text = loc.TargetBadge;
            }
            else
            {
                AndroidRoleText.Text = loc.SourceBadge;
                PcRoleText.Text = loc.TargetBadge;
            }
        }

        private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && ViewModel != null && ViewModel.Logs.Count > 0)
            {
                var lastItem = ViewModel.Logs[ViewModel.Logs.Count - 1];
                LogListView.ScrollIntoView(lastItem);
            }
        }

        private async void BrowsePcPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = App.MainWindow != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow) : IntPtr.Zero;
                var folderPath = FolderPickerHelper.PickFolder(hwnd);
                if (!string.IsNullOrEmpty(folderPath) && ViewModel != null)
                {
                    ViewModel.PcPath = folderPath;
                    ViewModel.AddPcPathToHistory(folderPath);
                    PcPathComboBox.Text = folderPath;
                    _lastCheckedPcPath = folderPath;

                    if (ViewModel.IsPcToAndroid)
                    {
                        await CheckAndHandleOverlongFilesAsync(folderPath, isBeforeSync: false);
                    }
                }
            }
            catch (Exception ex)
            {
                if (ViewModel != null)
                {
                    ViewModel.StatusText = "Error picking folder: " + ex.Message;
                }
            }
        }

        private async void BrowseAndroidPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var companion = CompanionSyncService.Instance;
                var adb = AdbSyncService.Instance;

                bool hasWireless = companion.ActiveConnectionMode == ConnectionMode.WirelessCompanion && companion.CurrentPeer != null;
                if (!hasWireless)
                {
                    if (adb.CurrentDevice == null || adb.CurrentDevice.State != SharpAdbClient.DeviceState.Online)
                    {
                        try
                        {
                            adb.StartServerAndConnect();
                        }
                        catch { }
                    }
                }

                var dialog = new AndroidFolderPickerDialog(AndroidPathComboBox.Text)
                {
                    XamlRoot = this.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary && ViewModel != null)
                {
                    var selectedPath = dialog.SelectedPath;
                    if (!string.IsNullOrWhiteSpace(selectedPath))
                    {
                        ViewModel.AndroidPath = selectedPath;
                        ViewModel.AddAndroidPathToHistory(selectedPath);
                        AndroidPathComboBox.Text = selectedPath;
                    }
                }
            }
            catch (Exception ex)
            {
                if (ViewModel != null)
                {
                    ViewModel.StatusText = "Error browsing target folder: " + ex.Message;
                }
            }
        }

        private async void PcPathComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViewModel != null && e.AddedItems.Count > 0 && e.AddedItems[0] is string selected && !string.IsNullOrWhiteSpace(selected))
            {
                var trimmed = selected.Trim();
                ViewModel.PcPath = trimmed;
                PcPathComboBox.Text = trimmed;

                if (!string.Equals(_lastCheckedPcPath, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    _lastCheckedPcPath = trimmed;
                    if (ViewModel.IsPcToAndroid)
                    {
                        await CheckAndHandleOverlongFilesAsync(trimmed, isBeforeSync: false);
                    }
                }
            }
        }

        private async void PcPathComboBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null && !string.IsNullOrWhiteSpace(PcPathComboBox.Text))
            {
                var trimmed = PcPathComboBox.Text.Trim();
                ViewModel.PcPath = trimmed;

                if (!string.Equals(_lastCheckedPcPath, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    _lastCheckedPcPath = trimmed;
                    if (ViewModel.IsPcToAndroid)
                    {
                        await CheckAndHandleOverlongFilesAsync(trimmed, isBeforeSync: false);
                    }
                }
            }
        }

        private void AndroidPathComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViewModel != null && e.AddedItems.Count > 0 && e.AddedItems[0] is string selected && !string.IsNullOrWhiteSpace(selected))
            {
                ViewModel.AndroidPath = selected;
                AndroidPathComboBox.Text = selected;
            }
        }

        private void AndroidPathComboBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null && !string.IsNullOrWhiteSpace(AndroidPathComboBox.Text))
            {
                ViewModel.AndroidPath = AndroidPathComboBox.Text.Trim();
            }
        }

        private async void StartSyncButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;

            if (!string.IsNullOrWhiteSpace(PcPathComboBox.Text))
            {
                ViewModel.PcPath = PcPathComboBox.Text.Trim();
            }
            if (!string.IsNullOrWhiteSpace(AndroidPathComboBox.Text))
            {
                ViewModel.AndroidPath = AndroidPathComboBox.Text.Trim();
            }

            if (ViewModel.IsPcToAndroid && !string.IsNullOrWhiteSpace(ViewModel.PcPath))
            {
                bool proceed = await CheckAndHandleOverlongFilesAsync(ViewModel.PcPath, isBeforeSync: true);
                if (!proceed)
                {
                    return;
                }
            }

            if (ViewModel.StartSyncCommand.CanExecute(null))
            {
                ViewModel.StartSyncCommand.Execute(null);
            }
        }

        private async Task<bool> CheckAndHandleOverlongFilesAsync(string pcFolderPath, bool isBeforeSync)
        {
            if (ViewModel == null || !ViewModel.IsPcToAndroid) return true;
            if (string.IsNullOrWhiteSpace(pcFolderPath) || !Directory.Exists(pcFolderPath)) return true;
            if (_isCheckingOverlongFiles) return false;

            _isCheckingOverlongFiles = true;
            try
            {
                var overlongFiles = await Task.Run(() => FileNameLengthHelper.ScanDirectoryForOverlongFileNames(pcFolderPath));
                if (overlongFiles.Count == 0)
                {
                    ViewModel.SkipOverlongFiles = false;
                    ViewModel.AcknowledgedOverlongFiles.Clear();
                    return true;
                }

                // If triggered right before sync and user already chose "Skip Files" for all current overlong files, proceed directly
                if (isBeforeSync &&
                    ViewModel.SkipOverlongFiles &&
                    overlongFiles.All(f => ViewModel.AcknowledgedOverlongFiles.Contains(f.FullPath)))
                {
                    return true;
                }

                var result = await ShowOverlongFilesDialogAsync(this.XamlRoot, overlongFiles);
                var loc = LocalizationService.Instance;

                if (result == ContentDialogResult.Primary)
                {
                    // Option 1: Rename local files strictly < 255 bytes
                    var renameResults = await Task.Run(() => FileNameLengthHelper.RenameOverlongFiles(pcFolderPath, overlongFiles));
                    foreach (var r in renameResults)
                    {
                        if (r.Success)
                        {
                            ViewModel.AddLog(string.Format(loc.OverlongFileRenamedLog, r.OldRelativePath, r.NewFileName));
                        }
                        else
                        {
                            ViewModel.AddLog(string.Format(loc.OverlongFileRenameFailedLog, r.OldRelativePath, r.ErrorMessage));
                        }
                    }

                    ViewModel.SkipOverlongFiles = false;
                    ViewModel.AcknowledgedOverlongFiles.Clear();
                    return true;
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    // Option 2: Skip overlong files during sync
                    ViewModel.SkipOverlongFiles = true;
                    ViewModel.AcknowledgedOverlongFiles.Clear();
                    foreach (var f in overlongFiles)
                    {
                        ViewModel.AcknowledgedOverlongFiles.Add(f.FullPath);
                    }

                    ViewModel.AddLog(string.Format(loc.OverlongFileSkipSelectedLog, overlongFiles.Count));
                    return true;
                }
                else
                {
                    // Cancelled
                    ViewModel.SkipOverlongFiles = false;
                    ViewModel.AcknowledgedOverlongFiles.Clear();
                    return false;
                }
            }
            finally
            {
                _isCheckingOverlongFiles = false;
            }
        }

        public static async Task<ContentDialogResult> ShowOverlongFilesDialogAsync(
            XamlRoot xamlRoot,
            IReadOnlyList<OverlongFileItem> overlongFiles)
        {
            var loc = LocalizationService.Instance;

            var rootPanel = new StackPanel
            {
                Spacing = 12,
                Width = 520
            };

            var descText = new TextBlock
            {
                Text = string.Format(loc.OverlongFileDialogMessage, overlongFiles.Count),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            };
            rootPanel.Children.Add(descText);

            var listBorder = new Border
            {
                MaxHeight = 220,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
                Background = Application.Current.Resources["LayerFillColorAltBrush"] as Brush,
                Padding = new Thickness(6)
            };

            var listView = new ListView
            {
                SelectionMode = ListViewSelectionMode.None,
                ItemsSource = overlongFiles.Select(f => f.DisplayText).ToList()
            };

            listBorder.Child = listView;
            rootPanel.Children.Add(listBorder);

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = loc.OverlongFileDialogTitle,
                Content = rootPanel,
                PrimaryButtonText = loc.OverlongFileRenameButton,
                SecondaryButtonText = loc.OverlongFileSkipButton,
                CloseButtonText = loc.CancelButton,
                DefaultButton = ContentDialogButton.Primary
            };

            return await dialog.ShowAsync();
        }
    }
}
