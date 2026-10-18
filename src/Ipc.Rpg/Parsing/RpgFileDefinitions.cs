using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static string FileKeywords(IEnumerable<DeclarationKeyword> options) => string.Join(' ', options.Select(k =>
        k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")")));

    private static List<WorkstationDeclaration> ExpandDiskDeclarations(List<WorkstationDeclaration> declarations)
    {
        var result = new List<WorkstationDeclaration>();
        var definitions = new Dictionary<string, IReadOnlyList<DeclarationKeyword>>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            var options = DeclarationKeywords(declaration.Keywords, declaration.Line);
            if (!declaration.Disk && !options.Any(k => k.Name is "LIKEFILE" or "TEMPLATE" or "QUALIFIED" or "BLOCK"))
            { result.Add(declaration); definitions.Add(declaration.Name, options); continue; }
            if (options.GroupBy(k => k.Name).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
                throw new RpgCompileException(declaration.Line, "Duplicate " + duplicate.Key + " file keyword.");
            var like = options.SingleOrDefault(k => k.Name == "LIKEFILE");
            var template = options.Any(k => k.Name == "TEMPLATE"); var qualified = options.Any(k => k.Name == "QUALIFIED");
            if (options.Where(k => k.Name is "LIKEFILE" or "TEMPLATE" or "QUALIFIED" or "BLOCK").GroupBy(k => k.Name).Any(g => g.Count() != 1) ||
                options.Any(k => k.Name is "TEMPLATE" or "QUALIFIED" && k.Arguments is not null))
                throw new RpgCompileException(declaration.Line, "Duplicate or invalid file TEMPLATE/QUALIFIED/LIKEFILE/BLOCK keyword.");
            if (declaration.IncompleteFile && like is null) throw new RpgCompileException(declaration.Line, "Blank F-spec file attributes require LIKEFILE.");
            if (!declaration.Disk && (template || like is not null || qualified || options.Any(k => k.Name == "BLOCK"))) throw new RpgCompileException(declaration.Line, "WORKSTN file TEMPLATE/LIKEFILE requires unsupported result-structure I/O.");
            if (template && options.Any(k => k.Name is "USROPN" or "INFDS" or "INFSR"))
                throw new RpgCompileException(declaration.Line, "TEMPLATE files cannot specify non-inherited USROPN/INFDS/INFSR settings.");
            if (options.SingleOrDefault(k => k.Name == "BLOCK") is { } block && block.Arguments?.ToUpperInvariant() != "*NO")
                throw new RpgCompileException(declaration.Line, "The supported DISK blocking declaration is BLOCK(*NO).");
            if (like is not null)
            {
                var parentName = like.Arguments?.ToUpperInvariant() ?? "";
                var parent = result.SingleOrDefault(d => d.Name == parentName);
                if (parent is null || !parent.Disk) throw new RpgCompileException(declaration.Line, "LIKEFILE requires an earlier DISK file definition.");
                if (qualified || options.Any(k => k.Name is not ("LIKEFILE" or "TEMPLATE" or "USROPN" or "EXTFILE" or "INFDS" or "INFSR")))
                    throw new RpgCompileException(declaration.Line, "LIKEFILE cannot override inherited file attributes or explicitly specify QUALIFIED.");
                var inherited = definitions[parentName].Where(k => k.Name is not ("TEMPLATE" or "QUALIFIED" or "USROPN" or "INFDS" or "INFSR")).ToList();
                var request = DiskRequest(parent);
                if (request.Input && !request.Update && !request.Output && !inherited.Any(k => k.Name == "BLOCK"))
                    throw new RpgCompileException(declaration.Line, "An input LIKEFILE parent requires explicit BLOCK(*NO).");
                if (!inherited.Any(k => k.Name == "EXTDESC")) inherited.Add(new("EXTDESC", "'" + request.DescriptionFile + "'"));
                if (options.Any(k => k.Name == "EXTFILE")) inherited.RemoveAll(k => k.Name == "EXTFILE");
                inherited.AddRange(options.Where(k => k.Name is not ("LIKEFILE" or "TEMPLATE")));
                options = inherited; qualified = true;
                result.Add(declaration with { Keywords = FileKeywords(options.Where(k => k.Name != "BLOCK")), Template = template, Qualified = true, LikeFile = parentName });
            }
            else result.Add(declaration with { Keywords = FileKeywords(options.Where(k => k.Name is not ("TEMPLATE" or "QUALIFIED" or "BLOCK"))), Template = template, Qualified = qualified });
            definitions.Add(declaration.Name, options.Where(k => k.Name is not ("TEMPLATE" or "QUALIFIED")).ToArray());
        }
        return result;
    }
}
