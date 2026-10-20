using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed record BindingDirectoryEntry(string Library, string Name, string Type, string Activation = "*IMMED")
{
    public void Validate()
    {
        if (Library != "*LIBL" && (!ObjectName.IsValid(Library) || Library != Library.ToUpperInvariant()) || !ObjectName.IsValid(Name) || Name != Name.ToUpperInvariant() ||
            Type is not (ObjectType.Module or ObjectType.ServiceProgram) || Activation is not ("*IMMED" or "*DEFER"))
            throw new CpfException("IPC0003", "Binding entries require a library or *LIBL, an object name, *MODULE/*SRVPGM and *IMMED/*DEFER activation.");
    }
}
public sealed record BindingDirectorySelector(string Library, string Name, string Type)
{
    public bool Matches(BindingDirectoryEntry entry) => (Library == "*ALL" || Library == entry.Library) &&
        (Type == "*ALL" || Type == entry.Type) && (Name == "*ALL" || NamePattern.Matches(entry.Name, Name));
    public void Validate()
    {
        var stem = Name?.EndsWith('*') == true ? Name[..^1] : Name;
        if (Library is not ("*LIBL" or "*ALL") && (!ObjectName.IsValid(Library) || Library != Library.ToUpperInvariant()) ||
            Name != "*ALL" && (!ObjectName.IsValid(stem) || stem != stem!.ToUpperInvariant()) || Type is not ("*ALL" or ObjectType.Module or ObjectType.ServiceProgram))
            throw new CpfException("IPC0003", "Invalid binding-directory removal selector.");
    }
}

/// <summary>Ordered directory entries live in the object payload, so object copy/save/delete retain normal semantics.</summary>
public sealed class BindingDirectoryStore(SqliteConnectionFactory factory)
{
    private const string Attribute = "BNDDIR";
    private sealed record Image(int Version, IReadOnlyList<BindingDirectoryEntry> Entries);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    private readonly SqliteObjectStore _objects = new(factory);
    private readonly ServiceAuthorization _authorization = new(factory);
    public ObjectDescriptor? Find(string library, string name, AuthorityBit required)
    {
        var descriptor = _objects.GetForAuthorization(library, name, ObjectType.BindingDirectory);
        if (descriptor is null) return null;
        _authorization.RequireObject("QSYS", descriptor.Library, ObjectType.Library, Authorities.UseBits, checkLibrary: false);
        _authorization.RequireObject(library, name, ObjectType.BindingDirectory, required);
        return descriptor.Library == library ? descriptor : descriptor.Snapshot(new(library, name));
    }
    public IReadOnlyList<BindingDirectoryEntry> Read(string library, string name)
        => Decode(Find(library, name, Authorities.UseBits) ?? throw Missing());
    public void Create(string library, string name, string text = "", string authority = "*LIBCRTAUT")
    {
        if (text == "*BLANK") text = "";
        if (text.Length > 50 || text.Any(char.IsControl)) throw new CpfException("IPC0003", "Binding directory text must contain at most 50 printable characters.");
        var container = _objects.GetRequired("QSYS", library, ObjectType.Library);
        if (authority == "*LIBCRTAUT") authority = container.ExtendedAttributes?.GetValueOrDefault("ipc.library.createAuthority") ?? "*CHANGE";
        var list = ObjectName.IsValid(authority);
        if (list) _authorization.RequireObject("QSYS", authority, ObjectType.AuthorizationList, AuthorityBit.ObjectReference);
        AuthorityBit bits;
        try { bits = list ? AuthorityBit.None : Authorities.FromLevel(authority); }
        catch (ArgumentException) { throw new CpfException("IPC0003", "Invalid binding-directory authority."); }
        var descriptor = new ObjectDescriptor { Key = new(library, name), ObjectType = ObjectType.BindingDirectory, Attribute = Attribute,
            Source = Encode([]), Owner = OperationIdentity.Current?.Principal ?? "QSECOFR", Description = text, PublicAuthority = bits, UseAuthorizationListPublicAuthority = list };
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        _objects.Create(descriptor, connection, transaction);
        if (list)
        {
            var key = new JobTemporaryLibrary(factory).Resolve(descriptor.Key, ObjectType.BindingDirectory);
            using var attach = connection.CreateCommand(); attach.Transaction = transaction;
            attach.CommandText = "INSERT INTO sys_authorities(lib,name,type,holder,is_authl,bits) VALUES($lib,$name,'*BNDDIR',$list,1,$bits)";
            attach.Parameters.AddWithValue("$lib", key.Library); attach.Parameters.AddWithValue("$name", key.Name.Value);
            attach.Parameters.AddWithValue("$list", authority); attach.Parameters.AddWithValue("$bits", (int)Authorities.AllBits); attach.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void Add(string library, string name, IReadOnlyList<BindingDirectoryEntry> entries, string position = "*LAST", BindingDirectoryEntry? anchor = null)
    {
        if (entries.Count is < 1 or > 256) throw new CpfException("IPC0003", "Add 1 through 256 entries.");
        foreach (var entry in entries) entry.Validate();
        if (position is not ("*FIRST" or "*LAST" or "*BEFORE" or "*AFTER" or "*REPLACE") ||
            (position is "*FIRST" or "*LAST" ? anchor is not null : anchor is null) || position == "*REPLACE" && entries.Count != 1)
            throw new CpfException("IPC0003", "Invalid binding-directory position or replacement list.");
        anchor?.Validate();
        Change(library, name, AuthorityBit.ObjectOperate | AuthorityBit.Add, current =>
        {
            var result = current.ToList(); var index = position == "*FIRST" ? 0 : result.Count;
            if (anchor is not null)
            {
                index = result.FindIndex(e => Same(e, anchor)); if (index < 0) throw new CpfException("CPF9801", "Position entry not found.");
                if (position == "*AFTER") index++;
                if (position == "*REPLACE") result.RemoveAt(index);
            }
            result.InsertRange(index, entries); Validate(result); return result;
        });
    }
    public void Remove(string library, string name, IReadOnlyList<BindingDirectorySelector> selectors)
    {
        if (selectors.Count is < 1 or > 256) throw new CpfException("IPC0003", "Remove 1 through 256 selectors.");
        foreach (var selector in selectors) selector.Validate();
        Change(library, name, AuthorityBit.ObjectOperate | AuthorityBit.Delete, current =>
        {
            foreach (var selector in selectors) if (!current.Any(selector.Matches)) throw new CpfException("CPF9801", "Binding-directory entry not found.");
            return current.Where(e => !selectors.Any(s => s.Matches(e))).ToArray();
        });
    }
    private void Change(string library, string name, AuthorityBit required, Func<IReadOnlyList<BindingDirectoryEntry>, IReadOnlyList<BindingDirectoryEntry>> change)
    {
        _ = Find(library, name, required) ?? throw Missing();
        var key = new JobTemporaryLibrary(factory).Resolve(new(library, name), ObjectType.BindingDirectory);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        ObjectDescriptor descriptor;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction; read.CommandText = "SELECT * FROM sys_objects WHERE lib=$lib AND name=$name AND type='*BNDDIR'";
            read.Parameters.AddWithValue("$lib", key.Library); read.Parameters.AddWithValue("$name", key.Name.Value);
            using var reader = read.ExecuteReader(); descriptor = reader.Read() ? SqliteObjectStore.ReadDescriptor(reader) : throw Missing();
        }
        var source = Encode(change(Decode(descriptor)));
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE sys_objects SET source=$source,changed=$at WHERE lib=$lib AND name=$name AND type='*BNDDIR'";
        update.Parameters.AddWithValue("$lib", key.Library); update.Parameters.AddWithValue("$name", key.Name.Value);
        update.Parameters.AddWithValue("$source", source); update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        if (update.ExecuteNonQuery() != 1) throw Missing(); transaction.Commit();
    }
    private static bool Same(BindingDirectoryEntry left, BindingDirectoryEntry right) => left.Library == right.Library && left.Name == right.Name && left.Type == right.Type;
    private static void Validate(IReadOnlyList<BindingDirectoryEntry> entries)
    {
        if (entries is null || entries.Count > 256 || entries.Any(e => e is null)) throw new InvalidDataException("Invalid binding-directory entries.");
        foreach (var entry in entries) entry.Validate();
        if (entries.Select(e => (e.Library, e.Name, e.Type)).Distinct().Count() != entries.Count) throw new CpfException("IPC0003", "Duplicate binding-directory entry.");
    }
    private static string Encode(IReadOnlyList<BindingDirectoryEntry> entries)
    { Validate(entries); return JsonSerializer.Serialize(new Image(1, entries), Json); }
    private static IReadOnlyList<BindingDirectoryEntry> Decode(ObjectDescriptor descriptor)
    {
        var text = descriptor.Source;
        if (descriptor.Attribute != Attribute || text is null || text.Length > 524288 || Encoding.UTF8.GetByteCount(text) > 524288) throw new InvalidDataException("Invalid binding-directory image.");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 }); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 256)
                throw new InvalidDataException("Invalid binding-directory entry array.");
            void Unique(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate directory image property."); Unique(property.Value); }
                }
                else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
            }
            Unique(root); var image = JsonSerializer.Deserialize<Image>(text, Json) ?? throw new InvalidDataException("Missing binding-directory image.");
            if (image.Version != 1) throw new InvalidDataException("Unknown binding-directory image version."); Validate(image.Entries); return image.Entries;
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid binding-directory image.", error); }
    }
    private static CpfException Missing() => new("CPF9801", "Binding directory not found.");
}
