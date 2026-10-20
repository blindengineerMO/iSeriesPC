using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal RpgFileReference CaptureFileArgument(RpgExpr expression, RpgWorkstationBinding formal)
    {
        var actual = expression is RpgFieldRef name ? _program.Workstations.SingleOrDefault(f => f.Request.Name.Equals(name.Name, StringComparison.OrdinalIgnoreCase)) : null;
        if (actual is null || actual.Request.IsTemplate || RpgWorkstationBindings.FamilyKey(actual) != RpgWorkstationBindings.FamilyKey(formal))
            throw NativeFailure(105, "A LIKEFILE actual must be a runtime WORKSTN file from the same parent family.");
        return FileReference(actual.Request.Name) ?? new(actual.Request.Procedure is null ? ModuleContext : this, actual);
    }
    internal static bool WorkstationCompatible(RpgWorkstationBinding formal, RpgWorkstationBinding actual)
    {
        if (formal.Request.Input != actual.Request.Input || formal.Request.Output != actual.Request.Output ||
            formal.Request.Prefix != actual.Request.Prefix || !formal.Request.Renames.SequenceEqual(actual.Request.Renames) ||
            formal.Layout.IndicatorArea != actual.Layout.IndicatorArea || formal.Layout.Records.Count != actual.Layout.Records.Count) return false;
        return formal.Layout.Records.Zip(actual.Layout.Records).All(pair => pair.First.External == pair.Second.External &&
            pair.First.Subfile == pair.Second.Subfile && pair.First.MessageSubfile == pair.Second.MessageSubfile && pair.First.Capacity == pair.Second.Capacity &&
            pair.First.Fields.SequenceEqual(pair.Second.Fields) &&
            (pair.First.ControlFor is null ? pair.Second.ControlFor is null : pair.Second.ControlFor is not null &&
                formal.Layout.Records.Single(r => r.Name == pair.First.ControlFor).External == actual.Layout.Records.Single(r => r.Name == pair.Second.ControlFor).External));
    }
}
