using System.Globalization;
using System.Text;

namespace FluentSerial.Core;

/// <summary>
/// Conversions between what the user types and the bytes on the wire: HEX parsing,
/// text encoding, escape sequences, line endings and checksum appending.
/// </summary>
public static class SerialCodec
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    static SerialCodec()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // GBK simply falls back to UTF-8 if the provider is unavailable.
        }
    }

    public static Encoding GetEncoding(string? name)
    {
        var key = (name ?? string.Empty).Trim().ToLowerInvariant();
        return key switch
        {
            "gbk" or "gb2312" or "gb18030" or "936" or "cp936" => Encoding.GetEncoding(936),
            "ascii" or "us-ascii" => Encoding.ASCII,
            "iso-8859-1" or "latin-1" or "latin1" => Encoding.Latin1,
            _ => Utf8NoBom
        };
    }

    public static byte[] LineEndingBytes(LineEnding ending) => ending switch
    {
        LineEnding.Cr => new byte[] { 0x0D },
        LineEnding.Lf => new byte[] { 0x0A },
        LineEnding.CrLf => new byte[] { 0x0D, 0x0A },
        _ => Array.Empty<byte>()
    };

    /// <summary>Parses "01 02 0x03,04" or "01020304". Throws a user-facing FormatException.</summary>
    public static byte[] ParseHex(string input)
    {
        if (!TryParseHex(input, out var bytes, out var error))
            throw new FormatException(error);
        return bytes;
    }

    public static bool TryParseHex(string input, out byte[] bytes, out string? error)
    {
        bytes = Array.Empty<byte>();
        error = null;
        if (string.IsNullOrWhiteSpace(input)) return true;

        var digits = new StringBuilder(input.Length);
        var digitPositions = new List<int>();
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (char.IsWhiteSpace(ch) || ch is ',' or ';' or ':' or '|' or '-' or '\u3001')
                continue;

            if (ch is '0' && i + 1 < input.Length && (input[i + 1] is 'x' or 'X'))
            {
                i += 1;
                continue;
            }

            if (ch is 'x' or 'X') continue; // tolerate bare x after 0x stripping

            if (Uri.IsHexDigit(ch))
            {
                digits.Append(ch);
                digitPositions.Add(i);
                continue;
            }

            error = $"HEX 中包含无效字符“{ch}”（位置 {i + 1}）";
            return false;
        }

        if (digits.Length == 0) return true;
        if (digits.Length % 2 != 0)
        {
            var pos = digitPositions[^1] + 1;
            error = $"HEX 字节必须是偶数位，最后一个半字节位于位置 {pos}";
            return false;
        }

        var result = new byte[digits.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(digits.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                error = $"无法解析 HEX 字节 #{i + 1}";
                return false;
            }
            result[i] = value;
        }

        bytes = result;
        return true;
    }

    public static string FormatHex(ReadOnlySpan<byte> data, string separator = " ")
    {
        if (data.Length == 0) return string.Empty;
        var sb = new StringBuilder(data.Length * 3);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(separator);
            sb.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string FormatHexLines(ReadOnlySpan<byte> data, int bytesPerLine = 16, bool withOffset = true)
    {
        if (data.Length == 0) return string.Empty;
        bytesPerLine = Math.Clamp(bytesPerLine, 1, 64);
        var sb = new StringBuilder();
        for (var offset = 0; offset < data.Length; offset += bytesPerLine)
        {
            var count = Math.Min(bytesPerLine, data.Length - offset);
            if (sb.Length > 0) sb.AppendLine();
            if (withOffset) sb.Append(offset.ToString("X4", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[offset + i].ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    /// <summary>Encodes text with the selected code page and optional \r \n \t \0 \\ \xNN escapes.</summary>
    public static byte[] EncodeText(string text, string? encodingName, bool useEscapes)
    {
        var encoding = GetEncoding(encodingName);
        if (!useEscapes) return encoding.GetBytes(text);

        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\\' || i + 1 >= text.Length)
            {
                bytes.AddRange(encoding.GetBytes(ch.ToString()));
                continue;
            }

            var next = text[i + 1];
            switch (next)
            {
                case 'r':
                    bytes.Add(0x0D);
                    i += 1;
                    break;
                case 'n':
                    bytes.Add(0x0A);
                    i += 1;
                    break;
                case 't':
                    bytes.Add(0x09);
                    i += 1;
                    break;
                case '0':
                    bytes.Add(0x00);
                    i += 1;
                    break;
                case '\\':
                    bytes.Add(0x5C);
                    i += 1;
                    break;
                case 'x' or 'X':
                    if (i + 3 < text.Length && Uri.IsHexDigit(text[i + 2]) && Uri.IsHexDigit(text[i + 3]))
                    {
                        bytes.Add(byte.Parse(text.Substring(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 3;
                    }
                    else
                    {
                        // Leave malformed escapes untouched rather than losing the user's text.
                        bytes.Add(0x5C);
                    }
                    break;
                case 'u':
                    if (i + 5 < text.Length && ushort.TryParse(text.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    {
                        bytes.AddRange(encoding.GetBytes(char.ConvertFromUtf32(code)));
                        i += 5;
                    }
                    else
                    {
                        bytes.Add(0x5C);
                    }
                    break;
                default:
                    bytes.Add(0x5C);
                    break;
            }
        }
        return bytes.ToArray();
    }

    /// <summary>Human-readable escape for display/preview purposes.</summary>
    public static string UnescapeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\\' || i + 1 >= text.Length)
            {
                sb.Append(ch);
                continue;
            }

            var next = text[i + 1];
            switch (next)
            {
                case 'r': sb.Append('\r'); i++; break;
                case 'n': sb.Append('\n'); i++; break;
                case 't': sb.Append('\t'); i++; break;
                case '0': sb.Append('\0'); i++; break;
                case '\\': sb.Append('\\'); i++; break;
                case 'x' or 'X' when i + 3 < text.Length && Uri.IsHexDigit(text[i + 2]) && Uri.IsHexDigit(text[i + 3]):
                    sb.Append((char)byte.Parse(text.Substring(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 3;
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    public static string DecodeText(byte[] data, string? encodingName)
        => GetEncoding(encodingName).GetString(data);

    /// <summary>Formats control characters as visible escapes for one-line console output.</summary>
    public static string EscapeForDisplay(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (char.IsControl(ch)) sb.Append("\\x").Append(((int)ch).ToString("X2", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds the exact bytes for the send pipeline: body + checksum (over the body)
    /// + selected line ending. Throws FormatException with a user-facing message.
    /// </summary>
    public static byte[] BuildPayload(string input, PayloadMode mode, LineEnding ending,
        ChecksumKind checksum, string? encodingName, bool useEscapes)
    {
        var body = mode == PayloadMode.Hex
            ? ParseHex(input)
            : EncodeText(input, encodingName, useEscapes);
        var checksumBytes = checksum == ChecksumKind.None ? Array.Empty<byte>() : Checksums.Compute(checksum, body);
        var endingBytes = LineEndingBytes(ending);
        if (checksumBytes.Length == 0 && endingBytes.Length == 0) return body;
        var result = new byte[body.Length + checksumBytes.Length + endingBytes.Length];
        body.CopyTo(result, 0);
        checksumBytes.CopyTo(result, body.Length);
        endingBytes.CopyTo(result, body.Length + checksumBytes.Length);
        return result;
    }

    public static bool TryBuildPayload(string input, PayloadMode mode, LineEnding ending,
        ChecksumKind checksum, string? encodingName, bool useEscapes, out byte[] payload, out string? error)
    {
        try
        {
            payload = BuildPayload(input, mode, ending, checksum, encodingName, useEscapes);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            payload = Array.Empty<byte>();
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            payload = Array.Empty<byte>();
            error = $"无法生成发送数据：{ex.Message}";
            return false;
        }
    }

    /// <summary>A compact wire preview used by the command editor and send preview.</summary>
    public static string DescribeBytes(ReadOnlySpan<byte> data, string separator = " ")
        => data.Length == 0 ? "（空）" : FormatHex(data, separator);

    public static string DescribePayload(byte[] data, PayloadMode mode, string? encodingName, int maxBytes = 32)
    {
        if (data.Length == 0) return "（空）";
        var slice = data.Length > maxBytes ? data.AsSpan(0, maxBytes) : data.AsSpan();
        var hex = FormatHex(slice);
        var suffix = data.Length > maxBytes ? $" … (+{data.Length - maxBytes} B)" : string.Empty;
        if (mode == PayloadMode.Hex) return hex + suffix;
        var text = EscapeForDisplay(DecodeText(data, encodingName));
        if (text.Length > 90) text = text[..90] + "…";
        return $"{text}  [{hex}{suffix}]";
    }
}
