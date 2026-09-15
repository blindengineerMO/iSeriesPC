using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Cl.Parsing;
using Ipc.Core.Text;
using Ipc.Db.Definitions;
using Ipc.Db.Records;

namespace Ipc.Db.Dds;

internal static class DdsFieldDefaults
{
    internal static FieldDefault Compile(FieldSpec field, string specification, Func<string, DdsCompileException> error)
    {
        var tokens = CommandParser.Tokenize(specification);
        if (tokens.Count != 1) throw error("DFT requires one constant.");
        var raw = tokens[0];
        if (raw.Equals("*NULL", StringComparison.OrdinalIgnoreCase))
            return field.NullCapable ? new(null) : throw error("DFT(*NULL) requires ALWNULL.");
        var quoted = raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'';
        var value = quoted ? CommandParser.Unquote(raw) : raw;
        object typed = value;
        try
        {
            if (field.Type == FieldType.Alpha)
            {
                if (raw.StartsWith("X'", StringComparison.OrdinalIgnoreCase) && raw.EndsWith('\''))
                {
                    var hex = raw[2..^1];
                    if (hex.Length != field.Length * 2) throw new FormatException();
                    var encoding = (Encoding)CodePage.FromCcsid(field.Ccsid).Clone(); encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
                    value = encoding.GetString(Convert.FromHexString(hex)); typed = value;
                }
                else if (!quoted || value.Length == 0 && !field.VariableLength) throw new FormatException();
            }
            else if (field.IsNumeric)
            {
                if (quoted || !Regex.IsMatch(raw, @"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)\z", RegexOptions.CultureInvariant)) throw new FormatException();
            }
            else
            {
                if (!quoted) throw new FormatException();
                switch (field.Type)
                {
                    case FieldType.Date:
                        typed = DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
                    case FieldType.Time:
                        var time = TimeOnly.ParseExact(value, "HH.mm.ss", CultureInfo.InvariantCulture);
                        typed = time; value = time.ToString("HH:mm:ss", CultureInfo.InvariantCulture); break;
                    case FieldType.Timestamp:
                        var stamp = DateTime.ParseExact(value, "yyyy-MM-dd-HH.mm.ss.ffffff", CultureInfo.InvariantCulture);
                        typed = stamp; value = stamp.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture); break;
                    default: throw new FormatException();
                }
            }
            // Compile-time validation uses the actual external width, precision and
            // CCSID, so an invalid default cannot become a delayed write failure.
            var format = new RecordFormat { Name = "DEFAULT", Fields = new() { field } }; format.AssignPositions();
            _ = new RecordCodec(field.Ccsid).Encode(format, new Dictionary<string, object?> { [field.Name] = typed });
            return new(value);
        }
        catch (Exception failure) when (failure is ArgumentException or FormatException or OverflowException)
        { throw error("DFT constant does not fit field " + field.Name + " or its declared format."); }
    }
}
