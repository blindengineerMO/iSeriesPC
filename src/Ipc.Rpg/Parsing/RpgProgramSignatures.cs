using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private sealed record ProgramSignature(bool Entry, int Line, RpgPrototype? Prototype, List<string> Parameters)
    {
        internal bool NoPassSeen { get; set; }
    }

    private static RpgField? ReturnLayout(RpgProgram program, string text, int line)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var scratch = new RpgProgram { Name = program.Name, Library = program.Library };
        var keywords = DeclarationKeywords(text, line);
        if (keywords.Skip(1).Any(k => k.Name is not ("CCSID" or "DIM" or "RTNPARM")) ||
            keywords.Count(k => k.Name == "RTNPARM") > 1 || keywords.Any(k => k.Name == "RTNPARM" && k.Arguments is not null))
            throw new RpgCompileException(line, "A procedure return requires a supported type or LIKEDS, optional fixed DIM, CCSID and RTNPARM.");
        text = string.Join(' ', keywords.Where(k => k.Name != "RTNPARM").Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")")));
        DeclareNativeParameter(scratch, program, "__RETURN", text, line, allocate: false);
        var field = scratch.Fields.Single(); field.ReturnByParameter = keywords.Any(k => k.Name == "RTNPARM");
        return field;
    }

    private static ProgramSignature BeginProgramSignature(RpgProgram program, bool entry, string name, string text, int line, bool bind)
    {
        var options = DeclarationKeywords(text, line);
        if (entry)
        {
            if ((!program.IsNativeProcedure && options.Count != 0) || name != "*N" && !RpgWorkstationBindings.Name(name) ||
                program.IsNativeProcedure && name != "*N" && !name.Equals(program.NativeProcedureName, StringComparison.OrdinalIgnoreCase))
                throw new RpgCompileException(line, "A main PI requires *N or its prototype name without a return; a local PI requires *N or its procedure name.");
            var parameters = new List<string>();
            if (bind)
            {
                if (program.HasNativeEntryInterface) throw new RpgCompileException(line, "Only one procedure interface is allowed.");
                program.HasNativeEntryInterface = true; program.NativeEntryLine = line;
                program.NativeEntryPrototype = name == "*N" ? null : name;
                if (program.IsNativeProcedure) program.NativeReturnField = ReturnLayout(program, text, line);
                program.NativeParameterLists.Add("/*ENTRY", parameters.AsReadOnly());
            }
            return new(true, line, null, parameters);
        }
        if (!RpgWorkstationBindings.Name(name)) throw new RpgCompileException(line, "Invalid native prototype name.");
        var programCall = options.Any(k => k.Name == "EXTPGM");
        string external; RpgField? returns = null;
        if (programCall)
        {
            if (options.Count != 1) throw new RpgCompileException(line, "EXTPGM prototypes cannot return values or specify procedure options.");
            external = options[0].Arguments ?? name;
            if (options[0].Arguments is not null)
            {
                if (!Regex.IsMatch(external, @"\A'(?:[^']|'')+'\z", RegexOptions.CultureInvariant)) throw new RpgCompileException(line, "EXTPGM requires a constant quoted name.");
                external = Unquote(external).ToUpperInvariant();
            }
            var parts = external.Split('/');
            if (parts.Length is < 1 or > 2 || parts.Any(p => !Ipc.Core.Objects.ObjectName.IsValid(p))) throw new RpgCompileException(line, "EXTPGM requires a program name or LIB/PROGRAM, each at most ten characters.");
        }
        else
        {
            if (options.Count(k => k.Name == "EXTPROC") > 1) throw new RpgCompileException(line, "Duplicate EXTPROC.");
            var target = options.SingleOrDefault(k => k.Name == "EXTPROC");
            external = target?.Arguments is { } actual ? Regex.IsMatch(actual, @"\A'[A-Za-z_][A-Za-z0-9_]*'\z", RegexOptions.CultureInvariant)
                ? Unquote(actual) : throw new RpgCompileException(line, "Local EXTPROC requires a quoted procedure name; foreign conventions remain unsupported.") : name;
            returns = bind ? ReturnLayout(program, string.Join(' ', options.Where(k => k.Name != "EXTPROC").Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")"))), line) : null;
        }
        var prototype = new RpgPrototype { Name = name, ExternalName = external, NativeSignature = true, NativeProcedure = !programCall, NativeReturnField = returns, DeclarationLine = line };
        if (bind)
        {
            if (program.Prototypes.Count >= 256 || program.FindPrototype(name) is not null || HasLocalDeclaration(program, name)) throw new RpgCompileException(line, "Duplicate native prototype name or more than 256 prototypes.");
            program.Prototypes.Add(prototype);
        }
        return new(false, line, prototype, prototype.Parameters);
    }

    private static void AddProgramParameter(RpgProgram program, ProgramSignature signature, string name, string text, int line, bool bind)
    {
        var hidden = (signature.Entry ? program.NativeReturnField : signature.Prototype?.NativeReturnField)?.ReturnByParameter == true ? 1 : 0;
        if (signature.Parameters.Count + hidden >= 255) throw new RpgCompileException(line, "Native signatures are bounded to 255 parameters, including an RTNPARM return slot.");
        var options = DeclarationKeywords(text, line);
        if (options.Count == 0 || options.Skip(1).Any(k => k.Name is not ("CCSID" or "CONST" or "OPTIONS" or "VALUE" or "DIM" or "ASCEND" or "DESCEND")) ||
            options.Count(k => k.Name == "CONST") > 1 || options.Any(k => k.Name == "CONST" && k.Arguments is not null) ||
            options.Count(k => k.Name == "OPTIONS") > 1 || options.Any(k => k.Name == "OPTIONS" && string.IsNullOrWhiteSpace(k.Arguments)))
            throw new RpgCompileException(line, "Program signatures require a supported type and parameter options.");
        var optionKeyword = options.SingleOrDefault(k => k.Name == "OPTIONS");
        var passing = optionKeyword is null ? [] : DeclarationArguments(optionKeyword.Arguments!, line).Select(p => p.ToUpperInvariant()).ToArray();
        if (passing.Any(p => p is not ("*NOPASS" or "*OMIT" or "*VARSIZE" or "*EXACT" or "*TRIM" or "*RIGHTADJ")) || passing.Distinct().Count() != passing.Length)
            throw new RpgCompileException(line, "Unsupported or duplicate parameter OPTIONS value.");
        var readOnly = options.Any(k => k.Name == "CONST");
        var byValue = options.Any(k => k.Name == "VALUE");
        var trim = passing.Contains("*TRIM"); var right = passing.Contains("*RIGHTADJ"); var exact = passing.Contains("*EXACT");
        if ((trim || right) && ((!readOnly && !byValue) || options[0].Name is not ("CHAR" or "LIKE")))
            throw new RpgCompileException(line, "*TRIM and *RIGHTADJ require character CONST or VALUE parameters.");
        if (byValue && (readOnly || passing.Contains("*OMIT") || options.Count(k => k.Name == "VALUE") != 1 || options.Any(k => k.Name == "VALUE" && k.Arguments is not null) ||
            !(signature.Entry ? program.IsNativeProcedure : signature.Prototype!.NativeProcedure)))
            throw new RpgCompileException(line, "VALUE requires a local procedure parameter without CONST or *OMIT.");
        var noPass = passing.Contains("*NOPASS"); var omit = passing.Contains("*OMIT");
        if (signature.NoPassSeen && !noPass) throw new RpgCompileException(line, "Every parameter following OPTIONS(*NOPASS) must also specify OPTIONS(*NOPASS).");
        signature.NoPassSeen |= noPass;
        text = string.Join(' ', options.Where(k => k.Name is not ("CONST" or "OPTIONS" or "VALUE")).Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")")));
        var variable = passing.Contains("*VARSIZE");
        if (variable && (byValue || options[0].Name is not ("CHAR" or "LIKEDS" or "LIKEREC" or "LIKE") && !options.Any(k => k.Name == "DIM")))
            throw new RpgCompileException(line, "*VARSIZE requires reference character/structure storage or an array; VALUE is incompatible.");
        if (signature.Entry && !RpgWorkstationBindings.Name(name)) throw new RpgCompileException(line, "A PI parameter requires a variable name.");
        if (!bind) { signature.Parameters.Add(name); return; }
        if (signature.Entry)
        {
            DeclareNativeParameter(program, program, name, text, line, true, readOnly, noPass, omit, byValue);
            ValidateResolvedParameter(program.FindField(name)!, trim, right, variable, line);
            program.FindField(name)!.VariableSizeParameter = variable;
            program.FindField(name)!.ExactParameter = exact; program.FindField(name)!.TrimParameter = trim; program.FindField(name)!.RightAdjustParameter = right;
            signature.Parameters.Add(name);
        }
        else
        {
            // Prototype parameter names are documentation, not runtime variables.
            // Keep their types outside Program.Fields and native storage allocations.
            var scratch = new RpgProgram { Name = program.Name, Library = program.Library };
            var privateName = "__SIG_" + (signature.Parameters.Count + 1);
            DeclareNativeParameter(scratch, program, privateName, text, line, false, readOnly, noPass, omit, byValue);
            ValidateResolvedParameter(scratch.Fields.Single(), trim, right, variable, line);
            scratch.Fields.Single().VariableSizeParameter = variable;
            scratch.Fields.Single().ExactParameter = exact; scratch.Fields.Single().TrimParameter = trim; scratch.Fields.Single().RightAdjustParameter = right;
            signature.Prototype!.NativeParameters.Add(scratch.Fields.Single());
            signature.Parameters.Add(privateName);
        }
    }

    private static void ValidateResolvedParameter(RpgField field, bool trim, bool right, bool variable, int line)
    {
        if ((trim || right) && field.Kind != RpgFieldKind.Character || variable && field.Kind != RpgFieldKind.Character && !field.IsArray)
            throw new RpgCompileException(line, "Resolved parameter type is incompatible with the requested character or variable-size options.");
    }

    private static void ReadNativeFixedSignature(List<string> lines, ref int index, RpgProgram program, int shift, bool bind, CancellationToken cancellationToken, ForwardDeclarationTypes? collector = null)
    {
        var line = index + 1; var header = lines[index].PadRight(80 + shift);
        string Column(int start, int end) => Col(header, start + shift, end + shift);
        if (Column(22, 23).Any(c => c != ' ') || Column(26, 32).Any(c => c != ' ') || Column(43, 43) != " ")
            throw new RpgCompileException(line, "Native PR/PI headers require blank external, DS, From and reserved columns.");
        var entry = Column(24, 25).ToUpperInvariant() == "PI";
        var name = Column(7, 21).Trim().ToUpperInvariant(); if (entry && name.Length == 0) name = "*N";
        var cursor = index;
        var keywords = ReadTraditionalKeywords(lines, ref cursor, shift, cancellationToken);
        var headerOptions = DeclarationKeywords(keywords, line);
        var likeReturn = headerOptions.Any(k => k.Name == "LIKE");
        if (likeReturn) keywords = FixedLikeDeclaration(keywords, Column(33, 39), Column(40, 40), Column(41, 42), line);
        if (headerOptions.Any(k => k.Name is "LIKEDS" or "LIKEREC"))
        {
            if (Column(33, 42).Any(c => c != ' ')) throw new RpgCompileException(line, "LIKEDS return type columns must be blank.");
            keywords = string.Join(' ', headerOptions.OrderBy(k => k.Name is "LIKEDS" or "LIKEREC" ? 0 : 1).Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")")));
        }
        if (!likeReturn && Column(33, 42).Any(c => c != ' '))
            keywords = TraditionalScalarType(TraditionalPosition(Column(33, 39), line), TraditionalPosition(Column(41, 42), line), char.ToUpperInvariant(Column(40, 40)[0]), line) + " " + keywords;
        collector?.Signature(entry, name, keywords, line);
        var signature = BeginProgramSignature(program, entry, name, keywords, line, bind);
        while (cursor < lines.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[cursor])) { cursor++; continue; }
            if (!TraditionalDefinitionAt(lines[cursor], shift) || Col(lines[cursor], 24 + shift, 25 + shift).Any(c => c != ' ')) break;
            var subline = cursor + 1; var parameter = lines[cursor].PadRight(80 + shift);
            string Part(int start, int end) => Col(parameter, start + shift, end + shift);
            if (Part(22, 23).Any(c => c != ' ') || Part(26, 32).Any(c => c != ' ') || Part(43, 43) != " ")
                throw new RpgCompileException(subline, "Native signature parameters require blank external, DS, From and reserved columns.");
            var parameterName = Part(7, 21).Trim().ToUpperInvariant();
            var parameterKeywords = ReadTraditionalKeywords(lines, ref cursor, shift, cancellationToken);
            var parameterOptions = DeclarationKeywords(parameterKeywords, subline);
            var likeDs = parameterOptions.Any(k => k.Name is "LIKEDS" or "LIKEREC");
            var likeScalar = parameterOptions.Any(k => k.Name == "LIKE");
            if (likeScalar) parameterKeywords = FixedLikeDeclaration(parameterKeywords, Part(33, 39), Part(40, 40), Part(41, 42), subline);
            if (likeDs)
            {
                if (Part(33, 42).Any(c => c != ' ')) throw new RpgCompileException(subline, "LIKEDS parameter type columns must be blank.");
                parameterKeywords = string.Join(' ', parameterOptions.OrderBy(k => k.Name is "LIKEDS" or "LIKEREC" ? 0 : 1).Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")")));
            }
            var scalar = likeDs || likeScalar ? "" : TraditionalScalarType(TraditionalPosition(Part(33, 39), subline), TraditionalPosition(Part(41, 42), subline), char.ToUpperInvariant(Part(40, 40)[0]), subline);
            collector?.Parameter(entry, parameterName, scalar + " " + parameterKeywords, subline);
            AddProgramParameter(program, signature, parameterName, scalar + " " + parameterKeywords, subline, bind);
        }
        for (var n = index; n < cursor; n++) lines[n] = "";
        index = cursor - 1;
    }

    private static void ReadFreeProgramSignature(List<string> lines, ref int index, RpgProgram program, bool bind, bool columnLimited, CancellationToken cancellationToken, ForwardDeclarationTypes? collector = null)
    {
        var line = index + 1; var text = ReadFreeDeclaration(lines, ref index, line, columnLimited);
        var header = Regex.Match(text, @"\ADCL-(PR|PI)\s+([A-Za-z_][A-Za-z0-9_]*|\*N)(?:\s+(.*))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!header.Success) throw new RpgCompileException(line, "Invalid program PR/PI declaration.");
        var kind = header.Groups[1].Value.ToUpperInvariant(); var name = header.Groups[2].Value.ToUpperInvariant();
        var options = header.Groups[3].Value;
        var inline = Regex.IsMatch(options, @"(?:\A|\s)END-" + kind + @"\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (inline) options = Regex.Replace(options, @"(?:\A|\s)END-" + kind + @"\z", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).TrimEnd();
        collector?.Signature(kind == "PI", name, options, line);
        var signature = BeginProgramSignature(program, kind == "PI", name, options, line, bind);
        if (inline) return;
        for (index++; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[index]) || lines[index].TrimStart().StartsWith("//", StringComparison.Ordinal)) { lines[index] = ""; continue; }
            var subline = index + 1; var declaration = ReadFreeDeclaration(lines, ref index, subline, columnLimited);
            if (Regex.IsMatch(declaration, @"\AEND-" + kind + @"(?:\s+" + Regex.Escape(name) + @")?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return;
            var parameter = Regex.Match(declaration, @"\A(DCL-PARM\s+)?([A-Za-z_][A-Za-z0-9_]*|\*N)\s+(.+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!parameter.Success || parameter.Groups[1].Length == 0 && MapFreeOpcode(parameter.Groups[2].Value.ToUpperInvariant()) != RpgOpcode.None)
                throw new RpgCompileException(subline, "Expected a typed parameter or END-" + kind + "; operation-code names require DCL-PARM.");
            collector?.Parameter(kind == "PI", parameter.Groups[2].Value.ToUpperInvariant(), parameter.Groups[3].Value, subline);
            AddProgramParameter(program, signature, parameter.Groups[2].Value.ToUpperInvariant(), parameter.Groups[3].Value, subline, bind);
        }
        throw new RpgCompileException(line, "DCL-" + kind + " without END-" + kind + ".");
    }

    private static void ValidateNativeProgramSignatures(RpgProgram program)
    {
        foreach (var prototype in program.Prototypes.Where(p => p.NativeSignature))
            if (program.Prototypes.Count(p => p.Name.Equals(prototype.Name, StringComparison.OrdinalIgnoreCase)) != 1 || program.FindField(prototype.Name) is not null || program.FindDs(prototype.Name) is not null || program.FindDsElement(prototype.Name) is not null || !prototype.NativeProcedure && program.FindSubprocedure(prototype.Name) is not null)
                throw new RpgCompileException(prototype.DeclarationLine, "Native prototype name collides with another declaration.");
        if (program.IsNativeProcedure || program.NativeEntryPrototype is not { } name) return;
        var entryPrototype = program.FindPrototype(name);
        var entry = program.NativeParameterLists["/*ENTRY"];
        if (entryPrototype is not { NativeSignature: true, NativeProcedure: false } || entry.Count != entryPrototype.NativeParameters.Count || entry.Select(program.FindField).Zip(entryPrototype.NativeParameters).Any(pair =>
            pair.First is null || !SameProcedureType(pair.First, pair.Second)))
            throw new RpgCompileException(program.NativeEntryLine, "The main PI must match the named native program prototype's parameter layouts.");
    }

    private static void ValidateNativeProgramCalls(RpgProgram program, IEnumerable<RpgStatement>? statements = null)
    {
        foreach (var statement in (statements ?? program.MainStatements).Where(s => s.Opcode == RpgOpcode.CallP))
        {
            var text = statement.Factor1 ?? statement.Value ?? "";
            var target = Regex.Match(text, @"\A([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);
            if (!target.Success || program.FindPrototype(target.Groups[1].Value) is not { NativeSignature: true } prototype) continue;
            var call = Regex.Match(text, @"\A([A-Za-z_][A-Za-z0-9_]*)\s*(?:\((.*)\))?\z", RegexOptions.CultureInvariant);
            if (!call.Success) throw new RpgCompileException(statement.LineNumber, "Malformed native prototyped call argument list.");
            var arguments = call.Groups[2].Value.Trim().Length == 0 ? [] : DeclarationArguments(call.Groups[2].Value, statement.LineNumber);
            if (!prototype.AcceptsArgumentCount(arguments.Length))
                throw new RpgCompileException(statement.LineNumber, "Native prototyped call parameter count does not match its declaration.");
            for (var index = 0; index < arguments.Length; index++)
            {
                RpgExpr expression;
                try { expression = RpgExpressionParser.Parse(arguments[index]); }
                catch (RpgCompileException error) when (error.LineNumber == 0 && error.Location is null)
                { throw new RpgCompileException(statement.LineNumber, error.Message); }
                var actual = NativeCallLayout(program, expression);
                if (actual is null && expression is RpgFieldRef root && program.FindDs(root.Name) is { NativeStorage: true, IsArray: false } ds)
                    actual = new(RpgFieldKind.Character, ds.Length * ds.StorageCount, 0, false, true, false, null, Opaque: true);
                var formal = prototype.NativeParameters[index];
                if (expression is RpgOmittedArgument)
                {
                    if (!formal.OmitParameter) throw new RpgCompileException(statement.LineNumber, "*OMIT requires OPTIONS(*OMIT) on its prototype parameter.");
                    continue;
                }
                if (formal.ExactParameter && ExactActualType(program, expression) is { } actualType && ExactTypeError(actualType, formal) is { } exactError)
                    throw new RpgCompileException(statement.LineNumber, exactError);
                if (formal.VariableSizeParameter)
                {
                    if (formal.ReadOnlyParameter)
                    {
                        if (actual is { } valueLayout && valueLayout.IsArray != formal.IsArray) throw new RpgCompileException(statement.LineNumber, "*VARSIZE does not change the scalar or array parameter rank.");
                        continue;
                    }
                    if (actual is not { Constant: false, ReadOnly: false } variable || variable.IsArray != formal.IsArray || formal.IsArray && !variable.Native ||
                        (formal.Kind == RpgFieldKind.Character ? variable.Kind != RpgFieldKind.Character || !formal.IsDataStructure && variable.Opaque != formal.OpaqueBytes :
                        variable.Kind != formal.Kind || variable.Length != formal.Length || variable.Decimals != formal.Decimals))
                        throw new RpgCompileException(statement.LineNumber, "*VARSIZE requires compatible reference data; only character widths and array dimensions may differ.");
                    continue;
                }
                if (RpgRuntimeContext.AggregateParameter(formal))
                {
                    if (formal.ReadOnlyParameter || formal.ValueParameter)
                    {
                        if (formal.IsArray && actual is { IsArray: false }) throw new RpgCompileException(statement.LineNumber, "An array parameter requires an array actual.");
                        continue;
                    }
                    if (actual is not { Native: true, Constant: false, ReadOnly: false } aggregate || aggregate.IsArray != formal.IsArray ||
                        (formal.IsDataStructure ? aggregate.Kind != RpgFieldKind.Character || (formal.IsArray ? aggregate.Length != formal.Length : aggregate.Length < formal.Length) :
                        aggregate.Kind != formal.Kind || aggregate.Length != formal.Length || aggregate.Decimals != formal.Decimals || aggregate.Opaque != formal.OpaqueBytes))
                        throw new RpgCompileException(statement.LineNumber, "Aggregate references require compatible native array or structure storage.");
                    continue;
                }
                if (formal.ReadOnlyParameter || formal.ValueParameter)
                {
                    if (actual?.IsArray == true || expression is RpgAllElementsRef || expression is RpgBuiltin { Name: "SUBARR" })
                        throw new RpgCompileException(statement.LineNumber, "A scalar CONST formal cannot receive an array or keyed selection.");
                    continue;
                }
                if (actual is not { IsArray: false, Constant: false, ReadOnly: false } layout || layout.Kind != formal.Kind ||
                    (formal.Kind == RpgFieldKind.Character && layout.Native ? layout.Length < formal.Length : layout.Length != formal.Length) || layout.Decimals != formal.Decimals || layout.Opaque != formal.OpaqueBytes)
                    throw new RpgCompileException(statement.LineNumber, "Native writable references require compatible scalar variables; expressions require CONST or local VALUE.");
            }
        }
    }

    private static void ValidateReadOnlyParameters(RpgProgram program)
    {
        var names = program.Fields.Where(f => f.ReadOnlyParameter).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return;
        foreach (var statement in program.MainStatements)
        {
            var targets = new List<string>();
            if (statement.Result is { Length: > 0 } result) targets.Add(result);
            if (statement.Opcode == RpgOpcode.Eval && statement.Result is null && statement.Value is { } assignment)
            {
                var quoted = false;
                for (var index = 0; index < assignment.Length; index++)
                {
                    if (assignment[index] == '\'') { if (quoted && index + 1 < assignment.Length && assignment[index + 1] == '\'') index++; else quoted = !quoted; }
                    if (!quoted && assignment[index] == '=') { targets.Add(assignment[..index].Trim()); break; }
                }
            }
            if (statement.Opcode is RpgOpcode.SetOn or RpgOpcode.SetOff && statement.Factor1 is { } flags)
                targets.AddRange(flags.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            foreach (var target in targets)
            {
                RpgExpr expression;
                try { expression = RpgExpressionParser.Parse(target); }
                catch (RpgCompileException) { continue; } // Other operand validators own unsupported target syntax.
                if (Root(expression) is { } root && names.Contains(root))
                    throw new RpgCompileException(statement.LineNumber, "A CONST procedure-interface parameter cannot be assigned, cleared or used as a writable result.");
            }
        }
        static string? Root(RpgExpr expression) => expression switch
        {
            RpgFieldRef field => field.Name, RpgDynamicIndexRef field => field.Name, RpgIndexedRef field => field.Name,
            RpgElementRef field => Root(field.Target), RpgMemberRef field => Root(field.Target),
            RpgBuiltin { Arguments.Count: > 0 } builtin => Root(builtin.Arguments[0]), _ => null
        };
    }
}
