using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void ValidateParameterBuiltins(RpgProgram program)
    {
        var localProcedure = false;
        foreach (var statement in program.MainStatements)
        {
            if (statement.Opcode == RpgOpcode.BegProc) localProcedure = true;
            if (statement.Opcode == RpgOpcode.EndProc) localProcedure = false;
            foreach (var text in new[] { statement.Factor1, statement.Factor2, statement.Result, statement.Value }.OfType<string>().Distinct())
                for (var index = 0; index < text.Length; index++)
                {
                    if (text[index] == '\'') { SkipLiteral(text, ref index); continue; }
                    if (text[index] != '%') continue;
                    var end = index + 1;
                    while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_')) end++;
                    var name = text[(index + 1)..end].ToUpperInvariant();
                    if (name is not ("PARMNUM" or "PASSED" or "OMITTED")) continue;
                    while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
                    if (end >= text.Length || text[end] != '(') throw Invalid(statement);
                    var depth = 1; var close = end + 1;
                    for (; close < text.Length; close++)
                    {
                        if (text[close] == '\'') { SkipLiteral(text, ref close); continue; }
                        if (text[close] == '(') depth++;
                        if (text[close] == ')' && --depth == 0) break;
                    }
                    if (close >= text.Length) throw Invalid(statement);
                    RpgExpr expression;
                    try { expression = RpgExpressionParser.Parse(text[index..(close + 1)]); }
                    catch (RpgCompileException) { throw Invalid(statement); }
                    if (localProcedure || !program.HasNativeEntryInterface || expression is not RpgBuiltin { Arguments.Count: 1 } builtin ||
                        builtin.Arguments[0] is not RpgFieldRef field || !program.NativeParameterLists["/*ENTRY"].Contains(field.Name, StringComparer.OrdinalIgnoreCase) ||
                        name == "PASSED" && program.FindField(field.Name) is { NoPassParameter: false, OmitParameter: false } ||
                        name == "OMITTED" && program.FindField(field.Name)?.OmitParameter != true) throw Invalid(statement);
                    index = close;
                }
        }

        static RpgCompileException Invalid(RpgStatement statement) => new(statement.LineNumber,
            "%PARMNUM requires a parameter name from the current native PI; %PASSED requires OPTIONS(*NOPASS or *OMIT); %OMITTED requires OPTIONS(*OMIT).");
        static void SkipLiteral(string text, ref int index)
        {
            while (++index < text.Length)
                if (text[index] == '\'')
                {
                    if (index + 1 < text.Length && text[index + 1] == '\'') { index++; continue; }
                    break;
                }
        }
    }
}
