using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private readonly Dictionary<string, NativeScalarBinding> _nativeDetachedBindings = new(StringComparer.OrdinalIgnoreCase);
    internal static int ShapeUnitBytes(ProgramArgumentShape shape) => shape.ElementType switch
    { "Integer" => shape.ElementLength switch { 3 => 1, 5 => 2, 10 => 4, _ => 8 }, "Packed" => (shape.ElementLength + 2) / 2, _ => shape.ElementLength };
    private static ProgramArgumentShape ViewShape(NativeView view) => view.Element is { } element
        ? new(element.Kind.ToString(), element.Length, element.Decimals, view.IsArray ? view.Count : 1, false, element.OpaqueBytes)
        : new("*DS", view.Structure.Length, 0, view.IsArray ? view.Count : 1, true, true);

    internal ProgramArgumentMemory NativeMemory(NativeView view)
    {
        if (_program.GlobalStructures.Contains(view.Structure)) return GlobalContext!.NativeMemory(view);
        if (view.Binding?.Memory is { } existing) return view.Element?.ReadOnlyParameter == true || view.Structure.ReadOnlyParameter || view.Binding.Reference.IsReadOnly
            ? existing.Scoped(() => { }, readOnly: true) : existing;
        if (view.IsArray && view.Stride != view.UnitWidth) throw new CpfException("CPF3C3A", "A noncontiguous argument has no single byte address.");
        ProgramBuffer Read(int offset, int length)
        {
            EnsureNativeParameterAvailable(view);
            return view.Binding is null ? new(view.Bytes.AsSpan(view.Offset + offset, length), NativeCcsid)
                : new(NativeBuffer(view).ToArray().AsSpan(offset, length), NativeCcsid);
        }
        byte[] Merge(int offset, ProgramBuffer value) { var bytes = NativeBuffer(view).ToArray(); value.ToArray().CopyTo(bytes, offset); return bytes; }
        return new(NativeStorageRange(view), NativeCcsid, ViewShape(view), Read,
            (offset, value) => { _ = NormalizeNative(view, new ProgramBuffer(Merge(offset, value), NativeCcsid), strict: true); },
            (offset, value) => WriteNativeBytes(view, Merge(offset, value)),
            view.Element?.ReadOnlyParameter == true || view.Structure.ReadOnlyParameter || view.Binding?.Reference.IsReadOnly == true);
    }

    internal ProgramArgumentMemory ArgumentMemory(ProgramArgument argument, RpgField formal)
    {
        if (argument.Memory is { } existing) return existing.Scoped(() => { }, argument.IsReadOnly);
        var initial = argument.Length > 0 ? null : argument.ToBuffer() ?? argument.Value as ProgramBuffer;
        if (initial is null && argument.Length == 0 && formal.Kind == RpgFieldKind.Character && !formal.IsArray && argument.Value is string text)
            initial = new ProgramBuffer(NativeEncoding.GetBytes(text), NativeCcsid);
        if (initial is not null && (initial.Length < 1 || initial.Ccsid != NativeCcsid)) throw NativeFailure(105, "Native entry requires actual bytes in the current CCSID.");
        var length = initial?.Length ?? (argument.Type is "*CHAR" or "*RAW" || AggregateParameter(formal) || formal.Kind == RpgFieldKind.Character ? argument.Length : ParameterBytes(formal));
        if (length < 1) throw NativeFailure(105, "Native entry requires actual byte storage.");
        if (argument.Storage is { } supplied && supplied.Length != length) throw NativeFailure(105, "Native argument storage range and byte length differ.");
        if (!_nativeEntryOwners.TryGetValue(argument, out var owner)) _nativeEntryOwners.Add(argument, owner = new object());
        var shape = argument.Shape ?? ParameterShape(formal) with { ElementLength = !formal.IsArray && formal.Kind == RpgFieldKind.Character ? length : formal.Length,
            Dimension = formal.IsArray ? Math.Max(1, length / ParameterUnitBytes(formal)) : 1 };
        var codecElement = new RpgDsElement { Name = "VALUE", Kind = formal.Kind, Length = formal.Length, Decimals = formal.Decimals,
            OpaqueBytes = formal.OpaqueBytes, StorageLength = length, Stride = length };
        var codecStructure = new RpgDataStructure { Name = "ARGUMENT_MEMORY", NativeStorage = true, Length = length };
        var codec = new NativeView(codecStructure, new byte[length], 0, length, codecElement, false, 1);
        ProgramBuffer Read(int offset, int count)
        {
            var semantic = argument.HasBuffer ? null : argument.Value;
            var current = argument.ToBuffer() ?? semantic as ProgramBuffer;
            if (current is null && semantic is string value) current = new ProgramBuffer(NativeEncoding.GetBytes(value), NativeCcsid);
            if (current is null && !AggregateParameter(formal)) current = new ProgramBuffer(NormalizeNativeValue(codec, semantic, strict: true), NativeCcsid);
            if (current is null || current.Length != length || current.Ccsid != NativeCcsid) throw NativeFailure(105, "Native entry storage changed length or CCSID.");
            return new(current.ToArray().AsSpan(offset, count), NativeCcsid);
        }
        ProgramBuffer Merge(int offset, ProgramBuffer value) { var bytes = Read(0, length).ToArray(); value.ToArray().CopyTo(bytes, offset); return new(bytes, NativeCcsid); }
        object? OuterValue(ProgramBuffer value) => AggregateParameter(formal) || formal.Kind == RpgFieldKind.Character &&
            (formal.OpaqueBytes || argument.HasBuffer || argument.Value is ProgramBuffer)
            ? value : ReadNative(codec with { Bytes = value.ToArray() });
        return new(argument.Storage ?? new(owner, 0, length), NativeCcsid, shape, Read,
            (offset, value) => { _ = argument.Normalize(OuterValue(Merge(offset, value))); }, (offset, value) => argument.Value = OuterValue(Merge(offset, value)), argument.IsReadOnly);
    }

    private static NativeScalarBinding MemoryBinding(ProgramArgument argument, ProgramArgumentMemory memory, NativeView view)
        => new(argument, () => memory.Read(0, Math.Min(view.Width, memory.Capacity)).ToArray(),
            bytes => ValidateNativeMemory(view with { Binding = null }, memory, bytes), bytes => WriteNativeMemory(view with { Binding = null }, memory, bytes),
            new(memory.Storage.Owner, memory.Storage.Offset, Math.Min(view.Width, memory.Capacity)), memory);

    private NativeView AttachNativeBinding(NativeView view, NativeScalarBinding binding)
        => view with { Binding = binding, ArrayStride = view.IsArray && binding.Memory?.Shape is { } shape ? ShapeUnitBytes(shape) : view.ArrayStride };

    private static byte[] ReadNativeMemory(NativeView view, ProgramArgumentMemory memory)
    {
        try
        {
            if (!view.IsArray || view.Stride == view.UnitWidth) return memory.Read(0, view.Width).ToArray();
            var result = new byte[view.Width];
            for (var index = 0; index < view.Count; index++)
                memory.Slice(checked(index * view.Stride), view.Stride).Read(0, view.UnitWidth).ToArray().CopyTo(result, index * view.UnitWidth);
            return result;
        }
        catch (CpfException error) when (error.MessageId == "CPF3C3A") { throw NativeFailure(105, error.Message); }
    }
    private static void ValidateNativeMemory(NativeView view, ProgramArgumentMemory memory, byte[] bytes)
    {
        CheckNativeMemoryRange(view, memory);
        if (!view.IsArray || view.Stride == view.UnitWidth) { memory.Validate(0, new(bytes, memory.Ccsid)); return; }
        var end = checked((view.Count - 1) * view.Stride + view.UnitWidth);
        var image = memory.Read(0, end).ToArray();
        for (var index = 0; index < view.Count; index++) bytes.AsSpan(index * view.UnitWidth, view.UnitWidth).CopyTo(image.AsSpan(index * view.Stride));
        memory.Validate(0, new(image, memory.Ccsid));
    }
    private static void WriteNativeMemory(NativeView view, ProgramArgumentMemory memory, byte[] bytes)
    {
        CheckNativeMemoryRange(view, memory);
        if (!view.IsArray || view.Stride == view.UnitWidth) { memory.Write(0, new(bytes, memory.Ccsid)); return; }
        var end = checked((view.Count - 1) * view.Stride + view.UnitWidth);
        var image = memory.Read(0, end).ToArray();
        for (var index = 0; index < view.Count; index++) bytes.AsSpan(index * view.UnitWidth, view.UnitWidth).CopyTo(image.AsSpan(index * view.Stride));
        memory.Write(0, new(image, memory.Ccsid));
    }

    private static void CheckNativeMemoryRange(NativeView view, ProgramArgumentMemory memory)
    {
        var end = view.IsArray ? (long)(view.Count - 1) * view.Stride + view.UnitWidth : view.Width;
        if (end > memory.Capacity || view.IsArray && view.Stride < view.UnitWidth)
            throw NativeFailure(105, "Native parameter access exceeds the actual argument storage.");
    }

    internal void EnsureArgumentStorage(NativeView view)
    { EnsureNativeParameterAvailable(view); if (view.Binding?.Memory is { } memory) CheckNativeMemoryRange(view, memory); }

    private void DetachNativeMemory(string name, NativeView view, ProgramArgumentMemory memory)
    {
        var bytes = memory.Read(0, memory.Capacity).ToArray();
        var storage = new ProgramArgumentStorage(new object(), 0, bytes.Length);
        var owned = new ProgramArgumentMemory(storage, NativeCcsid, memory.Shape,
            (offset, count) => new(bytes.AsSpan(offset, count), NativeCcsid), (_, _) => { },
            (offset, value) => value.ToArray().CopyTo(bytes, offset), memory.IsReadOnly);
        var reference = new ProgramArgument(() => new ProgramBuffer(bytes, NativeCcsid), _ => { }, value => value, "*CHAR", bytes.Length, 0,
            () => new(bytes, NativeCcsid), null, storage) { IsReadOnly = memory.IsReadOnly, Memory = owned, Shape = memory.Shape };
        _nativeDetachedBindings[name] = MemoryBinding(reference, owned, view);
        bytes.AsSpan(0, Math.Min(bytes.Length, view.Bytes.Length - view.Offset)).CopyTo(view.Bytes.AsSpan(view.Offset));
    }
}
