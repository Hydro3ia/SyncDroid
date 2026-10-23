using System.Collections.Generic;

namespace SyncDroidWinUI.Models
{
    public class LocalSendInfoDto
    {
        public string Alias { get; set; } = "";
        public string Version { get; set; } = "2.1";
        public string? DeviceModel { get; set; } = "Windows PC";
        public string? DeviceType { get; set; } = "desktop";
        public string Fingerprint { get; set; } = "";
        public int Port { get; set; } = 53317;
        public string Protocol { get; set; } = "https";
        public bool Download { get; set; } = false;
        public bool? Announce { get; set; }
    }

    public class LocalSendFileMetadataDto
    {
        public string? Modified { get; set; }
        public string? Accessed { get; set; }
    }

    public class LocalSendFileDto
    {
        public string Id { get; set; } = "";
        public string FileName { get; set; } = "";
        public long Size { get; set; }
        public string FileType { get; set; } = "application/octet-stream";
        public string? Sha256 { get; set; }
        public string? Preview { get; set; }
        public LocalSendFileMetadataDto? Metadata { get; set; }
    }

    public class LocalSendPrepareUploadRequestDto
    {
        public LocalSendInfoDto Info { get; set; } = new();
        public Dictionary<string, LocalSendFileDto> Files { get; set; } = new();
    }

    public class LocalSendPrepareUploadResponseDto
    {
        public string SessionId { get; set; } = "";
        public Dictionary<string, string> Files { get; set; } = new();
    }

    public class LocalSendPeer
    {
        public string Alias { get; set; } = "";
        public string Version { get; set; } = "2.1";
        public string DeviceModel { get; set; } = "";
        public string DeviceType { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public int Port { get; set; } = 53317;
        public string Protocol { get; set; } = "https";

        public string Endpoint => $"{IpAddress}:{Port}";

        public string DisplayName => string.IsNullOrWhiteSpace(DeviceModel)
            ? $"[LocalSend] {Alias} ({Endpoint})"
            : $"[LocalSend] {Alias} ({DeviceModel} • {Endpoint})";

        public override string ToString() => DisplayName;
    }
}
