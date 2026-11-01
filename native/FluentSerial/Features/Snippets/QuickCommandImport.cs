using System.Text.Json;
using System.Text.Json.Serialization;
using FluentSerial.Core;

namespace FluentSerial.Features;

/// <summary>Validates an entire import before any command is added to the store.</summary>
public static class QuickCommandImport
{
    public const int MaxFileBytes = 4 * 1024 * 1024;
    public const int MaxCommands = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public sealed record Result(IReadOnlyList<QuickCommand> Commands, int DuplicateCount);

    public static Result Parse(string json, IEnumerable<QuickCommand> existing,
        string encoding, bool useEscapes)
    {
        var imported = JsonSerializer.Deserialize<List<QuickCommand?>>(json, JsonOptions)
            ?? throw new FormatException("文件应为指令数组，不能是 null。");
        if (imported.Count > MaxCommands)
            throw new FormatException($"一次最多导入 {MaxCommands} 条指令，请拆分文件后重试。");

        // Include every editable field so only exact copies are skipped. Re-importing
        // a revised command remains possible without overwriting the original.
        static string Key(QuickCommand c) => JsonSerializer.Serialize(new
        {
            c.Name, c.Group, c.Payload, c.Description, c.Mode, c.Ending, c.Checksum, c.Favorite
        });

        var known = new HashSet<string>(existing.Select(Key), StringComparer.Ordinal);
        var accepted = new List<QuickCommand>();
        var duplicates = 0;
        for (var index = 0; index < imported.Count; index++)
        {
            var command = imported[index];
            var prefix = $"第 {index + 1} 条指令";
            if (command is null) throw new FormatException($"{prefix}为空，请移除 null 条目。");
            command.Name = command.Name?.Trim() ?? string.Empty;
            command.Group = string.IsNullOrWhiteSpace(command.Group) ? "自定义" : command.Group.Trim();
            command.Payload ??= string.Empty;
            command.Description = command.Description?.Trim() ?? string.Empty;
            if (command.Name.Length == 0) throw new FormatException($"{prefix}缺少名称。");
            if (!Enum.IsDefined(command.Mode) || !Enum.IsDefined(command.Ending) ||
                !Enum.IsDefined(command.Checksum))
                throw new FormatException($"{prefix}「{command.Name}」的模式、行尾或校验类型无效。");
            if (!SerialCodec.TryBuildPayload(command.Payload, command.Mode, command.Ending,
                    command.Checksum, encoding, useEscapes, out var bytes, out var error))
                throw new FormatException($"{prefix}「{command.Name}」：{error}");
            if (bytes.Length == 0)
                throw new FormatException($"{prefix}「{command.Name}」的载荷不能为空。");
            if (!known.Add(Key(command)))
            {
                duplicates++;
                continue;
            }

            command.Id = Guid.NewGuid().ToString("N");
            command.UseCount = 0;
            command.CreatedAt = DateTime.Now;
            accepted.Add(command);
        }
        return new Result(accepted, duplicates);
    }
}
