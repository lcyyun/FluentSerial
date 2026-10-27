using System.Globalization;
using System.IO;
using System.Text;

namespace FluentSerial.Core;

/// <summary>
/// Appends every RX/TX chunk to a UTF-8 log file together with a readable header.
/// All methods are thread-safe: the serial read loop, the UI send path and recording can
/// collide freely. A logging failure never propagates to the serial pipeline.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private readonly object _sync = new();
    private readonly SerialConnectionOptions _options;
    private readonly StreamWriter _writer;
    private bool _disposed;

    public string FilePath { get; }
    public DateTime StartedAt { get; }

    public bool IsDisposed
    {
        get
        {
            lock (_sync) return _disposed;
        }
    }

    public SessionRecorder(string filePath, SerialConnectionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(options);

        FilePath = Path.GetFullPath(filePath);
        _options = options;
        StartedAt = DateTime.Now;

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite,
            4096, FileOptions.SequentialScan);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        try
        {
            WriteHeader();
        }
        catch
        {
            try { _writer.Dispose(); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Appends one RX/TX chunk: timestamp, direction, byte count, HEX and readable text.</summary>
    public void Append(TrafficDirection direction, ReadOnlySpan<byte> data, DateTime at)
    {
        var payload = data.ToArray();
        var textDirection = direction == TrafficDirection.Tx ? "TX" : "RX";
        var encoding = SerialCodec.GetEncoding(_options.Encoding);

        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                var time = at.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                var hex = SerialCodec.FormatHex(payload);
                var text = EscapeText(payload, encoding);
                _writer.WriteLine($"[{time}] {textDirection}  {payload.Length} B | {hex} | \"{text}\"");
            }
            catch (Exception ex)
            {
                LogFailure(ex);
            }
        }
    }

    /// <summary>Appends a free-form note such as "会话结束".</summary>
    public void AppendNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return;
        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] -- {note}");
            }
            catch (Exception ex)
            {
                LogFailure(ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _writer.WriteLine($"结束时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                _writer.Flush();
            }
            catch (Exception ex)
            {
                LogFailure(ex);
            }
            try { _writer.Dispose(); } catch { /* best effort */ }
        }
    }

    private void WriteHeader()
    {
        var options = _options;
        _writer.WriteLine("FluentSerial 串口会话记录");
        _writer.WriteLine($"端口: {options.PortName}{(options.IsDemo ? "（内置回环模拟器）" : string.Empty)}");
        _writer.WriteLine($"参数: {options.ParamsLabel} · {FormatText.HandshakeLabel(options.Handshake)}");
        if (!string.IsNullOrWhiteSpace(options.ProfileName))
            _writer.WriteLine($"配置: {options.ProfileName}");
        _writer.WriteLine($"编码: {FormatText.EncodingLabel(options.Encoding)}");
        _writer.WriteLine($"开始时间: {StartedAt:yyyy-MM-dd HH:mm:ss.fff}");
        _writer.WriteLine(new string('-', 72));
    }

    private static string EscapeText(ReadOnlySpan<byte> data, Encoding encoding)
    {
        var text = encoding.GetString(data);
        var builder = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\0':
                    builder.Append("\\0");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                default:
                    if (char.IsControl(ch))
                        builder.Append("\\x").Append(((int)ch).ToString("X2", CultureInfo.InvariantCulture));
                    else
                        builder.Append(ch);
                    break;
            }
        }
        return builder.ToString();
    }

    private void LogFailure(Exception ex)
    {
        try
        {
            AppServices.LogError("写入会话记录失败", ex);
        }
        catch
        {
            // recording must never break the serial session
        }
    }
}
