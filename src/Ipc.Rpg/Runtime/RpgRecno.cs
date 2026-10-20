using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal (RpgRuntimeContext Owner, RpgDiskBinding Binding)? RecnoFile(string name)
    {
        var binding = _program.FindDisk(name);
        if (binding is null) return null;
        if (FileReference(binding.Request.Name) is { } reference) return (reference.Owner, reference.Binding);
        return (binding.Request.Procedure is null ? ModuleContext : this, binding);
    }
    internal Action? PrepareRecno(string file, long? number)
    {
        if (number is null || RecnoFile(file) is not { } actual || !actual.Binding.Request.Input || actual.Binding.Request.Recno is not { } name) return null;
        var owner = actual.Owner;
        if (owner.TryNativeReference(RpgExpressionParser.Parse(name), out var view))
        {
            var image = owner.NormalizeNative(view, number.Value, strict: true);
            return () => WriteNativeBytes(view, image);
        }
        var value = owner.NormalizeFileValue(name, number.Value);
        return () => owner.WriteValue(name, value);
    }
}

public sealed partial class RpgInterpreter
{
    private static RpgFileHandle OpenWithRecno(RpgRuntimeContext context, string name, Func<RpgFileHandle> open)
    {
        var feedback = context.PrepareRecno(name, 0);
        var handle = open();
        try { feedback?.Invoke(); return handle; }
        catch { handle.Close(); throw; }
    }
}
