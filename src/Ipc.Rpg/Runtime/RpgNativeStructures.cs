using System.Globalization;
using System.Text;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Db.Definitions;
using Ipc.Db.Records;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private readonly Dictionary<string, byte[]> _nativeStructures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _nativeStorageOwners = new(StringComparer.OrdinalIgnoreCase);
    internal int NativeCcsid { get; }
    internal sealed record NativeView(RpgDataStructure Structure, byte[] Bytes, int Offset, int Width,
        RpgDsElement? Element, bool IsArray, int Count)
    {
        internal int UnitWidth => Element?.StorageLength ?? Structure.Length;
        internal int Stride => Element?.Stride is > 0 ? Element.Stride : UnitWidth;
        internal string Identity => Structure.Name + ":" + Offset + ":" + Width + ":" + Element?.Kind + ":" + Element?.Length + ":" + Element?.Decimals + ":" + Element?.OpaqueBytes + ":" + IsArray;
    }

    private void InitializeNativeStructures()
    {
        foreach (var ds in _program.DataStructures.Where(d => d.NativeStorage))
        {
            var bytes = Enumerable.Repeat(NativeBlank, checked(ds.Length * ds.Dimension)).ToArray();
            _nativeStructures.Add(ds.Name, bytes);
            _nativeStorageOwners.Add(ds.Name, new object());
            for (var index = 0; index < ds.Dimension; index++) InitializeNativeFields(ds, bytes, index * ds.Length, false);
        }
    }
    internal ProgramArgumentStorage NativeStorageRange(NativeView view) => new(_nativeStorageOwners[view.Structure.Name], view.Offset, view.Width);
    private byte NativeBlank => CodePage.FromCcsid(NativeCcsid).GetBytes(" ")[0];
    private Encoding NativeEncoding
    {
        get
        {
            var encoding = (Encoding)CodePage.FromCcsid(NativeCcsid).Clone();
            encoding.EncoderFallback = EncoderFallback.ExceptionFallback; encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
            return encoding;
        }
    }
    private void InitializeNativeFields(RpgDataStructure ds, byte[] bytes, int offset, bool clear)
    {
        foreach (var element in ds.Elements)
        {
            if (!clear && !ds.InitializeSubfields && !element.HasInitialValue) continue;
            var value = clear || element.InitialValue is null ? ClearValueFor(element) : RpgExpressionParser.Parse(element.InitialValue).Eval(this);
            for (var index = 0; index < element.Dimension; index++)
                WriteNative(new(ds, bytes, offset + element.Offset + index * element.Stride, element.StorageLength, element, false, 1), value);
        }
    }

    internal bool TryNativeReference(RpgExpr expression, out NativeView view)
    {
        view = null!;
        if (expression is RpgFieldRef field)
        {
            if (_program.FindDs(field.Name) is { NativeStorage: true, Anonymous: false } ds && _nativeStructures.TryGetValue(ds.Name, out var bytes))
            { view = new(ds, bytes, 0, bytes.Length, null, ds.IsArray, ds.Dimension); return true; }
            if (_program.FindDsElement(field.Name) is not { } element) return false;
            var owner = _program.DataStructures.First(d => d.Elements.Contains(element));
            if (!owner.NativeStorage || !_nativeStructures.TryGetValue(owner.Name, out bytes)) return false;
            if (owner.IsArray) throw NativeFailure(121, "A data structure array subfield requires an indexed, qualified structure.");
            view = NativeMember(new(owner, bytes, 0, owner.Length, null, false, 1), element.LocalName ?? element.Name); return true;
        }
        if (expression is RpgDynamicIndexRef dynamic && TryNativeReference(new RpgFieldRef(dynamic.Name), out var indexed))
        { view = NativeIndex(indexed, RpgValues.ToLong(dynamic.Index.Eval(this))); return true; }
        if (expression is RpgIndexedRef constant && TryNativeReference(new RpgFieldRef(constant.Name), out indexed))
        { view = NativeIndex(indexed, constant.Index); return true; }
        if (expression is RpgMemberRef member && TryNativeReference(member.Target, out var parent))
        {
            if (!parent.Structure.Qualified) throw new RpgRuntimeException("A qualified subfield reference requires QUALIFIED on its structure.");
            view = NativeMember(parent, member.Name); return true;
        }
        if (expression is RpgElementRef subscript && TryNativeReference(subscript.Target, out parent))
        { view = NativeIndex(parent, RpgValues.ToLong(subscript.Index.Eval(this))); return true; }
        return false;
    }
    private static NativeView NativeMember(NativeView parent, string name)
    {
        if (parent.Element is not null) throw new RpgRuntimeException("Only a data structure has subfields.");
        if (parent.IsArray) throw NativeFailure(121, "Select a data structure array element before its subfield.");
        var element = parent.Structure.Find(name) ?? throw new RpgRuntimeException("Unknown data structure subfield: " + name);
        return new(parent.Structure, parent.Bytes, parent.Offset + element.Offset, checked(element.StorageLength * element.Dimension), element, element.IsArray, element.Dimension);
    }
    private static NativeView NativeIndex(NativeView parent, long index)
    {
        if (!parent.IsArray || index < 1 || index > parent.Count) throw NativeFailure(121, "Data structure or subfield array index is out of range.");
        return parent with { Offset = parent.Offset + checked((int)(index - 1) * parent.Stride), Width = parent.UnitWidth, IsArray = false, Count = 1 };
    }
    public object? ReadNativeReference(RpgExpr expression) => TryNativeReference(expression, out var view)
        ? ReadNative(view) : throw new RpgRuntimeException("Qualified reference requires a native data structure.");
    public void WriteNativeReference(RpgExpr expression, object? value)
    {
        if (!TryNativeReference(expression, out var view)) throw new RpgRuntimeException("Qualified assignment requires a native data structure.");
        WriteNative(view, value);
    }
    internal void WriteCapturedNative(NativeView view, object? value) => WriteNative(view, value);
    internal ProgramBuffer NativeBuffer(NativeView view)
    {
        if (!view.IsArray || view.Stride == view.UnitWidth) return new(view.Bytes.AsSpan(view.Offset, view.Width).ToArray(), NativeCcsid);
        var result = new byte[view.Width];
        for (var i = 0; i < view.Count; i++) view.Bytes.AsSpan(view.Offset + i * view.Stride, view.UnitWidth).CopyTo(result.AsSpan(i * view.UnitWidth));
        return new(result, NativeCcsid);
    }
    internal object? ReadNative(NativeView view)
    {
        var e = view.Element;
        if (view.IsArray) return Enumerable.Range(1, view.Count).Select(i => ReadNative(NativeIndex(view, i))).ToArray();
        if (e is null || e.OpaqueBytes) return NativeBuffer(view);
        try
        {
            var data = view.Bytes.AsSpan(view.Offset, view.Width).ToArray();
            if (e.Kind == RpgFieldKind.Character) return NativeEncoding.GetString(data);
            if (e.Kind == RpgFieldKind.Integer && e.StorageLength == 1) return (long)unchecked((sbyte)data[0]);
            return new RecordCodec(NativeCcsid).ReadValue(data, NativeField(e));
        }
        catch (DecoderFallbackException error) { throw NativeFailure(452, error.Message); }
        catch (FormatException error) { throw NativeFailure(e.Kind is RpgFieldKind.Date or RpgFieldKind.Time or RpgFieldKind.Timestamp ? 112 : e.Kind is RpgFieldKind.Packed or RpgFieldKind.Zoned ? 907 : 105, error.Message); }
        catch (OverflowException error) { throw NativeFailure(103, error.Message); }
    }
    private FieldSpec NativeField(RpgDsElement e) => new() { Name = e.Name, Length = e.Kind == RpgFieldKind.Integer ? e.StorageLength : e.Length,
        Decimals = e.Decimals, Position = 1, Ccsid = NativeCcsid, Type = e.Kind switch {
            RpgFieldKind.Character => FieldType.Alpha, RpgFieldKind.Packed => FieldType.Packed, RpgFieldKind.Zoned => FieldType.Zoned,
            RpgFieldKind.Integer => FieldType.Binary, RpgFieldKind.Float => FieldType.Float, RpgFieldKind.Indicator => FieldType.Logic,
            RpgFieldKind.Date => FieldType.Date, RpgFieldKind.Time => FieldType.Time, RpgFieldKind.Timestamp => FieldType.Timestamp,
            _ => throw new RpgRuntimeException("Unsupported native subfield representation.") } };
    private static RpgRuntimeException NativeFailure(int status, string message) => new(message) { StatusCode = status };

    internal byte[] NormalizeNative(NativeView view, object? value, bool strict = false)
    {
        var result = Enumerable.Repeat(NativeBlank, view.Width).ToArray(); var e = view.Element;
        try
        {
            if (e is null || view.IsArray || e.OpaqueBytes || e.Kind == RpgFieldKind.Character)
            {
                byte[] source;
                if (value is ProgramBuffer buffer)
                {
                    if (strict && buffer.Ccsid != NativeCcsid) throw new RpgRuntimeException("Native argument CCSIDs do not match.");
                    source = buffer.ToArray();
                }
                else source = NativeEncoding.GetBytes(RpgValues.ToText(value));
                if (strict && source.Length != view.Width) throw new RpgRuntimeException("Native argument must match its byte width.");
                if (strict && e is { Kind: RpgFieldKind.Character, OpaqueBytes: false })
                    for (var i = 0; i < view.Count; i++) _ = NativeEncoding.GetString(source, i * view.UnitWidth, view.UnitWidth);
                source.AsSpan(0, Math.Min(source.Length, result.Length)).CopyTo(result); return result;
            }
            if (value is ProgramBuffer raw)
            {
                if (!strict || raw.Length != view.Width || raw.Ccsid != NativeCcsid) throw new RpgRuntimeException("Native numeric argument has an incompatible value, byte width or CCSID.");
                var data = raw.ToArray();
                _ = ReadNative(view with { Bytes = data, Offset = 0 });
                return data;
            }
            if (e.Kind == RpgFieldKind.Integer)
            {
                var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                if (strict && number != decimal.Truncate(number)) throw NativeFailure(103, "An integer argument cannot have fractional digits.");
                number = decimal.Truncate(number);
                var min = e.StorageLength == 8 ? long.MinValue : -(1L << (e.StorageLength * 8 - 1));
                var max = e.StorageLength == 8 ? long.MaxValue : (1L << (e.StorageLength * 8 - 1)) - 1;
                if (number < min || number > max) throw NativeFailure(103, "Integer result exceeds its storage width.");
                var bits = (long)number;
                for (var i = result.Length - 1; i >= 0; i--) { result[i] = (byte)bits; bits >>= 8; }
                return result;
            }
            if (e.Kind is RpgFieldKind.Packed or RpgFieldKind.Zoned)
            {
                var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                var rounded = decimal.Round(number, e.Decimals, MidpointRounding.ToZero);
                if (strict && rounded != number) throw NativeFailure(103, "Numeric argument exceeds its declared scale.");
                number = rounded;
                new RecordCodec(NativeCcsid).WriteValue(result, NativeField(e), number); return result;
            }
            new RecordCodec(NativeCcsid).WriteValue(result, NativeField(e), value); return result;
        }
        catch (EncoderFallbackException error) { throw NativeFailure(452, error.Message); }
        catch (DecoderFallbackException error) { throw NativeFailure(452, error.Message); }
        catch (OverflowException error) { throw NativeFailure(103, error.Message); }
        catch (FormatException error) { throw NativeFailure(e?.Kind is RpgFieldKind.Date or RpgFieldKind.Time or RpgFieldKind.Timestamp ? 112 : 105, error.Message); }
    }
    private void WriteNative(NativeView view, object? value)
    {
        if (view.IsArray)
        {
            var image = NativeBuffer(view).ToArray(); var values = value as object?[];
            for (var i = 0; i < (values is null ? view.Count : Math.Min(view.Count, values.Length)); i++)
                NormalizeNative(NativeIndex(view, i + 1), values is null ? value : values[i]).CopyTo(image, i * view.UnitWidth);
            WriteNativeBytes(view, image); return;
        }
        var data = NormalizeNative(view, value);
        data.CopyTo(view.Bytes, view.Offset);
    }
    internal static void WriteNativeBytes(NativeView view, byte[] bytes)
    {
        if (bytes.Length != view.Width) throw new RpgRuntimeException("Native receiver byte width changed.");
        if (!view.IsArray || view.Stride == view.UnitWidth) bytes.CopyTo(view.Bytes, view.Offset);
        else for (var i = 0; i < view.Count; i++) bytes.AsSpan(i * view.UnitWidth, view.UnitWidth).CopyTo(view.Bytes.AsSpan(view.Offset + i * view.Stride));
    }
    private bool TryClearNative(string operand)
    {
        if (!TryNativeReference(RpgExpressionParser.Parse(operand), out var view)) return false;
        if (view.Element is { } e)
        {
            for (var i = 0; i < view.Count; i++) WriteNative(view.IsArray ? NativeIndex(view, i + 1) : view, ClearValueFor(e));
        }
        else
        {
            // Prepare a private image, then publish the entire CLEAR atomically.
            var bytes = Enumerable.Repeat(NativeBlank, view.Width).ToArray();
            for (var i = 0; i < view.Count; i++) InitializeNativeFields(view.Structure, bytes, i * view.Structure.Length, true);
            bytes.CopyTo(view.Bytes, view.Offset);
        }
        return true;
    }
    private bool TryWriteNativeSubstring(IReadOnlyList<RpgExpr> arguments, object? value)
    {
        if (!TryNativeReference(arguments[0], out var view)) return false;
        if (view.IsArray) throw new RpgRuntimeException("Writable array %SUBST requires an indexed element.");
        if (view.Element is not null && view.Element.Kind != RpgFieldKind.Character) throw new RpgRuntimeException("Writable %SUBST requires character or data structure storage.");
        var from = RpgValues.ToLong(arguments[1].Eval(this));
        var count = arguments.Count == 3 ? RpgValues.ToLong(arguments[2].Eval(this)) : view.Width - from + 1;
        if (from < 1 || from > view.Width + 1 || count < 0 || count > view.Width - from + 1) throw NativeFailure(100, "Writable substring exceeds native storage.");
        byte[] source;
        try { source = value is ProgramBuffer buffer ? buffer.ToArray() : NativeEncoding.GetBytes(RpgValues.ToText(value)); }
        catch (EncoderFallbackException error) { throw NativeFailure(452, error.Message); }
        var result = NativeBuffer(view).ToArray(); var slice = result.AsSpan((int)from - 1, (int)count); slice.Fill(NativeBlank);
        source.AsSpan(0, Math.Min(source.Length, slice.Length)).CopyTo(slice); WriteNativeBytes(view, result); return true;
    }
}
