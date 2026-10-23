# Privacy Policy for SyncDroid / SyncDroid 隐私政策

**Last Updated / 最后更新日期: October 2026**

---

## 中文说明

1. **无数据收集与上传**  
   **SyncDroid** 是一款完全在本地运行的 Windows 电脑与 Android 设备文件夹镜像同步工具。本应用**不会**收集、存储、上传、分析或向任何第三方分享您的任何个人身份信息、文件内容、目录结构、设备标识符或使用遥测数据。

2. **本地数据处理与权限说明 (`runFullTrust`)**  
   本应用声明了 Windows 桌面应用所需的 `runFullTrust` 权限，仅用于以下本地功能：
   - 调用您电脑本地的 Android 调试桥程序（`adb.exe`）与通过 USB 或局域网连接的 Android 设备通信；
   - 读取或写入您在同步任务中主动选择的本地电脑文件夹与 Android 设备文件夹。  
   所有文件对比与传输均严格在您的电脑与您的 Android 设备之间直接完成，不经过任何外部服务器。

3. **本地配置存储**  
   您的界面语言偏好、ADB 路径设置以及最近使用的文件夹路径历史仅保存在您电脑本地的应用数据目录中（`%LocalAppData%\SyncDroidWinUI\settings.json`），不会同步至云端，您可随时在本地查看或删除该文件。

4. **政策更新与联系方式**  
   如本隐私政策发生变更，我们将在此页面同步更新。如有任何关于隐私保护的疑问，请通过 GitHub Issues（[https://github.com/Hydro3ia/SyncDroid/issues](https://github.com/Hydro3ia/SyncDroid/issues)）或 Microsoft Store 页面上的开发者联系方式与我们取得联系。

---

## English

1. **No Data Collection or Transmission**  
   **SyncDroid** is a strictly local folder mirror synchronization utility between Windows PCs and Android devices. This application **does not** collect, transmit, store, analyze, or share any personal information, file contents, directory structures, device identifiers, or telemetry data with the developer or any third party.

2. **Local Processing & Capabilities (`runFullTrust`)**  
   This application declares the `runFullTrust` capability required by WinUI 3 desktop applications solely to:
   - Launch the local Android Debug Bridge executable (`adb.exe`) to communicate with your connected Android device;
   - Read from and write to the local PC folders and Android device folders explicitly selected by you for synchronization.  
   All file scanning and transfers take place directly between your Windows PC and your Android device without passing through any external servers.

3. **Local Configuration Storage**  
   User preferences (including display language, custom ADB directory configuration, and recent folder path history) are stored exclusively on your local device at `%LocalAppData%\SyncDroidWinUI\settings.json`. You can clear or delete these local settings at any time.

4. **Changes & Contact**  
   Any updates to this privacy policy will be posted on this page. If you have any questions or concerns regarding privacy, please open an issue at [https://github.com/Hydro3ia/SyncDroid/issues](https://github.com/Hydro3ia/SyncDroid/issues) or contact the developer via the support information listed on the Microsoft Store page.
