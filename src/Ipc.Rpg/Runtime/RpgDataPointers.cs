using System.Buffers.Binary;
using System.Security.Cryptography;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

/// <summary>A byte offset within an opaque, execution-owned storage space.</summary>
public sealed class RpgDataAddress
{
    internal RpgDataAddress(ulong space, long offset = 0, bool readOnly = false) { Space = space; Offset = offset; ReadOnly = readOnly; }
    internal ulong Space { get; }
    internal long Offset { get; }
    internal bool ReadOnly { get; }
    public bool IsNull => Space == 0 && Offset == 0;
    public static RpgDataAddress Null { get; } = new(0);
    internal byte[] Bytes()
    { var data = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(data, Space); BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), Offset); return data; }
    internal static RpgDataAddress Decode(byte[] data) => new(BinaryPrimitives.ReadUInt64LittleEndian(data), BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8)));
    internal RpgDataAddress Add(long offset)
    {
        if (Space == 0 && offset != 0) throw Failure(222, "A null pointer has no addressable storage.");
        try { return new(Space, checked(Offset + offset), ReadOnly); }
        catch (OverflowException) { throw Failure(222, "Pointer offset overflow."); }
    }
    internal static RpgRuntimeException Failure(int status, string message) => new(message) { StatusCode = status };
}

public sealed class RpgDataPointerRegistry
{
    private sealed record Region(ProgramArgumentMemory Memory, object Lifetime, Action Validate, bool Heap, object Registration, Action? AddressTaken);
    private readonly Dictionary<ulong, Region> _regions = new();
    private long _heapBytes;
    internal RpgDataAddress Address(ProgramArgumentMemory memory, object lifetime, Action validate, bool heap = false, object? registration = null, Action? addressTaken = null)
    {
        foreach (var pair in _regions)
            if (ReferenceEquals(pair.Value.Memory.Storage.Owner, memory.Storage.Owner) && pair.Value.Memory.Storage.Offset == memory.Storage.Offset && pair.Value.Memory.Capacity == memory.Capacity)
                return new(pair.Key, readOnly: memory.IsReadOnly);
        if (_regions.Count >= 4096) throw RpgDataAddress.Failure(425, "Native pointer storage exceeds 4096 live regions.");
        ulong id; do { id = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)); } while (id == 0 || _regions.ContainsKey(id));
        _regions.Add(id, new(memory, lifetime, validate, heap, registration ?? lifetime, addressTaken)); if (heap) _heapBytes += memory.Capacity;
        return new(id, readOnly: memory.IsReadOnly);
    }
    internal RpgDataAddress? Existing(ProgramArgumentStorage storage)
    {
        foreach (var pair in _regions)
            if (ReferenceEquals(pair.Value.Memory.Storage.Owner, storage.Owner) && storage.Offset >= pair.Value.Memory.Storage.Offset &&
                (long)storage.Offset + storage.Length <= (long)pair.Value.Memory.Storage.Offset + pair.Value.Memory.Capacity)
                return new(pair.Key, storage.Offset - pair.Value.Memory.Storage.Offset, pair.Value.Memory.IsReadOnly);
        return null;
    }
    internal void MarkAddress(RpgDataAddress address)
    { if (_regions.TryGetValue(address.Space, out var region)) region.AddressTaken?.Invoke(); }
    private Region Resolve(RpgDataAddress address, int status = 222)
    {
        if (address.IsNull || !_regions.TryGetValue(address.Space, out var region)) throw RpgDataAddress.Failure(status, "Null, expired or foreign data address.");
        region.Validate(); return region;
    }
    internal ProgramArgumentMemory Memory(RpgDataAddress address, int ccsid)
    {
        var region = Resolve(address);
        if (region.Memory.Ccsid != ccsid) throw RpgDataAddress.Failure(452, "Pointer storage requires its original CCSID.");
        if (address.Offset < 0 || address.Offset > region.Memory.Capacity) throw RpgDataAddress.Failure(222, "Pointer offset is outside its storage space.");
        void Validate()
        {
            if (!_regions.TryGetValue(address.Space, out var current) || !ReferenceEquals(current, region)) throw RpgDataAddress.Failure(222, "The addressed storage has expired.");
            region.Validate();
        }
        return region.Memory.Slice((int)address.Offset).Scoped(Validate, address.ReadOnly);
    }
    internal RpgDataAddress Allocate(int length, int ccsid, object lifetime, Action validate)
    {
        if (length < 1 || length > 16776704 || _heapBytes + length > 67108864) throw RpgDataAddress.Failure(425, "Heap storage exceeds the allocation or 64 MiB execution limit.");
        byte[] bytes;
        try { bytes = new byte[length]; } catch (OutOfMemoryException) { throw RpgDataAddress.Failure(426, "Heap storage could not be allocated."); }
        var storage = new ProgramArgumentStorage(new object(), 0, length);
        var memory = new ProgramArgumentMemory(storage, ccsid, null, (start, count) => new(bytes.AsSpan(start, count), ccsid),
            (_, _) => { }, (start, value) => value.ToArray().CopyTo(bytes, start));
        return Address(memory, lifetime, validate, heap: true);
    }
    internal RpgDataAddress Reallocate(RpgDataAddress address, int length, int ccsid, object lifetime, Action validate)
    {
        var region = Heap(address);
        if (region.Memory.Ccsid != ccsid) throw RpgDataAddress.Failure(452, "Heap storage requires its original CCSID.");
        if (length < 1 || length > 16776704 || _heapBytes - region.Memory.Capacity + length > 67108864) throw RpgDataAddress.Failure(425, "Heap storage exceeds the allocation or execution limit.");
        // Keep the old allocation valid if preparation fails; only publish the replacement after the copy.
        _heapBytes -= region.Memory.Capacity;
        RpgDataAddress replacement;
        try { replacement = Allocate(length, ccsid, region.Lifetime, region.Validate); }
        catch { _heapBytes += region.Memory.Capacity; throw; }
        try { Memory(replacement, ccsid).Write(0, region.Memory.Read(0, Math.Min(length, region.Memory.Capacity))); }
        catch { Free(replacement); _heapBytes += region.Memory.Capacity; throw; }
        _regions.Remove(address.Space); return replacement;
    }
    private Region Heap(RpgDataAddress address)
    {
        var region = Resolve(address, 426);
        if (!region.Heap || address.Offset != 0 || address.ReadOnly) throw RpgDataAddress.Failure(426, "Deallocation requires the beginning of a heap allocation.");
        return region;
    }
    internal void Free(RpgDataAddress address)
    {
        if (address.IsNull) return;
        var region = Heap(address); _regions.Remove(address.Space); _heapBytes -= region.Memory.Capacity;
    }
    internal void ReleaseRegistration(object registration)
    {
        foreach (var pair in _regions.Where(p => ReferenceEquals(p.Value.Registration, registration)).ToArray())
        { if (pair.Value.Heap) _heapBytes -= pair.Value.Memory.Capacity; _regions.Remove(pair.Key); }
    }
    internal void Release(object lifetime)
    {
        foreach (var pair in _regions.Where(p => ReferenceEquals(p.Value.Lifetime, lifetime)).ToArray())
        { if (pair.Value.Heap) _heapBytes -= pair.Value.Memory.Capacity; _regions.Remove(pair.Key); }
    }
}

public sealed partial class RpgRuntimeContext
{
    private readonly List<(NativeView View, RpgExpr Expression)> _deferredAddresses = new();
    private bool DeferParameterAddress(RpgDataStructure structure, byte[] bytes, int offset, RpgDsElement element)
    {
        if (element.Kind != RpgFieldKind.DataPointer || element.InitialValue is null || RpgExpressionParser.Parse(element.InitialValue) is not RpgBuiltin { Name: "ADDR", Arguments.Count: 1 } expression ||
            !_program.NativeParameterLists.GetValueOrDefault("/*ENTRY", []).Contains(RpgCompiler.AddressRoot(expression.Arguments[0]), StringComparer.OrdinalIgnoreCase)) return false;
        for (var index = 0; index < element.Dimension; index++)
        {
            var view = new NativeView(structure, bytes, offset + element.Offset + index * element.Stride, element.StorageLength, element, false, 1);
            WriteNative(view, RpgDataAddress.Null); _deferredAddresses.Add((view, expression));
        }
        return true;
    }
    internal void InitializeDeferredAddresses()
    {
        foreach (var pending in _deferredAddresses) WriteNative(pending.View, pending.Expression.Eval(this));
        _deferredAddresses.Clear();
    }
    private bool _dataFrameEnded;
    public bool HasDataAddresses => _exportStorage.HasDataAddresses;
    internal RpgDataPointerRegistry DataPointers => _exportStorage.DataPointers ??= _host.DataPointers ?? new();
    private void ValidateDataActivation()
    {
        if (_exportStorage.ProcedureActivationEnded) throw NativeFailure(222, "The storage activation was reclaimed.");
        _exportStorage.ValidateProcedureActivation?.Invoke();
    }
    internal void ReleaseDataAddresses()
    { _exportStorage.DataPointers?.Release(_exportStorage); }
    internal void ReleaseAutomaticAddresses()
    { if (_program.IsNativeProcedure) _dataFrameEnded = true; _exportStorage.DataPointers?.Release(this); }
    private ProgramArgumentMemory RegisterNativeAddress(NativeView view, ProgramArgumentMemory memory)
    {
        if (DataPointers.Existing(memory.Storage) is not null) return memory;
        if (view.Binding is null && _nativeStructures.TryGetValue(view.Structure.Name, out var bytes))
        {
            var automatic = _program.IsNativeProcedure && !view.Structure.StaticLocal && !view.Structure.Elements.Any(e => _program.FindField(e.Name)?.StaticLocal == true);
            void Validate()
            { ValidateDataActivation(); if (automatic && _dataFrameEnded) throw NativeFailure(222, "Automatic procedure storage has expired."); }
            var storage = new ProgramArgumentStorage(_nativeStorageOwners[view.Structure.Name], 0, bytes.Length);
            var root = new ProgramArgumentMemory(storage, NativeCcsid, null, (start, count) => { Validate(); return new(bytes.AsSpan(start, count), NativeCcsid); },
                (_, _) => Validate(), (start, value) => { Validate(); value.ToArray().CopyTo(bytes, start); }, memory.IsReadOnly);
            DataPointers.Address(root, automatic ? this : _exportStorage, Validate, registration: this, addressTaken: () => _exportStorage.HasDataAddresses = true);
        }
        else DataPointers.Address(memory, this, ValidateDataActivation, registration: this);
        return memory;
    }
    private object AddressOf(IReadOnlyList<RpgExpr> arguments)
    {
        if (arguments.Count != 1) throw NativeFailure(105, "%ADDR requires one addressable native variable.");
        var indexed = arguments[0] as RpgElementRef;
        if ((indexed?.Target ?? arguments[0]) is RpgMemberRef { Target: RpgAllElementsRef rows } member)
        {
            if (!TryNativeReference(rows.Target, out var records, resolveBased: false) || !records.IsArray || !records.Layout.Qualified)
                throw NativeFailure(105, "%ADDR(*) requires a qualified structure array.");
            return Enumerable.Range(1, records.Count).Select(i =>
            {
                var selected = NativeMember(NativeIndex(records, i), member.Name);
                if (indexed is not null) selected = NativeIndex(selected, NativeSubscript(indexed.Index));
                return (object?)AddressOfView(selected.IsArray ? NativeIndex(selected, 1) : selected);
            }).ToArray();
        }
        var selected = arguments[0] is RpgAllElementsRef all ? all.Target : arguments[0];
        if (!TryNativeReference(selected, out var view, resolveBased: false)) throw NativeFailure(105, "%ADDR requires addressable native storage.");
        if (arguments[0] is RpgAllElementsRef)
        {
            if (!view.IsArray) throw NativeFailure(105, "%ADDR(*) requires an array.");
            return Enumerable.Range(1, view.Count).Select(i => (object?)AddressOfView(NativeIndex(view, i))).ToArray();
        }
        return AddressOfView(view.IsArray ? NativeIndex(view, 1) : view);
    }
    private RpgDataAddress AddressOfView(NativeView view)
    {
        EnsureNativeParameterAvailable(view);
        if (view.Structure.BasedOn is { } based)
            return DataAddress(RpgExpressionParser.Parse(based).Eval(this)).Add(view.Offset);
        var memory = NativeMemory(view); _exportStorage.HasDataAddresses = true;
        var address = DataPointers.Existing(memory.Storage) ?? DataPointers.Address(memory, _exportStorage, ValidateDataActivation);
        DataPointers.MarkAddress(address); return new(address.Space, address.Offset, memory.IsReadOnly);
    }
    private static RpgDataAddress DataAddress(object? value) => value switch
    { RpgDataAddress address => address, RpgNullAddress => RpgDataAddress.Null,
        _ => throw NativeFailure(105, "A basing pointer value is required.") };
    private static int AllocationSize(object? value)
    {
        if (!RpgValues.IsNumeric(value) || RpgValues.IsFloat(value)) throw NativeFailure(425, "Allocation size requires a non-float integer value.");
        var size = RpgValues.ToDecimal(value);
        if (size != decimal.Truncate(size) || size < 1 || size > 16776704) throw NativeFailure(425, "Invalid allocation size.");
        return (int)size;
    }
    private RpgDataAddress AllocateStorage(IReadOnlyList<RpgExpr> arguments, bool reallocate)
    {
        if (arguments.Count != (reallocate ? 2 : 1)) throw NativeFailure(105, "Invalid allocation builtin arguments.");
        var address = reallocate ? DataAddress(arguments[0].Eval(this)) : null;
        var length = AllocationSize(arguments[^1].Eval(this)); _exportStorage.HasDataAddresses = true;
        return reallocate ? DataPointers.Reallocate(address!, length, NativeCcsid, _exportStorage, ValidateDataActivation)
            : DataPointers.Allocate(length, NativeCcsid, _exportStorage, ValidateDataActivation);
    }
    private int _basingDepth;
    private NativeView BindBasedStorage(NativeView view)
    {
        if (view.Structure.BasedOn is not { } based || view.Binding is not null) return view;
        if (_basingDepth >= 128) throw NativeFailure(222, "Circular or excessively nested basing pointers.");
        _basingDepth++;
        try
        {
            var address = DataAddress(RpgExpressionParser.Parse(based).Eval(this)).Add(view.Offset);
            var memory = DataPointers.Memory(address, NativeCcsid).Slice(0, view.Width);
            if ((view.Element?.Kind is RpgFieldKind.DataPointer or RpgFieldKind.ProcedurePointer || view.Element is null && HasPointers(view.Layout)) && memory.Storage.Offset % 16 != 0)
                throw NativeFailure(222, "Based pointer storage requires sixteen-byte alignment.");
            var reference = new ProgramArgument(() => memory.Read(0, memory.Capacity), _ => { }, value => value) { Memory = memory };
            return view with { Binding = MemoryBinding(reference, memory, view) };
        }
        finally { _basingDepth--; }
    }
    private static bool HasPointers(RpgDataStructure layout) => layout.Elements.Any(e => e.Kind is RpgFieldKind.DataPointer or RpgFieldKind.ProcedurePointer || e.NestedStructure is { } nested && HasPointers(nested));
    internal void AllocateNamedStorage(string target, RpgExpr size, bool reallocate)
    {
        if (!TryNativeReference(RpgExpressionParser.Parse(target), out var view) || view.IsArray || view.Element?.Kind != RpgFieldKind.DataPointer)
            throw NativeFailure(105, "Allocation requires a scalar basing pointer receiver.");
        EnsureNativeWritable(view);
        IReadOnlyList<RpgExpr> arguments = reallocate ? [new RpgLiteral(ReadNative(view)), size] : [size];
        WriteNative(view, AllocateStorage(arguments, reallocate));
    }
    internal void DeallocateStorage(string target, bool clear)
    {
        var expression = RpgExpressionParser.Parse(target);
        if (!TryNativeReference(expression, out var view) || view.IsArray || view.Element?.Kind != RpgFieldKind.DataPointer) throw NativeFailure(105, "DEALLOC requires a scalar basing pointer variable.");
        if (clear) EnsureNativeWritable(view); else EnsureNativeParameterAvailable(view);
        var pointer = DataAddress(ReadNative(view)); DataPointers.Free(pointer);
        if (clear) WriteNative(view, RpgDataAddress.Null);
    }
}
