using Ipc.Core.Work;
using Ipc.Rpg.Model;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private bool _entryParametersProtected;
    internal void BeginEntryBinding() { _entryParametersProtected = false; _unpassedParameters.Clear(); _omittedParameters.Clear(); _nativeDetachedBindings.Clear(); InSubprocedure = false; }
    internal void ProtectEntryParameters() => _entryParametersProtected = true;
    private void EnsureNativeWritable(NativeView view)
    {
        if (_program.GlobalStructures.Contains(view.Structure)) { GlobalContext!.EnsureNativeWritable(view); return; }
        EnsureNativeParameterAvailable(view);
        if (_entryParametersProtected && (view.Element?.ReadOnlyParameter == true || view.Structure.ReadOnlyParameter))
            throw NativeFailure(105, "A CONST procedure-interface parameter cannot be changed.");
    }

    internal byte[] NormalizeArgumentImage(NativeView view, object? value, bool strict)
    {
        _host.CancellationToken.ThrowIfCancellationRequested();
        var bytes = NormalizeNativeValue(view, value, strict);
        _host.CancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    internal static bool SameParameterLayout(NativeView view, RpgField formal) => !view.IsArray &&
        formal.Kind == (view.Element?.Kind ?? RpgFieldKind.Character) &&
        formal.Length == (view.Element?.Kind is RpgFieldKind.Integer or RpgFieldKind.Packed or RpgFieldKind.Zoned ? view.Element.Length : view.Width) &&
        formal.Decimals == (view.Element?.Decimals ?? 0) && formal.OpaqueBytes == (view.Element?.OpaqueBytes ?? true);

    internal ProgramArgument ConstTemporary(RpgField formal, object? value, bool readOnly = true)
    {
        if (AggregateParameter(formal)) return AggregateTemporary(formal, value, readOnly);
        var width = formal.Kind switch { RpgFieldKind.Packed => (formal.Length + 2) / 2,
            RpgFieldKind.Integer => formal.Length switch { 3 => 1, 5 => 2, 10 => 4, _ => 8 }, _ => formal.Length };
        var numeric = formal.Kind is RpgFieldKind.Integer or RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Float;
        if (numeric && !RpgValues.IsNumeric(value) || formal.Kind == RpgFieldKind.Character && value is not (string or ProgramBuffer or byte[]) ||
            formal.Kind == RpgFieldKind.Indicator && value is not bool)
            throw NativeFailure(105, "CONST actual value has an incompatible scalar type; use an explicit conversion.");
        if (value is byte[] hex) value = new ProgramBuffer(hex, NativeCcsid);
        if (value is ProgramBuffer encoded && encoded.Ccsid != NativeCcsid) throw NativeFailure(452, "CONST buffer CCSIDs differ; explicit conversion is required.");
        var element = new RpgDsElement { Name = formal.Name, LocalName = formal.Name, Kind = formal.Kind,
            Length = formal.Length, Decimals = formal.Decimals, OpaqueBytes = formal.OpaqueBytes, StorageLength = width, Stride = width };
        var structure = new RpgDataStructure { Name = "CONST", NativeStorage = true, Length = width };
        var image = new NativeView(structure, new byte[width], 0, width, element, false, 1);
        var bytes = NormalizeArgumentImage(image, value, strict: false);
        image = image with { Bytes = bytes };
        var type = formal.Kind switch { RpgFieldKind.Character => "*CHAR", RpgFieldKind.Packed => "*DEC", RpgFieldKind.Zoned => "*ZONED",
            RpgFieldKind.Integer => "*INT", RpgFieldKind.Float => "*FLT", RpgFieldKind.Indicator => "*LGL", RpgFieldKind.Date => "*DATE",
            RpgFieldKind.Time => "*TIME", RpgFieldKind.Timestamp => "*TIMESTAMP", _ => throw NativeFailure(105, "Unsupported CONST type.") };
        ProgramBuffer Buffer() => new(bytes, NativeCcsid);
        object? Normalize(object? updated) => new ProgramBuffer(NormalizeArgumentImage(image, updated, strict: true), NativeCcsid);
        return new(() => formal.OpaqueBytes ? Buffer() : ReadNative(image with { Bytes = bytes }), updated => bytes = ((ProgramBuffer)updated!).ToArray(),
            Normalize, type, formal.Kind == RpgFieldKind.Integer ? width : formal.Length, formal.Decimals, Buffer, null, new(new object(), 0, width)) { IsReadOnly = readOnly };
    }

    internal NativeView NativeCharacterPrefix(NativeView original, int width)
    {
        if (original.IsArray || original.Element is not { Kind: RpgFieldKind.Character } source || width < 1 || width > original.Width)
            throw NativeFailure(105, "A character reference prefix requires enough scalar storage.");
        if (width == original.Width) return original;
        byte[] Read() => NativeBuffer(original).ToArray().AsSpan(0, width).ToArray();
        byte[] Merge(byte[] prefix) { var result = NativeBuffer(original).ToArray(); prefix.CopyTo(result, 0); return result; }
        var range = NativeStorageRange(original); range = range with { Length = width };
        var reference = original.Binding?.Reference ?? new ProgramArgument(() => new ProgramBuffer(Read(), NativeCcsid), _ => { }, v => v)
            { IsReadOnly = source.ReadOnlyParameter };
        var binding = new NativeScalarBinding(reference, Read, data => original.Binding?.Validate(Merge(data)),
            data => WriteNativeBytes(original, Merge(data)), range, NativeMemory(original));
        var element = new RpgDsElement { Name = source.Name, LocalName = source.LocalName, Kind = source.Kind,
            Length = width, StorageLength = width, Stride = width, OpaqueBytes = source.OpaqueBytes, ReadOnlyParameter = source.ReadOnlyParameter };
        return original with { Width = width, Element = element, Binding = binding };
    }
}
