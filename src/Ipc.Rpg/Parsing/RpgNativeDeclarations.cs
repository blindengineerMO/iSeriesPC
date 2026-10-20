using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static string ReadFreeDeclaration(List<string> lines, ref int index, int line, bool columnLimited = false)
    {
        var statement = new StringBuilder(); var quoted = false;
        while (index < lines.Count)
        {
            var part = lines[index]; lines[index] = "";
            if (columnLimited && !string.IsNullOrWhiteSpace(part))
            {
                if (part.Length < 8 || part[..5].Any(c => c != ' ' && !char.IsAsciiDigit(c)) || part[5] != ' ' || part[6] != ' ' ||
                    part.Length > 80 && part[80..].Any(c => c != ' '))
                    throw new RpgCompileException(index + 1, "Column-limited free declarations require numeric/blank sequence columns, blank columns 6-7 and code in columns 8-80; text beyond column 80 requires //.");
                part = part[7..Math.Min(part.Length, 80)];
                _ = FixedDefinitionKeywords(part, index + 1);
            }
            for (var n = 0; n < part.Length; n++)
            {
                if (statement.Length > 32768) throw new RpgCompileException(line, "Declaration exceeds 32768 characters.");
                var c = part[n];
                if (c == '\'')
                {
                    statement.Append(c);
                    if (quoted && n + 1 < part.Length && part[n + 1] == '\'') { statement.Append(part[++n]); continue; }
                    quoted = !quoted; continue;
                }
                if (!quoted && c == '/' && n + 1 < part.Length && part[n + 1] == '/') break;
                if (!quoted && c == ';')
                {
                    var tail = part[(n + 1)..].Trim();
                    if (tail.Length > 0 && !tail.StartsWith("//", StringComparison.Ordinal)) throw new RpgCompileException(line, "Use one declaration per source statement.");
                    return statement.ToString().Trim();
                }
                statement.Append(c);
            }
            if (statement.Length > 32768) throw new RpgCompileException(line, "Declaration exceeds 32768 characters.");
            statement.Append(' '); index++;
        }
        throw new RpgCompileException(line, "Unterminated free declaration.");
    }

    private static int NativeDimension(RpgProgram program, DeclarationKeyword? keyword, int line, int maximum = 32767, RpgDataStructure? current = null)
        => keyword is null ? 1 : DeclarationInteger(program, keyword.Arguments ?? "", line, 1, maximum, current);

    private static RpgDataStructure DeclareNativeStructure(List<string> lines, ref int index, RpgProgram program, CancellationToken cancellationToken, bool columnLimited = false, RpgDataStructure? container = null, int nesting = 0, string? preparedHeader = null, int? headerLine = null)
    {
        if (nesting >= 64) throw new RpgCompileException(index + 1, "Native data structure nesting exceeds 64 levels.");
        var line = headerLine ?? index + 1;
        var text = preparedHeader ?? ReadFreeDeclaration(lines, ref index, line, columnLimited);
        var inlineEnd = Regex.IsMatch(text, @"\sEND-DS\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (inlineEnd) text = text[..text.LastIndexOf("END-DS", StringComparison.OrdinalIgnoreCase)].TrimEnd();
        var header = Regex.Match(text, @"\ADCL-DS\s+([A-Za-z_][A-Za-z0-9_]*|\*N)(?:\s+(.*))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!header.Success) throw new RpgCompileException(line, "Invalid DCL-DS declaration.");
        var localName = header.Groups[1].Value.ToUpperInvariant();
        var name = localName; var anonymous = name == "*N";
        if (anonymous) name = "\u0001DS" + (program.DataStructures.Count + 1);
        if (program.IsNativeProcedure) program.Fields.RemoveAll(f => program.GlobalFields.Contains(f) && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (container is not null && (!container.Qualified || anonymous)) throw new RpgCompileException(line, "Nested data structures require a qualified parent and a name.");
        if (!anonymous && !RpgWorkstationBindings.Name(name) || container is null && (program.DataStructures.Count >= 256 || HasLocalDeclaration(program, name)))
            throw new RpgCompileException(line, "Duplicate data structure or more than 256 structures.");
        var options = DeclarationKeywords(header.Groups[2].Value, line);
        options = DataBindingKeywords(program, name, options, line, container is null);
        if (container is not null && options.Any(k => k.Name == "INZ") && program.DataDefinitions.Any(d => d.Import &&
            (container.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase) || container.Name.StartsWith(d.Name + ".", StringComparison.OrdinalIgnoreCase))))
            throw new RpgCompileException(line, "Imported nested structures cannot be initialized.");
        if (container is not null)
        {
            if (options.Any(k => k.Name is "STATIC" or "TEMPLATE" or "OCCURS" or "PSDS")) throw new RpgCompileException(line, "Nested storage belongs to the enclosing structure; STATIC, TEMPLATE, OCCURS and PSDS cannot be specified.");
            options = options.Where(k => k.Name is not ("POS" or "OVERLAY")).ToList();
            name = container.Name + "." + name;
        }
        if (options.Any(k => k.Name is not ("QUALIFIED" or "INZ" or "LEN" or "DIM" or "LIKEDS" or "LIKEREC" or "PSDS" or "OCCURS" or "TEMPLATE" or "STATIC" or "EXT" or "EXTNAME" or "PREFIX" or "NULLIND" or "BASED")) || options.GroupBy(k => k.Name).Any(g => g.Count() > 1) ||
            options.Any(k => k.Name is "QUALIFIED" or "PSDS" or "TEMPLATE" && k.Arguments is not null))
            throw new RpgCompileException(line, "Unsupported or duplicate DCL-DS keyword.");
        var basedOn = BasedDeclaration(program, name, options, line, container is null);
        var isStatic = options.Any(k => k.Name == "STATIC");
        if (isStatic && (!program.IsNativeProcedure || options.Any(k => k.Name == "STATIC" && k.Arguments is not null) || options.Any(k => k.Name == "TEMPLATE")))
            throw new RpgCompileException(line, "Data structure STATIC requires an ordinary local definition and no arguments.");
        var template = options.Any(k => k.Name == "TEMPLATE");
        if (template) ValidateTemplateCount(program, line);
        var likeDs = options.SingleOrDefault(k => k.Name == "LIKEDS");
        var likeRec = options.SingleOrDefault(k => k.Name == "LIKEREC");
        if (likeDs is not null && likeRec is not null) throw new RpgCompileException(line, "LIKEDS and LIKEREC cannot be combined.");
        var like = likeDs ?? likeRec;
        var external = options.Any(k => k.Name is "EXT" or "EXTNAME") ? ExternalStructureTemplate(program, localName, options, line) : null;
        if (external is null && options.Any(k => k.Name == "PREFIX")) throw new RpgCompileException(line, "Data structure PREFIX requires EXT or EXTNAME.");
        var init = options.SingleOrDefault(k => k.Name == "INZ");
        if (external is null && init?.Arguments is not null && (likeDs is null || !init.Arguments.Equals("*LIKEDS", StringComparison.OrdinalIgnoreCase)))
            throw new RpgCompileException(line, "DCL-DS INZ is argument-free, or INZ(*LIKEDS) with LIKEDS.");
        if (anonymous && options.Any(k => k.Name is "QUALIFIED" or "DIM" or "LIKEDS" or "LIKEREC" or "OCCURS")) throw new RpgCompileException(line, "Anonymous structures cannot be qualified, arrays, multiple-occurrence or LIKEDS definitions.");
        if (options.Any(k => k.Name == "OCCURS") && options.Any(k => k.Name == "DIM")) throw new RpgCompileException(line, "OCCURS and DIM cannot be combined.");
        var psds = options.Any(k => k.Name == "PSDS");
        if (psds && (program.IsNativeProcedure || program.DataStructures.Any(d => d.ProgramStatus && !d.IsTemplate) || options.Any(k => k.Name is "DIM" or "LIKEDS" or "LIKEREC" or "OCCURS")))
            throw new RpgCompileException(line, "Only one scalar PSDS is allowed; PSDS cannot use DIM, OCCURS or LIKEDS.");
        var ds = new RpgDataStructure { BasedOn = basedOn, Name = name, NullIndicatorName = StructureNullOption(program, options, line), StaticLocal = isStatic, IsTemplate = template, LikeType = DeclarationIdentity(program, line, localName), NativeStorage = true, Anonymous = anonymous, ProgramStatus = psds,
            LikeDataStructure = likeDs?.Arguments?.ToUpperInvariant(),
            TypeOrigin = external?.TypeOrigin,
            RecordOrigin = external?.RecordOrigin ?? (likeRec is null ? null : RecordTemplate(program, likeRec.Arguments ?? "", line).RecordOrigin),
            IsMultipleOccurrence = options.Any(k => k.Name == "OCCURS"), Occurrences = NativeDimension(program, options.SingleOrDefault(k => k.Name == "OCCURS"), line),
            DeclarationLine = line, ExplicitLength = options.Any(k => k.Name == "LEN"),
            Qualified = container is not null || like is not null || options.Any(k => k.Name == "QUALIFIED"), InitializeSubfields = init is not null,
            IsArray = options.Any(k => k.Name == "DIM"), Dimension = NativeDimension(program, options.SingleOrDefault(k => k.Name == "DIM"), line) };
        if (ds.IsArray && !ds.Qualified) throw new RpgCompileException(line, "A data structure array requires QUALIFIED.");
        if (like is not null)
        {
            var parent = likeRec is null ? NativeStructureTemplate(program, like.Arguments ?? "", init?.Arguments is not null, line) : RecordTemplate(program, likeRec.Arguments ?? "", line);
            if (parent is not { NativeStorage: true, Anonymous: false } || options.Any(k => k.Name == "LEN")) throw new RpgCompileException(line, "LIKEDS requires a named native structure and cannot specify LEN.");
            ds.Length = parent.Length;
            ds.TypeOrigin = parent.TypeOrigin ?? parent.LikeType;
            if (likeDs is not null && parent.RecordOrigin is not null) ds.RecordOrigin = parent.RecordOrigin;
            foreach (var e in parent.Elements)
                ds.Elements.Add(new() { Name = ds.Name + "." + e.LocalName, LocalName = e.LocalName, Kind = e.Kind, Length = e.Length,
                    StorageLength = e.StorageLength, Stride = e.Stride, Decimals = e.Decimals, Dimension = e.Dimension, IsArray = e.IsArray, OpaqueBytes = e.OpaqueBytes,
                    NullCapable = e.NullCapable, NullIndicatorName = e.NullIndicatorName, ExternalName = e.ExternalName, ExternalDefaultInitialization = init?.Arguments is not null && e.ExternalDefaultInitialization, NestedStructure = e.NestedStructure is null ? null : CopyNestedLayout(e.NestedStructure, ds.Name + "." + e.LocalName, init?.Arguments is not null),
                    ArrayDescending = e.ArrayDescending, OverlayTarget = e.OverlayTarget, LikeType = e.LikeType, DeclaredLike = e.DeclaredLike,
                    Offset = e.Offset, HasExplicitOffset = true, InitialValue = init?.Arguments is not null ? e.InitialValue : null,
                    HasInitialValue = init?.Arguments is not null && e.HasInitialValue, Anonymous = e.Anonymous });
            // INZ(*LIKEDS) inherits whether uninitialized parent subfields remain blanks.
            if (init?.Arguments is not null && !parent.InitializeSubfields)
                ds = CloneWithInitialization(ds, false);
        }
        else
        {
            if (external is not null)
                foreach (var e in external.Elements)
                    ds.Elements.Add(new() { Name = ds.Qualified ? name + "." + e.LocalName : e.LocalName!, LocalName = e.LocalName, NullCapable = e.NullCapable, NullIndicatorName = e.NullIndicatorName, ExternalName = e.ExternalName, ExternalDefaultInitialization = e.ExternalDefaultInitialization,
                        Kind = e.Kind, Length = e.Length, Decimals = e.Decimals, StorageLength = e.StorageLength, Stride = e.Stride,
                        Offset = e.Offset, HasExplicitOffset = true, LikeType = e.LikeType, InitialValue = e.InitialValue, HasInitialValue = e.HasInitialValue });
            var overrides = new HashSet<string>(StringComparer.Ordinal);
            var extent = external?.Length ?? 0; var next = extent; var overlayEnds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ended = inlineEnd;
            if (!inlineEnd) for (index++; index < lines.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = lines[index];
                var raw = (columnLimited && record.Length >= 7 && record[..5].All(c => c == ' ' || char.IsAsciiDigit(c)) && record[5] == ' ' && record[6] == ' ' ? record[7..] : record).Trim();
                if (raw.Length == 0 || raw.StartsWith("//", StringComparison.Ordinal)) { lines[index] = ""; continue; }
                var subline = index + 1; var declaration = ReadFreeDeclaration(lines, ref index, subline, columnLimited);
                if (Regex.IsMatch(declaration, @"\AEND-DS(?:\s+" + Regex.Escape(header.Groups[1].Value) + @")?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) { ended = true; break; }
                var inline = declaration.StartsWith("DCL-DS ", StringComparison.OrdinalIgnoreCase) || declaration.StartsWith("DCL-DS\t", StringComparison.OrdinalIgnoreCase);
                var match = Regex.Match(inline ? declaration[6..].TrimStart() : declaration, @"\A(?:DCL-SUBF\s+)?([A-Za-z_][A-Za-z0-9_]*|\*N)(?:\s+(.+))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success || !inline && match.Groups[2].Value.Length == 0) throw new RpgCompileException(subline, "Expected a typed subfield, nested DCL-DS or END-DS.");
                var local = match.Groups[1].Value.ToUpperInvariant(); var filler = local == "*N";
                if (!inline && !filler && MapFreeOpcode(local) != RpgOpcode.None && !declaration.StartsWith("DCL-SUBF", StringComparison.OrdinalIgnoreCase))
                    throw new RpgCompileException(subline, "An operation-code subfield name requires DCL-SUBF.");
                if (local == "*N") local = "__FILLER_" + (ds.Elements.Count + 1);
                var exported = ds.Qualified ? name + "." + local : local;
                if (program.IsNativeProcedure) program.Fields.RemoveAll(f => program.GlobalFields.Contains(f) && f.Name.Equals(exported, StringComparison.OrdinalIgnoreCase));
                if (external is not null && DeclarationKeywords(match.Groups[2].Value, subline).FirstOrDefault()?.Name == "EXTFLD")
                { OverrideExternalSubfield(program, ds, local, DeclarationKeywords(match.Groups[2].Value, subline), overrides, subline); continue; }
                if (ds.Elements.Count >= 1024 || !filler && (ds.Find(local) is not null || HasLocalDeclaration(program, exported) || exported == name))
                    throw new RpgCompileException(subline, "Duplicate subfield name or more than 1024 subfields.");
                if (match.Groups[2].Value.StartsWith('*'))
                {
                    var feedback = FeedbackSubfield(match.Groups[2].Value, psds, subline);
                    ds.Elements.Add(new() { Name = exported, LocalName = local, Kind = feedback.Kind, Length = feedback.Width,
                        StorageLength = feedback.Width, Stride = feedback.Width, Offset = feedback.Offset, HasExplicitOffset = true,
                        Anonymous = filler, LikeType = DeclarationIdentity(program, subline, local), FeedbackKeyword = match.Groups[2].Value.ToUpperInvariant(), DeclarationLine = subline });
                    extent = Math.Max(extent, feedback.Offset + feedback.Width); next = Math.Max(next, extent);
                    continue;
                }
                var keywords = DeclarationKeywords(match.Groups[2].Value, subline);
                if (keywords.Any(k => k.Name is "CTDATA" or "PERRCD" or "EXTFMT" or "ALT" || k.Name == "DIM" && k.Arguments?.Trim().Equals("*CTDATA", StringComparison.OrdinalIgnoreCase) == true))
                    throw new RpgCompileException(subline, "Compile-time DS subfield arrays require the remaining native overlay/initialization contract.");
                if (keywords.GroupBy(k => k.Name).Any(g => g.Count() > 1)) throw new RpgCompileException(subline, "Duplicate subfield keyword.");
                var scalarKeywords = keywords.Where(k => k.Name is not ("DIM" or "POS" or "OVERLAY" or "ASCEND" or "DESCEND")).ToArray();
                RpgDataStructure? nested = null;
                RpgField field;
                if (inline)
                {
                    nested = DeclareNativeStructure(lines, ref index, program, cancellationToken, columnLimited, ds, nesting + 1, declaration, subline);
                    field = StructureType(nested);
                }
                else if (keywords.FirstOrDefault()?.Name is "LIKEDS" or "LIKEREC")
                {
                    nested = NestedLayout(program, ds, exported, keywords, subline);
                    field = StructureType(nested);
                }
                else
                {
                    var scratch = new RpgProgram { Library = program.Library, Name = program.Name };
                    DeclareFreeScalar(scratch, local, string.Join(' ', scalarKeywords.Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")"))), subline, program, ds, standaloneStorage: false);
                    field = scratch.Fields.Single();
                }
                if (field.Kind == RpgFieldKind.Integer && field.InitialValue is { } integer && decimal.TryParse(integer, NumberStyles.Number, CultureInfo.InvariantCulture, out var initialInteger))
                {
                    var bytes = field.Length switch { 3 => 1, 5 => 2, 10 => 4, _ => 8 };
                    var min = bytes == 8 ? long.MinValue : -(1L << (bytes * 8 - 1));
                    var max = bytes == 8 ? long.MaxValue : (1L << (bytes * 8 - 1)) - 1;
                    if (initialInteger < min || initialInteger > max) throw new RpgCompileException(subline, "Integer INZ exceeds its native byte width.");
                }
                var width = nested?.Length ?? (field.Kind switch { RpgFieldKind.Packed => (field.Length + 2) / 2,
                    RpgFieldKind.Integer => field.Length switch { 3 => 1, 5 => 2, 10 => 4, _ => 8 }, _ => field.Length });
                var dimension = NativeDimension(program, keywords.SingleOrDefault(k => k.Name == "DIM"), subline, current: ds);
                var size = checked(width * dimension);
                var stride = width; var isArray = keywords.Any(k => k.Name == "DIM");
                if (size > 1048576) throw new RpgCompileException(subline, "A native subfield array exceeds 1 MiB.");
                var pos = keywords.SingleOrDefault(k => k.Name == "POS"); var overlay = keywords.SingleOrDefault(k => k.Name == "OVERLAY");
                if (pos is not null && overlay is not null) throw new RpgCompileException(subline, "POS and OVERLAY cannot be combined.");
                var offset = pos is null ? next : NativeDimension(program, pos, subline, 1048576, ds) - 1;
                var pointerAligned = field.Kind is RpgFieldKind.ProcedurePointer or RpgFieldKind.DataPointer || nested is not null && ContainsPointers(nested);
                if (pointerAligned && pos is null && overlay is null) offset = checked((offset + 15) / 16 * 16);
                if (overlay is not null)
                {
                    var parts = DeclarationArguments(overlay.Arguments ?? "", subline);
                    var target = ds.Find(parts[0]);
                    if (parts.Length is < 1 or > 2 || target is null) throw new RpgCompileException(subline, "OVERLAY requires a previously declared subfield.");
                    if (target.IsArray)
                    {
                        if (isArray) throw new RpgCompileException(subline, "An overlay of an array inherits DIM; it cannot specify its own DIM.");
                        dimension = target.Dimension; stride = target.Stride; isArray = true;
                        size = checked((dimension - 1) * stride + width);
                    }
                    var relative = parts.Length == 1 ? 0 : parts[1].Equals("*NEXT", StringComparison.OrdinalIgnoreCase) ? overlayEnds.GetValueOrDefault(target.Name) :
                        NativeDimension(program, new("OVERLAY", parts[1]), subline, 1048576, ds) - 1;
                    var covered = target.IsArray ? width : size;
                    if ((long)relative + covered > target.StorageLength) throw new RpgCompileException(subline, "OVERLAY exceeds its target subfield.");
                    offset = target.Offset + relative; overlayEnds[target.Name] = Math.Max(overlayEnds.GetValueOrDefault(target.Name), relative + covered);
                }
                else next = Math.Max(next, checked(offset + size));
                if (pointerAligned && offset % 16 != 0) throw new RpgCompileException(subline, "Pointers require a sixteen-byte aligned position.");
                if ((long)offset + size > 1048576) throw new RpgCompileException(subline, "Native subfield position exceeds 1 MiB.");
                extent = Math.Max(extent, checked(offset + size));
                var initial = keywords.SingleOrDefault(k => k.Name == "INZ");
                ds.Elements.Add(new() { Name = exported, LocalName = local, Kind = field.Kind, Length = field.Length, Decimals = field.Decimals,
                    NestedStructure = nested, ArrayDescending = ArraySequence(keywords, isArray, subline), LikeType = field.LikeType, DeclaredLike = field.DeclaredLike,
                    NullCapable = field.NullCapable, NullIndicatorName = nested?.NullIndicatorName ?? field.NullIndicatorName, OverlayTarget = overlay is null ? null : DeclarationArguments(overlay.Arguments!, subline)[0].ToUpperInvariant(),
                    StorageLength = width, Stride = stride, Dimension = dimension, IsArray = isArray, OpaqueBytes = field.OpaqueBytes,
                    Offset = offset, HasExplicitOffset = true, InitialValue = field.InitializationExpression, HasInitialValue = initial is not null, Anonymous = filler, DeclarationLine = subline });
            }
            if (!ended) throw new RpgCompileException(line, "DCL-DS without END-DS.");
            var length = options.SingleOrDefault(k => k.Name == "LEN");
            ds.Length = length is null ? extent : NativeDimension(program, length, line, 1048576, ds);
            if (ContainsPointers(ds))
            {
                if (length is not null && ds.Length % 16 != 0) throw new RpgCompileException(line, "Structures containing pointers require a sixteen-byte aligned length.");
                ds.Length = checked((ds.Length + 15) / 16 * 16);
            }
            if (ds.Length < extent || ds.Length < 1) throw new RpgCompileException(line, "Data structure LEN is smaller than its subfields or no storage was declared.");
        }
        if (NestedDepth(ds) > 64 || NestedFieldCount(ds) > 1024) throw new RpgCompileException(line, "Native structures support at most 64 nesting levels and 1024 recursive subfields.");
        if (container is null && !template && ((long)ds.Length * ds.StorageCount > 1048576 || program.DataStructures.Where(d => d.NativeStorage && !d.IsTemplate).Sum(d => (long)d.Length * d.StorageCount) + (long)ds.Length * ds.StorageCount > 16777216))
            throw new RpgCompileException(line, "Native data structure storage exceeds 1 MiB per declaration or 16 MiB per program.");
        if (!ds.Qualified && ds.Elements.Any(e => program.FindDs(e.Name) is not null)) throw new RpgCompileException(line, "Data structure and subfield names collide.");
        if (container is null) program.DataStructures.Add(ds);
        return ds;
    }

    private static RpgDataStructure CloneWithInitialization(RpgDataStructure source, bool initialized)
    {
        var ds = new RpgDataStructure { Name = source.Name, BasedOn = source.BasedOn, NullIndicatorName = source.NullIndicatorName, StaticLocal = source.StaticLocal, IsTemplate = source.IsTemplate, RecordOrigin = source.RecordOrigin, NativeStorage = true, Qualified = source.Qualified, Anonymous = source.Anonymous,
            LikeDataStructure = source.LikeDataStructure, TypeOrigin = source.TypeOrigin, LikeType = source.LikeType, DeclarationLine = source.DeclarationLine,
            ProgramStatus = source.ProgramStatus, ExplicitLength = source.ExplicitLength, IndicatorDataStructure = source.IndicatorDataStructure,
            ParameterName = source.ParameterName, ReadOnlyParameter = source.ReadOnlyParameter,
            IsMultipleOccurrence = source.IsMultipleOccurrence, Occurrences = source.Occurrences,
            InitializeSubfields = initialized, IsArray = source.IsArray, Dimension = source.Dimension, Length = source.Length };
        ds.Elements.AddRange(source.Elements); return ds;
    }
}
