using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void ParseArraySort(RpgProgram program, RpgStatement statement, string operands, int line)
    {
        if (operands.Length == 0) throw new RpgCompileException(line, "SORTA requires an array operand.");
        var split = -1; var depth = 0; var quoted = false;
        for (var index = 0; index < operands.Length; index++)
        {
            var current = operands[index];
            if (current == '\'')
            {
                if (quoted && index + 1 < operands.Length && operands[index + 1] == '\'') { index++; continue; }
                quoted = !quoted; continue;
            }
            if (quoted) continue;
            if (current == '(') depth++;
            else if (current == ')') depth--;
            else if (depth == 0 && current == '%' && index > 0 && char.IsWhiteSpace(operands[index - 1]) &&
                operands.AsSpan(index).StartsWith("%FIELDS", StringComparison.OrdinalIgnoreCase)) { split = index; break; }
        }
        RpgExpr operand;
        try
        {
            operand = RpgExpressionParser.Parse(split < 0 ? operands : operands[..split]);
            if (split >= 0)
            {
                var options = DeclarationKeywords(operands[(split + 1)..], line);
                if (options.Count != 1 || options[0] is not { Name: "FIELDS", Arguments: { } arguments })
                    throw new RpgCompileException(line, "%FIELDS requires one parenthesized list of scalar subfield names.");
                var fields = DeclarationArguments(arguments, line).Select(f => f.ToUpperInvariant()).ToArray();
                if (fields.Length is < 1 or > 1024 || fields.Any(name => !RpgWorkstationBindings.Name(name)))
                    throw new RpgCompileException(line, "%FIELDS requires 1 through 1024 unqualified scalar subfield names.");
                statement.SortFields = fields;
            }
        }
        catch (RpgCompileException error) when (error.LineNumber == 0) { throw new RpgCompileException(line, error.Message); }
        var selected = operand is RpgBuiltin { Name: "SUBARR", Arguments.Count: > 0 } slice ? slice.Arguments[0] : operand;
        if (selected is RpgFieldRef table && program.FindField(table.Name) is { IsTable: true })
            throw new RpgCompileException(line, "SORTA requires an array; a table name denotes its current element.");
        if (statement.SortFields.Count > 0)
        {
            var ds = selected is RpgFieldRef root ? program.FindDs(root.Name) : null;
            if (ds is not { NativeStorage: true, IsArray: true, Qualified: true })
                throw new RpgCompileException(line, "%FIELDS requires an array data structure, optionally selected with %SUBARR.");
            foreach (var field in statement.SortFields)
                if (ds.Find(field) is not { IsArray: false }) throw new RpgCompileException(line, "%FIELDS requires scalar subfields of its array data structure: " + field + ".");
        }
        else if (selected is RpgElementRef { Target: RpgMemberRef { Target: RpgAllElementsRef } } or RpgMemberRef { Target: RpgAllElementsRef })
        {
            var indexed = selected as RpgElementRef; var key = (RpgMemberRef)(indexed?.Target ?? selected);
            var ds = ((RpgAllElementsRef)key.Target).Target is RpgFieldRef root ? program.FindDs(root.Name) : null;
            if (ds is not { NativeStorage: true, IsArray: true, Qualified: true } || ds.Find(key.Name) is not { } field || field.IsArray != (indexed is not null))
                throw new RpgCompileException(line, "A keyed SORTA requires an array data structure and a scalar or indexed scalar subfield.");
        }
        else if (selected is RpgAllElementsRef || selected is RpgFieldRef name && program.FindDs(name.Name) is { NativeStorage: true, IsArray: true })
            throw new RpgCompileException(line, "SORTA of an array data structure requires a scalar key or %FIELDS.");
        if (statement.SortDescending is { } direction && SortSequence(program, operand) is { } declared && direction != declared)
            throw new RpgCompileException(line, "SORTA direction conflicts with ASCEND/DESCEND on its array.");
        statement.Factor1 = (split < 0 ? operands : operands[..split]).Trim();
    }
}
