using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static int? NativeDefinitionShift(string text)
    {
        if (text.Length > 5 && text[..5].All(c => c == ' ' || char.IsAsciiDigit(c)) && char.ToUpperInvariant(text[5]) == 'D') return 0;
        if (text.Length > 6 && text[..6].All(c => c == ' ' || char.IsAsciiDigit(c)) && char.ToUpperInvariant(text[6]) == 'D' &&
            (Col(text, 25, 26).Trim().ToUpperInvariant() is "S" or "C" or "DS" or "PR" or "PI" ||
             Col(text, 8, 44).All(c => c == ' '))) return 1;
        return null;
    }

    private static void ReadNativeFixedDefinition(List<string> lines, ref int index, RpgProgram program, int shift, bool bind, CancellationToken cancellationToken, ForwardDeclarationTypes? collector = null)
    {
        var number = index + 1; var header = lines[index].PadRight(81); string Column(int first, int last) => Col(header, first + shift, last + shift);
        var name = Column(7, 21).Trim().ToUpperInvariant(); var definition = Column(24, 25).Trim().ToUpperInvariant();
        if (Column(7, 80).All(c => c == ' ')) { lines[index] = ""; return; }
        if (definition is "PR" or "PI")
        {
            ReadNativeFixedSignature(lines, ref index, program, shift, bind, cancellationToken, collector);
            return;
        }
        if (definition == "DS")
        {
            ReadNativeFixedStructure(lines, ref index, program, shift, bind, cancellationToken, collector);
            return;
        }
        if (definition is not ("S" or "C"))
            throw new RpgCompileException(number, "Traditional fixed definitions support S, C, DS, PR and main PI; orphan continuations are unsupported.");
        if (!RpgWorkstationBindings.Name(name) || Column(22, 23).Any(c => c != ' ') || Column(26, 32).Any(c => c != ' ') || Column(43, 43) != " ")
            throw new RpgCompileException(number, "Invalid traditional standalone name, external/DS type, From position or reserved column.");
        var fragments = new StringBuilder(FixedDefinitionKeywords(Column(44, 80), number)); lines[index] = "";
        var next = index + 1;
        while (next < lines.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[next])) { next++; continue; }
            if (NativeDefinitionShift(lines[next]) != shift || Col(lines[next], 7 + shift, 43 + shift).Any(c => c != ' ')) break;
            fragments.Append(' ').Append(FixedDefinitionKeywords(Col(lines[next], 44 + shift, 80 + shift), next + 1));
            lines[next] = ""; index = next++;
        }
        var keywords = fragments.ToString();
        if (definition == "C")
        {
            if (Column(33, 42).Any(c => c != ' ')) throw new RpgCompileException(number, "Traditional named constants cannot specify length, type or decimals.");
            collector?.Constant(name, keywords, number);
            if (bind) DeclareFreeConstant(program, "DCL-C " + name + " " + keywords, number);
            return;
        }
        if (DeclarationKeywords(keywords, number).Any(k => k.Name == "LIKE"))
        {
            var like = FixedLikeDeclaration(keywords, Column(33, 39), Column(40, 40), Column(41, 42), number);
            collector?.Scalar(name, like, number);
            if (bind) DeclareFreeScalar(program, name, like, number);
            return;
        }
        int? Number(int first, int last)
        {
            var text = Column(first, last);
            if (text.All(c => c == ' ')) return null;
            if (!Regex.IsMatch(text, @"\A *[0-9]+\z", RegexOptions.CultureInvariant) || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new RpgCompileException(number, "Traditional length/decimal entries require right-justified unsigned digits.");
            return value;
        }
        var length = Number(33, 39); var decimals = Number(41, 42); var type = char.ToUpperInvariant(header[39 + shift]);
        var scalar = TraditionalDefinitionType(length, decimals, type, number, ref keywords);
        collector?.Scalar(name, scalar + " " + keywords, number);
        if (bind) DeclareFreeScalar(program, name, scalar + " " + keywords, number);
    }

    private static string TraditionalDefinitionType(int? length, int? decimals, char type, int line, ref string keywords, bool subfield = false)
    {
        if (type == '*' && !HasKeyword(keywords, "PROCPTR"))
        {
            if (length is not (null or 16) || decimals is not null) throw new RpgCompileException(line, "Basing pointers require sixteen bytes and no decimals.");
            return "POINTER";
        }
        if (type != '*' || !HasKeyword(keywords, "PROCPTR")) return TraditionalScalarType(length, decimals, type, line, subfield);
        var options = DeclarationKeywords(keywords, line);
        if (length is not (null or 16) || decimals is not null || options.Count(k => k.Name == "PROCPTR") != 1 || options.Any(k => k.Name == "PROCPTR" && k.Arguments is not null))
            throw new RpgCompileException(line, "Procedure pointers require sixteen bytes, one PROCPTR keyword and no decimals.");
        keywords = string.Join(' ', options.Where(k => k.Name != "PROCPTR").Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")")));
        return "POINTER(*PROC)";
    }

    private static string TraditionalScalarType(int? length, int? decimals, char type, int number, bool subfield = false)
    {
        if (type == ' ') type = decimals is null ? 'A' : subfield ? 'S' : 'P';
        string Size() => length?.ToString(CultureInfo.InvariantCulture) ?? throw new RpgCompileException(number, "Traditional character/numeric definitions require a length.");
        string Scale() => decimals?.ToString(CultureInfo.InvariantCulture) ?? throw new RpgCompileException(number, "Traditional numeric definitions require decimal positions, including zero.");
        return type switch
        {
            'A' when decimals is null => "CHAR(" + Size() + ")",
            'P' or 'S' => (type == 'P' ? "PACKED(" : "ZONED(") + Size() + ":" + Scale() + ")",
            'I' when decimals == 0 => "INT(" + Size() + ")",
            'F' when decimals is null => "FLOAT(" + Size() + ")",
            'N' when decimals is null && length is null or 1 => "IND",
            'D' when decimals is null && length is null or 10 => "DATE",
            'T' when decimals is null && length is null or 8 => "TIME",
            'Z' when decimals is null && length is null or 26 => "TIMESTAMP",
            _ => throw new RpgCompileException(number, "Unsupported traditional type/length/decimal combination; binary, unsigned, graphic, UCS-2, pointers and extended datetime forms remain open.")
        };
    }

    private static string FixedDefinitionKeywords(string text, int line)
    {
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
            if (text[index] == '\'')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '\'') index++;
                else quoted = !quoted;
            }
        if (quoted) throw new RpgCompileException(line, "Traditional literal continuation requires the remaining +/- continuation contract.");
        return text.Trim();
    }
}
