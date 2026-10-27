using System.IO.Ports;

namespace FluentSerial.Core;

public enum SerialLinkState
{
    Closed,
    Opening,
    Open,
    Error
}

public enum TrafficDirection
{
    Rx,
    Tx,
    System
}

/// <summary>Everything needed to open a serial port, independent of any saved profile.</summary>
public sealed class SerialConnectionOptions
{
    public string PortName { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Handshake Handshake { get; set; } = Handshake.None;
    public bool Dtr { get; set; } = true;
    public bool Rts { get; set; } = true;
    public string Encoding { get; set; } = "utf-8";
    public PayloadMode ReceiveMode { get; set; } = PayloadMode.Text;
    public PayloadMode SendMode { get; set; } = PayloadMode.Text;
    public string? ProfileId { get; set; }
    public string ProfileName { get; set; } = string.Empty;

    public string ParamsLabel => $"{BaudRate} {DataBits}-{FormatText.ParityCode(Parity)}-{FormatText.StopBitsCode(StopBits)}";
    public string PortLabel => IsDemo ? "DEMO 回环" : PortName;
    public bool IsDemo => SerialPortCatalog.IsDemo(PortName);

    public SerialConnectionOptions Clone() => new()
    {
        PortName = PortName,
        BaudRate = BaudRate,
        DataBits = DataBits,
        Parity = Parity,
        StopBits = StopBits,
        Handshake = Handshake,
        Dtr = Dtr,
        Rts = Rts,
        Encoding = Encoding,
        ReceiveMode = ReceiveMode,
        SendMode = SendMode,
        ProfileId = ProfileId,
        ProfileName = ProfileName
    };

    public static SerialConnectionOptions FromProfile(PortProfile profile) => new()
    {
        PortName = profile.PortName,
        BaudRate = profile.BaudRate,
        DataBits = profile.DataBits,
        Parity = profile.Parity,
        StopBits = profile.StopBits,
        Handshake = profile.Handshake,
        Dtr = profile.Dtr,
        Rts = profile.Rts,
        Encoding = profile.Encoding,
        ReceiveMode = profile.ReceiveMode,
        SendMode = profile.SendMode,
        ProfileId = profile.Id,
        ProfileName = profile.Name
    };

    public void RememberIn(AppSettings settings)
    {
        settings.LastPortName = PortName;
        settings.LastBaudRate = BaudRate;
        settings.LastDataBits = DataBits;
        settings.LastParity = Parity;
        settings.LastStopBits = StopBits;
        settings.LastHandshake = Handshake;
        settings.LastDtr = Dtr;
        settings.LastRts = Rts;
    }

    public static SerialConnectionOptions FromSettings(AppSettings settings) => new()
    {
        PortName = settings.LastPortName,
        BaudRate = settings.LastBaudRate,
        DataBits = settings.LastDataBits,
        Parity = settings.LastParity,
        StopBits = settings.LastStopBits,
        Handshake = settings.LastHandshake,
        Dtr = settings.LastDtr,
        Rts = settings.LastRts,
        Encoding = settings.DefaultEncoding,
        ReceiveMode = settings.ReceiveMode,
        SendMode = settings.SendMode
    };
}

public sealed class SerialPortInfo
{
    public SerialPortInfo(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }
    public string Description { get; }
    public bool IsDemo => SerialPortCatalog.IsDemo(Name);
    public string Label => string.IsNullOrWhiteSpace(Description) ? Name : $"{Name}  {Description}";
}

/// <summary>One chunk of data or one system line in the serial console.</summary>
public sealed class SerialTraffic
{
    public DateTime At { get; init; } = DateTime.Now;
    public TrafficDirection Direction { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public string? Message { get; init; }

    public int Length => Data.Length;

    public static SerialTraffic Rx(byte[] data) => new() { Direction = TrafficDirection.Rx, Data = data };
    public static SerialTraffic Rx(DateTime at, byte[] data) => new() { At = at, Direction = TrafficDirection.Rx, Data = data };
    public static SerialTraffic Tx(byte[] data) => new() { Direction = TrafficDirection.Tx, Data = data };
    public static SerialTraffic System(string message) => new() { Direction = TrafficDirection.System, Message = message };
}

/// <summary>Transport abstraction: a real Windows serial port or the built-in DEMO loopback.</summary>
public interface ISerialTransport : IDisposable
{
    string PortName { get; }
    bool IsOpen { get; }

    event Action<byte[]>? DataReceived;
    event Action<string>? ErrorOccurred;
    event Action<string>? Disconnected;

    void Open(SerialConnectionOptions options);
    void Close();
    void Write(byte[] data);
    void SetDtr(bool value);
    void SetRts(bool value);
    void SendBreak(int milliseconds);
}
