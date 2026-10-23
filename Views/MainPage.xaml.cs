using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SyncDroidWinUI.Helpers;
using SyncDroidWinUI.Models;
using SyncDroidWinUI.Services;
using SyncDroidWinUI.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SyncDroidWinUI.Views
{
    public class ConnectedDeviceItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public ConnectionMode Mode { get; set; }
        public SharpAdbClient.DeviceData? AdbDevice { get; set; }
        public CompanionPeer? CompanionPeer { get; set; }
        public LocalSendPeer? LocalSendPeer { get; set; }

        public override string ToString() => Name;
    }

    public sealed partial class MainPage : Page
    {
        public LocalizationService Loc => LocalizationService.Instance;

        private bool _isRefreshingDevices;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
        private ContentDialog? _activeUnauthorizedDialog;
        private string? _activeUnauthorizedSerial;
        private ContentDialog? _activeIncomingConfirmDialog;

        public MainPage()
        {
            this.InitializeComponent();
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            this.DataContext = this;

            PcToAndroidFrame.Navigate(typeof(SyncPage), new SyncViewModel(isPcToAndroid: true));
            AndroidToPcFrame.Navigate(typeof(SyncPage), new SyncViewModel(isPcToAndroid: false));
            DirectTransferFrame.Navigate(typeof(DirectTransferPage));
            ReceiveFrame.Navigate(typeof(ReceivePage));

            if (ReceiveFrame.Content is ReceivePage receivePage)
            {
                receivePage.ConfirmationResolvedFromBanner += () =>
                {
                    _dispatcherQueue?.TryEnqueue(() =>
                    {
                        try { _activeIncomingConfirmDialog?.Hide(); } catch { }
                    });
                };
            }

            SettingsView.BackRequested += (_, _) => ShowSyncTabs();
            SettingsView.AdbConfigChanged += (_, _) => _ = RefreshDeviceStatusAsync();
            SettingsView.ManageDevicesRequested += (_, _) => ShowManageDevicesPage();
            ManageDevicesView.BackRequested += (_, _) => ShowSettingsPage();

            SettingsService.TrustedDevicesChanged += (_, _) =>
            {
                _dispatcherQueue?.TryEnqueue(() =>
                {
                    if (DeviceComboBox.SelectedItem is ConnectedDeviceItem sel &&
                        sel.Mode == ConnectionMode.WirelessCompanion &&
                        sel.CompanionPeer != null &&
                        !SettingsService.IsDeviceTrusted("SyncDroid", null, sel.CompanionPeer.Endpoint, sel.CompanionPeer.DeviceName))
                    {
                        CompanionSyncService.Instance.CurrentPeer = null;
                        DeviceComboBox.SelectedIndex = -1;
                        DisconnectDeviceButton.IsEnabled = false;
                        DeviceStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
                    }
                });
            };

            LocalSendService.Instance.IncomingTransferRequested += OnIncomingTransferRequested;
            CompanionSyncService.Instance.IncomingTransferRequested += OnIncomingTransferRequested;
            CompanionSyncService.Instance.ConfirmFirstTimeDeviceAsync = ConfirmFirstTimeSyncDroidDeviceOnUiAsync;

            AdbSyncService.Instance.UnauthorizedDeviceDetected += OnUnauthorizedDeviceDetected;
            AdbSyncService.Instance.TrackedDevicesChanged += OnTrackedDevicesChanged;

            _ = RefreshDeviceStatusAsync();
        }

        private void OnIncomingTransferRequested(IncomingTransferSessionEventArgs args)
        {
            _dispatcherQueue?.TryEnqueue(async () =>
            {
                ShowReceivePage();

                if (ReceiveFrame.Content is ReceivePage receivePage)
                {
                    receivePage.HandleIncomingSessionOnUi(args);
                }

                if (args.RequiresConfirmation && args.ConfirmationTcs != null)
                {
                    await ShowLocalSendReceiveConfirmDialogAsync(args);
                }
            });
        }

        private async Task ShowLocalSendReceiveConfirmDialogAsync(IncomingTransferSessionEventArgs args)
        {
            if (args.ConfirmationTcs == null || args.ConfirmationTcs.Task.IsCompleted)
            {
                return;
            }

            for (int i = 0; i < 15 && this.XamlRoot == null; i++)
            {
                await Task.Delay(100);
            }

            if (this.XamlRoot == null || args.ConfirmationTcs.Task.IsCompleted)
            {
                return;
            }

            try
            {
                var previewLines = args.Files
                    .Take(12)
                    .Select(f => $"• {f.FileName} ({f.SizeDisplay})")
                    .ToList();
                if (args.Files.Count > 12)
                {
                    previewLines.Add($"... (+{args.Files.Count - 12} more)");
                }
                string filePreviewText = string.Join("\n", previewLines);

                var dialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = Loc.FirstTimeLocalSendReceiveTitle,
                    Content = new ScrollViewer
                    {
                        MaxHeight = 360,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = new TextBlock
                        {
                            Text = string.Format(
                                Loc.FirstTimeLocalSendReceiveMessage,
                                args.SenderName,
                                args.SenderIp,
                                args.Files.Count,
                                filePreviewText),
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 480
                        }
                    },
                    PrimaryButtonText = Loc.ConfirmAcceptButton,
                    CloseButtonText = Loc.ConfirmRejectButton,
                    DefaultButton = ContentDialogButton.Primary
                };

                _activeIncomingConfirmDialog = dialog;
                var result = await dialog.ShowAsync();
                if (!args.ConfirmationTcs.Task.IsCompleted)
                {
                    bool accepted = result == ContentDialogResult.Primary;
                    (ReceiveFrame.Content as ReceivePage)?.SyncConfirmationDecision(accepted);
                    args.ConfirmationTcs.TrySetResult(accepted);
                }
            }
            catch
            {
                // If another ContentDialog is open, the user can still confirm using the banner on ReceivePage
            }
            finally
            {
                _activeIncomingConfirmDialog = null;
            }
        }

        private Task<bool> ConfirmFirstTimeSyncDroidDeviceOnUiAsync(string deviceName, string ipOrEndpoint)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_dispatcherQueue == null)
            {
                tcs.TrySetResult(true);
                return tcs.Task;
            }

            _dispatcherQueue.TryEnqueue(async () =>
            {
                for (int i = 0; i < 15 && this.XamlRoot == null; i++)
                {
                    await Task.Delay(100);
                }

                if (this.XamlRoot == null)
                {
                    tcs.TrySetResult(true);
                    return;
                }

                try
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = this.XamlRoot,
                        Title = Loc.FirstTimeConnectDialogTitle,
                        Content = new TextBlock
                        {
                            Text = string.Format(Loc.FirstTimeSyncDroidConnectMessage, deviceName, ipOrEndpoint),
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 460
                        },
                        PrimaryButtonText = Loc.ConfirmAcceptButton,
                        CloseButtonText = Loc.ConfirmRejectButton,
                        DefaultButton = ContentDialogButton.Primary
                    };

                    var result = await dialog.ShowAsync();
                    tcs.TrySetResult(result == ContentDialogResult.Primary);
                }
                catch
                {
                    tcs.TrySetResult(false);
                }
            });

            return tcs.Task;
        }

        private void OnUnauthorizedDeviceDetected(string serial)
        {
            _dispatcherQueue?.TryEnqueue(async () =>
            {
                await ShowUnauthorizedDevicePromptAsync(serial);
            });
        }

        private void OnTrackedDevicesChanged()
        {
            _dispatcherQueue?.TryEnqueue(async () =>
            {
                await RefreshDeviceStatusAsync();

                if (_activeUnauthorizedDialog != null && !string.IsNullOrEmpty(_activeUnauthorizedSerial))
                {
                    var allDevs = AdbSyncService.Instance.GetDevices();
                    bool stillUnauthorized = allDevs.Any(d =>
                        string.Equals(d.Serial, _activeUnauthorizedSerial, StringComparison.OrdinalIgnoreCase) &&
                        d.State == SharpAdbClient.DeviceState.Unauthorized);
                    if (!stillUnauthorized)
                    {
                        try { _activeUnauthorizedDialog.Hide(); } catch { }
                    }
                }
            });
        }

        private async Task ShowUnauthorizedDevicePromptAsync(string serial)
        {
            if (_activeUnauthorizedDialog != null)
            {
                return;
            }

            // Wait briefly if page XamlRoot is not yet attached during initial window startup
            for (int i = 0; i < 15 && this.XamlRoot == null; i++)
            {
                await Task.Delay(100);
            }

            if (this.XamlRoot == null || _activeUnauthorizedDialog != null)
            {
                return;
            }

            try
            {
                _activeUnauthorizedSerial = serial;
                var dialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = Loc.AdbUnauthorizedDialogTitle,
                    Content = new TextBlock
                    {
                        Text = string.Format(Loc.AdbUnauthorizedDialogMessage, serial),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 460
                    },
                    CloseButtonText = Loc.AdbUnauthorizedDialogOkButton,
                    DefaultButton = ContentDialogButton.Close
                };

                _activeUnauthorizedDialog = dialog;
                await dialog.ShowAsync();
            }
            catch
            {
                // Ignore if another ContentDialog is currently open
            }
            finally
            {
                _activeUnauthorizedDialog = null;
                _activeUnauthorizedSerial = null;
            }
        }

        private async void ConnectionHelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.XamlRoot == null) return;

            var scrollViewer = new ScrollViewer
            {
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = Loc.ConnectionHelpDialogContent,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    LineHeight = 22,
                    IsTextSelectionEnabled = true,
                    Width = 500
                }
            };

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = Loc.ConnectionHelpDialogTitle,
                Content = scrollViewer,
                CloseButtonText = Loc.AdbUnauthorizedDialogOkButton,
                DefaultButton = ContentDialogButton.Close
            };

            await dialog.ShowAsync();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsView.Visibility == Visibility.Visible || ManageDevicesView.Visibility == Visibility.Visible)
            {
                ShowSyncTabs();
            }
            else
            {
                ShowSettingsPage();
            }
        }

        private void ShowSettingsPage()
        {
            SettingsView.LoadSettingsToUi();
            MainTabView.Visibility = Visibility.Collapsed;
            ManageDevicesView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Visible;
        }

        private void ShowManageDevicesPage()
        {
            ManageDevicesView.LoadDevicesToUi();
            MainTabView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            ManageDevicesView.Visibility = Visibility.Visible;
        }

        private void ShowReceivePage()
        {
            SettingsView.Visibility = Visibility.Collapsed;
            ManageDevicesView.Visibility = Visibility.Collapsed;
            MainTabView.Visibility = Visibility.Visible;
            MainTabView.SelectedItem = ReceiveTabItem;
        }

        private void ShowSyncTabs()
        {
            SettingsView.Visibility = Visibility.Collapsed;
            ManageDevicesView.Visibility = Visibility.Collapsed;
            MainTabView.Visibility = Visibility.Visible;
            _ = RefreshDeviceStatusAsync(allowAutoSelectUntrusted: false);
        }

        private async void RefreshAdb_Click(object sender, RoutedEventArgs e)
        {
            AdbSyncService.Instance.ResetUnauthorizedPromptState();
            await RefreshDeviceStatusAsync();
        }

        private async Task RefreshDeviceStatusAsync(string? preferredSelectId = null, bool allowAutoSelectUntrusted = true)
        {
            if (_isRefreshingDevices) return;
            _isRefreshingDevices = true;

            try
            {
                string? previousSelectedId = preferredSelectId ?? (DeviceComboBox.SelectedItem as ConnectedDeviceItem)?.Id;
                var items = new List<ConnectedDeviceItem>();

                // 1. Check ADB devices
                var adbService = AdbSyncService.Instance;
                try
                {
                    await Task.Run(() => adbService.StartServerAndConnect());
                }
                catch { }

                var adbDevices = adbService.GetDevices()
                    .Where(d => d.State == SharpAdbClient.DeviceState.Online)
                    .ToList();

                foreach (var d in adbDevices)
                {
                    items.Add(new ConnectedDeviceItem
                    {
                        Id = $"adb:{d.Serial}",
                        Name = $"[ADB] {d.Model} ({d.Serial})",
                        Mode = ConnectionMode.Adb,
                        AdbDevice = d
                    });
                }

                // 2. Scan LAN Wireless Companion & LocalSend peers in parallel
                var companionTask = CompanionSyncService.Instance.ScanLanForPeersAsync();
                var localSendTask = LocalSendService.Instance.ScanLanForPeersAsync();
                await Task.WhenAll(companionTask, localSendTask);

                foreach (var peer in companionTask.Result)
                {
                    items.Add(new ConnectedDeviceItem
                    {
                        Id = $"lan:{peer.Endpoint}",
                        Name = peer.DisplayName,
                        Endpoint = peer.Endpoint,
                        Mode = ConnectionMode.WirelessCompanion,
                        CompanionPeer = peer
                    });
                }

                foreach (var lsPeer in localSendTask.Result)
                {
                    items.Add(new ConnectedDeviceItem
                    {
                        Id = $"localsend:{lsPeer.Endpoint}",
                        Name = lsPeer.DisplayName,
                        Endpoint = lsPeer.Endpoint,
                        Mode = ConnectionMode.LocalSend,
                        LocalSendPeer = lsPeer
                    });
                }

                if (items.Count > 0)
                {
                    DeviceComboBox.ItemsSource = items;

                    int targetIndex = -1;
                    if (!string.IsNullOrEmpty(previousSelectedId))
                    {
                        int foundIdx = items.FindIndex(i => string.Equals(i.Id, previousSelectedId, StringComparison.OrdinalIgnoreCase));
                        if (foundIdx >= 0) targetIndex = foundIdx;
                    }

                    if (targetIndex < 0)
                    {
                        if (allowAutoSelectUntrusted)
                        {
                            targetIndex = 0;
                        }
                        else
                        {
                            targetIndex = items.FindIndex(i =>
                                i.Mode != ConnectionMode.WirelessCompanion ||
                                (i.CompanionPeer != null && SettingsService.IsDeviceTrusted("SyncDroid", null, i.CompanionPeer.Endpoint, i.CompanionPeer.DeviceName)));
                        }
                    }

                    DeviceComboBox.SelectedIndex = targetIndex;
                    if (targetIndex >= 0)
                    {
                        DisconnectDeviceButton.IsEnabled = true;
                        DeviceStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
                    }
                    else
                    {
                        DisconnectDeviceButton.IsEnabled = false;
                        DeviceStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
                    }
                }
                else
                {
                    DeviceComboBox.ItemsSource = null;
                    AdbSyncService.Instance.CurrentDevice = null;
                    CompanionSyncService.Instance.CurrentPeer = null;
                    LocalSendService.Instance.CurrentPeer = null;
                    DisconnectDeviceButton.IsEnabled = false;
                    DeviceStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
                }
            }
            catch
            {
                DeviceComboBox.ItemsSource = null;
                AdbSyncService.Instance.CurrentDevice = null;
                CompanionSyncService.Instance.CurrentPeer = null;
                LocalSendService.Instance.CurrentPeer = null;
                DisconnectDeviceButton.IsEnabled = false;
                DeviceStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            }
            finally
            {
                _isRefreshingDevices = false;
            }
        }

        private void DisconnectDevice_Click(object sender, RoutedEventArgs e)
        {
            if (DeviceComboBox.SelectedItem is ConnectedDeviceItem selected)
            {
                if (selected.Mode == ConnectionMode.WirelessCompanion)
                {
                    CompanionSyncService.Instance.DisconnectCurrentPeer();
                }
                else if (selected.Mode == ConnectionMode.LocalSend)
                {
                    LocalSendService.Instance.DisconnectCurrentPeer();
                }
                else if (selected.Mode == ConnectionMode.Adb)
                {
                    AdbSyncService.Instance.DisconnectCurrentDevice();
                }

                if (DeviceComboBox.ItemsSource is List<ConnectedDeviceItem> currentList)
                {
                    var remaining = currentList.Where(i => !string.Equals(i.Id, selected.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (remaining.Count > 0)
                    {
                        DeviceComboBox.ItemsSource = remaining;
                        DeviceComboBox.SelectedIndex = -1;
                    }
                    else
                    {
                        DeviceComboBox.ItemsSource = null;
                    }
                }
                else
                {
                    DeviceComboBox.SelectedIndex = -1;
                }
            }

            AdbSyncService.Instance.CurrentDevice = null;
            CompanionSyncService.Instance.CurrentPeer = null;
            LocalSendService.Instance.CurrentPeer = null;
            DisconnectDeviceButton.IsEnabled = false;
            DeviceStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
        }

        private async void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DeviceComboBox.SelectedItem is ConnectedDeviceItem selected)
            {
                DisconnectDeviceButton.IsEnabled = true;
                if (selected.Mode == ConnectionMode.WirelessCompanion && selected.CompanionPeer != null)
                {
                    bool confirmed = await CompanionSyncService.Instance.EnsurePeerAuthenticatedAsync(
                        selected.CompanionPeer.DeviceName,
                        selected.CompanionPeer.Endpoint,
                        notifyRemoteOnSuccess: true);
                    if (!confirmed)
                    {
                        CompanionSyncService.Instance.CurrentPeer = null;
                        DeviceComboBox.SelectedIndex = -1;
                        DisconnectDeviceButton.IsEnabled = false;
                        DeviceStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
                        return;
                    }

                    CompanionSyncService.Instance.CurrentPeer = selected.CompanionPeer;
                    CompanionSyncService.Instance.ActiveConnectionMode = ConnectionMode.WirelessCompanion;
                    DeviceStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
                }
                else if (selected.Mode == ConnectionMode.LocalSend && selected.LocalSendPeer != null)
                {
                    LocalSendService.Instance.CurrentPeer = selected.LocalSendPeer;
                    CompanionSyncService.Instance.ActiveConnectionMode = ConnectionMode.LocalSend;
                    DeviceStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
                }
                else if (selected.Mode == ConnectionMode.Adb && selected.AdbDevice != null)
                {
                    AdbSyncService.Instance.CurrentDevice = selected.AdbDevice;
                    CompanionSyncService.Instance.ActiveConnectionMode = ConnectionMode.Adb;
                    DeviceStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
                }
            }
            else
            {
                DisconnectDeviceButton.IsEnabled = false;
            }
        }

        private async void WirelessConnect_Click(object sender, RoutedEventArgs e)
        {
            var settings = SettingsService.Load();

            var rootPanel = new StackPanel
            {
                Spacing = 12,
                Width = 500
            };

            rootPanel.Children.Add(new TextBlock
            {
                Text = Loc.WirelessConnectDialogDesc,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
            });

            rootPanel.Children.Add(new TextBlock
            {
                Text = Loc.WirelessIpEndpointLabel,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            var inputGrid = new Grid { ColumnSpacing = 8 };
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string initialEndpoint = !string.IsNullOrWhiteSpace(settings.LastCompanionIpEndpoint)
                ? settings.LastCompanionIpEndpoint
                : settings.LastLocalSendIpEndpoint;

            var endpointCombo = new ComboBox
            {
                IsEditable = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = Loc.WirelessIpEndpointPlaceholder,
                ItemsSource = settings.CompanionEndpointHistory,
                Text = initialEndpoint ?? ""
            };
            Grid.SetColumn(endpointCombo, 0);
            inputGrid.Children.Add(endpointCombo);

            var scanBtn = new Button
            {
                Content = Loc.ScanLanButton,
                Padding = new Thickness(12, 6, 12, 6)
            };
            Grid.SetColumn(scanBtn, 1);
            inputGrid.Children.Add(scanBtn);
            rootPanel.Children.Add(inputGrid);

            rootPanel.Children.Add(new TextBlock
            {
                Text = Loc.DiscoveredPeersLabel,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            var peerListBorder = new Border
            {
                Height = 150,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
                Background = Application.Current.Resources["LayerFillColorAltBrush"] as Brush,
                Padding = new Thickness(4)
            };

            var peerListView = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single
            };

            var statusText = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
            };

            peerListView.SelectionChanged += (_, _) =>
            {
                if (peerListView.SelectedItem is ConnectedDeviceItem item && !string.IsNullOrEmpty(item.Endpoint))
                {
                    endpointCombo.Text = item.Endpoint;
                }
            };

            peerListBorder.Child = peerListView;
            rootPanel.Children.Add(peerListBorder);
            rootPanel.Children.Add(statusText);

            async Task RunScanAsync()
            {
                scanBtn.IsEnabled = false;
                statusText.Text = "Scanning LAN (Companion UDP 45820 & LocalSend 224.0.0.167:53317)...";
                try
                {
                    var cTask = CompanionSyncService.Instance.ScanLanForPeersAsync();
                    var lTask = LocalSendService.Instance.ScanLanForPeersAsync();
                    await Task.WhenAll(cTask, lTask);

                    var discovered = new List<ConnectedDeviceItem>();
                    foreach (var cp in cTask.Result)
                    {
                        discovered.Add(new ConnectedDeviceItem
                        {
                            Id = $"lan:{cp.Endpoint}",
                            Name = cp.DisplayName,
                            Endpoint = cp.Endpoint,
                            Mode = ConnectionMode.WirelessCompanion,
                            CompanionPeer = cp
                        });
                    }
                    foreach (var lp in lTask.Result)
                    {
                        discovered.Add(new ConnectedDeviceItem
                        {
                            Id = $"localsend:{lp.Endpoint}",
                            Name = lp.DisplayName,
                            Endpoint = lp.Endpoint,
                            Mode = ConnectionMode.LocalSend,
                            LocalSendPeer = lp
                        });
                    }

                    peerListView.ItemsSource = discovered;
                    if (discovered.Count > 0)
                    {
                        statusText.Text = $"Found {discovered.Count} online device(s).";
                        if (string.IsNullOrWhiteSpace(endpointCombo.Text))
                        {
                            endpointCombo.Text = discovered[0].Endpoint;
                        }
                    }
                    else
                    {
                        statusText.Text = Loc.NoPeersFoundText;
                    }
                }
                catch (Exception ex)
                {
                    statusText.Text = ex.Message;
                }
                finally
                {
                    scanBtn.IsEnabled = true;
                }
            }

            scanBtn.Click += async (_, _) => await RunScanAsync();

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = Loc.WirelessConnectDialogTitle,
                Content = rootPanel,
                PrimaryButtonText = Loc.ConnectButton,
                CloseButtonText = Loc.CancelButton,
                DefaultButton = ContentDialogButton.Primary
            };

            dialog.Opened += async (_, _) => await RunScanAsync();

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                string rawEndpoint = endpointCombo.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(rawEndpoint)) return;
                if (FileHashHelper.IsLoopbackOrDisallowedEndpoint(rawEndpoint)) return;

                bool preferLocalSend = rawEndpoint.EndsWith(":53317", StringComparison.OrdinalIgnoreCase);

                CompanionPeer? companionPeer = null;
                LocalSendPeer? localSendPeer = null;

                if (preferLocalSend)
                {
                    localSendPeer = await LocalSendService.Instance.ProbePeerEndpointAsync(rawEndpoint);
                    if (localSendPeer == null)
                    {
                        companionPeer = await CompanionSyncService.Instance.ProbePeerEndpointAsync(rawEndpoint);
                    }
                }
                else
                {
                    companionPeer = await CompanionSyncService.Instance.ProbePeerEndpointAsync(rawEndpoint);
                    if (companionPeer == null)
                    {
                        localSendPeer = await LocalSendService.Instance.ProbePeerEndpointAsync(rawEndpoint);
                    }
                }

                if (companionPeer != null)
                {
                    bool confirmed = await CompanionSyncService.Instance.EnsurePeerAuthenticatedAsync(
                        companionPeer.DeviceName,
                        companionPeer.Endpoint,
                        notifyRemoteOnSuccess: true);
                    if (!confirmed)
                    {
                        return;
                    }

                    var updatedSettings = SettingsService.Load();
                    updatedSettings.LastCompanionIpEndpoint = companionPeer.Endpoint;
                    SettingsService.AddToHistoryList(updatedSettings.CompanionEndpointHistory, companionPeer.Endpoint, ignoreCase: true);
                    SettingsService.Save(updatedSettings);

                    CompanionSyncService.Instance.CurrentPeer = companionPeer;
                    CompanionSyncService.Instance.ActiveConnectionMode = ConnectionMode.WirelessCompanion;

                    await RefreshDeviceStatusAsync(preferredSelectId: $"lan:{companionPeer.Endpoint}");
                }
                else if (localSendPeer != null)
                {
                    var updatedSettings = SettingsService.Load();
                    updatedSettings.LastLocalSendIpEndpoint = localSendPeer.Endpoint;
                    SettingsService.AddToHistoryList(updatedSettings.CompanionEndpointHistory, localSendPeer.Endpoint, ignoreCase: true);
                    SettingsService.Save(updatedSettings);

                    LocalSendService.Instance.CurrentPeer = localSendPeer;
                    CompanionSyncService.Instance.ActiveConnectionMode = ConnectionMode.LocalSend;

                    await RefreshDeviceStatusAsync(preferredSelectId: $"localsend:{localSendPeer.Endpoint}");
                }
                else
                {
                    var errDialog = new ContentDialog
                    {
                        XamlRoot = this.XamlRoot,
                        Title = Loc.WirelessConnectDialogTitle,
                        Content = $"Failed to connect to '{rawEndpoint}'. Please verify the IP:Port (45821 for SyncDroid Companion, 53317 for LocalSend) and ensure both devices are on the same network.",
                        CloseButtonText = "OK"
                    };
                    await errDialog.ShowAsync();
                }
            }
        }
    }
}
