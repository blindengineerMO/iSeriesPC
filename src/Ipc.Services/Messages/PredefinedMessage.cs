using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;

namespace Ipc.Services.Messages;

/// <summary>The definition and replacement bytes at send time, independent of later file changes.</summary>
public sealed record PredefinedMessage(string Library, string File, string RequestedLibrary, DateTimeOffset FileCreated,
    MessageDescription Description, ProgramBuffer Replacement)
{
    private sealed record Payload(int Version, string Library, string File, string RequestedLibrary, DateTimeOffset FileCreated,
        MessageDescription Description, string Data, int Ccsid);
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public string Serialize()
    {
        Validate();
        var text = JsonSerializer.Serialize(new Payload(1, Library, File, RequestedLibrary, FileCreated, Description, Replacement.ToBase64(), Replacement.Ccsid), Json);
        if (Encoding.UTF8.GetByteCount(text) > 32767) throw Invalid();
        return text;
    }
    public static PredefinedMessage? Restore(string text)
    {
        if (text.Length == 0) return null;
        if (text.Length > 32767 || Encoding.UTF8.GetByteCount(text) > 32767) throw Invalid();
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), new JsonReaderOptions { MaxDepth = 16 });
            var properties = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) properties.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) properties.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !properties.Peek().Add(reader.GetString()!)) throw Invalid();
            }
            var stored = JsonSerializer.Deserialize<Payload>(text, Json);
            if (stored is null || stored.Version != 1 || stored.Data is null || stored.Data.Length > 684) throw Invalid();
            var result = new PredefinedMessage(stored.Library, stored.File, stored.RequestedLibrary, stored.FileCreated,
                stored.Description, new(Convert.FromBase64String(stored.Data), stored.Ccsid));
            result.Validate(); return result with { Description = result.Description with { Fields = Array.AsReadOnly(result.Description.Fields.ToArray()) } };
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException) { throw Invalid(); }
    }
    private void Validate()
    {
        if (!ObjectName.IsValid(Library) || !ObjectName.IsValid(File) || Library != Library.ToUpperInvariant() || File != File.ToUpperInvariant() ||
            RequestedLibrary is null || RequestedLibrary is not ("*LIBL" or "*CURLIB") && !ObjectName.IsValid(RequestedLibrary) ||
            Description is null || Replacement is null || Replacement.Length > 512) throw Invalid();
        MessageDescriptionFormat.Validate(Description, legacyText: true);
    }
    private static CpfException Invalid() => new("IPC0127", "Invalid predefined message snapshot.");
}
