namespace FluentSerial.Core;

/// <summary>Serial checksum helpers used by the send pipeline.</summary>
public static class Checksums
{
    public static byte Sum8(ReadOnlySpan<byte> data)
    {
        byte sum = 0;
        foreach (var b in data) sum = unchecked((byte)(sum + b));
        return sum;
    }

    public static byte Xor8(ReadOnlySpan<byte> data)
    {
        byte xor = 0;
        foreach (var b in data) xor ^= b;
        return xor;
    }

    public static ushort Crc16Modbus(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    public static ushort Crc16Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    /// <summary>
    /// Returns bytes to append after the payload. SUM8/XOR8 are one byte, Modbus CRC is
    /// low-byte-first (its wire format), CCITT is high-byte-first.
    /// </summary>
    public static byte[] Compute(ChecksumKind kind, ReadOnlySpan<byte> data) => kind switch
    {
        ChecksumKind.Sum8 => new[] { Sum8(data) },
        ChecksumKind.Xor8 => new[] { Xor8(data) },
        ChecksumKind.Crc16Modbus => LowHigh(Crc16Modbus(data)),
        ChecksumKind.Crc16Ccitt => HighLow(Crc16Ccitt(data)),
        _ => Array.Empty<byte>()
    };

    private static byte[] LowHigh(ushort value) => new[] { (byte)(value & 0xFF), (byte)(value >> 8) };
    private static byte[] HighLow(ushort value) => new[] { (byte)(value >> 8), (byte)(value & 0xFF) };
}
