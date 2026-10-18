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

    private static RpgDataStructure RecordTemplate(RpgProgram program, string text, int line)
    {
        var parts = DeclarationArguments(text.ToUpperInvariant(), line);
        if (parts.Length is < 1 or > 2 || parts[0].Length == 0 || parts.Length == 2 && parts[1] is not ("*INPUT" or "*OUTPUT" or "*ALL" or "*KEY"))
            throw new RpgCompileException(line, "LIKEREC requires a DISK record and optional *INPUT, *OUTPUT, *ALL or *KEY.");
        var file = (program.RecordLayoutsLookup?.Invoke() ?? program.DeclarationDiskFiles).SingleOrDefault(f => f.Layout.Record == parts[0]);
        if (file is null) throw new RpgCompileException(line, "LIKEREC requires a declared externally described DISK record: " + parts[0] + ".");
        var extract = parts.Length == 1 ? "*INPUT" : parts[1];
        if (extract == "*INPUT" && !file.Request.Input || extract == "*OUTPUT" && !file.Request.Output && !file.Request.Update || extract == "*KEY" && !file.Request.Keyed)
            throw new RpgCompileException(line, "LIKEREC extract type is not available for the declared record.");
        var owner = program; while (owner.GlobalProgram is { } globals) owner = globals;
        var key = file.Request.Name + ":" + file.Layout.Signature + ":" + extract + ":" + (parts.Length == 1);
        if (owner.RecordStructures.TryGetValue(key, out var cached)) return cached;
        var fields = extract == "*KEY" ? file.Layout.Fields.Where(f => f.KeySequence > 0).OrderBy(f => f.KeySequence) : file.Layout.Fields.AsEnumerable();
        var elements = new List<RpgDsElement>(); var extent = 0;
        foreach (var field in fields)
        {
            var offset = extract == "*KEY" ? extent : field.Position - 1;
            elements.Add(new() { Name = field.Name, LocalName = field.Name, Kind = field.Kind, Length = field.Length,
                Decimals = field.Decimals, Offset = offset, StorageLength = field.StorageLength, Stride = field.StorageLength, LikeType = field });
            extent = Math.Max(extent, offset + field.StorageLength);
        }
        var length = extract == "*KEY" ? extent : file.Layout.RecordLength;
        if (length is < 1 or > 1048576) throw new RpgCompileException(line, "LIKEREC record layouts require 1 through 1048576 bytes.");
        var result = new RpgDataStructure { Name = "\u0001RECORD", NativeStorage = true, TypeOrigin = (program.RecordLayoutsLookup?.Invoke() ?? program.DeclarationDiskFiles).First(f => RpgDiskBindings.FamilyKey(f) == RpgDiskBindings.FamilyKey(file) && f.Request.LikeFile is null), Qualified = true, Length = length,
            RecordOrigin = new(file.Request.Name, file.Layout.Record, file.Layout.Signature, extract, parts.Length == 1, RpgDiskBindings.FamilyKey(file)) };
        result.Elements.AddRange(elements); owner.RecordStructures.Add(key, result); return result;
    }
}
