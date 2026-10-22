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
        public string AppSubtitle => T("Android ADB Mirror Sync", "Android ADB 镜像同步工具", "Android ADB 鏡像同步工具");
        public string AndroidDeviceLabel => T("Android Device", "Android 设备", "Android 裝置");
        public string NoDevicePlaceholder => T("No online ADB device", "未检测到在线 ADB 设备", "未偵測到線上 ADB 裝置");
        public string RefreshAdbButton => T("Refresh ADB", "刷新 ADB", "重新整理 ADB");
        public string SettingsButton => T("Settings", "设置", "設定");

        // Tabs
        public string TabPcToAndroid => T("PC  ➔  Android (Push Mirror)", "电脑  ➔  Android (推送镜像)", "電腦  ➔  Android (推送鏡像)");
        public string TabAndroidToPc => T("Android  ➔  PC (Pull Mirror)", "Android  ➔  电脑 (拉取镜像)", "Android  ➔  電腦 (拉取鏡像)");

        // SyncPage
        public string PcDirectoryLabel => T("Windows PC Directory", "Windows 电脑文件夹路径", "Windows 電腦資料夾路徑");
        public string AndroidDirectoryLabel => T("Android Device Directory", "Android 设备文件夹路径", "Android 裝置資料夾路徑");
        public string SourceBadge => T("SOURCE", "源目录 (SOURCE)", "來源目錄 (SOURCE)");
        public string TargetBadge => T("TARGET", "目标目录 (TARGET)", "目標目錄 (TARGET)");
        public string MirrorSyncBanner => T("MIRROR SYNC (/MIR)", "镜像同步模式 (/MIR)", "鏡像同步模式 (/MIR)");
        public string PcPathPlaceholder => T("Select or type a PC folder path (e.g. D:\\MyFolder)", "选择或输入电脑文件夹路径 (例如 D:\\MyFolder)", "選擇或輸入電腦資料夾路徑 (例如 D:\\MyFolder)");
        public string AndroidPathPlaceholder => T("Select or type an Android folder path (e.g. /sdcard/Pictures/MyFolder)", "选择或输入 Android 文件夹路径 (例如 /sdcard/Pictures/MyFolder)", "選擇或輸入 Android 資料夾路徑 (例如 /sdcard/Pictures/MyFolder)");
        public string BrowseButton => T("Browse...", "浏览...", "瀏覽...");
        public string StartSyncButton => T("Start Sync (MIR Mode)", "开始同步 (MIR 镜像)", "開始同步 (MIR 鏡像)");
        public string CancelButton => T("Cancel", "取消", "取消");
        public string ActivityLogsHeader => T("Activity Logs", "同步活动日志", "同步活動日誌");
        public string ClearLogsButton => T("Clear", "清空", "清除");
        public string CopyLogsButton => T("Copy All Logs", "复制全部日志", "複製全部日誌");

        // Status texts
        public string StatusReady => T("Ready", "就绪", "就緒");
        public string StatusSyncing => T("Syncing...", "正在同步...", "正在同步...");
        public string StatusCompleted => T("Sync Completed", "同步完成", "同步完成");
        public string StatusCancelled => T("Cancelled", "已取消", "已取消");
        public string StatusFailed => T("Sync Failed", "同步失败", "同步失敗");
        public string StatusInvalidPaths => T("Failed: Invalid paths", "失败：路径无效", "失敗：路徑無效");

        // Settings Page
        public string SettingsPageTitle => T("Application Settings", "应用设置", "應用程式設定");
        public string BackToSyncButton => T("Back to Sync", "返回同步界面", "返回同步介面");
        public string LanguageSectionTitle => T("Language / 语言", "界面语言 (Language)", "介面語言 (Language)");
        public string LanguageSectionDesc => T("Select display language for the application interface", "选择软件界面的显示语言（即时生效）", "選擇軟體介面的顯示語言（即時生效）");
        public string AdbSectionTitle => T("ADB Configuration", "ADB 路径配置", "ADB 路徑設定");
        public string AdbSectionDesc => T("Configure how SyncDroid locates and launches the Android Debug Bridge (adb.exe)", "配置软件如何查找并调用 Android 调试桥程序 (adb.exe)", "設定軟體如何尋找並呼叫 Android 偵錯橋接程式 (adb.exe)");
        public string AdbOptionPath => T("Use ADB from system PATH environment variable (Default)", "使用系统环境变量 PATH 中的 ADB 程序（默认）", "使用系統環境變數 PATH 中的 ADB 程式（預設）");
        public string AdbOptionCustom => T("Specify custom ADB directory", "指定自定义 ADB 所在文件夹目录", "指定自訂 ADB 所在資料夾目錄");
        public string AdbCustomDirLabel => T("Custom ADB Folder Path:", "自定义 ADB 目录路径：", "自訂 ADB 目錄路徑：");
        public string AdbCustomDirPlaceholder => T("e.g. C:\\platform-tools (folder containing adb.exe)", "例如 C:\\platform-tools（包含 adb.exe 的文件夹）", "例如 C:\\platform-tools（包含 adb.exe 的資料夾）");
        public string TestAdbButton => T("Verify & Restart ADB", "验证并重连 ADB", "驗證並重連 ADB");

        // About This App Card
        public string AboutSectionTitle => T("About This App", "关于此应用", "關於此應用程式");
        public string AboutAppVersion => "Version 0.1.1.1 Release";
        public string AboutAppDescription => T(
            "SyncDroid is a lightweight, CJK-safe folder mirror synchronization tool (/MIR) between Windows PC and Android devices over ADB.",
            "SyncDroid 是一款轻量级、完美支持中文路径的 Windows 电脑与 Android 设备 ADB 文件夹镜像同步工具 (/MIR)。",
            "SyncDroid 是一款輕量級、完美支援中文路徑的 Windows 電腦與 Android 裝置 ADB 資料夾鏡像同步工具 (/MIR)。");
        public string AboutDeveloperInfo => T(
            "Developer: Yiren Li  •  Open Source under MIT License",
            "开发者：Yiren Li  •  基于 MIT 协议开源",
            "開發者：Yiren Li  •  基於 MIT 協議開源");
        public string AboutGithubLinkText => T("GitHub Repository (Source Code)", "GitHub 源代码仓库", "GitHub 原始碼倉庫");
        public string AboutPrivacyLinkText => T("Privacy Policy", "隐私政策 (Privacy Policy)", "隱私權政策 (Privacy Policy)");

        // Android Folder Picker Dialog
        public string AndroidPickerTitle => T("Browse Android Storage (/storage)", "浏览 Android 存储目录 (/storage)", "瀏覽 Android 儲存目錄 (/storage)");
        public string RefreshTreeButton => T("Refresh", "刷新", "重新整理");
        public string NewFolderButton => T("New Folder", "新建文件夹", "新增資料夾");
        public string NewFolderPlaceholder => T("Enter new folder name...", "输入新文件夹名称...", "輸入新資料夾名稱...");
        public string CreateButton => T("Create", "创建", "建立");
        public string SelectedPathLabel => T("Selected Path:", "当前选中路径：", "目前選取路徑：");
        public string SelectFolderPrimaryButton => T("Select Folder", "选择此文件夹", "選取此資料夾");
        public string NoDeviceConnectedError => T("No online ADB device connected. Please connect a device and click Refresh ADB.", "未检测到已连接的 ADB 设备，请连接设备并点击“刷新 ADB”。", "未偵測到已連接的 ADB 裝置，請連接裝置並點擊「重新整理 ADB」。");

        public void NotifyAll()
        {
            OnPropertyChanged(string.Empty);
        }
    }
}
