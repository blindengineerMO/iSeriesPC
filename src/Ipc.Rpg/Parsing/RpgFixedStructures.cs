using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void ReadNativeFixedStructure(List<string> lines, ref int index, RpgProgram program, int shift, bool bind, CancellationToken cancellationToken, ForwardDeclarationTypes? collector = null)
    {
        // A separate declaration image keeps physical line numbers intact, including
        // expanded includes. END-DS is implicit in native source, so only this image
        // receives the synthetic terminator; the following real statement stays put.
        var first = index; var converted = new List<string>(lines);
        var cursor = index; var header = lines[cursor].PadRight(80 + shift);
        string Header(int from, int to) => Col(header, from + shift, to + shift);
        var name = Header(7, 21).Trim().ToUpperInvariant();
        if (name.Length == 0) name = "*N";
        var status = Header(23, 23).ToUpperInvariant();
        if (Header(22, 22) != " " || status is not (" " or "S") || Header(26, 32).Any(c => c != ' ') || Header(40, 43).Any(c => c != ' '))
            throw new RpgCompileException(first + 1, "Native DS requires program-described storage, blank From/type/decimal/reserved columns and optional S program status.");
        var length = TraditionalPosition(Header(33, 39), first + 1);
        var keywords = ReadTraditionalKeywords(lines, ref cursor, shift, cancellationToken);
        converted[first] = "DCL-DS " + name + " " + (status == "S" ? "PSDS " : "") +
            (length is null ? "" : "LEN(" + length.Value.ToString(CultureInfo.InvariantCulture) + ") ") + keywords + ";";
        for (var n = first + 1; n < cursor; n++) converted[n] = "";
        var like = DeclarationKeywords(keywords, first + 1).Any(k => k.Name is "LIKEDS" or "LIKEREC");
        while (cursor < lines.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[cursor])) { converted[cursor++] = ""; continue; }
            if (!TraditionalDefinitionAt(lines[cursor], shift) || Col(lines[cursor], 24 + shift, 25 + shift).Any(c => c != ' ')) break;
            var subline = cursor; var record = lines[cursor].PadRight(80 + shift);
            string Column(int from, int to) => Col(record, from + shift, to + shift);
            if (like) throw new RpgCompileException(subline + 1, "A LIKEDS structure cannot declare additional native subfields.");
            if (Column(22, 23).Any(c => c != ' ') || Column(43, 43) != " ")
                throw new RpgCompileException(subline + 1, "Native subfields require blank external, DS-type and reserved columns.");
            var local = Column(7, 21).Trim().ToUpperInvariant();
            if (local.Length == 0) local = "*N";
            var optionsText = ReadTraditionalKeywords(lines, ref cursor, shift, cancellationToken);
            var fromText = Column(26, 32); var toText = Column(33, 39);
            var special = Column(26, 39).TrimEnd().ToUpperInvariant();
            string declaration;
            if (special.StartsWith('*'))
            {
                if (Column(40, 42).Any(c => c != ' ') || optionsText.Length != 0)
                    throw new RpgCompileException(subline + 1, "Reserved feedback entries define their own type and length and cannot specify additional keywords.");
                declaration = special == "*PARM" ? "*PARMS" : special;
            }
            else
            {
                var options = DeclarationKeywords(optionsText, subline + 1);
                var likeScalar = options.Any(k => k.Name == "LIKE");
                if (likeScalar && fromText.Any(c => c != ' '))
                    throw new RpgCompileException(subline + 1, "LIKE subfields require blank From columns; use OVERLAY for a shared position.");
                var from = TraditionalPosition(fromText, subline + 1);
                var size = likeScalar ? null : TraditionalPosition(toText, subline + 1);
                var decimals = TraditionalPosition(Column(41, 42), subline + 1);
                var type = char.ToUpperInvariant(Column(40, 40)[0]);
                var even = options.Where(k => k.Name == "PACKEVEN").ToArray();
                if (options.Any(k => k.Name == "POS") || even.Length > 1 || even.Any(k => k.Arguments is not null) ||
                    even.Length != 0 && (from is null || type != 'P'))
                    throw new RpgCompileException(subline + 1, "Native subfields use From/To or OVERLAY; PACKEVEN requires a packed From/To field.");
                var overlay = options.Where(k => k.Name == "OVERLAY").ToArray();
                if (from is not null)
                {
                    if (from < 1 || size is null || size < from || size > 1048576 || overlay.Length != 0)
                        throw new RpgCompileException(subline + 1, "Invalid native From/To range or combination with OVERLAY.");
                    var bytes = size.Value - from.Value + 1;
                    size = type switch
                    {
                        'P' => checked(2 * bytes - (even.Length == 0 ? 1 : 2)),
                        'I' => bytes switch { 1 => 3, 2 => 5, 4 => 10, 8 => 20,
                            _ => throw new RpgCompileException(subline + 1, "A native integer From/To range must occupy 1, 2, 4 or 8 bytes.") },
                        _ => bytes
                    };
                }
                var positioned = from is null ? "" : " POS(" + from.Value.ToString(CultureInfo.InvariantCulture) + ")";
                var retained = new List<DeclarationKeyword>();
                foreach (var option in options.Where(k => k.Name != "PACKEVEN"))
                {
                    // Unlike free form, native OVERLAY can address the structure.
                    if (option.Name == "OVERLAY" && name != "*N")
                    {
                        var arguments = DeclarationArguments(option.Arguments ?? "", subline + 1);
                        if (arguments.Length is >= 1 and <= 2 && arguments[0].Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            if (overlay.Length != 1) throw new RpgCompileException(subline + 1, "Duplicate native OVERLAY keyword.");
                            if (arguments.Length != 2 || !arguments[1].Equals("*NEXT", StringComparison.OrdinalIgnoreCase))
                                positioned = " POS(" + (arguments.Length == 1 ? "1" : arguments[1]) + ")";
                            continue;
                        }
                    }
                    retained.Add(option);
                }
                var retainedText = string.Join(' ', retained.Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")")));
                declaration = likeScalar ? FixedLikeDeclaration(retainedText, toText, Column(40, 40), Column(41, 42), subline + 1) + positioned :
                    TraditionalScalarType(size, decimals, type, subline + 1, subfield: true) + positioned + " " + retainedText;
            }
            converted[subline] = "DCL-SUBF " + local + " " + declaration + ";";
            for (var n = subline + 1; n < cursor; n++) converted[n] = "";
        }
        if (cursor == converted.Count) converted.Add("END-DS;"); else converted[cursor] = "END-DS;";
        collector?.StructureImage(converted, first, cursor);
        if (bind)
        {
            var declarationIndex = first;
            DeclareNativeStructure(converted, ref declarationIndex, program, cancellationToken);
        }
        for (var n = first; n < cursor; n++) lines[n] = "";
        index = cursor - 1;
    }

    private static bool TraditionalDefinitionAt(string line, int shift) => line.Length > 5 + shift &&
        line[..(5 + shift)].All(c => c == ' ' || char.IsAsciiDigit(c)) && char.ToUpperInvariant(line[5 + shift]) == 'D';

    private static int? TraditionalPosition(string text, int line)
    {
        if (text.All(c => c == ' ')) return null;
        if (!Regex.IsMatch(text, @"\A *[0-9]+\z", RegexOptions.CultureInvariant) ||
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new RpgCompileException(line, "Native positions, lengths and decimals require right-justified unsigned digits.");
        return value;
    }

    private static string ReadTraditionalKeywords(List<string> lines, ref int index, int shift, CancellationToken cancellationToken)
    {
        var fragments = new StringBuilder(FixedDefinitionKeywords(Col(lines[index], 44 + shift, 80 + shift), index + 1));
        for (index++; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            if (!TraditionalDefinitionAt(lines[index], shift) || Col(lines[index], 7 + shift, 43 + shift).Any(c => c != ' ')) break;
            fragments.Append(' ').Append(FixedDefinitionKeywords(Col(lines[index], 44 + shift, 80 + shift), index + 1));
            if (fragments.Length > 32768) throw new RpgCompileException(index + 1, "Native declaration exceeds 32768 characters.");
        }
        return fragments.ToString();
    }
}
