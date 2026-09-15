using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class ObjectIdentityTests
{
    [Theory]
    [InlineData(false, "*LIBL", "*FILE")]
    [InlineData(true, "*LIBL", "*FILE")]
    [InlineData(false, "QGPL", "*PRTF")]
    [InlineData(true, "QGPL", "*PRTF")]
    public void Stores_reject_unresolved_libraries_and_file_attributes_as_object_types(bool sqlite, string library, string type)
    {
        using var factory = new SqliteConnectionFactory(":memory:");
        new Migrator(factory).MigrateToLatest();
        IObjectStore store = sqlite ? new SqliteObjectStore(factory) : new InMemoryObjectStore();
        Assert.Throws<ArgumentException>(() => store.Create(new ObjectDescriptor
        { Key = new QualifiedName(library, "OBJECT"), ObjectType = type }));
        Assert.Equal(0, store.ObjectCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_name_different_types_have_independent_lifecycles(bool sqlite)
    {
        using var factory = new SqliteConnectionFactory(":memory:");
        new Migrator(factory).MigrateToLatest();
        IObjectStore store = sqlite ? new SqliteObjectStore(factory) : new InMemoryObjectStore();
        var file = Descriptor(ObjectType.File, Authorities.UseBits);
        var program = Descriptor(ObjectType.Program, AuthorityBit.None);
        store.Create(file);
        store.Create(program);
        Assert.Equal(2, store.ObjectCount);
        program.Description = "program only";
        store.Update(program);
        Assert.Null(store.GetRequired("LIB", "SHARED", ObjectType.File).Description);
        Assert.Equal("program only", store.GetRequired("LIB", "SHARED", ObjectType.Program).Description);
        store.Delete("LIB", "SHARED", ObjectType.File);
        Assert.False(store.Exists("LIB", "SHARED", ObjectType.File));
        Assert.True(store.Exists("LIB", "SHARED", ObjectType.Program));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Authority_for_one_type_never_authorizes_another(bool sqlite)
    {
        using var factory = new SqliteConnectionFactory(":memory:");
        new Migrator(factory).MigrateToLatest();
        IObjectStore store = sqlite ? new SqliteObjectStore(factory) : new InMemoryObjectStore();
        store.Create(Descriptor(ObjectType.File, Authorities.UseBits));
        store.Create(Descriptor(ObjectType.Program, AuthorityBit.None));
        var authority = new AuthorityEngine(factory, store);
        var user = new UserProfile { Name = "READER" };
        Assert.True(authority.CanExecute(user, Array.Empty<string>(), "LIB", "SHARED", ObjectType.File, Authorities.UseBits));
        Assert.False(authority.CanExecute(user, Array.Empty<string>(), "LIB", "SHARED", ObjectType.Program, Authorities.UseBits));
        Assert.Equal(AuthorityBit.None, authority.EffectiveFor(user, Array.Empty<string>(), "LIB", "SHARED", ObjectType.Program));
        authority.Grant("LIB", "SHARED", ObjectType.Program, user.Name, Authorities.UseBits);
        Assert.True(authority.CanExecute(user, Array.Empty<string>(), "LIB", "SHARED", ObjectType.Program, Authorities.UseBits));
        authority.Revoke("LIB", "SHARED", ObjectType.Program, user.Name);
        Assert.False(authority.CanExecute(user, Array.Empty<string>(), "LIB", "SHARED", ObjectType.Program, Authorities.UseBits));
    }

    [Fact]
    public void All_object_authority_does_not_make_a_missing_object_exist()
    {
        using var factory = new SqliteConnectionFactory(":memory:");
        new Migrator(factory).MigrateToLatest();
        var engine = new AuthorityEngine(factory, new SqliteObjectStore(factory));
        var admin = new UserProfile { Name = "ADMIN", SpecialAuthorities = SpecialAuthority.AllObject };
        Assert.False(engine.CanExecute(admin, Array.Empty<string>(), "LIB", "MISSING", ObjectType.Program, Authorities.UseBits));
    }

    private static ObjectDescriptor Descriptor(string type, AuthorityBit bits) => new()
    {
        Key = new QualifiedName("LIB", "SHARED"),
        ObjectType = type,
        PublicAuthority = bits,
    };
}
