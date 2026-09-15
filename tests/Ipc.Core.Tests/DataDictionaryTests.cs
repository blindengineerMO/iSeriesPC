using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class DataDictionaryTests
{
    [Fact]
    public void Command_creates_dictionary_in_its_named_library_with_real_object_authority()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); system.Libraries.CreateLibrary("DICTIONARY");
        var service = new CommandService(system);
        var result = service.Execute("CRTDTADCT DICTIONARY TEXT('Test dictionary') AUT(*USE)"); Assert.False(result.IsError, result.Message);
        var dictionary = system.Objects.GetRequired("DICTIONARY", "DICTIONARY", ObjectType.DataDictionary);
        Assert.Equal("Test dictionary", dictionary.Description); Assert.Equal(Authorities.UseBits, dictionary.PublicAuthority);
        Assert.Contains("*DTADCT", string.Join(' ', service.Execute("DSPLIB LIB(DICTIONARY)").Listing!));
        Assert.Contains("CPF2F04", service.Execute("CRTDTADCT DICTIONARY").Message);
        Assert.True(service.Execute("CRTDTADCT MISSING").IsError);
        Assert.False(service.Execute("DLTLIB DICTIONARY").IsError);
    }
    [Fact]
    public void Authorization_list_attachment_is_atomic_and_uses_live_membership()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); system.Libraries.CreateLibrary("DICTIONARY");
        system.Security.Authority.AddAuthLMember("DICTAUTH", "QUSER", Authorities.UseBits);
        var store = new DataDictionaryStore(system.Connections); store.Create("DICTIONARY", authority: "DICTAUTH");
        using (OperationIdentity.Enter("QUSER")) Assert.NotNull(system.Objects.GetRequired("DICTIONARY", "DICTIONARY", ObjectType.DataDictionary));
        system.Security.Authority.RemoveAuthLMember("DICTAUTH", "QUSER");
        using (OperationIdentity.Enter("QUSER")) Assert.Throws<CpfException>(() => system.Objects.GetRequired("DICTIONARY", "DICTIONARY", ObjectType.DataDictionary));
        Assert.Throws<CpfException>(() => system.Objects.Delete("QSYS", "DICTAUTH", ObjectType.AuthorizationList));
        system.Libraries.CreateLibrary("BADDICT");
        Assert.Throws<CpfException>(() => store.Create("BADDICT", authority: "MISSING"));
        Assert.False(system.Objects.Exists("BADDICT", "BADDICT", ObjectType.DataDictionary));
    }
    [Fact]
    public void Dictionary_creation_requires_library_add_authority_and_valid_text()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); system.Libraries.CreateLibrary("DICTIONARY");
        var store = new DataDictionaryStore(system.Connections);
        using (OperationIdentity.Enter("QUSER")) Assert.Throws<CpfException>(() => store.Create("DICTIONARY"));
        Assert.Throws<CpfException>(() => store.Create("DICTIONARY", new string('x', 51)));
        store.Create("DICTIONARY", "*BLANK");
        Assert.Equal(Authorities.ChangeBits, system.Objects.GetRequired("DICTIONARY", "DICTIONARY", ObjectType.DataDictionary).PublicAuthority);
    }
}
