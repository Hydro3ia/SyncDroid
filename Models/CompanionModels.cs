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
        public string Version { get; set; } = "0.1.4.0";
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
}
