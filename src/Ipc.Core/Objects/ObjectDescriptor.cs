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

    public void Touch() => Changed = DateTimeOffset.UtcNow;
}