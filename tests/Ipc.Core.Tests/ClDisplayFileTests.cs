using System.Text;
using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Db.Store;
using Ipc.Dsp;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Services.Work;
using Ipc.Session;
using Ipc.Terminal;
using static Ipc.Core.Tests.DisplayPanelTests;

namespace Ipc.Core.Tests;

public sealed class ClDisplayFileTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _sources;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
    public ClDisplayFileTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ClDisplayFixture22");
        _sources = new(_system.Connections, _system.Objects); _sources.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
    }
    private static string Screen(string extra = "", bool second = false) => Dds(keywords: "HELP CA03(03) CF06(06)") +
        Dds("ENTRY", record: true) + Dds(row: 1, column: 2, keywords: "'CL display exchange'") +
        Dds("NAME", 10, row: 4, column: 2, usage: 'B') + Dds("AMOUNT", 5, row: 6, column: 2, usage: 'B', type: 'Y', decimals: 2) +
        extra + (second ? Dds("SECOND", record: true) + Dds("NAME", 10, row: 4, column: 2, usage: 'B') : "");
    private void Display(string source, string name = "SCREEN") => new DisplayFileStore(_system.Connections).Create("QGPL", name, source);
    private CommandResult Create(string source, string name = "APP")
    {
        _sources.SaveSourceMember("QGPL", "SOURCE", name, source, null);
        return new CommandService(_system).Execute($"CRTCLPGM PGM(QGPL/{name}) SRCFILE(QGPL/SOURCE)");
    }
    private void Compile(string source, string name = "APP") { var result = Create(source, name); Assert.False(result.IsError, result.Message); }
    private MenuController Menu(string user = "QSECOFR") => new(_system, _system.Security.Profiles.Get(user), cancellationToken: _timeout.Token);
    private static void Type(MenuController menu, string text) { foreach (var c in text) menu.Handle(new(AidKey.None, Character: c)); }
    private static SessionEvent Command(MenuController menu, string command) { Type(menu, command); return menu.Handle(new(AidKey.Enter)); }
    private static string Status(MenuController menu) => menu.Buffer.RowText(24).Trim();

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_CL_sends_fields_waits_and_returns_input_and_function_indicators(int ccsid)
    {
        Display(Screen());
        Compile("PGM\nDCLF FILE(QGPL/SCREEN)\nCHGVAR &NAME 'START'\nCHGVAR &AMOUNT 1.25\nSNDRCVF\n" +
            "IF &IN06 THEN(SNDPGMMSG MSG(&NAME *TCAT ':' *CAT &AMOUNT))\nENDPGM");
        var profile = _system.Security.Profiles.Get("QSECOFR"); profile.Ccsid = ccsid; _system.Security.Profiles.Update(profile);
        using var menu = Menu();
        Assert.Equal("ProgramDisplay", Command(menu, "CALL QGPL/APP").State);
        Assert.Contains("START", menu.Buffer.RowText(4)); Assert.Contains("1.25", menu.Buffer.RowText(6));
        menu.Handle(new(AidKey.Clear)); // This AID returns without input; test normal editing in a second call below.
        Assert.Contains("Selection", menu.Buffer.RowText(23));
        Command(menu, "CALL QGPL/APP");
        Type(menu, "AB"); menu.Handle(new(AidKey.None, Edit: CursorEdit.Tab));
        menu.Handle(new(AidKey.None, Edit: CursorEdit.FieldEraseToEnd)); Type(menu, "2.50");
        var result = menu.Handle(new(AidKey.Pf6));
        Assert.Equal("Menu", result.State); Assert.Contains("ABSTART:2.50", Status(menu));
        Assert.False(_system.Jobs.GetRequired(menu.JobKey).Status == JobStatus.Completed);
    }

    [Fact]
    public void CA_does_not_transfer_edits_or_overwrite_variables_changed_after_SNDF()
    {
        Display(Screen());
        Compile("DCLF FILE(QGPL/SCREEN)\nCHGVAR &NAME 'BEFORE'\nSNDF\nCHGVAR &NAME 'AFTER'\nRCVF\nIF &IN03 THEN(SNDPGMMSG MSG(&NAME))");
        using var menu = Menu(); Command(menu, "CALL QGPL/APP"); Type(menu, "EDIT");
        Assert.Equal("Menu", menu.Handle(new(AidKey.Pf3)).State); Assert.Equal("AFTER", Status(menu));
    }

    [Fact]
    public void Receive_returns_response_indicators_without_overwriting_changed_output_selectors()
    {
        Display(Screen(Dds(keywords: "DSPATR(HI)", indicators: " 21")));
        Compile("DCLF QGPL/SCREEN\nCHGVAR &IN21 '1'\nSNDF\nCHGVAR &IN21 '0'\nRCVF\n" +
            "IF (&IN21 *EQ '0' *AND &IN03 *EQ '1') THEN(SNDPGMMSG MSG('SELECTOR PRESERVED'))");
        using var menu = Menu(); Command(menu, "CALL QGPL/APP"); menu.Handle(new(AidKey.Pf3)); Assert.Equal("SELECTOR PRESERVED", Status(menu));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Large_decimal_display_fields_use_native_packed_character_variables(int ccsid)
    {
        Display(Dds(keywords: "CA03(03)") + Dds("ENTRY", record: true) + Dds("VALUE", 16, row: 4, column: 2, type: 'Y', decimals: 2, usage: 'B'));
        Compile("DCLF QGPL/SCREEN\nCHGVAR &VALUE X'01234567890123456D'\nSNDRCVF\nCRTDTAARA DTAARA(QTEMP/RESULT) TYPE(*CHAR) LEN(9) VALUE(&VALUE)");
        var terminal = new ScriptTerminal(display =>
        {
            Assert.Contains("-12345678901234.56", display.Session.Buffer.RowText(4));
            return display.Session.Handle(new(AidKey.Enter));
        });
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None, terminal);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message);
        using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var raw = Assert.IsType<ProgramBuffer>(new DataAreaStore(_system.Connections).Read("QTEMP", "RESULT").Value);
        Assert.Equal("01234567890123456D", Convert.ToHexString(raw.ToArray()));
    }

    [Fact]
    public void Multiple_formats_and_OPNID_variables_bind_independently_and_return_through_nested_calls()
    {
        Display(Screen(second: true));
        Compile("DCLF FILE(QGPL/SCREEN) RCDFMT(ENTRY   SECOND) OPNID(DSP)\nCHGVAR &DSP_NAME 'FIRST'\n" +
            "SNDRCVF RCDFMT(ENTRY) OPNID(DSP)\nSNDPGMMSG MSG(&DSP_NAME)\nCHGVAR &DSP_NAME 'SECOND'\n" +
            "SNDRCVF RCDFMT(SECOND) OPNID(DSP)\nSNDPGMMSG MSG(&DSP_NAME)", "CHILD");
        Compile("PGM\nCALL QGPL/CHILD\nSNDPGMMSG MSG('PARENT RESUMED')\nENDPGM");
        using var menu = Menu(); Command(menu, "CALL QGPL/APP"); Assert.Contains("FIRST", menu.Buffer.RowText(4));
        Assert.Equal("ProgramDisplay", menu.Handle(new(AidKey.Enter)).State); Assert.Contains("SECOND", menu.Buffer.RowText(4));
        Type(menu, "X"); menu.Handle(new(AidKey.Enter));
        Assert.Contains("PARENT RESUMED", Status(menu));
        using var identity = OperationIdentity.Enter("QSECOFR", menu.JobKey);
        Assert.Equal(0, _system.JobRuntime.Environment(menu.JobKey)!.OpenPathCount);
    }

    [Fact]
    public void Message_subfiles_run_from_compiled_CL_with_saved_second_level_help_and_paging()
    {
        var source = Dds(keywords: "HELP CA03(03)") + Dds("ROWS", record: true, keywords: "SFL SFLMSGRCD(4)") +
            Dds("KEY", keywords: "SFLMSGKEY") + Dds("QUEUE", keywords: "SFLPGMQ") +
            Dds("CTL", record: true, keywords: "SFLCTL(ROWS) SFLSIZ(2) SFLPAG(2) SFLDSP SFLDSPCTL SFLINZ SFLEND(*MORE)") +
            Dds("PGMQ", keywords: "SFLPGMQ");
        Display(source);
        var descriptions = new MessageDescriptionStore(_system.Connections); descriptions.Create("QGPL", "ERRORS", "");
        descriptions.Add("QGPL", "ERRORS", "ERR0001", new MessageDescription("Invalid &1", "Correct &1 and try again.", 30, [new("*CHAR", 3)], 37));
        Compile("PGM\nDCLF QGPL/SCREEN RCDFMT(CTL)\nCHGVAR &PGMQ 'APP'\n" +
            "SNDPGMMSG MSGID(ERR0001) MSGF(QGPL/ERRORS) MSGDTA('ABC') TOPGMQ(*SAME)\n" +
            "SNDPGMMSG MSG('Second message') TOPGMQ(*SAME)\nSNDPGMMSG MSG('Third message') TOPGMQ(*SAME)\n" +
            "SNDRCVF RCDFMT(CTL)\nSNDPGMMSG MSG('MESSAGE DISPLAY RETURNED')\nENDPGM");
        using var menu = Menu(); Assert.Equal("ProgramDisplay", Command(menu, "CALL QGPL/APP").State);
        Assert.Contains("Invalid ABC", menu.Buffer.RowText(4));
        var saved = menu.Buffer.Clone();
        Assert.Equal("Help", menu.Handle(new(AidKey.Pf1)).State);
        Assert.Contains("ERR0001", menu.Buffer.RowText(1)); Assert.Contains("Correct ABC", string.Concat(menu.Buffer.Positions().Select(p => menu.Buffer[p.Row, p.Column].Value)));
        Assert.Equal("ProgramDisplay", menu.Handle(new(AidKey.Pf3)).State);
        Assert.Equal(saved.RowText(4), menu.Buffer.RowText(4)); Assert.Equal(saved.Cursor, menu.Buffer.Cursor);
        menu.Handle(new(AidKey.RollUp)); Assert.Contains("Third message", menu.Buffer.RowText(4));
        menu.Handle(new(AidKey.Pf3)); Assert.Contains("MESSAGE DISPLAY RETURNED", Status(menu));
    }

    [Fact]
    public void Invalid_input_stays_in_the_editor_and_MONMSG_can_recover_display_output_errors()
    {
        Display(Screen());
        Compile("DCLF QGPL/SCREEN\nSNDRCVF\nSNDPGMMSG MSG('VALID INPUT')");
        using var menu = Menu(); Command(menu, "CALL QGPL/APP");
        menu.Handle(new(AidKey.None, Edit: CursorEdit.Tab)); Type(menu, "1..2");
        Assert.Equal("ProgramDisplay", menu.Handle(new(AidKey.Enter)).State);
        Assert.Contains("number", Status(menu), StringComparison.OrdinalIgnoreCase);
        menu.Handle(new(AidKey.Pf3)); Assert.Contains("VALID INPUT", Status(menu));
        Compile("DCLF QGPL/SCREEN\nCHGVAR &NAME X'25'\nSNDRCVF\nMONMSG CPF4131 EXEC(SNDPGMMSG MSG('OUTPUT MONITORED'))", "BADOUT");
        Assert.Equal("Menu", Command(menu, "CALL QGPL/BADOUT").State); Assert.Equal("OUTPUT MONITORED", Status(menu));
    }

    [Fact]
    public void Headless_execution_fails_as_a_monitorable_display_error()
    {
        Display(Screen()); Compile("DCLF QGPL/SCREEN\nSNDRCVF\nMONMSG CPF0861 EXEC(SNDPGMMSG MSG('NO TERMINAL'))");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Equal("NO TERMINAL", result.Message);
    }

    [Fact]
    public void Live_authority_failure_is_reported_to_the_suspended_program_MONMSG()
    {
        Display(Screen()); Compile("DCLF QGPL/SCREEN\nSNDRCVF\nMONMSG CPF9802 EXEC(SNDPGMMSG MSG('AUTHORITY MONITORED'))");
        var profile = new UserProfile { Name = "READER", InitialMenu = "*MAIN" }; _system.Security.Profiles.Create(profile);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("READER"), "ReaderDisplay22");
        using var menu = Menu("READER"); Command(menu, "CALL QGPL/APP"); Assert.Contains("CL display exchange", menu.Buffer.RowText(1));
        var descriptor = _system.Objects.GetRequired("QGPL", "SCREEN", ObjectType.File); descriptor.PublicAuthority = AuthorityBit.None; _system.Objects.Update(descriptor);
        menu.Handle(new(AidKey.Enter)); Assert.Equal("AUTHORITY MONITORED", Status(menu));
    }

    [Fact]
    public void Suspended_access_checks_preserve_the_programs_adopted_authority()
    {
        Display(Screen()); Compile("DCLF QGPL/SCREEN\nCHGVAR &NAME 'ADOPTED'\nSNDRCVF\nSNDPGMMSG MSG('ADOPTED DISPLAY RETURNED')");
        _system.Security.Profiles.Create(new UserProfile { Name = "READER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("READER"), "ReaderDisplay22");
        var file = _system.Objects.GetRequired("QGPL", "SCREEN", ObjectType.File); file.PublicAuthority = AuthorityBit.None; _system.Objects.Update(file);
        var program = _system.Objects.GetRequired("QGPL", "APP", ObjectType.Program); program.AdoptsOwnerAuthority = true; _system.Objects.Update(program);
        using var menu = Menu("READER"); Assert.Equal("ProgramDisplay", Command(menu, "CALL QGPL/APP").State);
        Assert.Equal("ProgramDisplay", menu.Handle(new(AidKey.None, Character: 'X')).State);
        menu.Handle(new(AidKey.Pf3)); Assert.Equal("ADOPTED DISPLAY RETURNED", Status(menu));
    }

    [Fact]
    public void Display_date_time_and_timestamp_fields_return_text_in_the_declared_layout()
    {
        Display(Dds("ENTRY", record: true) + Dds("DAY", 10, row: 4, column: 2, type: 'L', usage: 'B') +
            Dds("TIME", 8, row: 6, column: 2, type: 'T', usage: 'B') + Dds("STAMP", 26, row: 8, column: 2, type: 'Z', usage: 'B'));
        Compile("DCLF QGPL/SCREEN\nCHGVAR &DAY '2024-02-29'\nCHGVAR &TIME '12:34:56'\nCHGVAR &STAMP '2024-02-29-12.34.56.123456'\n" +
            "SNDRCVF\nSNDPGMMSG MSG(&DAY *TCAT ':' *CAT &TIME *TCAT ':' *CAT &STAMP)");
        var terminal = new ScriptTerminal(display => display.Session.Handle(new(AidKey.Enter)));
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None, terminal);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message);
        Assert.Equal("2024-02-29:12:34:56:2024-02-29-12.34.56.123456", result.Message);
    }

    [Fact]
    public void Initial_program_can_wait_for_display_input_before_the_initial_menu_or_signoff()
    {
        Display(Screen()); Compile("DCLF QGPL/SCREEN\nCHGVAR &NAME 'INITIAL'\nSNDRCVF");
        var profile = _system.Security.Profiles.Get("QSECOFR"); profile.InitialProgram = "QGPL/APP"; profile.InitialMenu = "*SIGNOFF"; _system.Security.Profiles.Update(profile);
        using var menu = Menu(); Assert.False(menu.StartupSignOff); Assert.Contains("INITIAL", menu.Buffer.RowText(4));
        var result = menu.Handle(new(AidKey.Pf3)); Assert.True(result.EndSession); Assert.True(menu.StartupSignOff);
    }

    [Fact]
    public void Disconnect_joins_the_waiting_program_before_releasing_frames_and_private_data()
    {
        Display(Screen()); Compile("DCLF QGPL/SCREEN\nSNDRCVF");
        var menu = Menu(); Command(menu, "CALL QGPL/APP"); var key = menu.JobKey;
        menu.RequestDisconnect(); menu.Dispose();
        var job = _system.Jobs.GetRequired(key); Assert.Equal(JobStatus.Completed, job.Status); Assert.Equal(JobCompletion.Abnormal, job.CompletionCode);
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM sys_job_temp_libraries WHERE job_number=$job"; query.Parameters.AddWithValue("$job", key.Number);
        Assert.Equal(0L, query.ExecuteScalar());
    }

    [Fact]
    public void System_request_transfers_to_another_job_and_restores_the_suspended_CL_editor()
    {
        Display(Screen()); Compile("DCLF QGPL/SCREEN\nSNDRCVF\nSNDPGMMSG MSG(&NAME)");
        using var menu = Menu(); Command(menu, "CALL QGPL/APP"); Type(menu, "UNSENT");
        var original = menu.JobKey; var cursor = menu.Buffer.Cursor;
        menu.Handle(new(AidKey.SysReq)); Assert.Contains("System Request", menu.Buffer.RowText(1));
        Type(menu, "1"); menu.Handle(new(AidKey.Enter)); Assert.NotEqual(original, menu.JobKey);
        Assert.Contains("Selection", menu.Buffer.RowText(23));
        menu.Handle(new(AidKey.SysReq)); Type(menu, "1"); menu.Handle(new(AidKey.Enter));
        Assert.Equal(original, menu.JobKey); Assert.Contains("UNSENT", menu.Buffer.RowText(4)); Assert.Equal(cursor, menu.Buffer.Cursor);
        menu.Handle(new(AidKey.SysReq)); Type(menu, "3"); menu.Handle(new(AidKey.Enter)); Assert.Contains("Job details", menu.Buffer.RowText(1));
        menu.Handle(new(AidKey.Pf3)); Assert.Contains("UNSENT", menu.Buffer.RowText(4)); Assert.Equal(cursor, menu.Buffer.Cursor);
        menu.Handle(new(AidKey.Enter)); Assert.Equal("UNSENT", Status(menu));
    }

    [Fact]
    public void Ending_a_suspended_original_group_job_keeps_the_alternate_group_alive()
    {
        Display(Screen()); Compile("DCLF QGPL/SCREEN\nSNDRCVF");
        using var menu = Menu(); Command(menu, "CHGGRPA GRPJOB(HOME)"); var original = menu.JobKey;
        Command(menu, "CALL QGPL/APP"); menu.Handle(new(AidKey.SysReq)); Type(menu, "6"); menu.Handle(new(AidKey.Enter));
        Assert.Contains("Transfer to Group Job", menu.Buffer.RowText(1)); Type(menu, "NEXT"); menu.Handle(new(AidKey.Pf6));
        Assert.NotEqual(original, menu.JobKey); var alternate = menu.JobKey;
        Command(menu, "ENDGRPJOB GRPJOB(HOME)");
        Assert.Equal(alternate, menu.JobKey); Assert.Equal(JobStatus.Completed, _system.Jobs.GetRequired(original).Status);
        Assert.False(menu.CancellationToken.IsCancellationRequested);
        Command(menu, "DSPJOB"); Assert.Equal(JobStatus.Active, _system.Jobs.GetRequired(alternate).Status);
    }

    [Fact]
    public void Script_hosts_receive_a_live_call_frame_and_normalized_display_values()
    {
        Display(Screen()); Compile("DCLF QGPL/SCREEN\nCHGVAR &NAME 'SCRIPT'\nSNDRCVF\nSNDPGMMSG MSG(&NAME)");
        ProgramDisplay? retained = null;
        var terminal = new ScriptTerminal(display =>
        {
            retained = display;
            Assert.Equal("APP", OperationIdentity.Current!.CallStack.Last().Program.Name.Value);
            Assert.Contains("SCRIPT", display.Session.Buffer.RowText(4));
            display.CheckAccess(); display.Session.Handle(new(AidKey.None, Character: 'X')); return display.Session.Handle(new(AidKey.Enter));
        });
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None, terminal);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Equal("XSCRIPT", result.Message);
        Assert.Equal(1, terminal.Calls);
        Assert.Throws<ObjectDisposedException>(() => retained!.CheckAccess());
    }

    [Fact]
    public void Display_bindings_round_trip_without_changing_legacy_database_metadata()
    {
        Display(Screen(second: true)); Compile("DCLF QGPL/SCREEN RCDFMT(SECOND ENTRY)\nSNDRCVF RCDFMT(SECOND)");
        var descriptor = _system.Objects.GetRequired("QGPL", "APP", ObjectType.Program);
        var restored = ClFileBindings.Restore(descriptor.Source!, descriptor.ExtendedAttributes![ClFileBindings.AttributeName]);
        var layout = Assert.Single(restored.Files).Definition;
        Assert.Equal(new[] { "SECOND", "ENTRY" }, layout.DisplayRecords!.Select(r => r.Name));
        Assert.Contains(layout.Fields, f => f == new ClFileField("IN03", "*LGL", 1, 0));
        Assert.Throws<InvalidDataException>(() => ClFileBindings.Restore(descriptor.Source!, descriptor.ExtendedAttributes[ClFileBindings.AttributeName].Replace("\"name\":\"SECOND\"", "\"name\":\"MISSING!\"")));
    }

    [Fact]
    public void An_initial_program_failure_cleans_its_job_and_runtime()
    {
        Compile("BOGUS");
        var profile = _system.Security.Profiles.Get("QSECOFR"); profile.InitialProgram = "QGPL/APP"; _system.Security.Profiles.Update(profile);
        Assert.Throws<CpfException>(() => Menu());
        var job = Assert.Single(_system.Jobs.List()); Assert.Equal(JobStatus.Completed, job.Status); Assert.Equal(JobCompletion.Abnormal, job.CompletionCode);
    }

    [Fact]
    public void Changed_display_layout_is_level_checked_before_any_terminal_side_effect()
    {
        Display(Screen()); Compile("DCLF QGPL/SCREEN\nSNDRCVF\nMONMSG CPF4131 EXEC(SNDPGMMSG MSG('LEVEL CHECK'))");
        new DisplayFileStore(_system.Connections).Create("QGPL", "SCREEN", Screen().Replace("   10A", "   12A"), replace: true);
        var terminal = new ScriptTerminal(_ => throw new InvalidOperationException("Should not reach terminal"));
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None, terminal);
        var result = session.Execute("CALL QGPL/APP"); Assert.Equal("LEVEL CHECK", result.Message); Assert.Equal(0, terminal.Calls);
    }

    [Theory]
    [InlineData("SNDRCVF WAIT(*NO)")]
    [InlineData("SNDRCVF DEV(OTHER)")]
    [InlineData("SNDF WAIT(*YES)")]
    [InlineData("SNDRCVF RCDFMT(MISSING)")]
    [InlineData("CLOSE")]
    public void Unsupported_display_operations_fail_at_compile_time(string operation)
    {
        Display(Screen()); Assert.True(Create("DCLF QGPL/SCREEN\n" + operation).IsError);
    }

    [Fact]
    public void Multi_format_FILE_default_and_inconsistent_shared_fields_are_rejected()
    {
        Display(Screen(second: true)); Assert.True(Create("DCLF QGPL/SCREEN\nSNDRCVF").IsError);
        Assert.True(Create("DCLF QGPL/SCREEN RCDFMT(ENTRY)\nSNDRCVF", "SUBSET").IsError);
        new DisplayFileStore(_system.Connections).Create("QGPL", "SCREEN", Screen() + Dds("SECOND", record: true) + Dds("NAME", 12, row: 4, column: 2, usage: 'B'), replace: true);
        Assert.True(Create("DCLF QGPL/SCREEN\nSNDRCVF RCDFMT(ENTRY)", "OTHER").IsError);
    }

    private sealed class ScriptTerminal(Func<ProgramDisplay, PanelResponse> read) : IProgramTerminal
    {
        public int Calls { get; private set; }
        public PanelResponse Read(ProgramDisplay display, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Calls++; return read(display); }
    }
    public void Dispose() { _system.Dispose(); _timeout.Dispose(); }
}
