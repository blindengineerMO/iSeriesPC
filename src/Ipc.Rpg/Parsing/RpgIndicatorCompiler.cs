using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void ParseIndicatorBindings(RpgProgram program, List<WorkstationDeclaration> declarations)
    {
        for (var i = 0; i < declarations.Count; i++)
        {
            var declaration = declarations[i]; var keywords = DeclarationKeywords(declaration.Keywords, declaration.Line);
            var indicators = keywords.Where(k => k.Name == "INDDS").ToArray();
            if (indicators.Length == 0) continue;
            var ds = program.FindDs(indicators[0].Arguments ?? "");
            if (declaration.Disk || indicators.Length != 1 || ds is not { NativeStorage: true, IsTemplate: false, Anonymous: false, IsArray: false, ProgramStatus: false } ||
                ds.Elements.Any(e => e.Kind is not (RpgFieldKind.Indicator or RpgFieldKind.Character) || e.FeedbackKeyword is not null))
                throw new RpgCompileException(declaration.Line, "INDDS requires a named native structure, optionally OCCURS, with only IND or fixed CHAR subfields on WORKSTN.");
            if (ds.ExplicitLength && ds.Length != 99 || ds.Length > 99)
                throw new RpgCompileException(ds.DeclarationLine, "INDDS must be exactly 99 bytes; omit LEN or specify LEN(99).");
            ds.Length = 99; ds.IndicatorDataStructure = true;
            program.IndicatorBindings.Add(new(declaration.Name, ds.Name, declaration.Line));
            declarations[i] = declaration with { Keywords = string.Join(' ', keywords.Where(k => k.Name != "INDDS")
                .Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")"))) };
        }
    }
    private static void ValidateIndicatorBindings(RpgProgram program)
    {
        foreach (var binding in program.IndicatorBindings)
        {
            if (program.FindDs(binding.Structure)!.Length != 99)
                throw new RpgCompileException(binding.DeclarationLine, "INDDS LIKEDS layout exceeds 99 bytes after final binding.");
            if (program.Workstations.Single(f => f.Request.Name == binding.File).Layout.IndicatorArea != true)
                throw new RpgCompileException(binding.DeclarationLine, "INDDS requires a display description with DDS INDARA.");
        }
    }
}
