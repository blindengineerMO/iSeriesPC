using System.Globalization;
using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;
using Ipc.Core.Compilation;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    public static RpgProgram Compile(string library, string name, string source, CancellationToken cancellationToken = default,
        Func<RpgWorkstationRequest, RpgWorkstationLayout>? workstationResolver = null,
        Func<RpgDiskRequest, RpgDiskLayout>? diskResolver = null)
        => Compile(library, name, new SourceDocument(library + "/" + name, source), cancellationToken: cancellationToken, workstationResolver: workstationResolver, diskResolver: diskResolver);

    public static RpgProgram Compile(string library, string name, SourceDocument source,
        Func<SourceDocument, string, SourceDocument>? resolver = null, CancellationToken cancellationToken = default,
        Func<RpgWorkstationRequest, RpgWorkstationLayout>? workstationResolver = null,
        Func<RpgDiskRequest, RpgDiskLayout>? diskResolver = null)
    {
        try { return Compile(library, name, new RpgSourcePreprocessor(resolver).Expand(source, cancellationToken), cancellationToken, workstationResolver, diskResolver); }
        catch (SourcePreprocessException error) { throw new RpgCompileException(error.Location, error.Message); }
    }

    public static RpgProgram Compile(string library, string name, PreprocessedSource source, CancellationToken cancellationToken = default,
        Func<RpgWorkstationRequest, RpgWorkstationLayout>? workstationResolver = null,
        Func<RpgDiskRequest, RpgDiskLayout>? diskResolver = null)
        => CompileUnit(library, name, source, cancellationToken, workstationResolver, diskResolver);

    private static RpgProgram CompileUnit(string library, string name, PreprocessedSource source, CancellationToken cancellationToken,
        Func<RpgWorkstationRequest, RpgWorkstationLayout>? workstationResolver, Func<RpgDiskRequest, RpgDiskLayout>? diskResolver,
        List<string>? preparedLines = null, RpgProgram? globals = null, string? procedureName = null, int procedureLine = 0,
        ForwardDeclarationTypes? forwardTypes = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var program = new RpgProgram { Name = name.ToUpperInvariant(), Library = library.ToUpperInvariant(), Source = source, GlobalProgram = globals, NativeProcedureName = procedureName, NativeEntryLine = procedureLine };
        if (globals is not null) ImportGlobalSymbols(program, globals);
        try
        {
            var specificationLines = preparedLines ?? ReadSpecs(ExtractCompileTimeData(program, source, cancellationToken));
            var freeForm = specificationLines.Any(l =>
                l.TrimStart().StartsWith("**free", StringComparison.OrdinalIgnoreCase));
            program.IsFreeForm = freeForm;
            var nativeProcedures = globals is null ? ExtractNativeProcedures(specificationLines, freeForm, cancellationToken) : [];
            program.NativeProcedureNames.UnionWith(nativeProcedures.Select(p => p.Name));
            forwardTypes ??= new(program, specificationLines, freeForm, cancellationToken);
            forwardTypes.Attach(program);
            foreach (var definition in nativeProcedures) forwardTypes.Child(definition.Name, definition.Lines);
            diskResolver = CachedDiskResolver(diskResolver);
            var workstations = ParseWorkstationDeclarations(specificationLines, program, freeForm, cancellationToken, diskResolver: diskResolver);
            ParseDeclarations(specificationLines, program, cancellationToken);
            if (program.IsNativeProcedure && !program.HasNativeEntryInterface)
            { program.HasNativeEntryInterface = true; program.NativeParameterLists.Add("/*ENTRY", Array.Empty<string>()); }
            ValidateNativeProgramSignatures(program);
            BindCompileTimeData(program, cancellationToken);
            ParseFileErrorHandlers(program, workstations);
            ParseIndicatorBindings(program, workstations);
            BindFeedbackStructures(program, workstations);
            BindWorkstations(program, workstations.Where(d => !d.Disk).ToArray(), workstationResolver);
            BindDisks(program, workstations.Where(d => d.Disk).ToArray(), diskResolver);
            foreach (var field in program.Fields.Where(f => f.NativeScalar && !f.IsTemplate && !program.GlobalFields.Contains(f)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeclareNativeStandalone(program, field, field.DeclarationLine);
            }
            ValidateIndicatorBindings(program);
            program.FileSpecs.Clear(); program.FileSpecs.AddRange(workstations.Where(d => !d.Template).Select(d => d.Name));
            foreach (var definition in nativeProcedures)
            {
                var body = CompileUnit(library, name, source, cancellationToken, workstationResolver, diskResolver, definition.Lines, program, definition.Name, definition.Line, forwardTypes.Child(definition.Name));
                program.Subprocedures.Add(new() { Name = definition.Name, NativeBody = body, IsExport = definition.Export });
            }
            if (globals is null) LinkNativeProcedures(program);
            var keyDeclarations = ParseKeyDeclarations(specificationLines, program, freeForm, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ParseStatements(specificationLines, program, freeForm, cancellationToken);
            ValidateRecordIo(program);
            ValidateTemplateUses(program, keyDeclarations.Lists);
            ValidateNativeReturns(program);
            ValidateProcedureExpressions(program);
            ValidateNativeParameterLists(program);
            ValidateNativeProgramCalls(program);
            ValidateReadOnlyParameters(program);
            ValidateParameterBuiltins(program);
            foreach (var statement in program.MainStatements.Where(s => s.ClearAllOccurrences))
                if (program.FindDs(statement.Result ?? "") is not { NativeStorage: true, IsMultipleOccurrence: true })
                    throw new RpgCompileException(statement.LineNumber, "CLEAR *ALL requires a named multiple-occurrence data structure.");
            BindFileSearches(program, keyDeclarations.Lists, keyDeclarations.Scopes, cancellationToken);
            ValidateWorkstationStatements(program);
            ValidateDiskStatements(program);
            ValidateProcedureFiles(program);
            foreach (var statement in program.MainStatements)
                if (statement.LineNumber > 0 && statement.LineNumber <= source.Locations.Count)
                    statement.Location = source.Locations[statement.LineNumber - 1];
            cancellationToken.ThrowIfCancellationRequested();
            RpgStructureValidator.Validate(program.MainStatements, cancellationToken);
            ExtractSubroutines(program);
            ExtractSubprocedures(program);
            RpgStructureValidator.ValidateLabels(program.MainStatements);
            foreach (var routine in program.Subroutines) RpgStructureValidator.ValidateLabels(routine.Statements);
            foreach (var procedure in program.Subprocedures) RpgStructureValidator.ValidateLabels(procedure.Statements);
            ResolveBlocks(program.MainStatements);
            foreach (var subroutine in program.Subroutines)
            {
                ResolveBlocks(subroutine.Statements);
            }

            foreach (var subprocedure in program.Subprocedures)
            {
                ResolveBlocks(subprocedure.Statements);
            }

            ValidateErrorSubroutines(program);

            AssignDsOffsets(program);
            return program;
        }
        catch (RpgCompileException error) when (error.Location is null && error.LineNumber > 0 && error.LineNumber <= source.Locations.Count)
        { throw new RpgCompileException(source.Locations[error.LineNumber - 1], error.Message); }
        finally { forwardTypes?.Detach(); program.RecordLayoutsLookup = null; }
    }

    private static List<string> ReadSpecs(string source)
    {
        var lines = new List<string>();
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                lines.Add(string.Empty);
                continue;
            }

            lines.Add(line);
        }

        return lines;
    }

    private static void ParseDeclarations(List<string> lines, RpgProgram program, CancellationToken cancellationToken)
    {
        RpgDataStructure? currentDs = null;
        RpgSubprocedure? currentProc = null;
        RpgPrototype? currentPrototype = null;
        var prototypeParamIndex = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[index];
            var spec = Specification(line);
            if (spec is 'D' or 'P' && !FixedShape(line, spec)) continue;
            if (spec != 'D' && spec != 'P' && !line.TrimStart().StartsWith("**free", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (spec == 'P')
            {
                var marker = line.Length >= 24 ? line[23] : ' ';
                var procName = Col(line, 8, 21).Trim();
                currentDs = null;
                currentPrototype = null;
                if (marker == 'B' && procName.Length > 0)
                {
                    currentProc = new RpgSubprocedure { Name = procName.ToUpperInvariant() };
                    program.Subprocedures.Add(currentProc);
                }
                else if (marker == 'E')
                {
                    currentProc = null;
                }

                continue;
            }

            if (spec != 'D')
            {
                continue;
            }

            var name = Col(line, 8, 20).Trim();
            var type = Col(line, 22, 23).Trim().ToUpperInvariant();
            var lengthText = Col(line, 39, 40).Trim();
            var decimalsText = Col(line, 41, 42).Trim();
            var keywords = Col(line, 51, 80).Trim();
            if (type is not ("" or "S" or "A" or "P" or "B" or "I" or "F" or "G" or "D" or "T" or "Z" or "C" or "DS" or "PR" or "PI"))
                throw new RpgCompileException(index + 1, "Unsupported RPG D-spec type " + type + ".");

            if (type == "DS")
            {
                currentDs = new RpgDataStructure { Name = name };
                if (int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
                {
                    currentDs.Length = length;
                }

                currentDs.Dimension = ParseKeywordInt(keywords, "DIM") ?? 1;
                program.DataStructures.Add(currentDs);
                currentPrototype = null;
                continue;
            }

            if (type is "PR" or "PI")
            {
                currentDs = null;
                prototypeParamIndex = 0;
                currentPrototype = new RpgPrototype
                {
                    Name = name.ToUpperInvariant(),
                    ExternalName = ParseExternalName(keywords),
                };
                program.Prototypes.Add(currentPrototype);
                continue;
            }

            if (currentDs is not null && type.Length == 0 && name.Length > 0)
            {
                var element = ParseDsElement(name, lengthText, decimalsText, keywords, currentDs);
                currentDs.Elements.Add(element);
                continue;
            }

            currentDs = null;
            if (currentPrototype is not null && (type.Length == 0 || HasKeyword(keywords, "PARM")))
            {
                prototypeParamIndex++;
                // Named formal parameters describe a call layout, not a caller variable.
                var storageName = name.Length == 0 ? $"P{prototypeParamIndex}" : $"__PR{program.Prototypes.Count}_{prototypeParamIndex}";
                if (program.FindField(storageName) is not null) storageName = $"__PR{program.Prototypes.Count}_{prototypeParamIndex}";
                var parameterField = ParseStandalone(
                    storageName, type, lengthText, decimalsText, keywords);
                if (parameterField is not null)
                {
                    program.Fields.Add(parameterField);
                    currentPrototype.Parameters.Add(parameterField.Name);
                    currentPrototype.ParameterKeywords.Add(parameterField.Name, keywords);
                }

                continue;
            }

            currentPrototype = null;
            var field = ParseStandalone(name, type, lengthText, decimalsText, keywords);
            if (field is not null)
            {
                program.Fields.Add(field);
                if (currentProc is not null)
                {
                    currentProc.Parameters.Add(field.Name);
                }
            }
        }
    }

    private static string? ParseExternalName(string keywords)
    {
        var match = Regex.Match(keywords, @"(?i)EXTPROC\(\s*'([^']*)'\s*\)");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = Regex.Match(keywords, @"(?i)EXTPGM\(\s*'([^']*)'\s*\)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static RpgDsElement ParseDsElement(string name, string lengthText, string decimalsText,
        string keywords, RpgDataStructure owner)
    {
        var length = ParseLength(lengthText, decimalsText, out var decimals);
        var element = new RpgDsElement
        {
            Name = name,
            Kind = !string.IsNullOrWhiteSpace(decimalsText) ? RpgFieldKind.Zoned : RpgFieldKind.Character,
            Length = length,
            Decimals = decimals,
        };

        var overlay = MatchKeyword(keywords, "OVERLAY");
        if (overlay is not null)
        {
            var target = overlay;
            var colon = overlay.IndexOf(':');
            if (colon >= 0)
            {
                target = overlay[..colon];
                if (int.TryParse(overlay[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
                {
                    element.Offset = offset - 1;
                    element.HasExplicitOffset = true;
                    return element;
                }
            }

            var baseElement = owner.Find(target);
            if (baseElement is not null)
            {
                element.Offset = baseElement.Offset;
                element.HasExplicitOffset = true;
            }
        }

        return element;
    }

    private static RpgField? ParseStandalone(string name, string type, string lengthText, string decimalsText, string keywords)
    {
        if (name.Length == 0)
        {
            return null;
        }

        if (type == "C")
        {
            return new RpgField
            {
                Name = name,
                Kind = RpgFieldKind.Character,
                Length = ParseLength(lengthText, decimalsText, out var decimals),
                Decimals = decimals,
                Source = RpgFieldSource.Constant,
                InitialValue = ParseConstantValue(name, keywords),
            };
        }

        var length = ParseLength(lengthText, decimalsText, out var decimalPositions);
        var kind = !string.IsNullOrWhiteSpace(decimalsText) ? RpgFieldKind.Zoned : RpgFieldKind.Character;
        if (type is "P")
        {
            kind = RpgFieldKind.Packed;
        }
        else if (type is "B" or "I")
        {
            kind = RpgFieldKind.Binary;
        }
        else if (type is "F" or "G")
        {
            kind = RpgFieldKind.Float;
        }
        else if (type is "D")
        {
            kind = RpgFieldKind.Date;
            length = 10;
        }
        else if (type is "T")
        {
            kind = RpgFieldKind.Time;
            length = 8;
        }
        else if (type is "Z")
        {
            kind = RpgFieldKind.Timestamp;
            length = 26;
        }

        if (HasKeyword(keywords, "PROCPTR"))
        {
            kind = RpgFieldKind.ProcPtr;
            length = Math.Max(1, length);
        }

        var field = new RpgField
        {
            Name = name,
            Kind = kind,
            Length = length,
            Decimals = decimalPositions,
            InitialValue = ParseInitialValue(keywords),
            Varying = HasKeyword(keywords, "VARYING"),
        };
        field.Dimension = ParseKeywordInt(keywords, "DIM") ?? 1;
        field.IsArray = field.Dimension > 1;
        field.Source = RpgFieldSource.Standalone;
        return field;
    }

    private static int ParseLength(string lengthText, string decimalsText, out int decimals)
    {
        decimals = int.TryParse(decimalsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        return int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) && length > 0
            ? length
            : Math.Max(1, decimals + (decimals > 0 ? 1 : 1));
    }

    private static string? ParseInitialValue(string keywords)
    {
        var initial = MatchKeyword(keywords, "INZ");
        if (initial is null)
        {
            return null;
        }

        return Unquote(initial);
    }

    private static string? ParseConstantValue(string name, string keywords)
    {
        var constant = MatchKeyword(keywords, "CONST");
        if (constant is not null)
        {
            return Unquote(constant);
        }

        return decimal.TryParse(name, NumberStyles.Any, CultureInfo.InvariantCulture, out _) ? name : Unquote(name);
    }

    private static string? MatchKeyword(string keywords, string keyword)
    {
        var pattern = keyword + @"(?:\((.*?)\))?";
        var match = Regex.Match(keywords, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && match.Groups[1].Success ? match.Groups[1].Value : null;
    }

    private static bool HasKeyword(string keywords, string keyword) =>
        Regex.IsMatch(keywords, @"(?:^|[\s,])(?i)" + keyword + @"(?:\s|\)|$|,)", RegexOptions.CultureInvariant);

    private static int? ParseKeywordInt(string keywords, string keyword)
    {
        var value = MatchKeyword(keywords, keyword);
        return value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1];
        }

        return value;
    }

    private static void ParseStatements(List<string> lines, RpgProgram program, bool globalFree, CancellationToken cancellationToken)
    {
        var inFreeBlock = false;
        var freeStart = 0;
        var globalMarkerSeen = false;
        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var lineNumber = index + 1;
            if (IsNativeCalculation(line))
            {
                if (globalFree || inFreeBlock) throw new RpgCompileException(lineNumber, "Native fixed calculations cannot appear inside a free region.");
                if (Col(line, 7, 80).All(c => c == ' ')) continue;
                program.MainStatements.Add(ParseNativeCalculation(lines, ref index, program, cancellationToken)); continue;
            }
            var spec = Specification(line);
            var trimmed = line.TrimStart();
            if ((globalFree || inFreeBlock) && !FixedShape(line, spec)) spec = ' ';
            if (trimmed.StartsWith("**free", StringComparison.OrdinalIgnoreCase))
            {
                if (!trimmed.Equals("**free", StringComparison.OrdinalIgnoreCase) || globalMarkerSeen)
                    throw new RpgCompileException(lineNumber, "Invalid or repeated **FREE marker.");
                globalMarkerSeen = true;
                continue;
            }

            if (trimmed.Equals("/free", StringComparison.OrdinalIgnoreCase))
            {
                if (globalFree || inFreeBlock) throw new RpgCompileException(lineNumber, "Nested /FREE or /FREE in a **FREE program is unsupported.");
                inFreeBlock = true;
                freeStart = lineNumber;
                continue;
            }

            if (trimmed.Equals("/end-free", StringComparison.OrdinalIgnoreCase))
            {
                if (globalFree || !inFreeBlock) throw new RpgCompileException(lineNumber, "/END-FREE without a matching /FREE.");
                inFreeBlock = false;
                continue;
            }

            if (spec == 'P')
            {
                var marker = line.Length >= 24 ? line[23] : ' ';
                if (marker is 'B' or 'E')
                {
                    program.MainStatements.Add(new RpgStatement
                    {
                        Opcode = marker == 'B' ? RpgOpcode.BegProc : RpgOpcode.EndProc,
                        Factor1 = Col(line, 8, 21).Trim(),
                        LineNumber = lineNumber,
                    });
                }
                else throw new RpgCompileException(lineNumber, "Unsupported RPG P-spec marker; use B or E.");

                continue;
            }

            if (spec == 'D')
            {
                if (!FixedShape(line, spec)) throw new RpgCompileException(lineNumber, "Malformed fixed RPG D specification.");
                continue;
            }
            if (spec != ' ' && spec != 'C')
                throw new RpgCompileException(lineNumber, "Unsupported RPG specification " + spec + ".");

            if (globalFree || inFreeBlock)
            {
                if (spec == 'C') throw new RpgCompileException(lineNumber, "Fixed calculation specifications are unsupported inside a free region.");
                if (trimmed == ";") continue;
                program.MainStatements.Add(ParseFreeStatement(line, program, lineNumber));
            }
            else if (spec == 'C')
            {
                if (line[7..].Trim().Length == 0) continue;
                program.MainStatements.Add(ParseFixedStatement(line, lineNumber));
            }
            else throw new RpgCompileException(lineNumber, "Expected a supported fixed D/P/C specification or /FREE region.");
        }
        if (inFreeBlock) throw new RpgCompileException(freeStart, "/FREE without a matching /END-FREE.");
    }

    private static RpgStatement ParseFixedStatement(string line, int lineNumber)
    {
        var conditions = ParseConditions(Col(line, 8, 17));
        var factor1 = Col(line, 18, 35).Trim();
        var opcode = Col(line, 36, 42).Trim().ToUpperInvariant();
        var (noLock, errorExtender) = ParseFileExtenders(ref opcode, lineNumber);
        var mapped = MapOpcode(opcode);
        if (mapped == RpgOpcode.None) throw new RpgCompileException(lineNumber, opcode.Length == 0 ? "Missing fixed RPG operation." : "Unsupported fixed RPG operation " + opcode + ".");
        var factor2 = Col(line, 43, 47).Trim();
        var result = Col(line, 48, 58).Trim();
        var length = Col(line, 53, 58).Trim();
        var indicator1 = ParseIndicator(Col(line, 65, 66));
        var indicator2 = ParseIndicator(Col(line, 67, 68));
        var indicator3 = ParseIndicator(Col(line, 69, 70));

        var statement = new RpgStatement
        {
            Opcode = mapped,
            ClearAllOccurrences = mapped == RpgOpcode.Clear && factor2.Equals("*ALL", StringComparison.OrdinalIgnoreCase),
            NoLock = noLock,
            ErrorExtender = errorExtender,
            Factor1 = factor1,
            Factor2 = factor2,
            Result = result,
            Length = length,
            LineNumber = lineNumber,
            Indicator1 = indicator1,
            Indicator2 = indicator2,
            Indicator3 = indicator3,
        };
        if (errorExtender && indicator2 != 0) throw new RpgCompileException(lineNumber, "E and an error indicator cannot be combined.");
        statement.Conditions.AddRange(conditions);
        if (mapped is RpgOpcode.BegSr or RpgOpcode.Exsr or RpgOpcode.Goto or RpgOpcode.Tag)
        {
            var names = new[] { factor1, factor2, result }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (names.Length > 1) throw new RpgCompileException(lineNumber, "RPG " + opcode + " accepts a single name operand.");
            statement.Factor1 = names.FirstOrDefault();
        }
        if (statement.Opcode is RpgOpcode.If or RpgOpcode.When && !string.IsNullOrWhiteSpace(statement.Factor2))
        {
            statement.Factor1 = (statement.Factor1 + " " + statement.Factor2).Trim();
            statement.Factor2 = null;
        }

        return statement;
    }

    private static RpgStatement ParseFreeStatement(string line, RpgProgram program, int lineNumber)
    {
        var text = line.Trim().TrimStart('*').Trim();
        if (text.EndsWith(';'))
        {
            text = text[..^1].TrimEnd();
        }

        var space = text.IndexOfAny(new[] { ' ', '\t' });
        var opcode = (space >= 0 ? text[..space] : text).Trim().ToUpperInvariant();
        var operands = space >= 0 ? text[(space + 1)..].Trim() : string.Empty;
        var callError = opcode == "CALLP(E)";
        if (opcode.StartsWith("CALLP(", StringComparison.Ordinal) && !callError)
            throw new RpgCompileException(lineNumber, "CALLP currently supports only the E extender.");
        if (callError) opcode = "CALLP";
        var (noLock, errorExtender) = ParseFileExtenders(ref opcode, lineNumber);
        var mapped = MapFreeOpcode(opcode);
        if (mapped == RpgOpcode.None && System.Text.RegularExpressions.Regex.Match(text, @"\A([A-Za-z_][A-Za-z0-9_]*)\s*\(.*\)\z") is { Success: true } implicitCall &&
            (program.FindPrototype(implicitCall.Groups[1].Value) is { NativeProcedure: true } || program.NativeProcedureNames.Contains(implicitCall.Groups[1].Value) || program.GlobalProgram?.NativeProcedureNames.Contains(implicitCall.Groups[1].Value) == true))
        { mapped = RpgOpcode.CallP; operands = text; }
        if (mapped == RpgOpcode.None) throw new RpgCompileException(lineNumber, "Unsupported free RPG operation " + (opcode.Length == 0 ? "(empty)" : opcode) + ".");
        if (mapped == RpgOpcode.CallP && operands.Length == 0) throw new RpgCompileException(lineNumber, "CALLP requires a call target.");
        if (operands.Length > 0 && mapped is (RpgOpcode.Else or RpgOpcode.EndIf or RpgOpcode.Select or RpgOpcode.Other or RpgOpcode.EndSl or
            RpgOpcode.EndDo or RpgOpcode.Iter or RpgOpcode.Leave or RpgOpcode.Retrn or RpgOpcode.Return or RpgOpcode.Monitor or RpgOpcode.EndMon) &&
            !(mapped == RpgOpcode.Return && program.IsNativeProcedure && program.NativeReturnField is not null))
            throw new RpgCompileException(lineNumber, "Operands are unsupported for free RPG " + opcode + ".");
        if (operands.Length == 0 && mapped is (RpgOpcode.If or RpgOpcode.When or RpgOpcode.DoU or RpgOpcode.DoW or RpgOpcode.For))
            throw new RpgCompileException(lineNumber, "Free RPG " + opcode + " requires a condition or loop expression.");

        var statement = new RpgStatement
        {
            Opcode = mapped,
            NoLock = noLock,
            ErrorExtender = errorExtender || callError,
            Factor1 = null,
            Factor2 = null,
            Result = null,
            Value = operands.Length > 0 ? operands : null,
            IsFreeForm = true,
            SortDescending = mapped == RpgOpcode.SortA ? opcode == "SORTA(D)" ? true : opcode == "SORTA(A)" ? false : null : null,
            LineNumber = lineNumber,
        };

        switch (statement.Opcode)
        {
            case RpgOpcode.SortA:
                ParseArraySort(program, statement, operands, lineNumber);
                break;
            case RpgOpcode.Eval:
            case RpgOpcode.If:
            case RpgOpcode.When:
            case RpgOpcode.DoW:
            case RpgOpcode.DoU:
            case RpgOpcode.CallB:
            case RpgOpcode.CallP:
            case RpgOpcode.Else:
            case RpgOpcode.EndIf:
            case RpgOpcode.Select:
            case RpgOpcode.Other:
            case RpgOpcode.EndSl:
            case RpgOpcode.Do:
            case RpgOpcode.For:
            case RpgOpcode.EndDo:
            case RpgOpcode.Iter:
            case RpgOpcode.Leave:
            case RpgOpcode.Return:
            case RpgOpcode.Retrn:
            case RpgOpcode.Monitor:
            case RpgOpcode.EndMon:
            case RpgOpcode.Parm:
            case RpgOpcode.Plist:
            case RpgOpcode.None:
                break;

            case RpgOpcode.EndSr:
                statement.Factor2 = statement.Value;
                break;

            case RpgOpcode.OnError:
            case RpgOpcode.BegSr:
            case RpgOpcode.Tag:
            case RpgOpcode.Exsr:
            case RpgOpcode.Goto:
            case RpgOpcode.Dsply:
            case RpgOpcode.SndPgMmsg:
            case RpgOpcode.SndMsg:
            case RpgOpcode.SetOn:
            case RpgOpcode.SetOff:
            case RpgOpcode.Test:
            case RpgOpcode.Open:
            case RpgOpcode.Close:
            case RpgOpcode.Unlock:
            case RpgOpcode.Write:
            case RpgOpcode.ExFmt:
            case RpgOpcode.Update:
                if (!ParseRecordIo(operands, statement, program)) statement.Factor1 = operands;
                break;

            case RpgOpcode.Call:
            case RpgOpcode.Force:
                SplitCall(operands, statement);
                break;

            case RpgOpcode.Delete:
            case RpgOpcode.Chain:
            case RpgOpcode.SetLl:
            case RpgOpcode.SetGt:
            case RpgOpcode.Read:
            case RpgOpcode.ReadC:
            case RpgOpcode.Reade:
            case RpgOpcode.ReadP:
            case RpgOpcode.ReadPe:
                if (!ParseRecordIo(operands, statement, program)) ParseKeyFile(operands, statement, program);
                break;

            case RpgOpcode.Move:
            case RpgOpcode.MoveL:
            case RpgOpcode.MoveA:
                statement.Factor1 = statement.Value != null ? FirstOperand(statement.Value) : null;
                statement.Result = statement.Value != null ? RestOperands(statement.Value) : null;
                break;

            case RpgOpcode.Comp:
                statement.Factor1 = statement.Value != null ? FirstOperand(statement.Value) : null;
                statement.Factor2 = statement.Value != null ? RestOperands(statement.Value) : null;
                break;

            case RpgOpcode.Clear:
                var clearAll = Regex.Match(operands, @"\A\*ALL\s+(.+)\z", RegexOptions.IgnoreCase);
                statement.ClearAllOccurrences = clearAll.Success;
                statement.Result = clearAll.Success ? clearAll.Groups[1].Value.Trim() : operands;
                break;
            case RpgOpcode.RcvMsg:
                statement.Result = operands;
                break;
        }

        return statement;
    }

    private static void SplitCall(string operands, RpgStatement statement)
    {
        operands = operands.Trim();
        if (operands.StartsWith('('))
        {
            var end = operands.IndexOf(')');
            if (end > 0)
            {
                statement.Factor2 = operands[1..end].Trim();
                statement.Factor1 = operands[(end + 1)..].Trim();
                return;
            }
        }

        SplitTwo(operands, statement);
    }

    private static string FirstOperand(string operands)
    {
        var parts = operands.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : string.Empty;
    }

    private static string RestOperands(string operands)
    {
        var parts = operands.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : string.Empty;
    }

    private static void SplitTwo(string operands, RpgStatement statement)
    {
        var parts = operands.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            statement.Factor1 = parts[0];
            statement.Factor2 = parts[1];
        }
        else if (parts.Length == 1)
        {
            statement.Factor1 = parts[0];
        }
    }

    private static List<RpgCondition> ParseConditions(string column)
    {
        var conditions = new List<RpgCondition>();
        var tokens = column.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var negated = token.StartsWith('N');
            var digits = negated ? token[1..] : token;
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var indicator) &&
                indicator is >= 0 and <= 99)
            {
                conditions.Add(new RpgCondition { Indicator = indicator, Negated = negated });
            }
        }

        return conditions;
    }

    private static int ParseIndicator(string column)
    {
        var text = column.Trim();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var indicator) &&
               indicator is >= 0 and <= 99
            ? indicator
            : 0;
    }

    private static string Col(string line, int startColumn, int endColumn)
    {
        var start = Math.Min(startColumn - 1, line.Length);
        var end = Math.Min(endColumn, line.Length);
        return start >= end ? string.Empty : line[start..end];
    }

    private static char Specification(string line) => line.Length >= 7 && line[..6].All(c => c == ' ' || char.IsAsciiDigit(c))
        ? char.ToUpperInvariant(line[6]) : ' ';

    private static bool FixedShape(string line, char spec) => spec switch
    {
        'D' => line.Length >= 23 && !Col(line, 8, 20).Trim().Any(char.IsWhiteSpace) && Col(line, 24, 38).All(char.IsWhiteSpace),
        'P' => line.Length >= 24 && !Col(line, 8, 21).Trim().Any(char.IsWhiteSpace),
        'C' => line.Length >= 7 && (line[7..].Trim().Length == 0 ||
            line.Length >= 36 && Col(line, 8, 17).All(c => char.IsWhiteSpace(c) || char.IsAsciiDigit(c) || c is 'N' or 'n')),
        _ => false,
    };

    private static (bool NoLock, bool Error) ParseFileExtenders(ref string opcode, int line)
    {
        var opening = opcode.IndexOf('(');
        if (opening < 0) return (false, false);
        var operation = opcode[..opening];
        var input = operation is "CHAIN" or "READ" or "READP" or "READE" or "READPE";
        if (!input && operation is not ("OPEN" or "CLOSE" or "SETLL" or "SETGT" or "WRITE" or "UPDATE" or "DELETE" or "UNLOCK" or "EXFMT" or "READC"))
            return (false, false);
        var match = Regex.Match(opcode, @"\A[A-Z]+\(([EN]+)\)\z", RegexOptions.CultureInvariant);
        var extenders = match.Success ? match.Groups[1].Value : "";
        if (extenders.Length == 0 || extenders.Distinct().Count() != extenders.Length || extenders.Contains('N') && !input)
            throw new RpgCompileException(line, "File operations support E and, on update input, N; extenders cannot repeat.");
        opcode = operation; return (extenders.Contains('N'), extenders.Contains('E'));
    }
    private static RpgOpcode MapFreeOpcode(string opcode) => opcode switch
    {
        "EVAL" => RpgOpcode.Eval,
        "CALLP" => RpgOpcode.CallP,
        "CALLB" => RpgOpcode.CallB,
        "CALL" => RpgOpcode.Call,
        "IF" => RpgOpcode.If,
        "ELSE" => RpgOpcode.Else,
        "ENDIF" => RpgOpcode.EndIf,
        "SELECT" => RpgOpcode.Select,
        "WHEN" => RpgOpcode.When,
        "OTHER" => RpgOpcode.Other,
        "ENDSL" => RpgOpcode.EndSl,
        "DO" => RpgOpcode.Do,
        "DOU" => RpgOpcode.DoU,
        "DOW" => RpgOpcode.DoW,
        "FOR" => RpgOpcode.For,
        "ENDFOR" => RpgOpcode.EndDo,
        "ENDDO" => RpgOpcode.EndDo,
        "ITER" => RpgOpcode.Iter,
        "LEAVE" => RpgOpcode.Leave,
        "GOTO" => RpgOpcode.Goto,
        "TAG" => RpgOpcode.Tag,
        "BEGSR" => RpgOpcode.BegSr,
        "ENDSR" => RpgOpcode.EndSr,
        "EXSR" => RpgOpcode.Exsr,
        "ON-ERROR" => RpgOpcode.OnError,
        "MONITOR" => RpgOpcode.Monitor,
        "ENDMON" => RpgOpcode.EndMon,
        "RETURN" => RpgOpcode.Return,
        "RETRN" => RpgOpcode.Retrn,
        "MOVE" => RpgOpcode.Move,
        "MOVEL" => RpgOpcode.MoveL,
        "MOVA" => RpgOpcode.MoveA,
        "SORTA" => RpgOpcode.SortA,
        "SORTA(A)" => RpgOpcode.SortA,
        "SORTA(D)" => RpgOpcode.SortA,
        "ZADD" => RpgOpcode.ZAdd,
        "ZSUB" => RpgOpcode.ZSub,
        "CLEAR" => RpgOpcode.Clear,
        "SETON" => RpgOpcode.SetOn,
        "SETOFF" => RpgOpcode.SetOff,
        "BITON" => RpgOpcode.BitOn,
        "BITOFF" => RpgOpcode.BitOff,
        "TEST" => RpgOpcode.Test,
        "CHAIN" => RpgOpcode.Chain,
        "READ" => RpgOpcode.Read,
        "READC" => RpgOpcode.ReadC,
        "EXFMT" => RpgOpcode.ExFmt,
        "READE" => RpgOpcode.Reade,
        "READP" => RpgOpcode.ReadP,
        "READPE" => RpgOpcode.ReadPe,
        "SETLL" => RpgOpcode.SetLl,
        "SETGT" => RpgOpcode.SetGt,
        "WRITE" => RpgOpcode.Write,
        "UPDATE" => RpgOpcode.Update,
        "DELETE" => RpgOpcode.Delete,
        "OPEN" => RpgOpcode.Open,
        "CLOSE" => RpgOpcode.Close,
        "UNLOCK" => RpgOpcode.Unlock,
        "FORCE" => RpgOpcode.Force,
        "DSPLY" => RpgOpcode.Dsply,
        "SNDMSG" => RpgOpcode.SndMsg,
        "SNDPGMMSG" => RpgOpcode.SndPgMmsg,
        "RCVMSG" => RpgOpcode.RcvMsg,
        _ => RpgOpcode.None,
    };

    private static RpgOpcode MapOpcode(string opcode) => opcode switch
    {
        "KLIST" => RpgOpcode.Klist,
        "KFLD" => RpgOpcode.Kfld,
        "ADD" => RpgOpcode.Add,
        "SUB" => RpgOpcode.Subtract,
        "MULT" => RpgOpcode.Multiply,
        "DIV" => RpgOpcode.Divide,
        "MVR" => RpgOpcode.Mvr,
        "Z-ADD" => RpgOpcode.ZAdd,
        "Z-SUB" => RpgOpcode.ZSub,
        "EVAL" => RpgOpcode.Eval,
        "MOVE" => RpgOpcode.Move,
        "MOVEL" => RpgOpcode.MoveL,
        "MOVA" => RpgOpcode.MoveA,
        "CLEAR" => RpgOpcode.Clear,
        "BITON" => RpgOpcode.BitOn,
        "BITOFF" => RpgOpcode.BitOff,
        "SETON" => RpgOpcode.SetOn,
        "SETOFF" => RpgOpcode.SetOff,
        "TEST" => RpgOpcode.Test,
        "COMP" => RpgOpcode.Comp,
        "IF" => RpgOpcode.If,
        "ELSE" => RpgOpcode.Else,
        "ENDIF" => RpgOpcode.EndIf,
        "SELECT" => RpgOpcode.Select,
        "WHEN" => RpgOpcode.When,
        "OTHER" => RpgOpcode.Other,
        "ENDSL" => RpgOpcode.EndSl,
        "DO" => RpgOpcode.Do,
        "DOU" => RpgOpcode.DoU,
        "DOW" => RpgOpcode.DoW,
        "FOR" => RpgOpcode.For,
        "ENDDO" => RpgOpcode.EndDo,
        "ITER" => RpgOpcode.Iter,
        "LEAVE" => RpgOpcode.Leave,
        "GOTO" => RpgOpcode.Goto,
        "TAG" => RpgOpcode.Tag,
        "BEGSR" => RpgOpcode.BegSr,
        "ENDSR" => RpgOpcode.EndSr,
        "EXSR" => RpgOpcode.Exsr,
        "RETRN" => RpgOpcode.Retrn,
        "RETURN" => RpgOpcode.Return,
        "OPEN" => RpgOpcode.Open,
        "CLOSE" => RpgOpcode.Close,
        "UNLOCK" => RpgOpcode.Unlock,
        "CHAIN" => RpgOpcode.Chain,
        "READ" => RpgOpcode.Read,
        "READC" => RpgOpcode.ReadC,
        "READE" => RpgOpcode.Reade,
        "READP" => RpgOpcode.ReadP,
        "READPE" => RpgOpcode.ReadPe,
        "SETLL" => RpgOpcode.SetLl,
        "SETGT" => RpgOpcode.SetGt,
        "WRITE" => RpgOpcode.Write,
        "UPDATE" => RpgOpcode.Update,
        "DELETE" => RpgOpcode.Delete,
        "FORCE" => RpgOpcode.Force,
        "EXFMT" => RpgOpcode.ExFmt,
        "DSPLY" => RpgOpcode.Dsply,
        "CALL" => RpgOpcode.Call,
        "CALLB" => RpgOpcode.CallB,
        "CALLP" => RpgOpcode.CallP,
        "PARM" => RpgOpcode.Parm,
        "PLIST" => RpgOpcode.Plist,
        "SNDMSG" => RpgOpcode.SndMsg,
        "RCVMSG" => RpgOpcode.RcvMsg,
        _ => RpgOpcode.None,
    };

    private static void ExtractSubroutines(RpgProgram program)
    {
        var main = program.MainStatements;
        List<RpgStatement>? current = null;
        RpgSubroutine? subroutine = null;
        string? procedure = null;
        var keep = new List<RpgStatement>();
        foreach (var statement in main)
        {
            switch (statement.Opcode)
            {
                case RpgOpcode.BegProc:
                    procedure = statement.Factor1?.ToUpperInvariant(); keep.Add(statement); break;
                case RpgOpcode.EndProc:
                    procedure = null; keep.Add(statement); break;
                case RpgOpcode.BegSr:
                    subroutine = new RpgSubroutine { Name = statement.Factor1?.ToUpperInvariant() ?? string.Empty, Procedure = procedure };
                    current = new List<RpgStatement>();
                    program.Subroutines.Add(subroutine);
                    break;
                case RpgOpcode.EndSr:
                    if (current is not null)
                    {
                        subroutine!.Statements.AddRange(current);
                        subroutine.Ending = statement;
                        current = null;
                    }

                    break;
                default:
                    if (current is not null)
                    {
                        current.Add(statement);
                    }
                    else
                    {
                        keep.Add(statement);
                    }

                    break;
            }
        }

        if (current is not null)
        {
            subroutine!.Statements.AddRange(current);
        }

        program.MainStatements.Clear();
        program.MainStatements.AddRange(keep);
    }

    private static void ExtractSubprocedures(RpgProgram program)
    {
        var main = program.MainStatements;
        List<RpgStatement>? current = null;
        RpgSubprocedure? procedure = null;
        var keep = new List<RpgStatement>();
        foreach (var statement in main)
        {
            switch (statement.Opcode)
            {
                case RpgOpcode.BegProc:
                    procedure = program.FindSubprocedure(statement.Factor1 ?? string.Empty)
                        ?? new RpgSubprocedure { Name = (statement.Factor1 ?? string.Empty).ToUpperInvariant() };
                    if (!program.Subprocedures.Contains(procedure))
                    {
                        program.Subprocedures.Add(procedure);
                    }

                    current = new List<RpgStatement>();
                    break;
                case RpgOpcode.EndProc:
                    if (current is not null && procedure is not null)
                    {
                        procedure.Statements.AddRange(current);
                        current = null;
                        procedure = null;
                    }

                    break;
                default:
                    if (current is not null)
                    {
                        current.Add(statement);
                    }
                    else
                    {
                        keep.Add(statement);
                    }

                    break;
            }
        }

        if (current is not null && procedure is not null)
        {
            procedure.Statements.AddRange(current);
        }

        program.MainStatements.Clear();
        program.MainStatements.AddRange(keep);
    }

    private static void ResolveBlocks(List<RpgStatement> statements)
    {
        ResolveIfBlocks(statements);
        ResolveDoBlocks(statements);
        ResolveSelectBlocks(statements);
        RpgMonitorCompiler.Bind(statements);
    }

    private static void ResolveIfBlocks(List<RpgStatement> statements)
    {
        var stack = new Stack<int>();
        var elseIndex = new Stack<int>();
        for (var index = 0; index < statements.Count; index++)
        {
            switch (statements[index].Opcode)
            {
                case RpgOpcode.If:
                    stack.Push(index);
                    elseIndex.Push(-1);
                    break;
                case RpgOpcode.Else:
                    if (stack.Count > 0)
                    {
                        elseIndex.Pop();
                        elseIndex.Push(index);
                    }

                    break;
                case RpgOpcode.EndIf:
                    if (stack.Count == 0)
                    {
                        throw new RpgCompileException(statements[index].LineNumber, "ENDIF without a matching IF.");
                    }

                    var ifIndex = stack.Pop();
                    var elseIdx = elseIndex.Pop();
                    // A false IF enters the ELSE body; executing ELSE itself skips that body.
                    statements[ifIndex].Jump = elseIdx >= 0 ? elseIdx + 1 : index;
                    statements[ifIndex].End = index;
                    if (elseIdx >= 0)
                    {
                        statements[elseIdx].Jump = index;
                    }

                    break;
            }
        }

        if (stack.Count > 0)
        {
            throw new RpgCompileException(statements[stack.Peek()].LineNumber, "IF without a matching ENDIF.");
        }
    }

    private static void ResolveDoBlocks(List<RpgStatement> statements)
    {
        var stack = new Stack<int>();
        for (var index = 0; index < statements.Count; index++)
        {
            var opcode = statements[index].Opcode;
            if (opcode is RpgOpcode.Do or RpgOpcode.DoU or RpgOpcode.DoW or RpgOpcode.For)
            {
                stack.Push(index);
            }
            else if (opcode == RpgOpcode.EndDo)
            {
                if (stack.Count == 0)
                {
                    throw new RpgCompileException(statements[index].LineNumber, "ENDDO without a matching DO/FOR.");
                }

                var start = stack.Pop();
                statements[start].End = index;
            }
        }

        if (stack.Count > 0)
        {
            throw new RpgCompileException(statements[stack.Peek()].LineNumber, "DO/FOR without a matching ENDDO.");
        }
    }

    private static void ResolveSelectBlocks(List<RpgStatement> statements)
    {
        var stack = new Stack<List<int>>();
        var openings = new Stack<int>();
        for (var index = 0; index < statements.Count; index++)
        {
            switch (statements[index].Opcode)
            {
                case RpgOpcode.Select:
                    stack.Push(new List<int>());
                    openings.Push(index);
                    break;
                case RpgOpcode.When:
                case RpgOpcode.Other:
                    if (stack.Count == 0)
                    {
                        throw new RpgCompileException(statements[index].LineNumber, "WHEN/OTHER outside of a SELECT.");
                    }

                    stack.Peek().Add(index);
                    break;
                case RpgOpcode.EndSl:
                    if (stack.Count == 0)
                    {
                        throw new RpgCompileException(statements[index].LineNumber, "ENDSL without a matching SELECT.");
                    }

                    var members = stack.Pop();
                    var end = index;
                    statements[openings.Pop()].End = end;
                    statements[end].Jump = -1;
                    for (var member = 0; member < members.Count; member++)
                    {
                        var bound = member + 1 < members.Count ? members[member + 1] : end;
                        statements[members[member]].Jump = bound;
                        statements[members[member]].End = end;
                    }

                    break;
            }
        }

        if (stack.Count > 0)
        {
            throw new RpgCompileException(statements[openings.Peek()].LineNumber, "SELECT without a matching ENDSL.");
        }
    }

    private static void AssignDsOffsets(RpgProgram program)
    {
        foreach (var ds in program.DataStructures)
        {
            if (ds.NativeStorage) continue;
            var extent = 0;
            var offset = 0;
            foreach (var element in ds.Elements)
            {
                if (!element.HasExplicitOffset)
                {
                    element.Offset = offset;
                    offset += element.Length;
                }

                extent = Math.Max(extent, element.Offset + element.Length);
            }

            ds.Length = ds.Length <= 0 ? extent : Math.Max(ds.Length, extent);
        }
    }
}
