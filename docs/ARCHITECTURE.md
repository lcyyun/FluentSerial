# FluentSerial 架构说明

FluentSerial 是一个单进程、纯原生的 Windows 桌面应用：.NET 10 + WPF + WPF-UI。没有本地 Web 服务、没有浏览器内核，串口数据从 Windows 串口驱动一路进入 WPF 视觉树。

## 运行拓扑

```text
FluentSerial.exe
├── App                    启动、主题、截图/UI 冒烟模式、全局异常处理
├── MainWindow             原生窗口、左侧导航、页面缓存与导航 API
├── Features/
│   ├── TerminalPage       串口终端：连接参数、接收控制台、发送面板、快捷指令
│   ├── PresetsPage        端口预设卡片与连接入口
│   ├── SnippetsPage       快捷指令库、导入导出
│   ├── LogsPage           会话记录 + 审计事件
│   └── SettingsPage       外观、收发默认值、记录与数据目录
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

同一时间只有一个串口会话，这是桌面串口工具的正确模型：一个物理端口对应一个控制台。页面由 `MainWindow` 懒加载并缓存，导航不会重建页面，也不会中断正在进行的串口会话。

## 数据流

### 接收

1. `SerialPortTransport` 的后台读循环每次 `Read` 得到一个原始字节块。
2. 原始块通过 `DataReceived` 交给 `SerialSessionService`；服务累计 RX 字节、写 `SessionRecorder`，再以 `SerialTraffic` 事件广播原始块。
3. `TerminalPage` 在 Dispatcher 上将块加入有界的原始历史（默认最多 5000 条 / 2 MB），然后：
   - 文本模式：流式 `Decoder` 解码（UTF-8 / GBK 多字节字符跨读取边界安全），按 CR/LF 切行；70 ms 空闲后刷新未完成的行。
   - HEX 模式：按 16 字节一行格式化。
4. 渲染队列每 40 ms 批量写入 `RichTextBox`，避免高频串口数据造成逐字符布局。切换时间戳、发送回显或文本/HEX 时会用同一份原始历史重新排版，保证显示与数据一致。
5. 暂停只暂停渲染，历史继续累积；恢复时整体重排。

### 发送

1. 发送参数（文本/HEX、行尾、校验、转义、编码）由 `SerialCodec.BuildPayload` 统一生成：`payload + checksum(payload) + lineEnding`。
2. `SerialSessionService.Send` 调用传输层写入，累计 TX 字节、写记录、广播 `SerialTraffic(Tx)`。
3. `TerminalPage` 按当前显示模式把 TX 行渲染为文本或 HEX。
4. 定时发送由 UI 的 `DispatcherTimer` 驱动；串口关闭或间隔非法时自动停止。
5. 发送文件直接读取字节流交给服务，不经过文本编码。

### 状态与错误

- `SerialLinkState`：`Closed → Opening → Open → Closed/Error`，所有变化由真实传输事件驱动。
- 端口占用、拒绝访问、不存在、IO 错误、USB 拔出都有中文原因，并写入审计与 app.log。
- `SerialSessionService` 在锁外逐订阅者触发事件并单独 try/catch，单个 UI 订阅者异常不会中断串口读线程。
- `TerminalPage` 所有服务事件都经过 Dispatcher；关闭会话时服务把会话元数据（结束时间、RX/TX 字节、文件路径）落库。

## 线程模型

| 位置 | 线程 |
| --- | --- |
| 串口读循环 / DEMO 定时器 | 后台线程；抛出原始字节事件 |
| `SerialSessionService` | 线程安全；事件可在任意线程触发 |
| `MainWindow` / 页面 | WPF Dispatcher；入口事件统一 BeginInvoke |
| 每秒速率统计 | UI `DispatcherTimer` 调用 `TickRates` |
| 记录器写入 | 在服务事件线程内串行写入，内部加锁 |

## 本地存储

```text
%APPDATA%\FluentSerial\
├── store.json     端口预设、快捷指令、会话元数据、审计事件、设置（原子写）
├── logs\          每次会话一份 UTF-8 文本记录
├── exports\       用户导出内容
└── app.log        本地诊断日志（不含业务敏感数据）
```

`store.json` 采用 JSON + 字符串枚举，写入时先写 `.tmp` 再 `File.Move(overwrite: true)`，崩溃不会留下半个文件；损坏的 store 会先备份为 `store.json.corrupt-*` 再回退默认值。

截图/UI 冒烟模式通过 `FLUENTSERIAL_DATA_DIR` 指向 `data\demo` 等临时目录，不会读写真实 `%APPDATA%` 数据。

## 串口细节

- 端口枚举走 SetupAPI `SetupDiGetClassDevs` / `SetupDiGetDeviceRegistryProperty`，失败时回退注册表与 `SerialPort.GetPortNames()`；每次刷新重新枚举。
- 打开/关闭、DTR/RTS、Break 都是真实 `SerialPort` API。
- 编码通过 `CodePagesEncodingProvider` 注册 GBK，文本按流式解码，不假设每次读取落在字符边界。
- 校验值计算在行尾之前；SUM8/XOR8 单字节，Modbus CRC 低字节在前，CCITT 高字节在前。
- DEMO 回环不创建真实虚拟 COM 端口，只在应用内模拟设备行为，界面与日志中均明确标注。

## 测试与截图

- `native/FluentSerial.Smoke`：90 项核心断言，覆盖编解码、校验、记录文件、DEMO 收发、会话生命周期、失败路径、存储与审计。
- `--ui-smoke`：在真实 WPF 窗口中导航五个页面、打开 DEMO、从发送区发送、切换 HEX 与主题、保存设置、校验会话记录落盘。
- `--capture`：`RenderTargetBitmap` 直接渲染 WPF 视觉树，不依赖桌面截图；自动生成本仓库 `docs/native` 截图。
