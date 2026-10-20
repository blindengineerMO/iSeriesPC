using System.Text.Json;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static Func<RpgDiskRequest, RpgDiskLayout>? CachedDiskResolver(Func<RpgDiskRequest, RpgDiskLayout>? resolver)
    {
        if (resolver is null) return null;
        var cache = new Dictionary<string, RpgDiskLayout>(StringComparer.Ordinal);
        return request =>
        {
            var key = JsonSerializer.Serialize(request);
            if (!cache.TryGetValue(key, out var value)) cache.Add(key, value = resolver(request));
            return value;
        };
    }

    private static IReadOnlyList<RpgDiskBinding> RecordFiles(RpgProgram program)
    {
        var discovered = program.RecordLayoutsLookup?.Invoke() ?? [];
        return discovered.Concat(program.DeclarationDiskFiles).GroupBy(f => f.Request.Name).Select(g => g.Last()).ToArray();
    }

    private static RpgDataStructure RecordTemplate(RpgProgram program, string text, int line)
    {
        var parts = DeclarationArguments(text.ToUpperInvariant(), line);
        if (parts.Length is < 1 or > 3 || parts[0].Length == 0) throw new RpgCompileException(line, "LIKEREC requires a record and optional extract types.");
        var (selectedExtract, nullMap) = NullExtractTypes(parts.Skip(1).ToArray(), line);
        parts = parts.Length == 1 ? parts : [parts[0], selectedExtract];
        RpgField? parameter = null;
        if (parts[0].Split('.') is [var qualifier, _])
        {
            parameter = program.FindField(qualifier);
            if (parameter?.IsFileParameter != true) parameter = program.ForwardFileLookup?.Invoke(qualifier, line);
            // A local file definition can shadow an imported parameter. Otherwise the
            // parameter owns its qualifier, including while resolving a forward return.
            if (parameter?.IsFileParameter != true || parameter.DeclarationLine <= program.FileDeclarationLines.GetValueOrDefault(qualifier)) parameter = null;
        }
        if (parameter?.WorkstationParameter is { } display) return WorkstationRecordTemplate(program, parts, line, display, nullMap);
        var file = parameter?.FileParameter ?? RecordFiles(program).Where(f => f.Layout.Record == parts[0]).OrderByDescending(f => f.Request.Procedure == program.NativeProcedureName && f.Request.Procedure is not null).FirstOrDefault();
        if (parameter is null && file?.Request.Procedure is null && RecordWorkstations(program).FirstOrDefault(f => f.Request.Procedure is not null && f.Request.Procedure == program.NativeProcedureName && f.Layout.Records.Any(r => r.Name == parts[0])) is { } localDisplay)
            return WorkstationRecordTemplate(program, parts, line, localDisplay, nullMap);
        if (parameter is not null && file?.Layout.Record != parts[0]) throw new RpgCompileException(line, "LIKEREC record is not defined by the file parameter: " + parts[0] + ".");
        if (file is null) return WorkstationRecordTemplate(program, parts, line, nullMap: nullMap);
        var extract = parts.Length == 1 ? "*INPUT" : parts[1];
        if (extract == "*INPUT" && !file.Request.Input || extract == "*OUTPUT" && !file.Request.Output && !file.Request.Update || extract == "*KEY" && !file.Request.Keyed)
            throw new RpgCompileException(line, "LIKEREC extract type is not available for the declared record.");
        var owner = program; while (owner.GlobalProgram is { } globals) owner = globals;
        // Different procedures can reuse a parameter name and physical layout from unrelated file families.
        var key = RpgDiskBindings.FamilyKey(file) + ":" + file.Request.Name + ":" + file.Layout.Signature + ":" + extract + ":" + (parts.Length == 1) + ":" + nullMap;
        if (owner.RecordStructures.TryGetValue(key, out var cached)) return cached;
        var fields = extract == "*KEY" ? file.Layout.Fields.Where(f => f.KeySequence > 0).OrderBy(f => f.KeySequence) : file.Layout.Fields.AsEnumerable();
        var elements = new List<RpgDsElement>(); var extent = 0;
        foreach (var field in fields)
        {
            var offset = nullMap ? extract == "*KEY" ? extent : file.Layout.Fields.ToList().IndexOf(field) : extract == "*KEY" ? extent : field.Position - 1;
            elements.Add(new() { Name = field.Name, LocalName = field.Name, ExternalName = field.External, Kind = nullMap ? RpgFieldKind.Indicator : field.Kind, Length = nullMap ? 1 : field.Length,
                Decimals = nullMap ? 0 : field.Decimals, Offset = offset, StorageLength = nullMap ? 1 : field.StorageLength, Stride = nullMap ? 1 : field.StorageLength, LikeType = field, NullCapable = !nullMap && field.NullCapable && program.NullControl == "*USRCTL" });
            extent = Math.Max(extent, offset + (nullMap ? 1 : field.StorageLength));
        }
        var length = extract == "*KEY" ? extent : nullMap ? file.Layout.Fields.Count : file.Layout.RecordLength;
        if (length is < 1 or > 1048576) throw new RpgCompileException(line, "LIKEREC record layouts require 1 through 1048576 bytes.");
        var result = new RpgDataStructure { Name = "\u0001RECORD", NativeStorage = true, TypeOrigin = RecordFiles(program).Concat(program.GlobalProgram is { } module ? RecordFiles(module) : [])
                .First(f => RpgDiskBindings.FamilyKey(f) == RpgDiskBindings.FamilyKey(file) && f.Request.LikeFile is null), Qualified = true, Length = length,
            RecordOrigin = new(file.Request.Name, file.Layout.Record, file.Layout.Signature, extract, parts.Length == 1, RpgDiskBindings.FamilyKey(file), NullMap: nullMap) };
        result.Elements.AddRange(elements); owner.RecordStructures.Add(key, result); return result;
    }
}
