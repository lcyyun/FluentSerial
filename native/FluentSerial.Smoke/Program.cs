using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using FluentSerial.Core;
using FluentSerial.Features;

// FluentSerial native smoke test: exercises the serial core end-to-end without hardware.
// Run with: dotnet run --project native\FluentSerial.Smoke\FluentSerial.Smoke.csproj -c Release

var failures = 0;
var passed = 0;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch
{
    // redirected output may reject the change; ASCII fallback is fine
}

void Check(string name, bool ok, string detail = "")
{
    if (ok)
    {
        passed++;
        Console.WriteLine($"PASS {name}{(detail.Length == 0 ? string.Empty : "  " + detail)}");
    }
    else
    {
        failures++;
        Console.WriteLine($"FAIL {name}{(detail.Length == 0 ? string.Empty : "  " + detail)}");
    }
}

static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000, int intervalMs = 20)
{
    var watch = Stopwatch.StartNew();
    while (watch.ElapsedMilliseconds < timeoutMs)
    {
        if (condition()) return true;
        await Task.Delay(intervalMs);
    }
    return condition();
}

// ---------------------------------------------------------------- environment

var smokeData = Path.Combine(Path.GetTempPath(), "fluentserial-native-smoke", Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("FLUENTSERIAL_DATA_DIR", smokeData);
AppServices.Initialize();
Console.WriteLine($"data={AppServices.Paths.Root}");

// ---------------------------------------------------------------- codec / checksums

try
{
    var parsed = SerialCodec.ParseHex("01 02 0x03,04");
    Check("hex parse", parsed.SequenceEqual(new byte[] { 0x01, 0x02, 0x03, 0x04 }), SerialCodec.FormatHex(parsed));
    Check("hex compact", SerialCodec.ParseHex("01020304").SequenceEqual(new byte[] { 0x01, 0x02, 0x03, 0x04 }));
    Check("hex empty", SerialCodec.ParseHex("   ").Length == 0);

    Check("hex invalid char", !SerialCodec.TryParseHex("0G", out _, out var hexError) && !string.IsNullOrWhiteSpace(hexError),
        hexError ?? string.Empty);
    Check("hex odd digits", !SerialCodec.TryParseHex("ABC", out _, out var oddError) && !string.IsNullOrWhiteSpace(oddError),
        oddError ?? string.Empty);
    foreach (var malformed in new[] { "AxB", "xAB", "0x", "01 0x", "A B", "0x1 0x2", "AA0xBB" })
        Check($"hex rejects {malformed}", !SerialCodec.TryParseHex(malformed, out var rejected, out var error)
                                            && rejected.Length == 0 && !string.IsNullOrWhiteSpace(error));
    Check("hex supported separators", SerialCodec.ParseHex("0X01;02:03|04-05、06\n07")
        .SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7 }));

    var threw = false;
    try
    {
        SerialCodec.ParseHex("ZZ");
    }
    catch (FormatException)
    {
        threw = true;
    }
    Check("hex throws FormatException", threw);

    var escaped = SerialCodec.EncodeText("A\\r\\nB", "utf-8", useEscapes: true);
    Check("escape encode", escaped.SequenceEqual(new byte[] { 0x41, 0x0D, 0x0A, 0x42 }), SerialCodec.FormatHex(escaped));
    var hexEscaped = SerialCodec.EncodeText("\\x41\\x42", "utf-8", useEscapes: true);
    Check("escape hex", hexEscaped.SequenceEqual(new byte[] { 0x41, 0x42 }), SerialCodec.FormatHex(hexEscaped));
    var unicodeEscaped = SerialCodec.EncodeText("\\u4F60", "utf-8", useEscapes: true);
    Check("escape unicode", unicodeEscaped.SequenceEqual(new byte[] { 0xE4, 0xBD, 0xA0 }), SerialCodec.FormatHex(unicodeEscaped));
    Check("escape preserves emoji", SerialCodec.EncodeText("A😀中\\r\\n", "utf-8", true)
        .SequenceEqual(Encoding.UTF8.GetBytes("A😀中\r\n")));
    Check("escape unicode surrogate pair", SerialCodec.EncodeText("\\uD83D\\uDE00", "utf-8", true)
        .SequenceEqual(Encoding.UTF8.GetBytes("😀")));
    Check("escape raw bytes stay raw", SerialCodec.EncodeText("😀\\xFF中", "utf-8", true)
        .SequenceEqual(Encoding.UTF8.GetBytes("😀").Concat(new byte[] { 0xFF }).Concat(Encoding.UTF8.GetBytes("中"))));
    Check("escape malformed preserved", SerialCodec.DecodeText(SerialCodec.EncodeText("\\xZ1\\uQQQQ\\z\\", "utf-8", true), "utf-8")
        == "\\xZ1\\uQQQQ\\z\\");
    var literal = SerialCodec.EncodeText("A\\rB", "utf-8", useEscapes: false);
    Check("escape disabled", literal.SequenceEqual(new byte[] { 0x41, 0x5C, 0x72, 0x42 }), SerialCodec.FormatHex(literal));
    Check("unescape", SerialCodec.UnescapeText("A\\r\\nB") == "A\r\nB");

    Check("line ending cr", SerialCodec.LineEndingBytes(LineEnding.Cr).SequenceEqual(new byte[] { 0x0D }));
    Check("line ending lf", SerialCodec.LineEndingBytes(LineEnding.Lf).SequenceEqual(new byte[] { 0x0A }));
    Check("line ending crlf", SerialCodec.LineEndingBytes(LineEnding.CrLf).SequenceEqual(new byte[] { 0x0D, 0x0A }));
    Check("line ending none", SerialCodec.LineEndingBytes(LineEnding.None).Length == 0);

    var modbusFrame = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x02 };
    var modbusCrc = Checksums.Crc16Modbus(modbusFrame);
    Check("crc16 modbus known", modbusCrc == 0x0BC4, $"0x{modbusCrc:X4}");
    Check("crc16 modbus wire", Checksums.Compute(ChecksumKind.Crc16Modbus, modbusFrame)
        .SequenceEqual(new byte[] { 0xC4, 0x0B }));
    var ccitt = Checksums.Crc16Ccitt(Encoding.ASCII.GetBytes("123456789"));
    Check("crc16 ccitt known", ccitt == 0x29B1, $"0x{ccitt:X4}");
    Check("crc16 ccitt wire", Checksums.Compute(ChecksumKind.Crc16Ccitt, Encoding.ASCII.GetBytes("123456789"))
        .SequenceEqual(new byte[] { 0x29, 0xB1 }));
    Check("sum8", Checksums.Sum8(new byte[] { 0x01, 0x02, 0x03 }) == 0x06);
    Check("xor8", Checksums.Xor8(new byte[] { 0x01, 0x02, 0x03 }) == 0x00);

    var ordered = SerialCodec.BuildPayload("AB", PayloadMode.Text, LineEnding.CrLf, ChecksumKind.Xor8, "utf-8",
        useEscapes: true);
    Check("build payload order", ordered.SequenceEqual(new byte[] { 0x41, 0x42, 0x03, 0x0D, 0x0A }),
        SerialCodec.FormatHex(ordered));
    var modbusPayload = SerialCodec.BuildPayload("01 03 00 00 00 02", PayloadMode.Hex, LineEnding.None,
        ChecksumKind.Crc16Modbus, "utf-8", useEscapes: true);
    Check("build payload modbus", modbusPayload.Length == 8 && modbusPayload[^2] == 0xC4 && modbusPayload[^1] == 0x0B,
        SerialCodec.FormatHex(modbusPayload));
    var invalidBuild = SerialCodec.TryBuildPayload("0G", PayloadMode.Hex, LineEnding.None, ChecksumKind.None, "utf-8",
        useEscapes: true, out _, out var buildError);
    Check("build payload invalid", !invalidBuild && !string.IsNullOrWhiteSpace(buildError), buildError ?? string.Empty);
}
catch (Exception ex)
{
    Check("codec section", false, ex.Message);
}

// ---------------------------------------------------------------- port catalog

try
{
    var ports = SerialPortCatalog.GetPorts();
    var demoEntries = ports.Where(p => p.IsDemo).ToList();
    Check("catalog demo always present", demoEntries.Count == 1 && demoEntries[0].Name == "DEMO",
        string.Join(",", ports.Select(p => p.Name)));
    Check("catalog demo description",
        demoEntries.Count == 1 && demoEntries[0].Description == "内置回环模拟器（无需硬件）");
    Check("catalog no duplicates",
        ports.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == ports.Count);
    Check("catalog isdemo", SerialPortCatalog.IsDemo("demo") && SerialPortCatalog.IsDemo(" DEMO ")
        && !SerialPortCatalog.IsDemo("COM1") && !SerialPortCatalog.IsDemo(string.Empty));
    Check("catalog find demo", SerialPortCatalog.Find("demo") is { IsDemo: true });
    Check("catalog find unknown", SerialPortCatalog.Find("COM199") is null);
    var firstReal = SerialPortCatalog.ResolveFirstRealPort();
    Check("catalog first real", firstReal.Length == 0 || firstReal.StartsWith("COM", StringComparison.OrdinalIgnoreCase),
        firstReal.Length == 0 ? "no hardware" : firstReal);
    Check("catalog refresh", SerialPortCatalog.GetPorts().Any(p => p.IsDemo));
}
catch (Exception ex)
{
    Check("catalog section", false, ex.ToString());
}

// ---------------------------------------------------------------- session recorder

try
{
    var recorderPath = Path.Combine(smokeData, "recorder-test.log");
    var recorderOptions = new SerialConnectionOptions { PortName = "DEMO" };
    var at = new DateTime(2026, 2, 3, 4, 5, 6, 789);

    using (var recorder = new SessionRecorder(recorderPath, recorderOptions))
    {
        Check("recorder file created", File.Exists(recorderPath));
        recorder.Append(TrafficDirection.Rx, new byte[] { 0x41, 0x54, 0x0D, 0x0A }, at);
        recorder.Append(TrafficDirection.Tx, Encoding.UTF8.GetBytes("OK\r\n"), at.AddMilliseconds(10));
        recorder.AppendNote("冒烟记录备注");
    }

    var recorderText = File.ReadAllText(recorderPath);
    Check("recorder header",
        recorderText.Contains("端口: DEMO") && recorderText.Contains("参数: 115200 8-N-1")
        && recorderText.Contains("编码: UTF-8") && recorderText.Contains("开始时间:"),
        recorderText.Split('\n')[1].Trim());
    Check("recorder rx line",
        recorderText.Contains("RX  4 B") && recorderText.Contains("41 54 0D 0A") && recorderText.Contains("AT\\r\\n"),
        recorderText.Split('\n').FirstOrDefault(line => line.Contains(" RX "))?.Trim() ?? string.Empty);
    Check("recorder tx line",
        recorderText.Contains("TX  4 B") && recorderText.Contains("4F 4B 0D 0A"),
        recorderText.Split('\n').FirstOrDefault(line => line.Contains(" TX "))?.Trim() ?? string.Empty);
    Check("recorder note", recorderText.Contains("冒烟记录备注"));

    var collisionRejected = false;
    try { using var duplicate = new SessionRecorder(recorderPath, recorderOptions); }
    catch (IOException) { collisionRejected = true; }
    Check("recorder never overwrites existing log", collisionRejected && File.ReadAllText(recorderPath) == recorderText);
    var uniquePaths = Enumerable.Range(0, 100).Select(_ => AppServices.Paths.NewSessionLogPath("DEMO", "same port")).ToList();
    Check("recorder rapid reconnect unique paths", uniquePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 100);

    var doubleDisposePath = Path.Combine(smokeData, "recorder-dispose.log");
    var doubleDisposeRecorder = new SessionRecorder(doubleDisposePath, recorderOptions);
    doubleDisposeRecorder.Dispose();
    doubleDisposeRecorder.Dispose();
    Check("recorder dispose idempotent", doubleDisposeRecorder.IsDisposed);
}
catch (Exception ex)
{
    Check("recorder section", false, ex.ToString());
}

// ---------------------------------------------------------------- data store

try
{
    var persisted = new PortProfile
    {
        Id = "smoke-persist",
        Name = "Smoke Persist",
        PortName = "COM_SMOKE",
        BaudRate = 9600
    };
    AppServices.Data.Profiles.Add(persisted);
    AppServices.Settings.LastPortName = "COM_SMOKE";
    AppServices.Store.Save();

    var reloaded = new DataStore(new AppPaths());
    reloaded.Load();
    Check("datastore profile persisted",
        reloaded.Data.Profiles.Any(p => p.Id == "smoke-persist" && p.Name == "Smoke Persist" && p.BaudRate == 9600));
    Check("datastore settings persisted", reloaded.Data.Settings.LastPortName == "COM_SMOKE");

    AppServices.Data.Profiles.Remove(persisted);
    AppServices.Settings.LastPortName = "DEMO";
    AppServices.Store.Save();
}
catch (Exception ex)
{
    Check("datastore section", false, ex.ToString());
}

// ---------------------------------------------------------------- demo transport (direct)

try
{
    const string valid = """[{"Id":"imported-id","Name":" Test ","Group":" Custom ","Payload":"01 02","Mode":"Hex","Ending":"None","Checksum":"None","UseCount":99}]""";
    var import = QuickCommandImport.Parse(valid, Array.Empty<QuickCommand>(), "utf-8", true);
    Check("import validates and normalizes", import.Commands.Count == 1 && import.Commands[0].Name == "Test"
        && import.Commands[0].Group == "Custom" && import.Commands[0].Id != "imported-id" && import.Commands[0].UseCount == 0);
    var repeated = QuickCommandImport.Parse(valid, import.Commands, "utf-8", true);
    Check("import deduplicates existing commands", repeated.Commands.Count == 0 && repeated.DuplicateCount == 1);
    var inFile = QuickCommandImport.Parse("[" + valid[1..^1] + "," + valid[1..^1] + "]", Array.Empty<QuickCommand>(), "utf-8", true);
    Check("import deduplicates within file", inFile.Commands.Count == 1 && inFile.DuplicateCount == 1);
    foreach (var invalid in new[] { "null", "[null]", "[{\"Name\":\"\",\"Payload\":\"AT\"}]",
                 "[{\"Name\":\"bad\",\"Mode\":99,\"Payload\":\"AT\"}]",
                 "[{\"Name\":\"bad hex\",\"Mode\":\"Hex\",\"Payload\":\"AxB\"}]",
                 "[{\"Name\":\"empty\",\"Payload\":\"\",\"Ending\":\"None\"}]" })
    {
        var rejected = false;
        try { QuickCommandImport.Parse(invalid, import.Commands, "utf-8", true); }
        catch (FormatException) { rejected = true; }
        Check("import rejects invalid entry", rejected, invalid);
    }
    var mixed = "[" + valid[1..^1] + ",null]";
    var existingCount = AppServices.Data.Commands.Count;
    var batchRejected = false;
    try { QuickCommandImport.Parse(mixed, AppServices.Data.Commands, "utf-8", true); }
    catch (FormatException) { batchRejected = true; }
    Check("import validates whole batch before mutation", batchRejected && AppServices.Data.Commands.Count == existingCount);
    var tooMany = "[" + string.Join(',', Enumerable.Repeat(valid[1..^1], QuickCommandImport.MaxCommands + 1)) + "]";
    var limitRejected = false;
    try { QuickCommandImport.Parse(tooMany, Array.Empty<QuickCommand>(), "utf-8", true); }
    catch (FormatException) { limitRejected = true; }
    Check("import enforces command limit", limitRejected);
}
catch (Exception ex)
{
    Check("command import section", false, ex.ToString());
}

try
{
    using var demo = new DemoSerialTransport();
    var chunks = new ConcurrentQueue<string>();
    var demoEventCount = 0;
    demo.DataReceived += data =>
    {
        Interlocked.Increment(ref demoEventCount);
        chunks.Enqueue(Encoding.UTF8.GetString(data));
    };
    string DemoText() => string.Join(string.Empty, chunks);

    demo.Open(new SerialConnectionOptions());
    Check("demo transport open", demo.IsOpen);

    Check("demo welcome", await WaitUntilAsync(() => DemoText().Contains("FluentSerial DEMO 串口模拟器 v1.0")),
        DemoText());
    Check("demo identity",
        await WaitUntilAsync(() => DemoText().Contains("设备: DemoSensor-01") && DemoText().Contains("固件: 2.4.1")));

    demo.Write(Encoding.UTF8.GetBytes("AT+VER?\r\n"));
    Check("demo at+ver",
        await WaitUntilAsync(() => DemoText().Contains("+VER: 2.4.1") && DemoText().Contains("OK\r\n")));

    demo.Write(Encoding.UTF8.GetBytes("AT\r\n"));
    demo.Write(Encoding.UTF8.GetBytes("ATE0\r\n"));
    demo.Write(Encoding.UTF8.GetBytes("AT+RST\r\n"));
    demo.Write(Encoding.UTF8.GetBytes("ATI\r\n"));
    Check("demo ati", await WaitUntilAsync(() => DemoText().Contains("FluentSerial DEMO\r\nOK\r\n")));

    demo.Write(Encoding.UTF8.GetBytes("hello again\r\n"));
    Check("demo echo", await WaitUntilAsync(() => DemoText().Contains("ECHO: hello again")));

    Check("demo telemetry",
        await WaitUntilAsync(() => Regex.IsMatch(DemoText(), @"\[SENSOR\] temp=\d+\.\dC hum=\d+% rssi=-\d+dBm")));

    demo.SetDtr(false);
    demo.SetRts(false);
    demo.SendBreak(5);
    Check("demo dtr rts", !demo.Dtr && !demo.Rts);

    var eventsBeforeClose = Volatile.Read(ref demoEventCount);
    demo.Close();
    Check("demo transport closed", !demo.IsOpen);
    await Task.Delay(1600);
    Check("demo no events after close", Volatile.Read(ref demoEventCount) == eventsBeforeClose,
        $"{eventsBeforeClose}->{Volatile.Read(ref demoEventCount)}");
}
catch (Exception ex)
{
    Check("demo transport section", false, ex.ToString());
}

// ---------------------------------------------------------------- session service (DEMO)

try
{
    var service = AppServices.Serial;
    var traffic = new ConcurrentQueue<SerialTraffic>();
    var errors = new ConcurrentQueue<string>();
    var stateEvents = 0;
    var counterEvents = 0;

    void OnTraffic(SerialTraffic item) => traffic.Enqueue(item);
    void OnError(string message) => errors.Enqueue(message);
    void OnStateChanged() => Interlocked.Increment(ref stateEvents);
    void OnCountersChanged() => Interlocked.Increment(ref counterEvents);

    service.Traffic += OnTraffic;
    service.ErrorOccurred += OnError;
    service.StateChanged += OnStateChanged;
    service.CountersChanged += OnCountersChanged;

    string TrafficText(TrafficDirection direction) => string.Join(string.Empty,
        traffic.Where(t => t.Direction == direction).Select(t => Encoding.UTF8.GetString(t.Data)));

    var profile = new PortProfile
    {
        Id = "smoke-demo-profile",
        Name = "Smoke DEMO",
        PortName = "DEMO",
        BaudRate = 115200,
        DataBits = 8,
        Parity = Parity.None,
        StopBits = StopBits.One,
        Encoding = "utf-8"
    };
    if (AppServices.Data.Profiles.All(p => p.Id != profile.Id)) AppServices.Data.Profiles.Add(profile);
    var useCountBefore = profile.UseCount;
    var sessionCountBefore = AppServices.Data.Sessions.Count;

    var options = SerialConnectionOptions.FromProfile(profile);
    var watch = Stopwatch.StartNew();
    var opened = service.Open(options);
    watch.Stop();

    Check("service open", opened && service.IsOpen && service.State == SerialLinkState.Open,
        $"state={service.State} err={service.LastError}");
    Check("service open fast", watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
    Check("service labels", service.IsDemo && service.PortLabel == "DEMO 回环" && service.ParamsLabel == "115200 8-N-1",
        $"{service.PortLabel} / {service.ParamsLabel}");
    Check("service connectedAt", service.ConnectedAt is not null);
    Check("service recording",
        service.IsRecording && service.RecordingFilePath is not null && File.Exists(service.RecordingFilePath),
        service.RecordingFilePath ?? "null");
    Check("service settings writeback", AppServices.Settings.LastPortName == "DEMO");
    Check("service profile usage", profile.UseCount == useCountBefore + 1 && profile.LastUsedAt is not null,
        $"{useCountBefore}->{profile.UseCount}");
    Check("service session record added", AppServices.Data.Sessions.Count == sessionCountBefore + 1,
        $"{sessionCountBefore}->{AppServices.Data.Sessions.Count}");
    Check("service state events", Volatile.Read(ref stateEvents) >= 2, stateEvents.ToString());

    Check("service rx welcome",
        await WaitUntilAsync(() => TrafficText(TrafficDirection.Rx).Contains("FluentSerial DEMO 串口模拟器 v1.0")),
        TrafficText(TrafficDirection.Rx));
    Check("service counters rx", service.RxFrames > 0 && service.BytesRx > 0, $"frames={service.RxFrames} bytes={service.BytesRx}");
    Check("service counters event", Volatile.Read(ref counterEvents) > 0);

    var payload = SerialCodec.BuildPayload("AT+VER?", PayloadMode.Text, LineEnding.CrLf, ChecksumKind.None, "utf-8", true);
    var txBefore = service.BytesTx;
    var sent = service.Send(payload);
    Check("service send", sent && service.BytesTx == txBefore + payload.Length && service.TxFrames >= 1,
        $"tx={service.BytesTx} frames={service.TxFrames}");
    Check("service tx traffic", traffic.Any(t => t.Direction == TrafficDirection.Tx && t.Data.SequenceEqual(payload)));
    Check("service at+ver reply",
        await WaitUntilAsync(() => TrafficText(TrafficDirection.Rx).Contains("+VER: 2.4.1")
                                        && TrafficText(TrafficDirection.Rx).Contains("OK\r\n")),
        TrafficText(TrafficDirection.Rx));
    Check("service telemetry", await WaitUntilAsync(() => Regex.IsMatch(TrafficText(TrafficDirection.Rx), @"\[SENSOR\] temp=")));

    await Task.Delay(150);
    service.TickRates();
    Check("service rates", service.RxBytesPerSecond > 0, $"{service.RxBytesPerSecond:0.0} B/s");

    // A throwing subscriber must not break the transport thread, and later subscribers still run.
    Action<SerialTraffic> boom = _ => throw new InvalidOperationException("smoke: traffic handler boom");
    service.Traffic += boom;
    var rxBeforeBoom = service.RxFrames;
    service.Send(SerialCodec.BuildPayload("AT", PayloadMode.Text, LineEnding.CrLf, ChecksumKind.None, "utf-8", true));
    Check("service handler exception safe", await WaitUntilAsync(() => service.RxFrames > rxBeforeBoom),
        $"rx {rxBeforeBoom}->{service.RxFrames}");
    service.Traffic -= boom;
    Check("service still open after handler exception", service.IsOpen);

    var totalTxBeforeReset = service.BytesTx;
    var totalRxBeforeReset = service.BytesRx;
    service.ResetCounters();
    Check("service display counters reset independently", service.BytesTx == 0 && service.TxFrames == 0);
    var afterResetPayload = Encoding.ASCII.GetBytes("AT\r\n");
    service.Send(afterResetPayload);
    Check("service counter after reset", service.BytesTx == afterResetPayload.Length);

    var logPath = service.RecordingFilePath;
    service.Close("冒烟测试结束");
    Check("service close", !service.IsOpen && service.State == SerialLinkState.Closed, $"state={service.State}");
    Check("service recording stopped", !service.IsRecording);

    var stateEventsBeforeSecondClose = Volatile.Read(ref stateEvents);
    service.Close("再次关闭");
    Check("service close idempotent", service.State == SerialLinkState.Closed
                                      && Volatile.Read(ref stateEvents) == stateEventsBeforeSecondClose);

    Check("service recording file", logPath is not null && File.Exists(logPath), logPath ?? "null");
    var logText = logPath is not null && File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
    Check("service recorder rx tx", logText.Contains("RX") && logText.Contains("TX")
                                    && logText.Contains("\"AT+VER?\\r\\n\""), logText.Split('\n').LastOrDefault()?.Trim() ?? string.Empty);
    Check("service recorder end note", logText.Contains("冒烟测试结束"));

    var session = AppServices.Data.Sessions.FirstOrDefault();
    Check("service session completed",
        session is not null && session.PortName == "DEMO" && session.ParamsLabel == "115200 8-N-1"
        && session.ProfileName == "Smoke DEMO" && session.EndedAt is not null && session.BytesRx > 0
        && session.BytesTx > 0 && session.Note.Contains("冒烟测试结束"),
        session is null ? "no session" : $"{session.PortName}/{session.Note}/rx={session.BytesRx}/tx={session.BytesTx}");
    Check("service reset preserves session totals", session is not null
        && session.BytesTx == totalTxBeforeReset + afterResetPayload.Length && session.BytesRx >= totalRxBeforeReset);

    Check("audit connection", AppServices.Data.Audit.Any(a => a.Category == "connection" && a.Message.Contains("已连接")));
    Check("audit disconnect", AppServices.Data.Audit.Any(a => a.Category == "connection" && a.Message.Contains("已断开")));
    Check("audit recording", AppServices.Data.Audit.Any(a => a.Category == "record" && a.Message.Contains("开始记录")));
    Check("service no unexpected errors", errors.IsEmpty, string.Join(" | ", errors));

    service.Close("再次关闭");
    Check("service send when closed", !service.Send(new byte[] { 0x01, 0x02 }));

    // Failure path: no leftovers, Chinese reason, Error state and events.
    var failStateEvents = Volatile.Read(ref stateEvents);
    var failErrorCount = errors.Count;
    var failed = service.Open(new SerialConnectionOptions { PortName = "COM199", BaudRate = 9600 });
    Check("service open failure", !failed && service.State == SerialLinkState.Error, $"state={service.State}");
    Check("service failure error",
        !string.IsNullOrWhiteSpace(service.LastError) && service.LastError.Any(ch => ch is >= '\u4E00' and <= '\u9FFF'),
        service.LastError);
    Check("service failure events", errors.Count > failErrorCount && Volatile.Read(ref stateEvents) > failStateEvents);
    Check("service failure no recorder", !service.IsRecording && service.RecordingFilePath is null,
        service.RecordingFilePath ?? "null");
    Check("audit failure", AppServices.Data.Audit.Any(a => a.Category == "connection" && a.Message.Contains("COM199")));

    service.Close("清理失败状态");
    Check("service failure cleanup", service.State == SerialLinkState.Closed && !service.IsOpen);

    var reopenOptions = new SerialConnectionOptions { PortName = "DEMO" };
    var reopened = service.Open(reopenOptions);
    Check("service reopen after failure", reopened && service.IsOpen);
    service.ResetCounters();
    Check("service reset counters", service.BytesRx == 0 && service.BytesTx == 0 && service.RxFrames == 0);
    service.Close("冒烟结束");
    Check("service final closed", service.State == SerialLinkState.Closed);

    using var disposedService = new SerialSessionService();
    disposedService.Dispose();
    var disposedRejected = false;
    try { disposedService.Open(new SerialConnectionOptions { PortName = "DEMO" }); }
    catch (ObjectDisposedException) { disposedRejected = true; }
    Check("service cannot reopen after disposal", disposedRejected && !disposedService.IsOpen);

    service.Traffic -= OnTraffic;
    service.ErrorOccurred -= OnError;
    service.StateChanged -= OnStateChanged;
    service.CountersChanged -= OnCountersChanged;
}
catch (Exception ex)
{
    Check("service section", false, ex.ToString());
}

// ---------------------------------------------------------------- summary

await FluentSerial.Smoke.FileTransferSmoke.RunAsync(Check, smokeData);

try
{
    AppServices.Shutdown();
}
catch (Exception ex)
{
    Console.WriteLine($"WARN shutdown: {ex.Message}");
}

Console.WriteLine($"RESULT passed={passed} failed={failures}");
return failures == 0 ? 0 : 1;
