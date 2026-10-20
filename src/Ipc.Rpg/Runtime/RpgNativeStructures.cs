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
    private readonly Dictionary<string, NativeScalarBinding> _nativeScalarBindings = new(StringComparer.OrdinalIgnoreCase);
    internal int NativeCcsid { get; }
    internal sealed record NativeScalarBinding(ProgramArgument Reference, Func<byte[]> Read, Action<byte[]> Validate,
        Action<byte[]> Write, ProgramArgumentStorage Storage, ProgramArgumentMemory? Memory = null);
    internal sealed record NativeView(RpgDataStructure Structure, byte[] Bytes, int Offset, int Width,
        RpgDsElement? Element, bool IsArray, int Count, int? ArrayStride = null, NativeScalarBinding? Binding = null, RpgDataStructure? NestedLayout = null, NativeView? Parent = null, RpgDsElement? Container = null)
    {
        internal RpgDataStructure Layout => NestedLayout ?? Structure;
        internal int UnitWidth => Element?.StorageLength ?? Layout.Length;
        internal int Stride => ArrayStride ?? (Element?.Stride is > 0 ? Element.Stride : UnitWidth);
        internal string Identity => Structure.Name + ":" + Offset + ":" + Width + ":" + Element?.Kind + ":" + Element?.Length + ":" + Element?.Decimals + ":" + Element?.OpaqueBytes + ":" + IsArray;
    }

    private void InitializeNativeStructures()
    {
        var initialize = new List<RpgDataStructure>();
        foreach (var ds in _program.DataStructures.Where(d => d.NativeStorage && !d.IsTemplate))
        {
            if (_program.GlobalStructures.Contains(ds)) continue;
            var exported = ExportedStructure(ds);
            if (exported && _exportStorage.Values.TryGetValue(ds, out var retained))
            { _nativeStructures.Add(ds.Name, retained.Bytes); _nativeStorageOwners.Add(ds.Name, retained.Owner); continue; }
            var isStatic = _program.IsNativeProcedure && (ds.StaticLocal || ds.Elements.Any(e => _program.FindField(e.Name)?.StaticLocal == true));
            if (isStatic && ModuleContext._procedureStaticStorage.TryGetValue(ds, out var saved))
            { _nativeStructures.Add(ds.Name, saved.Bytes); _nativeStorageOwners.Add(ds.Name, saved.Owner); continue; }
            if (isStatic && (long)ds.Length * ds.StorageCount > 16777216 - ModuleContext._procedureStaticBytes)
                throw NativeFailure(202, "Procedure STATIC storage exceeds 16 MiB for the module activation.");
            byte[] bytes; object owner;
            if (!_program.IsNativeProcedure && !exported && ds.BasedOn is null && _exportStorage.CycleValues.TryGetValue(ds, out var cycle))
            { bytes = cycle.Bytes; owner = cycle.Owner; Array.Fill(bytes, ds.IndicatorDataStructure ? NativeZero : NativeBlank); }
            else { bytes = Enumerable.Repeat(ds.IndicatorDataStructure ? NativeZero : NativeBlank, checked(ds.Length * ds.StorageCount)).ToArray(); owner = new object(); }
            _nativeStructures.Add(ds.Name, bytes); _nativeStorageOwners.Add(ds.Name, owner);
            if (ds.BasedOn is null) initialize.Add(ds);
            if (isStatic) { ModuleContext._procedureStaticStorage.Add(ds, (bytes, owner)); ModuleContext._procedureStaticBytes += bytes.Length; }
            if (exported) _exportStorage.Values.Add(ds, (bytes, owner));
            else if (!_program.IsNativeProcedure && ds.BasedOn is null) _exportStorage.CycleValues.TryAdd(ds, (bytes, owner));
        }
        // All addresses exist before evaluating INZ(%ADDR(...)), including forward declarations.
        foreach (var ds in initialize)
            for (var index = 0; index < ds.StorageCount; index++)
            {
                _host.CancellationToken.ThrowIfCancellationRequested();
                InitializeNativeFields(ds, _nativeStructures[ds.Name], index * ds.Length, false);
            }
        ValidateCompileTimeCharacterSequences();
    }
    internal ProgramArgumentStorage NativeStorageRange(NativeView view) => _program.GlobalStructures.Contains(view.Structure)
        ? GlobalContext!.NativeStorageRange(view) : view.Binding?.Storage ?? new(_nativeStorageOwners[view.Structure.Name], view.Offset, view.Width);
    private byte NativeBlank => CodePage.FromCcsid(NativeCcsid).GetBytes(" ")[0];
    private byte NativeZero => CodePage.FromCcsid(NativeCcsid).GetBytes("0")[0];
    private byte NativeOne => CodePage.FromCcsid(NativeCcsid).GetBytes("1")[0];
    private Encoding NativeEncoding
    {
        get
        {
            var encoding = (Encoding)CodePage.FromCcsid(NativeCcsid).Clone();
            encoding.EncoderFallback = EncoderFallback.ExceptionFallback; encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
            return encoding;
        }
    }
    private void InitializeNativeFields(RpgDataStructure ds, byte[] bytes, int offset, bool clear, bool initializeAll = false)
    {
        foreach (var element in ds.Elements)
        {
            if (element.NestedStructure is { } nested)
            {
                for (var index = 0; index < element.Dimension; index++)
                    InitializeNativeFields(nested, bytes, offset + element.Offset + index * element.Stride, clear, (initializeAll || ds.InitializeSubfields) && !element.HasInitialValue);
                continue;
            }
            if (!clear && element.ExternalDefaultInitialization && element.LikeType is RpgExternalField external)
            {
                var codec = new RpgDsElement { Name = element.Name, Kind = external.Kind, Length = external.Length, Decimals = external.Decimals,
                    StorageLength = external.StorageLength, Stride = external.StorageLength };
                var initial = element.InitialValue is null ? ClearValueFor(codec) : RpgExpressionParser.Parse(element.InitialValue).Eval(this);
                WriteNative(new(ds, bytes, offset + element.Offset, external.StorageLength, codec, false, 1), initial); continue;
            }
            if (!clear && !initializeAll && !ds.InitializeSubfields && !element.HasInitialValue) continue;
            if (!clear && DeferParameterAddress(ds, bytes, offset, element)) continue;
            var value = clear || element.InitialValue is null ? ClearValueFor(element) : RpgExpressionParser.Parse(element.InitialValue).Eval(this);
            for (var index = 0; index < element.Dimension; index++)
            {
                _host.CancellationToken.ThrowIfCancellationRequested();
                var supplied = !clear && index < element.CompileTimeInitializers.Count;
                var initial = supplied ? element.Kind == RpgFieldKind.Character ? element.CompileTimeInitializers[index] :
                    RpgExpressionParser.Parse(element.CompileTimeInitializers[index]).Eval(this) : value;
                try { WriteNative(new(ds, bytes, offset + element.Offset + index * element.Stride, element.StorageLength, element, false, 1), initial); }
                catch (RpgRuntimeException error) when (supplied) { throw CompileTimeError(element, index, error); }
            }
        }
    }

    internal bool TryNativeReference(RpgExpr expression, out NativeView view, bool resolveBased = true)
    {
        if (!TryNativeReferenceCore(expression, out view, resolveBased)) return false;
        if (resolveBased) view = BindBasedStorage(view);
        return true;
    }
    private bool TryNativeReferenceCore(RpgExpr expression, out NativeView view, bool resolveBased)
    {
        view = null!;
        if (RpgCompiler.TemplateReference(_program, expression)) throw NativeFailure(105, "TEMPLATE definitions have no runtime storage.");
        if (IsGlobalExpression(expression)) return GlobalContext!.TryNativeReference(expression, out view, resolveBased);
        if (expression is RpgAllElementsRef) throw NativeFailure(105, "A (*) data structure key requires SORTA or %LOOKUP.");
        if (expression is RpgBuiltin { Name: "SUBARR" } slice) { view = NativeSlice(slice.Arguments); return true; }
        if (expression is RpgFieldRef field)
        {
            if (_program.FindField(field.Name) is { IsTable: true })
            { view = NativeIndex(BindImportedData(field.Name, TableStorage(field.Name)), _nativeTableSelections.GetValueOrDefault(field.Name, 1)); return true; }
            if (_program.FindDs(field.Name) is { NativeStorage: true, Anonymous: false } ds && _nativeStructures.TryGetValue(ds.Name, out var bytes))
            {
                view = new(ds, bytes, NativeOccurrenceOffset(ds), ds.IsMultipleOccurrence ? ds.Length : bytes.Length, null, ds.IsArray, ds.Dimension);
                if (_nativeScalarBindings.TryGetValue(field.Name, out var aggregate) || _nativeDetachedBindings.TryGetValue(field.Name, out aggregate)) view = AttachNativeBinding(view, aggregate);
                view = BindImportedData(field.Name, view); return true;
            }
            if (_program.FindDsElement(field.Name) is not { } element) return false;
            var owner = _program.DataStructures.First(d => d.Elements.Contains(element));
            if (!owner.NativeStorage || !_nativeStructures.TryGetValue(owner.Name, out bytes)) return false;
            if (owner.IsArray) throw NativeFailure(121, "A data structure array subfield requires an indexed, qualified structure.");
            var root = new NativeView(owner, bytes, NativeOccurrenceOffset(owner), owner.Length, null, false, 1);
            if (_nativeScalarBindings.TryGetValue(owner.Name, out var rootBinding) || _nativeDetachedBindings.TryGetValue(owner.Name, out rootBinding)) root = AttachNativeBinding(root, rootBinding);
            root = BindImportedData(owner.Name, root);
            view = NativeMember(root, element.LocalName ?? element.Name);
            if (_nativeScalarBindings.TryGetValue(field.Name, out var binding) || _nativeDetachedBindings.TryGetValue(field.Name, out binding)) view = AttachNativeBinding(view, binding);
            view = BindImportedData(field.Name, view); return true;
        }
        if (expression is RpgDynamicIndexRef tableIndex && _program.FindField(tableIndex.Name) is { IsTable: true } ||
            expression is RpgIndexedRef tableConstant && _program.FindField(tableConstant.Name) is { IsTable: true })
            throw NativeFailure(105, "A table name denotes its current element and cannot be indexed.");
        if (expression is RpgDynamicIndexRef dynamic && TryNativeReference(new RpgFieldRef(dynamic.Name), out var indexed, resolveBased))
        { view = NativeIndex(indexed, NativeSubscript(dynamic.Index)); return true; }
        if (expression is RpgIndexedRef constant && TryNativeReference(new RpgFieldRef(constant.Name), out indexed, resolveBased))
        { view = NativeIndex(indexed, constant.Index); return true; }
        if (expression is RpgMemberRef member && TryNativeReference(member.Target, out var parent, resolveBased))
        {
            if (!parent.Layout.Qualified) throw new RpgRuntimeException("A qualified subfield reference requires QUALIFIED on its structure.");
            view = NativeMember(parent, member.Name); return true;
        }
        if (expression is RpgElementRef subscript && TryNativeReference(subscript.Target, out parent, resolveBased))
        { view = NativeIndex(parent, NativeSubscript(subscript.Index)); return true; }
        return false;
    }
    internal NativeView NativeMember(NativeView parent, string name)
    {
        if (parent.Element is not null) throw new RpgRuntimeException("Only a data structure has subfields.");
        if (parent.IsArray) throw NativeFailure(121, "Select a data structure array element before its subfield.");
        var element = parent.Layout.Find(name) ?? throw new RpgRuntimeException("Unknown data structure subfield: " + name);
        return NativeMember(parent, element);
    }
    private NativeView NativeMember(NativeView parent, RpgDsElement element)
    {
        var view = new NativeView(parent.Structure, parent.Bytes, parent.Offset + element.Offset, checked(element.StorageLength * element.Dimension),
            element.NestedStructure is null ? element : null, element.IsArray, element.Dimension, ArrayStride: element.Stride, NestedLayout: element.NestedStructure ?? parent.NestedLayout, Parent: parent, Container: element);
        return BindNativeSubview(parent, view, element.Offset, checked((view.Count - 1) * view.Stride + view.UnitWidth), ViewShape(view));
    }
    private NativeView NativeIndex(NativeView parent, long index)
    {
        if (!parent.IsArray || index < 1 || index > parent.Count) throw NativeFailure(121, "Data structure or subfield array index is out of range.");
        var delta = (index - 1) * parent.Stride;
        if (delta > int.MaxValue - parent.Offset) throw NativeFailure(105, "Native parameter address exceeds its actual storage.");
        var view = parent with { Offset = parent.Offset + (int)delta, Width = parent.UnitWidth, IsArray = false, Count = 1, Binding = null };
        return BindNativeSubview(parent, view, (int)delta, Math.Min(view.UnitWidth, parent.Stride), parent.Binding?.Memory?.Shape is { } shape ? shape with { Dimension = 1 } : null);
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
        EnsureNativeParameterAvailable(view);
        if (view.Binding?.Memory is { } memory) return new(ReadNativeMemory(view, memory), NativeCcsid);
        RefreshNativeBinding(view);
        if (!view.IsArray || view.Stride == view.UnitWidth) return new(view.Bytes.AsSpan(view.Offset, view.Width).ToArray(), NativeCcsid);
        var result = new byte[view.Width];
        for (var i = 0; i < view.Count; i++) view.Bytes.AsSpan(view.Offset + i * view.Stride, view.UnitWidth).CopyTo(result.AsSpan(i * view.UnitWidth));
        return new(result, NativeCcsid);
    }
    internal object? ReadNative(NativeView view)
    {
        EnsureNativeParameterAvailable(view);
        if (view.Binding?.Memory is { } memory)
            return ReadNative(view with { Bytes = ReadNativeMemory(view, memory), Offset = 0, ArrayStride = view.UnitWidth, Binding = null });
        RefreshNativeBinding(view);
        var e = view.Element;
        // The complete array was refreshed above. Decode that image without fetching
        // the entire borrowed buffer again for every element.
        if (view.IsArray) return Enumerable.Range(1, view.Count).Select(i => ReadNative(NativeIndex(view with { Binding = null }, i))).ToArray();
        if (e is null || e.OpaqueBytes) return NativeBuffer(view with { Binding = null });
        try
        {
            var data = view.Bytes.AsSpan(view.Offset, view.Width).ToArray();
            if (e.Kind == RpgFieldKind.DataPointer) return RpgDataAddress.Decode(data);
            if (e.Kind == RpgFieldKind.ProcedurePointer) return new RpgProcedureAddress(new Guid(data));
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
        EnsureNativeWritable(view);
        _host.CancellationToken.ThrowIfCancellationRequested();
        var bytes = NormalizeNativeValue(view, value, strict);
        view.Binding?.Validate(bytes);
        _host.CancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }
    private byte[] NormalizeNativeValue(NativeView view, object? value, bool strict)
    {
        var result = Enumerable.Repeat(NativeBlank, view.Width).ToArray(); var e = view.Element;
        try
        {
            if (e?.Kind == RpgFieldKind.DataPointer && !view.IsArray)
            {
                if (strict && value is ProgramBuffer rawData && rawData.Length == 16 && rawData.Ccsid == NativeCcsid) return rawData.ToArray();
                var pointer = DataAddress(value);
                if (pointer.ReadOnly) throw NativeFailure(105, "The address of a CONST parameter can only be compared.");
                return pointer.Bytes();
            }
            if (e?.Kind == RpgFieldKind.ProcedurePointer && !view.IsArray)
            {
                if (strict && value is ProgramBuffer rawPointer && rawPointer.Length == 16 && rawPointer.Ccsid == NativeCcsid) return rawPointer.ToArray();
                if (value is RpgNullAddress) value = RpgProcedureAddress.Null;
                if (value is not RpgProcedureAddress pointer) throw NativeFailure(105, "Procedure pointers require a procedure address or *NULL.");
                return pointer.Value.ToByteArray();
            }
            if (value is RpgProcedureAddress or RpgDataAddress) throw NativeFailure(105, "A pointer address requires matching pointer storage.");
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
                source.AsSpan(0, Math.Min(source.Length, result.Length)).CopyTo(result);
                return result;
            }
            if (value is ProgramBuffer raw)
            {
                if (!strict || raw.Length != view.Width || raw.Ccsid != NativeCcsid) throw new RpgRuntimeException("Native numeric argument has an incompatible value, byte width or CCSID.");
                var data = raw.ToArray();
                _ = ReadNative(view with { Bytes = data, Offset = 0, Binding = null });
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
        if (!view.IsArray && value is object?[]) throw NativeFailure(105, "An array value cannot be assigned to scalar storage.");
        if (view.IsArray)
        {
            EnsureNativeWritable(view);
            var values = value as object?[];
            var count = values is null ? view.Count : Math.Min(view.Count, values.Length);
            if (count == 0) return;
            var target = count == view.Count ? view : SliceArray(view, 1, count);
            var image = NativeBuffer(target).ToArray();
            for (var i = 0; i < count; i++)
            {
                _host.CancellationToken.ThrowIfCancellationRequested();
                NormalizeNative(NativeIndex(target with { Binding = null }, i + 1), values is null ? value : values[i]).CopyTo(image, i * view.UnitWidth);
            }
            target.Binding?.Validate(image);
            _host.CancellationToken.ThrowIfCancellationRequested();
            WriteNativeBytes(target, image); return;
        }
        var data = NormalizeNative(view, value);
        WriteNativeBytes(view, data);
    }
    internal static void WriteNativeBytes(NativeView view, byte[] bytes)
    {
        if (bytes.Length != view.Width) throw new RpgRuntimeException("Native receiver byte width changed.");
        if (view.Binding?.Memory is { } memory) { WriteNativeMemory(view, memory, bytes); return; }
        view.Binding?.Write(bytes);
        if (!view.IsArray || view.Stride == view.UnitWidth) bytes.CopyTo(view.Bytes, view.Offset);
        else for (var i = 0; i < view.Count; i++) bytes.AsSpan(i * view.UnitWidth, view.UnitWidth).CopyTo(view.Bytes.AsSpan(view.Offset + i * view.Stride));
    }
    private static void RefreshNativeBinding(NativeView view)
    {
        if (view.Binding?.Memory is { } memory)
        {
            var count = Math.Min(memory.Capacity, view.Bytes.Length - view.Offset);
            if (count > 0) memory.Read(0, count).ToArray().CopyTo(view.Bytes, view.Offset);
            return;
        }
        if (view.Binding is { } binding) binding.Read().CopyTo(view.Bytes, view.Offset);
    }
    private bool TryClearNative(string operand, bool allOccurrences = false)
    {
        if (!TryNativeReference(RpgExpressionParser.Parse(operand), out var view)) return false;
        if (allOccurrences)
        {
            if (view.Element is not null || !view.Layout.IsMultipleOccurrence) throw new RpgRuntimeException("CLEAR *ALL requires a multiple-occurrence data structure.");
            view = view with { Offset = 0, Width = view.Bytes.Length, Count = view.Structure.Occurrences };
            if (view.Structure.BasedOn is not null) view = BindBasedStorage(view with { Binding = null });
        }
        if (view.Element is { } e)
        {
            WriteNative(view, ClearValueFor(e));
        }
        else
        {
            EnsureNativeWritable(view);
            // Prepare a private image, then publish the entire CLEAR atomically.
            var bytes = Enumerable.Repeat(view.Layout.IndicatorDataStructure ? NativeZero : NativeBlank, view.Width).ToArray();
            for (var i = 0; i < view.Count; i++)
            {
                _host.CancellationToken.ThrowIfCancellationRequested();
                InitializeNativeFields(view.Layout, bytes, i * view.Layout.Length, true);
            }
            _host.CancellationToken.ThrowIfCancellationRequested();
            view.Binding?.Validate(bytes);
            _host.CancellationToken.ThrowIfCancellationRequested();
            WriteNativeBytes(view, bytes);
            if (allOccurrences) _nativeOccurrences[view.Structure.Name] = 1;
        }
        return true;
    }
    private bool TryWriteNativeSubstring(IReadOnlyList<RpgExpr> arguments, object? value)
    {
        if (!TryNativeReference(arguments[0], out var view)) return false;
        EnsureNativeWritable(view);
        if (view.IsArray) throw new RpgRuntimeException("Writable array %SUBST requires an indexed element.");
        if (view.Element is not null && view.Element.Kind != RpgFieldKind.Character) throw new RpgRuntimeException("Writable %SUBST requires character or data structure storage.");
        var from = RpgValues.ToLong(arguments[1].Eval(this));
        var count = arguments.Count == 3 ? RpgValues.ToLong(arguments[2].Eval(this)) : view.Width - from + 1;
        if (from < 1 || from > view.Width + 1 || count < 0 || count > view.Width - from + 1) throw NativeFailure(100, "Writable substring exceeds native storage.");
        byte[] source;
        try { source = value is ProgramBuffer buffer ? buffer.ToArray() : NativeEncoding.GetBytes(RpgValues.ToText(value)); }
        catch (EncoderFallbackException error) { throw NativeFailure(452, error.Message); }
        if (view.Binding?.Memory is { } memory)
        {
            if (from - 1 + count > memory.Capacity) throw NativeFailure(105, "Substring exceeds the actual argument storage.");
            var bytes = Enumerable.Repeat(NativeBlank, (int)count).ToArray(); source.AsSpan(0, Math.Min(source.Length, bytes.Length)).CopyTo(bytes);
            memory.Write((int)from - 1, new(bytes, NativeCcsid)); return true;
        }
        var result = NativeBuffer(view).ToArray(); var slice = result.AsSpan((int)from - 1, (int)count); slice.Fill(NativeBlank);
        source.AsSpan(0, Math.Min(source.Length, slice.Length)).CopyTo(slice); WriteNativeBytes(view, result); return true;
    }
}
