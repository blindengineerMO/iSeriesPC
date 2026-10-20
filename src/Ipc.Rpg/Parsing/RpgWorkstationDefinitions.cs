using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static WorkstationDeclaration ExpandWorkstationDefinition(WorkstationDeclaration declaration,
        List<DeclarationKeyword> options, WorkstationDeclaration? parent, IReadOnlyList<DeclarationKeyword>? parentOptions)
    {
        var template = options.Any(k => k.Name == "TEMPLATE");
        var qualified = options.Any(k => k.Name == "QUALIFIED");
        if (options.Where(k => k.Name is not ("SFILE" or "RENAME")).GroupBy(k => k.Name).Any(g => g.Count() > 1) ||
            options.Any(k => k.Name is "TEMPLATE" or "QUALIFIED" && k.Arguments is not null))
            throw new RpgCompileException(declaration.Line, "Duplicate or invalid WORKSTN file keyword.");
        var isStatic = options.Any(k => k.Name == "STATIC");
        if (options.Any(k => k.Name == "BLOCK")) throw new RpgCompileException(declaration.Line, "WORKSTN BLOCK remains unsupported.");
        if (isStatic && (declaration.Procedure is null || options.Any(k => k.Name == "STATIC" && k.Arguments is not null)))
            throw new RpgCompileException(declaration.Line, "File STATIC requires a subprocedure and no arguments.");
        if (template && options.Any(k => k.Name is "USROPN" or "INDDS" or "INFDS" or "INFSR" or "STATIC"))
            throw new RpgCompileException(declaration.Line, "TEMPLATE files cannot specify non-inherited USROPN/INDDS/INFDS/INFSR settings.");
        if (parent is not null)
        {
            if (declaration.Name == parent.Name) throw new RpgCompileException(declaration.Line, "A local LIKEFILE cannot shadow its own parent definition.");
            if (qualified || options.Any(k => k.Name is not ("LIKEFILE" or "TEMPLATE" or "USROPN" or "EXTFILE" or "INDDS" or "INFDS" or "INFSR" or "SFILE" or "STATIC")))
                throw new RpgCompileException(declaration.Line, "LIKEFILE cannot override inherited WORKSTN attributes or explicitly specify QUALIFIED.");
            var inherited = parentOptions!.Where(k => k.Name is not ("LIKEFILE" or "TEMPLATE" or "QUALIFIED" or "USROPN" or "INDDS" or "INFDS" or "INFSR" or "SFILE" or "STATIC")).ToList();
            if (!inherited.Any(k => k.Name == "EXTDESC")) inherited.Add(new("EXTDESC", "'" + parent.Name + "'"));
            if (!inherited.Any(k => k.Name == "USAGE")) inherited.Add(new("USAGE", parent.Input ? parent.Output ? "*INPUT:*OUTPUT" : "*INPUT" : "*OUTPUT"));
            if (options.Any(k => k.Name == "EXTFILE")) inherited.RemoveAll(k => k.Name == "EXTFILE");
            inherited.AddRange(options.Where(k => k.Name is not ("LIKEFILE" or "TEMPLATE")));
            options = inherited; qualified = true;
        }
        return declaration with { Disk = false, Input = parent?.Input ?? declaration.Input, Output = parent?.Output ?? declaration.Output,
            Keywords = FileKeywords(options.Where(k => k.Name is not ("TEMPLATE" or "QUALIFIED" or "STATIC"))),
            Template = template, Qualified = qualified, LikeFile = parent?.Name, Static = isStatic };
    }
}
