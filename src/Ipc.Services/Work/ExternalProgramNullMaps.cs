using System.Text.Json;
using Ipc.Core.Work;

namespace Ipc.Services.Work;

public sealed partial class ExternalProgramService
{
    private sealed record WireNullMap(bool[] Nullable, bool[] Values, bool ReadOnly);
    private static WireNullMap?[] PrepareNullMaps(int version, IReadOnlyList<object?> parameters, IReadOnlyList<ProgramArgumentNullMap?>? maps)
    {
        if (maps is not null && maps.Count != parameters.Count) throw Invalid("Null-map argument count differs from the data arguments.");
        if (version != 3 && maps?.Any(m => m is not null) == true) throw Invalid("External process null maps require null-map-aware protocol 3; no process was started.");
        long minimum = version == 3 ? parameters.Sum(value => value switch { string text => (long)text.Length, ProgramBuffer data => ((long)data.Length + 2) / 3 * 4,
            ProgramConstant constant => ((long)constant.Buffer.Length + 2) / 3 * 4, _ => 0L }) : 0;
        minimum += maps?.Sum(map => (long)(map?.Count ?? 0) * 10) ?? 0;
        if (minimum > 65536) throw Invalid("External program input exceeds 64 KiB.");
        var result = new WireNullMap?[parameters.Count];
        if (version != 3 || maps is null) return result;
        for (var index = 0; index < maps.Count; index++)
        {
            if (maps[index] is not { } map) continue;
            var nullable = new bool[map.Count]; var values = new bool[map.Count];
            for (var item = 0; item < map.Count; item++) { nullable[item] = map.IsNullable(item); values[item] = map.Read(item); }
            result[index] = new(nullable, values, map.IsReadOnly);
        }
        return result;
    }
    private static IReadOnlyList<bool[]?> ReadNullMaps(JsonElement root, WireNullMap?[] expected, int version)
    {
        if (version != 3) return [];
        if (!root.TryGetProperty("nullMaps", out var maps))
        {
            if (expected.Any(map => map is not null)) throw Invalid("Protocol 3 omitted returned null maps.");
            return expected.Select(_ => (bool[]?)null).ToArray();
        }
        if (maps.ValueKind != JsonValueKind.Array || maps.GetArrayLength() != expected.Length) throw Invalid("Returned null-map argument count differs.");
        var values = new bool[]?[expected.Length];
        for (var index = 0; index < expected.Length; index++)
        {
            var element = maps[index]; var source = expected[index];
            if (source is null)
            { if (element.ValueKind != JsonValueKind.Null) throw Invalid("An argument without a null map returned a map."); continue; }
            if (element.ValueKind != JsonValueKind.Object) throw Invalid("A mapped argument omitted its null map.");
            var properties = element.EnumerateObject().Select(p => p.Name).ToArray();
            if (properties.Length != 3 || properties.Distinct(StringComparer.Ordinal).Count() != 3 ||
                !element.TryGetProperty("nullable", out var capabilities) || !element.TryGetProperty("values", out var flags) ||
                !element.TryGetProperty("readOnly", out var readOnly) || readOnly.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || readOnly.GetBoolean() != source.ReadOnly ||
                capabilities.ValueKind != JsonValueKind.Array || flags.ValueKind != JsonValueKind.Array || capabilities.GetArrayLength() != source.Values.Length || flags.GetArrayLength() != source.Values.Length)
                throw Invalid("Returned null-map shape or mutability differs.");
            var returned = new bool[source.Values.Length];
            for (var item = 0; item < returned.Length; item++)
            {
                if (capabilities[item].ValueKind is not (JsonValueKind.True or JsonValueKind.False) || capabilities[item].GetBoolean() != source.Nullable[item] ||
                    flags[item].ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("Returned null-map capabilities or value types differ.");
                returned[item] = flags[item].GetBoolean();
                if (returned[item] && !source.Nullable[item] || source.ReadOnly && returned[item] != source.Values[item]) throw Invalid("Returned null map changed readonly or nonnullable storage.");
            }
            values[index] = returned;
        }
        return values;
    }
}
