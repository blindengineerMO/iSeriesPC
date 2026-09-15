using System.Text;
using Ipc.Console.Session;
using Ipc.Core.Menu;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Dsp;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed class CustomMenuTests
{
    private static ApplicationMenu Menu(string target = "CHGCURLIB CURLIB(QUSRSYS)", string authority = "*JOBCTL") => new() {
        Name = "CUSTOM", Library = "QGPL", Title = "Custom tasks", Options = new[] {
            new MenuOption { Number = "1", Text = "Change library", Target = target, Kind = MenuOptionKind.Command, RequiredAuthority = authority },
            new MenuOption { Number = "90", Text = "Sign off", Target = "SIGNOFF", Kind = MenuOptionKind.SignOff } } };
    private static SessionEvent Command(MenuController controller, string text)
    { var parser = new TerminalParser(); foreach (var c in text) foreach (var key in parser.Feed(c)) controller.Handle(key); return controller.Handle(new(AidKey.Enter)); }
    [Fact]
    public void Json_and_store_reject_ambiguous_invalid_or_oversized_definitions_before_mutation()
    {
        var encoded = MenuDefinition.Serialize(Menu()); var menu = MenuDefinition.Parse(Encoding.UTF8.GetBytes(encoded)); Assert.Equal("*JOBCTL", menu.Find("1")!.RequiredAuthority);
        Assert.Throws<ArgumentException>(() => MenuDefinition.Parse(Encoding.UTF8.GetBytes(encoded.Replace("\"Name\":\"CUSTOM\"", "\"Name\":\"CUSTOM\",\"Name\":\"OTHER\""))));
        Assert.Throws<ArgumentException>(() => MenuDefinition.Parse(Encoding.UTF8.GetBytes(encoded.Replace("\"Title\"", "\"Unknown\""))));
        Assert.Throws<ArgumentException>(() => MenuDefinition.Parse(new byte[65537]));
        using var system = IpcSystem.Create(":memory:"); system.Start(); system.Menus.Register(menu);
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Menus.Register(Menu(authority: "*INVALID")));
        Assert.Throws<ArgumentException>(() => system.Menus.Register(Menu(target: "DSPJOB\u001b[2J")));
        Assert.Equal("*JOBCTL", system.Menus.Get("CUSTOM", "QGPL").Find("1")!.RequiredAuthority);
    }
    [Fact]
    public void Menu_option_authority_is_live_and_definition_changes_do_not_execute_cached_targets()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); system.Menus.Register(Menu());
        using var controller = new MenuController(system, system.Security.Profiles.Get("QUSER"), "QGPL/CUSTOM");
        Command(controller, "1"); Assert.Contains("CPF9802", controller.Buffer.RowText(24)); Assert.Equal("QGPL", system.Jobs.GetRequired(controller.JobKey).CurrentLibrary);
        var user = system.Security.Profiles.Get("QUSER"); user.SpecialAuthorities = SpecialAuthority.JobControl; system.Security.Profiles.Update(user);
        Command(controller, "1"); Assert.Equal("QUSRSYS", system.Jobs.GetRequired(controller.JobKey).CurrentLibrary);
        system.Menus.Register(Menu("CHGCURLIB CURLIB(QGPL)")); Command(controller, "1"); Assert.Equal("QGPL", system.Jobs.GetRequired(controller.JobKey).CurrentLibrary);
        user.SpecialAuthorities = SpecialAuthority.None; system.Security.Profiles.Update(user); Command(controller, "1"); Assert.Contains("CPF9802", controller.Buffer.RowText(24));
        system.Security.Authority.Grant("QGPL", "CUSTOM", ObjectType.Menu, "QUSER", AuthorityBit.None);
        var result = controller.Handle(new(AidKey.Pf1)); Assert.True(result.EndSession); Assert.Equal("Failed", result.State); Assert.DoesNotContain("Custom tasks", new AnsiRenderer().Render(controller.Buffer));
    }
    [Fact]
    public void Json_import_replace_copy_and_delete_use_catalog_authority_and_atomic_payloads()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var commands = new CommandService(system);
        var path = Path.Combine(Path.GetTempPath(), "ipc-menu-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, MenuDefinition.Serialize(Menu()));
            var command = "CRTMNU MENU(QGPL/CUSTOM) JSONFILE('" + path + "')";
            var result = commands.Execute(command); Assert.False(result.IsError, result.Message);
            Assert.True(commands.Execute(command).IsError);
            File.WriteAllText(path, MenuDefinition.Serialize(Menu("DSPJOB", "*NONE")));
            Assert.False(commands.Execute(command + " REPLACE(*YES)").IsError);
            Assert.Equal("DSPJOB", system.Menus.Get("CUSTOM", "QGPL").Find("1")!.Target);
            Assert.False(commands.Execute("CRTDUPOBJ OBJ(CUSTOM) FROMLIB(QGPL) OBJTYPE(*MENU) TOLIB(QGPL) NEWOBJ(COPY)").IsError);
            Assert.Equal("*NONE", system.Menus.Get("COPY", "QGPL").Find("1")!.RequiredAuthority);
            using (OperationIdentity.Enter("QUSER")) Assert.True(commands.Execute("DLTOBJ OBJ(QGPL/CUSTOM) OBJTYPE(*MENU)").IsError);
            Assert.False(commands.Execute("DLTMNU MENU(QGPL/CUSTOM)").IsError); Assert.Null(system.Menus.TryGet("CUSTOM", "QGPL"));
            Assert.NotNull(system.Menus.TryGet("COPY", "QGPL"));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void SDA_round_trip_retains_option_policy_and_invalid_changes_keep_the_draft()
    {
        var design = ScreenDesign.New("CUSTOM"); design.SetMenu(new("MENU", "Custom tasks", new[] { new DesignMenuOption("1", "Job", "DSPJOB", MenuOptionKind.Command, "*JOBCTL") }));
        var opened = ScreenDesign.Open("CUSTOM", design.Source()); Assert.Equal("*JOBCTL", opened.CompileMenu("QGPL", "CUSTOM").Find("1")!.RequiredAuthority);
        Assert.Throws<ArgumentOutOfRangeException>(() => opened.SetMenu(opened.Menu! with { Options = new[] { opened.Menu!.Options[0] with { RequiredAuthority = "*INVALID" } } }));
        Assert.Equal("*JOBCTL", opened.Menu!.Options[0].RequiredAuthority);
    }
    [Fact]
    public void Prompted_options_recheck_policy_before_execution_and_cannot_overwrite_without_replace()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        var definition = new ApplicationMenu { Name = "CUSTOM", Library = "QGPL", Options = new[] { new MenuOption { Number = "1", Text = "Change library", Target = "CHGCURLIB CURLIB(QUSRSYS)", Kind = MenuOptionKind.Prompt, RequiredAuthority = "*JOBCTL" } } };
        system.Menus.Register(definition);
        Assert.Throws<CpfException>(() => system.Menus.Register(Menu(), replace: false));
        var user = system.Security.Profiles.Get("QUSER"); user.SpecialAuthorities = SpecialAuthority.JobControl; system.Security.Profiles.Update(user);
        using var controller = new MenuController(system, user, "QGPL/CUSTOM"); Command(controller, "1"); Assert.Contains("Prompt command", controller.Buffer.RowText(1));
        user.SpecialAuthorities = SpecialAuthority.None; system.Security.Profiles.Update(user);
        controller.Handle(new(AidKey.Enter)); Assert.Contains("CPF9802", controller.Buffer.RowText(24)); Assert.Equal("QGPL", system.Jobs.GetRequired(controller.JobKey).CurrentLibrary);
        user.SpecialAuthorities = SpecialAuthority.JobControl; system.Security.Profiles.Update(user); system.Menus.Register(Menu("DSPJOB"));
        controller.Handle(new(AidKey.Enter)); Assert.Contains("Menu option changed", controller.Buffer.RowText(24));
    }

    [Fact]
    public void Recursive_menu_navigation_is_bounded_and_back_remains_available()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); system.Menus.Register(new ApplicationMenu { Name = "LOOP", Library = "QGPL", Options = new[] { new MenuOption { Number = "1", Text = "Again", Target = "QGPL/LOOP" } } });
        using var controller = new MenuController(system, system.Security.Profiles.Get("QUSER"), "QGPL/LOOP");
        for (var i = 0; i < 65; i++) Command(controller, "1");
        Assert.Contains("nesting limit", controller.Buffer.RowText(24)); Assert.False(controller.Handle(new(AidKey.Pf12)).EndSession);
    }
}
