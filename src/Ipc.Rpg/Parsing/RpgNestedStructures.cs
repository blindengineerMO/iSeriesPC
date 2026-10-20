using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static RpgDataStructure NestedLayout(RpgProgram program, RpgDataStructure owner, string name,
        IReadOnlyList<DeclarationKeyword> keywords, int line)
    {
        if (!owner.Qualified || keywords.Skip(1).Any(k => k.Name is not ("DIM" or "POS" or "OVERLAY" or "INZ" or "NULLIND")))
            throw new RpgCompileException(line, "LIKEDS/LIKEREC subfields require QUALIFIED and optional DIM, POS, OVERLAY or INZ.");
        var init = keywords.SingleOrDefault(k => k.Name == "INZ");
        if (init?.Arguments is not null && (keywords[0].Name != "LIKEDS" || !init.Arguments.Equals("*LIKEDS", StringComparison.OrdinalIgnoreCase)))
            throw new RpgCompileException(line, "Nested INZ is argument-free, or INZ(*LIKEDS) with LIKEDS.");
        var definition = keywords[0].Name == "LIKEREC" ? RecordTemplate(program, keywords[0].Arguments ?? "", line)
            : NativeStructureTemplate(program, keywords[0].Arguments ?? "", init?.Arguments is not null, line);
        if (definition is not { NativeStorage: true, Anonymous: false, IsMultipleOccurrence: false, ProgramStatus: false, IndicatorDataStructure: false })
            throw new RpgCompileException(line, "A nested structure requires a plain named native layout.");
        if (NestedDepth(definition) >= 64) throw new RpgCompileException(line, "Native data structure nesting exceeds 64 levels.");
        var result = CopyNestedLayout(definition, name, init?.Arguments is not null, StructureNullOption(program, keywords, line));
        return init is null ? result : CloneWithInitialization(result, init.Arguments is null || definition.InitializeSubfields);
    }

    private static int NestedFieldCount(RpgDataStructure structure) => structure.Elements.Count + structure.Elements.Sum(e => e.NestedStructure is { } child ? NestedFieldCount(child) : 0);

    private static int NestedDepth(RpgDataStructure structure) => 1 + structure.Elements.Select(e => e.NestedStructure is { } child ? NestedDepth(child) : 0).DefaultIfEmpty().Max();

    private static int StructureEnd(List<string> lines, int first, int cursor, bool columnLimited, CancellationToken cancellation)
    {
        var depth = 1;
        while (++cursor < lines.Count)
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[cursor]) || lines[cursor].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            var declaration = ReadFreeDeclaration(lines, ref cursor, cursor + 1, columnLimited);
            if (declaration.StartsWith("DCL-DS ", StringComparison.OrdinalIgnoreCase) || declaration.StartsWith("DCL-DS\t", StringComparison.OrdinalIgnoreCase))
            {
                var options = System.Text.RegularExpressions.Regex.Match(declaration, @"\ADCL-DS\s+\S+\s*(.*)\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
                if (!DeclarationKeywords(options, cursor + 1).Any(k => k.Name is "LIKEDS" or "LIKEREC" or "END-DS"))
                    if (++depth > 64) throw new RpgCompileException(cursor + 1, "Native data structure nesting exceeds 64 levels.");
            }
            else if (declaration.StartsWith("END-DS", StringComparison.OrdinalIgnoreCase) && --depth == 0) return cursor;
        }
        throw new RpgCompileException(first + 1, "DCL-DS without END-DS.");
    }

    private static RpgDataStructure CopyNestedLayout(RpgDataStructure source, string name, bool initialization, string? nullIndicator = null)
    {
        var result = new RpgDataStructure { Name = name, NullIndicatorName = nullIndicator, NativeStorage = true, Qualified = true, Length = source.Length,
            TypeOrigin = source.TypeOrigin ?? source.LikeType, LikeType = source.LikeType, RecordOrigin = source.RecordOrigin,
            InitializeSubfields = initialization && source.InitializeSubfields, DeclarationLine = source.DeclarationLine };
        foreach (var element in source.Elements)
            result.Elements.Add(new() { Name = name + "." + element.LocalName, LocalName = element.LocalName,
                NullCapable = element.NullCapable, NullIndicatorName = element.NullIndicatorName, ExternalName = element.ExternalName, ExternalDefaultInitialization = initialization && element.ExternalDefaultInitialization, Kind = element.Kind, Length = element.Length, Decimals = element.Decimals, StorageLength = element.StorageLength,
                Stride = element.Stride, Dimension = element.Dimension, IsArray = element.IsArray, Offset = element.Offset,
                HasExplicitOffset = true, OpaqueBytes = element.OpaqueBytes, Anonymous = element.Anonymous,
                LikeType = element.LikeType, DeclaredLike = element.DeclaredLike, ArrayDescending = element.ArrayDescending,
                OverlayTarget = element.OverlayTarget, DeclarationLine = element.DeclarationLine,
                HasInitialValue = initialization && element.HasInitialValue, InitialValue = initialization ? element.InitialValue : null,
                NestedStructure = element.NestedStructure is { } child ? CopyNestedLayout(child, name + "." + element.LocalName, initialization) : null });
        return result;
    }
}
