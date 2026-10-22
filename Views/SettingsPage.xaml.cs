using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SyncDroidWinUI.Services;
using System;

namespace SyncDroidWinUI.Views
{
    public sealed partial class SettingsPage : Page
    {
        public LocalizationService Loc => LocalizationService.Instance;

        public event EventHandler? BackRequested;
        public event EventHandler? AdbConfigChanged;

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

            // ADB mode
            CustomAdbDirTextBox.Text = settings.CustomAdbDirectory ?? "";
            if (settings.UseCustomAdbPath)
            {
                UseCustomAdbRadio.IsChecked = true;
            }
            else
            {
                UsePathAdbRadio.IsChecked = true;
            }

            UpdateCustomAdbControlsEnabled(settings.UseCustomAdbPath);
            UpdateAdbResolvedPreview(settings);

            _isInitializing = false;
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

            var resolved = AdbSyncService.ResolveAdbExecutablePath(settings);
            if (!string.IsNullOrEmpty(resolved))
            {
                AdbStatusText.Text = $"✔ adb.exe: {resolved}";
                AdbStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
            }
            else
            {
                AdbStatusText.Text = settings.UseCustomAdbPath
                    ? "✖ adb.exe not found in specified directory"
                    : "⚠ adb.exe not found in system PATH";
                AdbStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            }
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (LanguageComboBox.SelectedItem is ComboBoxItem item && item.Tag is string langCode)
            {
                LocalizationService.Instance.CurrentLanguage = langCode;
            }
        }

        private void AdbModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            bool isCustom = UseCustomAdbRadio.IsChecked == true;
            UpdateCustomAdbControlsEnabled(isCustom);

            var settings = SettingsService.Load();
            settings.UseCustomAdbPath = isCustom;
            settings.CustomAdbDirectory = CustomAdbDirTextBox.Text?.Trim() ?? "";
            SettingsService.Save(settings);

            UpdateAdbResolvedPreview(settings);
            AdbConfigChanged?.Invoke(this, EventArgs.Empty);
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
    }
}
