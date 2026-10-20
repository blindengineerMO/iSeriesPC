using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static bool ErrorRoutineName(string name) => name.Equals("*PSSR", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    private static void ParseFileErrorHandlers(RpgProgram program, List<WorkstationDeclaration> declarations)
    {
        for (var i = 0; i < declarations.Count; i++)
        {
            var declaration = declarations[i]; var keywords = DeclarationKeywords(declaration.Keywords, declaration.Line);
            var handlers = keywords.Where(k => k.Name == "INFSR").ToArray();
            if (handlers.Length == 0) continue;
            if (handlers.Length != 1 || !ErrorRoutineName(handlers[0].Arguments ?? ""))
                throw new RpgCompileException(declaration.Line, "INFSR requires one subroutine name or *PSSR.");
            program.FileErrorHandlers.Add(new(declaration.Name, handlers[0].Arguments!.ToUpperInvariant(), declaration.Line));
            declarations[i] = declaration with { Keywords = string.Join(' ', keywords.Where(k => k.Name != "INFSR")
                .Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")"))) };
        }
    }
    private static void ValidateErrorSubroutines(RpgProgram program)
    {
        foreach (var binding in program.FileErrorHandlers)
            if (program.FindSubroutine(binding.Routine) is null)
                throw new RpgCompileException(binding.DeclarationLine, "INFSR requires a subroutine in the main procedure: " + binding.Routine);
        foreach (var routine in program.Subroutines)
        {
            var ending = routine.Ending!;
            if (!ErrorRoutineName(routine.Name)) throw new RpgCompileException(ending.LineNumber, "Invalid subroutine name: " + routine.Name);
            var errorRoutine = routine.Name == "*PSSR" || routine.Procedure is null && program.FileErrorHandlers.Any(f => f.Routine == routine.Name);
            if (ending.Factor2 is { Length: > 0 } point)
            {
                if (!errorRoutine || routine.Procedure is not null || program.IsNativeProcedure) throw new RpgCompileException(ending.LineNumber, "ENDSR return points require a main-procedure error subroutine.");
                RpgExpr expression;
                try { expression = RpgExpressionParser.Parse(point); }
                catch (RpgCompileException error) { throw new RpgCompileException(ending.LineNumber, error.Message); }
                if (expression is RpgLiteral literal)
                {
                    if (literal.Value is not string text || text.TrimEnd() is not ("" or "*CANCL"))
                        throw new RpgCompileException(ending.LineNumber, "Only blank and '*CANCL' ENDSR return points are supported; cycle return points require the native RPG cycle.");
                }
                else if (!ReturnPointField(program, expression)) throw new RpgCompileException(ending.LineNumber, "ENDSR return point requires a six-byte CHAR field or indexed element.");
            }
            // Error routines may branch back into their own procedure, but never into a monitor region.
            if (errorRoutine)
            {
                var body = routine.Procedure is null ? program.MainStatements : program.FindSubprocedure(routine.Procedure)!.Statements;
                foreach (var branch in routine.Statements.Where(s => s.Opcode == RpgOpcode.Goto && !routine.Statements.Any(t => t.Opcode == RpgOpcode.Tag && string.Equals(t.Factor1, s.Factor1, StringComparison.OrdinalIgnoreCase))))
                {
                    var target = body.FirstOrDefault(s => s.Opcode == RpgOpcode.Tag && string.Equals(s.Factor1, branch.Factor1, StringComparison.OrdinalIgnoreCase));
                    if (target is null || target.Monitor >= 0 || InMonitorHandler(body, target))
                        throw new RpgCompileException(branch.LineNumber, "Error-subroutine GOTO requires a label outside MONITOR regions in the same procedure.");
                }
            }
        }
        foreach (var procedure in program.Subprocedures)
            foreach (var statement in procedure.Statements.Concat(program.Subroutines.Where(s => s.Procedure == procedure.Name).SelectMany(s => s.Statements)))
            {
                if (!ErrorFileOperation(statement.Opcode)) continue;
                var operand = (statement.Factor2 ?? statement.Factor1 ?? "").ToUpperInvariant();
                var file = program.FindDisk(operand)?.Request.Name ??
                    program.FindWorkstation(operand)?.Request.Name;
                if (program.FileErrorHandlers.Any(f => f.File == file))
                    throw new RpgCompileException(statement.LineNumber, "A subprocedure cannot access a global file with INFSR.");
            }
    }
    private static bool ErrorFileOperation(RpgOpcode opcode) => opcode is RpgOpcode.Open or RpgOpcode.Close or RpgOpcode.Unlock or RpgOpcode.Chain or
        RpgOpcode.Read or RpgOpcode.ReadP or RpgOpcode.Reade or RpgOpcode.ReadPe or RpgOpcode.ReadC or RpgOpcode.SetLl or RpgOpcode.SetGt or
        RpgOpcode.Write or RpgOpcode.Update or RpgOpcode.Delete or RpgOpcode.ExFmt;
    private static bool InMonitorHandler(IReadOnlyList<RpgStatement> statements, RpgStatement target)
    {
        var index = -1; for (var i = 0; i < statements.Count; i++) if (ReferenceEquals(statements[i], target)) { index = i; break; }
        return statements.Select((s, i) => (s, i)).Any(p => p.s.Opcode == RpgOpcode.OnError && p.s.End >= 0 && p.i < index && index <= p.s.End);
    }
    private static bool ReturnPointField(RpgProgram program, RpgExpr expression)
    {
        if (expression is RpgFieldRef field)
        {
            var scalar = program.FindField(field.Name);
            if (scalar is not null) return scalar is { Kind: RpgFieldKind.Character, Length: 6, IsArray: false, OpaqueBytes: false };
            return program.FindDsElement(field.Name) is { Kind: RpgFieldKind.Character, Length: 6, IsArray: false, OpaqueBytes: false };
        }
        if (expression is RpgDynamicIndexRef indexed) return program.FindField(indexed.Name) is { Kind: RpgFieldKind.Character, Length: 6, IsArray: true, OpaqueBytes: false } ||
            program.FindDsElement(indexed.Name) is { Kind: RpgFieldKind.Character, Length: 6, IsArray: true, OpaqueBytes: false };
        if (expression is RpgMemberRef member) return NativeReturnPoint(program, member) is { Kind: RpgFieldKind.Character, Length: 6, IsArray: false, OpaqueBytes: false };
        if (expression is RpgElementRef element && element.Target is RpgMemberRef array)
            return NativeReturnPoint(program, array) is { Kind: RpgFieldKind.Character, Length: 6, IsArray: true, OpaqueBytes: false };
        return false;
    }
    private static RpgDsElement? NativeReturnPoint(RpgProgram program, RpgMemberRef member)
    {
        var ds = member.Target switch
        {
            RpgFieldRef owner => program.FindDs(owner.Name) is { IsArray: false } scalar ? scalar : null,
            RpgDynamicIndexRef owner => program.FindDs(owner.Name) is { IsArray: true } array ? array : null,
            _ => null
        };
        return ds is { NativeStorage: true, Qualified: true } ? ds.Find(member.Name) : null;
    }
}
