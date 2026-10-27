using System.IO;

namespace FluentSerial.Core;

/// <summary>
/// The single process-wide serial session used by the console. It owns exactly one
/// <see cref="ISerialTransport"/> plus an optional <see cref="SessionRecorder"/>, keeps the
/// byte/frame counters, writes the audit trail and persists session metadata.
/// <para>
/// Threading: transport events arrive on worker threads (read loop / simulator timers).
/// All public members are thread-safe; events are raised on the calling thread and the UI is
/// responsible for dispatching. No locks are held while user event handlers run, so a handler
/// may safely call back into this service.
/// </para>
/// </summary>
public sealed class SerialSessionService : IDisposable
{
    private readonly object _sync = new();

    private SerialLinkState _state = SerialLinkState.Closed;
    private string _lastError = string.Empty;
    private SerialConnectionOptions? _options;
    private ISerialTransport? _transport;
    private SessionRecorder? _recorder;
    private string? _recordingPath;
    private SerialSessionRecord? _activeRecord;

    private long _bytesRx;
    private long _bytesTx;
    private int _rxFrames;
    private int _txFrames;
    private DateTime? _connectedAt;
    private DateTime _sessionStartedAt = DateTime.Now;
    private DateTime _rateLastAt = DateTime.UtcNow;
    private long _rateLastBytesRx;
    private long _rateLastBytesTx;
    private double _rxBytesPerSecond;
    private double _txBytesPerSecond;
    private bool _disposed;

    private Action<byte[]>? _transportDataHandler;
    private Action<string>? _transportErrorHandler;
    private Action<string>? _transportDisconnectedHandler;

    public event Action? StateChanged;
    public event Action<SerialTraffic>? Traffic;
    public event Action<string>? ErrorOccurred;
    public event Action? CountersChanged;

    public bool IsOpen
    {
        get
        {
            lock (_sync) return _state == SerialLinkState.Open && _transport is not null;
        }
    }

    public SerialLinkState State
    {
        get
        {
            lock (_sync) return _state;
        }
    }

    public string LastError
    {
        get
        {
            lock (_sync) return _lastError;
        }
    }

    public SerialConnectionOptions? Options
    {
        get
        {
            lock (_sync) return _options;
        }
    }

    public string PortLabel
    {
        get
        {
            lock (_sync) return _options?.PortLabel ?? "未连接";
        }
    }

    public string ParamsLabel
    {
        get
        {
            lock (_sync) return _options?.ParamsLabel ?? "—";
        }
    }

    public bool IsDemo
    {
        get
        {
            lock (_sync) return _options?.IsDemo ?? false;
        }
    }

    public long BytesRx
    {
        get
        {
            lock (_sync) return _bytesRx;
        }
    }

    public long BytesTx
    {
        get
        {
            lock (_sync) return _bytesTx;
        }
    }

    public int RxFrames
    {
        get
        {
            lock (_sync) return _rxFrames;
        }
    }

    public int TxFrames
    {
        get
        {
            lock (_sync) return _txFrames;
        }
    }

    public DateTime? ConnectedAt
    {
        get
        {
            lock (_sync) return _connectedAt;
        }
    }

    public double RxBytesPerSecond
    {
        get
        {
            lock (_sync) return _rxBytesPerSecond;
        }
    }

    public double TxBytesPerSecond
    {
        get
        {
            lock (_sync) return _txBytesPerSecond;
        }
    }

    public bool IsRecording
    {
        get
        {
            lock (_sync) return _recorder is not null;
        }
    }

    /// <summary>Path of the current (or most recent) session log, or null when recording never started.</summary>
    public string? RecordingFilePath
    {
        get
        {
            lock (_sync) return _recordingPath;
        }
    }

    /// <summary>
    /// Opens a serial connection. Runs synchronously and is safe on the UI thread:
    /// the built-in DEMO simulator only starts timers here, so it returns immediately.
    /// On failure returns false with <see cref="State"/> = Error and a Chinese <see cref="LastError"/>.
    /// </summary>
    public bool Open(SerialConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Switching port/parameters while connected: close the old session first (idempotent).
        Close("重新打开串口");

        var resolved = options.Clone();
        if (string.IsNullOrWhiteSpace(resolved.PortName))
        {
            var firstReal = SerialPortCatalog.ResolveFirstRealPort();
            resolved.PortName = string.IsNullOrWhiteSpace(firstReal) ? SerialPortCatalog.DemoName : firstReal;
        }

        ISerialTransport transport;
        try
        {
            transport = SerialPortCatalog.IsDemo(resolved.PortName)
                ? new DemoSerialTransport()
                : new SerialPortTransport();
        }
        catch (Exception ex)
        {
            FailOpen(resolved, $"无法创建串口连接：{ex.Message}");
            return false;
        }

        var recorder = CreateRecorder(resolved);
        var dataHandler = new Action<byte[]>(data => HandleTransportData(transport, data));
        var errorHandler = new Action<string>(message => HandleTransportError(transport, message));
        var disconnectedHandler = new Action<string>(reason => HandleTransportDisconnected(transport, reason));

        transport.DataReceived += dataHandler;
        transport.ErrorOccurred += errorHandler;
        transport.Disconnected += disconnectedHandler;

        lock (_sync)
        {
            _options = resolved;
            _transport = transport;
            _recorder = recorder;
            _recordingPath = recorder?.FilePath;
            _transportDataHandler = dataHandler;
            _transportErrorHandler = errorHandler;
            _transportDisconnectedHandler = disconnectedHandler;
            _state = SerialLinkState.Opening;
            _lastError = string.Empty;
            _bytesRx = 0;
            _bytesTx = 0;
            _rxFrames = 0;
            _txFrames = 0;
            _connectedAt = null;
            _rxBytesPerSecond = 0;
            _txBytesPerSecond = 0;
            _rateLastAt = DateTime.UtcNow;
            _rateLastBytesRx = 0;
            _rateLastBytesTx = 0;
        }

        RaiseStateChanged();

        try
        {
            transport.Open(resolved);
        }
        catch (Exception ex)
        {
            var message = string.IsNullOrWhiteSpace(ex.Message) ? "打开串口失败。" : ex.Message;
            DetachHandlers(transport, dataHandler, errorHandler, disconnectedHandler);

            SessionRecorder? orphanRecorder;
            lock (_sync)
            {
                _transport = null;
                _transportDataHandler = null;
                _transportErrorHandler = null;
                _transportDisconnectedHandler = null;
                orphanRecorder = _recorder;
                _recorder = null;
                _recordingPath = null;
                _state = SerialLinkState.Error;
                _lastError = message;
            }

            try { transport.Dispose(); } catch { /* best effort */ }
            if (orphanRecorder is not null)
            {
                try { orphanRecorder.Dispose(); } catch { /* best effort */ }
                TryDeleteFile(orphanRecorder.FilePath);
            }

            Audit(AuditLevel.Error, "connection",
                $"打开串口失败 {resolved.PortLabel}：{message}", resolved.ParamsLabel);
            RaiseError(message);
            RaiseStateChanged();
            return false;
        }

        var connectedAt = DateTime.Now;
        var record = new SerialSessionRecord
        {
            PortName = resolved.PortName,
            ParamsLabel = resolved.ParamsLabel,
            ProfileName = resolved.ProfileName,
            StartedAt = connectedAt,
            FilePath = recorder?.FilePath ?? string.Empty
        };

        bool opened;
        lock (_sync)
        {
            opened = ReferenceEquals(_transport, transport);
            if (opened)
            {
                _state = SerialLinkState.Open;
                _connectedAt = connectedAt;
                _sessionStartedAt = connectedAt;
                _activeRecord = record;
                _rateLastAt = DateTime.UtcNow;
                _rateLastBytesRx = _bytesRx;
                _rateLastBytesTx = _bytesTx;
            }
        }

        if (!opened)
        {
            // A competing Close() won the race; drop this connection cleanly.
            try { transport.Dispose(); } catch { /* best effort */ }
            if (recorder is not null)
            {
                try { recorder.Dispose(); } catch { /* best effort */ }
                TryDeleteFile(recorder.FilePath);
            }
            return false;
        }

        PersistPreferences(resolved);
        try
        {
            AppServices.Data.Sessions.Insert(0, record);
            AppServices.Store.Save();
        }
        catch (Exception ex)
        {
            AppServices.LogError("保存会话元数据失败", ex);
        }

        Audit(AuditLevel.Info, "connection",
            $"已连接 {resolved.PortLabel}（{resolved.ParamsLabel}）", FormatText.HandshakeLabel(resolved.Handshake));
        if (recorder is not null)
        {
            Audit(AuditLevel.Info, "record",
                $"开始记录会话 {resolved.PortLabel} → {Path.GetFileName(recorder.FilePath)}", recorder.FilePath);
        }

        RaiseStateChanged();
        RaiseCountersChanged();
        return true;
    }

    /// <summary>Closes the connection and stops recording. Idempotent and thread-safe.</summary>
    public void Close(string reason = "用户关闭")
    {
        SessionTeardown? teardown = null;
        SerialLinkState previous;

        lock (_sync)
        {
            previous = _state;

            if (_transport is null && _recorder is null)
            {
                if (previous == SerialLinkState.Closed) return;

                _state = SerialLinkState.Closed;
                _lastError = string.Empty;
                _connectedAt = null;
                _rxBytesPerSecond = 0;
                _txBytesPerSecond = 0;
                _rateLastAt = DateTime.UtcNow;
                _rateLastBytesRx = _bytesRx;
                _rateLastBytesTx = _bytesTx;
            }
            else
            {
                teardown = TakeSessionLocked();
                _state = SerialLinkState.Closed;
                _lastError = string.Empty;
            }
        }

        // Close/join and file IO happen outside the lock so counters never stall.
        if (teardown is not null) FinishTeardown(teardown, reason);

        if (previous != SerialLinkState.Closed)
        {
            RaiseStateChanged();
            RaiseCountersChanged();
        }
    }

    /// <summary>Sends raw bytes. Returns false when the port is closed or the write failed.</summary>
    public bool Send(byte[] payload)
    {
        if (payload is null || payload.Length == 0) return false;

        ISerialTransport? transport;
        SessionRecorder? recorder;
        lock (_sync)
        {
            if (_state != SerialLinkState.Open || _transport is null) return false;
            transport = _transport;
            recorder = _recorder;
        }

        try
        {
            transport.Write(payload);
        }
        catch (Exception ex)
        {
            var message = string.IsNullOrWhiteSpace(ex.Message) ? "发送数据失败。" : ex.Message;
            lock (_sync)
            {
                if (ReferenceEquals(_transport, transport)) _lastError = message;
            }
            RaiseError(message);
            return false;
        }

        lock (_sync)
        {
            _bytesTx += payload.Length;
            _txFrames += 1;
        }

        recorder?.Append(TrafficDirection.Tx, payload, DateTime.Now);
        RaiseTraffic(SerialTraffic.Tx(payload));
        RaiseCountersChanged();
        return true;
    }

    /// <summary>Contract alias: sends an already-built payload through <see cref="Send"/>.</summary>
    public void SendText(byte[] payload) => Send(payload);

    /// <summary>Convenience overload that encodes text with the session code page.</summary>
    public bool SendText(string text, string? encodingName = null)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            var encoding = encodingName ?? Options?.Encoding ?? AppServices.Settings.DefaultEncoding;
            return Send(SerialCodec.EncodeText(text, encoding, useEscapes: false));
        }
        catch (Exception ex)
        {
            RaiseError($"无法编码发送文本：{ex.Message}");
            return false;
        }
    }

    public void SetDtr(bool value)
    {
        ISerialTransport? transport;
        lock (_sync)
        {
            transport = _transport;
            if (transport is null) return;
            if (_options is not null) _options.Dtr = value;
        }

        try
        {
            transport.SetDtr(value);
        }
        catch (Exception ex)
        {
            ReportRuntimeError($"设置 DTR 失败：{ex.Message}");
        }
    }

    public void SetRts(bool value)
    {
        ISerialTransport? transport;
        lock (_sync)
        {
            transport = _transport;
            if (transport is null) return;
            if (_options is not null) _options.Rts = value;
        }

        try
        {
            transport.SetRts(value);
        }
        catch (Exception ex)
        {
            ReportRuntimeError($"设置 RTS 失败：{ex.Message}");
        }
    }

    public void SendBreak(int milliseconds)
    {
        ISerialTransport? transport;
        lock (_sync) transport = _transport;
        if (transport is null) return;

        try
        {
            transport.SendBreak(Math.Clamp(milliseconds, 1, 5000));
        }
        catch (Exception ex)
        {
            ReportRuntimeError($"发送 Break 信号失败：{ex.Message}");
        }
    }

    /// <summary>Resets byte/frame counters and the rate sampling window.</summary>
    public void ResetCounters()
    {
        lock (_sync)
        {
            _bytesRx = 0;
            _bytesTx = 0;
            _rxFrames = 0;
            _txFrames = 0;
            _rxBytesPerSecond = 0;
            _txBytesPerSecond = 0;
            _rateLastAt = DateTime.UtcNow;
            _rateLastBytesRx = 0;
            _rateLastBytesTx = 0;
        }
        RaiseCountersChanged();
    }

    /// <summary>Called by the UI once per second to refresh the B/s indicators.</summary>
    public void TickRates()
    {
        double rx;
        double tx;
        var changed = false;

        lock (_sync)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _rateLastAt).TotalSeconds;
            if (elapsed <= 0.001) return;

            if (_state == SerialLinkState.Open)
            {
                rx = Math.Max(0d, (_bytesRx - _rateLastBytesRx) / elapsed);
                tx = Math.Max(0d, (_bytesTx - _rateLastBytesTx) / elapsed);
            }
            else
            {
                rx = 0;
                tx = 0;
            }

            _rateLastAt = now;
            _rateLastBytesRx = _bytesRx;
            _rateLastBytesTx = _bytesTx;
            changed = Math.Abs(rx - _rxBytesPerSecond) > 0.5 || Math.Abs(tx - _txBytesPerSecond) > 0.5;
            _rxBytesPerSecond = rx;
            _txBytesPerSecond = tx;
        }

        if (changed) RaiseCountersChanged();
    }

    public void DisposeSession() => Dispose();

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Close("服务释放");
    }

    private void FailOpen(SerialConnectionOptions options, string message)
    {
        lock (_sync)
        {
            _state = SerialLinkState.Error;
            _lastError = message;
        }
        Audit(AuditLevel.Error, "connection", $"打开串口失败 {options.PortLabel}：{message}", options.ParamsLabel);
        RaiseError(message);
        RaiseStateChanged();
    }

    private SessionRecorder? CreateRecorder(SerialConnectionOptions options)
    {
        if (!AppServices.Settings.AutoRecord) return null;
        try
        {
            var path = AppServices.Paths.NewSessionLogPath(options.PortName, options.ProfileName);
            return new SessionRecorder(path, options);
        }
        catch (Exception ex)
        {
            AppServices.LogError("创建会话记录文件失败，本次会话不记录", ex);
            return null;
        }
    }

    private SessionTeardown TakeSessionLocked()
    {
        var transport = _transport;
        var teardown = new SessionTeardown
        {
            Transport = transport,
            DataHandler = _transportDataHandler,
            ErrorHandler = _transportErrorHandler,
            DisconnectedHandler = _transportDisconnectedHandler,
            Recorder = _recorder,
            Options = _options,
            Record = _activeRecord,
            RecordingPath = _recordingPath,
            WasConnected = _connectedAt is not null,
            BytesRx = _bytesRx,
            BytesTx = _bytesTx
        };

        _transport = null;
        _transportDataHandler = null;
        _transportErrorHandler = null;
        _transportDisconnectedHandler = null;
        _recorder = null;
        _activeRecord = null;
        _connectedAt = null;
        _rxBytesPerSecond = 0;
        _txBytesPerSecond = 0;
        _rateLastAt = DateTime.UtcNow;
        _rateLastBytesRx = _bytesRx;
        _rateLastBytesTx = _bytesTx;

        return teardown;
    }

    private void FinishTeardown(SessionTeardown teardown, string reason)
    {
        if (teardown.Transport is not null)
        {
            DetachHandlers(teardown.Transport, teardown.DataHandler, teardown.ErrorHandler, teardown.DisconnectedHandler);
            try { teardown.Transport.Close(); } catch (Exception ex) { AppServices.LogError("关闭串口连接失败", ex); }
            try { teardown.Transport.Dispose(); } catch { /* best effort */ }
        }

        if (teardown.Recorder is not null)
        {
            try
            {
                teardown.Recorder.AppendNote($"会话结束：{reason}");
                teardown.Recorder.Dispose();
            }
            catch (Exception ex)
            {
                AppServices.LogError("结束会话记录失败", ex);
            }
        }

        var record = teardown.Record;
        var recorderPath = teardown.Recorder?.FilePath ?? teardown.RecordingPath;
        var endedAt = DateTime.Now;
        var wasConnected = teardown.WasConnected && teardown.Options is not null;

        // Session metadata is bound to the UI eventually; keep observable updates on the UI thread
        // when one exists (this is a no-op in console/smoke scenarios).
        RunOnUiThread(() =>
        {
            if (record is not null)
            {
                record.EndedAt = endedAt;
                record.BytesRx = teardown.BytesRx;
                record.BytesTx = teardown.BytesTx;
                record.FilePath = recorderPath ?? string.Empty;
                record.Note = reason;
                try { AppServices.Store.Save(); } catch (Exception ex) { AppServices.LogError("保存会话记录失败", ex); }
            }

            if (teardown.Recorder is not null)
            {
                Audit(AuditLevel.Info, "record",
                    $"结束记录 {Path.GetFileName(teardown.Recorder.FilePath)}", teardown.Recorder.FilePath);
            }

            if (wasConnected)
            {
                Audit(AuditLevel.Info, "connection",
                    $"已断开 {teardown.Options!.PortLabel}（{reason}）");
            }
        });
    }

    private void HandleTransportData(ISerialTransport source, byte[] data)
    {
        if (data.Length == 0) return;

        SessionRecorder? recorder;
        var at = DateTime.Now;
        lock (_sync)
        {
            if (!ReferenceEquals(_transport, source)) return;
            _bytesRx += data.Length;
            _rxFrames += 1;
            recorder = _recorder;
        }

        recorder?.Append(TrafficDirection.Rx, data, at);
        // One RX chunk per read block, exactly as delivered by the transport.
        RaiseTraffic(SerialTraffic.Rx(at, data));
        RaiseCountersChanged();
    }

    private void HandleTransportError(ISerialTransport source, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (_sync)
        {
            if (!ReferenceEquals(_transport, source)) return;
            _lastError = message;
        }
        RaiseError(message);
    }

    private void HandleTransportDisconnected(ISerialTransport source, string reason)
    {
        var message = string.IsNullOrWhiteSpace(reason) ? "串口连接已断开。" : reason;
        SessionTeardown? teardown = null;

        lock (_sync)
        {
            if (!ReferenceEquals(_transport, source)) return;
            teardown = TakeSessionLocked();
            _state = SerialLinkState.Error;
            _lastError = message;
        }

        FinishTeardown(teardown, message);
        RaiseTraffic(SerialTraffic.System($"连接已断开：{message}"));
        RaiseError(message);
        RaiseStateChanged();
        RaiseCountersChanged();
    }

    private void ReportRuntimeError(string message)
    {
        lock (_sync) _lastError = message;
        RaiseError(message);
    }

    private static void DetachHandlers(ISerialTransport transport, Action<byte[]>? data,
        Action<string>? error, Action<string>? disconnected)
    {
        try
        {
            if (data is not null) transport.DataReceived -= data;
        }
        catch
        {
            // best effort
        }
        try
        {
            if (error is not null) transport.ErrorOccurred -= error;
        }
        catch
        {
            // best effort
        }
        try
        {
            if (disconnected is not null) transport.Disconnected -= disconnected;
        }
        catch
        {
            // best effort
        }
    }

    private static void PersistPreferences(SerialConnectionOptions options)
    {
        try
        {
            options.RememberIn(AppServices.Settings);
            var profile = AppServices.Store.FindProfile(options.ProfileId);
            if (profile is not null)
            {
                profile.UseCount += 1;
                profile.LastUsedAt = DateTime.Now;
            }
            AppServices.Store.Save();
        }
        catch (Exception ex)
        {
            AppServices.LogError("保存串口连接偏好失败", ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    private static void Audit(AuditLevel level, string category, string message, string? detail = null)
    {
        try
        {
            AppServices.Audit.Add(level, category, message, detail);
        }
        catch (Exception ex)
        {
            AppServices.LogError("写入审计日志失败", ex);
        }
    }

    private static void RunOnUiThread(Action action)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
                return;
            }
            if (dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            AppServices.LogError("调度串口会话 UI 更新失败", ex);
        }
    }

    private void RaiseStateChanged() => InvokeHandlers(StateChanged);

    private void RaiseCountersChanged() => InvokeHandlers(CountersChanged);

    private void RaiseTraffic(SerialTraffic traffic) => InvokeHandlers(Traffic, traffic);

    private void RaiseError(string message) => InvokeHandlers(ErrorOccurred, message);

    private static void InvokeHandlers(Action? handlers)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                ReportHandlerFailure(ex);
            }
        }
    }

    private static void InvokeHandlers<T>(Action<T>? handlers, T argument)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)handler)(argument);
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
            AppServices.LogError("串口会话事件处理异常", ex);
        }
        catch
        {
            // diagnostics must never break the serial pipeline
        }
    }

    private sealed class SessionTeardown
    {
        public ISerialTransport? Transport { get; init; }
        public Action<byte[]>? DataHandler { get; init; }
        public Action<string>? ErrorHandler { get; init; }
        public Action<string>? DisconnectedHandler { get; init; }
        public SessionRecorder? Recorder { get; init; }
        public SerialConnectionOptions? Options { get; init; }
        public SerialSessionRecord? Record { get; init; }
        public string? RecordingPath { get; init; }
        public bool WasConnected { get; init; }
        public long BytesRx { get; init; }
        public long BytesTx { get; init; }
    }
}
