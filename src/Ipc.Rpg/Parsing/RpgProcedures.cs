using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private sealed record NativeProcedureSource(string Name, int Line, bool Export, List<string> Lines);
    private static bool HasLocalDeclaration(RpgProgram program, string name) =>
        program.Fields.Any(f => !program.GlobalFields.Contains(f) && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
        program.DataStructures.Any(d => !program.GlobalStructures.Contains(d) && (d.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            d.Elements.Any(e => !e.Anonymous && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))));

    private static void ImportGlobalSymbols(RpgProgram program, RpgProgram globals)
    {
        program.Fields.AddRange(globals.Fields); program.GlobalFields.UnionWith(globals.Fields);
        program.DataStructures.AddRange(globals.DataStructures); program.GlobalStructures.UnionWith(globals.DataStructures);
        program.Prototypes.AddRange(globals.Prototypes);
        program.NullControl = globals.NullControl;
        program.ExternalDescriptionLookup = globals.ExternalDescriptionLookup;
        program.DeclarationDiskFiles.AddRange(globals.DeclarationDiskFiles);
        program.RecordLayoutsLookup = globals.RecordLayoutsLookup;
        program.DeclarationWorkstations.AddRange(globals.DeclarationWorkstations);
        program.WorkstationLayoutsLookup = globals.WorkstationLayoutsLookup;
        foreach (var file in globals.FileDefinitionKeywords) program.FileDefinitionKeywords[file.Key] = file.Value;
        foreach (var file in globals.FileDeclarationLines) program.FileDeclarationLines[file.Key] = file.Value;
        program.UnblockedFiles.UnionWith(globals.UnblockedFiles);
        program.Workstations.AddRange(globals.Workstations); program.DiskFiles.AddRange(globals.DiskFiles);
        program.IndicatorBindings.AddRange(globals.IndicatorBindings);
    }

    private static List<NativeProcedureSource> ExtractNativeProcedures(List<string> lines, bool free, CancellationToken cancellationToken)
    {
        var results = new List<NativeProcedureSource>();
        var marker = lines.FindIndex(l => l.Trim().Equals("**free", StringComparison.OrdinalIgnoreCase));
        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = lines[index].Trim();
            var headerEnd = index;
            var freeHeader = Regex.IsMatch(text, @"\ADCL-PROC(?:\s|;|\z)" , RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var declaration = freeHeader ? ReadFreeDeclaration(new(lines), ref headerEnd, index + 1, !free) : "";
            var start = Regex.Match(declaration, @"\ADCL-PROC\s+([A-Za-z_][A-Za-z0-9_]*)(?:\s+(EXPORT))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var fixedHeader = ProcedureHeader(lines[index], index + 1);
            if (!start.Success && fixedHeader is not { Begin: true })
            {
                if (Regex.IsMatch(text, @"\A(?:DCL-PROC|END-PROC)\b", RegexOptions.IgnoreCase)) throw new RpgCompileException(index + 1, "Invalid or unmatched procedure declaration.");
                continue;
            }
            var name = start.Success ? start.Groups[1].Value.ToUpperInvariant() : fixedHeader!.Value.Name;
            var line = index + 1; var end = headerEnd + 1; var bodyStart = end;
            for (; end < lines.Count; end++)
            {
                var candidate = lines[end].Trim(); var nativeEnd = ProcedureHeader(lines[end], end + 1);
                if (Regex.IsMatch(candidate, @"\ADCL-PROC\b", RegexOptions.IgnoreCase) || nativeEnd is { Begin: true }) throw new RpgCompileException(end + 1, "Procedures cannot nest.");
                var freeEnd = start.Success && Regex.IsMatch(candidate, @"\AEND-PROC(?:\s|;|\z)", RegexOptions.IgnoreCase);
                if (freeEnd)
                {
                    var close = end;
                    var ending = ReadFreeDeclaration(new(lines), ref close, end + 1, !free);
                    if (!Regex.IsMatch(ending, @"\AEND-PROC(?:\s+" + Regex.Escape(name) + @")?\z", RegexOptions.IgnoreCase)) throw new RpgCompileException(end + 1, "Procedure ending name does not match its beginning.");
                }
                if (start.Success ? freeEnd : nativeEnd is { Begin: false })
                {
                    if (!start.Success && nativeEnd!.Value.Name.Length > 0 && nativeEnd.Value.Name != name) throw new RpgCompileException(end + 1, "Procedure ending name does not match its beginning.");
                    break;
                }
            }
            if (end == lines.Count) throw new RpgCompileException(line, "Procedure is not closed.");
            if (!RpgWorkstationBindings.Name(name)) throw new RpgCompileException(line, "Invalid native procedure name.");
            if (results.Count >= 256 || results.Any(p => p.Name == name)) throw new RpgCompileException(line, "Duplicate procedure or more than 256 native procedures.");
            var body = Enumerable.Repeat("", lines.Count).ToList();
            if (marker >= 0) body[marker] = "**free";
            for (var n = bodyStart; n < end; n++) body[n] = lines[n];
            results.Add(new(name, line, start.Success ? start.Groups[2].Success : fixedHeader!.Value.Export, body));
            var last = end;
            if (start.Success) _ = ReadFreeDeclaration(new(lines), ref last, end + 1, !free);
            for (var n = index; n <= last; n++) lines[n] = "";
            index = last;
        }
        return results;
    }

    private static (string Name, bool Begin, bool Export)? ProcedureHeader(string line, int lineNumber)
    {
        var shift = line.Length > 5 && char.ToUpperInvariant(line[5]) == 'P' ? 0 : line.Length > 6 && char.ToUpperInvariant(line[6]) == 'P' ? 1 : -1;
        if (shift < 0 || line[..(5 + shift)].Any(c => c != ' ' && !char.IsAsciiDigit(c))) return null;
        var marker = Col(line, 24 + shift, 24 + shift).ToUpperInvariant();
        if (marker is not ("B" or "E")) return null;
        if (Col(line, 22 + shift, 23 + shift).Any(c => c != ' ') || Col(line, 25 + shift, 43 + shift).Any(c => c != ' ')) throw new RpgCompileException(lineNumber, "Native P-spec reserved columns must be blank.");
        var options = Col(line, 44 + shift, 80 + shift).Trim().ToUpperInvariant();
        if (options is not ("" or "EXPORT") || marker == "E" && options.Length > 0) throw new RpgCompileException(lineNumber, "Native P specs support B/E and optional EXPORT only.");
        var name = Col(line, 7 + shift, 21 + shift).Trim().ToUpperInvariant();
        if (marker == "B" && !RpgWorkstationBindings.Name(name)) throw new RpgCompileException(lineNumber, "Native procedure requires a name.");
        return (name, marker == "B", options == "EXPORT");
    }

    private static bool SameProcedureType(RpgField? left, RpgField? right) => left is null ? right is null : right is not null &&
        SameAggregateElement(left, right) && left.IsArray == right.IsArray && left.Dimension == right.Dimension && left.ArrayDescending == right.ArrayDescending &&
        left.NullIndicatorParameterIndex == right.NullIndicatorParameterIndex && left.NullMapParameter == right.NullMapParameter && (!left.NullMapParameter || SameNullCapabilities(left, right)) && left.VariableSizeParameter == right.VariableSizeParameter && left.ReturnByParameter == right.ReturnByParameter && left.ValueParameter == right.ValueParameter && left.ReadOnlyParameter == right.ReadOnlyParameter && left.OmitParameter == right.OmitParameter && left.NoPassParameter == right.NoPassParameter;

    private static void LinkNativeProcedures(RpgProgram program)
    {
        foreach (var procedure in program.Subprocedures.Where(p => p.NativeBody is not null))
        {
            var body = procedure.NativeBody!;
            if (program.FindPrototype(procedure.Name) is null)
            {
                if (program.Prototypes.Count >= 256) throw new RpgCompileException(body.NativeEntryLine, "Native procedure/prototype metadata exceeds 256 entries.");
                if (HasLocalDeclaration(program, procedure.Name)) throw new RpgCompileException(body.NativeEntryLine, "Procedure name collides with module storage.");
                var prototype = new RpgPrototype { Name = procedure.Name, ExternalName = procedure.Name, NativeSignature = true, NativeProcedure = true, NativeReturnField = body.NativeReturnField, DeclarationLine = body.NativeEntryLine };
                foreach (var name in body.NativeParameterLists["/*ENTRY"]) { prototype.NativeParameterNames.Add(name); prototype.Parameters.Add(name); prototype.NativeParameters.Add(body.FindField(name)!); }
                program.Prototypes.Add(prototype);
            }
            else if (program.FindPrototype(procedure.Name) is not { NativeProcedure: true }) throw new RpgCompileException(body.NativeEntryLine, "Native procedure name collides with a program/legacy prototype.");
        }
        foreach (var prototype in program.Prototypes.Where(p => p.NativeProcedure))
            ValidateProcedureLink(program, prototype);
        foreach (var procedure in program.Subprocedures.Where(p => p.NativeBody is not null))
        {
            var body = procedure.NativeBody!;
            foreach (var prototype in program.Prototypes) if (!body.Prototypes.Contains(prototype)) body.Prototypes.Add(prototype);
            foreach (var prototype in body.Prototypes.Where(p => p.NativeProcedure)) ValidateProcedureLink(program, prototype, body);
            ValidateNativeProgramCalls(body, body.MainStatements.Concat(body.Subroutines.SelectMany(s => s.Statements)));
            ValidateProcedureExpressions(body);
            ValidateFileParameterUses(body, []);
        }
    }

    private static void ValidateProcedureLink(RpgProgram program, RpgPrototype prototype, RpgProgram? scope = null)
    {
        if (prototype.PointerTarget is { } pointer)
        {
            if (ExactActualType(scope ?? program, RpgExpressionParser.Parse(pointer)) is not { Kind: RpgFieldKind.ProcedurePointer, IsArray: false, IsDataStructure: false })
                throw new RpgCompileException(prototype.DeclarationLine, "EXTPROC requires a declared procedure pointer.");
            return;
        }
        var body = program.Subprocedures.FirstOrDefault(p => p.Name == (prototype.ExternalName ?? prototype.Name))?.NativeBody;
        if (body is null && program.IsModule) return;
        if (body is null) throw new RpgCompileException(prototype.DeclarationLine, "Case-sensitive procedure target is not defined in this compilation unit; compile modules with CRTRPGMOD and bind them with CRTPGM.");
        var parameters = body.NativeParameterLists["/*ENTRY"];
        if (!SameProcedureType(prototype.NativeReturnField, body.NativeReturnField) || parameters.Count != prototype.NativeParameters.Count ||
            parameters.Select(body.FindField).Zip(prototype.NativeParameters).Any(pair => !SameProcedureType(pair.First, pair.Second)))
            throw new RpgCompileException(body.NativeEntryLine, "Procedure PI must match its prototype's return type and parameter modes/layouts.");
    }

    private static void ValidateProcedureExpressions(RpgProgram program)
    {
        foreach (var statement in program.MainStatements.Concat(program.Subroutines.SelectMany(s => s.Statements)))
            foreach (var text in new[] { statement.Factor1, statement.Factor2, statement.Result, statement.Value }.OfType<string>().Distinct())
                for (var index = 0; index < text.Length; index++)
                {
                    if (text[index] == '\'') { Skip(text, ref index); continue; }
                    if (!char.IsAsciiLetter(text[index]) && text[index] != '_') continue;
                    var start = index; while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '_')) index++;
                    var name = text[start..index]; var end = index;
                    while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
                    if (start > 0 && text[start - 1] is '.' or '%' || end == text.Length || text[end] != '(' || program.FindPrototype(name) is not { NativeProcedure: true } prototype) { index--; continue; }
                    var depth = 1; var close = end + 1;
                    for (; close < text.Length; close++)
                    { if (text[close] == '\'') { Skip(text, ref close); continue; } if (text[close] == '(') depth++; if (text[close] == ')' && --depth == 0) break; }
                    if (close == text.Length) throw new RpgCompileException(statement.LineNumber, "Unclosed procedure expression.");
                    if (prototype.NativeReturnField is null && !((statement.Opcode is RpgOpcode.CallP or RpgOpcode.CallB) && start == 0 && close == text.Length - 1))
                        throw new RpgCompileException(statement.LineNumber, "A void procedure cannot be used as an expression.");
                    ValidateNativeProgramCalls(program, [new RpgStatement { Opcode = RpgOpcode.CallP, Factor1 = text[start..(close + 1)], LineNumber = statement.LineNumber }]);
                    index = end; // Inspect nested procedure arguments as well.
                }
        static void Skip(string text, ref int index)
        { while (++index < text.Length) if (text[index] == '\'') { if (index + 1 < text.Length && text[index + 1] == '\'') index++; else break; } }
    }

    private static void ValidateNativeReturns(RpgProgram program)
    {
        if (!program.IsNativeProcedure) return;
        foreach (var statement in program.MainStatements.Where(s => s.Opcode is RpgOpcode.Return or RpgOpcode.Retrn))
        {
            var value = statement.Factor1 ?? statement.Factor2 ?? statement.Value;
            if (string.IsNullOrWhiteSpace(value) != (program.NativeReturnField is null)) throw new RpgCompileException(statement.LineNumber, "RETURN must supply the value declared by the procedure PI.");
        }
    }

    private static void ValidateProcedureFiles(RpgProgram program)
    {
        if (program.GlobalProgram is not { } globals) return;
        foreach (var handler in globals.FileErrorHandlers)
            foreach (var statement in program.MainStatements.Where(s => ErrorFileOperation(s.Opcode)))
            {
                var operand = (statement.Factor2 ?? statement.Factor1 ?? "").ToUpperInvariant();
                var disk = program.FindDisk(operand);
                if (disk is not null)
                {
                    if (disk.Request.Procedure is null && !disk.Request.IsParameter && disk.Request.Name == handler.File)
                        throw new RpgCompileException(statement.LineNumber, "A native subprocedure cannot use a global file with an INFSR.");
                    continue;
                }
                if (program.FindWorkstation(operand) is { Request.Procedure: null, Request.IsParameter: false } display && display.Request.Name == handler.File)
                    throw new RpgCompileException(statement.LineNumber, "A native subprocedure cannot use a global file with an INFSR.");
            }
    }
}
