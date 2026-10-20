using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    public static IReadOnlyList<RpgProgram> ProgramModules(RpgProgram program) => program.BoundModules.Count > 0 ? program.BoundModules : [program];
    public static IReadOnlyList<RpgServiceImport> ServiceDataImports(IReadOnlyList<RpgProgram> modules, RpgServiceReference reference)
        => modules.SelectMany(m => m.BoundData.Values).Select(d => d.Service).Where(d => d is not null && ReferenceEquals(d.Service, reference)).Select(d => d!).ToArray();
    private static List<DeclarationKeyword> DataBindingKeywords(RpgProgram program, string name, List<DeclarationKeyword> keywords, int line, bool storage)
    {
        var bindings = keywords.Where(k => k.Name is "IMPORT" or "EXPORT").ToArray();
        if (bindings.Length == 0) return keywords;
        if (bindings.Length != 1 || program.IsNativeProcedure || !storage || name.StartsWith('\u0001') || name == "*N" ||
            keywords.Any(k => k.Name is "TEMPLATE" or "STATIC"))
            throw new RpgCompileException(line, "IMPORT/EXPORT requires one named global storage definition.");
        var binding = bindings[0]; var import = binding.Name == "IMPORT";
        if (import && keywords.Any(k => k.Name is "INZ" or "CTDATA" or "PERRCD" or "EXTFMT" or "ALT"))
            throw new RpgCompileException(line, "An imported variable cannot have initialization or compile-time data.");
        var symbol = name.ToUpperInvariant();
        if (binding.Arguments is { } argument)
        {
            if (argument.Equals("*DCLCASE", StringComparison.OrdinalIgnoreCase))
            {
                var original = program.Source?.Text.Replace("\r", "", StringComparison.Ordinal).Split('\n').ElementAtOrDefault(line - 1) ?? "";
                var declaration = Regex.Match(original, @"\bDCL-(?:S|DS)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase);
                if (!declaration.Success) throw new RpgCompileException(line, "*DCLCASE requires a free data declaration.");
                symbol = declaration.Groups[1].Value;
            }
            else
            {
                if (DeclarationValue(program, argument, line) is not string value) throw new RpgCompileException(line, "A data external name requires a character constant.");
                symbol = value;
            }
        }
        if (!RpgExportLevel.SymbolName(symbol) || program.DataDefinitions.Any(d => d.Symbol == symbol))
            throw new RpgCompileException(line, "Invalid or duplicate external data symbol: " + symbol);
        program.DataDefinitions.Add(new(name, symbol, import, line));
        return keywords.Where(k => k.Name is not ("IMPORT" or "EXPORT")).ToList();
    }
    private static void ValidateDataDefinitions(RpgProgram program)
    {
        foreach (var definition in program.DataDefinitions)
        {
            if (program.FindField(definition.Name)?.BasedOn is not null || program.FindDs(definition.Name)?.BasedOn is not null)
                throw new RpgCompileException(definition.Line, "A based variable cannot import or export storage.");
            if (definition.Import && !program.IsModule) throw new RpgCompileException(definition.Line, "Imported data requires module binding.");
            if (program.Subprocedures.Any(p => p.IsExport && p.Name == definition.Symbol) ||
                program.Prototypes.Concat(program.Subprocedures.Where(p => p.NativeBody is not null).SelectMany(p => p.NativeBody!.Prototypes)).Any(p => p.NativeProcedure && p.PointerTarget is null && (p.ExternalName ?? p.Name) == definition.Symbol))
                throw new RpgCompileException(definition.Line, "An external data symbol cannot also name a procedure: " + definition.Symbol);
            if (RpgInterpreter.EntryParameterNames(program).Contains(definition.Name, StringComparer.OrdinalIgnoreCase))
                throw new RpgCompileException(definition.Line, "Imported/exported data cannot be an entry parameter.");
            if (definition.Import && program.FindDs(definition.Name) is { } ds && DataElements(ds).Any(e => e.HasInitialValue))
                throw new RpgCompileException(definition.Line, "Imported structure subfields cannot be initialized.");
        }
    }
    private static IEnumerable<RpgDsElement> DataElements(RpgDataStructure structure)
        => structure.Elements.SelectMany(e => e.NestedStructure is { } nested ? DataElements(nested) : [e]);
    public static IReadOnlyDictionary<string, RpgDataExport> ModuleDataExports(IReadOnlyList<RpgProgram> modules)
    {
        var result = new Dictionary<string, RpgDataExport>(StringComparer.Ordinal); var procedures = ModuleExports(modules);
        foreach (var module in modules)
            foreach (var definition in module.DataDefinitions.Where(d => !d.Import))
                if (procedures.ContainsKey(definition.Symbol) || !result.TryAdd(definition.Symbol, new(module, definition)))
                    throw LinkError(module, definition.Line, "Duplicate external symbol: " + definition.Symbol);
        return result;
    }
    public static bool DataCompatible(RpgProgram importer, RpgDataDefinition import, RpgDataExport export)
    {
        var left = importer.FindDs(import.Name); var right = export.Module.FindDs(export.Definition.Name);
        if (left is not null || right is not null)
            return left is not null && right is not null && left.IsArray == right.IsArray && left.Dimension == right.Dimension &&
                left.IsMultipleOccurrence == right.IsMultipleOccurrence && left.StorageCount == right.StorageCount && SameStructureLayout(left, right) &&
                DataElements(left).Select(e => e.NullCapable).SequenceEqual(DataElements(right).Select(e => e.NullCapable));
        var a = importer.FindField(import.Name); var b = export.Module.FindField(export.Definition.Name);
        return a is not null && b is not null && a.Kind == b.Kind && a.Length == b.Length && a.Decimals == b.Decimals &&
            a.IsArray == b.IsArray && a.IsTable == b.IsTable && a.Dimension == b.Dimension && a.OpaqueBytes == b.OpaqueBytes && a.NullCapable == b.NullCapable;
    }
}
