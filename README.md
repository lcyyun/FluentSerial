# FluentSerial Native

真正原生的 Windows 串口调试工作台，使用 **.NET 10 + WPF** 构建。没有浏览器内核，没有 Electron / Tauri / WebView2 外壳；串口终端、端口预设、快捷指令、数据记录和设置全部是原生桌面界面。

- 原生窗口：WPF `FluentWindow` + Mica 背景 + 系统窗口按钮
- 串口引擎：`System.IO.Ports.SerialPort`，完整的数据位 / 校验 / 停止位 / 流控 / DTR / RTS / Break 控制
- 终端渲染：WPF `RichTextBox` 原生文本管线，支持文本与 HEX 两种视图、毫秒时间戳和跨读边界的安全换行
- 本地数据：`%APPDATA%\FluentSerial`，纯本机 JSON 存储；无遥测、无云同步
- 可执行文件：自包含单文件 `dist\FluentSerial-native-win-x64\FluentSerial.exe`
- 没有串口硬件也能完整体验：应用内置 **DEMO 回环**端口，会模拟设备应答与周期遥测

![串口终端](docs/native/terminal.png)
![HEX 视图](docs/native/terminal-hex.png)
![首次启动](docs/native/first-run.png)
![端口预设](docs/native/presets.png)
![端口预设编辑](docs/native/preset-dialog.png)
![快捷指令](docs/native/snippets.png)
![快捷指令编辑](docs/native/snippet-dialog.png)
![数据记录](docs/native/logs.png)
![审计事件](docs/native/logs-audit.png)
![设置](docs/native/settings.png)
![深色设置](docs/native/settings-dark.png)
![键盘快捷键](docs/native/shortcuts.png)
![深色主题](docs/native/terminal-dark.png)

更多截图：[`docs/native/`](docs/native)

---

## 功能

### 串口连接
- 串口枚举带设备友好名称（SetupAPI），刷新按钮与 `F5` 热刷新
- 波特率可编辑（1–4000000，常用 1200–921600），数据位 5–8、校验、停止位、流控
- DTR / RTS 实时电平控制，`Break` 信号，收发计数随时重置
- 端口占用、设备拔出、IO 异常均有中文错误提示，状态来自真实串口事件
- 内置 `DEMO` 回环模拟器：欢迎信息、周期遥测、AT 指令应答，无需硬件
- 打开串口参数写回本地设置，下次启动自动恢复

### 原生串口终端
- 文本 / HEX 双视图，一键切换并重排历史数据
- 毫秒时间戳、发送回显、自动滚动、最大行数限制、暂停与继续
- 文本编码 UTF-8 / GBK / ASCII / Latin-1，GBK 中文日志直接可读
- 跨读取边界的 CRLF 安全处理，不会切出多余空行
- 清空、复制选中、复制全部、保存控制台到本地日志
- 状态栏实时显示 RX / TX 总字节与接收速率

### 发送
- 文本 / HEX 发送，HEX 支持空格、逗号、`0x` 前缀
- 行尾：无 / CR / LF / CRLF
- 校验追加：SUM8、XOR8、CRC16-Modbus（低字节在前）、CRC16-CCITT（高字节在前）
- 文本转义：`\r` `\n` `\t` `\0` `\\` `\xNN` `\uNNNN`
- 定时发送（50–60000 ms）、发送历史（`Ctrl+↑ / Ctrl+↓` 召回）、发送文件
- `Ctrl+Enter` 发送，并拦截中文输入法组字过程中的 Enter

### 快捷指令
- 内置 AT 指令与 Modbus RTU 模板，可继续新增、编辑、分组、收藏
- 搜索与分组筛选，双击或点击即可发送
- 指令编辑器内实时显示字节数与 HEX 预览，非法 HEX 即时提示
- 串口未打开时，指令会先载入发送区，不会静默丢失
- 支持 JSON 导入 / 导出，便于团队共享调试脚本

### 端口预设
- 常用串口配置保存为预设，包含端口、波特率、校验、流控、DTR/RTS、编码与收发模式
- 收藏、搜索、最近使用时间，卡片展示编码与收发模式
- 一键连接、编辑、删除；双击卡片直接打开终端

### 数据记录与审计
- 自动会话记录：时间、端口/预设、参数、时长、RX/TX 字节、文件路径
- 记录文件为 UTF-8 文本，每条报文包含时间戳、方向、长度、HEX 与可读文本
- 会话记录一键打开或定位到资源管理器，删除前确认
- 审计事件：连接、断开、失败、预设修改、设置变更，可回溯

### 桌面体验
- 浅色 / 深色 / 跟随系统主题，切换即时生效
- 所有页面共享本地设置与 Roaming 数据目录
- 键盘导航：`Ctrl+1..4` 切换页面，`Ctrl+O` 开关串口，`Ctrl+L` 清空控制台，`Ctrl+S` 保存控制台，`Ctrl+,` 设置
- 原生窗口最大 / 最小 / 关闭按钮与 Per-Monitor DPI 缩放
- 自包含单文件发布，不需要安装 .NET 运行时

## 设计规范

界面遵循 [`desktop-agent-ui`](https://github.com/Ludi-Herry/desktop-agent-ui) 的桌面视觉规则：

- 中性分层 Surface，不使用装饰性阴影与左边缘彩条
- 0.5px 发丝边框；嵌套容器使用同心圆角（10 / 6 / 4）
- 全局字号收敛为 20 / 13 / 12 三档，正文不小于 12px
- 禁用中点分隔符，移除冗余副标题，让布局本身承担层级
- 控制台在浅色主题下也保持深色仪器面板，收发用颜色与方向箭头区分
- 状态与真实事件对应：连接、暂停、记录和计数器均来自实际串口流量

## 构建与运行

环境要求：Windows 10/11 x64，.NET 10 SDK。

```powershell
# 直接运行（开发）
dotnet run --project native\FluentSerial\FluentSerial.csproj

# 构建 Release
dotnet build native\FluentSerial\FluentSerial.csproj -c Release

# 发布自包含单文件
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\native-publish.ps1
```

也可以双击 `start.cmd`：存在已发布版本时直接启动，否则先构建再运行。

发布产物：

```text
dist\FluentSerial-native-win-x64\FluentSerial.exe   # 直接双击运行
dist\FluentSerial-native-win-x64.zip                # 便携压缩包
```

## 测试

Smoke 测试覆盖 HEX / 转义 / 行尾 / 校验算法 / 发送管线 / DEMO 回环 / 会话记录 / 本地存储 / 审计：

```powershell
dotnet run --project native\FluentSerial.Smoke\FluentSerial.Smoke.csproj -c Release
```

UI 冒烟测试会真实导航五个页面、打开 DEMO 串口、从发送区发送数据、切换 HEX 视图与深浅色主题，并校验会话记录落盘：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ui-smoke.ps1
```

UI 截图回归使用原生截图参数，数据目录会指向 `data\demo`，不会触碰 `%APPDATA%`：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\capture.ps1
```

也可以单独截图：

```powershell
native\FluentSerial\bin\Release\net10.0-windows\FluentSerial.exe `
  --capture docs\native\terminal.png --page terminal --demo --theme Light --delay 1200
```

## 数据目录

```text
%APPDATA%\FluentSerial\
├── store.json         # 端口预设、快捷指令、会话元数据、审计事件、设置
├── logs\              # 会话记录文件（UTF-8 文本）
├── exports\           # 快捷指令等导出内容
└── app.log            # 仅本地诊断日志
```

删除该目录即可完全清除本地数据。应用不访问网络，也不会上传任何串口内容。

## 架构

串口数据流、线程模型、本地存储格式与测试策略见 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)。

## 项目结构

```text
native/FluentSerial/
├── App.xaml                 # 启动、主题、全局异常处理、截图模式
├── MainWindow.xaml          # 原生外壳、侧栏与页面导航
├── Core/                    # 串口传输、端口枚举、DEMO 回环、编解码、校验、存储、记录、审计
├── Dialogs/                 # 预设、指令、确认、快捷键对话框
├── Features/                # 终端、预设、指令、记录、设置
├── Themes/                  # Light / Dark 设计令牌与 Fluent 控件样式
└── Assets/                  # 应用图标
native/FluentSerial.Smoke/   # 核心层 Smoke 测试
scripts/                     # 构建、发布、截图、图标生成
docs/native/                 # 原生界面截图
```

## 已知边界

- 仅支持 Windows；未提供 macOS / Linux 版本
- 内置 DEMO 是应用内模拟器，不会创建真实的虚拟 COM 端口对
- 不包含示波器式曲线绘制与脚本引擎，专注于串口收发与协议调试
- 多字节编码按流式解码，单次读取边界不会破坏 UTF-8 / GBK 字符
- CRC16-Modbus 按协议惯例低字节在前，CRC16-CCITT 采用高字节在前

## License

MIT
