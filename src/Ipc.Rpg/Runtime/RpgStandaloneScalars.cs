using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private readonly Dictionary<ProgramArgument, object> _nativeEntryOwners = new(ReferenceEqualityComparer.Instance);

    private void BindNativeScalar(RpgField field, ProgramArgument argument)
    {
        if (!TryNativeReference(new RpgFieldRef(field.Name), out var view)) throw new RpgRuntimeException("Missing native scalar storage.");
        var expected = field.Kind switch
        {
            RpgFieldKind.Character => "*CHAR", RpgFieldKind.Packed => "*DEC", RpgFieldKind.Zoned => "*ZONED",
            RpgFieldKind.Integer => "*INT", RpgFieldKind.Float => "*FLT", RpgFieldKind.Indicator => "*LGL",
            RpgFieldKind.Date => "*DATE", RpgFieldKind.Time => "*TIME", RpgFieldKind.Timestamp => "*TIMESTAMP", RpgFieldKind.ProcedurePointer => "*PROCPTR", RpgFieldKind.DataPointer => "*PTR", _ => null
        };
        var length = field.Kind == RpgFieldKind.Integer ? view.Width : field.Length;
        if (argument.Type is not null && (argument.Type != expected || argument.Length != length || argument.Decimals != field.Decimals) ||
            argument.Storage is { } range && range.Length != view.Width)
            throw new RpgRuntimeException("CL/RPG reference parameter types or lengths do not match.");

        byte[] Read()
        {
            var buffer = argument.ToBuffer(); var value = buffer is null ? argument.Value : buffer;
            if (value is ProgramBuffer raw)
            {
                if (raw.Length != view.Width || raw.Ccsid != NativeCcsid) throw new RpgRuntimeException("Native entry buffer width or CCSID does not match.");
                return raw.ToArray();
            }
            // Reads and final detachment must remain available after cancellation.
            return NormalizeNativeValue(view, value, strict: true);
        }
        object? OuterValue(byte[] bytes) => field.Kind == RpgFieldKind.Character &&
            (field.OpaqueBytes || argument.ToBuffer() is not null || argument.Value is ProgramBuffer)
                ? new ProgramBuffer(bytes, NativeCcsid) : ReadNative(view with { Bytes = bytes, Offset = 0 });

        var initial = Read();
        if (!_nativeEntryOwners.TryGetValue(argument, out var owner)) _nativeEntryOwners.Add(argument, owner = new object());
        var storage = argument.Storage ?? new ProgramArgumentStorage(owner, 0, view.Width);
        var binding = new NativeScalarBinding(argument, Read, bytes => { _ = argument.Normalize(OuterValue(bytes)); },
            bytes => argument.Value = OuterValue(bytes), storage, ArgumentMemory(argument, field));
        initial.CopyTo(view.Bytes, view.Offset);
        _nativeScalarBindings.Add(field.Name, binding); _references.Add(field.Name, argument);
    }
}
