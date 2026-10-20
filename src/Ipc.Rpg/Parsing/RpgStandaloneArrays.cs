using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static bool? SortSequence(RpgProgram program, RpgExpr expression)
    {
        if (expression is RpgBuiltin { Name: "SUBARR", Arguments.Count: > 0 } slice) return SortSequence(program, slice.Arguments[0]);
        if (expression is RpgFieldRef field) return program.FindDsElement(field.Name)?.ArrayDescending ?? program.FindField(field.Name)?.ArrayDescending;
        if (expression is RpgMemberRef member)
        {
            var name = member.Target switch { RpgFieldRef root => root.Name, RpgDynamicIndexRef indexed => indexed.Name, RpgIndexedRef indexed => indexed.Name, _ => null };
            return name is null ? null : program.FindDs(name)?.Find(member.Name)?.ArrayDescending;
        }
        return null;
    }
    private static bool? ArraySequence(IReadOnlyList<DeclarationKeyword> keywords, bool array, int line)
    {
        var options = keywords.Where(k => k.Name is "ASCEND" or "DESCEND").ToArray();
        if (options.Length == 0) return null;
        if (!array || options.Length != 1 || options[0].Arguments is not null)
            throw new RpgCompileException(line, "An array accepts either argument-free ASCEND or DESCEND.");
        return options[0].Name == "DESCEND";
    }
    private static void DeclareNativeStandalone(RpgProgram program, RpgField field, int line)
    {
        var width = ScalarDeclarationWidth(field); var size = (long)width * field.Dimension;
        if (program.DataStructures.Count >= 256 || size > 1048576 ||
            program.DataStructures.Where(d => d.NativeStorage && !d.IsTemplate).Sum(d => (long)d.Length * d.StorageCount) + size > 16777216)
            throw new RpgCompileException(line, "Native array storage exceeds 256 declarations, 1 MiB per declaration or 16 MiB per program.");
        if (field.Kind == RpgFieldKind.Integer && field.InitializationExpression is { } initial)
        {
            var number = RpgValues.ToDecimal(DeclarationValue(program, initial, line));
            var min = width == 8 ? long.MinValue : -(1L << (width * 8 - 1));
            var max = width == 8 ? long.MaxValue : (1L << (width * 8 - 1)) - 1;
            if (number < min || number > max) throw new RpgCompileException(line, "Array integer INZ exceeds its native byte width.");
        }
        // Reuse native element views without exporting a synthetic structure name to RPG.
        var storage = new RpgDataStructure { Name = (program.IsNativeProcedure ? "\u0001PROC_" + program.NativeProcedureName : "") + (field.IsTable ? "\u0001TABLE" : field.IsArray ? "\u0001ARRAY" : "\u0001SCALAR") + program.DataStructures.Count, Anonymous = true,
            NativeStorage = true, BasedOn = field.BasedOn, DeclarationLine = line, Length = (int)size, InitializeSubfields = true };
        storage.Elements.Add(new() { Name = field.Name, LocalName = field.Name, Kind = field.Kind, Length = field.Length,
            NullCapable = field.NullCapable, NullIndicatorName = field.NullIndicatorName, ReadOnlyParameter = field.ReadOnlyParameter, LikeType = field.LikeType, DeclaredLike = field.DeclaredLike,
            Decimals = field.Decimals, StorageLength = width, Stride = width, IsArray = field.IsArray || field.IsTable, Dimension = field.Dimension,
            OpaqueBytes = field.OpaqueBytes, HasInitialValue = field.InitializationExpression is not null,
            InitialValue = field.InitializationExpression, DeclarationLine = line, ArrayDescending = field.ArrayDescending });
        program.DataStructures.Add(storage);
    }
}
