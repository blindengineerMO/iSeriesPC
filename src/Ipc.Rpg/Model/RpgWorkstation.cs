using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ipc.Core.Objects;

namespace Ipc.Rpg.Model;

public sealed record RpgSubfileBinding(string Record, string RelativeRecord);
public sealed record RpgRecordRename(string External, string Name);
public sealed record RpgWorkstationRequest(string Name, string DescriptionFile, string RuntimeFile, bool Input, bool Output, bool UserOpen,
    IReadOnlyList<RpgSubfileBinding> Subfiles, IReadOnlyList<RpgRecordRename> Renames, string Prefix = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsTemplate = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Qualified = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LikeFile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsParameter = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Procedure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Static = false);
public sealed record RpgDisplayField(string External, string Name, RpgFieldKind Kind, int Length, int Decimals, bool Input, bool Output, bool Opaque = false);
public sealed record RpgDisplayRecord(string External, string Name, IReadOnlyList<RpgDisplayField> Fields, bool Subfile,
    bool MessageSubfile, string? ControlFor, int Capacity = 0);
public sealed record RpgWorkstationLayout(string DescriptionFile, string Signature, IReadOnlyList<RpgDisplayRecord> Records)
{
    // Null preserves version-one snapshots, whose record signature did not include INDARA.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IndicatorArea { get; init; }
    public static string SignatureFor(IReadOnlyList<RpgDisplayRecord> records)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(records)));
    public static string SignatureFor(IReadOnlyList<RpgDisplayRecord> records, bool indicatorArea)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { records, indicatorArea })));
}
public sealed record RpgWorkstationBinding(RpgWorkstationRequest Request, RpgWorkstationLayout Layout,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Family = null);

public sealed partial record RpgWorkstationBindings(int Version, string TextHash, IReadOnlyList<RpgWorkstationBinding> Files)
{
    public const string AttributeName = "ipc.rpg.workstations";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 24, Converters = { new JsonStringEnumConverter() } };
    public static string Serialize(RpgProgram program)
    {
        var files = ProgramFiles(program);
        foreach (var file in files) Validate(file);
        ValidateFamilies(files);
        var version = files.Any(f => f.Request.Procedure is not null) ? 5 : files.Any(f => f.Request.IsParameter) ? 4 : files.Any(Extended) ? 3 : files.Any(f => f.Layout.IndicatorArea is not null) ? 2 : 1;
        if (version == 2 && files.Any(f => f.Layout.IndicatorArea is null)) throw Invalid();
        var buffer = new BoundedMetadata();
        using (var writer = new Utf8JsonWriter(buffer)) JsonSerializer.Serialize(writer, new RpgWorkstationBindings(version, Hash(program.Source!.Text), files), Json);
        return Encoding.UTF8.GetString(buffer.Written);
    }
    public static RpgWorkstationBindings Restore(string text, string json)
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
            var result = JsonSerializer.Deserialize<RpgWorkstationBindings>(json, Json) ?? throw Invalid();
            if (result.Version is not (1 or 2 or 3 or 4 or 5) || result.TextHash != Hash(text) || result.Files is null || result.Files.Count > 4112 ||
                result.Files.Any(f => f is null || f.Request is null) || result.Files.Select(f => (f.Request.Procedure, f.Request.Name)).Distinct().Count() != result.Files.Count) throw Invalid();
            foreach (var file in result.Files)
            {
                Validate(file);
                if (result.Version == 1 && file.Layout.IndicatorArea is not null || result.Version == 2 && file.Layout.IndicatorArea is null || result.Version < 3 && Extended(file) || result.Version < 4 && file.Request.IsParameter || result.Version < 5 && (file.Request.Procedure is not null || file.Request.Static)) throw Invalid();
            }
            ValidateFamilies(result.Files);
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { throw Invalid(); }
    }
    public RpgWorkstationLayout Resolve(RpgWorkstationRequest request)
    {
        var file = Files.SingleOrDefault(f => f.Request.Name == request.Name && f.Request.Procedure == request.Procedure) ?? throw Invalid();
        if (JsonSerializer.Serialize(file.Request, Json) != JsonSerializer.Serialize(request, Json)) throw Invalid();
        return file.Layout;
    }
    public static bool Name(string? value) => value is { Length: >= 1 and <= 128 } &&
        Regex.IsMatch(value, @"\A[A-Z_][A-Z0-9_]*\z", RegexOptions.CultureInvariant);
    public static bool FileName(string? value) => value?.Split('/') is { Length: >= 1 and <= 2 } parts && ObjectName.IsValid(parts[^1]) &&
        (parts.Length == 1 || parts[0] is "*LIBL" or "*CURLIB" || ObjectName.IsValid(parts[0]));
    public static void Validate(RpgWorkstationBinding file)
    {
        var request = file.Request; var layout = file.Layout;
        if (request is null || request.Procedure is not null && !Name(request.Procedure) || request.Static && (request.Procedure is null || request.IsTemplate || request.IsParameter) ||
            !Name(request.Name) || !FileName(request.DescriptionFile) || request.RuntimeFile != "*EXTDESC" && !FileName(request.RuntimeFile) ||
            !request.Input && !request.Output || request.Prefix is null || request.Prefix.Length > 64 || request.Prefix.Length > 0 && !Name(request.Prefix) ||
            request.Subfiles is null || request.Renames is null || request.Subfiles.Count > 256 || request.Renames.Count > 256 ||
            request.Subfiles.Any(s => s is null || !Name(s.Record) || (request.IsTemplate || request.IsParameter ? s.RelativeRecord != "" : !Name(s.RelativeRecord))) || request.Subfiles.Select(s => s.Record).Distinct().Count() != request.Subfiles.Count ||
            request.Renames.Any(r => r is null || !ObjectName.IsValid(r.External) || !Name(r.Name)) || request.Renames.Select(r => r.External).Distinct().Count() != request.Renames.Count ||
            request.Renames.Select(r => r.Name).Distinct().Count() != request.Renames.Count || layout is null || !FileName(layout.DescriptionFile) || !layout.DescriptionFile.Contains('/') ||
            layout.Signature is not { Length: 64 } || layout.Signature.Any(c => !char.IsAsciiHexDigit(c)) || layout.Records is null || layout.Records.Count is < 1 or > 256) throw Invalid();
        if (request.IsParameter && (request.IsTemplate || !request.Qualified || !request.UserOpen || request.LikeFile is null) ||
            request.LikeFile is not null && (!Name(request.LikeFile) || request.LikeFile == request.Name || !request.Qualified || file.Family is null) ||
            request.LikeFile is null && file.Family is not null || request.IsTemplate && request.UserOpen) throw Invalid();
        if (layout.Records.Any(r => r is null || !ObjectName.IsValid(r.External) || !RecordNameValid(request, r.Name) ||
            r.Name != RecordName(request, request.Renames.SingleOrDefault(n => n.External == r.External)?.Name ?? r.External) || r.Fields is null || r.Fields.Count > 1024 ||
            r.Fields.Any(f => f is null || !ObjectName.IsValid(f.External) || !Name(f.Name) || f.Name != request.Prefix + f.External ||
                f.Kind is not (RpgFieldKind.Character or RpgFieldKind.Packed or RpgFieldKind.Date or RpgFieldKind.Time or RpgFieldKind.Timestamp) ||
                f.Length is < 1 or > 3563 || f.Decimals < 0 || f.Decimals > 28 || f.Kind == RpgFieldKind.Packed && (f.Length > 29 || f.Decimals > f.Length) ||
                f.Kind != RpgFieldKind.Packed && f.Decimals != 0 || f.Opaque && (f.Kind != RpgFieldKind.Character || f.Length != 4)) ||
            r.Fields.Select(f => f.Name).Distinct().Count() != r.Fields.Count || r.MessageSubfile && !r.Subfile || r.Capacity is < 0 or > 9999 ||
            r.Subfile && r.Capacity < 1 || r.ControlFor is not null && !layout.Records.Any(s => s.Name == r.ControlFor && s.Subfile)) ||
            layout.Records.Select(r => r.Name).Distinct().Count() != layout.Records.Count || layout.Records.Select(r => r.External).Distinct().Count() != layout.Records.Count ||
            request.Subfiles.Any(s => !layout.Records.Any(r => r.Name == RecordName(request, s.Record) && r.Subfile)) ||
            layout.Records.Any(r => r.Subfile && !request.Subfiles.Any(s => RecordName(request, s.Record) == r.Name)) ||
            layout.Signature != (layout.IndicatorArea is { } area ? RpgWorkstationLayout.SignatureFor(layout.Records, area) : RpgWorkstationLayout.SignatureFor(layout.Records))) throw Invalid();
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static InvalidDataException Invalid() => new("Invalid or stale compiled RPG workstation bindings.");
}
