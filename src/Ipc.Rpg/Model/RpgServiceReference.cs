using Ipc.Core.Objects;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;

namespace Ipc.Rpg.Model;

public sealed record RpgServiceReference(string Library, string Name, string Signature, IReadOnlyList<string> Exports)
{
    public string Activation { get; init; } = "*IMMED";
    public IReadOnlyList<string> DataExports { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LookupLibrary { get; init; }
    // Keep immediate-only images readable with the original version-two schema.
    internal static DefaultJsonTypeInfoResolver JsonTypes { get; } = new()
    {
        Modifiers = { info =>
        {
            if (info.Type == typeof(RpgServiceReference) || info.Type == typeof(RpgServiceImage))
                foreach (var property in info.Properties.Where(p => p.Name == "dataExports"))
                    property.ShouldSerialize = (_, value) => value is IReadOnlyList<string> { Count: > 0 };
            if (info.Type == typeof(RpgServiceReference))
                foreach (var property in info.Properties.Where(p => p.Name == "activation"))
                    property.ShouldSerialize = (_, value) => !Equals(value, "*IMMED");
        } }
    };
    public static int ImageVersion(IReadOnlyList<RpgServiceReference> references)
        => references.Count == 0 ? 1 : references.Any(r => r.DataExports.Count > 0) ? 5 : references.Any(r => r.LookupLibrary is not null) ? 4 : references.Any(r => r.Activation == "*DEFER") ? 3 : 2;
    internal static void ValidateImageVersion(int version, IReadOnlyList<RpgServiceReference> references)
    {
        ValidateList(references);
        if (version != ImageVersion(references)) throw new InvalidDataException("Invalid service-reference image version.");
    }
    public void Validate()
    {
        if (Activation is not ("*IMMED" or "*DEFER")) throw new InvalidDataException("Service activation must be *IMMED or *DEFER.");
        if (LookupLibrary is not (null or "*LIBL")) throw new InvalidDataException("Invalid service lookup library.");
        if (!ObjectName.IsValid(Library) || !ObjectName.IsValid(Name) || Library != Library.ToUpperInvariant() || Name != Name.ToUpperInvariant())
            throw new InvalidDataException("Invalid qualified service-program reference.");
        new RpgExportLevel("*CURRENT", Signature, Exports).Validate();
        ValidateDataExports(Exports, DataExports);
    }
    internal static void ValidateDataExports(IReadOnlyList<string> exports, IReadOnlyList<string> data)
    {
        if (exports is null || data is null || data.Count > 256 || data.Any(d => !exports.Contains(d, StringComparer.Ordinal)) ||
            !exports.Where(s => data.Contains(s, StringComparer.Ordinal)).SequenceEqual(data, StringComparer.Ordinal))
            throw new InvalidDataException("Invalid ordered data export table.");
    }
    public static void ValidateList(IReadOnlyList<RpgServiceReference> references)
    {
        if (references is null || references.Count > 16 || references.Any(r => r is null) ||
            references.Select(r => r.Library + "/" + r.Name).Distinct(StringComparer.Ordinal).Count() != references.Count)
            throw new InvalidDataException("Binding accepts at most 16 distinct service programs.");
        foreach (var reference in references) reference.Validate();
    }
}
public sealed record RpgServiceImport(RpgServiceReference Service, int Ordinal, string Symbol);

public sealed partial record RpgServiceDefinition
{
    public RpgServiceReference Reference(string library, string name) => new(library, name, Levels[0].Signature, Levels[0].Exports.ToArray())
        { DataExports = Levels[0].Exports.Where(Data.ContainsKey).ToArray() };
    public RpgDataExport ResolveData(RpgServiceImport import)
    {
        import.Service.Validate(); var level = Levels.FirstOrDefault(l => l.Signature == import.Service.Signature);
        if (level is null || !level.Exports.SequenceEqual(import.Service.Exports, StringComparer.Ordinal) ||
            !level.Exports.Where(Data.ContainsKey).SequenceEqual(import.Service.DataExports, StringComparer.Ordinal) ||
            import.Ordinal < 0 || import.Ordinal >= level.Exports.Count || level.Exports[import.Ordinal] != import.Symbol || !Data.TryGetValue(import.Symbol, out var data))
            throw new InvalidDataException("Service data signature or export position mismatch: " + import.Service.Library + "/" + import.Service.Name);
        return data;
    }
    public RpgSubprocedure Resolve(RpgServiceImport import)
    {
        import.Service.Validate();
        var level = Levels.FirstOrDefault(l => l.Signature == import.Service.Signature);
        if (level is null || !level.Exports.SequenceEqual(import.Service.Exports, StringComparer.Ordinal) || !level.Exports.Where(Data.ContainsKey).SequenceEqual(import.Service.DataExports, StringComparer.Ordinal) ||
            import.Ordinal < 0 || import.Ordinal >= level.Exports.Count || level.Exports[import.Ordinal] != import.Symbol || !Procedures.TryGetValue(import.Symbol, out var procedure))
            throw new InvalidDataException("Service-program signature or export position mismatch: " + import.Service.Library + "/" + import.Service.Name);
        return procedure;
    }
}
