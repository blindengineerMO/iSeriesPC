using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

/// <summary>Validate lexical boundaries before statements are moved into separate routines.</summary>
internal static class RpgStructureValidator
{
    private sealed class Block(RpgStatement opening)
    {
        internal RpgStatement Opening { get; } = opening;
        internal bool AlternateSeen { get; set; }
        internal bool IsLoop => Opening.Opcode is RpgOpcode.Do or RpgOpcode.DoU or RpgOpcode.DoW or RpgOpcode.For;
    }

    internal static void Validate(IReadOnlyList<RpgStatement> statements, CancellationToken cancellationToken)
    {
        var blocks = new Stack<Block>();
        RpgStatement? routine = null, procedure = null;
        var routines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var procedures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var statement in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            void Boundary()
            {
                if (blocks.TryPeek(out var open)) throw Error(open.Opening, open.Opening.Opcode + " is not closed before a routine boundary.");
            }
            Block Matching(Func<Block, bool> matches, string diagnostic)
                => blocks.TryPeek(out var block) && matches(block) ? block : throw Error(statement, diagnostic);

            switch (statement.Opcode)
            {
                case RpgOpcode.If:
                case RpgOpcode.Select:
                case RpgOpcode.Do:
                case RpgOpcode.DoU:
                case RpgOpcode.DoW:
                case RpgOpcode.For:
                    if (blocks.Count >= 256) throw Error(statement, "RPG control nesting exceeds 256 blocks.");
                    blocks.Push(new(statement)); break;
                case RpgOpcode.Else:
                    var conditional = Matching(b => b.Opening.Opcode == RpgOpcode.If, "ELSE without a matching IF in this block.");
                    if (conditional.AlternateSeen) throw Error(statement, "Repeated ELSE in an IF block.");
                    conditional.AlternateSeen = true; break;
                case RpgOpcode.EndIf:
                    _ = Matching(b => b.Opening.Opcode == RpgOpcode.If, "ENDIF without a matching IF in this block.");
                    blocks.Pop(); break;
                case RpgOpcode.EndDo:
                    _ = Matching(b => b.IsLoop, "ENDDO without a matching DO/FOR in this block.");
                    blocks.Pop(); break;
                case RpgOpcode.When:
                case RpgOpcode.Other:
                    var selection = Matching(b => b.Opening.Opcode == RpgOpcode.Select, "WHEN/OTHER outside of a SELECT branch boundary.");
                    if (selection.AlternateSeen) throw Error(statement, "WHEN or repeated OTHER after OTHER.");
                    selection.AlternateSeen = statement.Opcode == RpgOpcode.Other; break;
                case RpgOpcode.EndSl:
                    _ = Matching(b => b.Opening.Opcode == RpgOpcode.Select, "ENDSL without a matching SELECT in this block.");
                    blocks.Pop(); break;
                case RpgOpcode.Iter:
                case RpgOpcode.Leave:
                    if (!blocks.Any(b => b.IsLoop)) throw Error(statement, statement.Opcode + " outside of a loop.");
                    break;
                case RpgOpcode.BegSr:
                    Boundary();
                    if (routine is not null) throw Error(statement, "Nested BEGSR is unsupported.");
                    if (!routines.Add(Name(statement))) throw Error(statement, "Duplicate RPG subroutine " + Name(statement) + ".");
                    routine = statement; break;
                case RpgOpcode.EndSr:
                    if (routine is null) throw Error(statement, "ENDSR without a matching BEGSR.");
                    Boundary(); routine = null; break;
                case RpgOpcode.BegProc:
                    Boundary();
                    if (procedure is not null || routine is not null) throw Error(statement, "Nested RPG procedure or procedure inside BEGSR is unsupported.");
                    if (!procedures.Add(Name(statement))) throw Error(statement, "Duplicate RPG procedure " + Name(statement) + ".");
                    procedure = statement; break;
                case RpgOpcode.EndProc:
                    if (procedure is null || routine is not null) throw Error(statement, "Procedure end without a matching procedure boundary.");
                    Boundary();
                    if (!string.IsNullOrWhiteSpace(statement.Factor1) && !statement.Factor1.Equals(procedure.Factor1, StringComparison.OrdinalIgnoreCase))
                        throw Error(statement, "Procedure end name differs from its beginning.");
                    procedure = null; break;
                case RpgOpcode.Tag:
                case RpgOpcode.Goto:
                case RpgOpcode.Exsr:
                    _ = Name(statement); break;
            }
        }
        if (blocks.TryPeek(out var remaining))
            throw Error(remaining.Opening, remaining.Opening.Opcode switch {
                RpgOpcode.If => "IF without a matching ENDIF.",
                RpgOpcode.Select => "SELECT without a matching ENDSL.",
                _ => "DO/FOR without a matching ENDDO."
            });
        if (routine is not null) throw Error(routine, "BEGSR without a matching ENDSR.");
        if (procedure is not null) throw Error(procedure, "Procedure beginning without a matching end.");
    }

    internal static void ValidateLabels(IReadOnlyList<RpgStatement> statements)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var statement in statements.Where(s => s.Opcode == RpgOpcode.Tag))
            if (!labels.Add(Name(statement))) throw Error(statement, "Duplicate RPG TAG " + Name(statement) + ".");
    }

    private static string Name(RpgStatement statement)
        => string.IsNullOrWhiteSpace(statement.Factor1) ? throw Error(statement, statement.Opcode + " requires a name.") : statement.Factor1.Trim();
    private static RpgCompileException Error(RpgStatement statement, string message)
        => statement.Location is { } location ? new(location, message) : new(statement.LineNumber, message);
}
