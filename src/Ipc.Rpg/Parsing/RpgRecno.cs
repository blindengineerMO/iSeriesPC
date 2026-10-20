using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void ValidateRecnoFields(RpgProgram program)
    {
        foreach (var file in program.DiskFiles.Where(file => file.Request.Recno is not null && file.Request.Procedure == program.NativeProcedureName))
        {
            var name = file.Request.Recno!; var line = program.FileDeclarationLines.GetValueOrDefault(file.Request.Name);
            var layout = NativeCallLayout(program, RpgExpressionParser.Parse(name));
            if (layout is not { IsArray: false, Constant: false, ReadOnly: false, Decimals: 0 } target ||
                program.FindField(name.Split('.')[0])?.IsTemplate == true || program.FindDs(name.Split('.')[0])?.IsTemplate == true ||
                target.Kind is not (RpgFieldKind.Integer or RpgFieldKind.Binary or RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Decimal))
                throw new RpgCompileException(line, "RECNO requires a defined writable numeric scalar with zero decimals.");
        }
    }
}
