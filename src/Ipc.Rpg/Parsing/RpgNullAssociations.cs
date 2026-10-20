using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static (string Extract, bool NullMap) NullExtractTypes(string[] parts, int line)
    {
        parts = parts.Select(p => p.ToUpperInvariant()).ToArray();
        var extracts = parts.Where(p => p != "*NULL").ToArray();
        if (parts.Length > 2 || parts.Count(p => p == "*NULL") > 1 || extracts.Length > 1 || extracts.Any(p => p is not ("*INPUT" or "*OUTPUT" or "*ALL" or "*KEY")))
            throw new RpgCompileException(line, "Extract types require *INPUT, *OUTPUT, *ALL or *KEY and optional *NULL.");
        return (extracts.FirstOrDefault() ?? "*INPUT", parts.Contains("*NULL"));
    }
    private static string? StructureNullOption(RpgProgram program, IReadOnlyList<DeclarationKeyword> options, int line)
    {
        var option = options.SingleOrDefault(k => k.Name == "NULLIND");
        if (option is null) return null;
        if (!NativeNullOption(program, options, line) || option.Arguments is null)
            throw new RpgCompileException(line, "A data structure NULLIND requires a matching null-indicator structure name.");
        return option.Arguments.ToUpperInvariant();
    }
    private static void ValidateNullAssociations(RpgProgram program)
    {
        ValidateNullParameterAssociations(program);
        var used = new HashSet<(RpgDataStructure, object)>();
        var entry = program.NativeParameterLists.GetValueOrDefault("/*ENTRY") ?? [];
        var templates = new HashSet<RpgField>();
        foreach (var field in program.Fields.Where(f => f.IsTemplate && f.NullIndicatorName is not null && !program.GlobalFields.Contains(f)))
        {
            var target = program.FindField(field.NullIndicatorName!);
            if (target is not { IsTemplate: true, Kind: RpgFieldKind.Indicator, NullCapable: false } || program.GlobalFields.Contains(target) ||
                target.IsArray != field.IsArray || target.Dimension != field.Dimension || !templates.Add(target)) Fail(field.DeclarationLine);
        }
        foreach (var root in program.DataStructures.Where(d => !program.GlobalStructures.Contains(d))) Visit(root, root);
        void Visit(RpgDataStructure root, RpgDataStructure layout)
        {
            if (ReferenceEquals(root, layout) && root.NullIndicatorName is { } mapName)
            {
                var target = program.FindDs(mapName);
                if (target is null || program.GlobalStructures.Contains(target) || root.StaticLocal != target.StaticLocal || root.IsTemplate != target.IsTemplate ||
                    root.IsArray != target.IsArray || root.Dimension != target.Dimension || root.IsMultipleOccurrence != target.IsMultipleOccurrence || root.Occurrences != target.Occurrences)
                    Fail(root.DeclarationLine);
                ValidateMap(root, target!, root.DeclarationLine); Claim(target!, target!, root.DeclarationLine);
            }
            foreach (var element in layout.Elements)
            {
                if (element.NullIndicatorName is { } name)
                {
                    if (layout.Anonymous)
                    {
                        var source = program.FindField(element.Name)!;
                        var targetElement = program.FindDsElement(name);
                        var targetRoot = targetElement is null ? null : program.DataStructures.First(d => d.Elements.Contains(targetElement));
                        var target = ExactActualType(program, new RpgFieldRef(name));
                        var targetField = program.FindField(name);
                        if (target is null || targetElement is null || targetRoot is null || program.GlobalStructures.Contains(targetRoot) ||
                            targetRoot.IsTemplate != source.IsTemplate || source.StaticLocal != (targetRoot.StaticLocal || targetField?.StaticLocal == true) ||
                            targetRoot.IsArray || targetRoot.IsMultipleOccurrence || target.Source == RpgFieldSource.Constant || target.Kind != RpgFieldKind.Indicator ||
                            target.IsArray != element.IsArray || target.Dimension != element.Dimension || target.NullCapable)
                            Fail(element.DeclarationLine);
                        Claim(targetRoot!, targetElement!, element.DeclarationLine);
                    }
                    else
                    {
                        var target = layout.Find(name);
                        if (target is null || target.NullCapable || target.IsArray != element.IsArray || target.Dimension != element.Dimension ||
                            element.NestedStructure is null && (target.NestedStructure is not null || target.Kind != RpgFieldKind.Indicator)) Fail(element.DeclarationLine);
                        if (element.NestedStructure is { } data) ValidateMap(data, target!.NestedStructure, element.DeclarationLine);
                        Claim(root, target!, element.DeclarationLine);
                    }
                }
                if (element.NestedStructure is { } nested) Visit(root, nested);
            }
        }
        void Claim(RpgDataStructure root, object target, int line) { if (!used.Add((root, target))) Fail(line); }
        static void Fail(int line) => throw new RpgCompileException(line, "NULLIND requires a unique nonnullable indicator with matching scope, storage and dimensions, or a matching external *NULL structure.");
        static void ValidateMap(RpgDataStructure data, RpgDataStructure? target, int line)
        {
            if (data.RecordOrigin is not { NullMap: false } origin || target?.RecordOrigin is not { NullMap: true } map ||
                origin.Extract != map.Extract || origin.Signature != map.Signature || origin.Family != map.Family || target.NullIndicatorName is not null ||
                target.Elements.Any(e => e.NullCapable || e.Kind != RpgFieldKind.Indicator || e.NestedStructure is not null) ||
                data.Elements.Where(e => e.ExternalName is not null).Any(e => target.Elements.All(t => t.ExternalName != e.ExternalName))) Fail(line);
        }
    }
}
