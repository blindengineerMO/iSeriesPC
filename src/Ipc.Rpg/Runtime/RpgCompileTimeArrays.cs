using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private RpgRuntimeException CompileTimeError(RpgDsElement element, int index, RpgRuntimeException error)
    {
        var line = index < element.CompileTimeLines.Count ? element.CompileTimeLines[index] : element.DeclarationLine;
        return _program.Source is { } source && line > 0 && line <= source.Locations.Count
            ? new(source.Locations[line - 1], error.Message) { StatusCode = error.StatusCode }
            : error;
    }
    private void ValidateCompileTimeCharacterSequences()
    {
        foreach (var field in _program.Fields.Where(f => f.CompileTimeArray && f.Kind == RpgFieldKind.Character && f.ArrayDescending is not null))
        {
            var view = field.IsTable ? TableStorage(field.Name) : ArrayView(new RpgFieldRef(field.Name)); var previous = ArrayKey(NativeIndex(view, 1));
            for (var index = 1; index < view.Count; index++)
            {
                _host.CancellationToken.ThrowIfCancellationRequested(); var value = ArrayKey(NativeIndex(view, index + 1)); var compared = CompareArrayValues(previous, value);
                if (field.ArrayDescending == true ? compared < 0 : compared > 0)
                    throw CompileTimeError(view.Element!, index, new("Compile-time character data does not follow its declared sequence in the activation CCSID.") { StatusCode = 1052 });
                previous = value;
            }
        }
    }
}
