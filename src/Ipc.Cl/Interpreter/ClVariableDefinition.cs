using System.Globalization;
using System.Buffers.Binary;
using Ipc.Core.Work;
using Ipc.Cl.Definitions;
using System.Text;
using Ipc.Core.Text;

namespace Ipc.Cl.Interpreter;

internal sealed record ClVariableDefinition(string Type, int Length, int Decimals)
{
    internal static ClVariableDefinition? From(ClStatement declaration, int? ccsid = null)
    {
        if (declaration.DeclarationType is null) return null; // Existing untyped-variable extension.
        var type = declaration.DeclarationType;
        if (type is not ("*CHAR" or "*DEC" or "*INT" or "*UINT" or "*LGL")) throw new ClRuntimeException("Unsupported CL variable type.");
        var dimensions = (declaration.DeclarationLength ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int Dimension(int index, int fallback) => index >= dimensions.Length ? fallback : int.TryParse(dimensions[index], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : -1;
        var initial = Ipc.Cl.Parsing.CommandParser.Unquote(declaration.Value);
        var hex = declaration.Value?.Trim().StartsWith("X'", StringComparison.OrdinalIgnoreCase) == true && initial.EndsWith('\'');
        if (hex && type != "*CHAR") throw new ClRuntimeException("A hexadecimal initial value requires a CHAR declaration.");
        var initialLength = initial.Length;
        if (hex)
        {
            var literal = ClExpression.Compile(initial).Evaluate(_ => throw new ClRuntimeException("Expected a hexadecimal constant."), ccsid ?? 37);
            initialLength = ((ProgramBuffer)literal).Length;
        }
        else if (type == "*CHAR" && ccsid is { } codePage)
        {
            var encoding = (Encoding)CodePage.FromCcsid(codePage).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
            initialLength = encoding.GetByteCount(initial);
        }
        var fraction = initial.IndexOf('.') is var dot && dot >= 0 ? initial.Length - dot - 1 : 0;
        var inferredLength = type == "*CHAR" ? Math.Max(1, initialLength) : initial.TrimStart('+', '-').Replace(".", "", StringComparison.Ordinal).Length;
        var length = Dimension(0, type switch { "*INT" or "*UINT" => 4, "*LGL" => 1, "*CHAR" => declaration.Value is null ? 32 : inferredLength, _ => declaration.Value is null ? 15 : inferredLength });
        var decimals = Dimension(1, type == "*DEC" && dimensions.Length == 0 ? declaration.Value is null ? 5 : fraction : 0);
        if ((hex ? initialLength : initial.Length) > 5000) throw new ClRuntimeException("CL initial values cannot exceed 5000 characters (5000 bytes for hexadecimal constants).");
        var valid = type switch { "*CHAR" => length is >= 1 and <= 32767 && decimals == 0, "*DEC" => length is >= 1 and <= 15 && decimals >= 0 && decimals <= Math.Min(length, 9), "*INT" or "*UINT" => length is 2 or 4 && decimals == 0, "*LGL" => length == 1 && decimals == 0, _ => false };
        if (!valid || dimensions.Length > (type == "*DEC" ? 2 : 1)) throw new ClRuntimeException("Invalid CL variable length or decimal positions.");
        return new(type, length, decimals);
    }
    internal int StorageLength => Type == "*DEC" ? (Length + 2) / 2 : Length;
    internal ProgramBuffer Encode(string value, int ccsid)
    {
        var parameter = new ParameterDefinition { Keyword = "VALUE", Type = Type is "*INT" or "*UINT" ? Type + Length : Type, Length = Length, Decimals = Decimals };
        return new(CommandArgumentCodec.Encode(parameter, Read(value), ccsid), ccsid);
    }
    internal string Decode(ProgramBuffer buffer)
    {
        if (buffer.Length != StorageLength) throw new ClRuntimeException("CL parameter buffer length does not match its declaration.");
        var bytes = buffer.ToArray();
        if (Type == "*CHAR") return buffer.ToText();
        if (Type == "*LGL") return Assign(buffer.ToText(), buffer.Ccsid);
        decimal number;
        if (Type is "*INT" or "*UINT")
            number = Type == "*UINT" ? Length == 2 ? BinaryPrimitives.ReadUInt16BigEndian(bytes) : Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes)
                : Length == 2 ? (decimal)BinaryPrimitives.ReadInt16BigEndian(bytes) : Length == 4 ? BinaryPrimitives.ReadInt32BigEndian(bytes) : BinaryPrimitives.ReadInt64BigEndian(bytes);
        else
        {
            var sign = bytes[^1] & 15;
            if (sign is not (10 or 11 or 12 or 13 or 14 or 15)) throw new ClRuntimeException("Invalid packed CL parameter sign.");
            var digits = new StringBuilder();
            for (var nibble = 0; nibble < bytes.Length * 2 - 1; nibble++)
            {
                var digit = nibble % 2 == 0 ? bytes[nibble / 2] >> 4 : bytes[nibble / 2] & 15;
                if (digit > 9 || nibble == 0 && Length % 2 == 0 && digit != 0) throw new ClRuntimeException("Invalid packed CL parameter digit or padding.");
                digits.Append((char)('0' + digit));
            }
            if (Decimals > 0) digits.Insert(digits.Length - Decimals, '.');
            number = ClExpression.ExactDecimal(digits.ToString()) * (sign is 11 or 13 ? -1 : 1);
        }
        return Assign(number, buffer.Ccsid);
    }
    internal object Read(string value) => Type switch { "*CHAR" => value, "*LGL" => ClExpression.Logical(value), _ => Numeric(value) };
    private static decimal Numeric(string text)
    {
        text = text.Trim().Replace(',', '.');
        var unsigned = text.TrimStart('+', '-');
        if (text.Length == 0 || unsigned.Length == 0 || text.Length - unsigned.Length > 1) throw new ClRuntimeException("Invalid numeric assignment.");
        return ClExpression.ExactDecimal(unsigned) * (text[0] == '-' ? -1 : 1);
    }
    internal string Default => Type == "*CHAR" ? new string(' ', Length) : "0";
    internal string Assign(object value, int ccsid)
    {
        var text = ClExpression.Text(value);
        if (Type == "*CHAR")
        {
            var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
            if (value is decimal numeric)
            {
                var magnitude = ClExpression.Text(Math.Abs(numeric));
                var sign = numeric < 0 ? "-" : "";
                if (magnitude.Length + sign.Length > Length) throw new ClRuntimeException("Numeric value does not fit the character variable.");
                text = sign + magnitude.PadLeft(Length - sign.Length, '0');
            }
            var bytes = encoding.GetBytes(text);
            if (bytes.Length > Length)
            {
                encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
                try { return encoding.GetString(bytes, 0, Length); }
                catch (DecoderFallbackException) { throw new ClRuntimeException("Character truncation splits a multibyte character."); }
            }
            return text + new string(' ', Length - bytes.Length);
        }
        if (Type == "*LGL") return ClExpression.Logical(value) ? "1" : "0";
        if (value is bool) throw new ClRuntimeException("A logical value cannot be assigned to a numeric variable.");
        var number = Numeric(text);
        if (Type is "*INT" or "*UINT")
        {
            var minimum = Type == "*UINT" ? 0m : Length == 2 ? short.MinValue : Length == 4 ? int.MinValue : long.MinValue;
            decimal maximum = Type == "*UINT" ? Length == 2 ? (decimal)ushort.MaxValue : Length == 4 ? uint.MaxValue : ulong.MaxValue : Length == 2 ? short.MaxValue : Length == 4 ? int.MaxValue : long.MaxValue;
            if (decimal.Truncate(number) != number || number < minimum || number > maximum) throw new ClRuntimeException("Integer assignment exceeds its declared range.", "MCH1210");
            return number.ToString("0", CultureInfo.InvariantCulture);
        }
        if (value is string) number = decimal.Round(number, Decimals, MidpointRounding.ToZero);
        if (decimal.Round(number, Decimals) != number || Math.Abs(decimal.Truncate(number)).ToString("0", CultureInfo.InvariantCulture).TrimStart('0').Length > Length - Decimals)
            throw new ClRuntimeException("Decimal assignment exceeds its declared precision or scale.", "MCH1210");
        return number.ToString("F" + Decimals, CultureInfo.InvariantCulture);
    }
}
