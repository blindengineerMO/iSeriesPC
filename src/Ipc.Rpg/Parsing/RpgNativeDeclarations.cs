using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static string ReadFreeDeclaration(List<string> lines, ref int index, int line)
    {
        var statement = new StringBuilder(); var quoted = false;
        while (index < lines.Count)
        {
            var part = lines[index]; lines[index] = "";
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

    private static int NativeDimension(DeclarationKeyword? keyword, int line, int maximum = 32767)
        => keyword is null ? 1 : int.TryParse(keyword.Arguments, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 1 && value <= maximum
            ? value : throw new RpgCompileException(line, "Invalid " + keyword.Name + " dimension; use a positive integer within the supported storage bound.");

    private static void DeclareNativeStructure(List<string> lines, ref int index, RpgProgram program, CancellationToken cancellationToken)
    {
        var line = index + 1;
        var text = ReadFreeDeclaration(lines, ref index, line);
        var header = Regex.Match(text, @"\ADCL-DS\s+([A-Za-z_][A-Za-z0-9_]*|\*N)(?:\s+(.*))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!header.Success) throw new RpgCompileException(line, "Invalid DCL-DS declaration.");
        var name = header.Groups[1].Value.ToUpperInvariant(); var anonymous = name == "*N";
        if (anonymous) name = "\u0001DS" + (program.DataStructures.Count + 1);
        if (!anonymous && !RpgWorkstationBindings.Name(name) || program.DataStructures.Count >= 256 || program.FindDs(name) is not null || program.FindField(name) is not null || program.FindDsElement(name) is not null)
            throw new RpgCompileException(line, "Duplicate data structure or more than 256 structures.");
        var options = DeclarationKeywords(header.Groups[2].Value, line);
        if (options.Any(k => k.Name is not ("QUALIFIED" or "INZ" or "LEN" or "DIM" or "LIKEDS" or "PSDS")) || options.GroupBy(k => k.Name).Any(g => g.Count() > 1) ||
            options.Any(k => k.Name is "QUALIFIED" or "PSDS" && k.Arguments is not null))
            throw new RpgCompileException(line, "Unsupported or duplicate DCL-DS keyword.");
        var like = options.SingleOrDefault(k => k.Name == "LIKEDS");
        var init = options.SingleOrDefault(k => k.Name == "INZ");
        if (init?.Arguments is not null && (like is null || !init.Arguments.Equals("*LIKEDS", StringComparison.OrdinalIgnoreCase)))
            throw new RpgCompileException(line, "DCL-DS INZ is argument-free, or INZ(*LIKEDS) with LIKEDS.");
        if (anonymous && options.Any(k => k.Name is "QUALIFIED" or "DIM" or "LIKEDS")) throw new RpgCompileException(line, "Anonymous structures cannot be qualified, arrays or LIKEDS definitions.");
        var psds = options.Any(k => k.Name == "PSDS");
        if (psds && (program.DataStructures.Any(d => d.ProgramStatus) || options.Any(k => k.Name is "DIM" or "LIKEDS")))
            throw new RpgCompileException(line, "Only one scalar PSDS is allowed; PSDS cannot use DIM or LIKEDS.");
        var ds = new RpgDataStructure { Name = name, NativeStorage = true, Anonymous = anonymous, ProgramStatus = psds,
            LikeDataStructure = like?.Arguments?.ToUpperInvariant(),
            DeclarationLine = line, ExplicitLength = options.Any(k => k.Name == "LEN"),
            Qualified = like is not null || options.Any(k => k.Name == "QUALIFIED"), InitializeSubfields = init is not null,
            IsArray = options.Any(k => k.Name == "DIM"), Dimension = NativeDimension(options.SingleOrDefault(k => k.Name == "DIM"), line) };
        if (ds.IsArray && !ds.Qualified) throw new RpgCompileException(line, "A data structure array requires QUALIFIED.");
        if (like is not null)
        {
            var parent = program.FindDs(like.Arguments ?? "");
            if (parent is not { NativeStorage: true, Anonymous: false } || options.Any(k => k.Name == "LEN")) throw new RpgCompileException(line, "LIKEDS requires a previously declared named native structure and cannot specify LEN.");
            ds.Length = parent.Length;
            foreach (var e in parent.Elements)
                ds.Elements.Add(new() { Name = ds.Name + "." + e.LocalName, LocalName = e.LocalName, Kind = e.Kind, Length = e.Length,
                    StorageLength = e.StorageLength, Stride = e.Stride, Decimals = e.Decimals, Dimension = e.Dimension, IsArray = e.IsArray, OpaqueBytes = e.OpaqueBytes,
                    Offset = e.Offset, HasExplicitOffset = true, InitialValue = init?.Arguments is not null ? e.InitialValue : null,
                    HasInitialValue = init?.Arguments is not null && e.HasInitialValue, Anonymous = e.Anonymous });
            // INZ(*LIKEDS) inherits whether uninitialized parent subfields remain blanks.
            if (init?.Arguments is not null && !parent.InitializeSubfields)
                ds = CloneWithInitialization(ds, false);
        }
        else
        {
            var extent = 0; var next = 0; var overlayEnds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ended = false;
            for (index++; index < lines.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var raw = lines[index].Trim();
                if (raw.Length == 0 || raw.StartsWith("//", StringComparison.Ordinal)) { lines[index] = ""; continue; }
                var subline = index + 1; var declaration = ReadFreeDeclaration(lines, ref index, subline);
                if (Regex.IsMatch(declaration, @"\AEND-DS(?:\s+" + Regex.Escape(header.Groups[1].Value) + @")?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) { ended = true; break; }
                var match = Regex.Match(declaration, @"\A(?:DCL-SUBF\s+)?([A-Za-z_][A-Za-z0-9_]*|\*N)\s+(.+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success || declaration.StartsWith("DCL-DS", StringComparison.OrdinalIgnoreCase)) throw new RpgCompileException(subline, "Expected a typed subfield or END-DS; nested declarations are not yet supported.");
                var local = match.Groups[1].Value.ToUpperInvariant(); var filler = local == "*N";
                if (!filler && MapFreeOpcode(local) != RpgOpcode.None && !declaration.StartsWith("DCL-SUBF", StringComparison.OrdinalIgnoreCase))
                    throw new RpgCompileException(subline, "An operation-code subfield name requires DCL-SUBF.");
                if (local == "*N") local = "__FILLER_" + (ds.Elements.Count + 1);
                var exported = ds.Qualified ? name + "." + local : local;
                if (ds.Elements.Count >= 1024 || !filler && (ds.Find(local) is not null || program.FindField(exported) is not null || program.FindDsElement(exported) is not null || program.FindDs(exported) is not null || exported == name))
                    throw new RpgCompileException(subline, "Duplicate subfield name or more than 1024 subfields.");
                if (match.Groups[2].Value.StartsWith('*'))
                {
                    var feedback = FeedbackSubfield(match.Groups[2].Value, psds, subline);
                    ds.Elements.Add(new() { Name = exported, LocalName = local, Kind = feedback.Kind, Length = feedback.Width,
                        StorageLength = feedback.Width, Stride = feedback.Width, Offset = feedback.Offset, HasExplicitOffset = true,
                        Anonymous = filler, FeedbackKeyword = match.Groups[2].Value.ToUpperInvariant(), DeclarationLine = subline });
                    extent = Math.Max(extent, feedback.Offset + feedback.Width); next = Math.Max(next, extent);
                    continue;
                }
                var keywords = DeclarationKeywords(match.Groups[2].Value, subline);
                if (keywords.GroupBy(k => k.Name).Any(g => g.Count() > 1)) throw new RpgCompileException(subline, "Duplicate subfield keyword.");
                var scalarKeywords = keywords.Where(k => k.Name is not ("DIM" or "POS" or "OVERLAY")).ToArray();
                var scratch = new RpgProgram { Library = program.Library, Name = program.Name };
                DeclareFreeScalar(scratch, local, string.Join(' ', scalarKeywords.Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")"))), subline);
                var field = scratch.Fields.Single();
                if (field.Kind == RpgFieldKind.Integer && field.InitialValue is { } integer && decimal.TryParse(integer, NumberStyles.Number, CultureInfo.InvariantCulture, out var initialInteger))
                {
                    var bytes = field.Length switch { 3 => 1, 5 => 2, 10 => 4, _ => 8 };
                    var min = bytes == 8 ? long.MinValue : -(1L << (bytes * 8 - 1));
                    var max = bytes == 8 ? long.MaxValue : (1L << (bytes * 8 - 1)) - 1;
                    if (initialInteger < min || initialInteger > max) throw new RpgCompileException(subline, "Integer INZ exceeds its native byte width.");
                }
                var width = field.Kind switch { RpgFieldKind.Packed => (field.Length + 2) / 2,
                    RpgFieldKind.Integer => field.Length switch { 3 => 1, 5 => 2, 10 => 4, _ => 8 }, _ => field.Length };
                var dimension = NativeDimension(keywords.SingleOrDefault(k => k.Name == "DIM"), subline);
                var size = checked(width * dimension);
                var stride = width; var isArray = keywords.Any(k => k.Name == "DIM");
                if (size > 1048576) throw new RpgCompileException(subline, "A native subfield array exceeds 1 MiB.");
                var pos = keywords.SingleOrDefault(k => k.Name == "POS"); var overlay = keywords.SingleOrDefault(k => k.Name == "OVERLAY");
                if (pos is not null && overlay is not null) throw new RpgCompileException(subline, "POS and OVERLAY cannot be combined.");
                var offset = pos is null ? next : NativeDimension(pos, subline, 1048576) - 1;
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
                        NativeDimension(new("OVERLAY", parts[1]), subline, 1048576) - 1;
                    var covered = target.IsArray ? width : size;
                    if ((long)relative + covered > target.StorageLength) throw new RpgCompileException(subline, "OVERLAY exceeds its target subfield.");
                    offset = target.Offset + relative; overlayEnds[target.Name] = Math.Max(overlayEnds.GetValueOrDefault(target.Name), relative + covered);
                }
                else next = Math.Max(next, checked(offset + size));
                if ((long)offset + size > 1048576) throw new RpgCompileException(subline, "Native subfield position exceeds 1 MiB.");
                extent = Math.Max(extent, checked(offset + size));
                var initial = keywords.SingleOrDefault(k => k.Name == "INZ");
                ds.Elements.Add(new() { Name = exported, LocalName = local, Kind = field.Kind, Length = field.Length, Decimals = field.Decimals,
                    StorageLength = width, Stride = stride, Dimension = dimension, IsArray = isArray, OpaqueBytes = field.OpaqueBytes,
                    Offset = offset, HasExplicitOffset = true, InitialValue = initial?.Arguments, HasInitialValue = initial is not null, Anonymous = filler });
            }
            if (!ended) throw new RpgCompileException(line, "DCL-DS without END-DS.");
            var length = options.SingleOrDefault(k => k.Name == "LEN");
            ds.Length = length is null ? extent : NativeDimension(length, line, 1048576);
            if (ds.Length < extent || ds.Length < 1) throw new RpgCompileException(line, "Data structure LEN is smaller than its subfields or no storage was declared.");
        }
        if ((long)ds.Length * ds.Dimension > 1048576 || program.DataStructures.Where(d => d.NativeStorage).Sum(d => (long)d.Length * d.Dimension) + (long)ds.Length * ds.Dimension > 16777216)
            throw new RpgCompileException(line, "Native data structure storage exceeds 1 MiB per declaration or 16 MiB per program.");
        if (!ds.Qualified && ds.Elements.Any(e => program.FindDs(e.Name) is not null)) throw new RpgCompileException(line, "Data structure and subfield names collide.");
        program.DataStructures.Add(ds);
    }

    private static RpgDataStructure CloneWithInitialization(RpgDataStructure source, bool initialized)
    {
        var ds = new RpgDataStructure { Name = source.Name, NativeStorage = true, Qualified = source.Qualified, Anonymous = source.Anonymous,
            LikeDataStructure = source.LikeDataStructure, DeclarationLine = source.DeclarationLine,
            InitializeSubfields = initialized, IsArray = source.IsArray, Dimension = source.Dimension, Length = source.Length };
        ds.Elements.AddRange(source.Elements); return ds;
    }
}
