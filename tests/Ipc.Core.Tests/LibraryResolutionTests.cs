using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Services;

namespace Ipc.Core.Tests;

public sealed class LibraryResolutionTests
{
    [Fact]
    public void Library_commands_persist_only_the_calling_jobs_context_and_drive_file_creation()
    {
        using var system = IpcSystem.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), ":memory:");
        system.Start();
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "ADMIN1234");
        system.Libraries.CreateLibrary("WORK");
        foreach (var user in new[] { "FIRST", "SECOND" })
        {
            system.Security.Profiles.Create(new Ipc.Core.Security.UserProfile { Name = user });
            system.Security.Authority.Grant("QSYS", "WORK", ObjectType.Library, user, Authorities.ChangeBits);
            system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, user, Authorities.ChangeBits);
        }
        var first = system.Jobs.CreateInteractive("FIRST");
        var second = system.Jobs.CreateInteractive("SECOND");
        var commands = new CommandService(system, first);
        Assert.False(commands.Execute("CHGCURLIB CURLIB(WORK)").IsError);
        Assert.False(commands.Execute("CHGLIBL LIBL(WORK QGPL)").IsError);
        Assert.False(commands.Execute("CRTSRCPF FILE(SRC)").IsError);
        Assert.True(system.Objects.Exists("WORK", "SRC", ObjectType.File));
        Assert.Equal("FIRST", system.Objects.GetRequired("WORK", "SRC", ObjectType.File).Owner);
        Assert.False(new CommandService(system, second).Execute("CRTDUPOBJ OBJ(SRC) FROMLIB(WORK) OBJTYPE(*FILE) TOLIB(QGPL) NEWOBJ(COPY)").IsError);
        Assert.Equal("SECOND", system.Objects.GetRequired("QGPL", "COPY", ObjectType.File).Owner);
        Assert.Equal("WORK", system.Jobs.GetRequired(first.Key).CurrentLibrary);
        Assert.Equal("QGPL", system.Jobs.GetRequired(second.Key).CurrentLibrary);
        Assert.True(commands.Execute("CHGCURLIB CURLIB(MISSING)").IsError);
        Assert.Equal("WORK", system.Jobs.GetRequired(first.Key).CurrentLibrary);
    }

    [Fact]
    public void Calls_follow_job_library_order_and_do_not_reuse_deleted_or_replaced_programs()
    {
        using var system = IpcSystem.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), ":memory:");
        system.Start();
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "ADMIN1234");
        foreach (var library in new[] { "FIRST", "SECOND", "HIDDEN" })
        {
            system.Libraries.CreateLibrary(library);
            system.Objects.Create(new ObjectDescriptor
            {
                Key = new QualifiedName(library, "HELLO"), ObjectType = ObjectType.Program,
                Source = $"PGM\nSNDPGMMSG MSG('{library}')\nENDPGM",
            });
        }
        var job = system.Jobs.CreateInteractive("QSECOFR", currentLibrary: "FIRST", libraryList: "SECOND");
        var commands = new CommandService(system, job);
        Assert.Equal("FIRST", commands.Execute("CALL HELLO").Message);
        system.Objects.Delete("FIRST", "HELLO", ObjectType.Program);
        Assert.Equal("SECOND", commands.Execute("CALL HELLO").Message);
        var replacement = system.Objects.GetRequired("SECOND", "HELLO", ObjectType.Program);
        replacement.Source = "PGM\nSNDPGMMSG MSG('REPLACED')\nENDPGM";
        system.Objects.Update(replacement);
        Assert.Equal("REPLACED", commands.Execute("CALL HELLO").Message);
        system.Objects.Delete("SECOND", "HELLO", ObjectType.Program);
        Assert.True(commands.Execute("CALL HELLO").IsError);
        Assert.Equal("HIDDEN", commands.Execute("CALL HIDDEN/HELLO").Message);
    }

    [Fact]
    public void Menus_follow_current_library_and_never_scan_unlisted_libraries()
    {
        using var system = IpcSystem.Create(":memory:");
        system.Start();
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "ADMIN1234");
        foreach (var library in new[] { "FIRST", "SECOND", "HIDDEN" })
        {
            system.Libraries.CreateLibrary(library);
            system.Menus.Register(new Ipc.Core.Menu.ApplicationMenu { Library = library, Name = "CUSTOM", Title = library });
        }
        var job = system.Jobs.CreateInteractive("QSECOFR", currentLibrary: "FIRST", libraryList: "SECOND");
        var commands = new CommandService(system, job);
        Assert.Equal("FIRST", commands.Execute("GO CUSTOM").MenuLibrary);
        system.Objects.Delete("FIRST", "CUSTOM", ObjectType.Menu);
        Assert.Equal("SECOND", commands.Execute("GO CUSTOM").MenuLibrary);
        system.Objects.Delete("SECOND", "CUSTOM", ObjectType.Menu);
        Assert.True(commands.Execute("GO CUSTOM").IsError);
        Assert.Equal("HIDDEN", commands.Execute("GO HIDDEN/CUSTOM").MenuLibrary);
        Assert.Null(system.Menus.TryGet("CUSTOM"));
        var profile = system.Security.Profiles.Get("QSECOFR");
        profile.InitialCurrentLibrary = "HIDDEN";
        using var controller = new MenuController(system, profile, "CUSTOM");
        Assert.Equal("HIDDEN/CUSTOM", controller.CurrentMenu);
    }

    [Fact]
    public void Sqlite_generic_names_treat_underscores_as_literal_characters()
    {
        using var system = IpcSystem.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), ":memory:");
        system.Start();
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "ADMIN1234");
        foreach (var name in new[] { "A_ONE", "AXONE" })
            system.Objects.Create(new ObjectDescriptor { Key = new QualifiedName("QGPL", name), ObjectType = ObjectType.Program });
        Assert.Equal("A_ONE", Assert.Single(system.Objects.Find("QGPL", "A_*", null, null)).Name);
    }
}
