using System.Globalization;
using System.Text;

namespace FluentSerial.Core;

/// <summary>
/// Built-in loopback that behaves like a small AT-command sensor module. It needs no hardware:
/// opening it starts a welcome banner plus a periodic telemetry line, and every write is answered
/// with a deterministic AT response (or echoed back when it is not an AT command).
/// </summary>
public sealed class DemoSerialTransport : ISerialTransport
{
    private const string WelcomeLine = "FluentSerial DEMO 串口模拟器 v1.0\r\n";
    private const string IdentityLine = "设备: DemoSensor-01  固件: 2.4.1\r\n";

    private readonly object _sync = new();
    private readonly List<byte> _buffer = new();

    private Timer? _welcomeTimer;
    private Timer? _identityTimer;
    private Timer? _telemetryTimer;
    private Timer? _partialTimer;
    private int _bufferVersion;
    private bool _open;
    private int _closed = 1;

    public string PortName => SerialPortCatalog.DemoName;

    public bool IsOpen
    {
        get
        {
            lock (_sync) return _open;
        }
    }

    /// <summary>Last state applied through <see cref="SetDtr"/> (the simulator has no lines to toggle).</summary>
    public bool Dtr { get; private set; } = true;

    /// <summary>Last state applied through <see cref="SetRts"/> (the simulator has no lines to toggle).</summary>
    public bool Rts { get; private set; } = true;

    public event Action<byte[]>? DataReceived;
#pragma warning disable CS0067 // the built-in loopback never fails or unplugs; the events exist for interface parity
    public event Action<string>? ErrorOccurred;
    public event Action<string>? Disconnected;
#pragma warning restore CS0067

    public void Open(SerialConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_sync)
        {
            if (_open) throw new InvalidOperationException("模拟串口已经打开，请先关闭当前连接。");
            _open = true;
            Volatile.Write(ref _closed, 0);
            _buffer.Clear();

            // The UI calls Open synchronously; timers keep the actual data flow asynchronous.
            _welcomeTimer = new Timer(_ => EmitLine(WelcomeLine), null, 300, Timeout.Infinite);
            _identityTimer = new Timer(_ => EmitLine(IdentityLine), null, 600, Timeout.Infinite);
            _telemetryTimer = new Timer(_ => EmitLine(NextTelemetryLine()), null, 1200, 1200);
        }
    }

    public void Write(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        lock (_sync)
        {
            if (!_open) throw new InvalidOperationException("模拟串口尚未打开，无法发送数据。");
            if (data.Length == 0) return;

            _buffer.AddRange(data);
            _bufferVersion++;

            // Responses are produced on the thread pool so a write never re-enters the caller.
            ThreadPool.QueueUserWorkItem(_ => ProcessBuffer(flushRemainder: false));
        }
    }

    public void SetDtr(bool value)
    {
        lock (_sync) Dtr = value;
    }

    public void SetRts(bool value)
    {
        lock (_sync) Rts = value;
    }

    public void SendBreak(int milliseconds)
    {
        // No hardware line to break; the simulator simply ignores it.
    }

    public void Close()
    {
        Timer? welcome;
        Timer? identity;
        Timer? telemetry;
        Timer? partial;

        lock (_sync)
        {
            if (_welcomeTimer is null && _identityTimer is null && _telemetryTimer is null && _partialTimer is null && !_open)
            {
                Volatile.Write(ref _closed, 1);
                _buffer.Clear();
                return;
            }

            _open = false;
            Volatile.Write(ref _closed, 1);
            _buffer.Clear();
            _bufferVersion++;

            welcome = _welcomeTimer;
            identity = _identityTimer;
            telemetry = _telemetryTimer;
            partial = _partialTimer;
            _welcomeTimer = null;
            _identityTimer = null;
            _telemetryTimer = null;
            _partialTimer = null;
        }

        DisposeTimer(welcome);
        DisposeTimer(identity);
        DisposeTimer(telemetry);
        DisposeTimer(partial);
    }

    public void Dispose() => Close();

    private void ProcessBuffer(bool flushRemainder)
    {
        var lines = new List<string>();
        lock (_sync)
        {
            if (!_open) return;

            if (flushRemainder)
            {
                if (_buffer.Count == 0) return;
                lines.Add(Encoding.UTF8.GetString(_buffer.ToArray()));
                _buffer.Clear();
                DisposeTimerLocked(ref _partialTimer);
            }
            else
            {
                while (TryTakeLine(out var line)) lines.Add(line);
                if (_buffer.Count > 0) SchedulePartialFlushLocked();
            }
        }

        foreach (var line in lines)
        {
            var response = BuildResponse(line);
            if (response is not null) EmitLine(response);
        }
    }

    private bool TryTakeLine(out string line)
    {
        for (var i = 0; i < _buffer.Count; i++)
        {
            var ch = _buffer[i];
            if (ch is not (0x0D or 0x0A)) continue;

            line = Encoding.UTF8.GetString(_buffer.GetRange(0, i).ToArray());
            var skip = ch == 0x0D && i + 1 < _buffer.Count && _buffer[i + 1] == 0x0A ? 2 : 1;
            _buffer.RemoveRange(0, i + skip);
            return true;
        }

        line = string.Empty;
        return false;
    }

    private void SchedulePartialFlushLocked()
    {
        DisposeTimerLocked(ref _partialTimer);
        var version = _bufferVersion;
        _partialTimer = new Timer(_ =>
        {
            lock (_sync)
            {
                if (!_open || version != _bufferVersion || _buffer.Count == 0) return;
            }
            ProcessBuffer(flushRemainder: true);
        }, null, 45, Timeout.Infinite);
    }

    private static void DisposeTimerLocked(ref Timer? timer)
    {
        var current = timer;
        timer = null;
        DisposeTimer(current);
    }

    private static void DisposeTimer(Timer? timer)
    {
        if (timer is null) return;
        try { timer.Dispose(); } catch { /* best effort */ }
    }

    private static string? BuildResponse(string line)
    {
        var text = line.Trim();
        if (text.Length == 0) return null;

        var upper = text.ToUpperInvariant();
        return upper switch
        {
            "AT" => "OK\r\n",
            "AT+VER?" or "AT+VER" => "+VER: 2.4.1\r\nOK\r\n",
            "ATI" => "FluentSerial DEMO\r\nOK\r\n",
            "ATE0" or "ATE1" => "OK\r\n",
            "AT+RST" => "OK\r\n",
            _ when upper.StartsWith("AT", StringComparison.Ordinal) => "OK\r\n",
            _ => "ECHO: " + text + "\r\n"
        };
    }

    private static string NextTelemetryLine()
    {
        var random = Random.Shared;
        var temperature = 23.6 + (random.NextDouble() - 0.5) * 0.8;
        var humidity = 41 + random.Next(-2, 3);
        var rssi = -58 + random.Next(-4, 5);
        return FormattableString.Invariant($"[SENSOR] temp={temperature:0.0}C hum={humidity}% rssi={rssi}dBm\r\n");
    }

    private void EmitLine(string line) => Emit(Encoding.UTF8.GetBytes(line));

    private void Emit(byte[] payload)
    {
        lock (_sync)
        {
            if (!_open) return;
        }
        RaiseData(payload);
    }

    private void RaiseData(byte[] payload)
    {
        if (Volatile.Read(ref _closed) == 1) return;
        InvokeHandlers(DataReceived, handler => handler(payload));
    }

    private static void InvokeHandlers<T>(T? handlers, Action<T> invoke) where T : Delegate
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                invoke((T)handler);
            }
            catch (Exception ex)
            {
                ReportHandlerFailure(ex);
            }
        }
    }

    private static void ReportHandlerFailure(Exception ex)
    {
        try
        {
            AppServices.LogError("模拟串口事件处理异常", ex);
        }
        catch
        {
            // diagnostics must never break the simulator
        }
    }
}
