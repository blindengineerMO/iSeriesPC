using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ipc.Rpg.Model;

public sealed record RpgExternalRequest(string File, string? Format = null);
public sealed record RpgExternalField(string Name, RpgFieldKind Kind, int Length, int Decimals,
    int StorageLength, int Position, int KeySequence = 0, bool Input = true, bool Output = true, string? DefaultValue = null, bool CurrentDatetimeDefault = false, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool NullCapable = false);
public sealed record RpgExternalLayout(string File, string Format, string Device, int Length,
    IReadOnlyList<RpgExternalField> Fields);
public sealed record RpgExternalBinding(RpgExternalRequest Request, RpgExternalLayout Layout);
public sealed record RpgExternalOrigin(RpgExternalLayout Layout, bool ExplicitExtract);

/// <summary>Descriptions used by EXTNAME definitions, independent of open file declarations.</summary>
public sealed record RpgExternalBindings(int Version, string TextHash, IReadOnlyList<RpgExternalBinding> Descriptions)
{
    public const string AttributeName = "ipc.rpg.externalstructures";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 24, Converters = { new JsonStringEnumConverter() } };
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static InvalidDataException Invalid() => new("Invalid or stale compiled RPG external structure descriptions.");
    private static IReadOnlyList<RpgExternalBinding> ProgramDescriptions(RpgProgram program) => program.ExternalDescriptions.Concat(
        program.Subprocedures.Where(p => p.NativeBody is not null).SelectMany(p => p.NativeBody!.ExternalDescriptions)).DistinctBy(b => b.Request).ToArray();

    public static string Serialize(RpgProgram program)
    {
        var descriptions = ProgramDescriptions(program); ValidateDescriptions(descriptions);
        var bytes = new BoundedMetadata();
        using (var writer = new Utf8JsonWriter(bytes)) JsonSerializer.Serialize(writer, new RpgExternalBindings(descriptions.Any(b => b.Layout.Fields.Any(f => f.NullCapable)) ? 2 : 1, Hash(program.Source!.Text), descriptions), Json);
        return Encoding.UTF8.GetString(bytes.Written);
    }
    public static RpgExternalBindings Restore(string source, string json)
    {
        if (source.Length > 4194304 || Encoding.UTF8.GetByteCount(source) > 4194304 || json.Length > 1048576 || Encoding.UTF8.GetByteCount(json) > 1048576) throw Invalid();
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
            var result = JsonSerializer.Deserialize<RpgExternalBindings>(json, Json) ?? throw Invalid();
            if (result.Descriptions is null || result.Version is not (1 or 2) || result.Version < 2 && result.Descriptions.Any(b => b?.Layout?.Fields?.Any(f => f?.NullCapable == true) == true) || result.TextHash != Hash(source)) throw Invalid();
            ValidateDescriptions(result.Descriptions); return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or OverflowException) { throw Invalid(); }
    }
    public RpgExternalLayout Resolve(RpgExternalRequest request) => Descriptions.SingleOrDefault(b => b.Request == request)?.Layout ?? throw Invalid();
    public void Verify(RpgProgram program)
    {
        var actual = ProgramDescriptions(program);
        if (Descriptions.Count != actual.Count || actual.Any(b => JsonSerializer.Serialize(b, Json) != JsonSerializer.Serialize(Descriptions.SingleOrDefault(s => s.Request == b.Request), Json))) throw Invalid();
    }
    private static void ValidateDescriptions(IReadOnlyList<RpgExternalBinding> descriptions)
    {
        if (descriptions is null || descriptions.Count > 256 || descriptions.Any(b => b is null) || descriptions.Select(b => b.Request).Distinct().Count() != descriptions.Count) throw Invalid();
        foreach (var binding in descriptions) Validate(binding);
    }
    public static void Validate(RpgExternalBinding binding)
    {
        var request = binding.Request; var layout = binding.Layout;
        if (request is null || !RpgWorkstationBindings.FileName(request.File) || request.Format is not null && !Ipc.Core.Objects.ObjectName.IsValid(request.Format) ||
            layout is null || !RpgWorkstationBindings.FileName(layout.File) || !layout.File.Contains('/') || !Ipc.Core.Objects.ObjectName.IsValid(layout.Format) ||
            request.Format is not null && request.Format != layout.Format || layout.Device is not ("DISK" or "WORKSTN") || layout.Length is < 1 or > 1048576 ||
            layout.Fields is null || layout.Fields.Count is < 1 or > 1024 || layout.Fields.Any(f => f is null) || layout.Fields.Select(f => f.Name).Distinct().Count() != layout.Fields.Count) throw Invalid();
        foreach (var field in layout.Fields)
        {
            var width = field.Kind switch { RpgFieldKind.Packed => (field.Length + 2) / 2, RpgFieldKind.Integer => field.Length switch { 3 => 1, 5 => 2, 10 => 4, 20 => 8, _ => -1 }, _ => field.Length };
            if (field.NullCapable && layout.Device != "DISK" || width < 1 || !Ipc.Core.Objects.ObjectName.IsValid(field.Name) || field.Kind is not (RpgFieldKind.Character or RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Integer or RpgFieldKind.Float or RpgFieldKind.Indicator or RpgFieldKind.Date or RpgFieldKind.Time or RpgFieldKind.Timestamp) ||
                field.Length is < 1 or > 32767 || field.StorageLength != width || field.Decimals < 0 || field.Decimals > field.Length || field.Decimals > 28 ||
                field.Kind is RpgFieldKind.Packed or RpgFieldKind.Zoned && field.Length > 29 || field.Kind is not (RpgFieldKind.Packed or RpgFieldKind.Zoned) && field.Decimals != 0 ||
                field.Kind == RpgFieldKind.Float && field.Length is not (4 or 8) || field.Kind == RpgFieldKind.Indicator && field.Length != 1 ||
                field.Kind == RpgFieldKind.Date && field.Length != 10 || field.Kind == RpgFieldKind.Time && field.Length != 8 || field.Kind == RpgFieldKind.Timestamp && field.Length != 26 ||
                field.CurrentDatetimeDefault && field.Kind is not (RpgFieldKind.Date or RpgFieldKind.Time or RpgFieldKind.Timestamp) ||
                field.Position < 1 || (long)field.Position + width - 1 > layout.Length || field.KeySequence is < 0 or > 128 || field.DefaultValue?.Length > 32767 || !field.Input && !field.Output) throw Invalid();
        }
        var keys = layout.Fields.Where(f => f.KeySequence > 0).Select(f => f.KeySequence).Order().ToArray();
        if (!keys.SequenceEqual(Enumerable.Range(1, keys.Length))) throw Invalid();
        var ranges = layout.Fields.OrderBy(f => f.Position).ToArray();
        for (var i = 1; i < ranges.Length; i++) if (ranges[i].Position < ranges[i - 1].Position + ranges[i - 1].StorageLength) throw Invalid();
    }
    private sealed class BoundedMetadata : IBufferWriter<byte>
    {
        private readonly byte[] _bytes = new byte[1048576 + 16384]; private int _written;
        internal ReadOnlySpan<byte> Written => _bytes.AsSpan(0, _written);
        public void Advance(int count) { if (count < 0 || count > 1048576 - _written) throw Invalid(); _written += count; }
        public Memory<byte> GetMemory(int sizeHint = 0) { if (sizeHint < 0 || sizeHint > _bytes.Length - _written) throw Invalid(); return _bytes.AsMemory(_written); }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
