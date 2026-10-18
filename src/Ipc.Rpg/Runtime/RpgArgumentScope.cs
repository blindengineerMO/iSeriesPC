using System.Buffers.Binary;
using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Db.Definitions;
using Ipc.Db.Records;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

/// <summary>A synchronous view of scalar interpreter storage, invalidated at the call boundary.</summary>
internal sealed class RpgArgumentScope : IDisposable
{
    private bool _active = true;
    internal IReadOnlyList<object?> Arguments { get; }

    internal RpgArgumentScope(RpgRuntimeContext context, IReadOnlyList<RpgExpr> expressions, IReadOnlyList<object?> values, int ccsid,
        RpgPrototype? prototype = null, Func<int, RpgRuntimeContext.NativeView?>? capturedNative = null)
    {
        if (expressions.Count > 256 || values.Count != expressions.Count) throw Bad("Invalid RPG API argument count; at most 256 arguments are supported.");
        RpgField[]? formals = null;
        if (prototype is not null)
        {
            if (context.Program.Prototypes.Count(candidate => candidate.Name.Equals(prototype.Name, StringComparison.OrdinalIgnoreCase)) != 1)
                throw Bad("Ambiguous RPG API prototype binding.");
            if (prototype.Parameters.Count != expressions.Count || prototype.Parameters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != prototype.Parameters.Count)
                throw Bad("RPG API prototype parameter count or declarations do not match the call.");
            formals = prototype.Parameters.Select(name => {
                var matches = context.Program.Fields.Where(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                return matches.Length == 1 ? matches[0] : throw Bad("Missing or ambiguous RPG API prototype field.");
            }).ToArray();
            foreach (var formal in formals)
            {
                ValidateScalar(formal);
                if (formal.Source == RpgFieldSource.Constant) throw Bad("A prototype formal cannot be a named constant.");
                var keywords = prototype.ParameterKeywords.GetValueOrDefault(formal.Name, "").Trim();
                if (keywords.Length > 0 && !keywords.Equals("PARM", StringComparison.OrdinalIgnoreCase))
                    throw Bad("RPG API prototype parameter options are unsupported; only fixed reference parameters are accepted.");
            }
        }
        var byField = new Dictionary<string, ProgramArgument>(StringComparer.OrdinalIgnoreCase);
        var byNative = new Dictionary<string, ProgramArgument>(StringComparer.Ordinal);
        var byReference = new Dictionary<ProgramArgument, ProgramArgument>(ReferenceEqualityComparer.Instance);
        var arguments = new List<object?>(); long bytes = 0;
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone();
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback;

        for (var index = 0; index < expressions.Count; index++)
        {
            var expression = expressions[index];
            var formal = formals?[index];
            var native = capturedNative?.Invoke(index);
            if (native is null && capturedNative is null) context.TryNativeReference(expression, out native);
            if (native is not null)
            {
                if (context.NativeCcsid != ccsid) throw Bad("Native structure CCSID differs from the current call; explicit conversion is required.");
                if (native.IsArray && native.Stride != native.UnitWidth) throw Bad("A noncontiguous array cannot be borrowed as one native buffer; pass an indexed element.");
                var element = native.Element;
                var raw = element is null || native.IsArray || element.OpaqueBytes;
                var kind = raw ? RpgFieldKind.Character : element!.Kind;
                var precision = kind is RpgFieldKind.Packed or RpgFieldKind.Zoned ? element!.Length : native.Width;
                if (formal is not null && (formal.Kind != kind || formal.OpaqueBytes != raw ||
                    formal.Length != (kind == RpgFieldKind.Integer ? element!.Length : precision) || formal.Decimals != (element?.Decimals ?? 0)))
                    throw Bad("Native actual field differs from its scalar prototype.");
                bytes += native.Width;
                if (bytes > 1048576) throw Bad("RPG API arguments exceed 1 MiB.");
                if (!byNative.TryGetValue(native.Identity, out var reference))
                {
                    object? NormalizeNative(object? value)
                    {
                        CheckActive();
                        try { return new ProgramBuffer(context.NormalizeNative(native, value, strict: true), ccsid); }
                        catch (RpgRuntimeException error) { throw Bad(error.Message); }
                    }
                    var type = kind switch { RpgFieldKind.Character => "*CHAR", RpgFieldKind.Packed => "*DEC", RpgFieldKind.Zoned => "*ZONED",
                        RpgFieldKind.Integer => "*INT", RpgFieldKind.Float => "*FLT", RpgFieldKind.Indicator => "*LGL",
                        RpgFieldKind.Date => "*DATE", RpgFieldKind.Time => "*TIME", RpgFieldKind.Timestamp => "*TIMESTAMP", _ => throw Bad("Unsupported native argument type.") };
                    reference = new(() => { CheckActive(); return raw ? context.NativeBuffer(native) : context.ReadNative(native); },
                        value => { CheckActive(); RpgRuntimeContext.WriteNativeBytes(native, ((ProgramBuffer)value!).ToArray()); }, NormalizeNative,
                        type, precision, element?.Decimals ?? 0, () => { CheckActive(); return context.NativeBuffer(native); }, constant: null, storage: context.NativeStorageRange(native));
                    byNative.Add(native.Identity, reference);
                }
                arguments.Add(reference); continue;
            }
            if (expression is not RpgFieldRef fieldRef)
            {
                // Expressions are private values, never writable receivers.
                var value = values[index];
                arguments.Add(formal is null ? value : Constant(formal, value));
                bytes += formal is not null ? StorageLength(formal) : value is ProgramBuffer raw ? raw.Length : value is string text ? Encode(text).Length : 16;
            }
            else
            {
                var field = context.Program.FindField(fieldRef.Name);
                if (formal is not null && context.Program.Fields.Count(candidate => candidate.Name.Equals(fieldRef.Name, StringComparison.OrdinalIgnoreCase)) != 1)
                    throw Bad("Ambiguous RPG API actual field declaration.");
                if (field is null || context.Program.FindDsElement(fieldRef.Name) is not null)
                    throw Bad("RPG API arguments require scalar fields; arrays and data structures are not supported.");
                ValidateScalar(field);
                bytes += StorageLength(formal ?? field);
                if (bytes > 1048576) throw Bad("RPG API arguments exceed 1 MiB.");
                if (field.Source == RpgFieldSource.Constant)
                {
                    var value = context.ReadValue(field.Name);
                    if (formal is null && value is string text) bytes += Math.Max(0, Encode(text).Length - field.Length);
                    arguments.Add(formal is null ? value : Constant(formal, value));
                }
                else
                {
                    if (formal is not null && (field.Length != formal.Length || field.Decimals != formal.Decimals ||
                        (field.Kind == RpgFieldKind.Character) != (formal.Kind == RpgFieldKind.Character) ||
                        (field.Kind == RpgFieldKind.Integer) != (formal.Kind == RpgFieldKind.Integer) || field.OpaqueBytes != formal.OpaqueBytes))
                        throw Bad("RPG API actual field type, length or scale differs from its prototype.");
                    var original = context.Reference(field.Name);
                    if (field.Kind == RpgFieldKind.Integer)
                    {
                        if (!byField.TryGetValue(field.Name, out var integer) && (original is null || !byReference.TryGetValue(original, out integer)))
                        {
                            var width = StorageLength(field);
                            object? NormalizeInteger(object? value)
                            {
                                CheckActive();
                                var number = IntegerValue(value, width);
                                if (original is not null) _ = original.Normalize(number);
                                return number;
                            }
                            ProgramBuffer ReadInteger() => IntegerBuffer((long)NormalizeInteger(context.ReadApiScalar(field.Name))!, width, ccsid);
                            integer = new(() => NormalizeInteger(context.ReadApiScalar(field.Name)),
                                value => { CheckActive(); context.WriteApiScalar(field.Name, NormalizeInteger(value)); }, NormalizeInteger,
                                "*INT", width, 0, ReadInteger);
                            byField.Add(field.Name, integer); if (original is not null) byReference.Add(original, integer);
                        }
                        arguments.Add(integer); continue;
                    }
                    if (field.OpaqueBytes)
                    {
                        if (!byField.TryGetValue(field.Name, out var opaque) && (original is null || !byReference.TryGetValue(original, out opaque)))
                        {
                            object? NormalizeOpaque(object? value)
                            {
                                CheckActive();
                                if (value is not ProgramBuffer raw || raw.Length != field.Length || raw.Ccsid != ccsid) throw Bad("Invalid opaque RPG API buffer.");
                                var copy = new ProgramBuffer(raw.ToArray(), ccsid);
                                if (original is not null) _ = original.Normalize(copy);
                                return copy;
                            }
                            ProgramBuffer ReadOpaque() => (ProgramBuffer)NormalizeOpaque(context.ReadApiScalar(field.Name))!;
                            opaque = new(() => ReadOpaque(), value => { CheckActive(); context.WriteApiScalar(field.Name, NormalizeOpaque(value)); }, NormalizeOpaque,
                                "*CHAR", field.Length, 0, ReadOpaque);
                            byField.Add(field.Name, opaque); if (original is not null) byReference.Add(original, opaque);
                        }
                        arguments.Add(opaque); continue;
                    }
                    if (!byField.TryGetValue(field.Name, out var argument) &&
                        (original is null || !byReference.TryGetValue(original, out argument)))
                    {
                        object? OuterValue(object? value) => field.Kind == RpgFieldKind.Character && original?.ToBuffer() is not null
                            ? new ProgramBuffer(Encode((string)value!), ccsid) : value;
                        object? Normalize(object? value)
                        {
                            CheckActive();
                            if (field.Kind == RpgFieldKind.Character)
                            {
                                if (value is ProgramBuffer buffer)
                                {
                                    if (buffer.Ccsid != ccsid || buffer.Length != field.Length) throw Bad("Invalid RPG API character buffer.");
                                    value = buffer.ToText();
                                }
                                if (value is not string text || text.Length != field.Length || Encode(text).Length != field.Length)
                                    throw Bad("RPG API character storage must match its declared byte width without conversion loss.");
                            }
                            else
                            {
                                if (value is not decimal number || decimal.Round(number, field.Decimals) != number || Math.Abs(number) >= Power10(field.Length - field.Decimals))
                                    throw Bad("RPG API number does not fit its declared packed precision and scale.");
                            }
                            // Validate an outer CL/host receiver before the API commits any mutation.
                            if (original is not null) _ = original.Normalize(OuterValue(value));
                            return value;
                        }
                        object? Read()
                        {
                            CheckActive(); var value = context.ReadApiScalar(field.Name);
                            return value is ProgramBuffer buffer && field.Kind == RpgFieldKind.Character ? buffer.ToText() : value;
                        }
                        ProgramBuffer Buffer()
                        {
                            CheckActive(); var text = Normalize(Read()) as string ?? throw Bad("RPG character storage is required.");
                            return new(Encode(text), ccsid);
                        }
                        argument = new(Read, value => { CheckActive(); context.WriteApiScalar(field.Name, OuterValue(value)); }, Normalize,
                            field.Kind == RpgFieldKind.Character ? "*CHAR" : "*DEC", field.Length, field.Decimals,
                            field.Kind == RpgFieldKind.Character ? Buffer : null);
                        byField.Add(field.Name, argument);
                        if (original is not null) byReference.Add(original, argument);
                    }
                    arguments.Add(argument);
                }
            }
            if (bytes > 1048576) throw Bad("RPG API arguments exceed 1 MiB.");
        }
        Arguments = arguments.AsReadOnly();

        byte[] Encode(string text)
        {
            try { return encoding.GetBytes(text); }
            catch (EncoderFallbackException) { throw Bad("RPG API text cannot be represented in the job CCSID."); }
        }
        ProgramConstant Constant(RpgField field, object? value)
        {
            if (field.Kind == RpgFieldKind.Integer)
            {
                var width = StorageLength(field); var integer = IntegerValue(value, width);
                return new("*INT", width, 0, IntegerBuffer(integer, width, ccsid), integer);
            }
            if (field.Kind == RpgFieldKind.Character)
            {
                if (field.OpaqueBytes && value is ProgramBuffer raw && raw.Length == field.Length && raw.Ccsid == ccsid)
                    return new("*CHAR", field.Length, 0, new(raw.ToArray(), ccsid), raw);
                if (value is not string text || text.Length > field.Length) throw Bad("RPG API constant does not fit its character prototype.");
                var padded = text.PadRight(field.Length); var data = Encode(padded);
                if (data.Length != field.Length) throw Bad("RPG API constant does not fit its declared byte width.");
                return new("*CHAR", field.Length, 0, new(data, ccsid), padded);
            }
            if (value is not decimal number || decimal.Round(number, field.Decimals) != number || Math.Abs(number) >= Power10(field.Length - field.Decimals))
                throw Bad("RPG API constant does not fit its packed prototype.");
            var format = new RecordFormat { Name = "ARGUMENT", Fields = [new() { Name = "VALUE", Type = FieldType.Packed, Length = field.Length, Decimals = field.Decimals }] };
            format.AssignPositions();
            return new("*DEC", field.Length, field.Decimals, new(new RecordCodec(ccsid).Encode(format, new Dictionary<string, object?> { ["VALUE"] = number }), ccsid), number);
        }
    }
    private static int StorageLength(RpgField field) => field.Kind switch
    {
        RpgFieldKind.Character => field.Length,
        RpgFieldKind.Integer => field.Length switch { 3 => 1, 5 => 2, 10 => 4, 20 => 8, _ => throw Bad("Unsupported RPG integer precision.") },
        _ => (field.Length + 2) / 2
    };
    private static long IntegerValue(object? value, int width)
    {
        if (value is ProgramBuffer raw)
        {
            if (raw.Length != width) throw Bad("Invalid RPG integer buffer length.");
            var data = raw.ToArray();
            return width switch { 1 => (sbyte)data[0], 2 => BinaryPrimitives.ReadInt16BigEndian(data),
                4 => BinaryPrimitives.ReadInt32BigEndian(data), 8 => BinaryPrimitives.ReadInt64BigEndian(data), _ => throw Bad("Unsupported RPG integer width.") };
        }
        var number = value switch { decimal d => d, long l => l, int i => i, short s => s, sbyte b => b, _ => throw Bad("RPG integer storage requires an exact signed number.") };
        var (min, max) = width switch { 1 => ((decimal)sbyte.MinValue, (decimal)sbyte.MaxValue), 2 => (short.MinValue, short.MaxValue),
            4 => (int.MinValue, int.MaxValue), 8 => (long.MinValue, long.MaxValue), _ => throw Bad("Unsupported RPG integer width.") };
        if (decimal.Truncate(number) != number || number < min || number > max) throw Bad("RPG API integer does not fit its declared signed width.");
        return (long)number;
    }
    private static ProgramBuffer IntegerBuffer(long value, int width, int ccsid)
    {
        var data = new byte[width];
        switch (width)
        {
            case 1: data[0] = unchecked((byte)(sbyte)value); break;
            case 2: BinaryPrimitives.WriteInt16BigEndian(data, (short)value); break;
            case 4: BinaryPrimitives.WriteInt32BigEndian(data, (int)value); break;
            case 8: BinaryPrimitives.WriteInt64BigEndian(data, value); break;
        }
        return new(data, ccsid);
    }
    private static void ValidateScalar(RpgField field)
    {
        if (field.IsArray || field.IsDataStructure || field.Varying || field.Kind is not (RpgFieldKind.Character or RpgFieldKind.Packed or RpgFieldKind.Decimal or RpgFieldKind.Integer))
            throw Bad("RPG API borrowing supports fixed CHAR, signed INT and packed scalar fields only.");
        if (field.Kind == RpgFieldKind.Integer && (field.Length is not (3 or 5 or 10 or 20) || field.Decimals != 0)) throw Bad("Unsupported RPG integer dimensions.");
        if (field.Length is < 1 or > 1048576 || field.Kind is not (RpgFieldKind.Character or RpgFieldKind.Integer) &&
            (field.Length > 28 || field.Decimals < 0 || field.Decimals > field.Length)) throw Bad("Unsupported RPG API field dimensions.");
    }
    private void CheckActive() => ObjectDisposedException.ThrowIf(!_active, this);
    private static decimal Power10(int digits) { var result = 1m; for (var i = 0; i < digits; i++) result *= 10; return result; }
    private static CpfException Bad(string message) => new("CPF3C3A", message);
    public void Dispose() => _active = false;
}
