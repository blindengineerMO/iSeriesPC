using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    internal readonly record struct LookupLayout(RpgFieldKind Kind, int Length, int Decimals, bool IsArray,
        bool Native, bool Constant, bool? Descending, bool IsTable = false, bool Opaque = false, bool ReadOnly = false);

    internal static (RpgExpr Array, RpgExpr? Index) FixedLookupTarget(RpgExpr operand) => operand switch
    {
        RpgDynamicIndexRef indexed => (new RpgFieldRef(indexed.Name), indexed.Index),
        RpgIndexedRef indexed => (new RpgFieldRef(indexed.Name), new RpgLiteral(indexed.Index)),
        RpgElementRef indexed => (indexed.Target, indexed.Index),
        _ => (operand, null)
    };
    internal static LookupLayout? FixedLookupLayout(RpgProgram program, RpgExpr expression)
    {
        if (expression is RpgIndicatorRef) return new(RpgFieldKind.Indicator, 1, 0, false, false, false, null);
        if (expression is RpgDynamicIndexRef or RpgIndexedRef or RpgElementRef)
        {
            var (array, _) = FixedLookupTarget(expression);
            return FixedLookupLayout(program, array) is { IsArray: true } element ? element with { IsArray = false } : null;
        }
        RpgDsElement? subfield = null; RpgDataStructure? structure = null;
        if (expression is RpgFieldRef named)
        {
            if (program.FindField(named.Name) is { } field)
                return field.ParameterStructure is not null ? null : new(field.Kind, field.Length, field.Decimals, field.IsArray, field.NativeArray || field.NativeScalar || field.IsTable,
                    field.Source == RpgFieldSource.Constant, field.ArrayDescending, field.IsTable, field.OpaqueBytes, field.ReadOnlyParameter);
            subfield = program.FindDsElement(named.Name);
            if (subfield is not null) structure = program.DataStructures.Single(d => d.Elements.Contains(subfield));
            if (structure?.IsArray == true) return null;
        }
        else if (expression is RpgMemberRef member)
        {
            var name = member.Target switch { RpgFieldRef root => root.Name, RpgDynamicIndexRef root => root.Name, RpgIndexedRef root => root.Name, _ => null };
            structure = name is null ? null : program.FindDs(name);
            if (structure is null || !structure.Qualified || structure.IsArray != (member.Target is RpgDynamicIndexRef or RpgIndexedRef)) return null;
            subfield = structure.Find(member.Name);
        }
        return subfield is null ? null : new(subfield.Kind, subfield.Length, subfield.Decimals, subfield.IsArray,
            structure!.NativeStorage, false, subfield.ArrayDescending, Opaque: subfield.OpaqueBytes, ReadOnly: subfield.ReadOnlyParameter);
    }
    private static bool LookupNumeric(RpgFieldKind kind) => kind is RpgFieldKind.Integer or RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Decimal;
    private static void ValidateFixedLookup(RpgProgram program, RpgStatement statement, int line)
    {
        var (array, index) = FixedLookupTarget(RpgExpressionParser.Parse(statement.Factor2!));
        if (FixedLookupLayout(program, array) is not { Native: true } layout || !layout.IsArray && !layout.IsTable)
            throw new RpgCompileException(line, "Native LOOKUP requires a native array or table; (*) key forms require %LOOKUP or %LOOKUPNE.");
        if (layout.IsTable && index is not null) throw new RpgCompileException(line, "A table cannot have an index.");
        if (statement.Result is { } alternate && (!layout.IsTable || program.FindField(alternate) is not { IsTable: true }))
            throw new RpgCompileException(line, "LOOKUP result is allowed only for a table search and must name another table.");
        if ((statement.Indicator1 != 0 || statement.Indicator2 != 0) && layout.Descending is null)
            throw new RpgCompileException(line, "HI/LO LOOKUP requires ASCEND or DESCEND on the array.");
        var search = RpgExpressionParser.Parse(statement.Factor1!);
        if (FixedLookupLayout(program, search) is { Constant: false } field)
        {
            if (field.IsArray || (LookupNumeric(layout.Kind) ? !LookupNumeric(field.Kind) : layout.Kind != field.Kind) ||
                layout.Kind is not (RpgFieldKind.Date or RpgFieldKind.Time) && layout.Length != field.Length)
                throw new RpgCompileException(line, "LOOKUP fields require matching types and lengths; non-float numeric formats may differ, and decimal positions are ignored.");
        }
        else
        {
            var literal = search;
            while (literal is RpgUnary unary) literal = unary.Operand;
            if (search is RpgUnary && (literal is not RpgLiteral number || !RpgValues.IsNumeric(number.Value)))
                throw new RpgCompileException(line, "A LOOKUP literal sign requires a numeric literal.");
            if (literal is not (RpgLiteral or RpgHexLiteral) && FixedLookupLayout(program, search) is not { Constant: true })
                throw new RpgCompileException(line, "LOOKUP factor 1 requires a scalar field, literal, named constant or supported figurative constant.");
            if (statement.Factor1!.StartsWith('*') && statement.Factor1.ToUpperInvariant() is not
                ("*HIVAL" or "*LOVAL" or "*BLANK" or "*BLANKS" or "*ZERO" or "*ZEROS" or "*ON" or "*OFF" or "*YES" or "*NO"))
                throw new RpgCompileException(line, "Unsupported LOOKUP figurative constant.");
            var special = statement.Factor1.ToUpperInvariant();
            if (special is "*HIVAL" or "*LOVAL") { }
            else if (special is "*BLANK" or "*BLANKS" && layout.Kind == RpgFieldKind.Character) { }
            else if (special is "*ZERO" or "*ZEROS" && (layout.Kind == RpgFieldKind.Character || LookupNumeric(layout.Kind) || layout.Kind == RpgFieldKind.Float)) { }
            else
            {
                var value = literal is RpgHexLiteral bytes ? bytes.ToArray() : DeclarationValue(program, statement.Factor1, line);
                var compatible = layout.Kind switch
                {
                    RpgFieldKind.Character => value is string or byte[],
                    RpgFieldKind.Indicator => value is bool,
                    RpgFieldKind.Float => RpgValues.IsNumeric(value),
                    _ when LookupNumeric(layout.Kind) => RpgValues.IsNumeric(value) && !RpgValues.IsFloat(value),
                    RpgFieldKind.Date or RpgFieldKind.Timestamp => value is DateTime or DateTimeOffset,
                    RpgFieldKind.Time => value is TimeSpan,
                    _ => false
                };
                if (!compatible || value is string text && text.Length > layout.Length || value is byte[] raw && raw.Length > layout.Length)
                    throw new RpgCompileException(line, "LOOKUP literal/constant is incompatible with its array type or length.");
            }
        }
        if (index is not null)
        {
            if (FixedLookupLayout(program, index) is { ReadOnly: true })
                throw new RpgCompileException(line, "A CONST parameter cannot be a writable LOOKUP index.");
            if (FixedLookupLayout(program, index) is { IsArray: false } counter && LookupNumeric(counter.Kind) && counter.Decimals == 0) return;
            var literal = index; while (literal is RpgUnary unary) literal = unary.Operand;
            if (literal is RpgLiteral number && RpgValues.IsNumeric(number.Value) && !RpgValues.IsFloat(number.Value) &&
                RpgValues.ToDecimal(number.Value) == decimal.Truncate(RpgValues.ToDecimal(number.Value)) &&
                (decimal.GetBits(RpgValues.ToDecimal(number.Value))[3] & 0x00FF0000) == 0) return;
            throw new RpgCompileException(line, "LOOKUP index requires a non-float numeric field/constant with zero decimal positions or an integer literal.");
        }
    }
}
