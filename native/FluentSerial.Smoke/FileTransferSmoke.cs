using System.IO;
using FluentSerial.Features;

namespace FluentSerial.Smoke;

public static class FileTransferSmoke
{
    public static async Task RunAsync(Action<string, bool, string> check, string dataDirectory)
    {
        try
        {
            var source = Enumerable.Range(0, 10007).Select(i => (byte)(i % 256)).ToArray();
            var path = Path.Combine(dataDirectory, "file-transfer.bin");
            await File.WriteAllBytesAsync(path, source);
            var blocks = new List<byte[]>();
            var progress = new List<long>();
            var sent = await SerialFileSender.SendAsync(path, block => blocks.Add(block),
                (completed, total) => progress.Add(completed), blockSize: 128);
            check("file transfer exact bytes", sent == source.Length && blocks.SelectMany(b => b).SequenceEqual(source), "");
            check("file transfer bounded blocks", blocks.Count > 1 && blocks.All(b => b.Length <= 128), $"blocks={blocks.Count}");
            check("file transfer progress", progress[0] == 0 && progress[^1] == source.Length
                && progress.Zip(progress.Skip(1), (a, b) => b > a).All(v => v), "");

            using (var cancel = new CancellationTokenSource())
            {
                var writes = 0;
                long completed = 0;
                var cancelled = false;
                try
                {
                    await SerialFileSender.SendAsync(path, _ => writes++, (count, _) =>
                    {
                        completed = count;
                        if (count > 0) cancel.Cancel();
                    }, cancel.Token, 64);
                }
                catch (OperationCanceledException) { cancelled = true; }
                check("file transfer cancels between blocks", cancelled && writes == 1 && completed == 64, "");
            }

            using (var cancel = new CancellationTokenSource())
            using (var releaseWrite = new ManualResetEventSlim())
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var writes = 0;
                long completed = 0;
                var transfer = SerialFileSender.SendAsync(path, _ =>
                {
                    Interlocked.Increment(ref writes);
                    entered.TrySetResult();
                    if (!releaseWrite.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test writer did not resume");
                }, (count, _) => completed = count, cancel.Token, 64);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    cancel.Cancel();
                    check("file transfer waits for in-flight block", !transfer.IsCompleted, "");
                }
                finally { releaseWrite.Set(); }

                var cancelled = false;
                try { await transfer; }
                catch (OperationCanceledException) { cancelled = true; }
                check("file transfer counts completed cancelled block", cancelled && writes == 1 && completed == 64, "");
            }

            var attempted = 0;
            long accepted = 0;
            var writeFailure = false;
            try
            {
                await SerialFileSender.SendAsync(path, _ =>
                {
                    if (++attempted == 2) throw new IOException("simulated disconnect");
                }, (count, _) => accepted = count, blockSize: 64);
            }
            catch (IOException) { writeFailure = true; }
            check("file transfer stops on write failure", writeFailure && attempted == 2 && accepted == 64, "");

            var emptyPath = Path.Combine(dataDirectory, "file-transfer-empty.bin");
            await File.WriteAllBytesAsync(emptyPath, Array.Empty<byte>());
            var emptyRejected = false;
            var emptyWrites = 0;
            try { await SerialFileSender.SendAsync(emptyPath, _ => emptyWrites++); }
            catch (IOException) { emptyRejected = true; }
            check("file transfer rejects empty file", emptyRejected && emptyWrites == 0, "");

            var oversizePath = Path.Combine(dataDirectory, "file-transfer-oversize.bin");
            try
            {
                using (var file = File.Create(oversizePath)) file.SetLength(SerialFileSender.MaxFileBytes + 1);
                var oversizedRejected = false;
                var oversizedWrites = 0;
                try { await SerialFileSender.SendAsync(oversizePath, _ => oversizedWrites++); }
                catch (IOException) { oversizedRejected = true; }
                check("file transfer rejects oversize before sending", oversizedRejected && oversizedWrites == 0, "");
            }
            finally { File.Delete(oversizePath); }
        }
        catch (Exception ex)
        {
            check("file transfer section", false, ex.ToString());
        }
    }
}
