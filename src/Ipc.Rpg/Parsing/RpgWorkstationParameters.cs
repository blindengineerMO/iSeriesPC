using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void DeclareWorkstationParameter(RpgProgram destination, RpgProgram symbols, string name,
        RpgWorkstationBinding parent, int line, bool allocate, bool noPass, bool omit)
    {
        if (parent.Request.IsParameter || !symbols.FileDeclarationLines.TryGetValue(parent.Request.Name, out var parentLine) || parentLine >= line)
            throw new RpgCompileException(line, "LIKEFILE parameter parents must be earlier file definitions.");
        if (parent.Request.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) throw new RpgCompileException(line, "A file parameter cannot shadow its own parent definition.");
        if (!RpgWorkstationBindings.Name(name) || HasLocalDeclaration(destination, name)) throw new RpgCompileException(line, "Invalid or duplicate file parameter.");
        var files = RecordWorkstations(symbols);
        if (allocate && files.Count >= 16 && !files.Any(f => f.Request.Name == name)) throw new RpgCompileException(line, "At most 16 visible WORKSTN files, including file parameters, are supported.");
        var request = parent.Request with { Name = name, LikeFile = parent.Request.Name, IsTemplate = false, Qualified = true, UserOpen = true, IsParameter = true,
            Subfiles = parent.Request.Subfiles.Select(s => s with { RelativeRecord = "" }).ToArray(), Procedure = destination.NativeProcedureName, Static = false };
        var definition = new RpgWorkstationBinding(request, RpgWorkstationBindings.Derive(request, parent.Layout), RpgWorkstationBindings.FamilyKey(parent));
        if (destination.IsNativeProcedure) destination.Fields.RemoveAll(f => destination.GlobalFields.Contains(f) && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        destination.Fields.Add(new() { Name = name, Kind = RpgFieldKind.Character, Length = 0, WorkstationParameter = definition,
            NoPassParameter = noPass, OmitParameter = omit, DeclarationLine = line });
        if (!allocate) return;
        if (!destination.IsNativeProcedure && (files.Any(f => f.Request.Name == name) || RecordFiles(symbols).Any(f => f.Request.Name == name)))
            throw new RpgCompileException(line, "A file parameter collides with a file definition.");
        destination.Workstations.RemoveAll(f => f.Request.Name == name); destination.DeclarationWorkstations.RemoveAll(f => f.Request.Name == name);
        destination.DiskFiles.RemoveAll(f => f.Request.Name == name); destination.DeclarationDiskFiles.RemoveAll(f => f.Request.Name == name);
        destination.Workstations.Add(definition); destination.DeclarationWorkstations.Add(definition);
    }
    private static void ValidateWorkstationActual(RpgProgram program, RpgExpr expression, RpgWorkstationBinding formal, bool omit, int line)
    {
        if (expression is RpgOmittedArgument && omit) return;
        var actual = expression is RpgFieldRef name ? program.Workstations.SingleOrDefault(f => f.Request.Name.Equals(name.Name, StringComparison.OrdinalIgnoreCase)) : null;
        if (actual is null || actual.Request.IsTemplate || RpgWorkstationBindings.FamilyKey(actual) != RpgWorkstationBindings.FamilyKey(formal))
            throw new RpgCompileException(line, "A LIKEFILE actual must be a runtime WORKSTN file from the same parent family.");
    }
}
