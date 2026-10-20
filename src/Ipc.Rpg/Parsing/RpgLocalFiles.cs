using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void ValidateLocalFileVisibility(RpgProgram module)
    {
        var bodies = module.Subprocedures.Where(p => p.NativeBody is not null).Select(p => p.NativeBody!).ToArray();
        var privateNames = bodies.SelectMany(p => p.DiskFiles.Where(f => f.Request.Procedure == p.NativeProcedureName))
            .SelectMany(f => new[] { f.Request.Name, f.Layout.Record }).Concat(bodies.SelectMany(p => p.Workstations.Where(f => f.Request.Procedure == p.NativeProcedureName))
                .SelectMany(f => f.Layout.Records.Select(r => r.Name).Prepend(f.Request.Name))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (privateNames.Count == 0) return;
        foreach (var scope in bodies.Prepend(module))
            foreach (var statement in scope.MainStatements.Concat(scope.Subroutines.SelectMany(s => s.Statements)).Where(s => ErrorFileOperation(s.Opcode)))
            {
                var name = (statement.Factor2 ?? statement.Factor1 ?? "").Trim();
                if (privateNames.Contains(name) && scope.FindDisk(name) is null && !scope.Workstations.Any(f => f.Request.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || f.Layout.Records.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
                    throw new RpgCompileException(statement.LineNumber, "A procedure-local file or record is not visible in this scope: " + name + ".");
            }
    }
}
