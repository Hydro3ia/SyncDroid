using System;
using System.Collections.Generic;

namespace SyncDroidWinUI.Models
{
    public enum ConnectionMode
    {
        Adb = 0,
        WirelessCompanion = 1,
        LocalSend = 2
    }

    public enum AdbSourceMode
    {
        BuiltInOrRunning = 0,
        SystemPath = 1,
        CustomDirectory = 2
    }

    public enum ConflictPolicy
    {
        SkipIdenticalSize = 0,
        Overwrite = 1,
        KeepBothRename = 2
    }

    public class RemoteEntryDto
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public long Size { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
        public bool IsDirectory { get; set; }
    }

    public class DirectoryListingResponse
    {
        public string CurrentPath { get; set; } = "";
        public string ParentPath { get; set; } = "";
        public List<RemoteEntryDto> Entries { get; set; } = new();
    }

    public class TreeManifestResponse
    {
        public string RootPath { get; set; } = "";
        public List<RemoteEntryDto> Files { get; set; } = new();
    }

    public class CompanionInfoResponse
    {
        public string DeviceName { get; set; } = "";
        public string Platform { get; set; } = "Windows";
        public string Version { get; set; } = "0.2.0.0";
        public int ListeningPort { get; set; } = 45821;
        public long FreeSpaceBytes { get; set; }
        public string DefaultStorageRoot { get; set; } = @"C:\";
    }

    public class CompanionPeer
    {
        public string DeviceName { get; set; } = "";
        public string Platform { get; set; } = "Android";
        public string Version { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public int Port { get; set; } = 45821;

        public string Endpoint => $"{IpAddress}:{Port}";

        public string DisplayName => $"[LAN] {DeviceName} ({Endpoint})";

        public override string ToString() => DisplayName;
    }

    public class TransferQueueItem
    {
        public string LocalPath { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public string SizeDisplay { get; set; } = "";
        public string TypeBadge => IsDirectory ? "FOLDER" : "FILE";
        public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    }

    public class TrustedDevice
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string DeviceType { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string AuthenticatedAt { get; set; } = "";

        public string DisplayEndpoint => string.IsNullOrWhiteSpace(IpAddress) ? "-" : IpAddress;
        public string IconGlyph => string.Equals(DeviceType, "LocalSend", StringComparison.OrdinalIgnoreCase) ? "\uE898" : "\uE8EA";
        public string DeleteButtonText => SyncDroidWinUI.Services.LocalizationService.Instance.DeleteDeviceButton;
    }

    public class ReceivedFileItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public string Id { get; set; } = "";
        public string FileName { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string Protocol { get; set; } = "";
        public long Size { get; set; }
        public string SizeDisplay { get; set; } = "";

        private string _savedPath = "";
        public string SavedPath
        {
            get => _savedPath;
            set => SetProperty(ref _savedPath, value);
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private double _progressPercent;
        public double ProgressPercent
        {
            get => _progressPercent;
            set => SetProperty(ref _progressPercent, value);
        }
    }

    public class IncomingTransferSessionEventArgs : EventArgs
    {
        public string SessionId { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string SenderIp { get; set; } = "";
        public string Protocol { get; set; } = "";
        public string SaveDirectory { get; set; } = "";
        public bool RequiresConfirmation { get; set; }
        public bool AppendToExistingSession { get; set; }
        public List<ReceivedFileItem> Files { get; set; } = new();
        public System.Threading.Tasks.TaskCompletionSource<bool>? ConfirmationTcs { get; set; }
    }
}
