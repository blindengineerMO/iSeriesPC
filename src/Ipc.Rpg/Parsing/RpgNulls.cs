using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void ParseNullControl(List<string> lines, RpgProgram program, bool free, CancellationToken cancellation)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var declarations = false;
        for (var index = 0; index < lines.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested(); var raw = lines[index]; var text = raw.Trim();
            if (text.Length == 0 || text.Equals("**free", StringComparison.OrdinalIgnoreCase) || text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith('/') || raw.Length > 6 && raw[6] == '*') continue;
            var shift = raw.Length > 6 && raw[..6].All(c => c == ' ' || char.IsAsciiDigit(c)) && char.ToUpperInvariant(raw[6]) == 'H' ? 1 :
                raw.Length > 5 && raw[..5].All(c => c == ' ' || char.IsAsciiDigit(c)) && char.ToUpperInvariant(raw[5]) == 'H' ? 0 : -1;
            if (shift >= 0 && !raw[(6 + shift)..].TrimStart().StartsWith("ALWNULL", StringComparison.OrdinalIgnoreCase) && !raw[(6 + shift)..].TrimStart().StartsWith("NOMAIN", StringComparison.OrdinalIgnoreCase) && !raw[(6 + shift)..].TrimStart().StartsWith("BNDDIR", StringComparison.OrdinalIgnoreCase))
            { declarations = true; continue; }
            var ctl = text.Equals("CTL-OPT", StringComparison.OrdinalIgnoreCase) || text.StartsWith("CTL-OPT ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("CTL-OPT\t", StringComparison.OrdinalIgnoreCase);
            if (!ctl && shift < 0) { declarations = true; continue; }
            var line = index + 1;
            if (program.IsNativeProcedure || declarations || free && shift >= 0) throw new RpgCompileException(line, "Control keywords require global specifications before declarations and cannot repeat.");
            var options = shift >= 0 ? DeclarationKeywords(raw[(6 + shift)..].Trim(), line) : DeclarationKeywords(ReadFreeDeclaration(lines, ref index, line, !free)[7..].Trim(), line);
            if (options.Count == 0 || options.Any(o => !seen.Add(o.Name)) || options.Any(o => o.Name is not ("ALWNULL" or "NOMAIN" or "BNDDIR")) ||
                options.Any(o => o.Name == "ALWNULL" && o.Arguments?.ToUpperInvariant() is not ("*NO" or "*INPUTONLY" or "*USRCTL")) ||
                options.Any(o => o.Name == "NOMAIN" && (o.Arguments is not null || !program.IsModule)))
                throw new RpgCompileException(line, "Supported controls are ALWNULL(*NO | *INPUTONLY | *USRCTL), BNDDIR and module-only NOMAIN.");
            if (options.SingleOrDefault(o => o.Name == "ALWNULL") is { } nulls) program.NullControl = nulls.Arguments!.ToUpperInvariant();
            if (options.SingleOrDefault(o => o.Name == "BNDDIR") is { } directories) program.BindingDirectories = ParseBindingDirectoryControl(directories.Arguments, line);
            program.NoMain |= options.Any(o => o.Name == "NOMAIN"); lines[line - 1] = "";
        }
    }
    private static bool NativeNullOption(RpgProgram program, IReadOnlyList<DeclarationKeyword> options, int line, bool parameter = false)
    {
        var option = options.SingleOrDefault(k => k.Name == "NULLIND");
        if (option is null) return false;
        if (program.NullControl != "*USRCTL" || parameter && option.Arguments is null || option.Arguments is not null && !RpgWorkstationBindings.Name(option.Arguments.ToUpperInvariant()))
            throw new RpgCompileException(line, "NULLIND requires ALWNULL(*USRCTL) and an optional unqualified indicator name; parameter null maps remain unsupported.");
        return true;
    }
    private static void ValidateNullBuiltins(RpgProgram program)
    {
        foreach (var statement in program.MainStatements)
            foreach (var text in new[] { statement.Factor1, statement.Factor2, statement.Result, statement.Value, statement.RecordDataStructure }.OfType<string>().Distinct())
                for (var index = 0; index < text.Length; index++)
                {
                    if (text[index] == '\'') { SkipLiteral(text, ref index); continue; }
                    if (!text.AsSpan(index).StartsWith("%NULLIND", StringComparison.OrdinalIgnoreCase)) continue;
                    var end = index + 8; while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
                    if (end >= text.Length || text[end] != '(') throw new RpgCompileException(statement.LineNumber, "%NULLIND requires one null-capable scalar field or indexed element.");
                    var depth = 1; var close = end + 1;
                    for (; close < text.Length; close++) { if (text[close] == '\'') { SkipLiteral(text, ref close); continue; } if (text[close] == '(') depth++; if (text[close] == ')' && --depth == 0) break; }
                    if (close >= text.Length || RpgExpressionParser.Parse(text[index..(close + 1)]) is not RpgBuiltin { Arguments.Count: 1 } builtin ||
                        program.NullControl != "*USRCTL" || ExactActualType(program, builtin.Arguments[0]) is not { NullCapable: true, IsArray: false, IsDataStructure: false })
                        throw new RpgCompileException(statement.LineNumber, "%NULLIND requires ALWNULL(*USRCTL) and one null-capable scalar or indexed element.");
                    index = close;
                }
    }
}
