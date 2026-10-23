using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SyncDroidWinUI.Models;
using SyncDroidWinUI.Services;
using System;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;

namespace SyncDroidWinUI.Views
{
    public sealed partial class SettingsPage : Page
    {
        public LocalizationService Loc => LocalizationService.Instance;

        public event EventHandler? BackRequested;
        public event EventHandler? AdbConfigChanged;
        public event EventHandler? ManageDevicesRequested;

        private bool _isInitializing = true;

        public SettingsPage()
        {
            this.InitializeComponent();
            this.DataContext = this;
            LoadSettingsToUi();
        }

        public void LoadSettingsToUi()
        {
            _isInitializing = true;
            var settings = SettingsService.Load();

            // Select matching language item
            var lang = string.IsNullOrWhiteSpace(settings.Language) ? "en-US" : settings.Language;
            for (int i = 0; i < LanguageComboBox.Items.Count; i++)
            {
                if (LanguageComboBox.Items[i] is ComboBoxItem item && (item.Tag as string) == lang)
                {
                    LanguageComboBox.SelectedIndex = i;
                    break;
                }
            }
            if (LanguageComboBox.SelectedIndex < 0)
            {
                LanguageComboBox.SelectedIndex = 0;
            }

            // Wireless Companion Service
            CompanionServerToggle.IsOn = settings.CompanionServerEnabled;
            CompanionPortTextBox.Text = settings.CompanionPort.ToString();
            UpdateCompanionServerPreview();

            // LocalSend Service
            LocalSendServerToggle.IsOn = settings.LocalSendEnabled;
            LocalSendReceiveDirTextBox.Text = settings.LocalSendReceiveDirectory ?? "";
            UpdateLocalSendServerPreview();

            // ADB mode
            CustomAdbDirTextBox.Text = settings.CustomAdbDirectory ?? "";
            var adbMode = Enum.IsDefined(typeof(AdbSourceMode), settings.AdbSourceMode)
                ? (AdbSourceMode)settings.AdbSourceMode
                : (settings.UseCustomAdbPath ? AdbSourceMode.CustomDirectory : AdbSourceMode.BuiltInOrRunning);

            switch (adbMode)
            {
                case AdbSourceMode.CustomDirectory:
                    UseCustomAdbRadio.IsChecked = true;
                    break;
                case AdbSourceMode.SystemPath:
                    UsePathAdbRadio.IsChecked = true;
                    break;
                default:
                    UseBuiltInAdbRadio.IsChecked = true;
                    break;
            }

            KillBuiltInAdbOnExitToggle.IsOn = settings.KillBuiltInAdbOnExit;
            VerifyHashToggle.IsOn = settings.VerifyHashAfterTransfer;

            UpdateCustomAdbControlsEnabled(adbMode == AdbSourceMode.CustomDirectory);
            UpdateAdbResolvedPreview(settings);

            _isInitializing = false;
        }

        private void UpdateCompanionServerPreview()
        {
            var companion = CompanionSyncService.Instance;
            int port = companion.CurrentListeningPort;
            var ips = CompanionSyncService.GetLocalLanIpAddresses();
            LocalIpEndpointText.Text = string.Join("  |  ", ips.Select(ip => $"{ip}:{port}"));

            if (companion.IsServerRunning)
            {
                CompanionStatusText.Text = $"✔ Listening on TCP {port} & UDP {CompanionSyncService.UdpDiscoveryPort}";
                CompanionStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
            }
            else if (!string.IsNullOrEmpty(companion.LastServerError))
            {
                CompanionStatusText.Text = $"✖ {companion.LastServerError}";
                CompanionStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            }
            else
            {
                CompanionStatusText.Text = "○ Listener Stopped";
                CompanionStatusText.Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush;
            }
        }

        private void UpdateLocalSendServerPreview()
        {
            var ls = LocalSendService.Instance;
            if (ls.IsServerRunning)
            {
                LocalSendStatusText.Text = $"✔ Active on UDP/TCP {ls.CurrentListeningPort}";
                LocalSendStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
            }
            else if (ls.IsAccessForbiddenError)
            {
                LocalSendStatusText.Text = $"✖ {Loc.LocalSendAccessForbiddenPrompt}";
                LocalSendStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            }
            else if (!string.IsNullOrEmpty(ls.LastServerError))
            {
                LocalSendStatusText.Text = $"✖ {ls.LastServerError}";
                LocalSendStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            }
            else
            {
                LocalSendStatusText.Text = "○ Listener Stopped";
                LocalSendStatusText.Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush;
            }
        }

        private void CompanionServerToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            var settings = SettingsService.Load();
            settings.CompanionServerEnabled = CompanionServerToggle.IsOn;
            if (int.TryParse(CompanionPortTextBox.Text?.Trim(), out int port) && port > 0 && port <= 65535)
            {
                settings.CompanionPort = port;
            }
            SettingsService.Save(settings);

            CompanionSyncService.Instance.StartOrRestartServerFromSettings();
            UpdateCompanionServerPreview();
        }

        private void ApplyCompanionServer_Click(object sender, RoutedEventArgs e)
        {
            var settings = SettingsService.Load();
            settings.CompanionServerEnabled = CompanionServerToggle.IsOn;
            if (int.TryParse(CompanionPortTextBox.Text?.Trim(), out int port) && port >= 1024 && port <= 65535)
            {
                settings.CompanionPort = port;
            }
            else
            {
                settings.CompanionPort = CompanionSyncService.DefaultTcpPort;
                CompanionPortTextBox.Text = settings.CompanionPort.ToString();
            }
            SettingsService.Save(settings);

            CompanionSyncService.Instance.StartOrRestartServerFromSettings();
            UpdateCompanionServerPreview();
        }

        private void CopyLocalEndpoint_Click(object sender, RoutedEventArgs e)
        {
            var ips = CompanionSyncService.GetLocalLanIpAddresses();
            if (ips.Count == 0)
            {
                CompanionStatusText.Text = "⚠ No active LAN network interface found";
                CompanionStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
                return;
            }

            int port = CompanionSyncService.Instance.CurrentListeningPort;
            string primary = $"{ips[0]}:{port}";

            var pkg = new DataPackage();
            pkg.SetText(primary);
            Clipboard.SetContent(pkg);

            CompanionStatusText.Text = $"✔ Copied: {primary}";
            CompanionStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
        }

        private async void LocalSendServerToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            var settings = SettingsService.Load();
            settings.LocalSendEnabled = LocalSendServerToggle.IsOn;
            SettingsService.Save(settings);

            LocalSendService.Instance.StartOrRestartServerFromSettings();
            UpdateLocalSendServerPreview();

            if (LocalSendServerToggle.IsOn && LocalSendService.Instance.IsAccessForbiddenError && this.XamlRoot != null)
            {
                try
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = this.XamlRoot,
                        Title = Loc.LocalSendAccessForbiddenDialogTitle,
                        Content = Loc.LocalSendAccessForbiddenDialogMessage,
                        CloseButtonText = Loc.AdbUnauthorizedDialogOkButton,
                        DefaultButton = ContentDialogButton.Close
                    };
                    await dialog.ShowAsync();
                }
                catch { }
            }
        }

        private void LocalSendReceiveDirTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializing) return;

            var settings = SettingsService.Load();
            settings.LocalSendReceiveDirectory = LocalSendReceiveDirTextBox.Text?.Trim() ?? "";
            SettingsService.Save(settings);
        }

        private void BrowseLocalSendReceiveDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = App.MainWindow != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow) : IntPtr.Zero;
                var folderPath = Helpers.FolderPickerHelper.PickFolder(hwnd);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    LocalSendReceiveDirTextBox.Text = folderPath;
                }
            }
            catch (Exception ex)
            {
                LocalSendStatusText.Text = $"✖ {ex.Message}";
                LocalSendStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            }
        }

        private void UpdateCustomAdbControlsEnabled(bool isCustom)
        {
            if (CustomAdbDirTextBox != null)
                CustomAdbDirTextBox.IsEnabled = isCustom;
            if (BrowseAdbDirButton != null)
                BrowseAdbDirButton.IsEnabled = isCustom;
            if (CustomAdbPanel != null)
                CustomAdbPanel.Opacity = isCustom ? 1.0 : 0.55;
        }

        private void UpdateAdbResolvedPreview(AppSettings settings)
        {
            if (AdbStatusText == null) return;

            var mode = Enum.IsDefined(typeof(AdbSourceMode), settings.AdbSourceMode)
                ? (AdbSourceMode)settings.AdbSourceMode
                : AdbSourceMode.BuiltInOrRunning;

            var resolved = AdbSyncService.ResolveAdbExecutablePath(settings);
            if (!string.IsNullOrEmpty(resolved))
            {
                string modeLabel = mode switch
                {
                    AdbSourceMode.BuiltInOrRunning => "Built-in / Running",
                    AdbSourceMode.SystemPath => "System PATH",
                    AdbSourceMode.CustomDirectory => "Custom",
                    _ => "ADB"
                };
                AdbStatusText.Text = $"✔ [{modeLabel}] adb.exe: {resolved}";
                AdbStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
            }
            else
            {
                AdbStatusText.Text = mode == AdbSourceMode.CustomDirectory
                    ? "✖ adb.exe not found in specified directory"
                    : "⚠ adb.exe not found (Wireless Companion & LocalSend still work without ADB)";
                AdbStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            }
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (LanguageComboBox.SelectedItem is ComboBoxItem item && item.Tag is string langCode)
            {
                LocalizationService.Instance.CurrentLanguage = langCode;
                UpdateLocalSendServerPreview();
            }
        }

        private void AdbModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            AdbSourceMode selectedMode;
            if (UseCustomAdbRadio.IsChecked == true)
                selectedMode = AdbSourceMode.CustomDirectory;
            else if (UsePathAdbRadio.IsChecked == true)
                selectedMode = AdbSourceMode.SystemPath;
            else
                selectedMode = AdbSourceMode.BuiltInOrRunning;

            bool isCustom = selectedMode == AdbSourceMode.CustomDirectory;
            UpdateCustomAdbControlsEnabled(isCustom);

            var settings = SettingsService.Load();
            settings.AdbSourceMode = (int)selectedMode;
            settings.UseCustomAdbPath = isCustom;
            settings.CustomAdbDirectory = CustomAdbDirTextBox.Text?.Trim() ?? "";
            SettingsService.Save(settings);

            UpdateAdbResolvedPreview(settings);
            AdbConfigChanged?.Invoke(this, EventArgs.Empty);
        }

        private void KillBuiltInAdbOnExitToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            var settings = SettingsService.Load();
            settings.KillBuiltInAdbOnExit = KillBuiltInAdbOnExitToggle.IsOn;
            SettingsService.Save(settings);
        }

        private void VerifyHashToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            var settings = SettingsService.Load();
            settings.VerifyHashAfterTransfer = VerifyHashToggle.IsOn;
            SettingsService.Save(settings);
        }

        private void CustomAdbDirTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializing) return;

            var settings = SettingsService.Load();
            settings.CustomAdbDirectory = CustomAdbDirTextBox.Text?.Trim() ?? "";
            SettingsService.Save(settings);

            UpdateAdbResolvedPreview(settings);
            AdbConfigChanged?.Invoke(this, EventArgs.Empty);
        }

        private void BrowseAdbDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = App.MainWindow != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow) : IntPtr.Zero;
                var folderPath = Helpers.FolderPickerHelper.PickFolder(hwnd);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    CustomAdbDirTextBox.Text = folderPath;
                }
            }
            catch (Exception ex)
            {
                AdbStatusText.Text = $"✖ Error picking folder: {ex.Message}";
                AdbStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            }
        }

        private void VerifyAdb_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settings = SettingsService.Load();
                var resolved = AdbSyncService.ResolveAdbExecutablePath(settings);
                AdbSyncService.Instance.StartServerAndConnect();
                var dev = AdbSyncService.Instance.CurrentDevice;
                var devInfo = dev != null ? $"{dev.Model} ({dev.Serial})" : "No device online";
                AdbStatusText.Text = $"✔ ADB OK ({resolved ?? "Active Server"}) | Device: {devInfo}";
                AdbStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
                AdbConfigChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AdbStatusText.Text = $"✖ {ex.Message}";
                AdbStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ManageDevices_Click(object sender, RoutedEventArgs e)
        {
            ManageDevicesRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
