using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal static bool AggregateParameter(RpgField field) => field.IsArray || field.IsDataStructure;
    internal static int ParameterUnitBytes(RpgField field) => field.Kind switch
    { RpgFieldKind.Integer => field.Length switch { 3 => 1, 5 => 2, 10 => 4, _ => 8 }, RpgFieldKind.Packed => (field.Length + 2) / 2, _ => field.Length };
    internal static int ParameterBytes(RpgField field) => checked(ParameterUnitBytes(field) * (field.IsArray ? field.Dimension : 1));
    internal static ProgramArgumentShape ParameterShape(RpgField field) => new(field.IsDataStructure ? "*DS" : field.Kind.ToString(),
        field.Length, field.Decimals, field.IsArray ? field.Dimension : 1, field.IsDataStructure, field.OpaqueBytes);

    private ProgramArgument AggregateTemporary(RpgField formal, object? value, bool readOnly)
    {
        var width = ParameterBytes(formal);
        byte[] bytes;
        if (value is byte[] literal) value = new ProgramBuffer(literal, NativeCcsid);
        if (value is ProgramBuffer buffer)
        {
            if (buffer.Ccsid != NativeCcsid || buffer.Length < width) throw NativeFailure(105, "Aggregate copy requires enough bytes in the native CCSID.");
            bytes = buffer.ToArray().AsSpan(0, width).ToArray();
        }
        else if (formal.IsArray && value is object?[] items && items.Length >= formal.Dimension)
        {
            bytes = new byte[width];
            var element = new RpgField { Name = formal.Name, Kind = formal.Kind, Length = formal.Length, Decimals = formal.Decimals, OpaqueBytes = formal.OpaqueBytes };
            for (var index = 0; index < formal.Dimension; index++)
            {
                _host.CancellationToken.ThrowIfCancellationRequested();
                var data = formal.IsDataStructure ? items[index] is ProgramBuffer raw && raw.Ccsid == NativeCcsid && raw.Length >= formal.Length
                    ? raw.ToArray().AsSpan(0, formal.Length).ToArray() : throw NativeFailure(105, "Structure array copies require compatible structure elements.")
                    : ConstTemporary(element, items[index]).ToBuffer()!.ToArray();
                data.CopyTo(bytes, index * ParameterUnitBytes(formal));
            }
        }
        else if (formal.IsDataStructure && !formal.IsArray && value is string text)
        {
            byte[] data;
            try { data = NativeEncoding.GetBytes(text); }
            catch (System.Text.EncoderFallbackException error) { throw NativeFailure(452, error.Message); }
            if (data.Length > width) throw NativeFailure(105, "Character value exceeds its structure layout.");
            bytes = Enumerable.Repeat(NativeBlank, width).ToArray(); data.CopyTo(bytes, 0);
        }
        else throw NativeFailure(105, "Aggregate copies require a compatible array or byte image.");
        ProgramBuffer Buffer() => new(bytes, NativeCcsid);
        object? Normalize(object? updated) => updated is ProgramBuffer raw && raw.Ccsid == NativeCcsid && raw.Length == width
            ? new ProgramBuffer(raw.ToArray(), NativeCcsid) : throw NativeFailure(105, "Aggregate copy returned an incompatible buffer.");
        return new(() => Buffer(), updated => bytes = ((ProgramBuffer)updated!).ToArray(), Normalize, "*CHAR", width, 0, Buffer, null,
            new(new object(), 0, width)) { IsReadOnly = readOnly, Shape = ParameterShape(formal) };
    }

    private NativeView BindNativeSubview(NativeView parent, NativeView child, int logicalOffset, int? memoryLimit = null, ProgramArgumentShape? shape = null)
    {
        if (parent.Binding is not { } original) return child;
        if (original.Memory is { } memory) return child with { Binding = MemoryBinding(original.Reference, memory.Slice(logicalOffset, memoryLimit, shape), child) };
        byte[] Read() => NativeBuffer(parent).ToArray().AsSpan(logicalOffset, child.Width).ToArray();
        byte[] Merge(byte[] value) { var bytes = NativeBuffer(parent).ToArray(); value.CopyTo(bytes, logicalOffset); return bytes; }
        var range = new ProgramArgumentStorage(original.Storage.Owner, original.Storage.Offset + child.Offset - parent.Offset, child.Width);
        var binding = new NativeScalarBinding(original.Reference, Read, value => original.Validate(Merge(value)), value => WriteNativeBytes(parent, Merge(value)), range);
        return child with { Binding = binding };
    }

    internal NativeView AggregatePrefix(NativeView actual, RpgField formal)
    {
        var width = ParameterBytes(formal);
        if (actual.IsArray)
        {
            if (!formal.IsArray || actual.Count < formal.Dimension || actual.Stride != actual.UnitWidth || actual.UnitWidth != ParameterUnitBytes(formal))
                throw NativeFailure(105, "Aggregate actual dimensions, element widths or contiguity do not match the prototype.");
            var view = actual with { Width = width, Count = formal.Dimension, Binding = null };
            return AggregateReferencePrefix(actual, view);
        }
        if (!formal.IsDataStructure || formal.IsArray || actual.Width < width || actual.Element is { Kind: not RpgFieldKind.Character })
            throw NativeFailure(105, "LIKEDS requires enough scalar structure or character storage.");
        if (actual.Width == width) return actual;
        return AggregateReferencePrefix(actual, actual with { Width = width, Binding = null });
    }

    private NativeView AggregateReferencePrefix(NativeView parent, NativeView child)
    {
        if (parent.Binding is not null) return BindNativeSubview(parent, child, 0);
        if (parent.Width == child.Width) return child;
        var memory = NativeMemory(parent);
        var reference = new ProgramArgument(() => memory.Read(0, parent.Width), _ => { }, value => value) { IsReadOnly = memory.IsReadOnly };
        return child with { Binding = MemoryBinding(reference, memory, child) };
    }

    internal static bool MatchingAggregate(NativeView actual, RpgField formal)
    {
        if (formal.IsArray != actual.IsArray) return false;
        if (formal.IsDataStructure) return (actual.Element is null || actual.Element.Kind == RpgFieldKind.Character) &&
            (formal.IsArray ? actual.UnitWidth == formal.Length : actual.Width >= formal.Length);
        return actual.Element is { } e && e.Kind == formal.Kind && e.Length == formal.Length && e.Decimals == formal.Decimals && e.OpaqueBytes == formal.OpaqueBytes;
    }

    private void BindNativeAggregate(RpgField field, ProgramArgument argument)
    {
        if (!TryNativeReference(new RpgFieldRef(field.Name), out var view)) throw NativeFailure(105, "Missing aggregate parameter storage.");
        if (argument.Type is not (null or "*CHAR" or "*RAW") || argument.Length != 0 && argument.Length != view.Width || argument.Storage is { } range && range.Length != view.Width)
            throw NativeFailure(105, "Aggregate entry buffer width does not match its interface.");
        if (argument.Shape is { } shape && !field.IsDataStructure && shape != ParameterShape(field)) throw NativeFailure(105, "Aggregate element metadata differs from the interface.");
        byte[] Read()
        {
            var buffer = argument.ToBuffer() ?? argument.Value as ProgramBuffer;
            if (buffer is null || buffer.Length != view.Width || buffer.Ccsid != NativeCcsid) throw NativeFailure(105, "Aggregate entry requires an exact native buffer and CCSID.");
            return buffer.ToArray();
        }
        object? Normalize(byte[] bytes) => argument.Normalize(new ProgramBuffer(bytes, NativeCcsid));
        var initial = Read();
        if (!_nativeEntryOwners.TryGetValue(argument, out var owner)) _nativeEntryOwners.Add(argument, owner = new object());
        var storage = argument.Storage ?? new ProgramArgumentStorage(owner, 0, view.Width);
        var binding = new NativeScalarBinding(argument, Read, bytes => { _ = Normalize(bytes); }, bytes => argument.Value = new ProgramBuffer(bytes, NativeCcsid), storage, ArgumentMemory(argument, field));
        initial.CopyTo(view.Bytes, view.Offset); _nativeScalarBindings.Add(field.Name, binding); _references.Add(field.Name, argument);
    }

    internal void InitializeAggregateParameter(string name, object? value)
    {
        var formal = _program.FindField(name)!;
        var image = AggregateTemporary(formal, value, readOnly: false).ToBuffer()!;
        if (!TryNativeReference(new RpgFieldRef(name), out var view)) throw NativeFailure(105, "Missing aggregate parameter storage.");
        WriteNativeBytes(view, image.ToArray());
    }
}
