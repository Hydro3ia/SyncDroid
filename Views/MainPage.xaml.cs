using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SyncDroidWinUI.Services;
using SyncDroidWinUI.ViewModels;
using System;
using System.Linq;

namespace SyncDroidWinUI.Views
{
    public class DeviceItem
    {
        public string Name { get; set; } = "";
        public string Serial { get; set; } = "";
        public SharpAdbClient.DeviceData? Device { get; set; }
    }

    public sealed partial class MainPage : Page
    {
        public LocalizationService Loc => LocalizationService.Instance;

        public MainPage()
        {
            this.InitializeComponent();
            this.DataContext = this;

            PcToAndroidFrame.Navigate(typeof(SyncPage), new SyncViewModel(isPcToAndroid: true));
            AndroidToPcFrame.Navigate(typeof(SyncPage), new SyncViewModel(isPcToAndroid: false));

            SettingsView.BackRequested += (_, _) => ShowSyncTabs();
            SettingsView.AdbConfigChanged += (_, _) => RefreshDeviceStatus();

            RefreshDeviceStatus();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsView.Visibility == Visibility.Visible)
            {
                ShowSyncTabs();
            }
            else
            {
                SettingsView.LoadSettingsToUi();
                MainTabView.Visibility = Visibility.Collapsed;
                SettingsView.Visibility = Visibility.Visible;
            }
        }

        private void ShowSyncTabs()
        {
            SettingsView.Visibility = Visibility.Collapsed;
            MainTabView.Visibility = Visibility.Visible;
            RefreshDeviceStatus();
        }

        private void RefreshAdb_Click(object sender, RoutedEventArgs e)
        {
            RefreshDeviceStatus();
        }

        private void RefreshDeviceStatus()
        {
            try
            {
                var syncService = AdbSyncService.Instance;
                syncService.StartServerAndConnect();

                var devices = syncService.GetDevices();
                DeviceComboBox.ItemsSource = devices
                    .Select(d => new DeviceItem { Name = $"{d.Model} ({d.Serial})", Serial = d.Serial, Device = d })
                    .ToList();

                if (devices.Any())
                {
                    DeviceComboBox.SelectedIndex = 0;
                    DeviceStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
                }
                else
                {
                    DeviceComboBox.ItemsSource = null;
                    syncService.CurrentDevice = null;
                    DeviceStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
                }
            }
            catch
            {
                DeviceComboBox.ItemsSource = null;
                AdbSyncService.Instance.CurrentDevice = null;
                DeviceStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            }
        }

        private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DeviceComboBox.SelectedItem is DeviceItem selected)
            {
                AdbSyncService.Instance.CurrentDevice = selected.Device;
            }
        }
    }
}
