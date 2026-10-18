using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ipc.Rpg.Model;

public sealed record RpgDiskRequest(string Name, string DescriptionFile, string RuntimeFile, bool Keyed, bool UserOpen,
    IReadOnlyList<RpgRecordRename> Renames, string Prefix = "", bool Update = false, bool Delete = false, bool Output = false, bool Input = true, bool IsTemplate = false, bool Qualified = false, string? LikeFile = null);
public sealed record RpgDiskField(string External, string Name, RpgFieldKind Kind, int Length, int Decimals,
    int Ccsid, int KeySequence, bool Descending, int Position, int StorageLength);
public sealed record RpgDiskLayout(string DescriptionFile, string Signature, string ExternalRecord, string Record,
    int RecordLength, int BufferLayoutVersion, IReadOnlyList<RpgDiskField> Fields)
{
    public static string SignatureFor(string external, string record, int length, int version, IReadOnlyList<RpgDiskField> fields)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { external, record, length, version, fields })));
}
public sealed record RpgDiskBinding(RpgDiskRequest Request, RpgDiskLayout Layout, string? Family = null);

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
        ValidateFamilies(program.DiskFiles);
        var result = JsonSerializer.Serialize(new RpgDiskBindings(program.DiskFiles.Any(f => f.Request.IsTemplate || f.Request.Qualified || f.Request.LikeFile is not null) ? 4 : 3, Hash(program.Source!.Text), program.DiskFiles), Json);
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
            if (result.Version is not (1 or 2 or 3 or 4) || result.TextHash != Hash(text) || result.Files is null || result.Files.Count > 16 ||
                result.Files.Any(f => f is null || f.Request is null) || result.Files.Select(f => f.Request.Name).Distinct().Count() != result.Files.Count) throw Invalid();
            foreach (var file in result.Files)
            {
                Validate(file);
                if (result.Version < 4 && (file.Request.IsTemplate || file.Request.Qualified || file.Request.LikeFile is not null || file.Family is not null) || result.Version == 1 && (file.Request.Update || file.Request.Delete) || result.Version < 3 && (file.Request.Output || !file.Request.Input)) throw Invalid();
            }
            ValidateFamilies(result.Files);
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
    public static string RecordName(RpgDiskRequest request, string record) => request.Qualified ? request.Name + "." + record : record;
    public static string FamilyKey(RpgDiskBinding file) => file.Family ?? file.Request.Name + ":" + file.Layout.Signature;
    private static void ValidateFamilies(IReadOnlyList<RpgDiskBinding> files)
    {
        if (files.Count > 16 || files.Select(f => f.Request.Name).Distinct().Count() != files.Count) throw Invalid();
        foreach (var file in files)
        {
            if (file.Request.LikeFile is null) { if (file.Family is not null) throw Invalid(); continue; }
            var seen = new HashSet<string>(StringComparer.Ordinal) { file.Request.Name }; var parent = file;
            while (parent.Request.LikeFile is { } name)
            {
                if (!seen.Add(name) || files.SingleOrDefault(f => f.Request.Name == name) is not { } next) throw Invalid();
                parent = next;
            }
            if (file.Family != FamilyKey(parent) || file.Request.DescriptionFile != parent.Request.DescriptionFile ||
                file.Request.Keyed != parent.Request.Keyed || file.Request.Input != parent.Request.Input || file.Request.Output != parent.Request.Output ||
                file.Request.Update != parent.Request.Update || file.Request.Delete != parent.Request.Delete || file.Request.Prefix != parent.Request.Prefix ||
                !file.Request.Renames.SequenceEqual(parent.Request.Renames) || file.Layout.DescriptionFile != parent.Layout.DescriptionFile ||
                file.Layout.ExternalRecord != parent.Layout.ExternalRecord || file.Layout.RecordLength != parent.Layout.RecordLength ||
                file.Layout.BufferLayoutVersion != parent.Layout.BufferLayoutVersion || !file.Layout.Fields.SequenceEqual(parent.Layout.Fields)) throw Invalid();
        }
    }
    public void Verify(RpgProgram program)
    {
        if (program.DiskFiles.Count != Files.Count) throw new InvalidDataException("Unused compiled RPG disk bindings.");
        foreach (var file in program.DiskFiles)
        {
            var stored = Files.SingleOrDefault(f => f.Request.Name == file.Request.Name) ?? throw Invalid();
            if (JsonSerializer.Serialize(stored, Json) != JsonSerializer.Serialize(file, Json)) throw Invalid();
        }
    }
    public static void Validate(RpgDiskBinding binding)
    {
        var request = binding.Request; var layout = binding.Layout;
        if (request is null || request.IsTemplate && request.UserOpen || request.LikeFile is not null && (!request.Qualified || !RpgWorkstationBindings.Name(request.LikeFile) || request.LikeFile == request.Name) ||
            binding.Family is { Length: > 256 } || request.Delete && !request.Update || request.Update && !request.Input || !request.Input && !request.Output ||
            !RpgWorkstationBindings.Name(request.Name) || !RpgWorkstationBindings.FileName(request.DescriptionFile) || request.DescriptionFile.StartsWith("*CURLIB/", StringComparison.Ordinal) ||
            request.RuntimeFile != "*EXTDESC" && !RpgWorkstationBindings.FileName(request.RuntimeFile) || request.RuntimeFile?.StartsWith("*CURLIB/", StringComparison.Ordinal) == true ||
            request.Prefix is null || request.Prefix.Length > 64 || request.Prefix.Length > 0 && !RpgWorkstationBindings.Name(request.Prefix) ||
            request.Renames is null || request.Renames.Count > 1 || request.Renames.Any(r => r is null || !Ipc.Core.Objects.ObjectName.IsValid(r.External) || !RpgWorkstationBindings.Name(r.Name)) ||
            layout is null || !RpgWorkstationBindings.FileName(layout.DescriptionFile) || !layout.DescriptionFile.Contains('/') ||
            !Ipc.Core.Objects.ObjectName.IsValid(layout.ExternalRecord) ||
            layout.Record != RecordName(request, request.Renames.SingleOrDefault()?.Name ?? layout.ExternalRecord) ||
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
