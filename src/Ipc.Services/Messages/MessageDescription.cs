using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Cl.Interpreter;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Core.Work;

namespace Ipc.Services.Messages;

public sealed record MessageDataField(string Type, int Length, int Decimals = 0, int LengthPrefix = 0);
public sealed record MessageDescription(string Text, string SecondLevel, int Severity,
    IReadOnlyList<MessageDataField> Fields, int Ccsid, string? DefaultReply = null, bool LegacyLiteral = false);
public sealed record FormattedMessage(ProgramBuffer Text, ProgramBuffer SecondLevel, int Severity, ProgramBuffer? DefaultReply);

public static class MessageDescriptionFormat
{
    public static IReadOnlyList<MessageDataField> Parse(string source)
    {
        if (source.Trim().Equals("*NONE", StringComparison.OrdinalIgnoreCase)) return Array.Empty<MessageDataField>();
        var entries = CommandParser.Tokenize(source);
        if (entries.Count == 0 || entries.Count > 99) throw Invalid("FMT requires from one to 99 fields.");
        // A single format may omit its element-list parentheses.
        if (!entries[0].StartsWith('(')) entries = new[] { "(" + source + ")" };
        var fields = new List<MessageDataField>();
        foreach (var entry in entries)
        {
            if (!entry.StartsWith('(') || !entry.EndsWith(')')) throw Invalid("Invalid message field list.");
            var values = CommandParser.Tokenize(entry[1..^1]);
            if (values.Count is < 1 or > 3) throw Invalid("Invalid message field attributes.");
            var type = values[0].ToUpperInvariant();
            var length = values.Count < 2 ? type is "*BIN" or "*UBIN" ? "2" : "*VARY" : values[1].ToUpperInvariant();
            var varying = length == "*VARY";
            int Number(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : throw Invalid("Invalid message field length.");
            var extra = values.Count == 3 ? Number(values[2]) : varying ? 2 : 0;
            fields.Add(new(type, varying ? 0 : Number(length), varying ? 0 : extra, varying ? extra : 0));
        }
        ValidateFields(fields); return fields.AsReadOnly();
    }

    public static void Validate(MessageDescription description, bool legacyText = false)
    {
        if (description.Text is null || description.SecondLevel is null || description.Fields is null ||
            description.Text.Length is < 1 || description.Text.Length > (legacyText ? 512 : 132) || description.SecondLevel.Length > 3000 ||
            description.Severity is < 0 or > 99 || !CodePage.IsSupported(description.Ccsid) ||
            description.Text.Any(char.IsControl) || description.SecondLevel.Any(char.IsControl) || description.DefaultReply?.Any(char.IsControl) == true)
            throw Invalid("Invalid message text, severity or CCSID.");
        if (description.LegacyLiteral && (!legacyText || description.Fields.Count != 0)) throw Invalid("Invalid legacy literal message.");
        ValidateFields(description.Fields);
        foreach (Match match in Regex.Matches(description.LegacyLiteral ? "" : description.Text + description.SecondLevel, @"&([0-9]+)"))
            if (!int.TryParse(match.Groups[1].Value, out var field) || field < 1 || field > description.Fields.Count)
                throw Invalid("Message text references an undefined substitution field.");
        var encoding = EncodingFor(description.Ccsid);
        try
        {
            _ = encoding.GetBytes(description.Text); _ = encoding.GetBytes(description.SecondLevel);
            if (description.DefaultReply is { } reply && encoding.GetByteCount(reply) > 132) throw Invalid("Default reply exceeds 132 bytes.");
        }
        catch (EncoderFallbackException) { throw Invalid("Message text is not representable in its CCSID."); }
    }

    private static void ValidateFields(IReadOnlyList<MessageDataField> fields)
    {
        if (fields.Count > 99) throw Invalid("At most 99 substitution fields are supported.");
        var size = 0;
        foreach (var field in fields)
        {
            if (field is null) throw Invalid("Missing substitution field.");
            var valid = field.Type switch {
                "*CHAR" or "*QTDCHAR" or "*HEX" => field.Decimals == 0 &&
                    (field.LengthPrefix is 2 or 4 && field.Length == 0 || field.LengthPrefix == 0 && field.Length is >= 1 and <= 512),
                "*CCHAR" => field.Decimals == 0 && field.LengthPrefix is 2 or 4 && field.Length == 0,
                "*DEC" => field.LengthPrefix == 0 && field.Length is >= 1 and <= 24 && field.Decimals >= 0 && field.Decimals <= Math.Min(field.Length, 9),
                "*BIN" or "*UBIN" => field.LengthPrefix == 0 && field.Length is 2 or 4 or 8 && field.Decimals == 0,
                _ => false };
            if (!valid) throw Invalid("Unsupported substitution field type or length.");
            size += field.LengthPrefix != 0 ? field.LengthPrefix : field.Type == "*DEC" ? (field.Length + 2) / 2 : field.Length;
        }
        if (size > 512) throw Invalid("Message data fields exceed 512 bytes.");
    }

    public static FormattedMessage Format(MessageDescription description, ProgramBuffer data, int outputCcsid, bool convertCharacterData = true)
    {
        Validate(description, legacyText: true);
        if (data.Length > 512 || !CodePage.IsSupported(outputCcsid)) throw Invalid("Invalid replacement data length or output CCSID.");
        var encoding = EncodingFor(outputCcsid); var source = data.ToArray(); var offset = 0; var values = new List<byte[]>();
        foreach (var field in description.Fields)
        {
            var length = field.Type == "*DEC" ? (field.Length + 2) / 2 : field.Length;
            if (field.LengthPrefix > 0)
            {
                if (source.Length - offset < field.LengthPrefix) { values.Add(Array.Empty<byte>()); offset = source.Length; continue; }
                length = field.LengthPrefix == 2 ? BinaryPrimitives.ReadInt16BigEndian(source.AsSpan(offset, 2)) : BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(offset, 4));
                offset += field.LengthPrefix;
                if (length < 0) throw Invalid("Negative varying message field length.");
            }
            // Native short replacement fields become empty; never read into another buffer.
            if (length > source.Length - offset) { values.Add(Array.Empty<byte>()); offset = source.Length; continue; }
            var bytes = source.AsSpan(offset, length).ToArray(); offset += length;
            try
            {
                if (field.Type is "*CHAR" or "*QTDCHAR" or "*CCHAR")
                {
                    if (field.Type == "*CCHAR" && convertCharacterData) bytes = encoding.GetBytes(new ProgramBuffer(bytes, data.Ccsid).ToText());
                    var end = bytes.Length; var blank = encoding.GetBytes(" ")[0]; while (end > 0 && bytes[end - 1] == blank) end--;
                    bytes = bytes.AsSpan(0, end).ToArray();
                    if (field.Type == "*QTDCHAR") bytes = encoding.GetBytes("'").Concat(bytes).Concat(encoding.GetBytes("'")).ToArray();
                }
                else if (field.Type == "*HEX") bytes = encoding.GetBytes("X'" + Convert.ToHexString(bytes) + "'");
                else
                {
                    var type = field.Type == "*DEC" ? "*DEC" : field.Type == "*BIN" ? "*INT" : "*UINT";
                    var buffer = new ProgramBuffer(bytes, data.Ccsid);
                    var cell = ClCallArgument.Bind(new("*RAW", bytes.Length, 0, buffer, buffer), type, field.Length, field.Decimals);
                    bytes = encoding.GetBytes(Convert.ToDecimal(cell.Value, CultureInfo.InvariantCulture).ToString("F" + field.Decimals, CultureInfo.InvariantCulture));
                }
            }
            catch (Exception error) when (error is ClRuntimeException or EncoderFallbackException)
            { throw Invalid("Invalid replacement field: " + error.Message); }
            values.Add(bytes);
        }
        ProgramBuffer Render(string template)
        {
            using var stream = new MemoryStream(); var start = 0;
            foreach (Match match in Regex.Matches(description.LegacyLiteral ? "" : template, @"&([0-9]+)"))
            {
                stream.Write(encoding.GetBytes(template[start..match.Index]));
                stream.Write(values[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1]);
                start = match.Index + match.Length;
                if (stream.Length > 32767) throw Invalid("Formatted message exceeds 32767 bytes.");
            }
            stream.Write(encoding.GetBytes(template[start..]));
            if (stream.Length > 32767) throw Invalid("Formatted message exceeds 32767 bytes.");
            return new(stream.ToArray(), outputCcsid);
        }
        try { return new(Render(description.Text), Render(description.SecondLevel), description.Severity,
            description.DefaultReply is null ? null : new(encoding.GetBytes(description.DefaultReply), outputCcsid)); }
        catch (EncoderFallbackException) { throw Invalid("Message is not representable in the requested CCSID."); }
    }

    private static Encoding EncodingFor(int ccsid)
    {
        if (!CodePage.IsSupported(ccsid)) throw Invalid("Unsupported message CCSID.");
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback; return encoding;
    }
    public static ProgramBuffer ConvertReplacementData(MessageDescription description, ProgramBuffer data, int outputCcsid)
    {
        Validate(description, legacyText: true);
        if (data.Length > 512) throw Invalid("Replacement data exceeds 512 bytes.");
        var encoding = EncodingFor(outputCcsid); var bytes = data.ToArray(); var offset = 0;
        using var stream = new MemoryStream();
        foreach (var field in description.Fields)
        {
            var start = offset; var length = field.Type == "*DEC" ? (field.Length + 2) / 2 : field.Length;
            if (field.LengthPrefix > 0)
            {
                if (bytes.Length - offset < field.LengthPrefix) break;
                length = field.LengthPrefix == 2 ? BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset, 2)) : BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
                if (length < 0) throw Invalid("Negative varying message field length.");
                offset += field.LengthPrefix;
            }
            if (length > bytes.Length - offset) { offset = start; break; }
            if (field.Type == "*CCHAR")
            {
                byte[] converted;
                try { converted = encoding.GetBytes(new ProgramBuffer(bytes.AsSpan(offset, length), data.Ccsid).ToText()); }
                catch (EncoderFallbackException) { throw Invalid("CCHAR data is not representable in the receiving CCSID."); }
                var prefix = new byte[field.LengthPrefix];
                if (prefix.Length == 2) BinaryPrimitives.WriteInt16BigEndian(prefix, checked((short)converted.Length));
                else BinaryPrimitives.WriteInt32BigEndian(prefix, converted.Length);
                stream.Write(prefix); stream.Write(converted);
            }
            else stream.Write(bytes.AsSpan(start, offset + length - start));
            offset += length;
        }
        stream.Write(bytes.AsSpan(offset));
        if (stream.Length > 32767) throw Invalid("Converted replacement data exceeds 32767 bytes.");
        return new(stream.ToArray(), description.Fields.Any(field => field.Type == "*CCHAR") ? outputCcsid : data.Ccsid);
    }
    private static CpfException Invalid(string message) => new("IPC0127", message);
}
