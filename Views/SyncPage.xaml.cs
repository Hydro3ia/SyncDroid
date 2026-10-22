using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SyncDroidWinUI.Services;
using SyncDroidWinUI.ViewModels;
using System;
using System.Collections.Specialized;

namespace SyncDroidWinUI.Views
{
    public sealed partial class SyncPage : Page
    {
        public SyncViewModel? ViewModel { get; set; }

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

        private void BrowsePcPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = App.MainWindow != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow) : IntPtr.Zero;
                var folderPath = Helpers.FolderPickerHelper.PickFolder(hwnd);
                if (!string.IsNullOrEmpty(folderPath) && ViewModel != null)
                {
                    ViewModel.PcPath = folderPath;
                    ViewModel.AddPcPathToHistory(folderPath);
                    PcPathComboBox.Text = folderPath;
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
                var syncService = AdbSyncService.Instance;
                if (syncService.CurrentDevice == null || syncService.CurrentDevice.State != SharpAdbClient.DeviceState.Online)
                {
                    try
                    {
                        syncService.StartServerAndConnect();
                    }
                    catch { }
                }

                if (syncService.CurrentDevice == null || syncService.CurrentDevice.State != SharpAdbClient.DeviceState.Online)
                {
                    if (ViewModel != null)
                    {
                        ViewModel.StatusText = LocalizationService.Instance.NoDeviceConnectedError;
                    }
                    return;
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
                    ViewModel.StatusText = "Error browsing Android folder: " + ex.Message;
                }
            }
        }

        private void PcPathComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViewModel != null && e.AddedItems.Count > 0 && e.AddedItems[0] is string selected && !string.IsNullOrWhiteSpace(selected))
            {
                ViewModel.PcPath = selected;
                PcPathComboBox.Text = selected;
            }
        }

        private void PcPathComboBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null && !string.IsNullOrWhiteSpace(PcPathComboBox.Text))
            {
                ViewModel.PcPath = PcPathComboBox.Text.Trim();
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

        private void StartSyncButton_Click(object sender, RoutedEventArgs e)
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

            if (ViewModel.StartSyncCommand.CanExecute(null))
            {
                ViewModel.StartSyncCommand.Execute(null);
            }
        }
    }
}
