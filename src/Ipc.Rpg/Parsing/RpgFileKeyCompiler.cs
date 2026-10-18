using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private sealed record KeyDeclaration(string Name, string? Procedure, int Line, List<string> Fields);
    private static bool KeyName(string text) => text.Length <= 128 && Regex.IsMatch(text, @"\A[A-Za-z_$#@][A-Za-z0-9_$#@]*\z", RegexOptions.CultureInvariant);

    private static (List<KeyDeclaration> Lists, string?[] Scopes) ParseKeyDeclarations(List<string> lines, RpgProgram program, bool globalFree, CancellationToken cancellationToken)
    {
        var lists = new List<KeyDeclaration>(); var scopes = new string?[lines.Count];
        KeyDeclaration? current = null; string? procedure = null; var free = globalFree;
        void EndList()
        {
            if (current is { Fields.Count: 0 }) throw new RpgCompileException(current.Line, "KLIST must be followed by at least one KFLD.");
            current = null;
        }
        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = lines[index]; var line = index + 1; scopes[index] = procedure;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.Trim().Equals("/free", StringComparison.OrdinalIgnoreCase)) free = true;
            if (raw.Trim().Equals("/end-free", StringComparison.OrdinalIgnoreCase)) free = false;
            if (Specification(raw) == 'P')
            {
                EndList(); procedure = raw.Length >= 24 && raw[23] == 'B' ? Col(raw, 8, 21).Trim().ToUpperInvariant() : null;
                continue;
            }
            // Standard RPG IV C-spec positions; the historical interpreter layout
            // remains accepted for existing saved source members.
            var native = raw.Length >= 6 && raw[..5].All(c => c == ' ' || char.IsAsciiDigit(c)) && char.ToUpperInvariant(raw[5]) == 'C' &&
                (raw.Length == 6 || raw[6] is ' ' or 'L' or 'l');
            var opcode = native ? Col(raw, 26, 35).Trim().ToUpperInvariant() : Specification(raw) == 'C' && FixedShape(raw, 'C') ? Col(raw, 36, 42).Trim().ToUpperInvariant() : "";
            if (opcode is not ("KLIST" or "KFLD")) { EndList(); continue; }
            if (free) throw new RpgCompileException(line, "KLIST/KFLD declarations require traditional calculations; use %KDS or a list in free form.");
            var factor1 = (native ? Col(raw, 12, 25) : Col(raw, 18, 35)).Trim();
            var factor2 = (native ? Col(raw, 36, 49) : Col(raw, 43, 47)).Trim();
            var result = (native ? Col(raw, 50, 63) : Col(raw, 48, 58)).Trim();
            var conditions = native ? Col(raw, 9, 11) : Col(raw, 8, 17);
            var trailing = native ? Col(raw, 64, 76) : Col(raw, 59, 70);
            if (conditions.Any(c => c != ' ') || trailing.Any(c => c != ' ') || native && Col(raw, 7, 8).Trim() is { Length: > 0 } level &&
                level != "LR" && !(level.Length == 2 && level[0] == 'L' && char.IsAsciiDigit(level[1])))
                throw new RpgCompileException(line, "KLIST/KFLD cannot have conditioning indicators, lengths or result indicators.");
            if (opcode == "KLIST")
            {
                EndList();
                if (!KeyName(factor1) || factor2.Length != 0 || result.Length != 0 ||
                    program.FindField(factor1) is not null || program.FindDs(factor1) is not null || program.FindDsElement(factor1) is not null ||
                    lists.Any(list => list.Procedure == procedure && list.Name.Equals(factor1, StringComparison.OrdinalIgnoreCase)))
                    throw new RpgCompileException(line, "KLIST requires a unique name in factor 1 and no other operands.");
                current = new(factor1.ToUpperInvariant(), procedure, line, []); lists.Add(current);
                if (lists.Count > 256) throw new RpgCompileException(line, "At most 256 KLIST declarations are supported.");
            }
            else
            {
                if (current is null || factor1.Length != 0 || !KeyName(result)) throw new RpgCompileException(line, "KFLD requires a preceding KLIST and a scalar result-field name.");
                if (factor2.Length != 0) throw new RpgCompileException(line, "KFLD null indicators require ALWNULL(*USRCTL), which is not yet supported.");
                var field = program.FindField(result);
                if (field is { IsArray: true } || program.FindDs(result) is not null || field is null && program.FindDsElement(result) is null ||
                    program.DataStructures.Any(ds => ds.Dimension != 1 && ds.Find(result) is not null))
                    throw new RpgCompileException(line, "KFLD requires a declared scalar field or scalar data-structure subfield.");
                current.Fields.Add(result);
                if (current.Fields.Count > 128) throw new RpgCompileException(line, "KLIST exceeds 128 fields.");
            }
            lines[index] = "";
        }
        EndList(); return (lists, scopes);
    }

    private static void BindFileSearches(RpgProgram program, List<KeyDeclaration> lists, string?[] scopes, CancellationToken cancellationToken)
    {
        foreach (var statement in program.MainStatements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (statement.Opcode is not (RpgOpcode.Chain or RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Reade or RpgOpcode.ReadPe or RpgOpcode.Delete)) continue;
            if (string.IsNullOrWhiteSpace(statement.Factor2)) continue; // legacy no-key read/delete
            if (string.IsNullOrWhiteSpace(statement.Factor1)) continue;
            var text = statement.Factor1.Trim(); var line = statement.LineNumber;
            try
            {
                if (text.Equals("*KEY", StringComparison.OrdinalIgnoreCase))
                {
                    if (statement.Opcode is not (RpgOpcode.Reade or RpgOpcode.ReadPe)) throw new RpgCompileException(line, "*KEY is allowed only for READE/READPE.");
                    statement.FileSearch = new() { Kind = RpgFileSearchKind.Current }; continue;
                }
                var scope = line > 0 && line <= scopes.Length ? scopes[line - 1] : null;
                var list = lists.LastOrDefault(list => list.Name.Equals(text, StringComparison.OrdinalIgnoreCase) && list.Procedure == scope)
                    ?? lists.LastOrDefault(list => list.Name.Equals(text, StringComparison.OrdinalIgnoreCase) && list.Procedure is null);
                if (list is not null)
                { statement.FileSearch = new() { Kind = RpgFileSearchKind.KeyList, Expressions = list.Fields.ToArray() }; continue; }
                if (text.StartsWith("%KDS", StringComparison.OrdinalIgnoreCase))
                {
                    if (!statement.IsFreeForm || !text.StartsWith("%KDS(", StringComparison.OrdinalIgnoreCase) || !text.EndsWith(')'))
                        throw new RpgCompileException(line, "%KDS requires free-form %KDS(data-structure{:count}).");
                    var parts = SplitKeyComponents(text[5..^1], line);
                    if (parts.Count is < 1 or > 2 || !KeyName(parts[0]) || program.FindDs(parts[0]) is not { } ds || ds.Elements.Count == 0)
                        throw new RpgCompileException(line, "%KDS requires a declared nonempty data structure and an optional key count.");
                    if (ds.Dimension != 1) throw new RpgCompileException(line, "Indexed array data structures in %KDS are not yet supported.");
                    if (parts.Count == 2) _ = RpgExpressionParser.Parse(parts[1]);
                    statement.FileSearch = new() { Kind = RpgFileSearchKind.DataStructure, DataStructure = ds.Name, CountExpression = parts.Count == 2 ? parts[1] : null };
                    continue;
                }
                if (text.StartsWith('('))
                {
                    if (!statement.IsFreeForm || !text.EndsWith(')')) throw new RpgCompileException(line, "Compound expression keys require a parenthesized free-form list.");
                    var parts = SplitKeyComponents(text[1..^1], line);
                    foreach (var part in parts) _ = RpgExpressionParser.Parse(part);
                    statement.FileSearch = new() { Kind = RpgFileSearchKind.Values, Expressions = parts }; continue;
                }
                var expression = RpgExpressionParser.Parse(text);
                if (expression is RpgBinary or RpgBuiltin) throw new RpgCompileException(line, "Key expressions must be enclosed in a parenthesized search list.");
                statement.FileSearch = new() { Kind = RpgFileSearchKind.Scalar, Expressions = [text] };
            }
            catch (RpgCompileException error) when (error.LineNumber == 0) { throw new RpgCompileException(line, error.Message); }
        }
    }

    private static List<string> SplitKeyComponents(string text, int line)
    {
        var result = new List<string>(); var start = 0;
        ScanKeyText(text, line, (index, depth, c) => {
            if (c == ':' && depth == 0) { result.Add(text[start..index].Trim()); start = index + 1; }
        });
        result.Add(text[start..].Trim());
        if (result.Count > 128 || result.Any(string.IsNullOrWhiteSpace)) throw new RpgCompileException(line, "Search lists require 1 through 128 nonempty components.");
        return result;
    }
    private static void ScanKeyText(string text, int line, Action<int, int, char> visit)
    {
        if (text.Length > 32768) throw new RpgCompileException(line, "File operands exceed 32768 characters.");
        var depth = 0; var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (c == '\'') { if (quoted && index + 1 < text.Length && text[index + 1] == '\'') { index++; continue; } quoted = !quoted; continue; }
            if (quoted) continue;
            if (c == '(' && ++depth > 32 || c == ')' && --depth < 0) throw new RpgCompileException(line, "Invalid or excessive search-argument nesting.");
            visit(index, depth, c);
        }
        if (quoted || depth != 0) throw new RpgCompileException(line, "Unbalanced search-argument parentheses or literal.");
    }
    private static void ParseKeyFile(string operands, RpgStatement statement, RpgProgram program)
    {
        var text = operands.Trim(); var split = -1;
        ScanKeyText(text, statement.LineNumber, (index, depth, c) => { if (depth == 0 && char.IsWhiteSpace(c)) split = index; });
        var file = split < 0 ? text : text[(split + 1)..].Trim();
        var key = split < 0 ? null : text[..split].Trim();
        if (!KeyName(file)) throw new RpgCompileException(statement.LineNumber, "A single file or record-format name is required.");
        if (statement.Opcode is RpgOpcode.Read or RpgOpcode.ReadP or RpgOpcode.ReadC && key is not null)
        {
            var workstation = program.Workstations.Any(binding => binding.Request.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                binding.Layout.Records.Any(record => record.Name.Equals(key, StringComparison.OrdinalIgnoreCase)));
            throw new RpgCompileException(statement.LineNumber, workstation ? "Unsupported WORKSTN operands; result data structures are not yet supported." :
                "Sequential READ/READP/READC accepts only a file or record name.");
        }
        if (statement.Opcode is RpgOpcode.Chain or RpgOpcode.SetLl or RpgOpcode.SetGt && string.IsNullOrWhiteSpace(key))
            throw new RpgCompileException(statement.LineNumber, "CHAIN/SETLL/SETGT requires a search argument and file name.");
        statement.Factor1 = key; statement.Factor2 = file;
    }
}
