using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SyncDroidWinUI.Models;
using SyncDroidWinUI.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SyncDroidWinUI.Views
{
    public class AndroidFolderNode
    {
        public string DisplayName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDirectory { get; set; } = true;
        public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5";
        public string IconColor => IsDirectory ? "#E8A838" : "#6495ED";

        public override string ToString() => DisplayName;
    }

    public sealed partial class AndroidFolderPickerDialog : ContentDialog
    {
        public LocalizationService Loc => LocalizationService.Instance;

        private readonly bool _allowFileSelection;

        public string DialogTitle => _allowFileSelection ? Loc.AndroidPullPickerTitle : Loc.AndroidPickerTitle;
        public string PrimaryActionText => _allowFileSelection ? Loc.SelectItemPrimaryButton : Loc.SelectFolderPrimaryButton;

        public string SelectedPath => string.IsNullOrWhiteSpace(SelectedPathBox.Text)
            ? "/storage/emulated/0"
            : SelectedPathBox.Text.Trim();

        public bool SelectedIsDirectory { get; private set; } = true;

        private TreeViewNode? _selectedTreeNode;

        private bool IsWirelessMode =>
            CompanionSyncService.Instance.ActiveConnectionMode == ConnectionMode.WirelessCompanion &&
            CompanionSyncService.Instance.CurrentPeer != null;

        public AndroidFolderPickerDialog(string? initialPath = null, bool allowFileSelection = false)
        {
            _allowFileSelection = allowFileSelection;
            this.InitializeComponent();
            this.DataContext = this;

            if (IsWirelessMode)
            {
                var peer = CompanionSyncService.Instance.CurrentPeer!;
                DeviceNameText.Text = peer.DisplayName;
            }
            else
            {
                var dev = AdbSyncService.Instance.CurrentDevice;
                DeviceNameText.Text = dev != null ? $"[ADB] {dev.Model} ({dev.Serial})" : "";
            }

            if (_allowFileSelection)
            {
                NewFolderToggleBtn.Visibility = Visibility.Collapsed;
            }

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
                    if (IsWirelessMode)
                        CompanionSyncService.Instance.ClearDirectoryCache();
                    else
                        AdbSyncService.Instance.ClearDirectoryCache();
                }

                var rootFolder = new AndroidFolderNode
                {
                    DisplayName = "/storage",
                    FullPath = "/storage",
                    IsDirectory = true
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
            if (parentNode.Content is not AndroidFolderNode parentFolder || !parentFolder.IsDirectory) return;

            parentNode.Children.Clear();

            if (_allowFileSelection &&
                parentFolder.FullPath != "/storage" &&
                parentFolder.FullPath != "/storage/emulated")
            {
                List<RemoteEntryDto> entries;
                if (IsWirelessMode)
                {
                    var listing = await CompanionSyncService.Instance.GetRemoteDirectoryListingAsync(parentFolder.FullPath, includeFiles: true);
                    entries = listing.Entries;
                }
                else
                {
                    entries = await AdbSyncService.Instance.GetRemoteEntriesAsync(parentFolder.FullPath);
                }

                foreach (var entry in entries)
                {
                    var fullPath = !string.IsNullOrWhiteSpace(entry.FullPath)
                        ? entry.FullPath
                        : $"{parentFolder.FullPath.TrimEnd('/')}/{entry.Name}";
                    var childNode = new TreeViewNode
                    {
                        Content = new AndroidFolderNode
                        {
                            DisplayName = entry.Name,
                            FullPath = fullPath,
                            IsDirectory = entry.IsDirectory
                        },
                        HasUnrealizedChildren = entry.IsDirectory
                    };
                    parentNode.Children.Add(childNode);
                }
            }
            else
            {
                var subDirs = IsWirelessMode
                    ? await CompanionSyncService.Instance.GetRemoteSubdirectoriesAsync(parentFolder.FullPath, forceRefresh)
                    : await AdbSyncService.Instance.GetRemoteSubdirectoriesAsync(parentFolder.FullPath, forceRefresh);

                foreach (var dirName in subDirs)
                {
                    var fullPath = $"{parentFolder.FullPath.TrimEnd('/')}/{dirName}";
                    var childNode = new TreeViewNode
                    {
                        Content = new AndroidFolderNode
                        {
                            DisplayName = dirName,
                            FullPath = fullPath,
                            IsDirectory = true
                        },
                        HasUnrealizedChildren = true
                    };
                    parentNode.Children.Add(childNode);
                }
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
                SelectedIsDirectory = folder.IsDirectory;
            }
            else if (args.InvokedItem is AndroidFolderNode directFolder)
            {
                SelectedPathBox.Text = directFolder.FullPath;
                SelectedIsDirectory = directFolder.IsDirectory;
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
                var createdPath = IsWirelessMode
                    ? await CompanionSyncService.Instance.CreateRemoteDirectoryAsync(parentPath, folderName)
                    : await AdbSyncService.Instance.CreateRemoteDirectoryAsync(parentPath, folderName);

                NewFolderNameBox.Text = "";
                NewFolderBar.Visibility = Visibility.Collapsed;
                SelectedPathBox.Text = createdPath;
                SelectedIsDirectory = true;

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
