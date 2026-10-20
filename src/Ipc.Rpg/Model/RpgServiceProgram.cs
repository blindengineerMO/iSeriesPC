using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Core.Compilation;
using Ipc.Core.Objects;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Model;

public sealed record RpgExportLevel(string Level, string Signature, IReadOnlyList<string> Exports)
{
    internal static bool SymbolName(string? value) => value is not null && RpgWorkstationBindings.Name(value.ToUpperInvariant());
    internal void Validate()
    {
        if (Level is not ("*CURRENT" or "*PRV") || Signature is null || Signature.Length != 32 ||
            Signature.Any(c => !Uri.IsHexDigit(c)) || Signature != Signature.ToUpperInvariant() || Exports is null ||
            Exports.Count is < 1 or > 256 || Exports.Any(e => !SymbolName(e)) || Exports.Distinct(StringComparer.Ordinal).Count() != Exports.Count)
            throw new InvalidDataException("Invalid service-program export level.");
    }
}

/// <summary>Captured modules and binder definitions; this format does not activate or execute a service.</summary>
public sealed record RpgServiceImage(int Version, IReadOnlyList<RpgModuleImage> Modules, IReadOnlyList<RpgExportLevel> Levels, SourceDocument? BinderSource)
{
    public const string Attribute = "RPGSERVICE";
    public IReadOnlyList<RpgServiceReference> Services { get; init; } = [];
    public IReadOnlyList<string> DataExports { get; init; } = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16, TypeInfoResolver = RpgServiceReference.JsonTypes };
    public string Serialize()
    {
        Validate(); var text = JsonSerializer.Serialize(this, Json);
        if (Encoding.UTF8.GetByteCount(text) > 33554432) throw new InvalidDataException("Service image exceeds 32 MiB."); return text;
    }
    private void Validate()
    {
        if (Services is null || Modules is null || Modules.Count is < 1 or > 16 ||
            Modules.Any(m => m is null || !ObjectName.IsValid(m.Library) || !ObjectName.IsValid(m.Name) || m.Library != m.Library.ToUpperInvariant() || m.Name != m.Name.ToUpperInvariant()) ||
            Levels is null || Levels.Count is < 1 or > 16 || Levels.Any(l => l is null) || Levels[0].Level != "*CURRENT" ||
            Levels.Skip(1).Any(l => l.Level != "*PRV") || Levels.Select(l => l.Signature).Distinct().Count() != Levels.Count)
            throw new InvalidDataException("Invalid service-program image.");
        RpgServiceReference.ValidateList(Services);
        RpgServiceReference.ValidateDataExports(Levels[0].Exports, DataExports);
        if (Version != (DataExports.Count > 0 ? 5 : RpgServiceReference.ImageVersion(Services))) throw new InvalidDataException("Invalid service image version.");
        // Apply the same captured source and metadata budgets as static programs, without requiring a main entry.
        _ = new RpgBoundProgram(1, Modules[0].Library + "/" + Modules[0].Name, Modules).Serialize();
        foreach (var level in Levels)
        {
            level.Validate();
            if (!Levels[0].Exports.Take(level.Exports.Count).SequenceEqual(level.Exports, StringComparer.Ordinal)) throw new InvalidDataException("Inconsistent previous export positions.");
        }
        if (BinderSource is not null && (string.IsNullOrWhiteSpace(BinderSource.Identity) || BinderSource.Identity.Length > 1024 || BinderSource.Identity.Any(char.IsControl) ||
            BinderSource.Text is null || Encoding.UTF8.GetByteCount(BinderSource.Text) > 1048576))
            throw new InvalidDataException("Invalid saved binder source.");
    }
    public static RpgServiceImage Deserialize(string text)
    {
        if (text.Length > 33554432 || Encoding.UTF8.GetByteCount(text) > 33554432) throw new InvalidDataException("Service image exceeds 32 MiB.");
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            CheckArray(doc.RootElement, "modules", 16); CheckArray(doc.RootElement, "levels", 16);
            if (doc.RootElement.TryGetProperty("dataExports", out var data) && (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 256)) throw new InvalidDataException("Invalid service data export array.");
            foreach (var level in doc.RootElement.GetProperty("levels").EnumerateArray()) CheckArray(level, "exports", 256);
            if (doc.RootElement.TryGetProperty("services", out var services))
            {
                if (services.ValueKind != JsonValueKind.Array || services.GetArrayLength() > 16) throw new InvalidDataException("Invalid service dependency array.");
                foreach (var service in services.EnumerateArray())
                {
                    CheckArray(service, "exports", 256);
                    if (service.TryGetProperty("dataExports", out var names) && (names.ValueKind != JsonValueKind.Array || names.GetArrayLength() > 256)) throw new InvalidDataException("Invalid service data reference array.");
                }
            }
            Unique(doc.RootElement);
            var result = JsonSerializer.Deserialize<RpgServiceImage>(text, Json) ?? throw new InvalidDataException("Missing service image."); result.Validate(); return result;
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid service image.", error); }
    }
    public RpgServiceDefinition Restore(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Validate();
        var modules = Modules.Select(m => m.Restore(cancellationToken)).ToArray();
        var dependencies = RpgCompiler.BindServiceModules(modules, cancellationToken, Services);
        if (!dependencies.SequenceEqual(Services)) throw new InvalidDataException("Saved service dependencies include unused references.");
        var available = RpgCompiler.ModuleExports(modules); var data = RpgCompiler.ModuleDataExports(modules);
        var names = available.Keys.Concat(data.Keys).ToArray();
        var levels = BinderSource is null ? new[] { RpgCompiler.GeneratedExports(names) } : RpgCompiler.ParseBinderSource(BinderSource, cancellationToken);
        if (JsonSerializer.Serialize(levels, Json) != JsonSerializer.Serialize(Levels, Json) || Levels[0].Exports.Any(name => !available.ContainsKey(name) && !data.ContainsKey(name)) ||
            !Levels[0].Exports.Where(data.ContainsKey).SequenceEqual(DataExports, StringComparer.Ordinal))
            throw new InvalidDataException("Service exports differ from their saved definitions or module exports.");
        return new(modules, Levels, Levels[0].Exports.Where(available.ContainsKey).ToDictionary(name => name, name => available[name], StringComparer.Ordinal))
            { ServiceReferences = dependencies, Data = DataExports.ToDictionary(name => name, name => data[name], StringComparer.Ordinal) };
    }
    private static void CheckArray(JsonElement root, string name, int maximum)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < 1 || array.GetArrayLength() > maximum)
            throw new InvalidDataException("Invalid service image array: " + name);
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate service image property."); Unique(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
}

public sealed partial record RpgServiceDefinition(IReadOnlyList<RpgProgram> Modules, IReadOnlyList<RpgExportLevel> Levels, IReadOnlyDictionary<string, RpgSubprocedure> Procedures)
{
    public IReadOnlyList<RpgServiceReference> ServiceReferences { get; init; } = [];
    public IReadOnlyDictionary<string, RpgDataExport> Data { get; init; } = new Dictionary<string, RpgDataExport>(StringComparer.Ordinal);
}
