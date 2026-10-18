using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal bool VariableTransport(NativeView view) => (view.IsArray || view.Element is null || view.Element.Kind == RpgFieldKind.Character) &&
        _program.FindField(view.Structure.ParameterName ?? view.Element?.Name ?? "")?.VariableSizeParameter == true;

    internal static bool MatchingVariable(NativeView actual, RpgField formal)
    {
        if (actual.IsArray != formal.IsArray) return false;
        if (formal.IsDataStructure) return actual.Element is null or { Kind: RpgFieldKind.Character };
        if (formal.Kind == RpgFieldKind.Character) return actual.Element is { Kind: RpgFieldKind.Character } element && element.OpaqueBytes == formal.OpaqueBytes;
        return actual.Element is { } item && item.Kind == formal.Kind && item.Length == formal.Length && item.Decimals == formal.Decimals && item.OpaqueBytes == formal.OpaqueBytes;
    }

    private void BindVariableParameter(RpgField field, ProgramArgument argument)
    {
        if (argument.Type is not (null or "*CHAR" or "*RAW")) throw NativeFailure(105, "Variable-size storage requires a character or aggregate byte address.");
        if (!TryNativeReference(new RpgFieldRef(field.Name), out var view)) throw NativeFailure(105, "Missing variable parameter storage.");
        var memory = ArgumentMemory(argument, field);
        if (memory.Capacity is < 1 or > 1048576 || memory.Ccsid != NativeCcsid) throw NativeFailure(105, "Invalid variable parameter capacity or CCSID.");
        if (memory.Shape is { } shape && (!field.IsDataStructure && (shape.ElementType != field.Kind.ToString() || shape.OpaqueBytes != field.OpaqueBytes ||
            field.Kind != RpgFieldKind.Character && (shape.ElementLength != field.Length || shape.Decimals != field.Decimals)) || ShapeUnitBytes(shape) < 1))
            throw NativeFailure(105, "Variable parameter element types differ.");
        _ = memory.Read(0, Math.Min(memory.Capacity, 1));
        _nativeScalarBindings.Add(field.Name, MemoryBinding(argument, memory, view)); _references.Add(field.Name, argument);
    }

    internal ProgramBuffer VariableBuffer(NativeView view)
    {
        EnsureNativeParameterAvailable(view); var memory = NativeMemory(view);
        if (memory.Capacity < 1) throw NativeFailure(105, "The variable argument address has no actual storage.");
        return memory.Read(0, memory.Capacity);
    }

    internal object? ActualVariableValues(NativeView view)
    {
        var memory = NativeMemory(view); var bytes = VariableBuffer(view).ToArray();
        var shape = memory.Shape ?? ViewShape(view); var width = ShapeUnitBytes(shape);
        if (!view.IsArray) return new ProgramBuffer(bytes, NativeCcsid);
        var count = bytes.Length / width;
        if (count is < 1 or > 32767) throw NativeFailure(105, "Variable array storage has invalid element dimensions.");
        var structure = new RpgDataStructure { Name = "VARIABLE_COPY", NativeStorage = true, Length = shape.Structure ? width : bytes.Length };
        var element = shape.Structure ? null : new RpgDsElement { Name = "VALUE", Kind = Enum.Parse<RpgFieldKind>(shape.ElementType),
            Length = shape.ElementLength, Decimals = shape.Decimals, OpaqueBytes = shape.OpaqueBytes, StorageLength = width, Stride = width, IsArray = true, Dimension = count };
        return ReadNative(new(structure, bytes, 0, bytes.Length, element, true, count, width));
    }

    internal ProgramArgument VariableTemporary(RpgField formal, object? value)
    {
        if (formal.IsArray && value is not object?[]) throw NativeFailure(105, "An array parameter requires an array expression.");
        if (formal.IsArray) value = NormalizeProcedureReturn(formal, value);
        return ConstTemporary(formal, value);
    }

    internal void BindVariableValue(string name, object? value)
    {
        var formal = _program.FindField(name)!; ProgramArgumentShape? shape = null;
        ProgramBuffer image;
        if (value is object?[] items && formal.IsArray)
        {
            if (items.Length is < 1 or > 32767) throw NativeFailure(105, "Invalid actual array dimension.");
            var actual = new RpgField { Name = name, Kind = formal.Kind, Length = formal.Length, Decimals = formal.Decimals, OpaqueBytes = formal.OpaqueBytes,
                IsArray = true, IsDataStructure = formal.IsDataStructure, ParameterStructure = formal.ParameterStructure, Dimension = items.Length };
            image = AggregateTemporary(actual, items, readOnly: false).ToBuffer()!; shape = ParameterShape(actual);
        }
        else image = value switch { ProgramBuffer buffer => buffer, byte[] bytes => new(bytes, NativeCcsid),
            string text when !formal.IsArray => new(NativeEncoding.GetBytes(text), NativeCcsid), _ => throw NativeFailure(105, "Variable entry values require character or array data.") };
        if (image.Length < 1 || image.Ccsid != NativeCcsid) throw NativeFailure(105, "Invalid actual entry byte image.");
        var length = image.Length;
        object? Normalize(object? updated) => updated is ProgramBuffer data && data.Length == length && data.Ccsid == NativeCcsid ? data
            : throw NativeFailure(105, "Variable entry image changed size or CCSID.");
        var argument = new ProgramArgument(() => image, updated => image = (ProgramBuffer)updated!, Normalize, "*CHAR", length, 0, () => image)
            { Shape = shape, IsReadOnly = formal.ReadOnlyParameter };
        BindVariableParameter(formal, argument);
    }
}
