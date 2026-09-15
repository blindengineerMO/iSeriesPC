using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Menu;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Dsp;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed class ScreenDesignerTests
{
    [Fact]
    public void Design_edits_roundtrip_through_DDS_with_windows_subfiles_and_indicators()
    {
        var design = ScreenDesign.New("CUSTOMERS"); design.AddRecord("DETAIL", "Customer");
        design.PutField("DETAIL", null, new() { Name = "NAME", Length = 10, Usage = PanelFieldUsage.Both, Row = 4, Column = 2,
            Conditions = new[] { new PanelCondition(21) }, Keywords = new() { ScreenDesign.Keyword("DSPATR", "RI") } });
        design.SetWindow("DETAIL", 3, 10, 8, 30); design.AddSubfile("ROWS", "CONTROL", 2, 10, 4); design.SetWindow("CONTROL", 3, 10, 10, 40);
        var source = design.Source(); var reopened = ScreenDesign.Open("CUSTOMERS", source); Assert.False(reopened.Dirty); Assert.Equal(source, reopened.Source());
        var file = new DisplayFileSession(reopened.Definition); var indicators = new bool[100]; indicators[21] = true;
        file.Write("DETAIL", new Dictionary<string, object?> { ["NAME"] = "Alice" }, indicators);
        Assert.Equal('A', file.Buffer[7, 13].Value); Assert.True(file.Buffer[7, 13].Attributes.HasFlag(DisplayAttribute.ReverseVideo));
        file.Handle(new(AidKey.None, Character: 'X')); Assert.Equal("XAlice", file.Handle(new(AidKey.Enter)).Values["NAME"]);
        reopened.RenameRecord("ROWS", "NEWROWS"); Assert.Equal("NEWROWS", reopened.Definition.Records.Single(r => r.Name == "CONTROL").Keywords.Single(k => k.Name == "SFLCTL").Arguments[0]);
        Assert.Throws<PanelCompileException>(() => reopened.RemoveRecord("NEWROWS"));
        var before = reopened.Source(); Assert.Throws<ArgumentException>(() => reopened.PutField("DETAIL", null, new() { Name = "BAD", Length = 5, Row = 1000, Column = 2 })); Assert.Equal(before, reopened.Source());
        Assert.Throws<PanelCompileException>(() => reopened.SetWindow("DETAIL", 23, 70, 8, 30)); Assert.Equal(before, reopened.Source());
    }
    [Fact]
    public void Menu_options_are_persisted_in_DDS_comments_and_compiled_to_real_menu_actions()
    {
        var design = ScreenDesign.New("APP"); design.SetMenu(new("MENU", "Application", new[] { new DesignMenuOption("1", "Job details", "DSPJOB", MenuOptionKind.Command), new DesignMenuOption("90", "Sign off", "SIGNOFF", MenuOptionKind.SignOff) }));
        var source = design.Source(); var reopened = ScreenDesign.Open("APP", source); var menu = reopened.CompileMenu("QGPL", "APP");
        Assert.Equal("DSPJOB", menu.Options[0].Target); Assert.Equal(MenuOptionKind.SignOff, menu.Options[1].Kind);
        Assert.Throws<ArgumentException>(() => ScreenDesign.Open("APP", source.Replace("DFT('Application')", "DFT('Changed')")));
        Assert.Throws<ArgumentException>(() => reopened.RemoveField("MENU", "SELECTION"));
        using var system = IpcSystem.Create(":memory:"); system.Start(); system.Menus.Register(menu, "QSECOFR", source);
        using var controller = new MenuController(system, system.Security.Profiles.Get("QUSER"), "QGPL/APP");
        Assert.Contains("Job details", new AnsiRenderer().Render(controller.Buffer)); controller.Handle(new(AidKey.None, Character: '1'));
        Assert.False(controller.Handle(new(AidKey.Enter)).EndSession); Assert.Contains("is active", controller.Buffer.RowText(24));
    }
    [Fact]
    public void Source_save_is_atomic_revision_checked_width_bounded_and_audited_without_source_text()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects); files.CreateSourceFile("QGPL", "SOURCE");
        var original = files.ReadSourceMember("QGPL", "SOURCE", "NEW"); Assert.Null(original.Revision);
        var saved = files.SaveSourceMember("QGPL", "SOURCE", "NEW", "private-source\n\nlast\n", original.Revision);
        Assert.Equal(saved, files.ReadSourceMember("QGPL", "SOURCE", "NEW"));
        var next = files.SaveSourceMember("QGPL", "SOURCE", "NEW", "new content\n", saved.Revision);
        var conflict = Assert.Throws<CpfException>(() => files.SaveSourceMember("QGPL", "SOURCE", "NEW", "stale\n", saved.Revision)); Assert.Contains("IPC0130", conflict.Message);
        Assert.Equal(next, files.ReadSourceMember("QGPL", "SOURCE", "NEW"));
        Assert.Throws<CpfException>(() => files.SaveSourceMember("QGPL", "SOURCE", "WIDE", new string('x', 101), null)); Assert.False(files.MemberExists("QGPL", "SOURCE", "WIDE"));
        using var connection = system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM sys_events WHERE kind='source.saved'";
        using (var reader = command.ExecuteReader()) { Assert.True(reader.Read()); Assert.DoesNotContain("private-source", reader.GetString(0)); }
        command.CommandText = "CREATE TRIGGER fail_source_save BEFORE INSERT ON sys_events WHEN NEW.kind='source.saved' BEGIN SELECT RAISE(ABORT,'test failure'); END"; command.ExecuteNonQuery();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => files.SaveSourceMember("QGPL", "SOURCE", "ROLLBACK", "text\n", null)); Assert.False(files.MemberExists("QGPL", "SOURCE", "ROLLBACK"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => files.SaveSourceMember("QGPL", "SOURCE", "NEW", "rejected\n", next.Revision)); Assert.Equal(next, files.ReadSourceMember("QGPL", "SOURCE", "NEW"));
    }
    [Fact]
    public void Source_snapshots_respect_record_allocations_and_save_requires_change_authority()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects); files.CreateSourceFile("QGPL", "SOURCE");
        var saved = files.SaveSourceMember("QGPL", "SOURCE", "SOURCE", "line\n", files.ReadSourceMember("QGPL", "SOURCE", "SOURCE").Revision);
        var first = system.Jobs.CreateInteractive("QUSER"); var second = system.Jobs.CreateInteractive("QUSER"); var locks = new JobLockStore(system.Connections);
        using (OperationIdentity.Enter("QUSER", first.Key))
        using (locks.EnterCommand(first.Key))
        {
            Assert.Equal(saved, files.ReadSourceMember("QGPL", "SOURCE", "SOURCE"));
            using (OperationIdentity.Enter("QUSER", second.Key))
                Assert.Throws<CpfException>(() => locks.Acquire(second.Key, new("QGPL", "SOURCE", ObjectType.File, "SOURCE", 1), JobLockMode.Exclusive, TimeSpan.Zero));
            Assert.Throws<CpfException>(() => files.SaveSourceMember("QGPL", "SOURCE", "SOURCE", "denied\n", saved.Revision));
        }
        using (OperationIdentity.Enter("QUSER", second.Key))
        using (locks.Acquire(second.Key, new("QGPL", "SOURCE", ObjectType.File, "SOURCE", 1), JobLockMode.Exclusive, TimeSpan.Zero))
        using (OperationIdentity.Enter("QUSER", first.Key))
        using (locks.EnterCommand(first.Key)) Assert.Throws<CpfException>(() => files.ReadSourceMember("QGPL", "SOURCE", "SOURCE"));
    }
    [Fact]
    public void Keyboard_designer_adds_fields_previews_saves_compiles_and_preserves_failed_drafts()
    {
        string? saved = null; DesignCompileRequest? compiled = null;
        var design = ScreenDesign.New("ENTRY"); var session = new ScreenDesignerSession(design, "QGPL/SOURCE(ENTRY)", "QGPL", source => saved = source, request => compiled = request);
        session.Handle(new(AidKey.Enter)); session.Handle(new(AidKey.Pf6));
        Fill(session.Handle, "CUSTOMER", "A", "B", "10", "0", "4", "2", "Alice", "RI", "");
        Assert.Contains(design.Definition.Records[0].Fields, f => f.Name == "CUSTOMER");
        session.Handle(new(AidKey.Pf4)); Assert.Contains("Alice", session.Buffer.RowText(4)); session.Handle(new(AidKey.Pf3));
        session.Handle(new(AidKey.Pf2)); Assert.NotNull(saved); Assert.False(design.Dirty);
        session.Handle(new(AidKey.Pf5)); Fill(session.Handle, "QGPL", "COMPILED", "DSPF", "YES"); Assert.Equal(new("QGPL", "COMPILED", false, true), compiled);
        Assert.True(session.Handle(new(AidKey.Pf3)));
        var failing = new ScreenDesignerSession(ScreenDesign.New("FAILURE"), "QGPL/SOURCE(FAILURE)", "QGPL", _ => throw new CpfException("IPC0130", "Changed"), _ => { });
        Assert.False(failing.Handle(new(AidKey.Pf2))); Assert.True(failing.Design.Dirty); Assert.Contains("IPC0130", failing.Buffer.RowText(24));
        Assert.False(failing.Handle(new(AidKey.Pf3))); Assert.True(failing.Handle(new(AidKey.Pf12)));
    }
    [Fact]
    public void Keyboard_designer_creates_window_subfiles_and_rejects_invalid_menu_targets()
    {
        var design = ScreenDesign.New("DESIGN");
        var session = new ScreenDesignerSession(design, "QGPL/SOURCE(DESIGN)", "QGPL", _ => { }, _ => { });
        session.Handle(new(AidKey.Pf8)); Fill(session.Handle, "3", "10", "10", "40");
        Assert.Contains(design.Definition.Records[0].Keywords, k => k.Name == "WINDOW");
        session.Handle(new(AidKey.Pf9)); Fill(session.Handle, "ROWS", "CONTROL", "2", "10", "5");
        Assert.Contains(design.Definition.Records, r => r.Name == "ROWS");
        session.Handle(new(AidKey.None, Character: '3')); session.Handle(new(AidKey.Pf4));
        Assert.Contains("Subfile ROWS", new AnsiRenderer().Render(session.Buffer));
        session.Handle(new(AidKey.Pf3));
        design.SetMenu(new("menu", "Application", Array.Empty<DesignMenuOption>())); Assert.Equal("MENU", design.Menu!.Record);
        var source = design.Source();
        foreach (var option in new[] { new DesignMenuOption("1", "Missing", "", MenuOptionKind.Command), new DesignMenuOption("01", "Ambiguous", "DSPJOB", MenuOptionKind.Command) })
            Assert.Throws<ArgumentException>(() => design.SetMenu(design.Menu with { Options = new[] { option } }));
        Assert.Equal(source, design.Source());
    }
    internal static void Fill(Func<KeyPress, bool> handle, params string[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            handle(new(AidKey.None, Edit: CursorEdit.Home)); handle(new(AidKey.None, Edit: CursorEdit.FieldEraseToEnd));
            foreach (var c in values[i]) handle(new(AidKey.None, Character: c));
            if (i < values.Length - 1) handle(new(AidKey.None, Edit: CursorEdit.NextField));
        }
        handle(new(AidKey.Enter));
    }
    [Fact]
    public void STRSDA_saves_compiles_reopens_edits_replaces_and_runs_the_compiled_panel()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "DesignerPassword22");
        var files = new SqliteFileStore(system.Connections, system.Objects); files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        using var menu = new MenuController(system, system.Security.Profiles.Get("QSECOFR"));
        void Command(string command) { foreach (var c in command) menu.Handle(new(AidKey.None, Character: c)); menu.Handle(new(AidKey.Enter)); }
        bool Handle(KeyPress key) { menu.Handle(key); return false; }
        Command("STRSDA SRCFILE(QGPL/SOURCE) SRCMBR(ENTRY)"); Assert.Contains("Screen Design Aid", menu.Buffer.RowText(1));
        menu.Handle(new(AidKey.Enter)); menu.Handle(new(AidKey.Pf6)); Fill(Handle, "CUSTOMER", "A", "B", "10", "0", "4", "2", "Alice", "RI", "");
        menu.Handle(new(AidKey.Pf5)); Fill(Handle, "QGPL", "ENTRY", "DSPF", "NO"); Assert.True(system.Objects.Exists("QGPL", "ENTRY", ObjectType.File));
        menu.Handle(new(AidKey.Pf3)); Command("STRSDA SRCFILE(QGPL/SOURCE) SRCMBR(ENTRY)"); menu.Handle(new(AidKey.Enter));
        menu.Handle(new(AidKey.None, Character: '2')); menu.Handle(new(AidKey.Pf7)); Fill(Handle, "CUSTOMER", "A", "B", "10", "0", "4", "2", "Saved", "RI", "");
        menu.Handle(new(AidKey.Pf5)); Fill(Handle, "QGPL", "ENTRY", "DSPF", "YES"); menu.Handle(new(AidKey.Pf3));
        Command("RUNPNL FILE(QGPL/ENTRY)"); Assert.Contains("Saved", menu.Buffer.RowText(4)); menu.Handle(new(AidKey.Pf3));
        Command("STRSDA SRCFILE(QGPL/SOURCE) SRCMBR(APP)"); menu.Handle(new(AidKey.Pf10)); Fill(Handle, "MENU", "Application");
        menu.Handle(new(AidKey.Pf6)); Fill(Handle, "1", "Job details", "Command", "DSPJOB");
        menu.Handle(new(AidKey.Pf5)); Fill(Handle, "QGPL", "APP", "MENU", "NO"); Assert.Equal("DSPJOB", system.Menus.Get("APP", "QGPL").Options[0].Target);
    }
}
