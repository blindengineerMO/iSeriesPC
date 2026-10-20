using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void SetNullMapParameter(RpgProgram program, RpgField field, bool enabled)
    {
        field.NullMapParameter = enabled;
        if (!enabled || field.IsDataStructure) return;
        field.NullCapable = true;
        foreach (var element in program.DataStructures.SelectMany(d => d.Elements).Where(e => e.Name == field.Name)) element.NullCapable = true;
    }
    internal static bool SameNullCapabilities(RpgField left, RpgField right) => left.ParameterStructure is { } a
        ? right.ParameterStructure is { } b && SameNullStructure(a, b) : !right.IsDataStructure;
    private static bool SameNullStructure(RpgDataStructure left, RpgDataStructure right) => left.Elements.Count == right.Elements.Count &&
        left.Elements.Zip(right.Elements).All(p => p.First.NullCapable == p.Second.NullCapable && p.First.IsArray == p.Second.IsArray && p.First.Dimension == p.Second.Dimension &&
            (p.First.NestedStructure is { } nested ? p.Second.NestedStructure is { } other && SameNullStructure(nested, other) : p.Second.NestedStructure is null));
    internal static string? NullParameterTypeError(RpgProgram program, RpgExpr expression, RpgField formal)
    {
        const string error = "OPTIONS(*NULLIND) requires an exact variable type, dimension and related structure/null-capability layout, including for CONST.";
        if (!Variable(expression) || ExactActualType(program, expression) is not { } actual ||
            !formal.ReadOnlyParameter && actual.ReadOnlyParameter || actual.IsDataStructure != formal.IsDataStructure || actual.IsArray != formal.IsArray || actual.IsArray && actual.Dimension != formal.Dimension ||
            actual.Kind != formal.Kind || actual.Length != formal.Length || actual.Decimals != formal.Decimals || actual.OpaqueBytes != formal.OpaqueBytes ||
            ExactTypeError(actual, formal) is not null || !SameNullCapabilities(actual, formal)) return error;
        return null;
        bool Variable(RpgExpr expr) => expr switch {
            RpgFieldRef f => program.FindField(f.Name) is { Source: not RpgFieldSource.Constant, IsTemplate: false } || program.FindDs(f.Name) is { IsTemplate: false } || program.FindDsElement(f.Name) is not null,
            RpgDynamicIndexRef f => program.FindPrototype(f.Name) is null && Variable(new RpgFieldRef(f.Name)),
            RpgIndexedRef f => program.FindPrototype(f.Name) is null && Variable(new RpgFieldRef(f.Name)),
            RpgMemberRef f => Variable(f.Target), RpgElementRef f => Variable(f.Target), _ => false };
    }
}

public static partial class RpgCompiler
{
    private static void ValidateNullParameterAssociations(RpgProgram program)
    {
        if (program.NativeParameterLists.TryGetValue("/*ENTRY", out var entry))
            Validate(entry.Select(name => program.FindField(name)!).ToArray(), entry);
        foreach (var prototype in program.Prototypes.Where(p => p.NativeSignature && p.NativeParameterNames.Count == p.NativeParameters.Count))
            Validate(prototype.NativeParameters, prototype.NativeParameterNames);
        static void Validate(IReadOnlyList<RpgField> fields, IReadOnlyList<string> names)
        {
            var used = new HashSet<int>();
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index];
                if (field.NullIndicatorName is not { } name) continue;
                var targets = Enumerable.Range(0, names.Count).Where(i => names[i].Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (targets.Length != 1 || targets[0] == index) Fail(field);
                var position = targets[0]; var target = fields[position];
                if (!used.Add(position) || field.NullMapParameter || target.NullCapable || target.NullMapParameter || target.NullIndicatorName is not null ||
                    field.IsArray != target.IsArray || field.Dimension != target.Dimension ||
                    (field.ParameterStructure is { } data ? target.ParameterStructure is not { } map || !NamedParameterMapMatches(data, map) : target.IsDataStructure || target.Kind != RpgFieldKind.Indicator)) Fail(field);
                field.NullIndicatorParameterIndex = position;
            }
        }
        static void Fail(RpgField field) => throw new RpgCompileException(field.DeclarationLine, "A parameter NULLIND must identify one unique nonnullable indicator parameter or matching external *NULL map in the same signature.");
    }
    private static bool NamedParameterMapMatches(RpgDataStructure data, RpgDataStructure map) => data.RecordOrigin is { NullMap: false } source && map.RecordOrigin is { NullMap: true } target &&
        source.Family == target.Family && source.Signature == target.Signature && source.Extract == target.Extract && map.Elements.All(e => !e.NullCapable && e.Kind == RpgFieldKind.Indicator && e.NestedStructure is null) &&
        data.Elements.Where(e => e.ExternalName is not null).All(e => map.Elements.Any(m => m.ExternalName == e.ExternalName));
}
