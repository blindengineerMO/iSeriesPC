using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private sealed record WorkstationDeclaration(int Line, string Name, bool Input, bool Output, string Keywords);
    private sealed record DeclarationKeyword(string Name, string? Arguments);

    private static List<WorkstationDeclaration> ParseWorkstationDeclarations(List<string> lines, RpgProgram program, bool free,
        CancellationToken cancellationToken)
    {
        var declarations = new List<WorkstationDeclaration>(); var calculations = false;
        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = lines[index]; var text = raw.Trim(); var line = index + 1;
            var scalar = text.StartsWith("DCL-S ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("DCL-S\t", StringComparison.OrdinalIgnoreCase);
            var file = text.StartsWith("DCL-F ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("DCL-F\t", StringComparison.OrdinalIgnoreCase);
            if (scalar || file)
            {
                if (!free) throw new RpgCompileException(line, "Free declarations require **FREE.");
                if (calculations) throw new RpgCompileException(line, "Free declarations must precede calculations in the supported global declaration subset.");
                var statement = new StringBuilder(); var quoted = false; var ended = false;
                do
                {
                    var part = lines[index]; lines[index] = "";
                    for (var n = 0; n < part.Length; n++)
                    {
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
                            ended = true; break;
                        }
                        statement.Append(c);
                    }
                    if (statement.Length > 32768) throw new RpgCompileException(line, "Declaration exceeds 32768 characters.");
                    if (ended) break;
                    statement.Append(' '); index++;
                } while (index < lines.Count);
                if (!ended || quoted) throw new RpgCompileException(line, "Unterminated free declaration.");
                text = statement.ToString().Trim();
                var match = Regex.Match(text, @"\ADCL-[FS]\s+([A-Za-z_][A-Za-z0-9_]*)\s+(.*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) throw new RpgCompileException(line, "Invalid free declaration.");
                var name = match.Groups[1].Value.ToUpperInvariant(); var keywords = match.Groups[2].Value;
                if (scalar) DeclareFreeScalar(program, name, keywords, line);
                else declarations.Add(new(line, name, true, true, keywords));
                continue;
            }
            var shift = raw.Length > 6 && raw[..6].All(c => c == ' ' || char.IsAsciiDigit(c)) && char.ToUpperInvariant(raw[6]) == 'F' ? 1 :
                raw.Length > 5 && raw[..5].All(c => c == ' ' || char.IsAsciiDigit(c)) && char.ToUpperInvariant(raw[5]) == 'F' ? 0 : -1;
            if (shift < 0)
            {
                if (text.Length > 0 && text is not ("**free" or "**FREE") && !text.StartsWith('/') && !text.StartsWith('*') &&
                    (free || Specification(raw) == 'C')) calculations = true;
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
            if (!Ipc.Core.Objects.ObjectName.IsValid(fileName) || type is not ('C' or 'O') || type == 'C' && designation != 'F' || type == 'O' && designation is not (' ' or 'F') ||
                char.ToUpperInvariant(padded[21 + shift]) != 'E' || padded.Substring(35 + shift, 7).Trim().ToUpperInvariant() != "WORKSTN" ||
                padded.Substring(18 + shift, 3).Any(c => c != ' ') || padded.Substring(22 + shift, 13).Any(c => c != ' ') || padded[42 + shift] != ' ')
                throw new RpgCompileException(line, "Supported F-specs require an externally described full-procedural C/O WORKSTN file.");
            declarations.Add(new(line, fileName, type == 'C', true, "WORKSTN " + padded[(43 + shift)..]));
        }
        if (declarations.Count > 16 || declarations.Select(d => d.Name).Distinct().Count() != declarations.Count)
            throw new RpgCompileException(0, "At most 16 distinct workstation files are supported.");
        return declarations;
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
        var result = new List<string>(); var start = 0; var quoted = false;
        for (var n = 0; n < text.Length; n++)
        {
            if (text[n] == '\'') { if (quoted && n + 1 < text.Length && text[n + 1] == '\'') { n++; continue; } quoted = !quoted; }
            else if (!quoted && text[n] == ':') { result.Add(text[start..n].Trim()); start = n + 1; }
        }
        if (quoted) throw new RpgCompileException(line, "Unterminated declaration literal.");
        result.Add(text[start..].Trim()); return result.ToArray();
    }

    private static void DeclareFreeScalar(RpgProgram program, string name, string text, int line)
    {
        var keywords = DeclarationKeywords(text, line);
        if (!RpgWorkstationBindings.Name(name) || program.FindField(name) is not null || keywords.Count == 0) throw new RpgCompileException(line, "Invalid or duplicate scalar declaration.");
        var type = keywords[0]; var dimensions = type.Arguments is null ? Array.Empty<string>() : DeclarationArguments(type.Arguments, line);
        int Dimension(int index, int fallback) => dimensions.Length <= index ? fallback : int.TryParse(dimensions[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
        var kind = type.Name switch { "CHAR" => RpgFieldKind.Character, "PACKED" => RpgFieldKind.Packed, "ZONED" => RpgFieldKind.Zoned,
            "INT" => RpgFieldKind.Integer, "IND" => RpgFieldKind.Indicator, "DATE" => RpgFieldKind.Date,
            "TIME" => RpgFieldKind.Time, "TIMESTAMP" => RpgFieldKind.Timestamp, _ => throw new RpgCompileException(line, "Unsupported free scalar type " + type.Name + ".") };
        var length = Dimension(0, kind is RpgFieldKind.Character or RpgFieldKind.Packed or RpgFieldKind.Zoned ? -1 : kind == RpgFieldKind.Indicator ? 1 : kind == RpgFieldKind.Timestamp ? 26 : kind == RpgFieldKind.Time ? 8 : 10);
        var decimals = Dimension(1, 0);
        if (length is < 1 or > 32767 || decimals < 0 || decimals > 28 || kind is RpgFieldKind.Packed or RpgFieldKind.Zoned && (length > 29 || decimals > length) ||
            dimensions.Length > (kind is RpgFieldKind.Packed or RpgFieldKind.Zoned ? 2 : 1) || kind == RpgFieldKind.Integer && length is not (3 or 5 or 10 or 20) ||
            kind == RpgFieldKind.Indicator && type.Arguments is not null || kind is RpgFieldKind.Date or RpgFieldKind.Time or RpgFieldKind.Timestamp && type.Arguments is not null)
            throw new RpgCompileException(line, "Unsupported scalar dimensions or date/time options.");
        string? initial = null;
        if (keywords.Skip(1).Any(k => k.Name is not ("INZ" or "CCSID")) || keywords.Skip(1).GroupBy(k => k.Name).Any(g => g.Count() != 1))
            throw new RpgCompileException(line, "Supported scalar options are INZ and CHAR CCSID(*HEX).");
        var codePage = keywords.SingleOrDefault(k => k.Name == "CCSID");
        var opaque = codePage is not null;
        if (opaque && (kind != RpgFieldKind.Character || codePage!.Arguments?.ToUpperInvariant() is not ("*HEX" or "65535")))
            throw new RpgCompileException(line, "Only CHAR CCSID(*HEX) or CCSID(65535) is supported.");
        if (keywords.SingleOrDefault(k => k.Name == "INZ")?.Arguments is { } init)
        {
            var quoted = Regex.IsMatch(init, @"\A'(?:[^']|'')*'\z", RegexOptions.CultureInvariant);
            var numeric = Regex.IsMatch(init, @"\A[+-]?[0-9]+(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant);
            var special = init.ToUpperInvariant();
            var rawHex = opaque && Regex.IsMatch(init, @"\A[Xx]'(?:[0-9A-Fa-f]{2})+'\z", RegexOptions.CultureInvariant);
            var valid = kind switch {
                RpgFieldKind.Character => (opaque ? rawHex : quoted) || special is "*BLANK" or "*BLANKS",
                RpgFieldKind.Indicator => special is "*ON" or "*OFF" or "'0'" or "'1'",
                RpgFieldKind.Date or RpgFieldKind.Time or RpgFieldKind.Timestamp => quoted,
                _ => numeric || special is "*ZERO" or "*ZEROS" };
            if (!valid) throw new RpgCompileException(line, "INZ requires a supported constant literal for the scalar type.");
            initial = quoted ? Unquote(init).Replace("''", "'", StringComparison.Ordinal) : special;
            if (rawHex && (init.Length - 3) / 2 != length) throw new RpgCompileException(line, "Hexadecimal INZ must match the raw character field width.");
            if (numeric && (!decimal.TryParse(init, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
                decimal.Round(value, decimals) != value || decimal.Abs(value).ToString("F" + decimals, CultureInfo.InvariantCulture).Replace(".", "").TrimStart('0').Length > length ||
                kind == RpgFieldKind.Integer && (value < long.MinValue || value > long.MaxValue)))
                throw new RpgCompileException(line, "INZ exceeds the declared numeric precision.");
            if (kind == RpgFieldKind.Character && quoted && initial.Length > length)
                throw new RpgCompileException(line, "INZ exceeds the declared character length.");
            if (kind is RpgFieldKind.Date or RpgFieldKind.Timestamp && !DateTimeOffset.TryParse(initial, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _) ||
                kind == RpgFieldKind.Time && (!TimeSpan.TryParse(initial, CultureInfo.InvariantCulture, out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)))
                throw new RpgCompileException(line, "Unsupported date/time INZ literal.");
        }
        program.Fields.Add(new() { Name = name, Kind = kind, Length = length, Decimals = decimals, InitialValue = initial, OpaqueBytes = opaque });
    }

    private static void BindWorkstations(RpgProgram program, IReadOnlyList<WorkstationDeclaration> declarations,
        Func<RpgWorkstationRequest, RpgWorkstationLayout>? resolver)
    {
        var externalFields = new Dictionary<string, RpgDisplayField>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
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
                if (parts.Length != 2) throw new RpgCompileException(declaration.Line, "SFILE requires a format and RRN field.");
                return new RpgSubfileBinding(parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant());
            }).ToArray();
            var renames = keywords.Where(k => k.Name == "RENAME").Select(k => {
                var parts = DeclarationArguments(k.Arguments ?? "", declaration.Line);
                if (parts.Length != 2) throw new RpgCompileException(declaration.Line, "RENAME requires external and RPG format names.");
                return new RpgRecordRename(parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant());
            }).ToArray();
            var request = new RpgWorkstationRequest(declaration.Name, Literal("EXTDESC", declaration.Name), Literal("EXTFILE", declaration.Name), input, output,
                keywords.Any(k => k.Name == "USROPN"), subfiles, renames, (Single("PREFIX") ?? "").ToUpperInvariant());
            try
            {
                var layout = resolver?.Invoke(request) ?? throw new RpgCompileException(declaration.Line, "WORKSTN requires a compile-time display resolver.");
                var binding = new RpgWorkstationBinding(request, layout); RpgWorkstationBindings.Validate(binding);
                if (program.Workstations.SelectMany(f => f.Layout.Records).Any(r => layout.Records.Any(n => n.Name == r.Name)))
                    throw new RpgCompileException(declaration.Line, "Display record names must be distinct; use RENAME.");
                foreach (var field in layout.Records.SelectMany(r => r.Fields))
                {
                    if (externalFields.TryGetValue(field.Name, out var previous) && (previous.Kind != field.Kind || previous.Length != field.Length || previous.Decimals != field.Decimals || previous.Opaque != field.Opaque))
                        throw new RpgCompileException(declaration.Line, "Incompatible shared external field: " + field.Name);
                    externalFields[field.Name] = field;
                    var existing = program.FindField(field.Name);
                    if (program.FindDs(field.Name) is not null || program.FindDsElement(field.Name) is not null)
                        throw new RpgCompileException(declaration.Line, "External field conflicts with a data structure: " + field.Name);
                    if (existing is not null)
                    {
                        if (existing.IsArray || program.FindDsElement(field.Name) is not null || program.FindDs(field.Name) is not null || existing.Source == RpgFieldSource.Constant ||
                            existing.Kind != field.Kind || existing.Length != field.Length || existing.Decimals != field.Decimals)
                            throw new RpgCompileException(declaration.Line, "Incompatible external field declaration: " + field.Name);
                        existing.OpaqueBytes |= field.Opaque;
                    }
                    else program.Fields.Add(new() { Name = field.Name, Kind = field.Kind, Length = field.Length, Decimals = field.Decimals, Source = RpgFieldSource.ExternalFile, OpaqueBytes = field.Opaque });
                }
                foreach (var subfile in subfiles)
                {
                    var rrn = program.FindField(subfile.RelativeRecord);
                    var size = layout.Records.Single(r => r.Name == subfile.Record).Capacity;
                    if (rrn is null || rrn.IsArray || rrn.Decimals != 0 || rrn.Kind is not (RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Decimal or RpgFieldKind.Integer or RpgFieldKind.Binary) ||
                        rrn.Length < size.ToString(CultureInfo.InvariantCulture).Length)
                        throw new RpgCompileException(declaration.Line, "SFILE RRN requires a sufficiently large numeric scalar with zero decimals.");
                }
                program.Workstations.Add(binding); program.FileSpecs.Add(declaration.Name);
            }
            catch (Exception error) when (error is InvalidDataException or Ipc.Core.Messages.CpfException)
            { throw new RpgCompileException(declaration.Line, error.Message); }
        }
    }

    private static void ValidateWorkstationStatements(RpgProgram program)
    {
        foreach (var statement in program.MainStatements)
        {
            if (statement.Opcode is not (RpgOpcode.ExFmt or RpgOpcode.ReadC or RpgOpcode.Read or RpgOpcode.Write or RpgOpcode.Update or RpgOpcode.Chain or RpgOpcode.Open or RpgOpcode.Close or
                RpgOpcode.ReadP or RpgOpcode.Reade or RpgOpcode.ReadPe or RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Delete)) continue;
            var operands = statement.Value ?? string.Join(" ", new[] { statement.Factor1, statement.Factor2, statement.Result }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var tokens = Regex.Matches(operands.ToUpperInvariant(), @"[A-Z_][A-Z0-9_]*").Select(m => m.Value).ToArray();
            var binding = program.Workstations.FirstOrDefault(w => tokens.Contains(w.Request.Name) || w.Layout.Records.Any(r => tokens.Contains(r.Name)));
            if (binding is null)
            {
                if (statement.Opcode is RpgOpcode.ExFmt or RpgOpcode.ReadC)
                    throw new RpgCompileException(statement.LineNumber, "EXFMT/READC requires a declared WORKSTN record format.");
                continue;
            }
            if (statement.Opcode is not (RpgOpcode.ExFmt or RpgOpcode.ReadC or RpgOpcode.Read or RpgOpcode.Write or RpgOpcode.Update or RpgOpcode.Chain or RpgOpcode.Open or RpgOpcode.Close or
                RpgOpcode.ReadP or RpgOpcode.Reade or RpgOpcode.ReadPe or RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Delete)) continue;
            if (statement.Opcode is RpgOpcode.ReadP or RpgOpcode.Reade or RpgOpcode.ReadPe or RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Delete)
                throw new RpgCompileException(statement.LineNumber, "Unsupported WORKSTN operation " + statement.Opcode + ".");
            var name = (statement.Factor2 ?? statement.Factor1 ?? "").Trim().ToUpperInvariant();
            if (name.Length == 0) name = (statement.Factor1 ?? "").Trim().ToUpperInvariant();
            var format = binding.Layout.Records.SingleOrDefault(r => r.Name == name);
            var expected = statement.Opcode == RpgOpcode.Chain ? @"\A(?:\([^)]*\)|\S+)\s+" + Regex.Escape(name) + @"\z" : @"\A" + Regex.Escape(name) + @"\z";
            if (!Regex.IsMatch(operands.ToUpperInvariant(), expected) || statement.Opcode is RpgOpcode.Open or RpgOpcode.Close && name != binding.Request.Name ||
                statement.Opcode is not (RpgOpcode.Open or RpgOpcode.Close or RpgOpcode.Read) && format is null ||
                statement.Opcode == RpgOpcode.ExFmt && (format?.Subfile != false || !binding.Request.Input || !binding.Request.Output) ||
                statement.Opcode is RpgOpcode.ReadC or RpgOpcode.Chain or RpgOpcode.Update && format?.Subfile != true ||
                statement.Opcode == RpgOpcode.Read && format?.Subfile == true)
                throw new RpgCompileException(statement.LineNumber, "Unsupported WORKSTN operands; use a file/record name without a result data structure.");
        }
    }
}
