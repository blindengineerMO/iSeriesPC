using Ipc.Cl.Parsing;
using Ipc.Console.Session;
using Ipc.Core.Menu;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Dsp;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed class WorkWithTests
{
    private static void Type(Action<KeyPress> handle, string text)
    { var parser = new TerminalParser(); foreach (var c in text) foreach (var key in parser.Feed(c)) handle(key); }
    [Fact]
    public void Pages_preserve_options_and_require_explicit_confirmation()
    {
        var rows = Enumerable.Range(0, 36).Select(i => new WorkRow("row" + i, "Item " + i,
            new[] { new WorkAction("4", "Delete", "DELETE" + i, Confirm: true) })).ToArray();
        var screen = new WorkWithSession(new("Items", rows, "REFRESH"));
        Type(k => screen.Handle(k), "4"); screen.Handle(new(AidKey.RollUp));
        Assert.Contains("Page 2/3", screen.Buffer.RowText(2)); Assert.Contains("Item 15", screen.Buffer.RowText(5));
        Assert.Null(screen.Handle(new(AidKey.Enter)).Action); Assert.Contains("DELETE0", screen.Buffer.RowText(5));
        screen.Handle(new(AidKey.Pf12)); screen.Handle(new(AidKey.Enter));
        var action = screen.Handle(new(AidKey.Pf6)).Action; Assert.Equal("DELETE0", action!.Command); Assert.False(action.Confirm);
        screen.Completed("Deleted"); screen.Replace(new("Items", rows.Skip(1).ToArray(), "REFRESH"));
        Assert.Null(screen.Handle(new(AidKey.Enter)).Action);
        Assert.True(screen.Handle(new(AidKey.Pf5)).Refresh);
        screen.Handle(new(AidKey.Pf9)); Type(k => screen.Handle(k), "CRTLIB LIB(TEST)");
        action = screen.Handle(new(AidKey.Pf4)).Action; Assert.True(action!.Prompt); Assert.Equal("CRTLIB LIB(TEST)", action.Command);
    }
    [Fact]
    public void Multiple_rows_and_invalid_options_are_rejected_and_refresh_removes_stale_selection()
    {
        var rows = new[] { new WorkRow("a", "A", new[] { new WorkAction("5", "Display", "DSPJOB") }), new WorkRow("b", "B", new[] { new WorkAction("5", "Display", "DSPJOB") }) };
        var screen = new WorkWithSession(new("Items", rows));
        Type(k => screen.Handle(k), "5\t5"); Assert.Null(screen.Handle(new(AidKey.Enter)).Action); Assert.Contains("one action", screen.Buffer.RowText(24));
        screen.Replace(new("Items", rows.Skip(1).ToArray())); Assert.Equal("DSPJOB", screen.Handle(new(AidKey.Enter)).Action!.Command);
        screen.Completed(null); Type(k => screen.Handle(k), "9"); Assert.Null(screen.Handle(new(AidKey.Enter)).Action); Assert.Contains("not available", screen.Buffer.RowText(24));
    }
    [Fact]
    public void Canonical_menu_routes_resolve_and_every_command_has_prompt_metadata()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var commands = new CommandService(system);
        foreach (var name in commands.AvailableCommands) Assert.Equal(name, commands.CommandMetadata(name).Name);
        foreach (var menu in SystemMenus.All()) foreach (var option in menu.Options)
        {
            if (option.Kind == MenuOptionKind.SubMenu) Assert.NotNull(system.Menus.TryGet(option.Target));
            else if (option.Kind is MenuOptionKind.Command or MenuOptionKind.Prompt)
                Assert.Contains(CommandParser.Parse(option.Target).Name, commands.AvailableCommands);
        }
        Assert.Equal("INFOAST", system.Menus.Get("MAIN").Find("10")!.Target);
        Assert.Equal("CLIENT", system.Menus.Get("MAIN").Find("11")!.Target);
        Assert.Equal("SLTCMD", system.Menus.Get("MAJOR").Find("1")!.Target);
        var selected = commands.Execute("SLTCMD CMD(*USRPRF*)"); Assert.False(selected.IsError, selected.Message);
        Assert.Contains(selected.WorkList!.Rows, r => r.Key == "CRTUSRPRF"); Assert.All(selected.WorkList.Rows, r => Assert.Contains("USRPRF", r.Key));
        Assert.NotNull(commands.Execute("WRKSYSSTS").WorkList);
    }
    [Fact]
    public void Message_screen_displays_full_text_prompts_reply_and_confirms_removal()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "AdminPassword22");
        var commands = new CommandService(system);
        Assert.False(commands.Execute("CRTMSGQ QGPL/INBOX").IsError); Assert.False(commands.Execute("CRTMSGQ QGPL/REPLIES").IsError);
        var text = new string('A', 100) + " END OF MESSAGE";
        Assert.False(commands.Execute("SNDMSG MSG('" + text + "') TOMSGQ(QGPL/INBOX) MSGTYPE(*INQ) RPYMSGQ(QGPL/REPLIES)").IsError);
        using var menu = new MenuController(system, system.Security.Profiles.Get("QSECOFR"));
        void Input(string value) => Type(k => menu.Handle(k), value);
        Input("WRKMSGQ MSGQ(QGPL/INBOX)"); menu.Handle(new(AidKey.Enter)); Input("8"); menu.Handle(new(AidKey.Enter));
        Assert.Contains("Messages for QGPL/INBOX", menu.Buffer.RowText(1));
        Input("5"); menu.Handle(new(AidKey.Enter));
        Assert.Contains("END OF MESSAGE", string.Join('\n', Enumerable.Range(1, 24).Select(menu.Buffer.RowText)));
        menu.Handle(new(AidKey.Pf3));
        Input("2"); menu.Handle(new(AidKey.Enter)); Assert.Contains("Prompt command - SNDRPY", menu.Buffer.RowText(1));
        Input("\t\tOwner's reply"); menu.Handle(new(AidKey.Enter));
        Assert.Contains("Messages for QGPL/INBOX", menu.Buffer.RowText(1)); Assert.Contains("0 row(s)", menu.Buffer.RowText(2));
        var store = new Ipc.Services.Messages.MessageQueueStore(system.Connections);
        Assert.Equal("Owner's reply", Assert.Single(store.List(new("QGPL", "REPLIES"))).Data.ToText());
        store.Send(new[] { new QualifiedName("QGPL", "INBOX") }, new Ipc.Core.Work.ProgramBuffer(new byte[] { 255, 0 }, 1208));
        menu.Handle(new(AidKey.Pf5)); Assert.Contains("bytes FF00", menu.Buffer.RowText(5));
        Input("4"); menu.Handle(new(AidKey.Enter)); Assert.Single(store.List(new("QGPL", "INBOX")));
        menu.Handle(new(AidKey.Pf12)); Assert.Single(store.List(new("QGPL", "INBOX")));
        menu.Handle(new(AidKey.Enter)); menu.Handle(new(AidKey.Pf6)); Assert.Empty(store.List(new("QGPL", "INBOX")));
        Assert.Contains("0 row(s)", menu.Buffer.RowText(2));
    }
    [Fact]
    public void Legacy_defaults_upgrade_but_custom_menu_edits_survive_reseeding()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        system.Menus.Register(SystemMenus.LegacyMain()); system.Menus.Register(new ApplicationMenu { Name = "MAJOR", Title = "My command groups" });
        system.Menus.SeedDefaults(); Assert.Equal("AS/400 Main Menu", system.Menus.Get("MAIN").Title); Assert.Equal("My command groups", system.Menus.Get("MAJOR").Title);
    }
    [Fact]
    public void Profile_administration_is_atomic_preserves_credentials_and_enforces_live_authority()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var commands = new CommandService(system);
        var result = commands.Execute("CRTUSRPRF USRPRF(OPERATOR) USRCLS(*USER) TEXT('Night operator')"); Assert.False(result.IsError, result.Message);
        var profile = system.Security.Profiles.Get("OPERATOR"); Assert.Null(profile.PasswordHash);
        var stale = system.Security.Profiles.Get("OPERATOR"); system.Security.Profiles.SetPassword(profile, "OperatorPassword22"); var hash = system.Security.Profiles.Get("OPERATOR").PasswordHash;
        stale.Description = "New description"; system.Security.Profiles.SaveSettings(stale, create: false);
        Assert.Equal(hash, system.Security.Profiles.Get("OPERATOR").PasswordHash);
        result = commands.Execute("CHGUSRPRF USRPRF(OPERATOR) TEXT('Invalid edit') CCSID(999)"); Assert.True(result.IsError);
        Assert.Equal("New description", system.Security.Profiles.Get("OPERATOR").Description);
        Assert.Single(commands.Execute("WRKUSRPRF USRPRF(OPERATOR)").WorkList!.Rows);
        Assert.Empty(commands.Execute("WRKUSRPRF USRPRF(OPER)").WorkList!.Rows);
        using (OperationIdentity.Enter("QUSER")) { Assert.True(commands.Execute("DLTUSRPRF USRPRF(OPERATOR)").IsError); Assert.True(commands.Execute("WRKUSRPRF").IsError); }
        Assert.False(commands.Execute("CHGUSRPRF USRPRF(OPERATOR) PASSWORD(*NONE)").IsError); Assert.Null(system.Security.Profiles.Get("OPERATOR").PasswordHash);
        Assert.False(commands.Execute("DLTUSRPRF USRPRF(OPERATOR)").IsError); Assert.Null(system.Security.Profiles.TryGet("OPERATOR"));
    }
    [Fact]
    public void Work_screen_actions_refresh_after_delete_and_F4_chooser_returns_to_original_menu()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "AdminPassword22");
        var commands = new CommandService(system); Assert.False(commands.Execute("CRTLIB LIB(DELETE_ME)").IsError);
        using var menu = new MenuController(system, system.Security.Profiles.Get("QSECOFR"));
        Type(k => menu.Handle(k), "WRKLIB LIB(DELETE_ME)"); menu.Handle(new(AidKey.Enter)); Assert.Contains("Work with libraries", menu.Buffer.RowText(1));
        Type(k => menu.Handle(k), "4"); menu.Handle(new(AidKey.Enter)); Assert.True(system.Objects.Exists("QSYS", "DELETE_ME", ObjectType.Library));
        menu.Handle(new(AidKey.Pf6)); Assert.False(system.Objects.Exists("QSYS", "DELETE_ME", ObjectType.Library)); Assert.Contains("0 row(s)", menu.Buffer.RowText(2));
        menu.Handle(new(AidKey.Pf3)); menu.Handle(new(AidKey.Pf4)); Assert.Contains("Select a command", menu.Buffer.RowText(1));
        Type(k => menu.Handle(k), "1"); menu.Handle(new(AidKey.Enter)); Assert.Contains("Prompt command", menu.Buffer.RowText(1));
        menu.Handle(new(AidKey.Pf12)); Assert.Contains("Select a command", menu.Buffer.RowText(1)); menu.Handle(new(AidKey.Pf3)); Assert.Contains("AS/400 Main Menu", menu.Buffer.RowText(1));
    }
}
