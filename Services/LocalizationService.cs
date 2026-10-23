using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace SyncDroidWinUI.Services
{
    public class LocalizationService : ObservableObject
    {
        public static LocalizationService Instance { get; } = new LocalizationService();

        public event EventHandler? LanguageChanged;

        private string _currentLanguage = "en-US";

        private LocalizationService()
        {
            var settings = SettingsService.Load();
            _currentLanguage = string.IsNullOrWhiteSpace(settings.Language) ? "en-US" : settings.Language;
        }

        public string CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (string.IsNullOrWhiteSpace(value) || _currentLanguage == value) return;
                _currentLanguage = value;

                var settings = SettingsService.Load();
                settings.Language = _currentLanguage;
                SettingsService.Save(settings);

                NotifyAll();
                LanguageChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private string T(string en, string zhCn, string zhTw)
        {
            return _currentLanguage switch
            {
                "zh-CN" => zhCn,
                "zh-TW" => zhTw,
                _ => en
            };
        }

        // Title bar & Top bar
        public string AppSubtitle => T("ADB, Wireless Companion & LocalSend Sync", "ADB、无线伴侣与 LocalSend 传输同步工具", "ADB、無線伴侶與 LocalSend 傳輸同步工具");
        public string AndroidDeviceLabel => T("Target Device", "连接设备", "連接裝置");
        public string NoDevicePlaceholder => T(
            "Need help with connecting? Check ? button on the right",
            "连接遇到困难？请点击右侧的 ? 按钮查看帮助",
            "連接遇到困難？請點擊右側的 ? 按鈕查看說明");
        public string DisconnectDeviceButton => T("Disconnect", "断开连接", "中斷連接");
        public string DisconnectDeviceTooltip => T("Disconnect current device", "断开当前连接的设备", "中斷目前連接的裝置");
        public string RefreshAdbButton => T("Refresh Devices", "刷新设备", "重新整理裝置");
        public string WirelessConnectButton => T("Wireless Connect", "无线连接", "無線連接");
        public string ConnectionHelpTooltip => T("How to enable Developer Options & USB Debugging", "如何开启 Android 开发者模式与 USB 调试", "如何開啟 Android 開發人員選項與 USB 偵錯");
        public string ConnectionHelpDialogTitle => T(
            "How to Enable Developer Options & USB Debugging on Android",
            "如何在 Android 上开启开发者模式与打开 USB 调试",
            "如何在 Android 上開啟開發人員模式與打開 USB 偵錯");
        public string ConnectionHelpDialogContent => T(
            "1. Enable Developer Options:\n" +
            "   • Open the \"Settings\" app on your Android phone or tablet.\n" +
            "   • Tap \"About phone\" (or \"About tablet\" / \"My device\").\n" +
            "   • Locate \"Build number\" (Xiaomi/HyperOS: \"OS version\" / \"MIUI version\"; Samsung: \"Software information\" ➔ \"Build number\").\n" +
            "   • Tap \"Build number\" 7 times rapidly until you see \"You are now a developer!\" (enter your lock screen PIN if prompted).\n\n" +
            "2. Turn On USB Debugging:\n" +
            "   • Return to main \"Settings\" and open \"System\" ➔ \"Developer options\" (or \"Additional settings\" ➔ \"Developer options\").\n" +
            "   • Scroll down and turn ON \"USB debugging\".\n" +
            "   • (Tip: On Xiaomi/OPPO/vivo devices, also enable \"USB debugging (Security settings)\" if present, and set USB mode to \"File Transfer / MTP\").\n\n" +
            "3. Authorize Connection on Your Phone:\n" +
            "   • Connect your device to the PC via a USB data cable and click \"Refresh Devices\".\n" +
            "   • Unlock your phone screen, check \"Always allow from this computer\" in the \"Allow USB debugging?\" popup, and tap \"Allow / OK\".",
            "1. 开启「开发者选项（开发者模式）」：\n" +
            "   • 打开 Android 手机或平板上的系统「设置 (Settings)」。\n" +
            "   • 进入「关于手机」（或「关于本机」/「我的设备」）。\n" +
            "   • 找到「版本号 (Build number)」（小米澎湃 OS / MIUI 请点击「OS 版本」或「MIUI 版本」；三星请进入「软件信息」➔「编译编号」；OPPO / vivo 请进入「版本信息」➔「版本号」）。\n" +
            "   • 连续快速点击「版本号」7 次，直到屏幕提示「您已处于开发者模式！」（若提示输入锁屏密码请验证）。\n\n" +
            "2. 打开「USB 调试」：\n" +
            "   • 返回系统「设置」主界面，进入「系统与更新」➔「开发者选项」（或「更多设置」/「其他设置」➔「开发者选项」）。\n" +
            "   • 向下滚动找到并打开「USB 调试 (USB debugging)」开关，在弹出的确认框中点击「确定」。\n" +
            "   • （提示：小米等国产机型建议同时开启「USB 调试（安全设置）」，并将 USB 连接用途设为「传输文件 / MTP」）。\n\n" +
            "3. 在手机上确认授权：\n" +
            "   • 使用支持数据传输的 USB 线连接手机与电脑，点击顶部的「刷新设备」。\n" +
            "   • 保持手机屏幕解锁，在手机弹出的「允许 USB 调试吗？」窗口中勾选「始终允许使用这台计算机进行调试」，并点击「允许 / 确定」。",
            "1. 開啟「開發人員選項（開發者模式）」：\n" +
            "   • 打開 Android 手機或平板上的系統「設定 (Settings)」。\n" +
            "   • 進入「關於手機」（或「關於本機」/「我的裝置」）。\n" +
            "   • 找到「版本號碼 (Build number)」（小米澎湃 OS / MIUI 請點擊「OS 版本」或「MIUI 版本」；三星請進入「軟體資訊」➔「版本號碼」；OPPO / vivo 請進入「版本資訊」➔「版本號碼」）。\n" +
            "   • 連續快速點擊「版本號碼」7 次，直到畫面提示「您已成為開發人員！」（若提示輸入鎖定畫面密碼請完成驗證）。\n\n" +
            "2. 打開「USB 偵錯」：\n" +
            "   • 返回系統「設定」主畫面，進入「系統」➔「開發人員選項」（或「更多設定」/「進階設定」➔「開發人員選項」）。\n" +
            "   • 向下捲動找到並打開「USB 偵錯 (USB debugging)」開關，在彈出的確認視窗中點擊「確定」。\n" +
            "   • （提示：部分機型建議同時開啟「USB 偵錯（安全設定）」，並將 USB 用途設為「檔案傳輸 / MTP」）。\n\n" +
            "3. 在手機上確認授權：\n" +
            "   • 使用支援資料傳輸的 USB 傳輸線連接手機與電腦，點擊頂部的「重新整理裝置」。\n" +
            "   • 保持手機螢幕解鎖，在手機彈出的「允許 USB 偵錯嗎？」視窗中勾選「一律允許透過這台電腦進行偵錯」，並點擊「允許 / 確定」。");
        public string SettingsButton => T("Settings", "设置", "設定");

        // Wireless Companion & LocalSend Connect Dialog
        public string WirelessConnectDialogTitle => T("Connect to Wireless Companion or LocalSend", "连接无线伴侣或 LocalSend 设备", "連接無線伴侶或 LocalSend 裝置");
        public string WirelessConnectDialogDesc => T(
            "Enter the IP:Port of a SyncDroid Companion (default: 45821) or LocalSend device (default: 53317), or select an online device discovered on your LAN below.",
            "输入手机/设备端的 IP:端口（SyncDroid 伴侣默认 45821，LocalSend 默认 53317），或在下方直接选择局域网内发现的在线设备。",
            "輸入手機/裝置端的 IP:連接埠（SyncDroid 伴侶預設 45821，LocalSend 預設 53317），或在下方直接選擇區域網路內發現的線上裝置。");
        public string WirelessIpEndpointLabel => T("Device IP:Port Endpoint:", "设备端 IP:端口 地址：", "裝置端 IP:連接埠 位址：");
        public string WirelessIpEndpointPlaceholder => T("e.g. 192.168.1.50:45821 or 192.168.1.50:53317", "例如 192.168.1.50:45821 或 192.168.1.50:53317", "例如 192.168.1.50:45821 或 192.168.1.50:53317");
        public string ScanLanButton => T("Scan LAN", "扫描局域网", "掃描區域網路");
        public string DiscoveredPeersLabel => T("Discovered LAN Devices (Companion & LocalSend):", "局域网内发现的设备（无线伴侣 & LocalSend）：", "區域網路內發現的裝置（無線伴侶 & LocalSend）：");
        public string NoPeersFoundText => T(
            "No wireless companion or LocalSend devices discovered yet. Ensure devices are on the same Wi-Fi.",
            "暂未发现无线伴侣或 LocalSend 设备，请确保设备与电脑处于同一局域网。",
            "暫未發現無線伴侶或 LocalSend 裝置，請確保裝置與電腦處於同一區域網路。");
        public string ConnectButton => T("Connect", "连接", "連接");

        // Tabs
        public string TabPcToAndroid => "Push (/MIR mode)";
        public string TabAndroidToPc => "Pull (/MIR mode)";
        public string TabDirectTransfer => T("Direct Transfer (Files / Folders)", "文件直传 (单文件 / 文件夹)", "檔案直傳 (單檔案 / 資料夾)");
        public string TabReceive => T("Receive Files", "接收文件", "接收檔案");

        // SyncPage & Sync Modes
        public string PcDirectoryLabel => T("Windows PC Directory", "Windows 电脑文件夹路径", "Windows 電腦資料夾路徑");
        public string AndroidDirectoryLabel => T("Target Device Directory", "目标设备文件夹路径", "目標裝置資料夾路徑");
        public string SourceBadge => T("SOURCE", "源目录 (SOURCE)", "來源目錄 (SOURCE)");
        public string TargetBadge => T("TARGET", "目标目录 (TARGET)", "目標目錄 (TARGET)");
        public string MirrorSyncBanner => T("MIRROR SYNC (/MIR)", "镜像同步模式 (/MIR)", "鏡像同步模式 (/MIR)");
        public string IncrementalSyncBanner => T("INCREMENTAL MERGE", "增量合并模式 (MERGE)", "增量合併模式 (MERGE)");
        public string SyncModeLabel => T("Sync Mode:", "同步模式：", "同步模式：");
        public string MirrorModeOption => T("Mirror Sync (/MIR - Delete extra files on target)", "镜像同步 (/MIR - 清理目标端多余文件)", "鏡像同步 (/MIR - 清理目標端多餘檔案)");
        public string IncrementalModeOption => T("Incremental Merge (Keep existing files on target)", "增量合并 (保留目标端已有文件)", "增量合併 (保留目標端已有檔案)");
        public string PcPathPlaceholder => T("Select or type a PC folder path (e.g. D:\\MyFolder)", "选择或输入电脑文件夹路径 (例如 D:\\MyFolder)", "選擇或輸入電腦資料夾路徑 (例如 D:\\MyFolder)");
        public string AndroidPathPlaceholder => T("Select or type a target path (e.g. /storage/emulated/0/MyFolder or D:\\MyFolder)", "选择或输入目标设备路径 (例如 /storage/emulated/0/MyFolder 或 D:\\MyFolder)", "選擇或輸入目標裝置路徑 (例如 /storage/emulated/0/MyFolder 或 D:\\MyFolder)");
        public string BrowseButton => T("Browse...", "浏览...", "瀏覽...");
        public string StartSyncButton => T("Start Sync (MIR Mode)", "开始同步 (MIR 镜像)", "開始同步 (MIR 鏡像)");
        public string StartIncrementalSyncButton => T("Start Sync (Merge Mode)", "开始同步 (增量合并)", "開始同步 (增量合併)");
        public string CancelButton => T("Cancel", "取消", "取消");
        public string ActivityLogsHeader => T("Activity Logs", "同步活动日志", "同步活動日誌");
        public string ClearLogsButton => T("Clear", "清空", "清除");
        public string CopyLogsButton => T("Copy All Logs", "复制全部日志", "複製全部日誌");

        // Feature 1: Overlong Filename (> 255 Bytes) Dialog
        public string OverlongFileDialogTitle => T("Overlong Filenames Detected (> 255 Bytes)", "检测到超长文件名 (> 255 字节)", "偵測到超長檔案名稱 (> 255 位元組)");
        public string OverlongFileDialogMessage => T(
            "Found {0} file(s) whose UTF-8 filename length (including extension) exceeds Android's 255-byte limit and cannot be transferred to mobile storage.\n\n• Click 'Rename Files' to truncate local filenames from the end to < 255 bytes while preserving extensions.\n• Click 'Skip Files' to automatically ignore them during transfer.",
            "检测到 {0} 个文件的文件名（含扩展名）UTF-8 编码超过 255 字节，无法传输至移动设备存储。\n\n• 点击「修改文件名」将保留扩展名并从主文件名末尾向前逐字符截断至 < 255 字节（直接重命名本地源文件）。\n• 点击「略过文件」将在传输时自动跳过这些文件。",
            "偵測到 {0} 個檔案的檔案名稱（含副檔名）UTF-8 編碼超過 255 位元組，無法傳輸至行動裝置儲存空間。\n\n• 點擊「修改檔案名稱」將保留副檔名並從主檔名末尾向前逐字元截斷至 < 255 位元組（直接重新命名本機來源檔案）。\n• 點擊「略過檔案」將在傳輸時自動略過這些檔案。");
        public string OverlongFileRenameButton => T("Rename Files", "修改文件名", "修改檔案名稱");
        public string OverlongFileSkipButton => T("Skip Files", "略过文件", "略過檔案");
        public string OverlongFileRenamedLog => T("Renamed (> 255 bytes): {0}  ➔  {1}", "已重命名超长文件 (< 255 字节): {0}  ➔  {1}", "已重新命名超長檔案 (< 255 位元組): {0}  ➔  {1}");
        public string OverlongFileRenameFailedLog => T("Failed to rename {0}: {1}", "重命名超长文件失败 {0}: {1}", "重新命名超長檔案失敗 {0}: {1}");
        public string OverlongFileSkipSelectedLog => T("Marked {0} overlong file(s) (> 255 bytes) to be skipped during transfer.", "已标记略过 {0} 个文件名超过 255 字节的超长文件。", "已標記略過 {0} 個檔案名稱超過 255 位元組的超長檔案。");

        // Direct Transfer Tab (Device-neutral wording for ADB, Companion & LocalSend)
        public string DirectTransferQueueTitle => T("Transfer Queue (Drag & Drop Files or Folders)", "待传输队列（支持拖拽文件或文件夹至此处）", "待傳輸佇列（支援拖曳檔案或資料夾至此處）");
        public string DirectTransferEmptyHint => T(
            "Drag & drop files or folders from Windows Explorer here, or click 'Add Files...' / 'Add Folder...' above.",
            "从资源管理器拖拽文件或文件夹到此处，或点击上方「添加文件...」/「添加文件夹...」按钮。",
            "從檔案總管拖曳檔案或資料夾到此處，或點擊上方「新增檔案...」/「新增資料夾...」按鈕。");
        public string AddFilesButton => T("Add Files...", "添加文件...", "新增檔案...");
        public string AddFolderButton => T("Add Folder...", "添加文件夹...", "新增資料夾...");
        public string ClearQueueButton => T("Clear Queue", "清空队列", "清空佇列");
        public string TargetAndroidReceiveDirLabel => T("Target Device Receive Directory", "目标设备接收目录", "目標裝置接收目錄");
        public string TargetDeviceReceiveDirLabel => TargetAndroidReceiveDirLabel;
        public string LocalSendReceiverControlledDirHint => T(
            "Saved automatically to receiver's LocalSend download folder",
            "由接收端设备 LocalSend 自动保存至其下载目录",
            "由接收端裝置 LocalSend 自動儲存至其下載目錄");
        public string ConflictPolicyLabel => T("Conflict Policy:", "同名冲突策略：", "同名衝突策略：");
        public string ConflictSkipIdentical => T("Skip if identical size", "大小相同跳过", "大小相同略過");
        public string ConflictOverwrite => T("Overwrite existing", "强制覆盖", "強制覆蓋");
        public string ConflictKeepBoth => T("Keep both (auto-rename)", "自动重命名保留两者", "自動重新命名保留兩者");
        public string StartDirectPushButton => T("Push Queue to Device", "推送队列至设备", "推送佇列至裝置");
        public string PullFromAndroidButton => T("Pull Item from Device...", "从设备拉取文件/文件夹...", "從裝置拉取檔案/資料夾...");
        public string PullFromDeviceButton => PullFromAndroidButton;
        public string AndroidPullPickerTitle => T("Select Device File or Folder to Pull", "选择要拉取到电脑的设备文件或文件夹", "選擇要拉取到電腦的裝置檔案或資料夾");
        public string SelectItemPrimaryButton => T("Select Item", "选择此项", "選取此項目");
        public string LocalSendSyncTabNotSupported => T(
            "Folder mirror/merge sync requires ADB or SyncDroid Companion. For LocalSend devices, please use the 'Direct Transfer' tab.",
            "文件夹镜像/增量同步需要 ADB 或 SyncDroid 无线伴侣连接；当前选中的是 LocalSend 设备，请切换至「文件直传」标签页进行传输。",
            "資料夾鏡像/增量同步需要 ADB 或 SyncDroid 無線伴侶連接；目前選取的是 LocalSend 裝置，請切換至「檔案直傳」分頁進行傳輸。");
        public string LocalSendPullNotSupported => T(
            "LocalSend protocol only supports push initiated by the sender. Please send files directly from the LocalSend app on your device to this PC.",
            "LocalSend 协议不支持主动浏览远端目录拉取，请直接在对方设备的 LocalSend 中选择本电脑进行发送。",
            "LocalSend 協議不支援主動瀏覽遠端目錄拉取，請直接在對方裝置的 LocalSend 中選擇本電腦進行傳送。");

        // Status texts
        public string StatusReady => T("Ready", "就绪", "就緒");
        public string StatusSyncing => T("Syncing...", "正在同步...", "正在同步...");
        public string StatusTransferring => T("Transferring...", "正在传输...", "正在傳輸...");
        public string StatusCompleted => T("Sync Completed", "同步完成", "同步完成");
        public string StatusTransferCompleted => T("Transfer Completed", "传输完成", "傳輸完成");
        public string StatusCancelled => T("Cancelled", "已取消", "已取消");
        public string StatusFailed => T("Sync Failed", "同步失败", "同步失敗");
        public string StatusInvalidPaths => T("Failed: Invalid paths", "失败：路径无效", "失敗：路徑無效");
        public string StatusEmptyQueue => T("Queue is empty", "待传输队列为空", "待傳輸佇列為空");

        // Settings Page
        public string SettingsPageTitle => T("Application Settings", "应用设置", "應用程式設定");
        public string BackToSyncButton => T("Back to Sync", "返回同步界面", "返回同步介面");
        public string LanguageSectionTitle => T("Language / 语言", "界面语言 (Language)", "介面語言 (Language)");
        public string LanguageSectionDesc => T("Select display language for the application interface", "选择软件界面的显示语言（即时生效）", "選擇軟體介面的顯示語言（即時生效）");

        // Settings Page: Wireless Companion Card
        public string CompanionSectionTitle => T("Wireless Companion Service (LAN / Wi-Fi)", "无线伴侣服务配置 (Wireless Companion)", "無線伴侶服務設定 (Wireless Companion)");
        public string CompanionSectionDesc => T(
            "Allow SyncDroid on Android to discover this PC over LAN, browse PC drives, and initiate wireless sync or direct file sharing without ADB",
            "允许手机端 SyncDroid 在局域网内自动发现此电脑、浏览电脑磁盘并主动发起无线同步或文件投送（无需配置 ADB）",
            "允許手機端 SyncDroid 在區域網路內自動發現此電腦、瀏覽電腦磁碟並主動發起無線同步或檔案投送（無需設定 ADB）");
        public string CompanionEnableServerLabel => T("Enable PC Wireless Companion Listener", "开启电脑端无线伴侣后台监听服务", "開啟電腦端無線伴侶背景監聽服務");
        public string CompanionPortLabel => T("Listening TCP Port (UDP Discovery: 45820):", "监听 TCP 端口（UDP 发现端口固定为 45820）：", "監聽 TCP 連接埠（UDP 發現連接埠固定為 45820）：");
        public string CompanionLocalIpLabel => T("This PC's LAN Endpoint(s):", "本机局域网连接地址（供手机端连接）：", "本機區域網路連接位址（供手機端連接）：");
        public string CompanionRestartServerButton => T("Apply & Restart Listener", "应用并重启监听", "套用並重啟監聽");
        public string CopyIpButton => T("Copy Endpoint", "复制地址", "複製位址");

        // Settings Page: LocalSend Card
        public string LocalSendSectionTitle => T("LocalSend Protocol Service (UDP/TCP 53317)", "LocalSend 协议服务配置 (UDP/TCP 53317)", "LocalSend 協議服務設定 (UDP/TCP 53317)");
        public string LocalSendSectionDesc => T(
            "Discover and transfer files with any device running LocalSend on the same LAN in the Direct Transfer tab",
            "支持在「文件直传」中自动发现局域网内的 LocalSend 设备并双向互传文件",
            "支援在「檔案直傳」中自動發現區域網路內的 LocalSend 裝置並雙向互傳檔案");
        public string LocalSendEnableServerLabel => T("Enable LocalSend Discovery & Receiver (Port 53317)", "开启 LocalSend 局域网发现与文件接收服务 (端口 53317)", "開啟 LocalSend 區域網路發現與檔案接收服務 (連接埠 53317)");
        public string LocalSendReceiveDirLabel => T("LocalSend Receive Folder on PC:", "本机 LocalSend 接收保存目录：", "本機 LocalSend 接收儲存目錄：");
        public string LocalSendAccessForbiddenPrompt => T(
            "Access is forbidden (Port 53317 in use): Please close the existing LocalSend software to use this feature",
            "端口访问被拒绝 (Access is forbidden)：请关闭已存在的 LocalSend 软件以使用此功能",
            "連接埠存取被拒絕 (Access is forbidden)：請關閉已存在的 LocalSend 軟體以使用此功能");
        public string LocalSendAccessForbiddenDialogTitle => T("LocalSend Port Conflict", "LocalSend 端口冲突提示", "LocalSend 連接埠衝突提示");
        public string LocalSendAccessForbiddenDialogMessage => T(
            "Failed to start LocalSend service on port 53317 (Access is forbidden).\n\nPlease close the existing LocalSend application running on your PC to use this feature.",
            "启动 LocalSend 协议服务失败（Access is forbidden）。\n\n请先关闭并退出已存在的 LocalSend 软件以使用此功能。",
            "啟動 LocalSend 協議服務失敗（Access is forbidden）。\n\n請先關閉並退出已存在的 LocalSend 軟體以使用此功能。");

        // Settings Page: ADB Card
        public string AdbSectionTitle => T("ADB Configuration", "ADB 路径配置", "ADB 路徑設定");
        public string AdbSectionDesc => T("Configure how SyncDroid locates and launches the Android Debug Bridge (adb.exe)", "配置软件如何查找并调用 Android 调试桥程序 (adb.exe)", "設定軟體如何尋找並呼叫 Android 偵錯橋接程式 (adb.exe)");
        public string AdbOptionBuiltInOrRunning => T("Built-in ADB / Prioritize already-running ADB (Highest Priority, Default)", "内置 ADB / 优先调用已拉起的 ADB（最高优先级，默认）", "內建 ADB / 優先呼叫已啟動的 ADB（最高優先級，預設）");
        public string AdbOptionPath => T("Use ADB from system PATH environment variable", "使用系统环境变量 PATH 中的 ADB 程序", "使用系統環境變數 PATH 中的 ADB 程式");
        public string AdbOptionCustom => T("Specify custom ADB directory", "指定自定义 ADB 所在文件夹目录", "指定自訂 ADB 所在資料夾目錄");
        public string AdbCustomDirLabel => T("Custom ADB Folder Path:", "自定义 ADB 目录路径：", "自訂 ADB 目錄路徑：");
        public string AdbCustomDirPlaceholder => T("e.g. C:\\platform-tools (folder containing adb.exe)", "例如 C:\\platform-tools（包含 adb.exe 的文件夹）", "例如 C:\\platform-tools（包含 adb.exe 的資料夾）");
        public string AdbKillBuiltInOnExitTitle => T("Kill built-in ADB process on exit", "退出软件时杀死内置 ADB 进程", "退出軟體時終止內建 ADB 行程");
        public string AdbKillBuiltInOnExitDesc => T("If using built-in ADB, automatically terminate the adb.exe process when exiting the application (Default: On)", "若当前使用的是内置 ADB，在退出软件时自动杀死 adb.exe 进程（默认开启）", "若目前使用的是內建 ADB，在退出軟體時自動終止 adb.exe 行程（預設開啟）");
        public string TestAdbButton => T("Verify & Restart ADB", "验证并重连 ADB", "驗證並重連 ADB");

        // Settings Page: Post-Transfer Hash Verification Card
        public string HashVerifySectionTitle => T("Post-Transfer Hash Verification", "传输后 Hash 比对校验", "傳輸後 Hash 比對校驗");
        public string HashVerifySectionDesc => T(
            "Perform a SHA-256 hash comparison after file transfer completes to prevent corrupted or incomplete files caused by interruptions (Default: On)",
            "传输完后进行一次 SHA-256 Hash 比对，防止中断时出现文件错误或损坏（默认开启）",
            "傳輸完後進行一次 SHA-256 Hash 比對，防止中斷時出現檔案錯誤或損毀（預設開啟）");

        // ADB track-devices Unauthorized Dialog
        public string AdbUnauthorizedDialogTitle => T("USB Debugging Authorization Required", "需要在手机上操作授权 USB 调试", "需要在手機上操作授權 USB 偵錯");
        public string AdbUnauthorizedDialogMessage => T(
            "ADB monitor (adb track-devices) detected device \"{0}\" in an unauthorized state (unauthorized).\n\nPlease unlock your phone screen, check \"Always allow from this computer\" in the USB debugging authorization popup on your phone, and tap \"Allow / OK\".",
            "ADB 状态监控 (adb track-devices) 读取到设备 \"{0}\" 当前状态为未授权 (unauthorized)。\n\n请解锁您的手机屏幕，在手机弹出的「允许 USB 调试吗？」授权窗口中勾选「始终允许使用这台计算机进行调试」，并点击「允许 / 确定」。",
            "ADB 狀態監控 (adb track-devices) 讀取到裝置 \"{0}\" 目前狀態為未授權 (unauthorized)。\n\n請解鎖您的手機螢幕，在手機彈出的「允許 USB 偵錯嗎？」授權視窗中勾選「一律允許透過這台電腦進行偵錯」，並點擊「允許 / 確定」。");
        public string AdbUnauthorizedDialogOkButton => T("OK", "我知道了", "我知道了");

        // About This App Card
        public string AboutSectionTitle => T("About This App", "关于此应用", "關於此應用程式");
        public string AboutAppVersion => "Version 0.2.0.0 beta";
        public string AboutAppDescription => T(
            "SyncDroid is a lightweight, CJK-safe folder synchronization (/MIR & Incremental Merge) and direct file transfer tool supporting USB ADB, Wireless Companion, and LocalSend.",
            "SyncDroid 是一款轻量级、完美支持中文路径的双向文件夹同步（/MIR 镜像与增量合并）及文件直传工具，支持 USB ADB、无线伴侣与 LocalSend 协议。",
            "SyncDroid 是一款輕量級、完美支援中文路徑的雙向資料夾同步（/MIR 鏡像與增量合併）及檔案直傳工具，支援 USB ADB、無線伴侶與 LocalSend 協議。");
        public string AboutDeveloperInfo => T(
            "Developer: Yiren Li  •  Open Source under MIT License",
            "开发者：Yiren Li  •  基于 MIT 协议开源",
            "開發者：Yiren Li  •  基於 MIT 協議開源");
        public string AboutGithubLinkText => T("GitHub Repository (Source Code)", "GitHub 源代码仓库", "GitHub 原始碼倉庫");
        public string AboutPrivacyLinkText => T("Privacy Policy", "隐私政策 (Privacy Policy)", "隱私權政策 (Privacy Policy)");

        // Android Folder Picker Dialog
        public string AndroidPickerTitle => T("Browse Target Storage (/storage & Windows Drives)", "浏览目标存储目录 (/storage 与 Windows 盘符)", "瀏覽目標儲存目錄 (/storage 與 Windows 磁碟機)");
        public string RefreshTreeButton => T("Refresh", "刷新", "重新整理");
        public string NewFolderButton => T("New Folder", "新建文件夹", "新增資料夾");
        public string NewFolderPlaceholder => T("Enter new folder name...", "输入新文件夹名称...", "輸入新資料夾名稱...");
        public string CreateButton => T("Create", "创建", "建立");
        public string SelectedPathLabel => T("Selected Path:", "当前选中路径：", "目前選取路徑：");
        public string SelectFolderPrimaryButton => T("Select Folder", "选择此文件夹", "選取此資料夾");
        public string NoDeviceConnectedError => T(
            "No online device connected. Please connect via USB ADB or click 'Wireless Connect'.",
            "未检测到已连接的设备，请连接 USB ADB 或点击“无线连接”。",
            "未偵測到已連接的裝置，請連接 USB ADB 或點擊「無線連接」。");

        // Receive Page
        public string ReceivePageTitle => T("Incoming File Receiver", "文件接收中心", "檔案接收中心");
        public string ReceiveSenderLabel => T("Sender Device:", "发送方设备：", "傳送方裝置：");
        public string ReceiveNoActiveSender => T("Waiting for incoming transfers...", "等待其他设备发送文件...", "等待其他裝置傳送檔案...");
        public string ReceiveSaveDirLabel => T("Save Directory:", "保存目录：", "儲存目錄：");
        public string OpenReceiveFolderButton => T("Open Folder", "打开目录", "打開目錄");
        public string ReceiveFilesHeader => T("Incoming Files (File Names)", "接收文件列表（文件档名）", "接收檔案清單（檔案檔名）");
        public string ReceiveEmptyFilesHint => T(
            "No incoming files yet. When another LocalSend or SyncDroid device sends files to this PC, it will automatically switch to this page and display the file names here.",
            "暂无接收文件。当其他 LocalSend 或 SyncDroid 设备向本机发送数据时，将自动切换至此页面并显示文件档名与接收进度。",
            "暫無接收檔案。當其他 LocalSend 或 SyncDroid 裝置向本機傳送資料時，將自動切換至此頁面並顯示檔案檔名與接收進度。");
        public string ClearReceiveListButton => T("Clear List", "清空列表", "清空清單");
        public string ReceivePendingConfirmBannerFormat => T(
            "Incoming transfer request from \"{0}\" ({1} file(s)). Confirm to accept and authenticate this device:",
            "收到来自「{0}」的文件传输请求（共 {1} 个文件），请确认是否接受并认证该设备：",
            "收到來自「{0}」的檔案傳輸請求（共 {1} 個檔案），請確認是否接受並認證該裝置：");
        public string AcceptTransferButton => T("Accept & Authenticate", "接受并认证", "接受並認證");
        public string DeclineTransferButton => T("Decline", "拒绝", "拒絕");
        public string ReceiveStatusPending => T("Pending Confirmation", "等待确认", "等待確認");
        public string ReceiveStatusWaitingUpload => T("Accepted, waiting...", "已接受，等待传输...", "已接受，等待傳輸...");
        public string ReceiveStatusReceiving => T("Receiving...", "正在接收...", "正在接收...");
        public string ReceiveStatusCompleted => T("Completed", "接收完成", "接收完成");
        public string ReceiveStatusSkipped => T("Skipped (Identical)", "已跳过 (相同文件)", "已略過 (相同檔案)");
        public string ReceiveStatusDeclined => T("Declined", "已拒绝", "已拒絕");
        public string ReceiveStatusFailed => T("Failed", "接收失败", "接收失敗");

        // First-Time Connection & LocalSend Receive Confirmation Dialogs
        public string FirstTimeConnectDialogTitle => T("First-Time Device Connection Confirmation", "设备初次连接确认", "裝置初次連接確認");
        public string FirstTimeSyncDroidConnectMessage => T(
            "Connecting with SyncDroid device \"{0}\" ({1}) for the first time.\n\nDo you want to confirm this connection and add it to your authenticated devices?",
            "检测到与 SyncDroid 设备「{0} ({1})」初次连接。\n\n是否确认连接并将该设备加入已认证设备列表？",
            "偵測到與 SyncDroid 裝置「{0} ({1})」初次連接。\n\n是否確認連接並將該裝置加入已認證裝置清單？");
        public string FirstTimeLocalSendReceiveTitle => T("LocalSend Transfer Request Confirmation", "LocalSend 接收请求确认", "LocalSend 接收請求確認");
        public string FirstTimeLocalSendReceiveMessage => T(
            "LocalSend device \"{0}\" ({1}) is requesting to send {2} file(s) to this PC:\n\n{3}\n\nDo you want to accept this transfer request and authenticate the device?",
            "LocalSend 设备「{0} ({1})」请求向本机发送 {2} 个文件：\n\n{3}\n\n是否接受此次传输请求并认证该设备？",
            "LocalSend 裝置「{0} ({1})」請求向本機傳送 {2} 個檔案：\n\n{3}\n\n是否接受此次傳輸請求並認證該裝置？");
        public string ConfirmAcceptButton => T("Confirm & Accept", "确认接受", "確認接受");
        public string ConfirmRejectButton => T("Decline", "拒绝", "拒絕");

        // Settings & Manage Authenticated Devices Page
        public string TrustedDevicesSectionTitle => T("Authenticated Devices Management", "已认证设备管理", "已認證裝置管理");
        public string TrustedDevicesSectionDesc => T(
            "View and manage SyncDroid and LocalSend devices that have been confirmed and authenticated on first connection",
            "查看并管理在初次连接或接收时已通过确认的 SyncDroid 与 LocalSend 认证设备",
            "查看並管理在初次連接或接收時已通過確認的 SyncDroid 與 LocalSend 認證裝置");
        public string ManageTrustedDevicesButton => T("Manage Authenticated Devices", "管理认证设备", "管理認證裝置");
        public string ManageDevicesPageTitle => T("Manage Authenticated Devices", "管理认证设备", "管理認證裝置");
        public string ManageDevicesPageDesc => T(
            "Devices listed below have been confirmed by you. Deleting a device will require user confirmation again the next time it connects or sends files.",
            "以下设备已通过您的初次连接或接收确认。删除已认证设备后，下次与该设备连接或接收其发送的数据时将重新需要您进行确认。",
            "以下裝置已通過您的初次連接或接收確認。刪除已認證裝置後，下次與該裝置連接或接收其傳送的資料時將重新需要您進行確認。");
        public string BackToSettingsButton => T("Back to Settings", "返回设置", "返回設定");
        public string RemoveAllDevicesButton => T("Remove All", "全部删除", "全部刪除");
        public string DeleteDeviceButton => T("Delete", "删除", "刪除");
        public string NoTrustedDevicesHint => T(
            "No authenticated devices yet. Devices confirmed during first-time connection or file reception will appear here.",
            "暂无已认证的设备。在两台设备初次连接或接收文件时确认通过的设备将显示在此处。",
            "暫無已認證的裝置。在兩台裝置初次連接或接收檔案時確認通過的裝置將顯示在此處。");
        public string TrustedDeviceTimePrefix => T("Authenticated: ", "认证时间：", "認證時間：");

        public void NotifyAll()
        {
            OnPropertyChanged(string.Empty);
        }
    }
}
