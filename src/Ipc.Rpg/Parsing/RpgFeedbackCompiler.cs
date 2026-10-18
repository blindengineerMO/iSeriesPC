using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static (int Offset, int Width, RpgFieldKind Kind) FeedbackSubfield(string keyword, bool psds, int line)
    {
        var (offset, width, numeric) = (psds, keyword.ToUpperInvariant()) switch
        {
            (_, "*STATUS") => (10, 5, true),
            (true, "*PROC") => (0, 10, false),
            (true, "*ROUTINE") => (28, 8, false),
            (true, "*PARMS") => (36, 3, true),
            (false, "*FILE") => (0, 8, false),
            (false, "*OPCODE") => (15, 6, false),
            (false, "*ROUTINE") => (21, 8, false),
            (false, "*RECORD") => (37, 8, false),
            _ => throw new RpgCompileException(line, "Unsupported feedback subfield or options: " + keyword)
        };
        return (offset, width, numeric ? RpgFieldKind.Zoned : RpgFieldKind.Character);
    }

    private static void BindFeedbackStructures(RpgProgram program, List<WorkstationDeclaration> declarations)
    {
        for (var i = 0; i < declarations.Count; i++)
        {
            var declaration = declarations[i]; var keywords = DeclarationKeywords(declaration.Keywords, declaration.Line);
            var feedback = keywords.Where(k => k.Name == "INFDS").ToArray();
            if (feedback.Length == 0) continue;
            var name = feedback[0].Arguments?.ToUpperInvariant(); var ds = program.FindDs(name ?? "");
            if (feedback.Length != 1 || ds is not { NativeStorage: true, Anonymous: false, IsArray: false, ProgramStatus: false } ||
                program.FileInformation.Any(f => f.Structure == ds.Name))
                throw new RpgCompileException(declaration.Line, "INFDS requires a unique, named scalar native data structure for each file.");
            if (ds.ExplicitLength && ds.Length < 80) throw new RpgCompileException(ds.DeclarationLine, "An explicit INFDS LEN must be at least 80 bytes.");
            // Later native feedback areas need separate device/open contracts; do not return invented values.
            if (ds.Length > 80) throw new RpgCompileException(ds.DeclarationLine, "INFDS currently supports the 80-byte file feedback area only.");
            ds.Length = Math.Max(80, ds.Length);
            program.FileInformation.Add(new(declaration.Name, ds.Name, declaration.Line));
            declarations[i] = declaration with { Keywords = string.Join(' ', keywords.Where(k => k.Name != "INFDS")
                .Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")"))) };
        }
        foreach (var ds in program.DataStructures.Where(d => !d.ProgramStatus && d.Elements.Any(e => e.FeedbackKeyword is not null)))
            if (!program.FileInformation.Any(f => f.Structure == ds.Name))
                throw new RpgCompileException(ds.Elements.First(e => e.FeedbackKeyword is not null).DeclarationLine,
                    "Reserved file feedback subfields require an INFDS binding.");
        // LIKEDS definitions only reference earlier declarations, so propagate final INFDS lengths in source order.
        foreach (var ds in program.DataStructures.Where(d => d.LikeDataStructure is not null))
        {
            ds.Length = Math.Max(program.FindDs(ds.LikeDataStructure!)!.Length,
                program.FileInformation.Any(f => f.Structure == ds.Name) ? 80 : 0);
            if ((long)ds.Length * ds.Dimension > 1048576)
                throw new RpgCompileException(ds.DeclarationLine, "Native data structure storage exceeds 1 MiB after feedback binding.");
        }
        if (program.DataStructures.Where(d => d.NativeStorage).Sum(d => (long)d.Length * d.Dimension) > 16777216)
            throw new RpgCompileException(1, "Native feedback storage exceeds 16 MiB per program.");
    }
}
