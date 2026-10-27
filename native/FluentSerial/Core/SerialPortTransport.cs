using System.ComponentModel;
using System.IO;
using System.IO.Ports;

namespace FluentSerial.Core;

/// <summary>
/// <see cref="ISerialTransport"/> over <see cref="SerialPort"/> with a dedicated background
/// read loop. Every failure is surfaced as a friendly Chinese message and no further event is
/// raised once <see cref="Close"/> has completed.
/// </summary>
public sealed class SerialPortTransport : ISerialTransport
{
    private readonly object _sync = new();
    private readonly object _writeSync = new();

    private SerialPort? _port;
    private Thread? _readThread;
    private CancellationTokenSource? _readCts;
    private volatile bool _closing;
    private int _closed = 1;
    private int _disconnectedRaised;

    public string PortName { get; private set; } = string.Empty;

    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                try
                {
                    return _port?.IsOpen == true;
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    public event Action<byte[]>? DataReceived;
    public event Action<string>? ErrorOccurred;
    public event Action<string>? Disconnected;

    public void Open(SerialConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_sync)
        {
            if (_port is not null)
                throw new InvalidOperationException("串口已经打开，请先关闭当前连接。");
        }

        var name = (options.PortName ?? string.Empty).Trim();
        if (name.Length == 0) throw new InvalidOperationException("未指定串口名称，无法打开。");
        PortName = name;

        SerialPort port;
        try
        {
            port = new SerialPort(name, options.BaudRate, options.Parity, options.DataBits, options.StopBits)
            {
                Handshake = options.Handshake,
                DtrEnable = options.Dtr,
                RtsEnable = options.Rts,
                ReadTimeout = 150,
                WriteTimeout = 1500,
                ReadBufferSize = 8192,
                WriteBufferSize = 4096
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(FriendlyOpenError(name, ex), ex);
        }

        try
        {
            port.Open();
        }
        catch (Exception ex)
        {
            try { port.Dispose(); } catch { /* best effort */ }
            throw new InvalidOperationException(FriendlyOpenError(name, ex), ex);
        }

        port.ErrorReceived += OnPortErrorReceived;

        lock (_sync)
        {
            _port = port;
            _closing = false;
            Volatile.Write(ref _closed, 0);
            Interlocked.Exchange(ref _disconnectedRaised, 0);
        }

        StartReadLoop();
    }

    public void Write(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        SerialPort port;
        lock (_sync)
        {
            port = _port ?? throw new InvalidOperationException("串口尚未打开，无法发送数据。");
            if (!port.IsOpen) throw new InvalidOperationException("串口已关闭，无法发送数据。");
        }

        try
        {
            lock (_writeSync)
            {
                port.Write(data, 0, data.Length);
            }
        }
        catch (ObjectDisposedException)
        {
            throw new InvalidOperationException("串口已关闭，无法发送数据。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or TimeoutException or Win32Exception)
        {
            // Writing a device that has just been unplugged must not crash the caller.
            throw new InvalidOperationException(FriendlyOpenError(PortName, ex), ex);
        }
    }

    public void SetDtr(bool value)
    {
        SerialPort? port;
        lock (_sync) port = _port;
        if (port is null) return;
        try
        {
            port.DtrEnable = value;
        }
        catch (Exception ex) when (!_closing)
        {
            RaiseError($"设置 DTR 失败：{FriendlyOpenError(PortName, ex)}");
        }
    }

    public void SetRts(bool value)
    {
        SerialPort? port;
        lock (_sync) port = _port;
        if (port is null) return;
        try
        {
            port.RtsEnable = value;
        }
        catch (Exception ex) when (!_closing)
        {
            RaiseError($"设置 RTS 失败：{FriendlyOpenError(PortName, ex)}");
        }
    }

    public void SendBreak(int milliseconds)
    {
        SerialPort? port;
        lock (_sync) port = _port;
        if (port is null) return;

        var duration = Math.Clamp(milliseconds, 1, 5000);
        try
        {
            port.BreakState = true;
            Thread.Sleep(duration);
            port.BreakState = false;
        }
        catch (Exception ex) when (!_closing)
        {
            RaiseError($"发送 Break 信号失败：{FriendlyOpenError(PortName, ex)}");
        }
        finally
        {
            try { port.BreakState = false; } catch { /* port may already be gone */ }
        }
    }

    public void Close()
    {
        SerialPort? port;
        Thread? thread;
        CancellationTokenSource? cts;

        lock (_sync)
        {
            if (_port is null && _readThread is null)
            {
                Volatile.Write(ref _closed, 1);
                return;
            }

            _closing = true;
            Volatile.Write(ref _closed, 1);

            port = _port;
            _port = null;
            thread = _readThread;
            _readThread = null;
            cts = _readCts;
            _readCts = null;
        }

        try { cts?.Cancel(); } catch { /* best effort */ }
        if (thread is not null && thread != Thread.CurrentThread)
        {
            try { thread.Join(500); } catch { /* best effort */ }
        }
        try { cts?.Dispose(); } catch { /* best effort */ }

        if (port is not null)
        {
            try { port.ErrorReceived -= OnPortErrorReceived; } catch { /* best effort */ }
            try
            {
                if (port.IsOpen) port.Close();
            }
            catch
            {
                // the device may already be gone
            }
            try { port.Dispose(); } catch { /* best effort */ }
        }

        lock (_sync)
        {
            _closing = false;
        }
    }

    public void Dispose() => Close();

    private void StartReadLoop()
    {
        var cts = new CancellationTokenSource();
        var thread = new Thread(() => ReadLoop(cts.Token))
        {
            IsBackground = true,
            Name = "FluentSerial-SerialRead"
        };
        lock (_sync)
        {
            _readCts = cts;
            _readThread = thread;
        }
        thread.Start();
    }

    private void ReadLoop(CancellationToken token)
    {
        var buffer = new byte[8192];
        while (!token.IsCancellationRequested && Volatile.Read(ref _closed) == 0)
        {
            SerialPort? port;
            lock (_sync) port = _port;
            if (port is null) break;

            try
            {
                var available = port.BytesToRead;
                if (available <= 0)
                {
                    if (token.WaitHandle.WaitOne(15)) break;
                    continue;
                }

                var count = Math.Min(available, buffer.Length);
                var read = port.Read(buffer, 0, count);
                if (read <= 0) continue;

                var data = new byte[read];
                Buffer.BlockCopy(buffer, 0, data, 0, read);
                RaiseData(data);
            }
            catch (TimeoutException)
            {
                // BytesToRead raced with an empty buffer; poll again.
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or Win32Exception or NotSupportedException)
            {
                if (_closing || token.IsCancellationRequested) break;
                RaiseDisconnected(FriendlyOpenError(PortName, ex));
                break;
            }
            catch (Exception ex)
            {
                if (_closing || token.IsCancellationRequested) break;
                RaiseError($"读取串口 {PortName} 失败：{ex.Message}");
                if (token.WaitHandle.WaitOne(200)) break;
            }
        }
    }

    private void OnPortErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        if (_closing) return;
        var detail = e.EventType switch
        {
            SerialError.Frame => "帧错误",
            SerialError.Overrun => "缓冲区溢出",
            SerialError.RXOver => "接收缓冲区溢出",
            SerialError.RXParity => "校验错误",
            SerialError.TXFull => "发送缓冲区已满",
            _ => "串口硬件错误"
        };
        RaiseError($"串口 {PortName} {detail}（{e.EventType}），部分数据可能丢失。");
    }

    private void RaiseData(byte[] data)
    {
        if (Volatile.Read(ref _closed) == 1) return;
        InvokeHandlers(DataReceived, handler => handler(data));
    }

    private void RaiseError(string message)
    {
        if (Volatile.Read(ref _closed) == 1) return;
        InvokeHandlers(ErrorOccurred, handler => handler(message));
    }

    private void RaiseDisconnected(string message)
    {
        if (Volatile.Read(ref _closed) == 1) return;
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) == 1) return;
        InvokeHandlers(Disconnected, handler => handler(message));
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
            AppServices.LogError("串口事件处理异常", ex);
        }
        catch
        {
            // diagnostics must never break the read loop
        }
    }

    /// <summary>Maps low-level exceptions to a friendly Chinese message for the status bar.</summary>
    internal static string FriendlyOpenError(string portName, Exception ex)
    {
        var port = string.IsNullOrWhiteSpace(portName) ? "串口" : portName.Trim();
        return ex switch
        {
            UnauthorizedAccessException => $"串口 {port} 被占用或拒绝访问，请关闭占用该串口的程序后重试。",
            FileNotFoundException => $"串口 {port} 不存在，请确认设备已连接、驱动已安装。",
            DirectoryNotFoundException => $"串口 {port} 不存在，请确认设备已连接、驱动已安装。",
            IOException io when LooksLikeMissingPort(io) => $"串口 {port} 不存在，请确认设备已连接、驱动已安装。",
            IOException => $"串口 {port} 通信失败：设备可能被占用、已断开或驱动异常。",
            TimeoutException => $"串口 {port} 操作超时，请稍后重试。",
            ArgumentException => $"串口 {port} 参数无效：{ex.Message}",
            InvalidOperationException => $"串口 {port} 无法打开：{ex.Message}",
            Win32Exception win32 => $"串口 {port} 打开失败（系统错误 {win32.NativeErrorCode}）：{win32.Message}",
            _ => $"打开串口 {port} 失败：{ex.Message}"
        };
    }

    private static bool LooksLikeMissingPort(IOException ex)
    {
        var message = ex.Message;
        return message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
               || message.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
               || message.Contains("不存在", StringComparison.Ordinal);
    }
}
