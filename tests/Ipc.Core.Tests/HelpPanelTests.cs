using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Dsp;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Session;
using Ipc.Terminal;
using static Ipc.Core.Tests.DisplayPanelTests;

namespace Ipc.Core.Tests;

public sealed class HelpPanelTests
{
    private const string Source = """
        :PNLGRP.
        .* A UIM source comment.
        :HELP NAME=GENERAL.General help
        :P.Read the :HP2.shared help:EHP2. and select the
        :LINK PERFORM='DSPHELP FIELD'.field description:ELINK..
        :EHELP.
        :HELP NAME=FIELD.Field help
        :ISCH ROOTS='customer entry'.
        :XH3.Customer name
        :P.Enter the customer's name. Use &colon. for a literal colon.
        :EHELP.
        :EPNLGRP.
        """;
    [Fact]
    public void UIM_compilation_preserves_modules_styles_links_index_words_and_source_diagnostics()
    {
        var definition = new UimCompiler().Compile("HELPS", Source, "QPNLSRC/HELPS");
        Assert.Equal(2, definition.Modules.Count); Assert.Equal("General help", definition.Modules[0].Title);
        Assert.Contains(definition.Modules[0].Blocks.SelectMany(b => b.Spans), s => s.Style == "HP2" && s.Text == "shared help");
        Assert.Contains("CUSTOMER", definition.Modules[1].IndexWords);
        Assert.Equal(definition.ToJson(), HelpDefinition.FromJson(definition.ToJson()).ToJson());
        var error = Assert.Throws<UimCompileException>(() => new UimCompiler().Compile("BAD", ":PNLGRP.\n:HELP NAME=X.Title\n:RUN CMD='DLTLIB QGPL'.\n:EHELP.\n:EPNLGRP.", "BADMBR"));
        Assert.Equal(3, error.Line); Assert.Equal(1, error.Column); Assert.Equal("RUN", error.Token); Assert.Contains("BADMBR:3:1", error.Message);
    }
    [Theory]
    [InlineData(":PNLGRP.:HELP NAME=X.Title:P.:LINK PERFORM='CALL PGM(BAD)'.x:ELINK.:EHELP.:EPNLGRP.")]
    [InlineData(":PNLGRP.:HELP NAME=X.Title:P.:LINK PERFORM='DSPHELP MISSING'.x:ELINK.:EHELP.:EPNLGRP.")]
    [InlineData(":PNLGRP.:HELP NAME=X.Title:P.:HP1.x:EHP2.:EHELP.:EPNLGRP.")]
    [InlineData(":PNLGRP.:HELP NAME='X.Title:P.x:EHELP.:EPNLGRP.")]
    [InlineData(":PNLGRP.:HELP NAME=X.Title:P.x:EHELP.:HELP NAME=X.Other:EHELP.:EPNLGRP.")]
    [InlineData(":PNLGRP.:HELP NAME=X.Title:P.\u001b[2J:EHELP.:EPNLGRP.")]
    public void Unsafe_unknown_or_inconsistent_UIM_fails_compilation(string source) => Assert.Throws<UimCompileException>(() => new UimCompiler().Compile("BAD", source));
    [Fact]
    public void Viewer_follows_links_returns_back_searches_index_and_wraps_long_text()
    {
        var definition = new UimCompiler().Compile("HELPS", Source); var viewer = new HelpSession("QGPL/HELPS", "GENERAL", _ => definition);
        viewer.Handle(new(AidKey.None, Edit: CursorEdit.NextField)); Assert.Contains(viewer.Buffer.Positions(), p => viewer.Buffer[p.Row, p.Column].Attributes.HasFlag(DisplayAttribute.ReverseVideo));
        viewer.Handle(new(AidKey.Enter)); Assert.Equal("FIELD", viewer.CurrentModule);
        viewer.Handle(new(AidKey.Pf12)); Assert.Equal("GENERAL", viewer.CurrentModule);
        viewer.Handle(new(AidKey.Pf5)); foreach (var c in "customer") viewer.Handle(new(AidKey.None, Character: c));
        Assert.Contains("Field help", new AnsiRenderer().Render(viewer.Buffer)); viewer.Handle(new(AidKey.Enter)); Assert.Equal("FIELD", viewer.CurrentModule);
        Assert.True(viewer.Handle(new(AidKey.Pf3)));
        definition.Modules[0].Blocks.Add(new() { Spans = new() { new(string.Join(' ', Enumerable.Repeat("A long help paragraph.", 250))) } });
        viewer = new("QGPL/HELPS", "GENERAL", _ => definition); viewer.Handle(new(AidKey.RollUp)); Assert.Contains("Page 2", viewer.Buffer.RowText(22));
    }
    [Fact]
    public void Compiler_selects_conditioned_DDS_help_areas_and_file_fallback()
    {
        var source = DisplaySource(); var definition = new DisplayDdsCompiler().Compile("ENTRY", source);
        var panel = new PanelSession(definition); var flags = new bool[100]; flags[11] = true;
        panel.Write("ENTRY", new Dictionary<string, object?>(), flags);
        var field = panel.Handle(new(AidKey.Pf1)); Assert.Equal("FIELD", field.Help!.Module); Assert.Equal("QGPL/HELPS", field.Help.PanelGroup);
        flags[11] = false; panel.Write("ENTRY", new Dictionary<string, object?>(), flags);
        Assert.Equal("GENERAL", panel.Handle(new(AidKey.Pf1)).Help!.Module);
        var error = Assert.Throws<PanelCompileException>(() => new DisplayDdsCompiler().Compile("BAD", source.Replace("*FLD VALUE", "*FLD ABSENT")));
        Assert.Equal("HLPARA", error.Token);
    }
    private static string DisplaySource()
    {
        var help = Dds(keywords: "HLPARA(*FLD VALUE)").ToCharArray(); help[16] = 'H';
        return Dds(keywords: "HELP HLPTITLE('Customer help') HLPPNLGRP(GENERAL QGPL/HELPS) CA03(03)") +
            Dds("ENTRY", record: true) + new string(help) + Dds(keywords: "HLPPNLGRP(FIELD QGPL/HELPS)", indicators: " 11") +
            Dds("VALUE", 8, usage: 'B', row: 4, column: 4);
    }
    [Fact]
    public void Panel_groups_compile_from_source_members_follow_typed_rename_and_reject_headless_display()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "HelpPassword22");
        var files = new Ipc.Db.Store.SqliteFileStore(system.Connections, system.Objects); files.CreateSourceFile("QGPL", "QPNLSRC");
        foreach (var line in Source.Split('\n')) files.Insert("QGPL", "QPNLSRC", "QPNLSRC", "QPNLSRC", new Dictionary<string, object?> { ["SRCDTA"] = line });
        using var session = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("CRTPNLGRP PNLGRP(QGPL/HELPS) SRCFILE(QGPL/QPNLSRC) SRCMBR(QPNLSRC)"); Assert.False(result.IsError, result.Message);
        Assert.Equal(CommandOutcome.DisplayHelp, session.Execute("DSPHELP PNLGRP(QGPL/HELPS) MODULE(FIELD)").Outcome);
        system.ObjectOperations.Relocate(new("QGPL", "HELPS"), ObjectType.PanelGroup, new("QGPL", "RENAMED"));
        Assert.Equal("HELPS", new HelpPanelStore(system.Connections).Load("QGPL", "RENAMED").Name);
        using var headless = new ExecutionSession(system, system.Jobs.CreateCommunication("QUSER"), CancellationToken.None);
        Assert.True(headless.Execute("DSPHELP PNLGRP(QGPL/RENAMED)").IsError);
    }
    [Fact]
    public void Menu_command_and_display_help_preserve_unsent_input_and_recheck_live_authority()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        new HelpPanelStore(system.Connections).Create("QGPL", "HELPS", Source);
        new DisplayFileStore(system.Connections).Create("QGPL", "ENTRY", DisplaySource().Replace(" 11", "   "));
        using var menu = new MenuController(system, system.Security.Profiles.Get("QUSER"));
        Assert.Equal("Help", menu.Handle(new(AidKey.Pf1)).State); Assert.Contains("Enter an option", new AnsiRenderer().Render(menu.Buffer)); menu.Handle(new(AidKey.Pf3));
        foreach (var c in "CRTLIB") menu.Handle(new(AidKey.None, Character: c)); var before = menu.Buffer.Clone();
        Assert.Equal("Help", menu.Handle(new(AidKey.Pf1)).State); Assert.Contains("Accepted parameters", new AnsiRenderer().Render(menu.Buffer));
        menu.Handle(new(AidKey.Pf3)); Assert.Equal(before.RowText(23), menu.Buffer.RowText(23)); Assert.Equal(before.Cursor, menu.Buffer.Cursor);
        menu.Handle(new(AidKey.Clear)); foreach (var c in "RUNPNL FILE(QGPL/ENTRY)") menu.Handle(new(AidKey.None, Character: c)); menu.Handle(new(AidKey.Enter));
        menu.Handle(new(AidKey.None, Character: 'X')); before = menu.Buffer.Clone();
        Assert.Equal("Help", menu.Handle(new(AidKey.Pf1)).State); Assert.Contains("Field help", menu.Buffer.RowText(1));
        menu.Handle(new(AidKey.Pf3)); Assert.Equal(before.RowText(4), menu.Buffer.RowText(4)); Assert.Equal(before.Cursor, menu.Buffer.Cursor);
        menu.Handle(new(AidKey.Pf1)); system.Security.Authority.Grant("QGPL", "HELPS", ObjectType.PanelGroup, "QUSER", AuthorityBit.None);
        Assert.Equal("DisplayPanel", menu.Handle(new(AidKey.RollUp)).State); Assert.DoesNotContain("Field help", menu.Buffer.RowText(1));
        Assert.Equal('X', menu.Buffer[4, 4].Value);
    }
    [Fact]
    public void Stored_help_checks_signature_policy_and_source_payload_agreement()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var store = new HelpPanelStore(system.Connections, system.ObjectSigning);
        store.Create("QGPL", "HELPS", Source); var descriptor = system.Objects.GetRequired("QGPL", "HELPS", ObjectType.PanelGroup);
        descriptor.Source = Source.Replace("General help", "Altered title"); system.Objects.Update(descriptor);
        Assert.Throws<CpfException>(() => store.Load("QGPL", "HELPS"));
        descriptor.Source = Source; system.Objects.Update(descriptor);
        using var connection = system.Connections.Open(); using var policy = connection.CreateCommand();
        policy.CommandText = "INSERT INTO sys_object_signature_policy(lib,name,type) VALUES('QGPL','HELPS','*PNLGRP')"; policy.ExecuteNonQuery();
        Assert.Throws<CpfException>(() => store.Load("QGPL", "HELPS"));
    }
}
