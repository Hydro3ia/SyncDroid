using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SyncDroidWinUI.Models;
using SyncDroidWinUI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SyncDroidWinUI.Views
{
    public class AndroidFolderNode
    {
        public string DisplayName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDirectory { get; set; } = true;
        public bool IsDrive { get; set; }
        public string IconGlyph => IsDrive ? "\uEDA2" : (IsDirectory ? "\uE8B7" : "\uE8A5");
        public string IconColor => IsDrive ? "#4CC2FF" : (IsDirectory ? "#E8A838" : "#6495ED");

        public override string ToString() => DisplayName;
    }

    public sealed partial class AndroidFolderPickerDialog : ContentDialog
    {
        public LocalizationService Loc => LocalizationService.Instance;

        private readonly bool _allowFileSelection;
        private bool _isUpdatingRootSelector;
        private bool _remoteHasWindowsDrives;

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

        private bool HasOnlineAdbDevice =>
            AdbSyncService.Instance.CurrentDevice != null &&
            AdbSyncService.Instance.CurrentDevice.State == SharpAdbClient.DeviceState.Online;

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
            else if (HasOnlineAdbDevice)
            {
                var dev = AdbSyncService.Instance.CurrentDevice!;
                DeviceNameText.Text = $"[ADB] {dev.Model} ({dev.Serial})";
            }
            else
            {
                DeviceNameText.Text = $"{Environment.MachineName} (Windows Drives)";
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

        private static List<string> GetLocalWindowsDrives()
        {
            var list = new List<string>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.IsReady)
                        {
                            list.Add(d.RootDirectory.FullName);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (list.Count == 0)
            {
                list.Add(@"C:\");
            }
            return list;
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

                var winDrives = new List<string>();
                _remoteHasWindowsDrives = false;

                if (IsWirelessMode)
                {
                    var remoteDrives = await CompanionSyncService.Instance.GetRemoteWindowsDrivesAsync(forceRefresh);
                    if (remoteDrives.Count > 0)
                    {
                        _remoteHasWindowsDrives = true;
                        winDrives.AddRange(remoteDrives);
                    }
                }

                if (winDrives.Count == 0)
                {
                    winDrives.AddRange(GetLocalWindowsDrives());
                }

                bool hasAndroidStorage = (IsWirelessMode && !_remoteHasWindowsDrives) || (!IsWirelessMode && HasOnlineAdbDevice);

                var rootOptions = new List<string>();
                TreeViewNode? storageRootNode = null;

                if (hasAndroidStorage)
                {
                    rootOptions.Add("/storage");
                    var rootFolder = new AndroidFolderNode
                    {
                        DisplayName = "/storage",
                        FullPath = "/storage",
                        IsDirectory = true,
                        IsDrive = false
                    };

                    storageRootNode = new TreeViewNode
                    {
                        Content = rootFolder,
                        HasUnrealizedChildren = true
                    };
                    StorageTreeView.RootNodes.Add(storageRootNode);
                }

                var driveNodes = new Dictionary<string, TreeViewNode>(StringComparer.OrdinalIgnoreCase);
                foreach (var drive in winDrives)
                {
                    string normalizedDrive = CompanionSyncService.NormalizeRemoteTargetPath(drive);
                    rootOptions.Add(normalizedDrive);

                    var driveNode = new TreeViewNode
                    {
                        Content = new AndroidFolderNode
                        {
                            DisplayName = normalizedDrive,
                            FullPath = normalizedDrive,
                            IsDirectory = true,
                            IsDrive = true
                        },
                        HasUnrealizedChildren = true
                    };
                    StorageTreeView.RootNodes.Add(driveNode);
                    driveNodes[normalizedDrive] = driveNode;
                }

                _isUpdatingRootSelector = true;
                RootDriveComboBox.ItemsSource = rootOptions;

                string currentSelected = SelectedPathBox.Text?.Trim() ?? "";
                if (!hasAndroidStorage && (string.IsNullOrEmpty(currentSelected) || currentSelected.StartsWith("/")))
                {
                    currentSelected = winDrives.FirstOrDefault() ?? @"C:\";
                    SelectedPathBox.Text = currentSelected;
                }

                if (CompanionSyncService.IsWindowsDrivePath(currentSelected))
                {
                    string driveRoot = char.ToUpperInvariant(currentSelected.TrimStart('/')[0]) + @":\";
                    int idx = rootOptions.FindIndex(r => string.Equals(r, driveRoot, StringComparison.OrdinalIgnoreCase));
                    RootDriveComboBox.SelectedIndex = idx >= 0 ? idx : 0;
                    _isUpdatingRootSelector = false;

                    if (driveNodes.TryGetValue(driveRoot, out var targetDriveNode))
                    {
                        await PopulateNodeChildrenAsync(targetDriveNode, forceRefresh);
                        targetDriveNode.IsExpanded = true;
                        _selectedTreeNode = targetDriveNode;
                    }
                }
                else if (storageRootNode != null)
                {
                    RootDriveComboBox.SelectedIndex = 0;
                    _isUpdatingRootSelector = false;

                    await PopulateNodeChildrenAsync(storageRootNode, forceRefresh);
                    storageRootNode.IsExpanded = true;

                    var emulatedNode = storageRootNode.Children.FirstOrDefault(
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
                else
                {
                    RootDriveComboBox.SelectedIndex = rootOptions.Count > 0 ? 0 : -1;
                    _isUpdatingRootSelector = false;
                }
            }
            catch (Exception ex)
            {
                _isUpdatingRootSelector = false;
                ErrorStatusText.Text = ex.Message;
                ErrorStatusText.Visibility = Visibility.Visible;
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private async void RootDriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingRootSelector) return;
            if (RootDriveComboBox.SelectedItem is not string selectedRoot || string.IsNullOrWhiteSpace(selectedRoot))
                return;

            if (selectedRoot == "/storage")
            {
                SelectedPathBox.Text = "/storage/emulated/0";
            }
            else
            {
                SelectedPathBox.Text = selectedRoot;
            }
            SelectedIsDirectory = true;

            var matchedNode = StorageTreeView.RootNodes.FirstOrDefault(
                n => string.Equals((n.Content as AndroidFolderNode)?.FullPath, selectedRoot, StringComparison.OrdinalIgnoreCase));
            if (matchedNode != null)
            {
                try
                {
                    LoadingRing.IsActive = true;
                    if (matchedNode.HasUnrealizedChildren)
                    {
                        await PopulateNodeChildrenAsync(matchedNode, forceRefresh: false);
                    }
                    matchedNode.IsExpanded = true;
                    _selectedTreeNode = matchedNode;
                }
                catch { }
                finally
                {
                    LoadingRing.IsActive = false;
                }
            }
        }

        private async Task PopulateNodeChildrenAsync(TreeViewNode parentNode, bool forceRefresh)
        {
            if (parentNode.Content is not AndroidFolderNode parentFolder || !parentFolder.IsDirectory) return;

            parentNode.Children.Clear();
            bool isWinPath = CompanionSyncService.IsWindowsDrivePath(parentFolder.FullPath);

            if (isWinPath && !(IsWirelessMode && _remoteHasWindowsDrives))
            {
                // Browse local Windows drive path directly
                await Task.Run(() =>
                {
                    var childFolders = new List<AndroidFolderNode>();
                    var childFiles = new List<AndroidFolderNode>();

                    try
                    {
                        foreach (var dir in Directory.EnumerateDirectories(parentFolder.FullPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var di = new DirectoryInfo(dir);
                                if ((di.Attributes & FileAttributes.System) != 0) continue;
                                childFolders.Add(new AndroidFolderNode
                                {
                                    DisplayName = di.Name,
                                    FullPath = di.FullName,
                                    IsDirectory = true
                                });
                            }
                            catch { }
                        }
                    }
                    catch { }

                    if (_allowFileSelection)
                    {
                        try
                        {
                            foreach (var file in Directory.EnumerateFiles(parentFolder.FullPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    var fi = new FileInfo(file);
                                    childFiles.Add(new AndroidFolderNode
                                    {
                                        DisplayName = fi.Name,
                                        FullPath = fi.FullName,
                                        IsDirectory = false
                                    });
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }

                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        foreach (var cf in childFolders)
                        {
                            parentNode.Children.Add(new TreeViewNode
                            {
                                Content = cf,
                                HasUnrealizedChildren = true
                            });
                        }
                        foreach (var fl in childFiles)
                        {
                            parentNode.Children.Add(new TreeViewNode
                            {
                                Content = fl,
                                HasUnrealizedChildren = false
                            });
                        }
                    });
                });

                parentNode.HasUnrealizedChildren = false;
                return;
            }

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
                        : CompanionSyncService.CombineRemotePath(parentFolder.FullPath, entry.Name);
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
                    var fullPath = CompanionSyncService.CombineRemotePath(parentFolder.FullPath, dirName);
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
                string createdPath;

                if (CompanionSyncService.IsWindowsDrivePath(parentPath) && !(IsWirelessMode && _remoteHasWindowsDrives))
                {
                    createdPath = Path.Combine(CompanionSyncService.NormalizeRemoteTargetPath(parentPath), folderName.Trim('\\', '/'));
                    Directory.CreateDirectory(createdPath);
                }
                else if (IsWirelessMode)
                {
                    createdPath = await CompanionSyncService.Instance.CreateRemoteDirectoryAsync(parentPath, folderName);
                }
                else
                {
                    createdPath = await AdbSyncService.Instance.CreateRemoteDirectoryAsync(parentPath, folderName);
                }

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
