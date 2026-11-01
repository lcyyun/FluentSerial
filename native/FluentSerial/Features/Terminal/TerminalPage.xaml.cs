using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FluentSerial.Core;
using FluentSerial.Dialogs;
using Microsoft.Win32;

namespace FluentSerial.Features;

public partial class TerminalPage : UserControl, IActivatablePage
{
    private const int MaxHistoryEntries = 5000;
    private const long MaxHistoryBytes = 2_000_000;

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private enum ConsoleTone
    {
        Rx,
        Tx,
        Success,
        Error,
        Warning,
        Muted
    }

    private sealed class ConsoleLine
    {
        public DateTime At { get; init; }
        public string Text { get; init; } = string.Empty;
        public ConsoleTone Tone { get; init; } = ConsoleTone.Rx;
        public bool Continuation { get; init; }
    }

    private readonly ObservableCollection<SerialPortInfo> _ports = new();
    private readonly List<SerialTraffic> _history = new();
    private readonly Queue<ConsoleLine> _pendingLines = new();
    private readonly StringBuilder _rxLine = new();
    private readonly List<string> _sendHistory = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _rateTimer;
    private readonly DispatcherTimer _autoSendTimer;
    private readonly FlowDocument _document = new();

    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private DateTime? _rxLineAt;
    private DateTime _lastRxAt = DateTime.MinValue;
    private long _historyBytes;
    private int _renderedLines;
    private string _encodingName = "utf-8";
    private bool _initialized;
    private bool _loadingUi;
    private bool _paused;
    private bool _reRenderRequested;
    private bool _autoSendTicking;
    private bool _suppressPortMemory;
    private SerialLinkState _lastState = SerialLinkState.Closed;
    private int _sendHistoryIndex = -1;
    private string _historyDraft = string.Empty;
    private PortProfile? _selectedProfile;
    private bool _skipNextRxLf;
    private const int RenderBatchSize = 200;
    private const int MaxRxLineLength = 4096;

    public TerminalPage()
    {
        InitializeComponent();
        ConfigureChoiceSources();
        ConfigureDocument();

        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        _renderTimer.Tick += (_, _) => FlushRenderQueue();
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(70) };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            FlushRxLine();
        };
        _rateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _rateTimer.Tick += (_, _) =>
        {
            AppServices.Serial.TickRates();
            UpdateCounters();
        };
        _autoSendTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoSendTimer.Tick += (_, _) => AutoSendTick();

        AppServices.Serial.Traffic += OnSerialTraffic;
        AppServices.Serial.StateChanged += OnSerialStateChanged;
        AppServices.Serial.ErrorOccurred += OnSerialError;
        AppServices.Serial.CountersChanged += OnCountersChanged;
        AppServices.DataChanged += OnDataChanged;

        Loaded += OnLoaded;
        _renderTimer.Start();
        _rateTimer.Start();
    }

    private static AppSettings Settings => AppServices.Settings;

    private void ConfigureChoiceSources()
    {
        PortBox.ItemsSource = _ports;

        BaudBox.ItemsSource = new[]
        {
            "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200",
            "230400", "460800", "921600", "1000000", "2000000"
        };
        DataBitsBox.ItemsSource = new[] { 5, 6, 7, 8 };
        ParityBox.ItemsSource = new List<Choice<Parity>>
        {
            new(Parity.None, "无校验"),
            new(Parity.Odd, "奇校验"),
            new(Parity.Even, "偶校验"),
            new(Parity.Mark, "标记"),
            new(Parity.Space, "空号")
        };
        StopBitsBox.ItemsSource = new List<Choice<StopBits>>
        {
            new(StopBits.One, "1 位"),
            new(StopBits.OnePointFive, "1.5 位"),
            new(StopBits.Two, "2 位")
        };
        HandshakeBox.ItemsSource = new List<Choice<Handshake>>
        {
            new(Handshake.None, "无流控"),
            new(Handshake.XOnXOff, "软件 XON/XOFF"),
            new(Handshake.RequestToSend, "硬件 RTS/CTS"),
            new(Handshake.RequestToSendXOnXOff, "硬件 + 软件")
        };
        EncodingBox.ItemsSource = new List<Choice<string>>
        {
            new("utf-8", "UTF-8"),
            new("gbk", "GBK"),
            new("ascii", "ASCII"),
            new("iso-8859-1", "Latin-1")
        };
        EndingBox.ItemsSource = new List<Choice<LineEnding>>
        {
            new(LineEnding.None, "无"),
            new(LineEnding.Cr, "CR"),
            new(LineEnding.Lf, "LF"),
            new(LineEnding.CrLf, "CRLF")
        };
        ChecksumBox.ItemsSource = new List<Choice<ChecksumKind>>
        {
            new(ChecksumKind.None, "无"),
            new(ChecksumKind.Sum8, "SUM8"),
            new(ChecksumKind.Xor8, "XOR8"),
            new(ChecksumKind.Crc16Modbus, "CRC16-Modbus"),
            new(ChecksumKind.Crc16Ccitt, "CRC16-CCITT")
        };

        BaudBox.Text = "115200";
        DataBitsBox.SelectedItem = 8;
        ParityBox.SelectedIndex = 0;
        StopBitsBox.SelectedIndex = 0;
        HandshakeBox.SelectedIndex = 0;
        EncodingBox.SelectedIndex = 0;
        EndingBox.SelectedIndex = 3;
        ChecksumBox.SelectedIndex = 0;
    }

    private void ConfigureDocument()
    {
        _document.PagePadding = new Thickness(14, 10, 10, 12);
        _document.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
        _document.FontSize = 12.5;
        _document.LineHeight = 18;
        ConsoleBox.Document = _document;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            ApplySettingsFromStore();
            RefreshPorts();
            _initialized = true;
            UpdateQuickCommands();
            UpdateUiState();
            UpdateCounters();
            UpdateSendSize();
        }
        else
        {
            UpdateQuickCommands();
            UpdateUiState();
        }
    }

    public void OnActivated()
    {
        if (!_initialized) return;
        UpdateQuickCommands();
        UpdateUiState();
        UpdateCounters();
    }

    // ---------------------------------------------------------------- settings

    public void ApplySettings()
    {
        ApplySettingsFromStore();
        UpdateSendSize();
        _reRenderRequested = true;
    }

    private void ApplySettingsFromStore()
    {
        _loadingUi = true;
        try
        {
            var settings = Settings;
            _encodingName = string.IsNullOrWhiteSpace(settings.DefaultEncoding) ? "utf-8" : settings.DefaultEncoding;
            _decoder = SerialCodec.GetEncoding(_encodingName).GetDecoder();

            var family = string.IsNullOrWhiteSpace(settings.FontFamily) ? "Cascadia Mono" : settings.FontFamily;
            var fontFamily = new FontFamily($"{family}, Consolas, Microsoft YaHei UI");
            _document.FontFamily = fontFamily;
            ConsoleBox.FontFamily = fontFamily;
            _document.FontSize = double.IsFinite(settings.ConsoleFontSize) ? Math.Clamp(settings.ConsoleFontSize, 9, 24) : 12;
            ConsoleBox.FontSize = _document.FontSize;
            _document.LineHeight = Math.Round(_document.FontSize * 1.5, 1);

            TimestampCheck.IsChecked = settings.ShowTimestamp;
            ShowTxCheck.IsChecked = settings.ShowTx;
            AutoScrollCheck.IsChecked = settings.AutoScroll;
            EscapeCheck.IsChecked = settings.UseEscapes;
            AutoSendIntervalBox.Text = Math.Clamp(settings.AutoSendIntervalMs, 50, 60000).ToString();
            SelectChoice(EncodingBox, _encodingName);
            if (settings.ReceiveMode == PayloadMode.Hex) RxHexRadio.IsChecked = true;
            else RxTextRadio.IsChecked = true;
            if (settings.SendMode == PayloadMode.Hex) TxHexRadio.IsChecked = true;
            else TxTextRadio.IsChecked = true;
            SelectChoice(EndingBox, settings.DefaultEnding);
            SelectChoice(ChecksumBox, settings.DefaultChecksum);

            if (!AppServices.Serial.IsOpen)
            {
                BaudBox.Text = settings.LastBaudRate.ToString();
                DataBitsBox.SelectedItem = Math.Clamp(settings.LastDataBits, 5, 8);
                SelectChoice(ParityBox, settings.LastParity);
                SelectChoice(StopBitsBox, settings.LastStopBits);
                SelectChoice(HandshakeBox, settings.LastHandshake);
            }
            DtrCheck.IsChecked = settings.LastDtr;
            RtsCheck.IsChecked = settings.LastRts;
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private static void SelectChoice<T>(ComboBox box, T value)
    {
        foreach (var item in box.Items)
        {
            if (item is Choice<T> choice && EqualityComparer<T>.Default.Equals(choice.Value, value))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static T ChoiceValue<T>(ComboBox box, T fallback)
        => box.SelectedItem is Choice<T> choice ? choice.Value : fallback;

    // ---------------------------------------------------------------- ports

    public void RefreshPorts()
    {
        var previous = AppServices.Serial.IsOpen ? AppServices.Serial.Options?.PortName : (PortBox.SelectedItem as SerialPortInfo)?.Name;
        if (string.IsNullOrWhiteSpace(previous) && !_initialized)
            previous = Settings.LastPortName;

        IReadOnlyList<SerialPortInfo> ports;
        try
        {
            ports = SerialPortCatalog.GetPorts();
        }
        catch (Exception ex)
        {
            AppServices.LogError("枚举串口失败", ex);
            ports = Array.Empty<SerialPortInfo>();
        }

        _suppressPortMemory = true;
        try
        {
            _ports.Clear();
            foreach (var port in ports) _ports.Add(port);
            if (AppServices.Serial.IsOpen && !string.IsNullOrEmpty(previous) && !_ports.Any(p => p.Name.Equals(previous, StringComparison.OrdinalIgnoreCase)))
                _ports.Add(new SerialPortInfo(previous, "当前连接，刷新时未检测到设备"));
            PortBox.SelectedItem = _ports.FirstOrDefault(p => string.Equals(p.Name, previous, StringComparison.OrdinalIgnoreCase))
                                   ?? _ports.FirstOrDefault();
        }
        finally
        {
            _suppressPortMemory = false;
        }

        PortHintText.Text = _ports.Any(p => !p.IsDemo)
            ? "选择端口后打开"
            : "未检测到硬件串口，可使用 DEMO";
    }

    private void SelectPortByName(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName)) return;
        var existing = _ports.FirstOrDefault(p => string.Equals(p.Name, portName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new SerialPortInfo(portName, "未检测到该端口");
            _ports.Add(existing);
        }
        PortBox.SelectedItem = existing;
    }

    private void PortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _loadingUi || _suppressPortMemory) return;
        if (PortBox.SelectedItem is SerialPortInfo info)
        {
            _selectedProfile = null;
            Settings.LastPortName = info.Name;
        }
    }

    // ---------------------------------------------------------------- connection

    private SerialConnectionOptions BuildOptions()
    {
        var port = PortBox.SelectedItem as SerialPortInfo;
        if (port is null) throw new ArgumentException("请选择一个串口设备，或使用 DEMO 回环。");
        if (!int.TryParse(BaudBox.Text.Trim(), out var baud) || baud < 1 || baud > 4_000_000)
            throw new ArgumentException("波特率必须是 1–4000000 之间的整数，请检查输入。");
        return new SerialConnectionOptions
        {
            PortName = port.Name,
            BaudRate = baud,
            ProfileId = _selectedProfile?.Id,
            ProfileName = _selectedProfile?.Name ?? string.Empty,
            DataBits = DataBitsBox.SelectedItem is int bits ? bits : 8,
            Parity = ChoiceValue(ParityBox, Parity.None),
            StopBits = ChoiceValue(StopBitsBox, StopBits.One),
            Handshake = ChoiceValue(HandshakeBox, Handshake.None),
            Dtr = DtrCheck.IsChecked == true,
            Rts = RtsCheck.IsChecked == true,
            Encoding = _encodingName,
            ReceiveMode = RxHexRadio.IsChecked == true ? PayloadMode.Hex : PayloadMode.Text,
            SendMode = TxHexRadio.IsChecked == true ? PayloadMode.Hex : PayloadMode.Text
        };
    }

    public void ToggleConnect()
    {
        if (IsSendingFile) { CancelFileSend(); return; }
        if (AppServices.Serial.IsOpen || AppServices.Serial.State == SerialLinkState.Opening)
        {
            AppServices.Serial.Close("用户关闭");
            return;
        }
        OpenCurrent();
    }

    private void OpenCurrent()
    {
        ConnectionErrorText.Visibility = Visibility.Collapsed;
        ConnectionErrorText.Text = string.Empty;
        var opened = false;
        SerialConnectionOptions options;
        try
        {
            options = BuildOptions();
        }
        catch (ArgumentException ex)
        {
            ConnectionErrorText.Text = ex.Message;
            ConnectionErrorText.Visibility = Visibility.Visible;
            BaudBox.Focus();
            return;
        }
        try
        {
            opened = AppServices.Serial.Open(options);
        }
        catch (Exception ex)
        {
            AppServices.LogError("打开串口失败", ex);
            ConnectionErrorText.Text = ex.Message;
            ConnectionErrorText.Visibility = Visibility.Visible;
        }

        if (opened)
        {
            options.RememberIn(Settings);
            AppServices.Store.Save();
            ResetConsole();
            AddSystemLine($"已打开 {AppServices.Serial.PortLabel}  {AppServices.Serial.ParamsLabel}  {FormatText.EncodingLabel(_encodingName)}", ConsoleTone.Success);
            SendBox.Focus();
        }
        else if (string.IsNullOrWhiteSpace(ConnectionErrorText.Text))
        {
            ConnectionErrorText.Text = AppServices.Serial.LastError;
            ConnectionErrorText.Visibility = Visibility.Visible;
        }
        UpdateUiState();
    }

    public void ConnectWithProfile(PortProfile profile)
    {
        if (profile is null) return;
        if (IsSendingFile) { CancelFileSend(); return; }
        if (AppServices.Serial.IsOpen) AppServices.Serial.Close("切换端口预设");

        _loadingUi = true;
        try
        {
            SelectPortByName(profile.PortName);
            BaudBox.Text = profile.BaudRate.ToString();
            DataBitsBox.SelectedItem = Math.Clamp(profile.DataBits, 5, 8);
            SelectChoice(ParityBox, profile.Parity);
            SelectChoice(StopBitsBox, profile.StopBits);
            SelectChoice(HandshakeBox, profile.Handshake);
            SelectChoice(EncodingBox, profile.Encoding);
            DtrCheck.IsChecked = profile.Dtr;
            RtsCheck.IsChecked = profile.Rts;
            if (profile.ReceiveMode == PayloadMode.Hex) RxHexRadio.IsChecked = true; else RxTextRadio.IsChecked = true;
            if (profile.SendMode == PayloadMode.Hex) TxHexRadio.IsChecked = true; else TxTextRadio.IsChecked = true;
            _encodingName = profile.Encoding;
            _decoder = SerialCodec.GetEncoding(_encodingName).GetDecoder();
        }
        finally
        {
            _loadingUi = false;
        }

        _selectedProfile = profile;
        UpdateSendSize();
        OpenCurrent();
    }

    public void FocusConnectionArea()
    {
        if (AppServices.Serial.IsOpen)
        {
            SendBox.Focus();
            return;
        }
        PortBox.Focus();
        if (_ports.Count > 0) PortBox.IsDropDownOpen = true;
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e) => ToggleConnect();

    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        RefreshPorts();
        SetTransientHint("串口列表已刷新");
    }

    private void UseDemo_Click(object sender, RoutedEventArgs e)
    {
        if (IsSendingFile) { CancelFileSend(); return; }
        _selectedProfile = null;
        SelectPortByName("DEMO");
        if (AppServices.Serial.IsOpen) AppServices.Serial.Close("切换到 DEMO");
        OpenCurrent();
    }

    // ---------------------------------------------------------------- pin signals

    private void DtrRts_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _loadingUi) return;
        var dtr = DtrCheck.IsChecked == true;
        var rts = RtsCheck.IsChecked == true;
        Settings.LastDtr = dtr;
        Settings.LastRts = rts;
        if (AppServices.Serial.IsOpen)
        {
            try
            {
                if (ReferenceEquals(sender, DtrCheck)) AppServices.Serial.SetDtr(dtr);
                if (ReferenceEquals(sender, RtsCheck) && RtsCheck.IsEnabled) AppServices.Serial.SetRts(rts);
            }
            catch (Exception ex)
            {
                AppServices.LogError("设置 DTR/RTS 失败", ex);
            }
        }
    }

    private void Break_Click(object sender, RoutedEventArgs e)
    {
        if (!AppServices.Serial.IsOpen) return;
        AppServices.Serial.SendBreak(200);
        AddSystemLine("已发送 Break 信号（200 ms）", ConsoleTone.Warning);
    }

    private void ResetCounters_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Serial.ResetCounters();
        UpdateCounters();
        SetTransientHint("收发计数已重置");
    }

    // ---------------------------------------------------------------- serial events

    private void OnSerialTraffic(SerialTraffic traffic)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSerialTraffic(traffic)), DispatcherPriority.Background);
            return;
        }
        PushTraffic(traffic);
    }

    private void PushTraffic(SerialTraffic traffic)
    {
        AddHistory(traffic);
        EmptyState.Visibility = Visibility.Collapsed;
        if (_paused) return;
        RenderTraffic(traffic);
    }

    private void RenderTraffic(SerialTraffic traffic)
    {
        if (traffic.Direction == TrafficDirection.System)
        {
            FlushRxLine();
            EnqueueLine(new ConsoleLine
            {
                At = traffic.At,
                Text = traffic.Message ?? string.Empty,
                Tone = ToneForSystem(traffic.Message ?? string.Empty)
            });
        }
        else if (traffic.Direction == TrafficDirection.Tx)
        {
            FlushRxLine();
            if (ShowTxCheck.IsChecked == true) EnqueueTxLine(traffic);
        }
        else
        {
            if (RxHexRadio.IsChecked == true)
            {
                FlushRxLine();
                EnqueueHexLines(traffic);
            }
            else
            {
                if (_lastRxAt != DateTime.MinValue && (traffic.At - _lastRxAt).TotalMilliseconds > 80)
                    FlushRxLine();
                AppendRxText(traffic);
            }
        }
    }

    private void OnSerialStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnSerialStateChanged));
            return;
        }

        var state = AppServices.Serial.State;
        if (_lastState == SerialLinkState.Open && state == SerialLinkState.Closed)
            AddSystemLine("串口已关闭", ConsoleTone.Muted);
        if (state == SerialLinkState.Error && !string.IsNullOrWhiteSpace(AppServices.Serial.LastError))
        {
            ConnectionErrorText.Text = AppServices.Serial.LastError;
            ConnectionErrorText.Visibility = Visibility.Visible;
        }
        _lastState = state;
        if (state != SerialLinkState.Open && AutoSendCheck.IsChecked == true)
            AutoSendCheck.IsChecked = false;
        UpdateUiState();
        UpdateCounters();
    }

    private void OnSerialError(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSerialError(message)));
            return;
        }
        AddSystemLine(message, ConsoleTone.Error);
        ConnectionErrorText.Text = message;
        ConnectionErrorText.Visibility = Visibility.Visible;
        UpdateUiState();
    }

    private void OnCountersChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnCountersChanged));
            return;
        }
        UpdateCounters();
    }

    private void OnDataChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(UpdateQuickCommands));
            return;
        }
        UpdateQuickCommands();
    }

    private void UpdateCounters()
    {
        var serial = AppServices.Serial;
        CounterText.Text = $"RX {FormatText.Bytes(serial.BytesRx)}    TX {FormatText.Bytes(serial.BytesTx)}    {serial.RxBytesPerSecond:0.#} B/s";
        RecordingText.Visibility = serial.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        RecordingText.Text = serial.IsRecording ? "正在记录" : string.Empty;
        RecordingText.Foreground = serial.IsRecording
            ? (Brush)FindResource("AppSuccessBrush")
            : (Brush)FindResource("AppTextTertiaryBrush");
    }

    private void UpdateUiState()
    {
        if (!_initialized) return;
        var serial = AppServices.Serial;
        var state = serial.State;
        var open = state == SerialLinkState.Open;
        var busy = state == SerialLinkState.Opening;

        PortBox.IsEnabled = !open && !busy;
        BaudBox.IsEnabled = !open && !busy;
        DataBitsBox.IsEnabled = !open && !busy;
        ParityBox.IsEnabled = !open && !busy;
        StopBitsBox.IsEnabled = !open && !busy;
        HandshakeBox.IsEnabled = !open && !busy;
        UpdateSignalControls();
        RefreshPortsButton.IsEnabled = !busy;
        BreakButton.IsEnabled = open;
        ResetCountersButton.IsEnabled = open;
        ConnectButton.IsEnabled = !busy;
        PauseButton.IsEnabled = open || _history.Count > 0;

        ConnectButtonLabel.Text = open ? "关闭串口" : busy ? "正在打开" : "打开串口";
        ConnectButtonGlyph.Text = open ? "\uE769" : "\uE768";

        HeaderStatusText.Text = open
            ? $"{serial.PortLabel}  {serial.ParamsLabel}"
            : busy ? "正在打开串口" : "选择端口后打开，或使用 DEMO 回环";

        ConsoleTitle.Text = open ? "已连接" : state == SerialLinkState.Error ? "连接失败" : "未连接";
        ConsoleMeta.Text = open
            ? $"{serial.PortLabel}  {serial.ParamsLabel}  {FormatText.EncodingLabel(_encodingName)}"
            : state == SerialLinkState.Error
                ? serial.LastError
                : "选择端口后打开";

        ConsoleDot.Fill = (Brush)FindResource(open ? "ConsoleOkBrush" : state == SerialLinkState.Error ? "ConsoleErrorBrush" : "ConsoleMetaBrush");
        StatusDot.Fill = (Brush)FindResource(open ? "AppSuccessBrush" : state == SerialLinkState.Error ? "AppDangerBrush" : "AppTextTertiaryBrush");
        StatusText.Text = open
            ? $"已连接 {serial.PortLabel}  {serial.ParamsLabel}"
            : state == SerialLinkState.Error ? "串口异常，请检查连接" : "未连接串口";

        if (open) ConnectionErrorText.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSendSize();

        if (!open && state != SerialLinkState.Error)
        {
            // Reconnect the decoder when parked; a profile can change the code page.
            _decoder = SerialCodec.GetEncoding(_encodingName).GetDecoder();
        }
    }

    // ---------------------------------------------------------------- receive rendering

    private void AddHistory(SerialTraffic traffic)
    {
        _history.Add(traffic);
        _historyBytes += traffic.Length;
        if (_history.Count > MaxHistoryEntries || _historyBytes > MaxHistoryBytes)
        {
            var remove = 0;
            while (_history.Count - remove > MaxHistoryEntries || _historyBytes > MaxHistoryBytes)
            {
                _historyBytes -= _history[remove++].Length;
            }
            _history.RemoveRange(0, remove);
        }
    }

    private void AppendRxText(SerialTraffic traffic)
    {
        var text = DecodeChunk(traffic.Data);
        _lastRxAt = traffic.At;
        foreach (var ch in text)
        {
            if (_skipNextRxLf)
            {
                _skipNextRxLf = false;
                if (ch == '\n') continue;
            }
            _rxLineAt ??= traffic.At;
            if (ch is '\r' or '\n')
            {
                EnqueueLine(new ConsoleLine
                {
                    At = _rxLineAt ?? traffic.At,
                    Text = _rxLine.ToString(),
                    Tone = ConsoleTone.Rx
                });
                _rxLine.Clear();
                _rxLineAt = null;
                _skipNextRxLf = ch == '\r';
            }
            else
            {
                // Split only when more text follows. An exact-length line ending
                // must not produce an empty paragraph, or split a surrogate pair.
                if (_rxLine.Length >= MaxRxLineLength && !char.IsHighSurrogate(_rxLine[^1]))
                    FlushRxLine();
                _rxLine.Append(ch);
            }
        }
        _idleTimer.Stop();
        if (_rxLine.Length > 0) _idleTimer.Start();
    }

    private string DecodeChunk(byte[] data)
    {
        if (data.Length == 0) return string.Empty;
        try
        {
            var chars = new char[SerialCodec.GetEncoding(_encodingName).GetMaxCharCount(data.Length) + 2];
            var count = _decoder.GetChars(data, 0, data.Length, chars, 0, false);
            return new string(chars, 0, count);
        }
        catch
        {
            return SerialCodec.DecodeText(data, _encodingName);
        }
    }

    private void EnqueueTxLine(SerialTraffic traffic)
    {
        string text;
        if (RxHexRadio.IsChecked == true)
        {
            text = "→ " + SerialCodec.FormatHex(traffic.Data);
        }
        else
        {
            text = "→ " + SerialCodec.EscapeForDisplay(SerialCodec.DecodeText(traffic.Data, _encodingName));
        }
        EnqueueLine(new ConsoleLine { At = traffic.At, Text = text, Tone = ConsoleTone.Tx });
    }

    private void EnqueueHexLines(SerialTraffic traffic)
    {
        if (traffic.Data.Length == 0) return;
        var text = SerialCodec.FormatHexLines(traffic.Data, 16, withOffset: false);
        var parts = text.Split('\n');
        var first = true;
        foreach (var part in parts)
        {
            var value = part.TrimEnd('\r');
            EnqueueLine(new ConsoleLine
            {
                At = traffic.At,
                Text = first ? value : "  " + value,
                Tone = ConsoleTone.Rx,
                Continuation = !first
            });
            first = false;
        }
    }

    private void FlushRxLine()
    {
        if (_rxLine.Length == 0)
        {
            _rxLineAt = null;
            return;
        }
        EnqueueLine(new ConsoleLine
        {
            At = _rxLineAt ?? DateTime.Now,
            Text = _rxLine.ToString(),
            Tone = ConsoleTone.Rx
        });
        _rxLine.Clear();
        _rxLineAt = null;
    }

    private void AddSystemLine(string text, ConsoleTone tone)
    {
        AddHistory(SerialTraffic.System(text));
        EmptyState.Visibility = Visibility.Collapsed;
        if (_paused) return;
        FlushRxLine();
        EnqueueLine(new ConsoleLine { At = DateTime.Now, Text = text, Tone = tone });
    }

    private static ConsoleTone ToneForSystem(string message)
    {
        if (message.Contains("失败", StringComparison.Ordinal) || message.Contains("错误", StringComparison.Ordinal) ||
            message.Contains("异常", StringComparison.Ordinal) || message.Contains("断开", StringComparison.Ordinal))
            return ConsoleTone.Error;
        if (message.Contains("已打开", StringComparison.Ordinal) || message.Contains("已连接", StringComparison.Ordinal))
            return ConsoleTone.Success;
        if (message.Contains("警告", StringComparison.Ordinal) || message.Contains("提示", StringComparison.Ordinal))
            return ConsoleTone.Warning;
        return ConsoleTone.Muted;
    }

    private void EnqueueLine(ConsoleLine line)
    {
        var limit = Math.Clamp(Settings.MaxLines, 200, 20000);
        while (_pendingLines.Count >= limit) _pendingLines.Dequeue();
        _pendingLines.Enqueue(line);
    }

    private void FlushRenderQueue()
    {
        if (!_initialized) return;

        // A display option change or history eviction must never unfreeze a paused view.
        if (_paused)
        {
            ConsoleMeta.Text = $"已暂停显示  缓存 {_history.Count} 条（最多 {MaxHistoryEntries} 条 / 2 MB）";
            return;
        }

        if (_reRenderRequested)
        {
            _reRenderRequested = false;
            RebuildConsole();
            return;
        }

        if (_pendingLines.Count == 0) return;
        ConsoleBox.BeginChange();
        try
        {
            for (var i = 0; i < RenderBatchSize && _pendingLines.Count > 0; i++)
                AppendLine(_pendingLines.Dequeue());
            TrimConsole();
        }
        finally { ConsoleBox.EndChange(); }
        if (Settings.AutoScroll) ConsoleBox.ScrollToEnd();
    }

    private void AppendLine(ConsoleLine line)
    {
        if (line.Text.Length == 0 && line.Tone == ConsoleTone.Rx) line = new ConsoleLine
        {
            At = line.At,
            Text = string.Empty,
            Tone = line.Tone,
            Continuation = line.Continuation
        };

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = Math.Round(Math.Max(15, _document.FontSize * 1.5), 1)
        };

        if (TimestampCheck.IsChecked == true)
        {
            var prefix = line.Continuation ? new string(' ', 13) : line.At.ToString("HH:mm:ss.fff") + "  ";
            paragraph.Inlines.Add(new Run(prefix) { Foreground = (Brush)FindResource("ConsoleMetaBrush") });
        }
        else if (line.Continuation)
        {
            paragraph.Inlines.Add(new Run("  "));
        }

        var (brushKey, prefixText) = line.Tone switch
        {
            ConsoleTone.Tx => ("ConsoleTxBrush", string.Empty),
            ConsoleTone.Success => ("ConsoleOkBrush", string.Empty),
            ConsoleTone.Error => ("ConsoleErrorBrush", "错误  "),
            ConsoleTone.Warning => ("ConsoleWarningBrush", "提示  "),
            ConsoleTone.Muted => ("ConsoleMetaBrush", string.Empty),
            _ => ("ConsoleRxBrush", string.Empty)
        };
        if (line.Tone is ConsoleTone.Tx or ConsoleTone.Success or ConsoleTone.Error or ConsoleTone.Warning)
        {
            var foreground = (Brush)FindResource(brushKey);
            if (prefixText.Length > 0) paragraph.Inlines.Add(new Run(prefixText) { Foreground = foreground });
            paragraph.Inlines.Add(new Run(line.Text) { Foreground = foreground });
        }
        else
        {
            paragraph.Inlines.Add(new Run(line.Text) { Foreground = (Brush)FindResource(brushKey) });
        }

        _document.Blocks.Add(paragraph);
        _renderedLines++;
    }

    private void TrimConsole()
    {
        var max = Math.Clamp(Settings.MaxLines, 200, 20000);
        while (_renderedLines > max && _document.Blocks.Count > 0)
        {
            _document.Blocks.Remove(_document.Blocks.FirstBlock);
            _renderedLines--;
        }
    }

    private void RebuildConsole()
    {
        if (_paused) { _reRenderRequested = true; return; }
        _idleTimer.Stop();
        _document.Blocks.Clear();
        _renderedLines = 0;
        _pendingLines.Clear();
        _rxLine.Clear();
        _rxLineAt = null;
        _decoder = SerialCodec.GetEncoding(_encodingName).GetDecoder();
        _skipNextRxLf = false;
        _lastRxAt = DateTime.MinValue;

        // Replay through the live formatter so mode, CRLF and decoder state agree.
        // In particular, retain its partial UTF-8/GBK character for the next live read.
        foreach (var traffic in _history) RenderTraffic(traffic);
        _idleTimer.Stop();
        FlushRxLine();
        EmptyState.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }


    private void ResetConsole()
    {
        _idleTimer.Stop();
        _history.Clear();
        _historyBytes = 0;
        _pendingLines.Clear();
        _rxLine.Clear();
        _rxLineAt = null;
        _document.Blocks.Clear();
        _renderedLines = 0;
        _reRenderRequested = false;
        _skipNextRxLf = false;
        _lastRxAt = DateTime.MinValue;
        _decoder = SerialCodec.GetEncoding(_encodingName).GetDecoder();
        EmptyState.Visibility = Visibility.Collapsed;
    }

    private void DisplayOption_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        Settings.ShowTimestamp = TimestampCheck.IsChecked == true;
        Settings.ShowTx = ShowTxCheck.IsChecked == true;
        AppServices.Store.Save();
        _reRenderRequested = true;
    }

    private void RxMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _loadingUi) return;
        Settings.ReceiveMode = RxHexRadio.IsChecked == true ? PayloadMode.Hex : PayloadMode.Text;
        if (AppServices.Serial.Options is not null) AppServices.Serial.Options.ReceiveMode = Settings.ReceiveMode;
        AppServices.Store.Save();
        _reRenderRequested = true;
    }

    private void TxMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _loadingUi) return;
        Settings.SendMode = TxHexRadio.IsChecked == true ? PayloadMode.Hex : PayloadMode.Text;
        if (AppServices.Serial.Options is not null) AppServices.Serial.Options.SendMode = Settings.SendMode;
        AppServices.Store.Save();
        UpdateSendSize();
    }

    private void EncodingBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _loadingUi) return;
        var value = ChoiceValue(EncodingBox, "utf-8");
        if (string.Equals(value, _encodingName, StringComparison.OrdinalIgnoreCase)) return;
        _encodingName = value;
        _decoder = SerialCodec.GetEncoding(_encodingName).GetDecoder();
        if (AppServices.Serial.Options is not null) AppServices.Serial.Options.Encoding = value;
        Settings.DefaultEncoding = value;
        AppServices.Store.Save();
        UpdateSendSize();
        _reRenderRequested = true;
    }

    private void EscapeCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _loadingUi) return;
        Settings.UseEscapes = EscapeCheck.IsChecked == true;
        AppServices.Store.Save();
        UpdateSendSize();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseButton.Content = _paused ? "继续" : "暂停";
        if (_paused)
        {
            _idleTimer.Stop();
            _pendingLines.Clear();
            ConsoleMeta.Text = "已暂停显示，接收与会话记录继续";
        }
        else
        {
            _reRenderRequested = true;
            UpdateUiState();
        }
    }

    private void ClearConsole_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count > 0 && Settings.ConfirmBeforeClear)
        {
            var owner = Window.GetWindow(this);
            if (!ConfirmDialog.Show(owner, "清空控制台", "将清空当前接收区中的所有数据。会话记录文件不受影响。", "清空", danger: true))
                return;
        }
        ResetConsole();
        AddSystemLine("控制台已清空", ConsoleTone.Muted);
    }

    private void CopyConsole_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = new TextRange(_document.ContentStart, _document.ContentEnd).Text.TrimEnd();
            if (string.IsNullOrWhiteSpace(text))
            {
                SetTransientHint("控制台为空，没有可复制的内容");
                return;
            }
            Clipboard.SetText(text);
            SetTransientHint($"已复制 {text.Length} 个字符");
        }
        catch (Exception ex)
        {
            AppServices.LogError("复制控制台失败", ex);
        }
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        if (ConsoleBox.Selection.IsEmpty)
        {
            SetTransientHint("没有选中内容");
            return;
        }
        ConsoleBox.Copy();
        SetTransientHint("已复制选中内容");
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        ConsoleBox.SelectAll();
        ConsoleBox.Focus();
    }

    private void SaveConsole_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0)
        {
            SetTransientHint("控制台为空，没有可保存的内容");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "保存控制台内容",
            FileName = $"serial-console-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".log",
            AddExtension = true,
            InitialDirectory = AppServices.Paths.ExportsDirectory
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var text = new TextRange(_document.ContentStart, _document.ContentEnd).Text.TrimEnd();
            var sb = new StringBuilder();
            sb.AppendLine("# FluentSerial 控制台导出");
            sb.AppendLine($"# 端口: {AppServices.Serial.PortLabel}  {AppServices.Serial.ParamsLabel}");
            sb.AppendLine($"# 导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.Append(text);
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(false));
            SetTransientHint($"已保存到 {Path.GetFileName(dialog.FileName)}");
            AppServices.Audit.Add(AuditLevel.Info, "record", "导出控制台内容", dialog.FileName);
        }
        catch (Exception ex)
        {
            AppServices.LogError("保存控制台失败", ex);
            AppServices.ShowError($"保存失败：{ex.Message}");
        }
    }

    private void Console_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.L)
        {
            ClearConsole_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.S)
        {
            SaveConsole_Click(sender, e);
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- send

    private void SendBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Let the IME consume Enter while a Chinese/Japanese composition is active.
        if (e.Key == Key.ImeProcessed || e.ImeProcessedKey == Key.Enter) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            DoSend();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Control)
        {
            RecallHistory(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.Control)
        {
            RecallHistory(1);
            e.Handled = true;
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => DoSend();

    private bool DoSend()
    {
        if (IsSendingFile) return false;
        if (!AppServices.Serial.IsOpen)
        {
            SetTransientHint("串口未打开，请先打开端口", error: true);
            return false;
        }
        var input = SendBox.Text;
        var mode = TxHexRadio.IsChecked == true ? PayloadMode.Hex : PayloadMode.Text;
        var ending = ChoiceValue(EndingBox, LineEnding.None);
        var checksum = ChoiceValue(ChecksumBox, ChecksumKind.None);
        var escapes = EscapeCheck.IsChecked == true;
        if (!SerialCodec.TryBuildPayload(input, mode, ending, checksum, _encodingName, escapes, out var payload, out var error))
        {
            SetTransientHint(error ?? "无法生成发送数据", error: true);
            return false;
        }
        if (payload.Length == 0)
        {
            SetTransientHint("发送内容为空", error: true);
            return false;
        }
        if (!AppServices.Serial.Send(payload))
        {
            SetTransientHint(AppServices.Serial.LastError.Length > 0 ? AppServices.Serial.LastError : "发送失败", error: true);
            return false;
        }

        _sendHistory.Remove(input);
        _sendHistory.Insert(0, input);
        while (_sendHistory.Count > 30) _sendHistory.RemoveAt(_sendHistory.Count - 1);
        _sendHistoryIndex = -1;

        var summary = payload.Length <= 16
            ? SerialCodec.FormatHex(payload)
            : SerialCodec.FormatHex(payload.AsSpan(0, 16)) + " …";
        SetTransientHint($"已发送 {payload.Length} 字节  {summary}");
        UpdateSendSize();
        return true;
    }

    private void RecallHistory(int direction)
    {
        if (_sendHistory.Count == 0) return;
        if (_sendHistoryIndex < 0)
        {
            if (direction > 0) return;
            _historyDraft = SendBox.Text;
        }
        _sendHistoryIndex = Math.Clamp(_sendHistoryIndex + (direction < 0 ? 1 : -1), -1, _sendHistory.Count - 1);
        SendBox.Text = _sendHistoryIndex < 0 ? _historyDraft : _sendHistory[_sendHistoryIndex];
        SendBox.CaretIndex = SendBox.Text.Length;
        UpdateSendSize();
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        if (_sendHistory.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "暂无发送历史", IsEnabled = false });
        }
        else
        {
            foreach (var item in _sendHistory.Take(15))
            {
                var text = item;
                var entry = new MenuItem
                {
                    Header = text.Length > 60 ? text[..60] + "…" : text.Replace("\n", "\\n")
                };
                entry.Click += (_, _) =>
                {
                    SendBox.Text = text;
                    SendBox.CaretIndex = SendBox.Text.Length;
                    UpdateSendSize();
                };
                menu.Items.Add(entry);
            }
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "清除发送历史" };
            clear.Click += (_, _) =>
            {
                _sendHistory.Clear();
                SetTransientHint("发送历史已清除");
            };
            menu.Items.Add(clear);
        }
        menu.PlacementTarget = HistoryButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void AutoSend_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _loadingUi) return;
        if (AutoSendCheck.IsChecked == true)
        {
            if (IsSendingFile) { AutoSendCheck.IsChecked = false; return; }
            if (!AppServices.Serial.IsOpen)
            {
                AutoSendCheck.IsChecked = false;
                SetTransientHint("请先打开串口，再启用定时发送", error: true);
                return;
            }
            if (!int.TryParse(AutoSendIntervalBox.Text.Trim(), out var interval) || interval < 50 || interval > 60000)
            {
                AutoSendCheck.IsChecked = false;
                SetTransientHint("定时发送间隔需在 50-60000 ms 之间", error: true);
                return;
            }
            Settings.AutoSendIntervalMs = interval;
            if (!TryGetSendPayload(out _, out var payloadError))
            {
                AutoSendCheck.IsChecked = false;
                SetTransientHint(payloadError, error: true);
                return;
            }
            _autoSendTimer.Interval = TimeSpan.FromMilliseconds(interval);
            _autoSendTimer.Start();
            AutoSendIntervalBox.IsEnabled = false;
            SetTransientHint($"定时发送已启动，每 {interval} ms 发送一次");
        }
        else
        {
            _autoSendTimer.Stop();
            AutoSendIntervalBox.IsEnabled = true;
            SetTransientHint("定时发送已停止");
        }
    }

    private void AutoSendTick()
    {
        if (_autoSendTicking) return;
        if (!AppServices.Serial.IsOpen)
        {
            AutoSendCheck.IsChecked = false;
            return;
        }
        _autoSendTicking = true;
        try
        {
            if (!DoSend())
            {
                AutoSendCheck.IsChecked = false;
                SetTransientHint("定时发送已停止，请检查发送内容或串口连接", error: true);
            }
        }
        finally
        {
            _autoSendTicking = false;
        }
    }

    private void UpdateSendSize()
    {
        if (!_initialized) return;
        var valid = TryGetSendPayload(out var payload, out var error);
        EscapeCheck.IsEnabled = TxHexRadio.IsChecked != true;
        SendButton.IsEnabled = valid && AppServices.Serial.IsOpen && !IsSendingFile;
        SendButton.ToolTip = !AppServices.Serial.IsOpen ? "请先打开串口" : valid ? "发送 (Ctrl+Enter)" : error;
        AutoSendCheck.IsEnabled = AppServices.Serial.IsOpen && !IsSendingFile;
        UpdateFileSendButton();
        SendSizeText.Text = valid ? $"{payload.Length} B" : "0 B";
        SendSizeText.Foreground = (Brush)FindResource(valid ? "AppTextTertiaryBrush" : "AppDangerBrush");
        SendPreviewText.Text = string.IsNullOrEmpty(SendBox.Text) ? valid ? "仅发送行尾 / 校验  " + SerialCodec.FormatHex(payload) : "输入发送内容，在这里预览实际字节" : valid
            ? "HEX  " + SerialCodec.FormatHex(payload.AsSpan(0, Math.Min(payload.Length, 48))) + (payload.Length > 48 ? " …" : string.Empty)
            : error;
        SendPreviewText.ToolTip = SendPreviewText.Text;
        SendPreviewText.Foreground = (Brush)FindResource(valid || string.IsNullOrEmpty(SendBox.Text) ? "AppTextTertiaryBrush" : "AppDangerBrush");
    }

    private bool TryGetSendPayload(out byte[] payload, out string error)
    {
        payload = Array.Empty<byte>();
        error = "请输入发送内容";
        var mode = TxHexRadio.IsChecked == true ? PayloadMode.Hex : PayloadMode.Text;
        var ending = ChoiceValue(EndingBox, LineEnding.None);
        var checksum = ChoiceValue(ChecksumBox, ChecksumKind.None);
        var escapes = EscapeCheck.IsChecked == true;
        var valid = SerialCodec.TryBuildPayload(SendBox.Text, mode, ending, checksum, _encodingName, escapes, out payload, out var reason);
        error = reason ?? (payload.Length == 0 ? "发送内容为空" : string.Empty);
        return valid && payload.Length > 0;
    }

    private void SendBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSendSize();

    private void SetTransientHint(string message, bool error = false)
    {
        SendHintText.Text = message;
        SendHintText.Foreground = (Brush)FindResource(error ? "AppDangerBrush" : "AppTextTertiaryBrush");
    }

    // ---------------------------------------------------------------- quick commands

    private void UpdateQuickCommands()
    {
        if (!_initialized) return;
        QuickCommandPanel.Children.Clear();
        var commands = AppServices.Data.Commands
            .Where(c => c.Favorite)
            .Concat(AppServices.Data.Commands.Where(c => !c.Favorite))
            .Take(7)
            .ToList();
        if (commands.Count == 0)
        {
            QuickCommandPanel.Children.Add(new TextBlock
            {
                Text = "暂无指令",
                Style = (Style)FindResource("CaptionTextStyle"),
                VerticalAlignment = VerticalAlignment.Center
            });
            return;
        }
        foreach (var command in commands)
        {
            var button = new Button
            {
                Style = (Style)FindResource("ChipButtonStyle"),
                Content = command.Name,
                Tag = command,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = $"{command.Summary}\n{command.ModeLabel}  行尾 {command.EndingLabel}"
            };
            button.Click += QuickCommand_Click;
            QuickCommandPanel.Children.Add(button);
        }
    }

    private void QuickCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuickCommand command }) SendQuickCommand(command);
    }

    public void SendQuickCommand(QuickCommand command)
    {
        if (command is null) return;
        if (IsSendingFile) { SetTransientHint("文件正在发送，请先取消或等待完成", error: true); return; }
        _loadingUi = true;
        try
        {
            SendBox.Text = command.Payload;
            if (command.Mode == PayloadMode.Hex) TxHexRadio.IsChecked = true; else TxTextRadio.IsChecked = true;
            SelectChoice(EndingBox, command.Ending);
            SelectChoice(ChecksumBox, command.Checksum);
        }
        finally
        {
            _loadingUi = false;
        }
        UpdateSendSize();

        if (!AppServices.Serial.IsOpen)
        {
            SetTransientHint($"串口未打开，指令“{command.Name}”已载入发送区");
            SendBox.Focus();
            return;
        }

        if (!SerialCodec.TryBuildPayload(command.Payload, command.Mode, command.Ending, command.Checksum,
                _encodingName, Settings.UseEscapes, out var bytes, out var error))
        {
            SetTransientHint($"指令“{command.Name}”无法发送：{error}", error: true);
            return;
        }
        if (AppServices.Serial.Send(bytes))
            SetTransientHint($"已发送指令“{command.Name}”（{bytes.Length} 字节）");
        else
            SetTransientHint(AppServices.Serial.LastError, error: true);
    }

    private void ManageCommands_Click(object sender, RoutedEventArgs e)
    {
        AppServices.MainWindow?.Navigate("snippets");
    }

    // ---------------------------------------------------------------- capture support

    /// <summary>Opens the built-in DEMO loopback and produces a realistic exchange for docs screenshots.</summary>
    public async Task OpenDemoForCaptureAsync()
    {
        RefreshPorts();
        SelectPortByName("DEMO");
        if (!AppServices.Serial.IsOpen) OpenCurrent();
        SendBox.Text = "AT+VER?";
        TxTextRadio.IsChecked = true;
        SelectChoice(EndingBox, LineEnding.CrLf);
        await Task.Delay(450);
        if (RxHexRadio.IsChecked == true)
        {
            // Keep the HEX screenshot focused on one exchange instead of a scrolled history.
            ResetConsole();
            AddSystemLine($"已打开 {AppServices.Serial.PortLabel}  {AppServices.Serial.ParamsLabel}  HEX 视图", ConsoleTone.Success);
            await Task.Delay(200);
        }
        SendRawForCapture("AT+VER?");
        await Task.Delay(500);
        SendRawForCapture("ATI");
        await Task.Delay(500);
        UpdateSendSize();
    }

    private void SendRawForCapture(string text)
    {
        if (!AppServices.Serial.IsOpen) return;
        var body = SerialCodec.EncodeText(text, "ascii", false);
        var ending = SerialCodec.LineEndingBytes(LineEnding.CrLf);
        var payload = new byte[body.Length + ending.Length];
        body.CopyTo(payload, 0);
        ending.CopyTo(payload, body.Length);
        AppServices.Serial.Send(payload);
    }

    // ---------------------------------------------------------------- UI smoke hooks

    public int ConsoleLineCount => _document.Blocks.Count;

    /// <summary>Deterministic rendering regressions; invoke after closing the smoke DEMO session.</summary>
    public IReadOnlyList<(string Name, bool Ok, string Detail)> CheckRenderingForSmoke()
    {
        var checks = new List<(string Name, bool Ok, string Detail)>();
        var previousMode = RxHexRadio.IsChecked == true;
        var previousEncoding = _encodingName;
        var previousMaxLines = Settings.MaxLines;
        string Text() => new TextRange(_document.ContentStart, _document.ContentEnd).Text;
        void Drain()
        {
            FlushRxLine();
            for (var i = 0; i < 110 && (_reRenderRequested || _pendingLines.Count > 0); i++)
                FlushRenderQueue();
        }
        void Check(string name, bool ok, string detail = "") => checks.Add((name, ok, detail));
        var at = DateTime.Now;
        try
        {
            _paused = false;
            _encodingName = "utf-8";
            RxTextRadio.IsChecked = true;
            ResetConsole();
            PushTraffic(SerialTraffic.Rx(at, Encoding.UTF8.GetBytes("Hello\r\n")));
            _reRenderRequested = true;
            Drain();
            Check("文本历史重排保留文本", Text().Contains("Hello", StringComparison.Ordinal));

            RxHexRadio.IsChecked = true;
            Drain();
            Check("HEX 历史重排显示字节", Text().Contains("48 65 6C 6C 6F", StringComparison.Ordinal));

            RxTextRadio.IsChecked = true;
            ResetConsole();
            PushTraffic(SerialTraffic.Rx(at, new byte[] { 0xE4, 0xB8 }));
            _reRenderRequested = true;
            Drain();
            PushTraffic(SerialTraffic.Rx(at.AddMilliseconds(20), new byte[] { 0xAD, 13, 10 }));
            Drain();
            Check("重排后保留跨块 UTF-8 字符", Text().Contains("中", StringComparison.Ordinal) && !Text().Contains('\uFFFD'));

            ResetConsole();
            PushTraffic(SerialTraffic.Rx(at, Encoding.UTF8.GetBytes("A\r")));
            Drain();
            PushTraffic(SerialTraffic.Rx(at.AddSeconds(1), Encoding.UTF8.GetBytes("\nB\r\n")));
            Drain();
            Check("延迟 CRLF 不产生多余空行", ConsoleLineCount == 2, $"{ConsoleLineCount} lines");

            var frozen = Text();
            Pause_Click(this, new RoutedEventArgs());
            PushTraffic(SerialTraffic.Rx(at.AddSeconds(2), Encoding.UTF8.GetBytes("paused-payload\r\n")));
            _reRenderRequested = true;
            FlushRenderQueue();
            Check("暂停时选项重排保持画面不变", Text() == frozen && _pendingLines.Count == 0);
            Pause_Click(this, new RoutedEventArgs());
            Drain();
            Check("恢复后重放暂停期间数据", Text().Contains("paused-payload", StringComparison.Ordinal));

            ResetConsole();
            Settings.MaxLines = 200;
            PushTraffic(SerialTraffic.Rx(at, Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("line\n", 1000)))));
            Check("待渲染队列有上限", _pendingLines.Count <= 200, $"{_pendingLines.Count} lines");
            Drain();
            Check("可见控制台遵守行数限制", ConsoleLineCount == 200, $"{ConsoleLineCount} lines");

            ResetConsole();
            PushTraffic(SerialTraffic.Rx(at, Encoding.UTF8.GetBytes(new string('x', MaxRxLineLength * 2 + 1))));
            Check("无换行连续数据分段处理", _rxLine.Length <= MaxRxLineLength && _pendingLines.Count == 2);

            ResetConsole();
            PushTraffic(SerialTraffic.Rx(at, Encoding.UTF8.GetBytes(new string('x', MaxRxLineLength) + "\r\n")));
            Drain();
            Check("恰好达到分段长度不产生空行", ConsoleLineCount == 1, $"{ConsoleLineCount} lines");

            ResetConsole();
            PushTraffic(SerialTraffic.Rx(at, Encoding.UTF8.GetBytes(new string('x', MaxRxLineLength - 1) + "\U0001F600y\n")));
            Drain();
            Check("长行分段保留完整 emoji", Text().Contains("\U0001F600", StringComparison.Ordinal), $"{ConsoleLineCount} lines");
        }
        finally
        {
            Settings.MaxLines = previousMaxLines;
            _encodingName = previousEncoding;
            _paused = false;
            PauseButton.Content = "暂停";
            if (previousMode) RxHexRadio.IsChecked = true; else RxTextRadio.IsChecked = true;
            ResetConsole();
            UpdateUiState();
        }
        return checks;
    }

    public async Task<bool> OpenDemoForSmokeAsync()
    {
        RefreshPorts();
        SelectPortByName("DEMO");
        if (!AppServices.Serial.IsOpen) OpenCurrent();
        await Task.Delay(800);
        return AppServices.Serial.IsOpen;
    }

    public bool SendForSmoke(string text)
    {
        SendBox.Text = text;
        TxTextRadio.IsChecked = true;
        var before = AppServices.Serial.BytesTx;
        DoSend();
        return AppServices.Serial.BytesTx > before;
    }

    public void ToggleHexForSmoke()
    {
        RxHexRadio.IsChecked = RxHexRadio.IsChecked != true;
    }

    public void CloseForSmoke() => AppServices.Serial.Close("UI 冒烟测试");

    /// <summary>Global Ctrl+L invokes the same clear flow as the console toolbar button.</summary>
    public void ClearConsoleFromShortcut() => ClearConsole_Click(this, new RoutedEventArgs());

    /// <summary>Global Ctrl+S invokes the same save flow as the console toolbar button.</summary>
    public void SaveConsoleFromShortcut() => SaveConsole_Click(this, new RoutedEventArgs());
}
