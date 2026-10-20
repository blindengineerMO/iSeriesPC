using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void DeclareFileParameter(RpgProgram destination, RpgProgram symbols, string name,
        IReadOnlyList<DeclarationKeyword> options, int line, bool allocate, bool readOnly, bool noPass, bool omit, bool byValue)
    {
        if (options.Count != 1 || readOnly || byValue || options[0].Arguments is not { } parentName)
            throw new RpgCompileException(line, "LIKEFILE parameters require one file definition and reference passing.");
        var files = RecordFiles(symbols); var parent = files.SingleOrDefault(f => f.Request.Name.Equals(parentName, StringComparison.OrdinalIgnoreCase));
        if (parent is null && RecordWorkstations(symbols).SingleOrDefault(f => f.Request.Name.Equals(parentName, StringComparison.OrdinalIgnoreCase)) is { } display)
        { DeclareWorkstationParameter(destination, symbols, name, display, line, allocate, noPass, omit); return; }
        if (parent is null || parent.Request.IsParameter) throw new RpgCompileException(line, "LIKEFILE parameters require a previously defined DISK file.");
        if (parent.Request.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new RpgCompileException(line, "A file parameter cannot shadow its own parent definition.");
        if (!symbols.FileDeclarationLines.TryGetValue(parent.Request.Name, out var parentLine) || parentLine >= line) throw new RpgCompileException(line, "LIKEFILE parameter parents must be declared earlier.");
        if (!symbols.UnblockedFiles.Contains(parent.Request.Name)) throw new RpgCompileException(line, "A LIKEFILE parameter parent requires a determined blocking property, such as BLOCK(*NO).");
        if (!RpgWorkstationBindings.Name(name) || HasLocalDeclaration(destination, name)) throw new RpgCompileException(line, "Invalid or duplicate file parameter.");
        if (allocate && files.Count >= 16 && !files.Any(f => f.Request.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new RpgCompileException(line, "At most 16 visible DISK files, including file parameters, are supported.");
        var request = parent.Request with { Name = name, LikeFile = parent.Request.Name, IsTemplate = false, Qualified = true, UserOpen = true, IsParameter = true, Procedure = destination.NativeProcedureName, Static = false, Recno = null };
        var record = RpgDiskBindings.RecordName(request, request.Renames.SingleOrDefault()?.Name ?? parent.Layout.ExternalRecord);
        var layout = parent.Layout with { Record = record, Signature = RpgDiskLayout.SignatureFor(parent.Layout.ExternalRecord, record, parent.Layout.RecordLength, parent.Layout.BufferLayoutVersion, parent.Layout.Fields) };
        var definition = new RpgDiskBinding(request, layout, RpgDiskBindings.FamilyKey(parent));
        if (destination.IsNativeProcedure) destination.Fields.RemoveAll(f => destination.GlobalFields.Contains(f) && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        destination.Fields.Add(new() { Name = name, Kind = RpgFieldKind.Character, Length = 0, FileParameter = definition,
            NoPassParameter = noPass, OmitParameter = omit, DeclarationLine = line });
        if (allocate)
        {
            if (!destination.IsNativeProcedure && (files.Any(f => f.Request.Name == name) || RecordWorkstations(symbols).Any(f => f.Request.Name == name))) throw new RpgCompileException(line, "A file parameter collides with a file definition.");
            destination.DiskFiles.RemoveAll(f => f.Request.Name == name); destination.DeclarationDiskFiles.RemoveAll(f => f.Request.Name == name);
            destination.Workstations.RemoveAll(f => f.Request.Name == name); destination.DeclarationWorkstations.RemoveAll(f => f.Request.Name == name);
            destination.DiskFiles.Add(definition); destination.DeclarationDiskFiles.Add(definition);
        }
    }

    private static void ValidateFileActual(RpgProgram program, RpgExpr expression, RpgDiskBinding formal, bool omit, int line)
    {
        if (expression is RpgOmittedArgument && omit) return;
        var actual = expression is RpgFieldRef name ? program.DiskFiles.SingleOrDefault(f => f.Request.Name.Equals(name.Name, StringComparison.OrdinalIgnoreCase)) : null;
        if (actual is null || actual.Request.IsTemplate || RpgDiskBindings.FamilyKey(actual) != RpgDiskBindings.FamilyKey(formal))
            throw new RpgCompileException(line, "A LIKEFILE actual must be a runtime file from the same parent family.");
    }

    private static void ValidateFileParameterUses(RpgProgram program, IReadOnlyList<KeyDeclaration> keys)
    {
        if (!program.Fields.Any(f => f.IsFileParameter)) return;
        foreach (var key in keys) foreach (var field in key.Fields) Check(field, key.Line);
        foreach (var statement in program.MainStatements.Concat(program.Subroutines.SelectMany(s => s.Statements)))
        {
            if (statement.Opcode is RpgOpcode.Goto or RpgOpcode.Tag or RpgOpcode.BegSr or RpgOpcode.EndSr or RpgOpcode.Exsr or
                RpgOpcode.BegProc or RpgOpcode.EndProc or RpgOpcode.Plist or RpgOpcode.Klist or RpgOpcode.Open or RpgOpcode.Close or RpgOpcode.Force or RpgOpcode.Unlock) continue;
            var file = statement.Opcode is RpgOpcode.Chain or RpgOpcode.Read or RpgOpcode.ReadP or RpgOpcode.Reade or RpgOpcode.ReadPe or RpgOpcode.ReadC or
                RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Write or RpgOpcode.Update or RpgOpcode.Delete or RpgOpcode.ExFmt;
            var keyed = statement.Opcode is RpgOpcode.Chain or RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Reade or RpgOpcode.ReadPe ||
                statement.Opcode == RpgOpcode.Delete && !string.IsNullOrWhiteSpace(statement.Factor2);
            foreach (var text in new[] { !file || keyed ? statement.Factor1 : null, !file ? statement.Factor2 : null,
                !file || statement.NativeOperation is not null ? statement.Result : null, statement.RecordDataStructure, !file ? statement.Value : null }.OfType<string>().Distinct())
                Check(text, statement.LineNumber);
        }

        void Check(string text, int line, int depth = 0)
        {
            if (depth > 64) throw new RpgCompileException(line, "File parameter expression nesting exceeds 64 levels.");
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '\'') { SkipLiteral(text, ref index); continue; }
                if (char.IsAsciiDigit(text[index]) || text[index] == '.' && index + 1 < text.Length && char.IsAsciiDigit(text[index + 1]))
                {
                    while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] == '.')) index++;
                    if (index < text.Length && text[index] is 'e' or 'E')
                    { index++; if (index < text.Length && text[index] is '+' or '-') index++; while (index < text.Length && char.IsAsciiDigit(text[index])) index++; }
                    index--; continue;
                }
                if (!char.IsAsciiLetter(text[index]) && text[index] != '_') continue;
                var start = index;
                while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '_')) index++;
                var name = text[start..index]; var end = index;
                while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
                var previous = start - 1;
                while (previous >= 0 && char.IsWhiteSpace(text[previous])) previous--;
                if (previous >= 0 && text[previous] == '.' || start > 0 && text[start - 1] == '*' &&
                    (name.ToUpperInvariant() is "ALL" or "IN" or "INLR" or "ON" or "OFF" or "YES" or "NO" or "HIVAL" or "LOVAL" or "ZERO" or "ZEROS" or "BLANK" or "BLANKS" or "OMIT" or "DATE" or "TIME" or "TIMESTAMP" ||
                    name.Length == 4 && name.StartsWith("IN", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(name[2]) && char.IsAsciiDigit(name[3])))
                { index--; continue; }
                if (!(start > 0 && text[start - 1] == '%') && program.FindField(name)?.IsFileParameter == true)
                    throw new RpgCompileException(line, "A LIKEFILE parameter has no data storage: " + name + ".");
                if (end < text.Length && text[end] == '(')
                {
                    var nesting = 1; var close = end + 1;
                    for (; close < text.Length; close++)
                    { if (text[close] == '\'') { SkipLiteral(text, ref close); continue; } if (text[close] == '(') nesting++; if (text[close] == ')' && --nesting == 0) break; }
                    if (close >= text.Length) throw new RpgCompileException(line, "Unclosed file parameter expression.");
                    var arguments = text[(end + 1)..close].Trim().Length == 0 ? [] : DeclarationArguments(text[(end + 1)..close], line);
                    var builtin = start > 0 && text[start - 1] == '%';
                    var prototype = builtin ? null : program.FindPrototype(name);
                    for (var argument = 0; argument < arguments.Length; argument++)
                    {
                        if (builtin && arguments.Length == 1 && name.ToUpperInvariant() is "OPEN" or "EOF" or "FOUND" or "EQUAL" or "STATUS" or "OMITTED" or "PARMNUM" &&
                            program.FindField(arguments[argument].Trim())?.IsFileParameter == true) continue;
                        if (prototype?.NativeSignature == true && argument < prototype.NativeParameters.Count && prototype.NativeParameters[argument].FileParameter is { } formal)
                            ValidateFileActual(program, RpgExpressionParser.Parse(arguments[argument]), formal, prototype.NativeParameters[argument].OmitParameter, line);
                        else if (prototype?.NativeSignature == true && argument < prototype.NativeParameters.Count && prototype.NativeParameters[argument].WorkstationParameter is { } workstation)
                            ValidateWorkstationActual(program, RpgExpressionParser.Parse(arguments[argument]), workstation, prototype.NativeParameters[argument].OmitParameter, line);
                        else if (prototype is null && program.GlobalProgram?.NativeProcedureNames.Contains(name) == true &&
                            program.FindField(arguments[argument].Trim())?.IsFileParameter == true) continue;
                        else Check(arguments[argument], line, depth + 1);
                    }
                    index = close; continue;
                }
                index--;
            }
        }
    }
}
