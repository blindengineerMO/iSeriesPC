using System.Text;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private bool TryExternalFileView(string name, out NativeView view)
    {
        view = null!;
        if (_program.FindDsElement(name) is not { ExternalName: not null, LikeType: RpgExternalField external } || !TryNativeReference(new RpgFieldRef(name), out var member)) return false;
        var codec = new RpgDsElement { Name = name, Kind = external.Kind, Length = external.Length, Decimals = external.Decimals,
            StorageLength = external.StorageLength, Stride = external.StorageLength };
        view = member with { Element = codec, Width = external.StorageLength, IsArray = false, Count = 1, ArrayStride = null }; return true;
    }
    internal object? ReadFileValue(string name) => TryExternalFileView(name, out var view) ? ReadNative(view) : ReadValue(name);
    internal void WriteFileValue(string name, object? value)
    {
        if (TryExternalFileView(name, out var view) || value is ProgramBuffer && TryNativeReference(new RpgFieldRef(name), out view)) { var image = NormalizeNative(view, value, strict: true); WriteNativeBytes(view, image); }
        else WriteValue(name, value);
        SetFileNullIndicator(name, false);
    }
    private ProgramBuffer NormalizeExternalFileValue(NativeView view, object? value)
    {
        if (view.Element!.Kind == RpgFieldKind.Character && value is not ProgramBuffer)
        {
            try
            {
                var encoded = NativeEncoding.GetBytes(RpgValues.ToText(value));
                if (encoded.Length > view.Width) throw NativeFailure(452, "File input exceeds the native external field's byte width.");
                var padded = Enumerable.Repeat(NativeBlank, view.Width).ToArray(); encoded.CopyTo(padded, 0); value = new ProgramBuffer(padded, NativeCcsid);
            }
            catch (EncoderFallbackException error) { throw NativeFailure(452, error.Message); }
        }
        return new(NormalizeNative(view, value, strict: true), NativeCcsid);
    }
}
