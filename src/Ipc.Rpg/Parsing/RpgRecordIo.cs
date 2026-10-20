using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static bool RecordInput(RpgOpcode opcode) => opcode is RpgOpcode.Chain or RpgOpcode.Read or RpgOpcode.ReadP or RpgOpcode.Reade or RpgOpcode.ReadPe;
    private static bool RecordIo(RpgOpcode opcode) => RecordInput(opcode) || opcode is RpgOpcode.Write or RpgOpcode.Update;

    private static bool ParseRecordIo(string text, RpgStatement statement, RpgProgram program)
    {
        if (!RecordIo(statement.Opcode) && statement.Opcode is not (RpgOpcode.ReadC or RpgOpcode.ExFmt)) return false;
        var boundaries = new List<int> { -1 };
        ScanKeyText(text, statement.LineNumber, (i, depth, c) => { if (depth == 0 && char.IsWhiteSpace(c)) boundaries.Add(i); });
        boundaries.Add(text.Length);
        var sequential = statement.Opcode is RpgOpcode.Read or RpgOpcode.ReadP or RpgOpcode.ReadC or RpgOpcode.ExFmt or RpgOpcode.Write or RpgOpcode.Update;
        for (var n = 1; n < boundaries.Count; n++)
        {
            var start = boundaries[n - 1] + 1; var end = boundaries[n];
            if (end == start || sequential && start != 0 || !sequential && start == 0 || end == text.Length) continue;
            var name = text[start..end].ToUpperInvariant();
            if (!program.DiskFiles.Any(f => f.Request.Name == name || f.Layout.Record == name) &&
                !program.Workstations.Any(f => f.Request.Name == name || f.Layout.Records.Any(r => r.Name == name))) continue;
            statement.RecordDataStructure = text[end..].Trim();
            statement.Factor1 = sequential ? null : text[..start].Trim(); statement.Factor2 = name; return true;
        }
        return false;
    }

    private static void ValidateRecordIo(RpgProgram program)
    {
        foreach (var statement in program.MainStatements)
        {
            if (statement.NativeOperation is not null && (RecordIo(statement.Opcode) || statement.Opcode is RpgOpcode.ReadC or RpgOpcode.ExFmt) && statement.RecordDataStructure is null && statement.Result is not null)
            { statement.RecordDataStructure = statement.Result; statement.Result = null; }
            if (statement.RecordDataStructure is not { } text) continue;
            var fileName = (statement.Factor2 ?? statement.Factor1)?.Trim().ToUpperInvariant();
            var file = program.FindDisk(fileName);
            if (file is null && program.FindWorkstation(fileName) is { } workstation)
            { ValidateWorkstationRecordIo(program, statement, workstation, fileName!); continue; }
            if (!RecordIo(statement.Opcode) || file is null)
                throw new RpgCompileException(statement.LineNumber, "Result data structures require a declared DISK or WORKSTN record operation.");
            RpgExpr expression;
            try { expression = RpgExpressionParser.Parse(text); }
            catch (RpgCompileException error) { throw new RpgCompileException(statement.LineNumber, error.Message); }
            var actual = ExactActualType(program, expression);
            var structure = actual?.ParameterStructure;
            if (structure is not { NativeStorage: true, IsTemplate: false } || actual!.IsArray || structure.Length != file.Layout.RecordLength ||
                structure.RecordOrigin is not { } origin || !RecordMatches(origin, file, statement.Opcode))
                throw new RpgCompileException(statement.LineNumber, "A result data structure must derive from the matching LIKEREC record and permitted extract type; arrays require one element.");
            if (RecordInput(statement.Opcode) && actual!.ReadOnlyParameter)
                throw new RpgCompileException(statement.LineNumber, "A CONST parameter cannot receive record input.");
        }
    }
    internal static bool RecordMatches(RpgRecordOrigin origin, RpgDiskBinding file, RpgOpcode opcode)
    {
        if (origin.NullMap || (origin.External is { } external ? !ExternalDiskMatches(external, file) : (origin.Family ?? origin.File + ":" + origin.Signature) != RpgDiskBindings.FamilyKey(file)) || origin.Extract is not ("*INPUT" or "*OUTPUT" or "*ALL")) return false;
        return opcode == RpgOpcode.Update || RecordInput(opcode) && origin.Extract is "*INPUT" or "*ALL" ||
            opcode == RpgOpcode.Write && (origin.Extract is "*OUTPUT" or "*ALL" || origin.ImplicitInput);
    }
}
