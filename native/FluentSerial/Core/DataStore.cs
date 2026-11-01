using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluentSerial.Core;

public sealed class AppPaths
{
    public string Root { get; }
    public string StoreFile => Path.Combine(Root, "store.json");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string ExportsDirectory => Path.Combine(Root, "exports");

    public AppPaths()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("FLUENTSERIAL_DATA_DIR");
        Root = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FluentSerial")
            : Path.GetFullPath(overrideRoot);
    }

    public void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ExportsDirectory);
    }

    public string NewSessionLogPath(string portName, string? profileName)
    {
        var label = string.IsNullOrWhiteSpace(profileName) ? portName : $"{profileName}-{portName}";
        var safe = string.Concat(label.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (string.IsNullOrWhiteSpace(safe)) safe = "session";
        // Reconnects can occur within one clock tick. A unique suffix prevents a later
        // session from replacing a previous recording, even across app instances.
        if (safe.Length > 80) safe = safe[..80];
        return Path.Combine(LogsDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{safe}-{Guid.NewGuid():N}.log");
    }
}

/// <summary>
/// Local JSON persistence. Everything lives on this machine; there is no telemetry
/// and no cloud sync. Writes are atomic so a crash cannot truncate the store.
/// </summary>
public sealed class DataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private bool _loaded;

    public AppPaths Paths { get; }
    public AppData Data { get; private set; } = new();
    public AppSettings Settings => Data.Settings;

    public DataStore(AppPaths paths)
    {
        Paths = paths;
    }

    public void Load()
    {
        lock (_sync)
        {
            if (_loaded) return;
            Paths.Ensure();
            var fresh = !File.Exists(Paths.StoreFile);
            Data = LoadJson<AppData>(Paths.StoreFile) ?? new AppData();
            Normalize(Data);
            if (fresh) SeedDefaults(Data);
            _loaded = true;
        }
    }

    private static void Normalize(AppData data)
    {
        data.Profiles ??= new ObservableCollection<PortProfile>();
        data.Commands ??= new ObservableCollection<QuickCommand>();
        data.Sessions ??= new ObservableCollection<SerialSessionRecord>();
        data.Audit ??= new ObservableCollection<AuditEvent>();
        data.Settings ??= new AppSettings();
    }

    private static void SeedDefaults(AppData data)
    {
        void Add(string name, string payload, string group, string description, PayloadMode mode, LineEnding ending, ChecksumKind checksum = ChecksumKind.None, bool favorite = false)
        {
            data.Commands.Add(new QuickCommand
            {
                Name = name,
                Payload = payload,
                Group = group,
                Description = description,
                Mode = mode,
                Ending = ending,
                Checksum = checksum,
                Favorite = favorite
            });
        }

        Add("握手测试", "AT", "AT 指令", "确认模块能正常响应", PayloadMode.Text, LineEnding.CrLf, favorite: true);
        Add("查询版本", "AT+VER?", "AT 指令", "读取固件版本号", PayloadMode.Text, LineEnding.CrLf);
        Add("模块信息", "ATI", "AT 指令", "读取模块厂商与型号", PayloadMode.Text, LineEnding.CrLf);
        Add("关闭回显", "ATE0", "AT 指令", "关闭命令回显", PayloadMode.Text, LineEnding.CrLf);
        Add("复位模块", "AT+RST", "AT 指令", "软复位设备", PayloadMode.Text, LineEnding.CrLf);
        Add("Modbus 读保持寄存器", "01 03 00 00 00 02 C4 0B", "Modbus RTU", "从站 1 读取 2 个保持寄存器", PayloadMode.Hex, LineEnding.None, ChecksumKind.None);
        Add("Modbus 读输入寄存器", "01 04 00 00 00 01 B0 0A", "Modbus RTU", "从站 1 读取 1 个输入寄存器", PayloadMode.Hex, LineEnding.None, ChecksumKind.None);
    }

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                Paths.Ensure();
                SaveJson(Paths.StoreFile, Data);
            }
            catch (Exception ex)
            {
                AppServices.LogError("保存本地数据失败", ex);
            }
        }
    }

    private static T? LoadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text, JsonOptions);
        }
        catch
        {
            TryBackupCorrupt(path);
            return null;
        }
    }

    private static void SaveJson<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryBackupCorrupt(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
        }
        catch
        {
            // ignore
        }
    }

    public PortProfile? FindProfile(string? id) => string.IsNullOrEmpty(id) ? null : Data.Profiles.FirstOrDefault(p => p.Id == id);
    public QuickCommand? FindCommand(string? id) => string.IsNullOrEmpty(id) ? null : Data.Commands.FirstOrDefault(c => c.Id == id);

    public void RemoveProfile(PortProfile profile)
    {
        Data.Profiles.Remove(profile);
        Save();
    }

    public void RemoveCommand(QuickCommand command)
    {
        Data.Commands.Remove(command);
        Save();
    }

    public void RemoveSession(SerialSessionRecord session)
    {
        Data.Sessions.Remove(session);
        Save();
    }
}
