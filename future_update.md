# SyncDroid WinUI — Future Update Roadmap (`future_update.md`)

本文档记录计划在下一版本（`> 0.1.3.0`，即 `0.1.4.0`）中合入的新功能需求、确认的设计决策及详细实现方案。

---

## 功能 1：在「文件直传 (Direct Transfer)」中添加对 LocalSend 协议的支持

### 1. 背景与目标
- **背景**：LocalSend 是目前跨平台（Android、iOS/iPadOS、Windows、macOS、Linux）生态中最通用的开源局域网无网文件直传协议（Protocol v2.1）。
- **目标**：在 `SyncDroidWinUI` 的 **「文件直传 (Direct Transfer)」** 模块中原生支持 **LocalSend 协议（v2.1）**，使 Windows 端不仅可以通过 `ADB` 和 `SyncDroid 无线伴侣协议` 传输文件，还能直接与局域网内任何运行 **LocalSend** 客户端的手机、平板或电脑双向互传文件与文件夹：
  1. **作为发送端（PC ➔ LocalSend 设备）**：将「文件直传」队列中的单文件、多文件或文件夹推送至局域网内的 LocalSend 设备。
  2. **作为接收端（LocalSend 设备 ➔ PC）**：在后台监听 LocalSend 协议端口（默认 `53317`），使手机或其他设备上的 LocalSend 客户端能够直接搜索到本机 `SyncDroid` 并投送文件到本地指定接收目录。

---

### 2. LocalSend 通信协议规范（对齐 LocalSend Protocol v2.1）

#### 2.1 局域网发现协议（UDP 多播 + HTTP 注册）
- **默认多播地址与端口**：
  - 多播组 IP：`224.0.0.167`
  - 端口：`53317`（UDP 多播发现 + TCP HTTP/HTTPS 控制与传输）
- **设备信息模型 (`LocalSendDeviceInfoDto`)**：
  ```json
  {
    "alias": "DESKTOP-PC (SyncDroid)",
    "version": "2.1",
    "deviceModel": "Windows PC",
    "deviceType": "desktop",
    "fingerprint": "<SHA256_Cert_Or_Random_Fingerprint>",
    "port": 53317,
    "protocol": "https",
    "download": false,
    "announce": true
  }
  ```
- **发现与回应机制**：
  1. **UDP 多播探测**：
     - `SyncDroidWinUI` 加入多播组 `224.0.0.167:53317`。
     - 当点击“刷新设备”或进入局域网扫描时，向 `224.0.0.167:53317` 发送 `announce: true` 的 UDP JSON 广播包。
     - 当收到局域网内其他 LocalSend 设备发来的 `announce: true` 多播包时，将其加入在线设备列表，并向对端发起 `POST /api/localsend/v2/register`（若失败则回退发送 `announce: false` 的 UDP 包）完成双向发现。
  2. **HTTP 主动探测与手动 IP 连接**：
     - 支持通过 `GET /api/localsend/v2/info` 与 `POST /api/localsend/v2/register` 探测指定 `IP:53317` 节点，解决部分路由器隔离 UDP 多播的问题。

#### 2.2 HTTP / HTTPS REST 传输协议（默认 TCP 端口：`53317`）
LocalSend 客户端默认启用自签名 TLS 证书（`protocol: "https"`）或明文 HTTP（`protocol: "http"`）。`SyncDroidWinUI` 作为客户端连接时需配置 `HttpClientHandler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true` 以信任 LocalSend 节点的自签名证书；作为服务端监听时支持自动生成内存自签名 X509 证书（兼顾 HTTPS 与 HTTP 请求）。

| 接口路径 | 方法 | 查询参数 (`QueryString`) | 功能说明 |
| :--- | :--- | :--- | :--- |
| `/api/localsend/v2/info` | `GET` | 无 | 获取设备基本信息（`alias`、`version`、`deviceModel`、`deviceType`、`fingerprint`、`port`、`protocol`） |
| `/api/localsend/v2/register` | `POST` | 无 | 接收对端设备注册信息，将其加入在线设备列表，并返回本机设备信息 |
| `/api/localsend/v2/prepare-upload` | `POST` | `pin=<可选PIN>` | 发送文件前的握手请求，提交发送方 `info` 与待传文件字典 `files`（含 `id`、`fileName`、`size`、`fileType`、`metadata`），返回 `sessionId` 与每个文件的上传 `token` |
| `/api/localsend/v2/upload` | `POST` | `sessionId=<sid>&fileId=<fid>&token=<tok>` | 以 `application/octet-stream` 流式上传单个文件二进制内容 |
| `/api/localsend/v2/cancel` | `POST` | `sessionId=<sid>` | 取消当前传输会话 |

#### 2.3 核心 DTO 数据结构（新增 `Models/LocalSendModels.cs`）
```csharp
public class LocalSendInfoDto
{
    public string Alias { get; set; } = "";
    public string Version { get; set; } = "2.1";
    public string DeviceModel { get; set; } = "Windows PC";
    public string DeviceType { get; set; } = "desktop";
    public string Fingerprint { get; set; } = "";
    public int Port { get; set; } = 53317;
    public string Protocol { get; set; } = "http";
    public bool Download { get; set; } = false;
    public bool? Announce { get; set; }
}

public class LocalSendFileDto
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = ""; // 支持相对路径如 "Folder/Sub/file.txt"
    public long Size { get; set; }
    public string FileType { get; set; } = "application/octet-stream";
    public string? Sha256 { get; set; }
    public string? Preview { get; set; }
    public LocalSendFileMetadataDto? Metadata { get; set; }
}

public class LocalSendFileMetadataDto
{
    public string? Modified { get; set; } // ISO-8601 UTC 时间字符串
}

public class LocalSendPrepareUploadRequestDto
{
    public LocalSendInfoDto Info { get; set; } = new();
    public Dictionary<string, LocalSendFileDto> Files { get; set; } = new();
}

public class LocalSendPrepareUploadResponseDto
{
    public string SessionId { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new(); // fileId -> token
}
```

---

### 3. 「文件直传」对 LocalSend 的交互与业务流程

#### 3.1 设备识别与选择
1. **`ConnectionMode` 扩展**：
   - 在 `Models/CompanionModels.cs` 的 `ConnectionMode` 枚举中新增 `LocalSend = 2`：
     - `ConnectionMode.Adb` ➔ 显示 `[ADB] Pixel 9 Pro (xxxx)`
     - `ConnectionMode.WirelessCompanion` ➔ 显示 `[LAN] Pixel 9 Pro (192.168.1.x:45821)`
     - `ConnectionMode.LocalSend` ➔ 显示 `[LocalSend] iPhone / Android / PC (192.168.1.x:53317)`
2. **模式能力边界处理**：
   - 当顶部当前选中的设备为 `[LocalSend]` 时：
     - **Tab 3「文件直传 (Direct Transfer)」**：完全可用。因 LocalSend 协议由接收端决定保存位置，远端接收目录栏自动提示“由接收端 LocalSend 决定保存位置”并跳过远端目录树浏览；点击“推送队列至设备”时走 LocalSend `prepare-upload` + `upload` 流程。
     - **Tab 1 / Tab 2（文件夹镜像/增量同步）**：由于 LocalSend 协议不提供远端任意目录树扫描与删除接口（`/storage/tree`、`/file/delete`），若用户在选中 `[LocalSend]` 设备时点击 Tab 1 / Tab 2 的“开始同步”，则在日志与状态栏给出多语言友好提示，引导用户切换至「文件直传」标签页或使用 ADB / SyncDroid 无线伴侣连接。

#### 3.2 发送队列至 LocalSend 设备流程（`PC ➔ LocalSend 设备`）
1. 展开「文件直传」队列中的文件与文件夹（文件夹内文件使用 `FolderName/RelativePath` 作为 `FileName`，使对端 LocalSend 自动还原文件夹结构）。
2. 执行超长文件名（`> 255` 字节）预检（若目标设备为移动端可避免接收失败）。
3. 调用目标设备 `POST /api/localsend/v2/prepare-upload`：
   - 在日志中输出：`[LocalSend] Waiting for target device ({peer.Alias}) to accept transfer...`
   - 若返回 `204 No Content`：目标端无需传输或全部跳过，直接完成。
   - 若返回 `403 Forbidden`：提示目标设备拒绝了接收请求。
   - 若返回 `200 OK`：解析 `sessionId` 及文件 `token` 映射表。
4. 按序遍历 `files` 字典，调用 `POST /api/localsend/v2/upload?sessionId=...&fileId=...&token=...` 流式发送每个文件，实时更新进度条与 `Activity Logs`。
5. 若用户点击「取消」，取消上传流并调用 `POST /api/localsend/v2/cancel?sessionId=...`。

#### 3.3 从 LocalSend 设备接收文件流程（`LocalSend 设备 ➔ PC`）
1. 在设置页「无线伴侣与 LocalSend 服务配置」中支持开启本机 LocalSend 接收服务（默认端口 `53317`）及配置默认本地接收文件夹（默认 `Downloads\SyncDroid`）。
2. 当局域网内其他设备的 LocalSend 向本机发起 `POST /api/localsend/v2/prepare-upload` 时：
   - 根据设置（自动接收 vs 弹窗确认接收）决定是否放行；
   - 在随后的 `/api/localsend/v2/upload` 中将文件流写入本地接收目录的 `.syncdroid_<guid>.part` 临时文件，按当前设定的同名冲突策略（大小相同跳过 / 强制覆盖 / 自动重命名保留两者）落盘，并在「文件直传」的 `Activity Logs` 中实时打印接收日志。

---

## 功能 2：在「文件直传 (Direct Transfer)」Tab 内将标识为“Android”的字符串统一调整为“设备 (Device)”

### 1. 背景与原因
- 引入 LocalSend 协议支持后，「文件直传 (Direct Transfer)」标签页不再局限于仅向 Android 设备传输文件，而是可以向局域网内的任意设备（Android、iOS、macOS、Windows、Linux 等 LocalSend 节点或伴侣节点）直传文件。
- 因此，需要将「文件直传」标签页内所有写死为 `"Android"` 的 UI 标签、按钮文案、弹窗标题及日志提示统一调整为中立且准确的 **“设备 / 裝置 / Device”**。

### 2. 具体三语词条调整对照表 (`Services/LocalizationService.cs`)

| 属性名 (`LocalizationService`) | 原文案 (`0.1.3.0`) | 调整后新文案 (`0.1.4.0`) |
| :--- | :--- | :--- |
| **`TargetAndroidReceiveDirLabel`**<br>*(建议同步重命名或保留兼容属性名 `TargetDeviceReceiveDirLabel`)* | • `en-US`: `Target Android Receive Directory`<br>• `zh-CN`: `Android 目标接收目录`<br>• `zh-TW`: `Android 目標接收目錄` | • `en-US`: `Target Device Receive Directory`<br>• `zh-CN`: `目标设备接收目录`<br>• `zh-TW`: `目標裝置接收目錄` |
| **`StartDirectPushButton`** | • `en-US`: `Push Queue to Android`<br>• `zh-CN`: `推送队列至 Android`<br>• `zh-TW`: `推送佇列至 Android` | • `en-US`: `Push Queue to Device`<br>• `zh-CN`: `推送队列至设备`<br>• `zh-TW`: `推送佇列至裝置` |
| **`PullFromAndroidButton`**<br>*(或 `PullFromDeviceButton`)* | • `en-US`: `Pull Item from Android...`<br>• `zh-CN`: `从 Android 拉取文件/文件夹...`<br>• `zh-TW`: `從 Android 拉取檔案/資料夾...` | • `en-US`: `Pull Item from Device...`<br>• `zh-CN`: `从设备拉取文件/文件夹...`<br>• `zh-TW`: `從裝置拉取檔案/資料夾...` |
| **`AndroidPullPickerTitle`**<br>*(用于直传拉取选择器标题)* | • `en-US`: `Select Android File or Folder to Pull`<br>• `zh-CN`: `选择要拉取到电脑的 Android 文件或文件夹`<br>• `zh-TW`: `選擇要拉取到電腦的 Android 檔案或資料夾` | • `en-US`: `Select Device File or Folder to Pull`<br>• `zh-CN`: `选择要拉取到电脑的设备文件或文件夹`<br>• `zh-TW`: `選擇要拉取到電腦的裝置檔案或資料夾` |

### 3. 「文件直传」页内代码与日志文案调整 (`Views/DirectTransferPage.xaml(.cs)`)
1. **`Views/DirectTransferPage.xaml`**：
   - 将接收目录标签、推送按钮、拉取按钮的绑定更新为上述调整后的“设备 (Device)”词条。
   - 当选中 `[LocalSend]` 设备时，接收目录输入框动态显示提示文案：
     - `en-US`: `Saved to receiver's LocalSend download folder automatically`
     - `zh-CN`: `由接收端设备 LocalSend 自动保存至其下载目录`
     - `zh-TW`: `由接收端裝置 LocalSend 自動儲存至其下載目錄`
2. **`Views/DirectTransferPage.xaml.cs`、`Services/AdbSyncService.cs`、`Services/CompanionSyncService.cs`**：
   - 将直传相关的活动日志与错误提示中的 `"Android"` 替换为 `"Device"` / `"设备"`：
     - `"Direct transfer to Android completed..."` ➔ `"Direct transfer to device completed..."`
     - `"Direct pull from Android completed..."` ➔ `"Direct pull from device completed..."`
     - `"Error browsing Android folder: ..."` ➔ `"Error browsing device folder: ..."`
     - `"Error pulling from Android: ..."` ➔ `"Error pulling from device: ..."`

---

## 待新增与修改的代码文件清单（`0.1.4.0` 实施阶段）

1. **`Models/LocalSendModels.cs`（新增）**：
   - 定义 LocalSend Protocol v2.1 所需的所有 DTO（`LocalSendInfoDto`、`LocalSendFileDto`、`LocalSendPrepareUploadRequestDto`、`LocalSendPrepareUploadResponseDto`、`LocalSendPeer`）。
2. **`Models/CompanionModels.cs`（修改）**：
   - 在 `ConnectionMode` 中新增 `LocalSend = 2`。
3. **`Services/LocalSendService.cs`（新增）**：
   - 实现 UDP 多播 `224.0.0.167:53317` 设备发现与公告回应；
   - 实现 HTTP/HTTPS `53317` LocalSend 客户端（`info`、`register`、`prepare-upload`、`upload`、`cancel`）及服务端接收监听。
4. **`Services/SettingsService.cs`（修改）**：
   - 在 `AppSettings` 中新增 `LocalSendEnabled`（默认 `true`）、`LocalSendPort`（默认 `53317`）、`LocalSendAlias`、`LocalSendReceiveDirectory`、`LocalSendAutoAccept` 等持久化配置。
5. **`Services/LocalizationService.cs`（修改）**：
   - 将「文件直传」标签页内所有 `"Android"` 标识调整为 `"Device"` / `"设备"` / `"裝置"`；
   - 新增 LocalSend 连接、发现、等待接收端确认、本地接收配置等三语词条。
6. **`Views/DirectTransferPage.xaml(.cs)`（修改）**：
   - 应用新的“设备 (Device)”文案；
   - 接入 `LocalSendService` 发送与接收逻辑，并根据当前设备是否为 `LocalSend` 模式自适应切换接收路径栏状态。
7. **`Views/MainPage.xaml(.cs)` & `Views/SettingsPage.xaml(.cs)`（修改）**：
   - 顶部设备栏与“无线连接”弹窗支持同时扫描并选择 `[LocalSend]` 设备；
   - 设置页支持配置本机 LocalSend 监听开关、设备别名与默认接收目录。
