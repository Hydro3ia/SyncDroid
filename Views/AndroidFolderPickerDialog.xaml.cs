using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SyncDroidWinUI.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace SyncDroidWinUI.Views
{
    public class AndroidFolderNode
    {
        public string DisplayName { get; set; } = "";
        public string FullPath { get; set; } = "";

        public override string ToString() => DisplayName;
    }

    public sealed partial class AndroidFolderPickerDialog : ContentDialog
    {
        public LocalizationService Loc => LocalizationService.Instance;

        public string SelectedPath => string.IsNullOrWhiteSpace(SelectedPathBox.Text)
            ? "/storage/emulated/0"
            : SelectedPathBox.Text.Trim();

        private TreeViewNode? _selectedTreeNode;

        public AndroidFolderPickerDialog(string? initialPath = null)
        {
            this.InitializeComponent();
            this.DataContext = this;

            var dev = AdbSyncService.Instance.CurrentDevice;
            DeviceNameText.Text = dev != null ? $"{dev.Model} ({dev.Serial})" : "";

            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                SelectedPathBox.Text = initialPath.Trim();
            }
            else
            {
                SelectedPathBox.Text = "/storage/emulated/0";
            }

            this.Opened += AndroidFolderPickerDialog_Opened;
        }

        private async void AndroidFolderPickerDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            await LoadStorageTreeAsync(forceRefresh: false);
        }

        private async Task LoadStorageTreeAsync(bool forceRefresh)
        {
            try
            {
                ErrorStatusText.Visibility = Visibility.Collapsed;
                LoadingRing.IsActive = true;
                StorageTreeView.RootNodes.Clear();

                if (forceRefresh)
                {
                    AdbSyncService.Instance.ClearDirectoryCache();
                }

                var rootFolder = new AndroidFolderNode
                {
                    DisplayName = "/storage",
                    FullPath = "/storage"
                };

                var rootNode = new TreeViewNode
                {
                    Content = rootFolder,
                    HasUnrealizedChildren = true
                };

                StorageTreeView.RootNodes.Add(rootNode);

                await PopulateNodeChildrenAsync(rootNode, forceRefresh);
                rootNode.IsExpanded = true;

                // Automatically expand /storage/emulated and /storage/emulated/0 for convenience
                var emulatedNode = rootNode.Children.FirstOrDefault(
                    n => (n.Content as AndroidFolderNode)?.DisplayName == "emulated");
                if (emulatedNode != null)
                {
                    await PopulateNodeChildrenAsync(emulatedNode, forceRefresh);
                    emulatedNode.IsExpanded = true;

                    var zeroNode = emulatedNode.Children.FirstOrDefault(
                        n => (n.Content as AndroidFolderNode)?.DisplayName == "0");
                    if (zeroNode != null)
                    {
                        await PopulateNodeChildrenAsync(zeroNode, forceRefresh);
                        zeroNode.IsExpanded = true;
                        _selectedTreeNode = zeroNode;
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorStatusText.Text = ex.Message;
                ErrorStatusText.Visibility = Visibility.Visible;
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private async Task PopulateNodeChildrenAsync(TreeViewNode parentNode, bool forceRefresh)
        {
            if (parentNode.Content is not AndroidFolderNode parentFolder) return;

            parentNode.Children.Clear();
            var subDirs = await AdbSyncService.Instance.GetRemoteSubdirectoriesAsync(parentFolder.FullPath, forceRefresh);
            foreach (var dirName in subDirs)
            {
                var fullPath = $"{parentFolder.FullPath.TrimEnd('/')}/{dirName}";
                var childNode = new TreeViewNode
                {
                    Content = new AndroidFolderNode
                    {
                        DisplayName = dirName,
                        FullPath = fullPath
                    },
                    HasUnrealizedChildren = true
                };
                parentNode.Children.Add(childNode);
            }

            parentNode.HasUnrealizedChildren = false;
        }

        private async void StorageTreeView_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
        {
            if (args.Node.HasUnrealizedChildren)
            {
                try
                {
                    LoadingRing.IsActive = true;
                    await PopulateNodeChildrenAsync(args.Node, forceRefresh: false);
                }
                catch (Exception ex)
                {
                    ErrorStatusText.Text = ex.Message;
                    ErrorStatusText.Visibility = Visibility.Visible;
                }
                finally
                {
                    LoadingRing.IsActive = false;
                }
            }
        }

        private void StorageTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            if (args.InvokedItem is TreeViewNode node && node.Content is AndroidFolderNode folder)
            {
                _selectedTreeNode = node;
                SelectedPathBox.Text = folder.FullPath;
            }
            else if (args.InvokedItem is AndroidFolderNode directFolder)
            {
                SelectedPathBox.Text = directFolder.FullPath;
            }
        }

        private async void RefreshTree_Click(object sender, RoutedEventArgs e)
        {
            await LoadStorageTreeAsync(forceRefresh: true);
        }

        private void NewFolderToggle_Click(object sender, RoutedEventArgs e)
        {
            NewFolderBar.Visibility = NewFolderBar.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private async void CreateNewFolder_Click(object sender, RoutedEventArgs e)
        {
            var folderName = NewFolderNameBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(folderName)) return;

            try
            {
                ErrorStatusText.Visibility = Visibility.Collapsed;
                LoadingRing.IsActive = true;

                var parentPath = SelectedPath;
                var createdPath = await AdbSyncService.Instance.CreateRemoteDirectoryAsync(parentPath, folderName);

                NewFolderNameBox.Text = "";
                NewFolderBar.Visibility = Visibility.Collapsed;
                SelectedPathBox.Text = createdPath;

                if (_selectedTreeNode != null && (_selectedTreeNode.Content as AndroidFolderNode)?.FullPath == parentPath)
                {
                    await PopulateNodeChildrenAsync(_selectedTreeNode, forceRefresh: true);
                    _selectedTreeNode.IsExpanded = true;
                }
                else
                {
                    await LoadStorageTreeAsync(forceRefresh: true);
                }
            }
            catch (Exception ex)
            {
                ErrorStatusText.Text = ex.Message;
                ErrorStatusText.Visibility = Visibility.Visible;
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }
    }
}
