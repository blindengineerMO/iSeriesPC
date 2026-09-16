using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Ipc.Core.Objects;
using Ipc.Core.Text;

namespace Ipc.Cl.Definitions;

/// <summary>IBM command CPP layouts for simple lists and qualified names. Numeric scalars
/// remain semantic values for interpreted CL/RPG; their packed/binary layouts are exposed here.</summary>
public static class CommandArgumentCodec
{
    public static byte[] Encode(ParameterDefinition parameter, object? value, int ccsid = CodePage.DefaultSystem)
    {
        if (parameter.Maximum <= 1) return Scalar(parameter, value, ccsid);
        if (value is not IReadOnlyList<object?> values || values.Count > parameter.Maximum) throw CommandDefinition.Invalid("Invalid list argument.");
        var width = Width(parameter); var bytes = new byte[checked(2 + width * values.Count)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)values.Count));
        for (var i = 0; i < values.Count; i++) Scalar(parameter, values[i], ccsid).CopyTo(bytes, 2 + i * width);
        return bytes;
    }
    public static IReadOnlyList<object?> ProgramArguments(CommandDefinition definition, BoundCommand bound, int ccsid)
    {
        var result = new List<object?>();
        for (var i = 0; i < definition.Parameters.Count; i++)
        {
            var parameter = definition.Parameters[i]; var value = bound.Arguments[i];
            if (parameter.Maximum > 1 || parameter.DefaultLibrary is not null)
            {
                result.Add(new Ipc.Core.Work.ProgramBuffer(Encode(parameter, value, ccsid), ccsid));
            }
            else { _ = Encode(parameter, value, ccsid); result.Add(value); }
        }
        return result;
    }
    public static int Width(ParameterDefinition p) => p.DefaultLibrary is not null ? 20 : p.Type switch {
        "*INT2" or "*UINT2" => 2, "*INT4" or "*UINT4" => 4, "*INT8" or "*UINT8" => 8, "*DEC" => (p.Length + 2) / 2,
        "*LGL" => 1, "*DATE" => 7, "*TIME" => 6, _ => p.Length };
    private static byte[] Scalar(ParameterDefinition p, object? value, int ccsid)
    {
        var width = Width(p); var bytes = new byte[width];
        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (p.DefaultLibrary is not null)
        {
            if (text.Length == 0) return Text("", 20, ccsid);
            var name = QualifiedName.Parse(text, p.DefaultLibrary ?? "*LIBL");
            Text(name.Name.Value, 10, ccsid).CopyTo(bytes, 0); Text(name.Library, 10, ccsid).CopyTo(bytes, 10); return bytes;
        }
        if (p.Type is "*CHAR" or "*NAME" or "*GENERIC" or "*PNAME") return Text(text, width, ccsid);
        if (p.Type == "*LGL") return Text(value is true ? "1" : "0", 1, ccsid);
        if (p.Type == "*DATE" || p.Type == "*TIME") return Text(text.Replace(":", "", StringComparison.Ordinal), width, ccsid);
        var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        switch (p.Type)
        {
            case "*INT2": BinaryPrimitives.WriteInt16BigEndian(bytes, checked((short)number)); break;
            case "*UINT2": BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)number)); break;
            case "*INT4": BinaryPrimitives.WriteInt32BigEndian(bytes, checked((int)number)); break;
            case "*UINT4": BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)number)); break;
            case "*INT8": BinaryPrimitives.WriteInt64BigEndian(bytes, checked((long)number)); break;
            case "*UINT8": BinaryPrimitives.WriteUInt64BigEndian(bytes, checked((ulong)number)); break;
            case "*DEC":
                var digits = Math.Abs(number).ToString("F" + p.Decimals, CultureInfo.InvariantCulture).Replace(".", "", StringComparison.Ordinal).PadLeft(width * 2 - 1, '0') + (number < 0 ? "D" : "C");
                if (digits.Length != width * 2) throw CommandDefinition.Invalid("Packed CPP argument overflow.");
                return Convert.FromHexString(digits);
            default: throw CommandDefinition.Invalid("Unsupported CPP byte layout.");
        }
        return bytes;
    }
    private static byte[] Text(string text, int length, int ccsid)
    {
        try
        {
            var encoding = StrictEncoding(ccsid); var encoded = encoding.GetBytes(text); var blank = encoding.GetBytes(" ");
            if (encoded.Length > length || blank.Length != 1) throw CommandDefinition.Invalid("CPP character argument exceeds its byte length.");
            var result = Enumerable.Repeat(blank[0], length).ToArray(); encoded.CopyTo(result, 0); return result;
        }
        catch (EncoderFallbackException) { throw CommandDefinition.Invalid("CPP character is not representable in the job CCSID."); }
    }
    private static Encoding StrictEncoding(int ccsid)
    {
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback; encoding.DecoderFallback = DecoderFallback.ExceptionFallback; return encoding;
    }
}
