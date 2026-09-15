using Ipc.Core.Objects;

namespace Ipc.Core.Tests;

public sealed class FileSystemObjectStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-objects-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Metadata_and_signature_evidence_survive_restart_with_type_qualified_identity()
    {
        var signature = new ObjectSignature("TEST", "KEY1", "HASH", "SIGNATURE", DateTimeOffset.UtcNow);
        using (var store = new FileSystemObjectStore(_directory))
        {
            var descriptor = new ObjectDescriptor
            {
                Key = new QualifiedName("QGPL", "SAME"), ObjectType = ObjectType.PanelGroup,
                Owner = "OWNER", Source = "panel source", Ccsid = 1208,
                ExtendedAttributes = new Dictionary<string, string> { ["custom"] = "value" }, Signature = signature,
            };
            store.Create(descriptor);
            store.Create(new ObjectDescriptor { Key = descriptor.Key, ObjectType = ObjectType.Program });
        }
        using var reopened = new FileSystemObjectStore(_directory);
        Assert.Equal(2, reopened.ObjectCount);
        var panel = reopened.GetRequired("QGPL", "SAME", ObjectType.PanelGroup);
        Assert.Equal(signature, panel.Signature);
        Assert.Equal("OWNER", panel.Owner);
        Assert.Equal("panel source", panel.Source);
        Assert.Equal("value", panel.ExtendedAttributes!["custom"]);
        Assert.Equal(1208, panel.Ccsid);
    }

    [Fact]
    public void Readers_cannot_mutate_persisted_state_without_update_and_duplicate_create_is_atomic()
    {
        using var store = new FileSystemObjectStore(_directory);
        var descriptor = new ObjectDescriptor { Key = new QualifiedName("QGPL", "OBJ"), ObjectType = ObjectType.Program, Source = "original" };
        store.Create(descriptor);
        descriptor.Source = "outside mutation";
        Assert.Equal("original", store.GetRequired("QGPL", "OBJ", ObjectType.Program).Source);
        Assert.Throws<InvalidOperationException>(() => store.Create(descriptor));
        Assert.Equal(1, store.ObjectCount);
        store.Update(descriptor);
        Assert.Equal("outside mutation", store.GetRequired("QGPL", "OBJ", ObjectType.Program).Source);
    }

    [Fact]
    public void A_second_owner_is_rejected_and_disposal_releases_ownership()
    {
        using (var first = new FileSystemObjectStore(_directory))
            Assert.Throws<IOException>(() => new FileSystemObjectStore(_directory));
        using var reopened = new FileSystemObjectStore(_directory);
        Assert.Equal(0, reopened.ObjectCount);
    }

    [Theory]
    [InlineData("{\"Version\":999,\"Objects\":[]}")]
    [InlineData("{truncated")]
    public void Unsupported_or_corrupt_stores_are_not_overwritten(string data)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "objects.json"), data);
        Assert.ThrowsAny<Exception>(() => new FileSystemObjectStore(_directory));
        Assert.Equal(data, File.ReadAllText(Path.Combine(_directory, "objects.json")));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
