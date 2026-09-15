using System.Globalization;
using System.Text;
using Ipc.Cl.Parsing;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Db.Definitions;

namespace Ipc.Db.Dds;

/// <summary>Native-column physical DDS; unimplemented keywords fail with a source location.</summary>
internal static class NativePhysicalDdsCompiler
{
    internal static FileDefinition Compile(string name, string source, int ccsid, string sourceName)
    {
        DdsCompileException Error(int line, string message) => new($"IPC0006: {sourceName}:{line}:1: {message}");
        if (!ObjectName.IsValid(name) || !CodePage.IsSupported(ccsid) || Encoding.UTF8.GetByteCount(source) > 1048576) throw Error(1, "Invalid file identity, CCSID or source size.");
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > 10000) throw Error(1, "DDS source exceeds 10000 lines.");
        RecordFormat? record = null; var keysStarted = false; var sequence = 0; var unique = false; var excludeNullKeys = false;
        foreach (var statement in DdsStatements.Read(lines, Error))
        {
            var raw = statement.Text; var line = statement.Line;
            if (raw[6..16].Any(c => c != ' ') || raw[17] != ' ') throw Error(line, "Unsupported conditioning or reserved columns.");
            var level = raw[16]; var fieldName = raw[18..28].Trim().ToUpperInvariant();
            CommandCall keywords;
            try { keywords = CommandParser.Parse("DDS " + raw[44..]); }
            catch (ClParseException error) { throw Error(line, error.Message); }
            void Check(string names, string flags = "")
            {
                if (keywords.Keywords.Keys.Any(k => !names.Split(' ').Contains(k.ToUpperInvariant())) || keywords.Positional.Any(k => !flags.Split(' ').Contains(k.ToUpperInvariant())))
                    throw Error(line, "Unsupported physical DDS keyword.");
            }
            string? Option(string keyword) => keywords.GetOption(keyword) is { } value ? CommandParser.Unquote(value) : null;
            if (record is null && level == ' ' && fieldName.Length == 0)
            {
                Check("UNIQUE", "UNIQUE");
                if (raw[28..44].Any(c => c != ' ') || unique || keywords.Positional.Count + keywords.Keywords.Count != 1) throw Error(line, "Expected one UNIQUE file keyword.");
                var nulls = Option("UNIQUE")?.ToUpperInvariant() ?? "*INCNULL";
                if (nulls is not ("*INCNULL" or "*EXCNULL")) throw Error(line, "UNIQUE requires *INCNULL or *EXCNULL.");
                unique = true; excludeNullKeys = nulls == "*EXCNULL"; continue;
            }
            if (level == 'R')
            {
                Check("TEXT");
                if (record is not null || !ObjectName.IsValid(fieldName) || raw[28..44].Any(c => c != ' ')) throw Error(line, "Physical DDS requires one named record format.");
                record = new() { Name = fieldName, Text = Option("TEXT") }; continue;
            }
            if (record is null) throw Error(line, "A physical record format must precede fields.");
            if (level == 'K')
            {
                Check("", "DESCEND"); keysStarted = true;
                var key = record.Find(fieldName) ?? throw Error(line, "Key field has not been defined.");
                if (raw[28..44].Any(c => c != ' ') || key.Sequence != 0 || keywords.Positional.Count > 1) throw Error(line, "Invalid or duplicate key specification.");
                key.Sequence = ++sequence; key.Descending = keywords.Positional.Count == 1; continue;
            }
            if (level != ' ' || !ObjectName.IsValid(fieldName) || keysStarted || record.Find(fieldName) is not null || raw[28] != ' ' || raw[37..44].Any(c => c != ' '))
                throw Error(line, "Invalid field specification, reference, or field order.");
            Check("TEXT CCSID FLTPCN DATFMT TIMFMT DFT", "ALWNULL VARLEN");
            if (keywords.Positional.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keywords.Positional.Count) throw Error(line, "Duplicate field flag.");
            var numericPositions = raw[35..37].Trim();
            var type = raw[34] == ' ' ? (numericPositions.Length == 0 ? 'A' : 'P') : raw[34];
            var size = raw[29..34].Trim();
            if (size.Length != 0 && (raw[33] == ' ' || !int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out _))) throw Error(line, "Field length must be a right-aligned unsigned integer.");
            var length = size.Length == 0 ? 0 : int.Parse(size, CultureInfo.InvariantCulture);
            if (numericPositions.Length != 0 && (raw[36] == ' ' || !int.TryParse(numericPositions, NumberStyles.None, CultureInfo.InvariantCulture, out _))) throw Error(line, "Invalid decimal positions.");
            var decimals = numericPositions.Length == 0 ? 0 : int.Parse(numericPositions, CultureInfo.InvariantCulture);
            var kind = type switch { 'A' => FieldType.Alpha, 'P' => FieldType.Packed, 'S' => FieldType.Zoned, 'B' => FieldType.Binary, 'F' => FieldType.Float,
                'L' => FieldType.Date, 'T' => FieldType.Time, 'Z' => FieldType.Timestamp, _ => throw Error(line, "Unsupported native DDS data type.") };
            var nullable = keywords.Positional.Any(k => k.Equals("ALWNULL", StringComparison.OrdinalIgnoreCase));
            var varying = keywords.Positional.Any(k => k.Equals("VARLEN", StringComparison.OrdinalIgnoreCase));
            if (varying && kind != FieldType.Alpha) throw Error(line, "VARLEN requires a character field.");
            if (Option("CCSID") is not null && kind != FieldType.Alpha || Option("FLTPCN") is not null && kind != FieldType.Float || Option("DATFMT") is not null && kind != FieldType.Date || Option("TIMFMT") is not null && kind != FieldType.Time)
                throw Error(line, "Field keyword does not match its data type.");
            var fieldCcsid = ccsid;
            if (Option("CCSID") is { } code && (!int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out fieldCcsid) || !CodePage.IsSupported(fieldCcsid))) throw Error(line, "Unsupported field CCSID.");
            var digits = length;
            switch (kind)
            {
                case FieldType.Alpha:
                    if (length < 1 || length > (varying ? nullable ? 32739 : 32740 : nullable ? 32765 : 32766) || numericPositions.Length != 0) throw Error(line, "Invalid character length or decimal positions.");
                    break;
                case FieldType.Packed: case FieldType.Zoned:
                    if (length is < 1 or > 29 || decimals > Math.Min(length, 28)) throw Error(line, "Current decimal support is 1–29 digits and scale up to 28.");
                    break;
                case FieldType.Binary:
                    if (length is < 1 or > 18 || decimals != 0) throw Error(line, "Current binary support is 1–18 digits with zero decimal positions.");
                    length = length <= 4 ? 2 : length <= 9 ? 4 : 8; break;
                case FieldType.Float:
                    var precision = Option("FLTPCN")?.ToUpperInvariant() ?? "*SINGLE";
                    if (precision is not ("*SINGLE" or "*DOUBLE") || length < 1 || length > (precision == "*SINGLE" ? 9 : 17) || decimals > length) throw Error(line, "Invalid floating-point presentation length or precision.");
                    length = precision == "*SINGLE" ? 4 : 8; break;
                default:
                    if (size.Length != 0 || numericPositions.Length != 0 || Option("DATFMT") is { } date && date.ToUpperInvariant() != "*ISO" || Option("TIMFMT") is { } time && time.ToUpperInvariant() != "*ISO") throw Error(line, "Datetime fields currently require implicit length and ISO format.");
                    length = kind == FieldType.Date ? 10 : kind == FieldType.Time ? 8 : 26; break;
            }
            var field = new FieldSpec { Name = fieldName, Type = kind, Length = length, Decimals = decimals, Ccsid = fieldCcsid,
                NullCapable = nullable, VariableLength = varying, Text = Option("TEXT"), DeclaredDigits = kind is FieldType.Binary or FieldType.Float ? digits : 0,
                CurrentDatetimeDefault = kind is FieldType.Date or FieldType.Time or FieldType.Timestamp && keywords.GetOption("DFT") is null };
            if (keywords.GetOption("DFT") is { } defaultValue) field.DefaultValue = DdsFieldDefaults.Compile(field, defaultValue, message => Error(line, message));
            record.Fields.Add(field);
            if (record.Fields.Count > 1024) throw Error(line, "Physical format exceeds 1024 fields.");
        }
        if (record is null || record.Fields.Count == 0) throw Error(1, "Physical DDS requires a record and fields.");
        if (unique && sequence == 0) throw Error(1, "UNIQUE requires key fields.");
        record.AssignPositions();
        var storageLength = record.RecordLength + (record.Fields.Any(f => f.VariableLength) ? 24 : 0) + (record.Fields.Any(f => f.NullCapable) ? (record.Fields.Count + 7) / 8 : 0);
        if (storageLength > 32766) throw Error(1, "Native physical format exceeds the 32766-byte storage limit.");
        return new() { Name = name, Attribute = FileAttribute.Physical, Formats = new() { record }, Ccsid = ccsid, Text = record.Text,
            Unique = unique, ExcludeNullKeys = excludeNullKeys };
    }
}
