namespace Ipc.Core.Objects;

public sealed class ObjectDescriptor
{
    public required QualifiedName Key { get; init; }

    public required string ObjectType { get; init; }

    public string Library => Key.Library;

    public string Name => Key.Name.Value;

    public string Owner { get; set; } = "QSECOFR";

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset Changed { get; set; } = DateTimeOffset.UtcNow;

    public string? Description { get; set; }

    public int Ccsid { get; set; } = 37;

    public string? Attribute { get; set; }

    public string? Format { get; set; }

    public AuthorityBit PublicAuthority { get; set; } = Authorities.UseBits;

    public string? Source { get; set; }

    public IReadOnlyDictionary<string, string>? ExtendedAttributes { get; set; }

    public bool AdoptsOwnerAuthority
    {
        get => ExtendedAttributes?.TryGetValue("ipc.program.adoptOwner", out var value) == true && value == "*YES";
        set => SetAttribute("ipc.program.adoptOwner", value ? "*YES" : "*NO");
    }

    public bool UsesAdoptedAuthority
    {
        get => ExtendedAttributes?.TryGetValue("ipc.program.useAdopted", out var value) != true || value != "*NO";
        set => SetAttribute("ipc.program.useAdopted", value ? "*YES" : "*NO");
    }

    private void SetAttribute(string name, string value)
    {
        var attributes = new Dictionary<string, string>(ExtendedAttributes ?? new Dictionary<string, string>());
        attributes[name] = value;
        ExtendedAttributes = attributes;
    }

    public bool UseAuthorizationListPublicAuthority
    {
        get => ExtendedAttributes?.TryGetValue("ipc.authority.publicSource", out var value) == true && value == "*AUTL";
        set
        {
            var attributes = new Dictionary<string, string>(ExtendedAttributes ?? new Dictionary<string, string>());
            if (value) attributes["ipc.authority.publicSource"] = "*AUTL";
            else attributes.Remove("ipc.authority.publicSource");
            ExtendedAttributes = attributes;
        }
    }

    public ObjectSignature? Signature
    {
        get => ExtendedAttributes?.TryGetValue("ipc.signature", out var value) == true
            ? global::System.Text.Json.JsonSerializer.Deserialize<ObjectSignature>(value) : null;
        set
        {
            var attributes = new Dictionary<string, string>(ExtendedAttributes ?? new Dictionary<string, string>());
            if (value is null) attributes.Remove("ipc.signature");
            else attributes["ipc.signature"] = global::System.Text.Json.JsonSerializer.Serialize(value);
            ExtendedAttributes = attributes;
        }
    }

    public ObjectDescriptor Snapshot(QualifiedName? key = null) => new()
    {
        Key = key ?? Key, ObjectType = ObjectType, Owner = Owner, Created = Created,
        Changed = Changed, Description = Description, Ccsid = Ccsid, Attribute = Attribute,
        Format = Format, PublicAuthority = PublicAuthority, Source = Source,
        ExtendedAttributes = ExtendedAttributes is null ? null : new Dictionary<string, string>(ExtendedAttributes),
    };

    public void Touch() => Changed = DateTimeOffset.UtcNow;

    public void ValidateIdentity()
    {
        if (!ObjectName.IsValid(Library) || !ObjectName.IsValid(Name))
            throw new ArgumentException("Persisted objects require resolved library and object names.");
        if (!global::Ipc.Core.Objects.ObjectType.IsKnown(ObjectType))
            throw new ArgumentException($"Unknown object type {ObjectType}; file attributes use *FILE identity.");
        if (ObjectType is global::Ipc.Core.Objects.ObjectType.Library or global::Ipc.Core.Objects.ObjectType.UserProfile or global::Ipc.Core.Objects.ObjectType.AuthorizationList && Library != "QSYS")
            throw new ArgumentException($"{ObjectType} objects must reside in QSYS.");
        if (!ObjectName.IsValid(Owner)) throw new ArgumentException("An object owner must be a valid profile name.");
    }
}

/// <summary>Persisted signature evidence. Trust and verification are performed by the security service.</summary>
public sealed record ObjectSignature(string Algorithm, string KeyId, string ContentHash, string Value, DateTimeOffset SignedAt);
