using System.Globalization;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void ValidateTemplateCount(RpgProgram program, int line)
    {
        if (program.Fields.Count(f => f.IsTemplate) + program.DataStructures.Count(d => d.IsTemplate) >= 256)
            throw new RpgCompileException(line, "At most 256 visible data templates are supported.");
    }
    private static bool TemplateName(RpgProgram program, string name)
    {
        if (program.FindField(name) is { } field) return field.IsTemplate;
        if (program.FindDs(name) is { } ds) return ds.IsTemplate;
        if (program.FindDsElement(name) is { } element)
            return program.DataStructures.Any(d => d.IsTemplate && d.Elements.Contains(element));
        return TemplateFileSymbol(program, name);
    }
    internal static bool TemplateFileSymbol(RpgProgram program, string name) => program.DiskFiles.Any(f => f.Request.IsTemplate &&
        (f.Request.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || f.Layout.Record.Equals(name, StringComparison.OrdinalIgnoreCase))) ||
        program.Workstations.Any(f => f.Request.IsTemplate && (f.Request.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            f.Layout.Records.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))));
    internal static bool TemplateReference(RpgProgram program, RpgExpr expression, bool allowMetadata = true) => expression switch
    {
        RpgFieldRef field => TemplateName(program, field.Name),
        RpgDynamicIndexRef field => TemplateName(program, field.Name) || TemplateReference(program, field.Index, allowMetadata),
        RpgIndexedRef field => TemplateName(program, field.Name),
        RpgMemberRef field => TemplateReference(program, field.Target, allowMetadata),
        RpgElementRef field => TemplateReference(program, field.Target, allowMetadata) || TemplateReference(program, field.Index, allowMetadata),
        RpgAllElementsRef field => TemplateReference(program, field.Target, allowMetadata),
        RpgUnary unary => TemplateReference(program, unary.Operand, allowMetadata),
        RpgBinary binary => TemplateReference(program, binary.Lhs, allowMetadata) || TemplateReference(program, binary.Rhs, allowMetadata),
        RpgBuiltin builtin => !(allowMetadata && builtin.Name is "SIZE" or "ELEM") && builtin.Arguments.Any(a => TemplateReference(program, a, allowMetadata)),
        RpgProcedureExpression call => call.Arguments.Any(a => TemplateReference(program, a, allowMetadata)), _ => false
    };
    internal static object TemplateMetadata(RpgProgram program, string name, IReadOnlyList<RpgExpr> arguments)
    {
        try
        {
            var number = TemplateMetadataValue(program, name, arguments, 0);
            return number <= int.MaxValue ? (object)(int)number : (long)number;
        }
        catch (RpgCompileException error) { throw new RpgRuntimeException(error.Message) { StatusCode = 105 }; }
    }
    private static decimal TemplateMetadataValue(RpgProgram program, string name, IReadOnlyList<RpgExpr> arguments, int line)
    {
        string Text(RpgExpr expression) => expression switch
        {
            RpgFieldRef field => field.Name, RpgMemberRef field => Text(field.Target) + "." + field.Name,
            RpgIndexedRef field => field.Name + "(" + field.Index.ToString(CultureInfo.InvariantCulture) + ")",
            RpgDynamicIndexRef field => field.Name + "(" + Text(field.Index) + ")",
            RpgElementRef field => Text(field.Target) + "(" + Text(field.Index) + ")",
            RpgUnary { Op: "+" or "-" } unary => unary.Op + Text(unary.Operand),
            RpgLiteral { Value: not null } literal when RpgValues.IsNumeric(literal.Value) => Convert.ToString(literal.Value, CultureInfo.InvariantCulture)!,
            _ => throw new RpgCompileException(line, "Template layout inquiries require a declared layout and constant subscripts.")
        };
        return RpgValues.ToDecimal(DeclarationValue(program, "%" + name + "(" + string.Join(':', arguments.Select(Text)) + ")", line));
    }

    private static void ValidateTemplateUses(RpgProgram program, IReadOnlyList<KeyDeclaration> keys)
    {
        if (!program.Fields.Any(f => f.IsTemplate) && !program.DataStructures.Any(d => d.IsTemplate) && !program.DiskFiles.Any(f => f.Request.IsTemplate) && !program.Workstations.Any(f => f.Request.IsTemplate)) return;
        foreach (var key in keys) foreach (var field in key.Fields) ValidateTemplateText(program, field, key.Line);
        foreach (var statement in program.MainStatements)
        {
            if (statement.Opcode is RpgOpcode.Goto or RpgOpcode.Tag or RpgOpcode.BegSr or RpgOpcode.EndSr or RpgOpcode.Exsr or
                RpgOpcode.BegProc or RpgOpcode.EndProc or RpgOpcode.Plist or RpgOpcode.Klist or RpgOpcode.Open or RpgOpcode.Close or RpgOpcode.Force or RpgOpcode.Unlock) continue;
            var file = statement.Opcode is RpgOpcode.Chain or RpgOpcode.Read or RpgOpcode.ReadP or RpgOpcode.Reade or RpgOpcode.ReadPe or RpgOpcode.ReadC or
                RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Write or RpgOpcode.Update or RpgOpcode.Delete or RpgOpcode.ExFmt;
            var keyOperation = statement.Opcode is RpgOpcode.Chain or RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Reade or RpgOpcode.ReadPe ||
                statement.Opcode == RpgOpcode.Delete && !string.IsNullOrWhiteSpace(statement.Factor2);
            foreach (var text in new[] { !file || keyOperation ? statement.Factor1 : null, !file ? statement.Factor2 : null, statement.Result, statement.RecordDataStructure, statement.Value }.OfType<string>().Distinct())
                ValidateTemplateText(program, text, statement.LineNumber);
        }
        foreach (var list in program.NativeParameterLists)
            foreach (var name in list.Value)
                if (TemplateName(program, name)) throw new RpgCompileException(program.FindField(name)?.DeclarationLine ?? 1, "A TEMPLATE cannot supply PLIST argument storage.");
    }
    private static void ValidateTemplateText(RpgProgram program, string text, int line)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\'') { SkipLiteral(text, ref index); continue; }
            if (char.IsAsciiDigit(text[index]) || text[index] == '.' && index + 1 < text.Length && char.IsAsciiDigit(text[index + 1]))
            {
                while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] == '.')) index++;
                if (index < text.Length && text[index] is 'e' or 'E')
                {
                    index++;
                    if (index < text.Length && text[index] is '+' or '-') index++;
                    while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
                }
                index--; continue;
            }
            if (!char.IsAsciiLetter(text[index]) && text[index] != '_') continue;
            var start = index; while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '_')) index++;
            var name = text[start..index]; var end = index;
            if (name.Equals("X", StringComparison.OrdinalIgnoreCase) && index < text.Length && text[index] == '\'')
            { SkipLiteral(text, ref index); continue; }
            while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
            if (start > 0 && text[start - 1] == '%')
            {
                if (name.ToUpperInvariant() is "OPEN" or "EOF" or "FOUND" or "EQUAL" or "STATUS" && end < text.Length && text[end] == '(')
                {
                    var argument = System.Text.RegularExpressions.Regex.Match(text[end..], @"\A\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)");
                    if (argument.Success && TemplateFileSymbol(program, argument.Groups[1].Value))
                        throw new RpgCompileException(line, "A TEMPLATE file cannot supply runtime file status.");
                }
                if (name.ToUpperInvariant() is "SIZE" or "ELEM" && end < text.Length && text[end] == '(')
                {
                    var depth = 1; var close = end + 1;
                    for (; close < text.Length; close++)
                    { if (text[close] == '\'') { SkipLiteral(text, ref close); continue; } if (text[close] == '(') depth++; if (text[close] == ')' && --depth == 0) break; }
                    if (close >= text.Length) throw new RpgCompileException(line, "Unclosed template layout inquiry.");
                    var inquiry = text[(start - 1)..(close + 1)];
                    if (RpgExpressionParser.Parse(inquiry) is RpgBuiltin builtin && TemplateReference(program, builtin, false))
                        _ = TemplateMetadataValue(program, builtin.Name, builtin.Arguments, line);
                    index = close; continue;
                }
                index--; continue;
            }
            var previous = start - 1;
            while (previous >= 0 && char.IsWhiteSpace(text[previous])) previous--;
            var upper = name.ToUpperInvariant();
            var indicator = upper.Length == 4 && upper.StartsWith("IN", StringComparison.Ordinal) && char.IsAsciiDigit(upper[2]) && char.IsAsciiDigit(upper[3]);
            if (previous >= 0 && text[previous] == '.' || start > 0 && text[start - 1] == '*' && (indicator || upper is "ALL" or "IN" or "INLR" or "ON" or "OFF" or "YES" or "NO" or "HIVAL" or "LOVAL" or "ZERO" or "ZEROS" or "BLANK" or "BLANKS" or "OMIT" or "DATE" or "TIME" or "TIMESTAMP")) { index--; continue; }
            if (TemplateName(program, name)) throw new RpgCompileException(line, "A TEMPLATE definition or subfield has no runtime data: " + name + ".");
            index--;
        }
    }
    private static void SkipLiteral(string text, ref int index)
    { while (++index < text.Length) if (text[index] == '\'') { if (index + 1 < text.Length && text[index + 1] == '\'') index++; else break; } }
}
