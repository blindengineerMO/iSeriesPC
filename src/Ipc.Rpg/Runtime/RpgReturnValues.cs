using Ipc.Core.Work;
using Ipc.Rpg.Model;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal object? NormalizeProcedureReturn(RpgField layout, object? value)
    {
        _host.CancellationToken.ThrowIfCancellationRequested();
        if (!layout.IsArray)
        {
            if (value is object?[]) throw NativeFailure(105, "An array requires an array return declaration.");
            return layout.IsDataStructure ? ReturnStructure(layout, value) : ConstTemporary(layout, value).Value;
        }
        var result = new object?[layout.Dimension]; var source = value as object?[];
        var element = new RpgField { Name = "__RETURN_ELEMENT", Kind = layout.Kind, Length = layout.Length,
            Decimals = layout.Decimals, OpaqueBytes = layout.OpaqueBytes };
        for (var index = 0; index < result.Length; index++)
        {
            _host.CancellationToken.ThrowIfCancellationRequested();
            var missing = source is not null && index >= source.Length;
            var item = missing ? ClearValueFor(layout.Kind) : source is null ? value : source[index];
            result[index] = layout.IsDataStructure ? ReturnStructure(layout, item) : ConstTemporary(element, item).Value;
        }
        _host.CancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private ProgramBuffer ReturnStructure(RpgField layout, object? value)
    {
        if (value is byte[] literal) value = new ProgramBuffer(literal, NativeCcsid);
        if (value is ProgramBuffer buffer && buffer.Ccsid != NativeCcsid) throw NativeFailure(452, "Returned structure CCSIDs differ.");
        if (value is not (string or ProgramBuffer)) throw NativeFailure(105, "Structure returns require character or structure data.");
        var structure = layout.ParameterStructure ?? throw NativeFailure(105, "Missing structure return layout.");
        var image = new NativeView(structure, new byte[structure.Length], 0, structure.Length, null, false, 1);
        return new ProgramBuffer(NormalizeArgumentImage(image, value, strict: false), NativeCcsid);
    }
}
