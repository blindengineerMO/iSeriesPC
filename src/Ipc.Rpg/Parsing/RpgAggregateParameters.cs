using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static LookupLayout? NativeCallLayout(RpgProgram program, RpgExpr expression)
    {
        if (expression is RpgFieldRef root && program.FindDs(root.Name) is { NativeStorage: true } structure)
            return new(RpgFieldKind.Character, structure.Length, 0, structure.IsArray, true, false, null, Opaque: true, ReadOnly: structure.ReadOnlyParameter);
        if (expression is RpgDynamicIndexRef or RpgIndexedRef or RpgElementRef)
        {
            var (array, _) = FixedLookupTarget(expression);
            return NativeCallLayout(program, array) is { IsArray: true } element ? element with { IsArray = false } : null;
        }
        return FixedLookupLayout(program, expression);
    }

    private static void DeclareNativeParameter(RpgProgram destination, RpgProgram symbols, string name, string text, int line,
        bool allocate, bool readOnly = false, bool noPass = false, bool omit = false, bool byValue = false)
    {
        var keywords = DeclarationKeywords(text, line);
        if (keywords.FirstOrDefault()?.Name is not ("LIKEDS" or "LIKEREC"))
        {
            DeclareFreeScalar(destination, name, text, line, symbols, standaloneStorage: allocate, readOnlyParameter: readOnly,
                noPassParameter: noPass, omitParameter: omit, valueParameter: byValue, parameter: true);
            return;
        }
        if (keywords.Skip(1).Any(k => k.Name != "DIM") || keywords.Count(k => k.Name == "DIM") > 1 ||
            keywords[0].Arguments is not { } template || (keywords[0].Name == "LIKEREC" ? RecordTemplate(symbols, template, line) : NativeStructureTemplate(symbols, template, false, line)) is not { NativeStorage: true } definition)
            throw new RpgCompileException(line, "LIKEDS parameters require a native template and optional fixed DIM.");
        var dimension = keywords.SingleOrDefault(k => k.Name == "DIM");
        var count = dimension is null ? 1 : DeclarationInteger(symbols, dimension.Arguments ?? "", line, 1, 32767);
        if (definition.Length < 1 || (long)definition.Length * count > 1048576 || definition.IsMultipleOccurrence || definition.ProgramStatus || definition.IndicatorDataStructure)
            throw new RpgCompileException(line, "Parameter structures require a plain layout of at most 1 MiB.");
        if (!RpgWorkstationBindings.Name(name) || HasLocalDeclaration(destination, name)) throw new RpgCompileException(line, "Invalid or duplicate aggregate parameter.");
        if (destination.IsNativeProcedure) destination.Fields.RemoveAll(f => destination.GlobalFields.Contains(f) && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var identity = DeclarationIdentity(symbols, line, name);
        var structure = new RpgDataStructure { Name = name, LikeType = identity, Length = definition.Length, NativeStorage = true, Qualified = true,
            TypeOrigin = definition.TypeOrigin ?? definition.LikeType, RecordOrigin = definition.RecordOrigin,
            Dimension = count, IsArray = dimension is not null, ParameterName = name, ReadOnlyParameter = readOnly, DeclarationLine = line };
        foreach (var element in definition.Elements)
            structure.Elements.Add(new() { Name = name + "." + element.LocalName, LocalName = element.LocalName, Kind = element.Kind,
                Length = element.Length, Decimals = element.Decimals, StorageLength = element.StorageLength, Stride = element.Stride, Dimension = element.Dimension,
                IsArray = element.IsArray, Offset = element.Offset, HasExplicitOffset = true, OpaqueBytes = element.OpaqueBytes, Anonymous = element.Anonymous,
                ReadOnlyParameter = readOnly, DeclarationLine = line, ArrayDescending = element.ArrayDescending, OverlayTarget = element.OverlayTarget,
                LikeType = element.LikeType, DeclaredLike = element.DeclaredLike });
        destination.Fields.Add(new() { Name = name, LikeType = identity, Kind = RpgFieldKind.Character, Length = definition.Length, OpaqueBytes = true,
            IsDataStructure = true, IsArray = dimension is not null, Dimension = count, ParameterStructure = structure, DeclarationLine = line,
            ReadOnlyParameter = readOnly, NoPassParameter = noPass, OmitParameter = omit, ValueParameter = byValue });
        if (allocate)
        {
            if (destination.DataStructures.Count >= 256 || destination.DataStructures.Where(d => !d.IsTemplate).Sum(d => (long)d.Length * d.StorageCount) + (long)structure.Length * count > 16777216)
                throw new RpgCompileException(line, "Aggregate parameters exceed native variable storage bounds.");
            destination.DataStructures.Add(structure);
        }
    }

    internal static bool SameAggregateElement(RpgField left, RpgField right) => left.IsDataStructure == right.IsDataStructure &&
        left.Kind == right.Kind && left.Length == right.Length && left.Decimals == right.Decimals && left.OpaqueBytes == right.OpaqueBytes &&
        (left.ParameterStructure is null ? right.ParameterStructure is null : right.ParameterStructure is { } structure && SameStructureLayout(left.ParameterStructure, structure));

    private static bool SameStructureLayout(RpgDataStructure left, RpgDataStructure right) => left.Length == right.Length && left.Elements.Count == right.Elements.Count &&
        left.Elements.Zip(right.Elements).All(p => p.First.Kind == p.Second.Kind && p.First.Length == p.Second.Length && p.First.Decimals == p.Second.Decimals &&
            p.First.Offset == p.Second.Offset && p.First.StorageLength == p.Second.StorageLength && p.First.Stride == p.Second.Stride && p.First.Dimension == p.Second.Dimension && p.First.OpaqueBytes == p.Second.OpaqueBytes);
}
