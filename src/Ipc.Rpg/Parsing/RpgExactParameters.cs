using Ipc.Rpg.Model;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    internal static RpgField? ExactActualType(RpgProgram program, RpgExpr expression)
    {
        if (expression is RpgBuiltin builtin && builtin.Name is "DATE" or "TIME" or "TIMESTAMP" or "FLOAT")
            return new RpgField { Name = builtin.Name, Kind = builtin.Name switch { "DATE" => RpgFieldKind.Date, "TIME" => RpgFieldKind.Time,
                "TIMESTAMP" => RpgFieldKind.Timestamp, _ => RpgFieldKind.Float }, Length = builtin.Name switch { "DATE" => 10, "TIME" or "FLOAT" => 8, _ => 26 } };
        if (expression is RpgProcedureExpression call) return program.FindPrototype(call.Name)?.NativeReturnField;
        if (expression is RpgDynamicIndexRef function && program.FindPrototype(function.Name) is { NativeProcedure: true } prototype) return prototype.NativeReturnField;
        if (expression is RpgIndexedRef fixedFunction && program.FindPrototype(fixedFunction.Name) is { NativeProcedure: true } fixedPrototype) return fixedPrototype.NativeReturnField;
        if (expression is RpgFieldRef named)
        {
            if (program.FindField(named.Name) is { } field) return field.Source == RpgFieldSource.Constant ? null : field;
            if (program.FindDs(named.Name) is { } ds) return StructureType(ds);
            if (program.FindDsElement(named.Name) is { } element) return ElementType(element);
        }
        if (expression is RpgDynamicIndexRef or RpgIndexedRef or RpgElementRef)
        {
            var (array, _) = FixedLookupTarget(expression);
            return ExactActualType(program, array) is { IsArray: true } type ? new RpgField { Name = type.Name, Kind = type.Kind, Length = type.Length,
                LikeType = type.LikeType, DeclaredLike = type.DeclaredLike, Decimals = type.Decimals, IsDataStructure = type.IsDataStructure, ParameterStructure = type.ParameterStructure, OpaqueBytes = type.OpaqueBytes } : null;
        }
        if (expression is RpgMemberRef member && ExactActualType(program, member.Target)?.ParameterStructure is { } owner && owner.Find(member.Name) is { } subfield)
            return ElementType(subfield);
        return null;
    }

    private static RpgField StructureType(RpgDataStructure structure) => new() { Name = structure.Name, Kind = RpgFieldKind.Character,
        Length = structure.Length, IsDataStructure = true, OpaqueBytes = true, ParameterStructure = structure, IsArray = structure.IsArray, Dimension = structure.Dimension, LikeType = structure.LikeType };
    private static RpgField ElementType(RpgDsElement element) => new() { Name = element.Name, Kind = element.Kind, Length = element.Length,
        LikeType = element.LikeType, DeclaredLike = element.DeclaredLike, Decimals = element.Decimals, IsArray = element.IsArray, Dimension = element.Dimension, ArrayDescending = element.ArrayDescending, OpaqueBytes = element.OpaqueBytes };

    internal static string? ExactTypeError(RpgField actual, RpgField formal)
    {
        const string error = "OPTIONS(*EXACT) requires compatible type, precision, length, dimension/order and related structure definitions.";
        if (formal.DeclaredLike && !ReferenceEquals(actual.LikeType, formal.LikeType)) return error;
        if (actual.IsArray != formal.IsArray || actual.IsArray && (actual.Dimension != formal.Dimension || actual.ArrayDescending != formal.ArrayDescending)) return error;
        if (formal.IsDataStructure)
        {
            if (actual.ParameterStructure is not { } a || formal.ParameterStructure is not { } f ||
                !ReferenceEquals(a.TypeOrigin ?? a.LikeType, f.TypeOrigin ?? f.LikeType)) return error;
            return null;
        }
        var copy = formal.ReadOnlyParameter || formal.ValueParameter;
        if (!copy) return actual.Kind != formal.Kind || actual.Length != formal.Length || actual.Decimals != formal.Decimals || actual.OpaqueBytes != formal.OpaqueBytes ? error : null;
        var numeric = formal.Kind is RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Integer or RpgFieldKind.Decimal;
        if (numeric)
            return actual.Kind is not (RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Integer or RpgFieldKind.Decimal) ||
                actual.Decimals > formal.Decimals || actual.Length - actual.Decimals > formal.Length - formal.Decimals ? error : null;
        if (actual.Kind != formal.Kind || actual.OpaqueBytes != formal.OpaqueBytes) return error;
        return formal.Kind is RpgFieldKind.Character or RpgFieldKind.Float or RpgFieldKind.Timestamp &&
            !(formal.Kind == RpgFieldKind.Character && formal.TrimParameter) && actual.Length > formal.Length ? error : null;
    }
}
