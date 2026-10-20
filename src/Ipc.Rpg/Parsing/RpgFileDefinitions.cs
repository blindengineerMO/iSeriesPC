using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static string FileKeywords(IEnumerable<DeclarationKeyword> options) => string.Join(' ', options.Select(k =>
        k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")")));

    private static void HideImportedFiles(RpgProgram program, IReadOnlyList<WorkstationDeclaration> declarations)
    {
        if (!program.IsNativeProcedure) return;
        var names = declarations.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        program.Fields.RemoveAll(f => program.GlobalFields.Contains(f) && f.IsFileParameter && names.Contains(f.Name));
        program.DiskFiles.RemoveAll(f => f.Request.Procedure is null && names.Contains(f.Request.Name));
        program.DeclarationDiskFiles.RemoveAll(f => f.Request.Procedure is null && names.Contains(f.Request.Name));
        program.Workstations.RemoveAll(f => f.Request.Procedure is null && names.Contains(f.Request.Name));
        program.DeclarationWorkstations.RemoveAll(f => f.Request.Procedure is null && names.Contains(f.Request.Name));
        program.IndicatorBindings.RemoveAll(f => names.Contains(f.File) && program.GlobalProgram?.IndicatorBindings.Contains(f) == true);
    }

    private static List<WorkstationDeclaration> ExpandFileDeclarations(List<WorkstationDeclaration> declarations, RpgProgram program)
    {
        var result = new List<WorkstationDeclaration>();
        var definitions = new Dictionary<string, IReadOnlyList<DeclarationKeyword>>(StringComparer.Ordinal);
        var imported = new List<WorkstationDeclaration>();
        if (program.GlobalProgram is { } globals)
            foreach (var binding in RecordFiles(globals).Where(f => !f.Request.IsParameter))
            {
                var r = binding.Request;
                var options = new List<DeclarationKeyword> { new("DISK", null), new("EXTDESC", "'" + r.DescriptionFile + "'"),
                    new("EXTFILE", r.RuntimeFile == "*EXTDESC" ? "*EXTDESC" : "'" + r.RuntimeFile + "'") };
                if (r.Recno is not null) options.Add(new("RECNO", r.Recno));
                if (r.Keyed) options.Add(new("KEYED", null));
                options.Add(new("USAGE", string.Join(':', new[] { r.Input ? "*INPUT" : null, r.Output ? "*OUTPUT" : null, r.Update ? "*UPDATE" : null, r.Delete ? "*DELETE" : null }.OfType<string>())));
                if (r.Prefix.Length > 0) options.Add(new("PREFIX", r.Prefix));
                foreach (var rename in r.Renames) options.Add(new("RENAME", rename.External + ":" + rename.Name));
                if (globals.FileDefinitionKeywords.TryGetValue(r.Name, out var declared)) options = DeclarationKeywords(declared, globals.FileDeclarationLines.GetValueOrDefault(r.Name));
                if (globals.UnblockedFiles.Contains(r.Name)) options.Add(new("BLOCK", "*NO"));
                imported.Add(new(globals.FileDeclarationLines.GetValueOrDefault(r.Name), r.Name, true, false, FileKeywords(options), true,
                    r.IsTemplate, r.Qualified, r.LikeFile, Unblocked: globals.UnblockedFiles.Contains(r.Name)));
                definitions[r.Name] = options;
            }
        if (program.GlobalProgram is { } displayGlobals)
            foreach (var binding in RecordWorkstations(displayGlobals).Where(f => !f.Request.IsParameter))
            {
                var r = binding.Request;
                var options = DeclarationKeywords(displayGlobals.FileDefinitionKeywords[r.Name], displayGlobals.FileDeclarationLines.GetValueOrDefault(r.Name));
                imported.Add(new(displayGlobals.FileDeclarationLines.GetValueOrDefault(r.Name), r.Name, r.Input, r.Output, FileKeywords(options), false,
                    r.IsTemplate, r.Qualified, r.LikeFile));
                definitions[r.Name] = options;
            }
        foreach (var original in declarations)
        {
            var declaration = original with { Procedure = program.NativeProcedureName };
            var options = DeclarationKeywords(declaration.Keywords, declaration.Line);
            var displayParent = options.FirstOrDefault(k => k.Name == "LIKEFILE")?.Arguments?.ToUpperInvariant();
            var display = displayParent is null ? null : result.SingleOrDefault(d => d.Name == displayParent && !d.Disk) ?? imported.SingleOrDefault(d => d.Name == displayParent && !d.Disk);
            if (!declaration.Disk || display is not null)
            {
                if (displayParent is not null && display is null)
                    throw new RpgCompileException(declaration.Line, "LIKEFILE requires an earlier file definition of the same device.");
                var expandedDisplay = ExpandWorkstationDefinition(declaration, options, display, display is null ? null : definitions[display.Name]);
                result.Add(expandedDisplay); definitions[declaration.Name] = DeclarationKeywords(expandedDisplay.Keywords, declaration.Line); continue;
            }
            if (options.GroupBy(k => k.Name).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
                throw new RpgCompileException(declaration.Line, "Duplicate " + duplicate.Key + " file keyword.");
            var isStatic = options.Any(k => k.Name == "STATIC");
            if (isStatic && (!program.IsNativeProcedure || options.Any(k => k.Name == "STATIC" && k.Arguments is not null)))
                throw new RpgCompileException(declaration.Line, "File STATIC requires a subprocedure and no arguments.");
            declaration = declaration with { Static = isStatic };
            var like = options.SingleOrDefault(k => k.Name == "LIKEFILE");
            var template = options.Any(k => k.Name == "TEMPLATE"); var qualified = options.Any(k => k.Name == "QUALIFIED");
            if (options.Where(k => k.Name is "LIKEFILE" or "TEMPLATE" or "QUALIFIED" or "BLOCK").GroupBy(k => k.Name).Any(g => g.Count() != 1) ||
                options.Any(k => k.Name is "TEMPLATE" or "QUALIFIED" && k.Arguments is not null))
                throw new RpgCompileException(declaration.Line, "Duplicate or invalid file TEMPLATE/QUALIFIED/LIKEFILE/BLOCK keyword.");
            if (declaration.IncompleteFile && like is null) throw new RpgCompileException(declaration.Line, "Blank F-spec file attributes require LIKEFILE.");
            if (template && options.Any(k => k.Name is "USROPN" or "INFDS" or "INFSR" or "STATIC"))
                throw new RpgCompileException(declaration.Line, "TEMPLATE files cannot specify non-inherited USROPN/INFDS/INFSR settings.");
            if (options.SingleOrDefault(k => k.Name == "BLOCK") is { } block && block.Arguments?.ToUpperInvariant() != "*NO")
                throw new RpgCompileException(declaration.Line, "The supported DISK blocking declaration is BLOCK(*NO).");
            if (like is not null)
            {
                var parentName = like.Arguments?.ToUpperInvariant() ?? "";
                var parent = result.SingleOrDefault(d => d.Name == parentName) ?? imported.SingleOrDefault(d => d.Name == parentName);
                if (parent is null || !parent.Disk) throw new RpgCompileException(declaration.Line, "LIKEFILE requires an earlier DISK file definition.");
                if (qualified || options.Any(k => k.Name is not ("LIKEFILE" or "TEMPLATE" or "USROPN" or "EXTFILE" or "INFDS" or "INFSR" or "STATIC" or "RECNO")))
                    throw new RpgCompileException(declaration.Line, "LIKEFILE cannot override inherited file attributes or explicitly specify QUALIFIED.");
                var inherited = definitions[parentName].Where(k => k.Name is not ("TEMPLATE" or "QUALIFIED" or "USROPN" or "INFDS" or "INFSR" or "STATIC" or "RECNO")).ToList();
                if (parent.Unblocked && !inherited.Any(k => k.Name == "BLOCK")) inherited.Add(new("BLOCK", "*NO"));
                if (declaration.Name == parentName) throw new RpgCompileException(declaration.Line, "A local LIKEFILE cannot shadow its own parent definition.");
                var request = DiskRequest(parent with { Keywords = FileKeywords(DeclarationKeywords(parent.Keywords, parent.Line).Where(k => k.Name is not ("BLOCK" or "STATIC"))) });
                if (request.Input && !request.Update && !request.Output && !inherited.Any(k => k.Name == "BLOCK"))
                    throw new RpgCompileException(declaration.Line, "An input LIKEFILE parent requires explicit BLOCK(*NO).");
                if (!inherited.Any(k => k.Name == "EXTDESC")) inherited.Add(new("EXTDESC", "'" + request.DescriptionFile + "'"));
                if (options.Any(k => k.Name == "EXTFILE")) inherited.RemoveAll(k => k.Name == "EXTFILE");
                inherited.AddRange(options.Where(k => k.Name is not ("LIKEFILE" or "TEMPLATE")));
                options = inherited; qualified = true;
                result.Add(declaration with { Keywords = FileKeywords(options.Where(k => k.Name is not ("BLOCK" or "STATIC"))), Template = template, Qualified = true, LikeFile = parentName, Unblocked = parent.Unblocked });
            }
            else result.Add(declaration with { Keywords = FileKeywords(options.Where(k => k.Name is not ("TEMPLATE" or "QUALIFIED" or "BLOCK" or "STATIC"))), Template = template, Qualified = qualified, Unblocked = options.Any(k => k.Name is "BLOCK" or "RECNO") || options.Any(k => k.Name == "USAGE" && (k.Arguments?.ToUpperInvariant().Contains("*OUTPUT") == true || k.Arguments?.ToUpperInvariant().Contains("*UPDATE") == true || k.Arguments?.ToUpperInvariant().Contains("*DELETE") == true)) });
            definitions[declaration.Name] = options.Where(k => k.Name is not ("TEMPLATE" or "QUALIFIED")).ToArray();
        }
        return result;
    }
}
