using System.IO;
using System.IO.Ports;
using System.Text;

namespace FluentSerial.Core;

/// <summary>
/// Deterministic demo data used by --seed-demo / docs screenshots. It only writes to the
/// current data directory (the capture script points FLUENTSERIAL_DATA_DIR at a scratch folder).
/// </summary>
public static class DemoDataSeeder
{
    public static void Seed()
    {
        var data = AppServices.Data;
        data.Profiles.Clear();
        data.Sessions.Clear();
        data.Audit.Clear();

        var now = DateTime.Now;
        data.Profiles.Add(new PortProfile
        {
            Name = "USB 转串口",
            PortName = "COM3",
            BaudRate = 115200,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            Dtr = true,
            Rts = true,
            ReceiveMode = PayloadMode.Text,
            SendMode = PayloadMode.Text,
            Encoding = "utf-8",
            Favorite = true,
            Notes = "CH340 调试口，115200 8-N-1",
            LastUsedAt = now.AddHours(-2),
            UseCount = 26
        });
        data.Profiles.Add(new PortProfile
        {
            Name = "调试串口",
            PortName = "COM1",
            BaudRate = 9600,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReceiveMode = PayloadMode.Text,
            SendMode = PayloadMode.Text,
            Encoding = "gbk",
            Notes = "设备调试口，输出中文日志",
            LastUsedAt = now.AddDays(-1).AddHours(-3),
            UseCount = 11
        });
        data.Profiles.Add(new PortProfile
        {
            Name = "蓝牙模块",
            PortName = "COM5",
            BaudRate = 115200,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.RequestToSend,
            ReceiveMode = PayloadMode.Text,
            SendMode = PayloadMode.Text,
            Notes = "HC-05 / BLE 串口透传模块",
            LastUsedAt = now.AddDays(-2),
            UseCount = 8
        });
        data.Profiles.Add(new PortProfile
        {
            Name = "Modbus 从站",
            PortName = "COM4",
            BaudRate = 19200,
            DataBits = 8,
            Parity = Parity.Even,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReceiveMode = PayloadMode.Hex,
            SendMode = PayloadMode.Hex,
            Encoding = "utf-8",
            Favorite = true,
            Notes = "RS-485 从站，寄存器读写",
            LastUsedAt = now.AddDays(-4).AddHours(-1),
            UseCount = 15
        });
        data.Profiles.Add(new PortProfile
        {
            Name = "LoRa 网关",
            PortName = "COM6",
            BaudRate = 57600,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReceiveMode = PayloadMode.Hex,
            SendMode = PayloadMode.Hex,
            Notes = "持续上报二进制遥测帧",
            LastUsedAt = now.AddDays(-6),
            UseCount = 4
        });
        data.Profiles.Add(new PortProfile
        {
            Name = "无硬件体验",
            PortName = "DEMO",
            BaudRate = 115200,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReceiveMode = PayloadMode.Text,
            SendMode = PayloadMode.Text,
            Notes = "内置 DEMO 回环，无需串口硬件",
            LastUsedAt = now.AddMinutes(-25),
            UseCount = 3
        });

        EnsureCommand("读取传感器", "AT+SENSOR?", "AT 指令", "读取温湿度与 RSSI", PayloadMode.Text, LineEnding.CrLf);
        EnsureCommand("Modbus 写单个寄存器", "01 06 00 10 00 64 88 B3", "Modbus RTU", "向 0x0010 写入 100", PayloadMode.Hex, LineEnding.None);
        EnsureCommand("自定义心跳", "PING", "自定义", "每秒发送一次的心跳帧", PayloadMode.Text, LineEnding.CrLf);

        SeedSessions(now);
        SeedAudit(now);

        var settings = AppServices.Settings;
        settings.ShowTimestamp = true;
        settings.ShowTx = true;
        settings.AutoScroll = true;
        settings.AutoRecord = true;
        settings.UseEscapes = true;
        settings.LastPortName = "DEMO";
        settings.LastBaudRate = 115200;
        settings.DefaultEncoding = "utf-8";
        settings.ConsoleFontSize = 13.5;
        AppServices.Store.Save();
        AppServices.Audit.Add(AuditLevel.Info, "system", "已载入演示数据");
    }

    private static void EnsureCommand(string name, string payload, string group, string description,
        PayloadMode mode, LineEnding ending)
    {
        if (AppServices.Data.Commands.Any(c => c.Name == name)) return;
        AppServices.Data.Commands.Add(new QuickCommand
        {
            Name = name,
            Payload = payload,
            Group = group,
            Description = description,
            Mode = mode,
            Ending = ending
        });
    }

    private static void SeedSessions(DateTime now)
    {
        var entries = new (string Port, string Params, string Profile, DateTime Start, TimeSpan Duration, long Rx, long Tx, string Note)[]
        {
            ("COM3", "115200 8-N-1", "USB 转串口", now.AddHours(-2).AddMinutes(-14), TimeSpan.FromMinutes(14), 87_412, 4_096, "传感器调试，采集 20 分钟遥测"),
            ("COM5", "115200 8-N-1", "蓝牙模块", now.AddHours(-5).AddMinutes(-38), TimeSpan.FromMinutes(9), 12_840, 1_024, "AT 指令握手与透传测试"),
            ("COM4", "19200 8-E-1", "Modbus 从站", now.AddDays(-1).AddHours(-2), TimeSpan.FromMinutes(31), 6_144, 2_048, "读取保持寄存器 0x0000-0x0007"),
            ("COM1", "9600 8-N-1", "调试串口", now.AddDays(-1).AddHours(-6), TimeSpan.FromMinutes(7), 23_552, 512, "GBK 中文日志输出"),
            ("COM6", "57600 8-N-1", "LoRa 网关", now.AddDays(-2).AddHours(-1), TimeSpan.FromMinutes(52), 512_000, 0, "二进制遥测帧录制"),
            ("COM3", "115200 8-N-1", "USB 转串口", now.AddDays(-3).AddHours(-4), TimeSpan.FromMinutes(22), 164_280, 8_192, "固件升级前回归测试")
        };

        foreach (var entry in entries)
        {
            var session = new SerialSessionRecord
            {
                PortName = entry.Port,
                ParamsLabel = entry.Params,
                ProfileName = entry.Profile,
                StartedAt = entry.Start,
                EndedAt = entry.Start.Add(entry.Duration),
                BytesRx = entry.Rx,
                BytesTx = entry.Tx,
                Note = entry.Note
            };
            session.FilePath = CreateSessionFile(session);
            AppServices.Data.Sessions.Add(session);
        }
    }

    private static string CreateSessionFile(SerialSessionRecord session)
    {
        try
        {
            var safePort = session.PortName.Replace('\\', '_');
            var path = Path.Combine(AppServices.Paths.LogsDirectory,
                $"{session.StartedAt:yyyyMMdd-HHmmss}-{safePort}.log");
            var sb = new StringBuilder();
            sb.AppendLine("# FluentSerial session log");
            sb.AppendLine($"# Port: {session.PortName} ({session.ParamsLabel})");
            sb.AppendLine($"# Started: {session.StartedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"# Ended:   {session.EndedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"# Bytes:   RX {session.BytesRx} / TX {session.BytesTx}");
            sb.AppendLine();
            sb.AppendLine($"[{session.StartedAt:HH:mm:ss.fff}] INFO 已打开 {session.PortName}（{session.ParamsLabel}）");
            sb.AppendLine($"[{session.StartedAt.AddSeconds(1):HH:mm:ss.fff}] RX   4 B | 4F 4B 0D 0A | \"OK..\"");
            sb.AppendLine($"[{session.StartedAt.AddSeconds(2):HH:mm:ss.fff}] TX   8 B | 41 54 2B 56 45 52 3F 0D | \"AT+VER?\"");
            sb.AppendLine($"[{session.StartedAt.AddSeconds(3):HH:mm:ss.fff}] RX  16 B | 2B 56 45 52 3A 20 32 2E 34 2E 31 0D 0A 4F 4B 0D | \"+VER: 2.4.1..OK\"");
            sb.AppendLine($"[{session.StartedAt.AddSeconds(4):HH:mm:ss.fff}] INFO 会话结束，记录完整");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            AppServices.LogError("生成演示会话记录失败", ex);
            return string.Empty;
        }
    }

    private static void SeedAudit(DateTime now)
    {
        var entries = new (AuditLevel Level, string Category, string Message, DateTime At)[]
        {
            (AuditLevel.Info, "connection", "连接 COM3（115200 8-N-1）", now.AddHours(-2).AddMinutes(-14)),
            (AuditLevel.Info, "record", "开始记录会话日志", now.AddHours(-2).AddMinutes(-14)),
            (AuditLevel.Info, "connection", "断开 COM3", now.AddHours(-2)),
            (AuditLevel.Info, "profile", "更新端口预设「Modbus 从站」", now.AddDays(-1).AddHours(-3)),
            (AuditLevel.Warning, "connection", "打开 COM5 失败：端口被其他程序占用", now.AddDays(-1).AddHours(-5)),
            (AuditLevel.Info, "settings", "更新应用设置", now.AddDays(-1).AddHours(-6)),
            (AuditLevel.Info, "connection", "连接 COM4（19200 8-E-1）", now.AddDays(-1).AddHours(-2)),
            (AuditLevel.Info, "send", "发送 Modbus 读保持寄存器帧", now.AddDays(-1).AddHours(-2).AddMinutes(-10)),
            (AuditLevel.Error, "connection", "COM6 设备已拔出，会话中断", now.AddDays(-2).AddHours(-1)),
            (AuditLevel.Info, "record", "删除过期会话记录", now.AddDays(-4).AddHours(-2))
        };
        foreach (var entry in entries)
        {
            AppServices.Data.Audit.Add(new AuditEvent
            {
                At = entry.At,
                Level = entry.Level,
                Category = entry.Category,
                Message = entry.Message
            });
        }
    }
}
