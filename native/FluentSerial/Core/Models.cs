using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FluentSerial.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    protected void Raise([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum PayloadMode
{
    Text,
    Hex
}

public enum LineEnding
{
    None,
    Cr,
    Lf,
    CrLf
}

public enum ChecksumKind
{
    None,
    Sum8,
    Xor8,
    Crc16Modbus,
    Crc16Ccitt
}

public enum AuditLevel
{
    Info,
    Warning,
    Error
}

public static class FormatText
{
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
    }

    public static string Relative(DateTime time)
    {
        var diff = DateTime.Now - time;
        if (diff.TotalSeconds < 45) return "刚刚";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} 分钟前";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} 小时前";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} 天前";
        return time.ToString("yyyy-MM-dd");
    }

    public static string Duration(double milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒";
        return $"{Math.Max(1, (int)span.TotalSeconds)} 秒";
    }

    public static string ParityCode(Parity parity) => parity switch
    {
        Parity.Odd => "O",
        Parity.Even => "E",
        Parity.Mark => "M",
        Parity.Space => "S",
        _ => "N"
    };

    public static string StopBitsCode(StopBits stopBits) => stopBits switch
    {
        StopBits.OnePointFive => "1.5",
        StopBits.Two => "2",
        _ => "1"
    };

    public static string ParityLabel(Parity parity) => parity switch
    {
        Parity.Odd => "奇校验",
        Parity.Even => "偶校验",
        Parity.Mark => "标记",
        Parity.Space => "空号",
        _ => "无校验"
    };

    public static string StopBitsLabel(StopBits stopBits) => stopBits switch
    {
        StopBits.OnePointFive => "1.5 位",
        StopBits.Two => "2 位",
        _ => "1 位"
    };

    public static string HandshakeLabel(Handshake handshake) => handshake switch
    {
        Handshake.XOnXOff => "软件 XON/XOFF",
        Handshake.RequestToSend => "硬件 RTS/CTS",
        Handshake.RequestToSendXOnXOff => "硬件 + 软件",
        _ => "无流控"
    };

    public static string ModeLabel(PayloadMode mode) => mode == PayloadMode.Hex ? "HEX" : "文本";

    public static string EndingLabel(LineEnding ending) => ending switch
    {
        LineEnding.Cr => "CR",
        LineEnding.Lf => "LF",
        LineEnding.CrLf => "CRLF",
        _ => "无"
    };

    public static string ChecksumLabel(ChecksumKind kind) => kind switch
    {
        ChecksumKind.Sum8 => "SUM8",
        ChecksumKind.Xor8 => "XOR8",
        ChecksumKind.Crc16Modbus => "CRC16-Modbus",
        ChecksumKind.Crc16Ccitt => "CRC16-CCITT",
        _ => "无"
    };

    public static string EncodingLabel(string name) => name switch
    {
        "utf-8" => "UTF-8",
        "gbk" or "gb2312" or "gb18030" => "GBK",
        "ascii" => "ASCII",
        "iso-8859-1" => "Latin-1",
        _ => string.IsNullOrWhiteSpace(name) ? "UTF-8" : name.ToUpperInvariant()
    };

    public static string PayloadPreview(string payload, int maxLength = 64)
    {
        if (string.IsNullOrEmpty(payload)) return "空载荷";
        var text = payload.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t").Trim();
        if (text.Length > maxLength) text = text[..maxLength] + "…";
        return text;
    }
}

/// <summary>A saved serial-port configuration, equivalent to a host profile.</summary>
public sealed class PortProfile : ObservableObject
{
    private string _name = string.Empty;
    private string _portName = string.Empty;
    private int _baudRate = 115200;
    private int _dataBits = 8;
    private Parity _parity = Parity.None;
    private StopBits _stopBits = StopBits.One;
    private Handshake _handshake = Handshake.None;
    private bool _dtr = true;
    private bool _rts = true;
    private PayloadMode _receiveMode = PayloadMode.Text;
    private PayloadMode _sendMode = PayloadMode.Text;
    private string _encoding = "utf-8";
    private bool _favorite;
    private string _notes = string.Empty;
    private DateTime? _lastUsedAt;
    private int _useCount;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set { if (Set(ref _name, value)) Raise(nameof(Initials)); } }
    public string PortName { get => _portName; set { if (Set(ref _portName, value)) Raise(nameof(PortLabel)); } }
    public int BaudRate { get => _baudRate; set { if (Set(ref _baudRate, value)) Raise(nameof(ParamsLabel)); } }
    public int DataBits { get => _dataBits; set { if (Set(ref _dataBits, value)) Raise(nameof(ParamsLabel)); } }
    public Parity Parity { get => _parity; set { if (Set(ref _parity, value)) Raise(nameof(ParamsLabel)); } }
    public StopBits StopBits { get => _stopBits; set { if (Set(ref _stopBits, value)) Raise(nameof(ParamsLabel)); } }
    public Handshake Handshake { get => _handshake; set { if (Set(ref _handshake, value)) Raise(nameof(FlowLabel)); } }
    public bool Dtr { get => _dtr; set => Set(ref _dtr, value); }
    public bool Rts { get => _rts; set => Set(ref _rts, value); }
    public PayloadMode ReceiveMode { get => _receiveMode; set => Set(ref _receiveMode, value); }
    public PayloadMode SendMode { get => _sendMode; set => Set(ref _sendMode, value); }
    public string Encoding { get => _encoding; set { if (Set(ref _encoding, value)) Raise(nameof(EncodingLabel)); } }
    public bool Favorite { get => _favorite; set => Set(ref _favorite, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public DateTime? LastUsedAt { get => _lastUsedAt; set { if (Set(ref _lastUsedAt, value)) Raise(nameof(LastUsedLabel)); } }
    public int UseCount { get => _useCount; set => Set(ref _useCount, value); }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonIgnore] public string PortLabel => string.IsNullOrWhiteSpace(PortName) ? "自动选择" : PortName;
    [JsonIgnore] public string ParamsLabel => $"{BaudRate} {DataBits}-{FormatText.ParityCode(Parity)}-{FormatText.StopBitsCode(StopBits)}";
    [JsonIgnore] public string FlowLabel => FormatText.HandshakeLabel(Handshake);
    [JsonIgnore] public string EncodingLabel => FormatText.EncodingLabel(Encoding);
    [JsonIgnore] public string ModeLabel => FormatText.ModeLabel(ReceiveMode);
    [JsonIgnore] public string LastUsedLabel => LastUsedAt is null ? "尚未使用" : FormatText.Relative(LastUsedAt.Value);
    [JsonIgnore] public string Initials
    {
        get
        {
            var trimmed = Name.Trim();
            if (trimmed.Length == 0) return "?";
            var first = trimmed[0];
            return char.IsLetter(first) ? first.ToString().ToUpperInvariant() : first.ToString();
        }
    }
}

/// <summary>A reusable command fragment that can be sent with one click.</summary>
public sealed class QuickCommand : ObservableObject
{
    private string _name = string.Empty;
    private string _payload = string.Empty;
    private string _group = "自定义";
    private string _description = string.Empty;
    private PayloadMode _mode = PayloadMode.Text;
    private LineEnding _ending = LineEnding.CrLf;
    private ChecksumKind _checksum = ChecksumKind.None;
    private bool _favorite;
    private int _useCount;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Payload { get => _payload; set { if (Set(ref _payload, value)) Raise(nameof(PayloadPreview)); } }
    public string Group { get => _group; set => Set(ref _group, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public PayloadMode Mode { get => _mode; set { if (Set(ref _mode, value)) Raise(nameof(ModeLabel)); } }
    public LineEnding Ending { get => _ending; set { if (Set(ref _ending, value)) Raise(nameof(EndingLabel)); } }
    public ChecksumKind Checksum { get => _checksum; set { if (Set(ref _checksum, value)) Raise(nameof(ChecksumLabel)); } }
    public bool Favorite { get => _favorite; set => Set(ref _favorite, value); }
    public int UseCount { get => _useCount; set => Set(ref _useCount, value); }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonIgnore] public string PayloadPreview => FormatText.PayloadPreview(Payload);
    [JsonIgnore] public string ModeLabel => FormatText.ModeLabel(Mode);
    [JsonIgnore] public string EndingLabel => FormatText.EndingLabel(Ending);
    [JsonIgnore] public string ChecksumLabel => FormatText.ChecksumLabel(Checksum);
    [JsonIgnore] public string Summary => string.IsNullOrWhiteSpace(Description) ? PayloadPreview : Description;
}

/// <summary>Metadata for one recorded serial session.</summary>
public sealed class SerialSessionRecord : ObservableObject
{
    private DateTime? _endedAt;
    private long _bytesRx;
    private long _bytesTx;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PortName { get; set; } = string.Empty;
    public string ParamsLabel { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? EndedAt { get => _endedAt; set { if (Set(ref _endedAt, value)) { Raise(nameof(DurationLabel)); Raise(nameof(TimeLabel)); } } }
    public long BytesRx { get => _bytesRx; set { if (Set(ref _bytesRx, value)) Raise(nameof(RxLabel)); } }
    public long BytesTx { get => _bytesTx; set { if (Set(ref _bytesTx, value)) Raise(nameof(TxLabel)); } }
    public string FilePath { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    [JsonIgnore] public string DisplayPort => string.IsNullOrWhiteSpace(ProfileName) ? PortName : $"{ProfileName}  {PortName}";
    [JsonIgnore] public string TimeLabel => StartedAt.ToString("MM-dd HH:mm:ss");
    [JsonIgnore] public string DurationLabel => FormatText.Duration(((EndedAt ?? DateTime.Now) - StartedAt).TotalMilliseconds);
    [JsonIgnore] public string RxLabel => FormatText.Bytes(BytesRx);
    [JsonIgnore] public string TxLabel => FormatText.Bytes(BytesTx);
    [JsonIgnore] public string FileName => string.IsNullOrWhiteSpace(FilePath) ? "—" : System.IO.Path.GetFileName(FilePath);
    [JsonIgnore] public bool HasFile => !string.IsNullOrWhiteSpace(FilePath) && System.IO.File.Exists(FilePath);
}

public sealed class AuditEvent : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime At { get; set; } = DateTime.Now;
    public AuditLevel Level { get; set; } = AuditLevel.Info;
    public string Category { get; set; } = "system";
    public string Message { get; set; } = string.Empty;
    public string? Detail { get; set; }

    [JsonIgnore] public string TimeLabel => At.ToString("MM-dd HH:mm:ss");
    [JsonIgnore] public string CategoryLabel => Category switch
    {
        "connection" => "连接",
        "receive" => "接收",
        "send" => "发送",
        "record" => "记录",
        "profile" => "预设",
        "security" => "安全",
        _ => "系统"
    };
    [JsonIgnore] public string LevelLabel => Level switch
    {
        AuditLevel.Error => "错误",
        AuditLevel.Warning => "警告",
        _ => "信息"
    };
}

public sealed class AppSettings : ObservableObject
{
    private string _theme = "Light";
    private string _fontFamily = "Consolas";
    private double _consoleFontSize = 13.5;
    private string _defaultEncoding = "utf-8";
    private PayloadMode _receiveMode = PayloadMode.Text;
    private PayloadMode _sendMode = PayloadMode.Text;
    private bool _showTimestamp = true;
    private bool _showTx = true;
    private bool _autoScroll = true;
    private int _maxLines = 3000;
    private LineEnding _defaultEnding = LineEnding.CrLf;
    private ChecksumKind _defaultChecksum = ChecksumKind.None;
    private bool _useEscapes = true;
    private int _autoSendIntervalMs = 1000;
    private bool _autoRecord = true;
    private string _lastPortName = string.Empty;
    private int _lastBaudRate = 115200;
    private int _lastDataBits = 8;
    private Parity _lastParity = Parity.None;
    private StopBits _lastStopBits = StopBits.One;
    private Handshake _lastHandshake = Handshake.None;
    private bool _lastDtr = true;
    private bool _lastRts = true;
    private bool _confirmBeforeClear = true;

    public string Theme { get => _theme; set => Set(ref _theme, value); }
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }
    public double ConsoleFontSize { get => _consoleFontSize; set => Set(ref _consoleFontSize, value); }
    public string DefaultEncoding { get => _defaultEncoding; set => Set(ref _defaultEncoding, value); }
    public PayloadMode ReceiveMode { get => _receiveMode; set => Set(ref _receiveMode, value); }
    public PayloadMode SendMode { get => _sendMode; set => Set(ref _sendMode, value); }
    public bool ShowTimestamp { get => _showTimestamp; set => Set(ref _showTimestamp, value); }
    public bool ShowTx { get => _showTx; set => Set(ref _showTx, value); }
    public bool AutoScroll { get => _autoScroll; set => Set(ref _autoScroll, value); }
    public int MaxLines { get => _maxLines; set => Set(ref _maxLines, value); }
    public LineEnding DefaultEnding { get => _defaultEnding; set => Set(ref _defaultEnding, value); }
    public ChecksumKind DefaultChecksum { get => _defaultChecksum; set => Set(ref _defaultChecksum, value); }
    public bool UseEscapes { get => _useEscapes; set => Set(ref _useEscapes, value); }
    public int AutoSendIntervalMs { get => _autoSendIntervalMs; set => Set(ref _autoSendIntervalMs, value); }
    public bool AutoRecord { get => _autoRecord; set => Set(ref _autoRecord, value); }
    public string LastPortName { get => _lastPortName; set => Set(ref _lastPortName, value); }
    public int LastBaudRate { get => _lastBaudRate; set => Set(ref _lastBaudRate, value); }
    public int LastDataBits { get => _lastDataBits; set => Set(ref _lastDataBits, value); }
    public Parity LastParity { get => _lastParity; set => Set(ref _lastParity, value); }
    public StopBits LastStopBits { get => _lastStopBits; set => Set(ref _lastStopBits, value); }
    public Handshake LastHandshake { get => _lastHandshake; set => Set(ref _lastHandshake, value); }
    public bool LastDtr { get => _lastDtr; set => Set(ref _lastDtr, value); }
    public bool LastRts { get => _lastRts; set => Set(ref _lastRts, value); }
    public bool ConfirmBeforeClear { get => _confirmBeforeClear; set => Set(ref _confirmBeforeClear, value); }
}

public sealed class AppData
{
    public int SchemaVersion { get; set; } = 1;
    public ObservableCollection<PortProfile> Profiles { get; set; } = new();
    public ObservableCollection<QuickCommand> Commands { get; set; } = new();
    public ObservableCollection<SerialSessionRecord> Sessions { get; set; } = new();
    public ObservableCollection<AuditEvent> Audit { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}
