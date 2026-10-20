using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private sealed record WorkstationDeclaration(int Line, string Name, bool Input, bool Output, string Keywords, bool Disk = false, bool Template = false, bool Qualified = false, string? LikeFile = null, bool IncompleteFile = false, bool Unblocked = false, string? Procedure = null, bool Static = false);
    private sealed record DeclarationKeyword(string Name, string? Arguments);

    private static List<WorkstationDeclaration> ParseWorkstationDeclarations(List<string> lines, RpgProgram program, bool free,
        CancellationToken cancellationToken, bool filesOnly = false, Func<RpgDiskRequest, RpgDiskLayout>? diskResolver = null,
        Func<RpgWorkstationRequest, RpgWorkstationLayout>? workstationResolver = null)
    {
        if (!filesOnly)
        {
            // Discover final feedback lengths before folding layout-dependent declarations.
            var discovery = new RpgProgram { Library = program.Library, Name = program.Name, NativeProcedureName = program.NativeProcedureName, GlobalProgram = program.GlobalProgram };
            if (program.GlobalProgram is { } globals) ImportGlobalSymbols(discovery, globals);
            var files = ParseWorkstationDeclarations(new(lines), discovery, free, cancellationToken, true);
            foreach (var file in files) { program.FileDefinitionKeywords[file.Name] = file.Keywords; program.FileDeclarationLines[file.Name] = file.Line; if (file.Unblocked) program.UnblockedFiles.Add(file.Name); }
            {
                var recordFiles = files.Where(f => f.Disk).Select(f => f with { Keywords = string.Join(' ',
                    DeclarationKeywords(f.Keywords, f.Line).Where(k => k.Name is not ("INFDS" or "INFSR"))
                        .Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")"))) }).ToArray();
                var resolved = false;
                program.RecordLayoutsLookup = () =>
                {
                    if (!resolved)
                    {
                        var metadata = new RpgProgram { Name = program.Name, Library = program.Library, NativeProcedureName = program.NativeProcedureName, GlobalProgram = program.GlobalProgram };
                        if (program.GlobalProgram is { } owner) metadata.DiskFiles.AddRange(RecordFiles(owner));
                        HideImportedFiles(metadata, files);
                        BindDisks(metadata, recordFiles, diskResolver);
                        HideImportedFiles(program, files);
                        program.DeclarationDiskFiles.AddRange(metadata.DiskFiles.Where(f => !program.DeclarationDiskFiles.Contains(f))); resolved = true;
                    }
                    return program.DeclarationDiskFiles;
                };
            }
            var displays = files.Where(f => !f.Disk).Select(f => f with { Keywords = FileKeywords(DeclarationKeywords(f.Keywords, f.Line).Where(k => k.Name is not ("INFDS" or "INFSR" or "INDDS"))) }).ToArray();
            {
                var resolved = false;
                program.WorkstationLayoutsLookup = () =>
                {
                    if (!resolved)
                    {
                        var metadata = new RpgProgram { Library = program.Library, Name = program.Name, NativeProcedureName = program.NativeProcedureName, GlobalProgram = program.GlobalProgram };
                        if (program.GlobalProgram is { } owner) metadata.Workstations.AddRange(RecordWorkstations(owner));
                        HideImportedFiles(metadata, files);
                        BindWorkstations(metadata, displays, workstationResolver, metadataOnly: true);
                        HideImportedFiles(program, files);
                        program.DeclarationWorkstations.AddRange(metadata.Workstations.Where(f => !program.DeclarationWorkstations.Contains(f))); resolved = true;
                    }
                    return program.DeclarationWorkstations;
                };
            }
            foreach (var declaration in files)
                foreach (var keyword in DeclarationKeywords(declaration.Keywords, declaration.Line).Where(k => k.Name is "INFDS" or "INDDS"))
                    if (keyword.Arguments is { } structure)
                        program.DeclarationStructureLengths[structure.ToUpperInvariant()] = Math.Max(
                            program.DeclarationStructureLengths.GetValueOrDefault(structure.ToUpperInvariant()), keyword.Name == "INDDS" ? 99 : 80);
        }
        var declarations = new List<WorkstationDeclaration>(); var calculations = false; var freeBlock = false; var procedure = false;
        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = lines[index]; var text = (!free && raw.Length >= 8 && raw[..5].All(c => c == ' ' || char.IsAsciiDigit(c)) && raw[5] == ' ' && raw[6] == ' ' ? raw[7..] : raw).Trim(); var line = index + 1;
            if (!free && text.Length == 0) { lines[index] = ""; continue; }
            if (IsNativeCalculation(raw))
            {
                if (!filesOnly) DeclareNativeCalculationResult(program, raw, line, procedure);
                calculations = true; continue;
            }
            if (text.Equals("/free", StringComparison.OrdinalIgnoreCase)) freeBlock = true;
            if (text.Equals("/end-free", StringComparison.OrdinalIgnoreCase)) freeBlock = false;
            if (Specification(raw) == 'P') procedure = Col(raw, 24, 24).Equals("B", StringComparison.OrdinalIgnoreCase);
            if (NativeDefinitionShift(raw) is { } definitionShift)
            {
                if (!filesOnly && (calculations || freeBlock || procedure))
                    throw new RpgCompileException(line, "Traditional fixed definitions require global declaration scope before calculations.");
                ReadNativeFixedDefinition(lines, ref index, program, definitionShift, !filesOnly, cancellationToken);
                continue;
            }
            var scalar = text.StartsWith("DCL-S ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("DCL-S\t", StringComparison.OrdinalIgnoreCase);
            var file = text.StartsWith("DCL-F ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("DCL-F\t", StringComparison.OrdinalIgnoreCase);
            if (Regex.IsMatch(text, @"\ADCL-(PR|PI)\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (!filesOnly && (calculations || !free && (freeBlock || procedure)))
                    throw new RpgCompileException(line, "Program PR/PI declarations require global scope before calculations.");
                ReadFreeProgramSignature(lines, ref index, program, !filesOnly, !free, cancellationToken);
                continue;
            }
            if (text.StartsWith("DCL-C ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("DCL-C\t", StringComparison.OrdinalIgnoreCase))
            {
                if (filesOnly) { _ = ReadFreeDeclaration(lines, ref index, line, !free); continue; }
                if (calculations || !free && (freeBlock || procedure)) throw new RpgCompileException(line, "DCL-C must precede calculations and remain outside column-limited /FREE blocks and procedures.");
                DeclareFreeConstant(program, ReadFreeDeclaration(lines, ref index, line, !free), line);
                continue;
            }
            if (text.StartsWith("DCL-DS ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("DCL-DS\t", StringComparison.OrdinalIgnoreCase))
            {
                if (filesOnly)
                {
                    var header = ReadFreeDeclaration(lines, ref index, line, !free);
                    if (DeclarationKeywords(Regex.Match(header, @"\ADCL-DS\s+\S+\s*(.*)\z", RegexOptions.IgnoreCase).Groups[1].Value, line).Any(k => k.Name is "LIKEDS" or "LIKEREC" or "END-DS")) continue;
                    index = StructureEnd(lines, line - 1, index, !free, cancellationToken);
                    continue;
                }
                if (!free && (freeBlock || procedure)) throw new RpgCompileException(line, "Column-limited free declarations must remain outside /FREE blocks and procedures.");
                if (calculations) throw new RpgCompileException(line, "Free declarations must precede calculations.");
                DeclareNativeStructure(lines, ref index, program, cancellationToken, !free);
                continue;
            }
            if (scalar || file)
            {
                if (filesOnly && scalar) { _ = ReadFreeDeclaration(lines, ref index, line, !free); continue; }
                if (!free && (freeBlock || procedure)) throw new RpgCompileException(line, "Column-limited free declarations must remain outside /FREE blocks and procedures.");
                if (calculations && !filesOnly) throw new RpgCompileException(line, "Free declarations must precede calculations in the supported global declaration subset.");
                text = ReadFreeDeclaration(lines, ref index, line, !free);
                var match = Regex.Match(text, @"\ADCL-[FS]\s+([A-Za-z_][A-Za-z0-9_]*)(?:\s+(.*))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) throw new RpgCompileException(line, "Invalid free declaration.");
                var name = match.Groups[1].Value.ToUpperInvariant(); var keywords = match.Groups[2].Value;
                if (scalar) DeclareFreeScalar(program, name, keywords, line);
                else
                {
                    var options = DeclarationKeywords(keywords, line);
                    var disk = options.Count == 0 || options[0].Name != "WORKSTN";
                    declarations.Add(new(line, name, true, !disk, keywords, disk));
                }
                continue;
            }
            var shift = raw.Length > 6 && raw[..6].All(c => c == ' ' || char.IsAsciiDigit(c)) && char.ToUpperInvariant(raw[6]) == 'F' ? 1 :
                raw.Length > 5 && raw[..5].All(c => c == ' ' || char.IsAsciiDigit(c)) && char.ToUpperInvariant(raw[5]) == 'F' ? 0 : -1;
            if (shift < 0)
            {
                if (text.Length > 0 && text is not ("**free" or "**FREE") && !text.StartsWith('/') && !text.StartsWith('*') &&
                    (free || freeBlock || Specification(raw) == 'C')) calculations = true;
                continue;
            }
            var padded = raw.PadRight(43 + shift); lines[index] = "";
            var fileName = padded.Substring(6 + shift, 10).Trim().ToUpperInvariant();
            if (fileName.Length == 0)
            {
                if (declarations.Count == 0 || padded.Substring(16 + shift, 27).Any(c => c != ' ')) throw new RpgCompileException(line, "Invalid F-spec continuation.");
                declarations[^1] = declarations[^1] with { Keywords = declarations[^1].Keywords + " " + padded[(43 + shift)..] }; continue;
            }
            var type = char.ToUpperInvariant(padded[16 + shift]); var designation = char.ToUpperInvariant(padded[17 + shift]);
            var device = padded.Substring(35 + shift, 7).Trim().ToUpperInvariant();
            if (padded.Substring(16 + shift, 27).All(c => c == ' '))
            {
                if (!Ipc.Core.Objects.ObjectName.IsValid(fileName)) throw new RpgCompileException(line, "Invalid LIKEFILE F-spec name.");
                declarations.Add(new(line, fileName, true, false, padded[(43 + shift)..], true, IncompleteFile: true)); continue;
            }
            if (device == "DISK")
            {
                if (!Ipc.Core.Objects.ObjectName.IsValid(fileName) || type is not ('I' or 'U' or 'O') ||
                    (type == 'O' ? designation is not (' ' or 'F') : designation != 'F') ||
                    char.ToUpperInvariant(padded[21 + shift]) != 'E' || padded[18 + shift] != ' ' ||
                    char.ToUpperInvariant(padded[19 + shift]) is not (' ' or 'A') || padded[20 + shift] != ' ' ||
                    padded.Substring(22 + shift, 11).Any(c => c != ' ') || char.ToUpperInvariant(padded[33 + shift]) is not (' ' or 'K') ||
                    padded[34 + shift] != ' ' || padded[42 + shift] != ' ')
                    throw new RpgCompileException(line, "Supported DISK F-specs require an externally described full-procedural I/U or output O file, with optional K access and A addition.");
                var usage = type switch { 'U' => "*UPDATE:*DELETE", 'I' => "*INPUT", _ => "*OUTPUT" };
                if (type != 'O' && char.ToUpperInvariant(padded[19 + shift]) == 'A') usage += ":*OUTPUT";
                declarations.Add(new(line, fileName, true, false, "DISK USAGE(" + usage + ") " + (char.ToUpperInvariant(padded[33 + shift]) == 'K' ? "KEYED " : "") + padded[(43 + shift)..], true));
                continue;
            }
            if (!Ipc.Core.Objects.ObjectName.IsValid(fileName) || type is not ('C' or 'O') || type == 'C' && designation != 'F' || type == 'O' && designation is not (' ' or 'F') ||
                char.ToUpperInvariant(padded[21 + shift]) != 'E' || padded.Substring(35 + shift, 7).Trim().ToUpperInvariant() != "WORKSTN" ||
                padded.Substring(18 + shift, 3).Any(c => c != ' ') || padded.Substring(22 + shift, 13).Any(c => c != ' ') || padded[42 + shift] != ' ')
                throw new RpgCompileException(line, "Supported F-specs require an externally described full-procedural C/O WORKSTN file.");
            declarations.Add(new(line, fileName, type == 'C', true, "WORKSTN " + padded[(43 + shift)..]));
        }
        if (declarations.Select(d => d.Name).Distinct().Count() != declarations.Count)
            throw new RpgCompileException(0, "At most 16 workstation and 16 disk files with distinct names are supported.");
        var expanded = ExpandFileDeclarations(declarations, program);
        if (expanded.Count(d => !d.Disk) > 16 || expanded.Count(d => d.Disk) > 16)
            throw new RpgCompileException(0, "At most 16 workstation and 16 disk files with distinct names are supported.");
        HideImportedFiles(program, expanded);
        foreach (var declaration in expanded) program.FileDefinitionKeywords[declaration.Name] = declaration.Keywords;
        return expanded;
    }

    private static List<DeclarationKeyword> DeclarationKeywords(string text, int line)
    {
        var result = new List<DeclarationKeyword>(); var at = 0;
        while (at < text.Length)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            if (at == text.Length) break;
            var start = at;
            while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '_')) at++;
            if (start < at && text[start..at].Equals("END", StringComparison.OrdinalIgnoreCase) && text.AsSpan(at).StartsWith("-DS", StringComparison.OrdinalIgnoreCase) &&
                (at + 3 == text.Length || char.IsWhiteSpace(text[at + 3]) || text[at + 3] == '(')) at += 3;
            if (start == at) throw new RpgCompileException(line, "Invalid declaration keyword.");
            var name = text[start..at].ToUpperInvariant();
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            string? arguments = null;
            if (at < text.Length && text[at] == '(')
            {
                start = ++at; var depth = 1; var quoted = false;
                while (at < text.Length)
                {
                    var c = text[at++];
                    if (c == '\'')
                    { if (quoted && at < text.Length && text[at] == '\'') { at++; continue; } quoted = !quoted; }
                    if (quoted) continue;
                    if (c == '(') depth++;
                    if (c == ')' && --depth == 0) { arguments = text[start..(at - 1)].Trim(); break; }
                }
                if (depth != 0 || quoted) throw new RpgCompileException(line, "Unbalanced declaration keyword.");
            }
            result.Add(new(name, arguments));
            if (result.Count > 1024) throw new RpgCompileException(line, "Too many declaration keywords.");
        }
        return result;
    }
    private static string[] DeclarationArguments(string text, int line)
    {
        var result = new List<string>(); var start = 0; var quoted = false; var depth = 0;
        for (var n = 0; n < text.Length; n++)
        {
            if (text[n] == '\'') { if (quoted && n + 1 < text.Length && text[n + 1] == '\'') { n++; continue; } quoted = !quoted; }
            else if (!quoted && text[n] == '(') depth++;
            else if (!quoted && text[n] == ')') { if (--depth < 0) throw new RpgCompileException(line, "Unbalanced declaration argument."); }
            else if (!quoted && depth == 0 && text[n] == ':') { result.Add(text[start..n].Trim()); start = n + 1; }
        }
        if (quoted || depth != 0) throw new RpgCompileException(line, "Unbalanced declaration argument or literal.");
        result.Add(text[start..].Trim()); return result.ToArray();
    }

    private static void DeclareFreeScalar(RpgProgram program, string name, string text, int line,
        RpgProgram? symbols = null, RpgDataStructure? current = null, bool standaloneStorage = true, bool readOnlyParameter = false, bool noPassParameter = false, bool omitParameter = false, bool valueParameter = false, bool parameter = false)
    {
        symbols ??= program;
        if (program.IsNativeProcedure) program.Fields.RemoveAll(f => program.GlobalFields.Contains(f) && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var keywords = DeclarationKeywords(text, line);
        keywords = DataBindingKeywords(program, name, keywords, line, standaloneStorage && !parameter);
        if (!RpgWorkstationBindings.Name(name) || HasLocalDeclaration(program, name) || keywords.Count == 0) throw new RpgCompileException(line, "Invalid or duplicate scalar declaration.");
        var inherited = LikeDefinition(symbols, keywords[0], line, current);
        var type = inherited is null ? keywords[0] : LikeScalarKeyword(inherited, line); var dimensions = type.Arguments is null ? Array.Empty<string>() : DeclarationArguments(type.Arguments, line);
        int Dimension(int index, int fallback) => dimensions.Length <= index ? fallback : DeclarationInteger(symbols, dimensions[index], line, 0, 32767, current);
        var kind = type.Name switch { "CHAR" => RpgFieldKind.Character, "PACKED" => RpgFieldKind.Packed, "ZONED" => RpgFieldKind.Zoned,
            "INT" => RpgFieldKind.Integer, "FLOAT" => RpgFieldKind.Float, "IND" => RpgFieldKind.Indicator, "DATE" => RpgFieldKind.Date,
            "TIME" => RpgFieldKind.Time, "TIMESTAMP" => RpgFieldKind.Timestamp, "POINTER" => type.Arguments is null ? RpgFieldKind.DataPointer : RpgFieldKind.ProcedurePointer, _ => throw new RpgCompileException(line, "Unsupported free scalar type " + type.Name + ".") };
        if (kind == RpgFieldKind.ProcedurePointer && !string.Equals(type.Arguments, "*PROC", StringComparison.OrdinalIgnoreCase)) throw new RpgCompileException(line, "POINTER accepts no arguments for a basing pointer, or *PROC for a procedure pointer.");
        if (kind is RpgFieldKind.ProcedurePointer or RpgFieldKind.DataPointer) dimensions = [];
        var length = Dimension(0, kind is RpgFieldKind.ProcedurePointer or RpgFieldKind.DataPointer ? 16 : kind is RpgFieldKind.Character or RpgFieldKind.Packed or RpgFieldKind.Zoned ? -1 : kind == RpgFieldKind.Indicator ? 1 : kind == RpgFieldKind.Timestamp ? 26 : kind is RpgFieldKind.Time or RpgFieldKind.Float ? 8 : 10);
        var decimals = Dimension(1, 0);
        if (length is < 1 or > 32767 || decimals < 0 || decimals > 28 || kind is RpgFieldKind.Packed or RpgFieldKind.Zoned && (length > 29 || decimals > length) ||
            dimensions.Length > (kind is RpgFieldKind.Packed or RpgFieldKind.Zoned ? 2 : 1) || kind == RpgFieldKind.Integer && length is not (3 or 5 or 10 or 20) ||
            kind == RpgFieldKind.Float && length is not (4 or 8) ||
            kind == RpgFieldKind.Indicator && type.Arguments is not null || kind is RpgFieldKind.Date or RpgFieldKind.Time or RpgFieldKind.Timestamp && type.Arguments is not null)
            throw new RpgCompileException(line, "Unsupported scalar dimensions or date/time options.");
        string? initial = null;
        if (keywords.Skip(1).Any(k => k.Name is not ("INZ" or "CCSID" or "DIM" or "ASCEND" or "DESCEND" or "CTDATA" or "PERRCD" or "EXTFMT" or "ALT" or "STATIC" or "TEMPLATE" or "NULLIND" or "BASED")) || keywords.Skip(1).GroupBy(k => k.Name).Any(g => g.Count() != 1))
            throw new RpgCompileException(line, "Unsupported standalone declaration option.");
        var basedOn = BasedDeclaration(program, name, keywords, line, standaloneStorage && !parameter && current is null);
        var nullable = NativeNullOption(symbols, keywords, line, parameter);
        var template = keywords.Any(k => k.Name == "TEMPLATE");
        if (template && (parameter || current is not null || keywords.Any(k => k.Name == "TEMPLATE" && k.Arguments is not null) ||
            keywords.Any(k => k.Name is "CTDATA" or "PERRCD" or "EXTFMT" or "ALT") || keywords.Any(k => k.Name == "DIM" && k.Arguments?.StartsWith('*') == true)))
            throw new RpgCompileException(line, "TEMPLATE requires a standalone field without compile-time data options.");
        if (template) ValidateTemplateCount(program, line);
        var isStatic = keywords.Any(k => k.Name == "STATIC");
        if (isStatic && (!program.IsNativeProcedure || keywords.Any(k => k.Name == "STATIC" && k.Arguments is not null)))
            throw new RpgCompileException(line, "STATIC requires an argument-free local procedure declaration.");
        var alternate = keywords.SingleOrDefault(k => k.Name == "ALT"); var alternateOf = alternate?.Arguments?.Trim().ToUpperInvariant();
        if (alternate is not null && (!RpgWorkstationBindings.Name(alternateOf) || alternateOf == name || keywords.Any(k => k.Name is "CTDATA" or "PERRCD")))
            throw new RpgCompileException(line, "ALT requires one other main array name and cannot specify CTDATA or PERRCD.");
        var array = keywords.SingleOrDefault(k => k.Name == "DIM");
        var table = !template && !parameter && standaloneStorage && array is not null && name.StartsWith("TAB", StringComparison.OrdinalIgnoreCase);
        if (table && name.Length > 10) throw new RpgCompileException(line, "Standalone table names begin with TAB and contain at most ten characters.");
        var inferred = array?.Arguments?.Trim().Equals("*CTDATA", StringComparison.OrdinalIgnoreCase) == true;
        var compileTime = inferred || alternate is not null || keywords.Any(k => k.Name == "CTDATA");
        var count = inferred ? InferredCompileTimeCount(program, alternateOf ?? name, line) : NativeDimension(symbols, array, line);
        var perRecord = NativeDimension(symbols, keywords.SingleOrDefault(k => k.Name == "PERRCD"), line);
        var external = keywords.SingleOrDefault(k => k.Name == "EXTFMT"); var format = external?.Arguments?.Trim().ToUpperInvariant() ?? "S";
        if (compileTime && (array is null || keywords.Any(k => k.Name == "INZ" || k.Name == "CTDATA" && k.Arguments is not null)) ||
            !compileTime && keywords.Any(k => k.Name is "PERRCD" or "EXTFMT") || inferred && perRecord != 1 ||
            external is not null && (external.Arguments is null || kind is not (RpgFieldKind.Integer or RpgFieldKind.Packed or RpgFieldKind.Zoned) || format is not ("S" or "L" or "R")))
            throw new RpgCompileException(line, "Invalid CTDATA/DIM/PERRCD/EXTFMT combination or CTDATA with INZ.");
        var sequence = ArraySequence(keywords, array is not null, line);
        var codePage = keywords.SingleOrDefault(k => k.Name == "CCSID");
        var opaque = codePage is null ? inherited?.OpaqueBytes == true : codePage.Arguments?.ToUpperInvariant() is "*HEX" or "65535";
        if (codePage is not null && (kind != RpgFieldKind.Character || codePage.Arguments?.ToUpperInvariant() is not ("*HEX" or "65535" or "*DFT")))
            throw new RpgCompileException(line, "Only CHAR CCSID(*HEX), CCSID(65535) or CCSID(*DFT) is supported.");
        if (compileTime && opaque) throw new RpgCompileException(line, "Opaque CTDATA requires the complete source-CCSID byte contract, which is not yet supported.");
        string? initializationExpression = null;
        if (keywords.SingleOrDefault(k => k.Name == "INZ")?.Arguments is { } init)
        {
            if (kind is RpgFieldKind.ProcedurePointer or RpgFieldKind.DataPointer)
            {
                if (!init.Equals("*NULL", StringComparison.OrdinalIgnoreCase) && !System.Text.RegularExpressions.Regex.IsMatch(init, kind == RpgFieldKind.DataPointer ? @"\A%ADDR\s*\(.+\)\z" : @"\A%PADDR\s*\(.+\)\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    throw new RpgCompileException(line, kind == RpgFieldKind.DataPointer ? "Basing pointer INZ requires *NULL or %ADDR." : "Procedure pointer INZ requires *NULL or %PADDR.");
                initializationExpression = init;
            }
            else
            {
                var value = DeclarationValue(symbols, init, line, current);
                ValidateDeclarationInitializer(value, kind, length, decimals, opaque, line);
                if (kind != RpgFieldKind.Float && RpgValues.IsNumeric(value)) value = RpgValues.ToDecimal(value);
                initializationExpression = DeclarationLiteral(value);
                initial = value is string characters ? characters : initializationExpression;
            }
        }
        program.Fields.Add(new() { Name = name, BasedOn = basedOn, NullCapable = nullable, NullIndicatorName = keywords.SingleOrDefault(k => k.Name == "NULLIND")?.Arguments?.ToUpperInvariant(), IsTemplate = template, Kind = kind, Length = length, Decimals = decimals, InitialValue = initial,
            LikeType = inherited?.LikeType ?? DeclarationIdentity(symbols, line, name), DeclaredLike = inherited is not null, ReadOnlyParameter = readOnlyParameter, NoPassParameter = noPassParameter, OmitParameter = omitParameter, ValueParameter = valueParameter, StaticLocal = isStatic,
            InitializationExpression = initializationExpression, OpaqueBytes = opaque, IsArray = array is not null && !table, IsTable = table,
            NativeArray = !template && array is not null && !table, NativeScalar = !template && standaloneStorage && array is null, DeclarationLine = line, Dimension = count, ArrayDescending = sequence, CompileTimeArray = compileTime,
            InferredCompileTimeDimension = inferred, ElementsPerRecord = perRecord, CompileTimeFormat = format, AlternateOf = alternateOf });
        if (compileTime && (long)CompileTimeWidth(program.Fields[^1]) * perRecord > 100) throw new RpgCompileException(line, "Compile-time entries per record exceed 100 source positions.");
        if (array is not null && !template) DeclareNativeStandalone(program, program.Fields[^1], line);
    }

    private static void BindWorkstations(RpgProgram program, IReadOnlyList<WorkstationDeclaration> declarations,
        Func<RpgWorkstationRequest, RpgWorkstationLayout>? resolver, bool metadataOnly = false)
    {
        var externalFields = new Dictionary<string, RpgDisplayField>(StringComparer.Ordinal);
        var parameters = program.Workstations.Where(f => f.Request.IsParameter).ToArray();
        program.Workstations.RemoveAll(f => f.Request.IsParameter);
        foreach (var declaration in declarations)
        {
            if (program.Workstations.Count >= 16) throw new RpgCompileException(declaration.Line, "At most 16 visible WORKSTN files are supported.");
            var keywords = DeclarationKeywords(declaration.Keywords, declaration.Line);
            string? Single(string name)
            {
                var found = keywords.Where(k => k.Name == name).ToArray();
                if (found.Length > 1) throw new RpgCompileException(declaration.Line, "Duplicate " + name + ".");
                return found.FirstOrDefault()?.Arguments;
            }
            foreach (var keyword in keywords)
            {
                if (keyword.Name is not ("WORKSTN" or "SFILE" or "RENAME" or "PREFIX" or "EXTDESC" or "EXTFILE" or "USROPN" or "USAGE"))
                    throw new RpgCompileException(declaration.Line, "Unsupported workstation keyword " + keyword.Name + ".");
                if (keyword.Name is not ("WORKSTN" or "USROPN") && string.IsNullOrWhiteSpace(keyword.Arguments))
                    throw new RpgCompileException(declaration.Line, keyword.Name + " requires arguments.");
            }
            _ = Single("USROPN");
            if (keywords.Count(k => k.Name == "WORKSTN" && k.Arguments is null) != 1 || keywords.Any(k => k.Name is "WORKSTN" or "USROPN" && k.Arguments is not null))
                throw new RpgCompileException(declaration.Line, "A workstation declaration requires WORKSTN and optional USROPN.");
            string Literal(string name, string fallback)
            {
                var value = Single(name);
                if (value is null) return fallback;
                if (name == "EXTFILE" && value.Equals("*EXTDESC", StringComparison.OrdinalIgnoreCase)) return "*EXTDESC";
                if (value.Length < 2 || value[0] != '\'' || value[^1] != '\'') throw new RpgCompileException(declaration.Line, name + " requires a constant quoted file name.");
                return Unquote(value).ToUpperInvariant();
            }
            var usage = Single("USAGE"); var input = declaration.Input; var output = declaration.Output;
            if (usage is not null)
            {
                var modes = DeclarationArguments(usage.ToUpperInvariant(), declaration.Line);
                if (modes.Length is < 1 or > 2 || modes.Any(m => m is not ("*INPUT" or "*OUTPUT")) || modes.Distinct().Count() != modes.Length)
                    throw new RpgCompileException(declaration.Line, "Workstation USAGE supports *INPUT and *OUTPUT.");
                input = modes.Contains("*INPUT"); output = modes.Contains("*OUTPUT");
            }
            var subfiles = keywords.Where(k => k.Name == "SFILE").Select(k => {
                var parts = DeclarationArguments(k.Arguments ?? "", declaration.Line);
                if (parts.Length != (declaration.Template ? 1 : 2)) throw new RpgCompileException(declaration.Line, declaration.Template ? "TEMPLATE SFILE requires a format without an RRN field." : "SFILE requires a format and RRN field.");
                return new RpgSubfileBinding(parts[0].ToUpperInvariant(), declaration.Template ? "" : parts[1].ToUpperInvariant());
            }).ToArray();
            var renames = keywords.Where(k => k.Name == "RENAME").Select(k => {
                var parts = DeclarationArguments(k.Arguments ?? "", declaration.Line);
                if (parts.Length != 2) throw new RpgCompileException(declaration.Line, "RENAME requires external and RPG format names.");
                return new RpgRecordRename(parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant());
            }).ToArray();
            var request = new RpgWorkstationRequest(declaration.Name, Literal("EXTDESC", declaration.Name), Literal("EXTFILE", declaration.Template && declaration.Name.Length > 10 ? "*EXTDESC" : declaration.Name), input, output,
                keywords.Any(k => k.Name == "USROPN"), subfiles, renames, FilePrefix(Single("PREFIX"),declaration.Line), declaration.Template, declaration.Qualified, declaration.LikeFile,
                Procedure: declaration.Procedure, Static: declaration.Static);
            try
            {
                var parent = declaration.LikeFile is { } parentName ? program.Workstations.SingleOrDefault(f => f.Request.Name == parentName)
                    ?? program.GlobalProgram?.Workstations.SingleOrDefault(f => f.Request.Name == parentName)
                    ?? throw new RpgCompileException(declaration.Line, "LIKEFILE parent metadata is unavailable.") : null;
                var layout = parent is null ? resolver?.Invoke(request) ?? throw new RpgCompileException(declaration.Line, "WORKSTN requires a compile-time display resolver.")
                    : RpgWorkstationBindings.Derive(request, parent.Layout);
                var binding = new RpgWorkstationBinding(request, layout, parent is null ? null : RpgWorkstationBindings.FamilyKey(parent)); RpgWorkstationBindings.Validate(binding);
                if (program.Workstations.Where(f => f.Request.Procedure == request.Procedure).SelectMany(f => f.Layout.Records).Any(r => layout.Records.Any(n => n.Name == r.Name)))
                    throw new RpgCompileException(declaration.Line, "Display record names must be distinct; use RENAME.");
                foreach (var field in metadataOnly || request.IsTemplate || request.Qualified || request.Procedure is not null ? [] : layout.Records.SelectMany(r => r.Fields))
                {
                    if (externalFields.TryGetValue(field.Name, out var previous) && (previous.Kind != field.Kind || previous.Length != field.Length || previous.Decimals != field.Decimals || previous.Opaque != field.Opaque))
                        throw new RpgCompileException(declaration.Line, "Incompatible shared external field: " + field.Name);
                    externalFields[field.Name] = field;
                    var existing = program.FindField(field.Name);
                    var member = program.FindDsElement(field.Name);
                    var shared = SharedExternalField(program, field.Name, field.External, field.Kind, field.Length, field.Decimals, display: true);
                    if (shared) continue;
                    if (program.FindDs(field.Name) is not null || member is not null && !shared)
                        throw new RpgCompileException(declaration.Line, "External field conflicts with a data structure: " + field.Name);
                    if (existing is not null)
                    {
                        if (existing.IsArray || member is not null && !shared || program.FindDs(field.Name) is not null || existing.Source == RpgFieldSource.Constant ||
                            existing.Kind != field.Kind || existing.Length != field.Length || existing.Decimals != field.Decimals)
                            throw new RpgCompileException(declaration.Line, "Incompatible external field declaration: " + field.Name);
                        existing.OpaqueBytes |= field.Opaque;
                    }
                    else program.Fields.Add(new() { Name = field.Name, Kind = field.Kind, Length = field.Length, Decimals = field.Decimals, Source = RpgFieldSource.ExternalFile, OpaqueBytes = field.Opaque });
                }
                foreach (var subfile in metadataOnly || request.IsTemplate ? [] : subfiles)
                {
                    var rrn = program.FindField(subfile.RelativeRecord);
                    var size = layout.Records.Single(r => r.Name == RpgWorkstationBindings.RecordName(request, subfile.Record)).Capacity;
                    if (rrn is null || rrn.IsTemplate || rrn.IsArray || rrn.Decimals != 0 || rrn.Kind is not (RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Decimal or RpgFieldKind.Integer or RpgFieldKind.Binary) ||
                        rrn.Length < size.ToString(CultureInfo.InvariantCulture).Length)
                        throw new RpgCompileException(declaration.Line, "SFILE RRN requires a sufficiently large numeric scalar with zero decimals.");
                }
                program.Workstations.Add(binding); if (!request.IsTemplate) program.FileSpecs.Add(declaration.Name);
            }
            catch (Exception error) when (error is InvalidDataException or Ipc.Core.Messages.CpfException)
            { throw new RpgCompileException(declaration.Line, error.Message); }
        }
        program.Workstations.AddRange(parameters);
        if (program.Workstations.Count > 16) throw new RpgCompileException(0, "At most 16 visible WORKSTN files, including file parameters, are supported.");
    }

    private static void ValidateWorkstationStatements(RpgProgram program)
    {
        foreach (var statement in program.MainStatements)
        {
            if (statement.Opcode is not (RpgOpcode.ExFmt or RpgOpcode.ReadC or RpgOpcode.Read or RpgOpcode.Write or RpgOpcode.Update or RpgOpcode.Chain or RpgOpcode.Open or RpgOpcode.Close or
                RpgOpcode.ReadP or RpgOpcode.Reade or RpgOpcode.ReadPe or RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Delete)) continue;
            var operands = statement.RecordDataStructure is not null
                ? statement.Opcode == RpgOpcode.Chain ? (statement.Factor1 ?? "") + " " + (statement.Factor2 ?? "") : statement.Factor2 ?? statement.Factor1 ?? ""
                : statement.Value ?? string.Join(" ", new[] { statement.Factor1, statement.Factor2, statement.Result }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var tokens = Regex.Matches(operands.ToUpperInvariant(), @"[A-Z_][A-Z0-9_]*").Select(m => m.Value).ToArray();
            var name = (statement.Factor2 ?? statement.Factor1 ?? "").Trim().ToUpperInvariant();
            if (program.FindWorkstation(name) is null && program.FindDisk(name) is not null) continue;
            var binding = program.FindWorkstation(name) ??
                program.Workstations.FirstOrDefault(w => tokens.Contains(w.Request.Name) || w.Layout.Records.Any(r => tokens.Contains(r.Name)));
            if (binding is null)
            {
                if (statement.Opcode is RpgOpcode.ExFmt or RpgOpcode.ReadC)
                    throw new RpgCompileException(statement.LineNumber, "EXFMT/READC requires a declared WORKSTN record format.");
                continue;
            }
            if (binding.Request.IsTemplate) throw new RpgCompileException(statement.LineNumber, "A TEMPLATE file has no runtime I/O.");
            if ((binding.Request.Qualified || binding.Request.Procedure is not null) && statement.Opcode is not (RpgOpcode.Open or RpgOpcode.Close) && statement.RecordDataStructure is null)
                throw new RpgCompileException(statement.LineNumber, "Qualified and LIKEFILE WORKSTN files require result data structures.");
            if (statement.Opcode is not (RpgOpcode.ExFmt or RpgOpcode.ReadC or RpgOpcode.Read or RpgOpcode.Write or RpgOpcode.Update or RpgOpcode.Chain or RpgOpcode.Open or RpgOpcode.Close or
                RpgOpcode.ReadP or RpgOpcode.Reade or RpgOpcode.ReadPe or RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Delete)) continue;
            if (statement.Opcode is RpgOpcode.ReadP or RpgOpcode.Reade or RpgOpcode.ReadPe or RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Delete)
                throw new RpgCompileException(statement.LineNumber, "Unsupported WORKSTN operation " + statement.Opcode + ".");
            if (name.Length == 0) name = (statement.Factor1 ?? "").Trim().ToUpperInvariant();
            var format = binding.Layout.Records.SingleOrDefault(r => r.Name == name);
            var expected = statement.Opcode == RpgOpcode.Chain ? @"\A(?:\([^)]*\)|\S+)\s+" + Regex.Escape(name) + @"\z" : @"\A" + Regex.Escape(name) + @"\z";
            if (!Regex.IsMatch(operands.ToUpperInvariant(), expected) || statement.Opcode is RpgOpcode.Open or RpgOpcode.Close && name != binding.Request.Name ||
                statement.Opcode is not (RpgOpcode.Open or RpgOpcode.Close or RpgOpcode.Read) && format is null ||
                statement.Opcode == RpgOpcode.ExFmt && (format?.Subfile != false || !binding.Request.Input || !binding.Request.Output) ||
                statement.Opcode is RpgOpcode.ReadC or RpgOpcode.Chain or RpgOpcode.Update && format?.Subfile != true ||
                statement.Opcode == RpgOpcode.Read && format?.Subfile == true)
                throw new RpgCompileException(statement.LineNumber, "Unsupported WORKSTN operation, record or result data structure.");
        }
    }
}
