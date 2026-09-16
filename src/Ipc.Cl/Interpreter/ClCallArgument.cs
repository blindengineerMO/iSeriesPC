using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Cl.Parsing;
using Ipc.Core.Text;
using Ipc.Core.Work;

namespace Ipc.Cl.Interpreter;

/// <summary>CALL constants and expression temporaries, independent of the receiver.</summary>
public sealed class ClCallArgument
{
    public string? ReferenceName { get; private init; }
    private ClExpression Expression { get; }
    private ClCallArgument(ClExpression expression) { Expression = expression; }
    internal IEnumerable<string> CharacterStorageVariables => Expression.CharacterStorageVariables;
    private string? Type { get; init; }
    private int Length { get; init; }
    private int Decimals { get; init; }

    public static ClCallArgument Compile(string source)
    {
        if (source.Length > 32768) throw new ClRuntimeException("CALL argument exceeds 32768 characters.");
        source = source.Trim(); string? type = null; var length = 0; var decimals = 0;
        if (source.StartsWith('(') && source.EndsWith(')'))
        {
            var parts = CommandParser.Tokenize(source[1..^1]);
            if (parts.Count == 2 && (parts[1].Equals("*DFT", StringComparison.OrdinalIgnoreCase) || parts[1].StartsWith("(*", StringComparison.Ordinal)))
            {
                source = parts[0];
                if (!parts[1].Equals("*DFT", StringComparison.OrdinalIgnoreCase))
                {
                    var attributes = CommandParser.Tokenize(parts[1][1..^1]);
                    type = attributes[0].ToUpperInvariant();
                    if (attributes.Count is < 2 or > 3 || !int.TryParse(attributes[1], NumberStyles.None, CultureInfo.InvariantCulture, out length) ||
                        attributes.Count == 3 && !int.TryParse(attributes[2], NumberStyles.None, CultureInfo.InvariantCulture, out decimals))
                        throw new ClRuntimeException("Invalid CALL type and length.");
                    var valid = type switch {
                        "*CHAR" => length is >= 1 and <= 32767, "*DEC" => length is >= 1 and <= 24 && decimals <= Math.Min(length, 9),
                        "*LGL" => length == 1, "*INT" or "*UINT" => length is 2 or 4 or 8, "*FLT" => length is 4 or 8, _ => false };
                    if (!valid || type != "*DEC" && attributes.Count == 3) throw new ClRuntimeException("Unsupported CALL type or length.");
                }
            }
        }
        if (source.Equals("*N", StringComparison.OrdinalIgnoreCase)) throw new ClRuntimeException("CALL does not support null parameters.");
        return new(ClExpression.Compile(source)) { Type = type, Length = length, Decimals = decimals,
            ReferenceName = Regex.IsMatch(source, @"\A&[A-Za-z][A-Za-z0-9_]{0,20}\z") ? source.ToUpperInvariant() : null };
    }

    public ProgramConstant Evaluate(Func<string, object> read, int ccsid = 37)
    {
        var value = Expression.Evaluate(read, ccsid);
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        var type = Type ?? (value is decimal ? "*DEC" : Expression.IsHexadecimalConstant ? "*RAW" : "*CHAR");
        var length = Type is not null ? Length : type == "*DEC" ? 15 : value is ProgramBuffer raw ? Math.Max(type == "*RAW" ? 0 : 32, raw.Length) : Math.Max(32, encoding.GetByteCount(ClExpression.Text(value)));
        var decimals = Type is not null ? Decimals : type == "*DEC" ? 5 : 0;
        if (length is < 1 or > 32767) throw new ClRuntimeException("Invalid CALL temporary size.");
        byte[] bytes;
        try
        {
            if (type is "*CHAR" or "*RAW")
            {
                var data = value is ProgramBuffer rawValue ? rawValue.ToArray() : encoding.GetBytes(ClExpression.Text(value));
                bytes = Enumerable.Repeat(encoding.GetBytes(" ")[0], length).ToArray(); data.AsSpan(0, Math.Min(length, data.Length)).CopyTo(bytes);
                if (data.Length > length) value = new ProgramBuffer(bytes, ccsid);
            }
            else if (type == "*LGL") { value = ClExpression.Logical(value); bytes = encoding.GetBytes((bool)value ? "1" : "0"); }
            else
            {
                var number = ClExpression.Number(value); bytes = new byte[length];
                if (type == "*DEC")
                {
                    var definition = new ClVariableDefinition(type, length, decimals);
                    number = decimal.Round(number, decimals, MidpointRounding.ToZero);
                    bytes = definition.Encode(definition.Assign(number, ccsid), ccsid).ToArray(); value = number;
                }
                else if (type == "*FLT")
                {
                    if (length == 4) { var single = (float)number; if (!float.IsFinite(single)) throw new OverflowException(); BinaryPrimitives.WriteSingleBigEndian(bytes, single); value = single; }
                    else { var floating = (double)number; BinaryPrimitives.WriteDoubleBigEndian(bytes, floating); value = floating; }
                }
                else
                {
                    number = decimal.Truncate(number); value = number;
                    if (type == "*INT")
                    {
                        if (length == 2) BinaryPrimitives.WriteInt16BigEndian(bytes, checked((short)number));
                        else if (length == 4) BinaryPrimitives.WriteInt32BigEndian(bytes, checked((int)number));
                        else BinaryPrimitives.WriteInt64BigEndian(bytes, checked((long)number));
                    }
                    else
                    {
                        if (length == 2) BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)number));
                        else if (length == 4) BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)number));
                        else BinaryPrimitives.WriteUInt64BigEndian(bytes, checked((ulong)number));
                    }
                }
            }
        }
        catch (OverflowException) { throw new ClRuntimeException("CALL temporary overflow.", "MCH1210"); }
        return new(type, length, decimals, new ProgramBuffer(bytes, ccsid), value);
    }

    public static ProgramArgument Bind(ProgramConstant value, string type, int length, int decimals = 0)
    {
        var valid = type switch {
            "*CHAR" => length is >= 1 and <= 32767 && decimals == 0,
            "*DEC" => length is >= 1 and <= 29 && decimals >= 0 && decimals <= Math.Min(length, 28),
            "*INT" or "*UINT" => length is 2 or 4 or 8 && decimals == 0,
            "*LGL" => length == 1 && decimals == 0, "*FLT" => length is 4 or 8 && decimals == 0, _ => false };
        if (!valid) throw new ClRuntimeException("Unsupported CALL receiver layout.");
        if (type != "*FLT") return Cell(value, new(type, length, decimals)).Borrow();
        if (value.Type is not ("*FLT" or "*RAW") || value.Buffer.Length != length)
            throw new ClRuntimeException("Floating CALL constant does not match the receiving layout.");
        double Read(ProgramBuffer buffer)
        {
            if (buffer.Length != length) throw new ClRuntimeException("Floating CALL buffer length mismatch.");
            return length == 4 ? BinaryPrimitives.ReadSingleBigEndian(buffer.ToArray()) : BinaryPrimitives.ReadDoubleBigEndian(buffer.ToArray());
        }
        object Normalize(object? updated)
        {
            var number = updated is ProgramBuffer buffer ? Read(buffer) : Convert.ToDouble(updated, CultureInfo.InvariantCulture);
            if (!double.IsFinite(number) || length == 4 && !float.IsFinite((float)number)) throw new ClRuntimeException("Floating CALL argument overflow.", "MCH1210");
            return length == 4 ? (double)(float)number : number;
        }
        object current = Normalize(value.Buffer);
        ProgramBuffer Bytes()
        {
            var bytes = new byte[length];
            if (length == 4) BinaryPrimitives.WriteSingleBigEndian(bytes, (float)(double)current); else BinaryPrimitives.WriteDoubleBigEndian(bytes, (double)current);
            return new(bytes, value.Buffer.Ccsid);
        }
        return new(() => current, updated => current = updated!, Normalize, "*FLT", length, 0, Bytes);
    }

    internal static ClVariableCell Cell(ProgramConstant value, ClVariableDefinition? receiver)
    {
        if (receiver is null)
        {
            if (value.Value is not ProgramBuffer) return new(ClExpression.Text(value.Value), null, value.Buffer.Ccsid);
            var rawCell = new ClVariableCell("", new("*CHAR", value.Buffer.Length, 0), value.Buffer.Ccsid);
            rawCell.AssignBuffer(value.Buffer); return rawCell;
        }
        if (value.Type != "*RAW" && !(receiver.Type == value.Type && (receiver.Type == "*CHAR" || receiver.Length == value.Length && receiver.Decimals == value.Decimals)) &&
            !(receiver.Type == "*LGL" && value.Type == "*CHAR"))
            throw new ClRuntimeException("CALL constant type and length do not match the receiving declaration.");
        var width = receiver.StorageLength;
        if (width > value.Buffer.Length || receiver.Type is not ("*CHAR" or "*LGL") && width != value.Buffer.Length)
            throw new ClRuntimeException("CALL constant is smaller than or incompatible with the receiving declaration.");
        var buffer = new ProgramBuffer(value.Buffer.ToArray().AsSpan(0, width), value.Buffer.Ccsid);
        var cell = new ClVariableCell("", receiver, value.Buffer.Ccsid);
        if (receiver.Type == "*CHAR") cell.AssignBuffer(buffer); else cell.Value = receiver.Decode(buffer);
        return cell;
    }

    internal static ProgramArgument Temporary(ProgramConstant value)
    {
        object? current = value.Value;
        object? Normalize(object? updated)
        {
            if (updated is ProgramBuffer bytes && (bytes.Length != value.Buffer.Length || bytes.Ccsid != value.Buffer.Ccsid))
                throw new ClRuntimeException("CALL temporary response buffer has a different size or CCSID.");
            return updated;
        }
        return new(() => current, updated => current = updated, Normalize,
            value.Type, value.Length, value.Decimals, () => value.Buffer, value);
    }
}
