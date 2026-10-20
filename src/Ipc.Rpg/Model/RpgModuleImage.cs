using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Core.Compilation;
using Ipc.Core.Objects;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Model;

public sealed record RpgModuleImage(string Library, string Name, string Source, string SourceMap, string Disk, string Workstation, string External)
{
    public const string VersionAttribute = "ipc.rpg.moduleVersion";
    public static RpgModuleImage Capture(ObjectDescriptor descriptor)
    {
        var attrs = descriptor.ExtendedAttributes;
        if (descriptor.ObjectType != ObjectType.Module || descriptor.Attribute != "RPGLE" || attrs?.GetValueOrDefault(VersionAttribute) != "1" || descriptor.Source is null)
            throw new InvalidDataException("Invalid source-bound RPG module.");
        string Required(string key) => attrs.GetValueOrDefault(key) ?? throw new InvalidDataException("Missing compiled module metadata: " + key);
        return new(descriptor.Library, descriptor.Name, descriptor.Source, Required(CompiledSourceMap.AttributeName), Required(RpgDiskBindings.AttributeName),
            Required(RpgWorkstationBindings.AttributeName), Required(RpgExternalBindings.AttributeName));
    }
    public static RpgModuleImage Capture(RpgProgram program)
    {
        if (!program.IsModule || program.Source is null) throw new InvalidDataException("An RPG module with captured source is required.");
        return new(program.Library, program.Name, program.Source.Text, CompiledSourceMap.Serialize(program.Source), RpgDiskBindings.Serialize(program),
            RpgWorkstationBindings.Serialize(program), RpgExternalBindings.Serialize(program));
    }
    public RpgProgram Restore(CancellationToken cancellationToken = default)
    {
        if (!ObjectName.IsValid(Library) || !ObjectName.IsValid(Name) || Library != Library.ToUpperInvariant() || Name != Name.ToUpperInvariant() || Source is null || SourceMap is null || Disk is null || Workstation is null || External is null ||
            Source.Length > 4194304 || Encoding.UTF8.GetByteCount(Source) > 4194304 || SourceMap.Length > 16777216 || Disk.Length > 1048576 || Workstation.Length > 1048576 || External.Length > 1048576)
            throw new InvalidDataException("Invalid or oversized compiled module image.");
        var source = CompiledSourceMap.Restore(Source, SourceMap); var disk = RpgDiskBindings.Restore(Source, Disk);
        var display = RpgWorkstationBindings.Restore(Source, Workstation); var external = RpgExternalBindings.Restore(Source, External);
        var program = RpgCompiler.CompileModule(Library, Name, source, cancellationToken, display.Resolve, disk.Resolve, external.Resolve);
        disk.Verify(program); display.Verify(program); external.Verify(program); return program;
    }
}

public sealed record RpgBoundProgram(int Version, string Entry, IReadOnlyList<RpgModuleImage> Modules)
{
    public const string Attribute = "RPGBOUND";
    public IReadOnlyList<RpgServiceReference> Services { get; init; } = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false, MaxDepth = 16, TypeInfoResolver = RpgServiceReference.JsonTypes };
    private void Validate()
    {
        if (Services is null || Entry is null || Modules is null || Modules.Count is < 1 or > 16 || Modules.Any(m => m is null || m.Library is null || m.Name is null || m.Source is null || m.SourceMap is null || m.Disk is null || m.Workstation is null || m.External is null) ||
            Modules.Select(m => m.Library + "/" + m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Modules.Count ||
            Modules.Count(m => m.Library + "/" + m.Name == Entry) != 1 || Modules.Sum(m => (long)Encoding.UTF8.GetByteCount(m.Source)) > 4194304 ||
            Modules.Sum(m => (long)m.SourceMap.Length + m.Disk.Length + m.Workstation.Length + m.External.Length) > 16777216)
            throw new InvalidDataException("Invalid or oversized bound RPG program.");
        RpgServiceReference.ValidateImageVersion(Version, Services);
    }
    public string Serialize()
    {
        Validate(); var text = JsonSerializer.Serialize(this, Json);
        if (Encoding.UTF8.GetByteCount(text) > 33554432) throw new InvalidDataException("Bound RPG image exceeds 32 MiB.");
        return text;
    }
    public static RpgBoundProgram Deserialize(string text)
    {
        if (text.Length > 33554432 || Encoding.UTF8.GetByteCount(text) > 33554432) throw new InvalidDataException("Bound RPG image exceeds 32 MiB.");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("modules", out var moduleArray) ||
                moduleArray.ValueKind != JsonValueKind.Array || moduleArray.GetArrayLength() is < 1 or > 16)
                throw new InvalidDataException("Bound RPG images require 1 through 16 modules.");
            void Unique(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate bound program property."); Unique(property.Value); }
                }
                else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) Unique(value);
            }
            if (document.RootElement.TryGetProperty("services", out var services))
            {
                if (services.ValueKind != JsonValueKind.Array || services.GetArrayLength() > 16) throw new InvalidDataException("Invalid service reference array.");
                foreach (var service in services.EnumerateArray())
                {
                    if (service.ValueKind != JsonValueKind.Object || !service.TryGetProperty("exports", out var exports) || exports.ValueKind != JsonValueKind.Array || exports.GetArrayLength() is < 1 or > 256)
                        throw new InvalidDataException("Invalid service export array.");
                    if (service.TryGetProperty("dataExports", out var data) && (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 256)) throw new InvalidDataException("Invalid service data export array.");
                }
            }
            Unique(document.RootElement);
            var result = JsonSerializer.Deserialize<RpgBoundProgram>(text, Json) ?? throw new InvalidDataException("Missing bound RPG image.");
            result.Validate(); return result;
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid bound RPG image.", error); }
    }
    public RpgProgram Restore(CancellationToken cancellationToken = default)
    {
        Validate(); var modules = Modules.Select(m => m.Restore(cancellationToken)).ToArray();
        var entry = modules.Single(m => m.Library + "/" + m.Name == Entry);
        return RpgCompiler.BindModules(modules, entry, cancellationToken, Services);
    }
}
