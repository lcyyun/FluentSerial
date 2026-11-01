using System.IO;
using System.Windows;
using FluentSerial.Core;
using Microsoft.Win32;

namespace FluentSerial.Features;

public partial class TerminalPage
{
    private CancellationTokenSource? _fileSendCancellation;
    private long _fileBytesSent;
    private long _fileBytesTotal;

    private bool IsSendingFile => _fileSendCancellation is not null;

    private void CancelFileSend()
    {
        if (_fileSendCancellation is null) return;
        _fileSendCancellation.Cancel();
        UpdateFileSendButton();
        SetTransientHint("正在取消文件发送，等待当前数据块完成…");
    }

    private void UpdateFileSendButton()
    {
        SendFileButton.IsEnabled = IsSendingFile || AppServices.Serial.IsOpen;
        if (!IsSendingFile)
        {
            SendFileButton.Content = "发送文件";
            SendFileButton.ToolTip = "按原始字节发送文件，不追加行尾或校验；最大 128 MB";
            return;
        }

        var percentage = _fileBytesTotal == 0 ? 0 : (int)(_fileBytesSent * 100 / _fileBytesTotal);
        SendFileButton.Content = _fileSendCancellation!.IsCancellationRequested ? "正在取消…" : $"取消 {percentage}%";
        SendFileButton.ToolTip = $"已发送 {FormatText.Bytes(_fileBytesSent)} / {FormatText.Bytes(_fileBytesTotal)}。取消会等待当前数据块完成。";
    }

    private async void SendFile_Click(object sender, RoutedEventArgs e)
    {
        if (IsSendingFile)
        {
            CancelFileSend();
            return;
        }
        var service = AppServices.Serial;
        if (!service.IsOpen)
        {
            SetTransientHint("串口未打开，请先打开端口", error: true);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "按原始字节发送文件（最大 128 MB）",
            Filter = "所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var connectedAt = service.ConnectedAt;
        var options = service.Options;
        if (!service.IsOpen || connectedAt is null || options is null)
        {
            SetTransientHint("串口已断开，文件未发送", error: true);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _fileSendCancellation = cancellation;
        _fileBytesSent = 0;
        _fileBytesTotal = 0;
        _autoSendTimer.Stop();
        AutoSendCheck.IsChecked = false;
        UpdateSendSize();
        UpdateFileSendButton();

        var name = Path.GetFileName(dialog.FileName);
        try
        {
            // Roughly 100 ms of wire data per block at ordinary UART baud rates.
            // Smaller blocks keep cancellation responsive on slow connections.
            var blockSize = Math.Clamp(options.BaudRate / 100, 64, 4096);
            var sent = await SerialFileSender.SendAsync(dialog.FileName, block =>
            {
                // The UI prevents reconnecting while IsSendingFile is true. Check again
                // on the write worker to catch unplugging or a queued session change.
                if (!service.IsOpen || service.ConnectedAt != connectedAt || !ReferenceEquals(service.Options, options))
                    throw new IOException("串口会话已改变，文件发送已停止");
                if (!service.Send(block))
                    throw new IOException(string.IsNullOrWhiteSpace(service.LastError) ? "串口写入失败" : service.LastError);
            }, (completed, total) =>
            {
                _fileBytesSent = completed;
                _fileBytesTotal = total;
                UpdateFileSendButton();
                SetTransientHint($"正在发送 {name}  {FormatText.Bytes(completed)} / {FormatText.Bytes(total)}");
            }, cancellation.Token, blockSize);

            SetTransientHint($"已发送文件 {name}（{FormatText.Bytes(sent)}）");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetTransientHint($"已取消文件发送，已发送 {FormatText.Bytes(_fileBytesSent)} / {FormatText.Bytes(_fileBytesTotal)}");
        }
        catch (Exception ex)
        {
            AppServices.LogError("发送文件失败", ex);
            SetTransientHint($"文件发送停止（已发送 {FormatText.Bytes(_fileBytesSent)}）：{ex.Message}", error: true);
        }
        finally
        {
            _fileSendCancellation = null;
            UpdateSendSize();
            UpdateFileSendButton();
        }
    }
}

/// <summary>
/// Streams a bounded file to a synchronous serial writer without blocking the UI.
/// Cancellation never abandons an in-flight write: its actual result is counted first.
/// </summary>
public static class SerialFileSender
{
    public const long MaxFileBytes = 128L * 1024 * 1024;

    public static async Task<long> SendAsync(string filePath, Action<byte[]> sendBlock,
        Action<long, long>? progress = null, CancellationToken cancellationToken = default, int blockSize = 4096)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(sendBlock);
        if (blockSize is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(blockSize));
        cancellationToken.ThrowIfCancellationRequested();

        // Check before opening/reading; check again on the opened stream to cover replacement.
        ValidateLength(new FileInfo(filePath).Length);
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        ValidateLength(length);
        progress?.Invoke(0, length);

        var buffer = new byte[blockSize];
        long sent = 0;
        while (sent < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - sent)), cancellationToken);
            if (count == 0) throw new EndOfStreamException("文件在发送过程中意外结束");
            // Each traffic event keeps its data beyond Write, so never reuse its byte array.
            var block = buffer.AsSpan(0, count).ToArray();
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                sendBlock(block);
            });
            sent += count;
            progress?.Invoke(sent, length);
        }
        return sent;
    }

    private static void ValidateLength(long length)
    {
        if (length == 0) throw new IOException("文件为空，未发送数据");
        if (length > MaxFileBytes) throw new IOException("文件超过 128 MB，未发送数据");
    }
}
