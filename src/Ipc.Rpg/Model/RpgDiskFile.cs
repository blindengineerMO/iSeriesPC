using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ipc.Rpg.Model;

public sealed record RpgDiskRequest(string Name, string DescriptionFile, string RuntimeFile, bool Keyed, bool UserOpen,
    IReadOnlyList<RpgRecordRename> Renames, string Prefix = "", bool Update = false, bool Delete = false, bool Output = false, bool Input = true);
public sealed record RpgDiskField(string External, string Name, RpgFieldKind Kind, int Length, int Decimals,
    int Ccsid, int KeySequence, bool Descending, int Position, int StorageLength);
public sealed record RpgDiskLayout(string DescriptionFile, string Signature, string ExternalRecord, string Record,
    int RecordLength, int BufferLayoutVersion, IReadOnlyList<RpgDiskField> Fields)
{
    public static string SignatureFor(string external, string record, int length, int version, IReadOnlyList<RpgDiskField> fields)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { external, record, length, version, fields })));
}
public sealed record RpgDiskBinding(RpgDiskRequest Request, RpgDiskLayout Layout);

/// <summary>Source-bound external input layouts; restore never consults a live file.</summary>
public sealed record RpgDiskBindings(int Version, string TextHash, IReadOnlyList<RpgDiskBinding> Files)
{
    public const string AttributeName = "ipc.rpg.diskfiles";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 24, Converters = { new JsonStringEnumConverter() } };
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static InvalidDataException Invalid() => new("Invalid or stale compiled RPG disk bindings.");
    public static string Serialize(RpgProgram program)
    {
        foreach (var file in program.DiskFiles) Validate(file);
        var result = JsonSerializer.Serialize(new RpgDiskBindings(3, Hash(program.Source!.Text), program.DiskFiles), Json);
        if (Encoding.UTF8.GetByteCount(result) > 1048576) throw Invalid();
        return result;
    }
    public static RpgDiskBindings Restore(string text, string json)
    {
        if (text.Length > 4194304 || Encoding.UTF8.GetByteCount(text) > 4194304 || json.Length > 1048576 || Encoding.UTF8.GetByteCount(json) > 1048576) throw Invalid();
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = 24 });
            var objects = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!)) throw Invalid();
            }
            var result = JsonSerializer.Deserialize<RpgDiskBindings>(json, Json) ?? throw Invalid();
            if (result.Version is not (1 or 2 or 3) || result.TextHash != Hash(text) || result.Files is null || result.Files.Count > 16 ||
                result.Files.Any(f => f is null || f.Request is null) || result.Files.Select(f => f.Request.Name).Distinct().Count() != result.Files.Count) throw Invalid();
            foreach (var file in result.Files)
            {
                Validate(file);
                if (result.Version == 1 && (file.Request.Update || file.Request.Delete) || result.Version < 3 && (file.Request.Output || !file.Request.Input)) throw Invalid();
            }
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { throw Invalid(); }
    }
    public RpgDiskLayout Resolve(RpgDiskRequest request)
    {
        var file = Files.SingleOrDefault(f => f.Request.Name == request.Name) ?? throw Invalid();
        if (JsonSerializer.Serialize(file.Request, Json) != JsonSerializer.Serialize(request, Json)) throw Invalid();
        return file.Layout;
    }
    public static void Validate(RpgDiskBinding binding)
    {
        var request = binding.Request; var layout = binding.Layout;
        if (request is null || request.Delete && !request.Update || request.Update && !request.Input || !request.Input && !request.Output ||
            !RpgWorkstationBindings.Name(request.Name) || !RpgWorkstationBindings.FileName(request.DescriptionFile) || request.DescriptionFile.StartsWith("*CURLIB/", StringComparison.Ordinal) ||
            request.RuntimeFile != "*EXTDESC" && !RpgWorkstationBindings.FileName(request.RuntimeFile) || request.RuntimeFile?.StartsWith("*CURLIB/", StringComparison.Ordinal) == true ||
            request.Prefix is null || request.Prefix.Length > 64 || request.Prefix.Length > 0 && !RpgWorkstationBindings.Name(request.Prefix) ||
            request.Renames is null || request.Renames.Count > 1 || request.Renames.Any(r => r is null || !Ipc.Core.Objects.ObjectName.IsValid(r.External) || !RpgWorkstationBindings.Name(r.Name)) ||
            layout is null || !RpgWorkstationBindings.FileName(layout.DescriptionFile) || !layout.DescriptionFile.Contains('/') ||
            !Ipc.Core.Objects.ObjectName.IsValid(layout.ExternalRecord) || !RpgWorkstationBindings.Name(layout.Record) ||
            layout.Record != (request.Renames.SingleOrDefault()?.Name ?? layout.ExternalRecord) ||
            request.Renames.Any(r => r.External != layout.ExternalRecord) || layout.RecordLength is < 1 or > 16777216 ||
            layout.BufferLayoutVersion is not (1 or 2) || layout.Fields is null || layout.Fields.Count is < 1 or > 1024) throw Invalid();
        if (layout.Fields.Any(f => f is null || !Ipc.Core.Objects.ObjectName.IsValid(f.External) || !RpgWorkstationBindings.Name(f.Name) || f.Name != request.Prefix + f.External ||
                f.Kind is not (RpgFieldKind.Character or RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Integer or RpgFieldKind.Float or RpgFieldKind.Indicator or RpgFieldKind.Date or RpgFieldKind.Time or RpgFieldKind.Timestamp) ||
                f.Length is < 1 or > 32767 || f.Decimals < 0 || f.Decimals > 28 ||
                f.Kind is RpgFieldKind.Packed or RpgFieldKind.Zoned && (f.Length > 29 || f.Decimals > f.Length) ||
                f.Kind is not (RpgFieldKind.Packed or RpgFieldKind.Zoned) && f.Decimals != 0 ||
                f.Kind == RpgFieldKind.Integer && f.Length is not (3 or 5 or 10 or 20) || f.Kind == RpgFieldKind.Float && f.Length is not (4 or 8) ||
                f.Ccsid < 0 || f.KeySequence is < 0 or > 128 || f.Position < 1 || f.StorageLength < 1 || (long)f.Position + f.StorageLength - 1 > layout.RecordLength) ||
            layout.Fields.Select(f => f.Name).Distinct().Count() != layout.Fields.Count || layout.Fields.Select(f => f.External).Distinct().Count() != layout.Fields.Count ||
            !layout.Fields.Where(f => f.KeySequence > 0).Select(f => f.KeySequence).Order().SequenceEqual(Enumerable.Range(1, layout.Fields.Count(f => f.KeySequence > 0))) ||
            request.Keyed && !layout.Fields.Any(f => f.KeySequence > 0) ||
            layout.Signature != RpgDiskLayout.SignatureFor(layout.ExternalRecord, layout.Record, layout.RecordLength, layout.BufferLayoutVersion, layout.Fields)) throw Invalid();
    }
}
