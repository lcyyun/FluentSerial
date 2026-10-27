using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace FluentSerial.Core;

/// <summary>
/// Enumerates the real Windows serial ports (SetupAPI first, registry / <see cref="SerialPort"/>
/// as fallbacks) and always appends the built-in DEMO loopback entry.
/// </summary>
public static class SerialPortCatalog
{
    public const string DemoName = "DEMO";
    public const string DemoDescription = "内置回环模拟器（无需硬件）";

    private static readonly Guid PortsClassGuid = new("4D36E978-E325-11CE-BFC1-08002BE10318");
    private static readonly Regex ComPattern = new(@"\bCOM(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// All selectable ports, real ones first (sorted by COM number) and DEMO last.
    /// Re-enumerates on every call so the UI refresh button sees hot-plugged adapters.
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> GetPorts()
    {
        var ports = EnumerateRealPorts();
        ports.Add(new SerialPortInfo(DemoName, DemoDescription));
        return ports.AsReadOnly();
    }

    public static bool IsDemo(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Trim().Equals(DemoName, StringComparison.OrdinalIgnoreCase);
    }

    public static SerialPortInfo? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        if (IsDemo(trimmed)) return new SerialPortInfo(DemoName, DemoDescription);

        var normalized = NormalizeComName(trimmed);
        foreach (var info in GetPorts())
        {
            if (info.IsDemo) continue;
            if (string.Equals(info.Name, trimmed, StringComparison.OrdinalIgnoreCase)) return info;
            if (normalized is not null && string.Equals(info.Name, normalized, StringComparison.OrdinalIgnoreCase)) return info;
        }
        return null;
    }

    /// <summary>Name of the first physical port, or an empty string when only the DEMO entry exists.</summary>
    public static string ResolveFirstRealPort()
    {
        foreach (var info in GetPorts())
        {
            if (!info.IsDemo) return info.Name;
        }
        return string.Empty;
    }

    private static List<SerialPortInfo> EnumerateRealPorts()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            CollectSetupApiPorts(found);
        }
        catch (Exception ex)
        {
            LogFailure("SetupAPI", ex);
        }

        if (found.Count == 0)
        {
            try
            {
                CollectRegistryPorts(found);
            }
            catch (Exception ex)
            {
                LogFailure("注册表", ex);
            }
        }

        try
        {
            foreach (var name in SerialPort.GetPortNames())
                AddPort(found, name, string.Empty);
        }
        catch (Exception ex)
        {
            LogFailure("SerialPort.GetPortNames", ex);
        }

        return found
            .Select(pair => new SerialPortInfo(pair.Key, pair.Value))
            .OrderBy(info => ComNumber(info.Name))
            .ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CollectSetupApiPorts(Dictionary<string, string> found)
    {
        var classGuid = PortsClassGuid;
        var deviceSet = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (deviceSet == IntPtr.Zero || deviceSet == new IntPtr(-1)) return;
        try
        {
            var info = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(deviceSet, index, ref info); index++)
            {
                var friendly = ReadStringProperty(deviceSet, ref info, SPDRP_FRIENDLYNAME);
                var description = ReadStringProperty(deviceSet, ref info, SPDRP_DEVICEDESC);
                var com = ExtractComName(friendly)
                          ?? TryReadDevicePortName(deviceSet, ref info)
                          ?? ExtractComName(description);
                if (com is null) continue;

                var label = !string.IsNullOrWhiteSpace(friendly) ? friendly : description;
                AddPort(found, com, label ?? string.Empty);
            }
        }
        finally
        {
            try { SetupDiDestroyDeviceInfoList(deviceSet); } catch { /* best effort */ }
        }
    }

    private static void CollectRegistryPorts(Dictionary<string, string> found)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (key is null) return;
        foreach (var valueName in key.GetValueNames())
        {
            if (key.GetValue(valueName) is not string value) continue;
            var com = NormalizeComName(value);
            if (com is null) continue;
            AddPort(found, com, $"串口设备（{DescribeDevicePath(valueName)}）");
        }
    }

    private static void AddPort(Dictionary<string, string> found, string? name, string? description)
    {
        var com = NormalizeComName(name);
        if (com is null) return;
        var label = (description ?? string.Empty).Trim();
        if (!found.TryGetValue(com, out var existing) || string.IsNullOrWhiteSpace(existing))
        {
            found[com] = string.IsNullOrWhiteSpace(label) ? "串口设备" : label;
        }
    }

    private static string? ReadStringProperty(IntPtr deviceSet, ref SpDevInfoData info, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(deviceSet, ref info, property, out _, buffer,
                (uint)buffer.Length, out var requiredSize))
        {
            if (requiredSize <= buffer.Length || requiredSize > 64 * 1024) return null;
            buffer = new byte[requiredSize];
            if (!SetupDiGetDeviceRegistryProperty(deviceSet, ref info, property, out _, buffer,
                    (uint)buffer.Length, out requiredSize))
                return null;
        }

        if (requiredSize < 2) return null;
        var length = (int)Math.Min(requiredSize, (uint)buffer.Length);
        var text = Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? TryReadDevicePortName(IntPtr deviceSet, ref SpDevInfoData info)
    {
        var handle = SetupDiOpenDevRegKey(deviceSet, ref info, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
        try
        {
            using var safeHandle = new SafeRegistryHandle(handle, ownsHandle: true);
            using var key = RegistryKey.FromHandle(safeHandle);
            return NormalizeComName(key.GetValue("PortName") as string);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractComName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = ComPattern.Match(text);
        return match.Success ? "COM" + match.Groups[1].Value : null;
    }

    private static string? NormalizeComName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var match = ComPattern.Match(raw);
        if (!match.Success) return null;
        return "COM" + match.Groups[1].Value;
    }

    private static int ComNumber(string name)
        => int.TryParse(name.AsSpan(3), out var number) ? number : int.MaxValue;

    private static string DescribeDevicePath(string valueName)
    {
        var trimmed = valueName.Trim();
        var slash = trimmed.LastIndexOf('\\');
        var tail = slash >= 0 && slash + 1 < trimmed.Length ? trimmed[(slash + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(tail) ? trimmed : tail;
    }

    private static void LogFailure(string source, Exception ex)
    {
        try
        {
            AppServices.LogError($"串口枚举失败（{source}）", ex);
        }
        catch
        {
            // diagnostics only
        }
    }

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint SPDRP_DEVICEDESC = 0x00000000;
    private const uint SPDRP_FRIENDLYNAME = 0x0000000C;
    private const uint DICS_FLAG_GLOBAL = 0x00000001;
    private const uint DIREG_DEV = 0x00000001;
    private const uint KEY_READ = 0x00020019;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
        uint property, out uint propertyRegDataType, [Out] byte[]? propertyBuffer, uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
        uint scope, uint hwProfile, uint keyType, uint samDesired);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
