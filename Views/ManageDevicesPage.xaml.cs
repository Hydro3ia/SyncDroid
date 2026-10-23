using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SyncDroidWinUI.Models;
using SyncDroidWinUI.Services;
using System;
using System.Collections.ObjectModel;

namespace SyncDroidWinUI.Views
{
    public sealed partial class ManageDevicesPage : Page
    {
        public LocalizationService Loc => LocalizationService.Instance;

        public ObservableCollection<TrustedDevice> TrustedDevices { get; } = new();

        public event EventHandler? BackRequested;

        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

        public ManageDevicesPage()
        {
            this.InitializeComponent();
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            this.DataContext = this;

            SettingsService.TrustedDevicesChanged += (_, _) =>
            {
                _dispatcherQueue?.TryEnqueue(LoadDevicesToUi);
            };
            LocalizationService.Instance.LanguageChanged += (_, _) =>
            {
                _dispatcherQueue?.TryEnqueue(LoadDevicesToUi);
            };

            LoadDevicesToUi();
        }

        public void LoadDevicesToUi()
        {
            var settings = SettingsService.Load();
            TrustedDevices.Clear();
            foreach (var d in settings.TrustedDevices)
            {
                TrustedDevices.Add(d);
            }

            DeviceCountText.Text = TrustedDevices.Count.ToString();
            EmptyDevicesHintPanel.Visibility = TrustedDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RemoveAllDevicesButton.IsEnabled = TrustedDevices.Count > 0;
        }

        private void DeleteTrustedDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id && !string.IsNullOrWhiteSpace(id))
            {
                SettingsService.RemoveTrustedDevice(id);
                LoadDevicesToUi();
            }
        }

        private void RemoveAllDevices_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.ClearTrustedDevices();
            LoadDevicesToUi();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
