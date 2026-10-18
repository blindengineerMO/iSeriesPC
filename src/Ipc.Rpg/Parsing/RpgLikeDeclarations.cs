using System.Globalization;
using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static RpgDataStructure? NativeStructureTemplate(RpgProgram program, string name, bool initialization, int line)
        => program.ForwardStructureLookup?.Invoke(name.Trim(), initialization, line) ?? program.FindDs(name.Trim());

    private static object DeclarationIdentity(RpgProgram program, int line, string name)
        => program.DeclarationIdentityLookup?.Invoke(line, name) ?? new object();

    private static RpgField? LikeDefinition(RpgProgram symbols, DeclarationKeyword type, int line, RpgDataStructure? current)
    {
        if (type.Name != "LIKE") return null;
        var arguments = DeclarationArguments(type.Arguments ?? "", line);
        if (arguments.Length is < 1 or > 2 || !Regex.IsMatch(arguments[0], @"\A[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?\z", RegexOptions.CultureInvariant))
            throw new RpgCompileException(line, "LIKE requires a declared field, subfield, structure or returning prototype, with optional signed length adjustment.");
        var target = arguments[0].ToUpperInvariant();
        RpgField? field = symbols.ForwardTypeLookup?.Invoke(target, current);
        var parts = target.Split('.');
        if (field is null && current is not null && (parts.Length == 1 || parts.Length == 2 && parts[0] == current.Name))
            field = current.Find(parts[^1]) is { } member ? ElementType(member) : null;
        field ??= ExactActualType(symbols, RpgExpressionParser.Parse(target));
        if (field is null && symbols.FindPrototype(target) is { } prototype) field = prototype.NativeReturnField;
        if (field is null || field.Source == RpgFieldSource.Constant)
            throw new RpgCompileException(line, "LIKE requires an available declared type; missing, constant and void-prototype references cannot define storage.");
        if (field.Varying || field.Kind == RpgFieldKind.Indicator && field.Length != 1 ||
            field.Kind == RpgFieldKind.Date && field.Length != 10 || field.Kind == RpgFieldKind.Time && field.Length != 8 ||
            field.Kind == RpgFieldKind.Timestamp && field.Length != 26)
            throw new RpgCompileException(line, "LIKE cannot discard varying or unsupported date/time format attributes.");
        var length = field.Length;
        if (arguments.Length == 2)
        {
            var adjustment = arguments[1].Trim();
            if (adjustment.Length < 2 || adjustment[0] is not ('+' or '-') ||
                field.Kind is not (RpgFieldKind.Character or RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Integer))
                throw new RpgCompileException(line, "LIKE length adjustments require an explicit sign and a character, packed, zoned or integer type.");
            var amount = DeclarationInteger(symbols, adjustment[1..].Trim(), line, 0, 32767, current);
            length += adjustment[0] == '+' ? amount : -amount;
        }
        return new() { Name = field.Name, Kind = field.Kind, Length = length, Decimals = field.Decimals, OpaqueBytes = field.OpaqueBytes,
            LikeType = field.LikeType, DeclaredLike = true };
    }

    private static DeclarationKeyword LikeScalarKeyword(RpgField field, int line)
    {
        var name = field.Kind switch { RpgFieldKind.Character => "CHAR", RpgFieldKind.Packed => "PACKED", RpgFieldKind.Zoned => "ZONED",
            RpgFieldKind.Integer => "INT", RpgFieldKind.Float => "FLOAT", RpgFieldKind.Indicator => "IND", RpgFieldKind.Date => "DATE",
            RpgFieldKind.Time => "TIME", RpgFieldKind.Timestamp => "TIMESTAMP", _ => throw new RpgCompileException(line, "LIKE source uses an unsupported native type.") };
        return new(name, field.Kind is RpgFieldKind.Indicator or RpgFieldKind.Date or RpgFieldKind.Time or RpgFieldKind.Timestamp ? null :
            field.Length.ToString(CultureInfo.InvariantCulture) + (field.Kind is RpgFieldKind.Packed or RpgFieldKind.Zoned ? ":" + field.Decimals.ToString(CultureInfo.InvariantCulture) : ""));
    }

    private static string FixedLikeDeclaration(string text, string length, string type, string decimals, int line)
    {
        var options = DeclarationKeywords(text, line); var likes = options.Where(k => k.Name == "LIKE").ToArray();
        if (likes.Length != 1 || type.Any(c => c != ' ') || decimals.Any(c => c != ' '))
            throw new RpgCompileException(line, "Fixed LIKE requires one LIKE keyword and blank type/decimal columns.");
        var arguments = DeclarationArguments(likes[0].Arguments ?? "", line);
        if (arguments.Length != 1) throw new RpgCompileException(line, "Fixed LIKE specifies its length adjustment in the Length column.");
        var adjustment = length.Trim();
        if (adjustment.Length != 0 && !Regex.IsMatch(length, @"\A *[+-][0-9]+\z", RegexOptions.CultureInvariant))
            throw new RpgCompileException(line, "Fixed LIKE Length requires a right-justified signed adjustment.");
        return "LIKE(" + arguments[0] + (adjustment.Length == 0 ? "" : ":" + adjustment) + ") " +
            string.Join(' ', options.Where(k => k.Name != "LIKE").Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")")));
    }
}
