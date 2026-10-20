using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private NativeView? NamedNullIndicator(NativeView data)
    {
        if (data.Element is not { } element) return null;
        if (element.NullIndicatorName is { } name)
        {
            var parent = data.Parent!;
            var flag = parent.Layout.Anonymous ? TryNativeReference(new RpgFieldRef(name), out var scalar) ? scalar : throw NativeFailure(105, "Named null indicator storage is unavailable.") : NativeMember(parent, name);
            return element.IsArray ? NativeIndex(flag, (data.Offset - parent.Offset - element.Offset) / element.Stride + 1) : flag;
        }
        if (element.ExternalName is null || data.Parent is not { } owner) return null;
        var mapName = owner.Container?.NullIndicatorName ?? (owner.Container is null ? owner.Layout.NullIndicatorName : null);
        if (mapName is null) return null;
        NativeView map;
        if (owner.Parent is { } container)
        {
            map = NativeMember(container, mapName);
            if (owner.Container!.IsArray) map = NativeIndex(map, (owner.Offset - container.Offset - owner.Container.Offset) / owner.Container.Stride + 1);
        }
        else
        {
            if (!TryNativeReference(new RpgFieldRef(mapName), out map!)) throw NativeFailure(105, "Named null map storage is unavailable.");
            if (owner.Structure.IsArray) map = NativeIndex(map, owner.Offset / owner.Layout.Length + 1);
            else if (owner.Structure.IsMultipleOccurrence) map = map with { Offset = owner.Offset / owner.Layout.Length * map.Layout.Length, Width = map.Layout.Length };
        }
        var member = map.Layout.Elements.Single(e => e.ExternalName == element.ExternalName);
        var result = NativeMember(map, member.LocalName ?? member.Name);
        return result.IsArray && result.Count == 1 ? NativeIndex(result, 1) : result;
    }
}
