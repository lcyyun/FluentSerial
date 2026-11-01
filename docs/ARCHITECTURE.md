# FluentSerial 架构说明

FluentSerial 是一个单进程、纯原生的 Windows 桌面应用：.NET 10 + WPF + WPF-UI。没有本地 Web 服务、没有浏览器内核，串口数据从 Windows 串口驱动一路进入 WPF 视觉树。

## 运行拓扑

```text
FluentSerial.exe
├── App                    启动、主题、截图/UI 冒烟模式、全局异常处理
├── MainWindow             原生窗口、左侧导航、页面缓存与导航 API
├── Features/
│   ├── TerminalPage       串口终端：折叠参数、控制台查找、发送预览、快捷指令
│   ├── SerialFileSender   异步分块文件发送、进度与取消
│   ├── QuickCommandImport 指令 JSON 整批验证与精确去重
│   ├── PresetsPage        端口预设卡片与连接入口
│   ├── SnippetsPage       快捷指令库、导入导出
│   ├── LogsPage           会话/审计搜索、文件操作与活动会话删除保护
│   └── SettingsPage       外观、收发默认值、未保存草稿与撤销
├── Dialogs/               预设、指令、确认、快捷键
└── Core/
    ├── SerialSessionService   唯一串口会话：状态机、计数器、记录、审计、速率
    ├── SerialPortTransport    System.IO.Ports 包装 + 后台读循环
    ├── DemoSerialTransport    内置 DEMO 回环模拟器
    ├── SerialPortCatalog      SetupAPI 枚举端口 + 友好名称 + DEMO 项
    ├── SessionRecorder        UTF-8 会话日志写入
    ├── SerialCodec            HEX / 文本 / 转义 / 行尾 / 编码
    ├── Checksums              SUM8 / XOR8 / CRC16-Modbus / CRC16-CCITT
    ├── DataStore              %APPDATA%\FluentSerial\store.json 原子读写
    ├── AuditService           本机审计事件（最多 800 条）
    └── DemoDataSeeder         截图模式使用的确定性演示数据
```

应用当前采用单会话模型：同一时间连接一个端口，使用一个控制台。页面由 `MainWindow` 懒加载并缓存，导航不会重建页面，也不会中断正在进行的串口会话。默认窗口为 1280 × 820（最小 1080 × 740），高级串口参数可折叠。设置页在有未保存修改时保留表单草稿，保存或撤销后再与共享设置同步；设置页的 `Ctrl+S` 保存设置，其他页面保存控制台。

## 数据流

### 接收

1. `SerialPortTransport` 的后台读循环每次 `Read` 得到一个原始字节块。
2. 原始块通过 `DataReceived` 交给 `SerialSessionService`；服务累计 RX 字节、写 `SessionRecorder`，再以 `SerialTraffic` 事件广播原始块。
3. `TerminalPage` 在 Dispatcher 上将块加入有界的原始历史（默认最多 5000 条 / 2 MB），然后：
   - 文本模式：流式 `Decoder` 解码，按 CR/LF 切行；70 ms 空闲后刷新未完成的行。独立保存跨块 CRLF 状态，TX/系统消息插入不会破坏换行；连续无换行文本约每 4096 字符分段，避免拆开 Unicode 代理对。
   - HEX 模式：按 16 字节一行格式化。
4. 渲染队列受最大行数约束，每 40 ms 最多写入 200 行，并用 `BeginChange/EndChange` 批量更新 `RichTextBox`。历史达到容量后只淘汰旧条目；切换时间戳、发送回显或文本/HEX 时再从保留历史重排。
5. 暂停时保留当前画面，历史仍在容量限制内累积，显示选项变更推迟到恢复后重排。
6. `Ctrl+F` 在当前显示的文本 Run 中进行忽略大小写查找，支持前后循环定位；找到结果时关闭“跟随最新”，可从工具栏重新开启。查找不扫描已淘汰的历史或磁盘日志。

### 发送

1. 发送参数（文本/HEX、行尾、校验、转义、编码）由 `SerialCodec.BuildPayload` 统一生成：`payload + checksum(payload) + lineEnding`。发送区的字节数与 HEX 预览使用同一管线，修改行尾/校验即时更新；非法 HEX 禁止发送。
2. `SerialSessionService.Send` 调用传输层写入，累计 TX 字节、写记录、广播 `SerialTraffic(Tx)`。
3. `TerminalPage` 按当前显示模式把 TX 行渲染为文本或 HEX。
4. 定时发送由 UI 的 `DispatcherTimer` 驱动；串口关闭或间隔非法时自动停止。
5. `SerialFileSender.SendAsync` 以异步 `FileStream` 读取文件，并将同步串口写入放到工作线程，逐块等待完成后报告进度。块大小按波特率调整为 64–4096 字节，文件上限为 128 MB；原始文件字节不追加编码、行尾或校验。
6. 文件发送期间停用定时发送和其他发送入口；取消等待当前写入完成，并保留实际已发送字节数。每块发送前校验连接身份，断开或会话改变后停止，避免继续写入新连接。

### 状态与错误

- `SerialLinkState`：`Closed → Opening → Open → Closed/Error`，所有变化由真实传输事件驱动。
- 端口占用、拒绝访问、不存在、IO 错误、USB 拔出都有中文原因，并写入审计与 app.log。
- `SerialSessionService` 在锁外逐订阅者触发事件并单独 try/catch，单个 UI 订阅者异常不会中断串口读线程。
- `TerminalPage` 所有服务事件都经过 Dispatcher；关闭会话时服务把会话元数据（结束时间、RX/TX 字节、文件路径）落库。
- 可重置的界面计数与整场会话累计分别维护；旧传输实例的延迟写入结果不计入新会话。日志页拒绝删除仍在进行的会话。

## 线程模型

| 位置 | 线程 |
| --- | --- |
| 串口读循环 / DEMO 定时器 | 后台线程；抛出原始字节事件 |
| `SerialSessionService` | 线程安全；事件可在任意线程触发 |
| `MainWindow` / 页面 | WPF Dispatcher；入口事件统一 BeginInvoke |
| 每秒速率统计 | UI `DispatcherTimer` 调用 `TickRates` |
| 记录器写入 | 在服务事件线程内串行写入，内部加锁 |
| 文件发送 | 异步读文件，工作线程同步写串口；进度返回调用方上下文 |

## 本地存储

```text
%APPDATA%\FluentSerial\
├── store.json     端口预设、快捷指令、会话元数据、审计事件、设置（原子写）
├── logs\          每次会话一份 UTF-8 文本记录
├── exports\       用户导出内容
└── app.log        本地诊断日志（不含业务敏感数据）
```

`store.json` 采用 JSON + 字符串枚举，写入时先写 `.tmp` 再 `File.Move(overwrite: true)`，崩溃不会留下半个文件；损坏的 store 会先备份为 `store.json.corrupt-*` 再回退默认值。

会话日志名包含毫秒时间戳和 GUID，并通过 `FileMode.CreateNew` 创建；快速重连或重复路径不会覆盖旧日志。

脚本通过 `FLUENTSERIAL_DATA_DIR` 隔离每次运行：UI 冒烟使用 `data\ui-smoke\<运行 ID>`，截图使用 `data\capture\<运行 ID>\<图片名>`。脚本结束后恢复原环境变量，保留报告和诊断文件，不终止其他实例；超时时仅终止自己启动的进程。直接调用 `--capture` / `--ui-smoke` 时，调用方仍需显式设置隔离目录。

快捷指令导入由 `QuickCommandImport` 在修改集合前完成整批验证，检查枚举、载荷与当前编码/转义设置下的可发送性。以全部可编辑字段精确去重，不覆盖同名但内容不同的旧指令；导入界面限制单个文件 4 MB，解析器限制 1000 条。

## 串口细节

- 端口枚举走 SetupAPI `SetupDiGetClassDevs` / `SetupDiGetDeviceRegistryProperty`，失败时回退注册表与 `SerialPort.GetPortNames()`；每次刷新重新枚举。
- 打开/关闭、DTR/RTS、Break 都是真实 `SerialPort` API。
- 编码通过 `CodePagesEncodingProvider` 注册 GBK，文本按流式解码，不假设每次读取落在字符边界；发送转义时保留 emoji 与 `\uNNNN` 代理对。
- HEX 输入按完整字节组校验，支持连续字节和 `0x` 前缀，拒绝跨分隔符拼接半字节等歧义输入。
- 校验值计算在行尾之前；SUM8/XOR8 单字节，Modbus CRC 低字节在前，CCITT 高字节在前。
- DEMO 回环不创建真实虚拟 COM 端口，只在应用内模拟设备行为，界面与日志中均明确标注。

## 测试与截图

- `native/FluentSerial.Smoke`：覆盖编解码、Unicode 代理对、校验、日志防覆盖、DEMO 收发、计数重置与会话累计、存储、指令导入，以及文件分块发送/取消/大小限制。
- `--ui-smoke`：操作真实 WPF 窗口，覆盖页面导航、DEMO 收发、HEX/主题切换、分包 CRLF、暂停与队列上限、预览与查找、设置草稿/NaN 拒绝、日志搜索、收藏筛选及记录落盘。结果写入每次运行的 `ui-smoke.txt`。
- `--capture`：`RenderTargetBitmap` 直接渲染 WPF 视觉树，不依赖桌面截图；自动生成本仓库 `docs/native` 截图。

本轮自动化以 DEMO 和进程内测试为基础，未验证物理串口硬件、设备拔插或真实硬件流控；DEMO 不创建虚拟 COM 端口对。
