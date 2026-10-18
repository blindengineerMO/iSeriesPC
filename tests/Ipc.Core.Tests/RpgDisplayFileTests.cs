using System.Text.Json;
using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Db.Store;
using Ipc.Dsp;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Services.Work;
using Ipc.Session;
using Ipc.Terminal;
using static Ipc.Core.Tests.DisplayPanelTests;

namespace Ipc.Core.Tests;

public sealed class RpgDisplayFileTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _sources;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
    public RpgDisplayFileTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "RpgDisplayFixture22");
        _sources = new(_system.Connections, _system.Objects);
        _sources.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
    }
    private static string Screen => Dds(keywords: "CA03(03) CF06(06)") + Dds("ENTRY", record: true) +
        Dds(row: 1, column: 2, keywords: "'RPG display exchange'") + Dds("NAME", 10, row: 4, column: 2, usage: 'B') +
        Dds("AMOUNT", 5, type: 'Y', decimals: 2, row: 6, column: 2, usage: 'B');
    private static string Rows => Dds(keywords: "CA03(03) CF06(06)") + Dds("ROWS", record: true, keywords: "SFL") +
        Dds(keywords: "SFLNXTCHG", indicators: " 21") + Dds("VALUE", 10, usage: 'B', row: 4, column: 2) + Dds("SECRET", 4, usage: 'H') +
        Dds("CTL", record: true, keywords: "SFLCTL(ROWS) SFLSIZ(10) SFLPAG(2) SFLDSP SFLDSPCTL SFLEND(*MORE)");
    private void Display(string text, string name = "SCREEN") => new DisplayFileStore(_system.Connections).Create("QGPL", name, text);
    private CommandResult Create(string text, string name = "APP")
    {
        _sources.SaveSourceMember("QGPL", "SOURCE", name, text, null);
        return new CommandService(_system).Execute($"CRTBNDRPG PGM(QGPL/{name}) SRCFILE(QGPL/SOURCE)");
    }
    private void Compile(string text, string name = "APP") { var result = Create(text, name); Assert.False(result.IsError, result.Message); }
    private void CompileCl(string text, string name)
    {
        _sources.SaveSourceMember("QGPL", "SOURCE", name, text, null);
        var result = new CommandService(_system).Execute($"CRTCLPGM PGM(QGPL/{name}) SRCFILE(QGPL/SOURCE)"); Assert.False(result.IsError, result.Message);
    }
    private static string File(string options = "") => "**free\ndcl-f SCREEN workstn extdesc('QGPL/SCREEN') extfile(*extdesc) " + options + ";\n";
    private MenuController Menu(string user = "QSECOFR") => new(_system, _system.Security.Profiles.Get(user), cancellationToken: _timeout.Token);
    private static void Type(MenuController menu, string text) { foreach (var c in text) menu.Handle(new(AidKey.None, Character: c)); }
    private static SessionEvent Command(MenuController menu, string text) { Type(menu, text); return menu.Handle(new(AidKey.Enter)); }
    private static string Status(MenuController menu) => menu.Buffer.RowText(24).Trim();
    private IReadOnlyList<JobLockInfo> Allocations() => new JobLockStore(_system.Connections).Inspect(new("QGPL", "SCREEN", ObjectType.File));
    private sealed class Terminal(Func<ProgramDisplay, PanelResponse> read) : IProgramTerminal
    {
        public PanelResponse Read(ProgramDisplay display, CancellationToken cancellationToken) => read(display);
    }

    [Fact]
    public void Workstation_INFSR_handles_closed_file_errors_before_any_terminal_transfer()
    {
        Display(Screen); Compile(File("usropn infsr(FAIL)") + "read SCREEN;\nreturn;\nbegsr FAIL;\n" +
            "if %status(SCREEN)=1211;\nsndpgmmsg 'WORKSTATION INFSR';\nendif;\nreturn;\nendsr;");
        using var menu = Menu(); Command(menu, "CALL QGPL/APP"); Assert.Equal("WORKSTATION INFSR", Status(menu));
    }

    [Fact]
    public void Workstation_INFDS_reports_actual_transfer_and_close_without_clearing_PSDS()
    {
        Display(Screen); Compile(File("infds(F)") +
            "dcl-ds F qualified;\nSTATUS *status;\nOP *opcode;\nRECORD_NAME *record;\nIS_OPEN ind pos(9);\nend-ds;\n" +
            "dcl-ds P psds qualified;\nSTATUS *status;\nend-ds;\n" +
            "exfmt ENTRY;\nif F.STATUS=0 and F.OP='EXFMTR' and F.RECORD_NAME='ENTRY' and F.IS_OPEN and P.STATUS=0;\n" +
            "close SCREEN;\nif not F.IS_OPEN and F.OP='CLOSEF';\nsndpgmmsg 'WORKSTATION FEEDBACK';\nendif;\nendif;\nseton LR;");
        var terminal = new Terminal(display => display.Session.Handle(new(AidKey.Enter)));
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), _timeout.Token, terminal);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Contains("WORKSTATION FEEDBACK", result.Message);
    }

    [Theory]
    [InlineData("read(e) SCREEN;")]
    [InlineData("write(e) ENTRY;")]
    [InlineData("exfmt(e) ENTRY;")]
    public void Closed_workstation_E_operations_report_numeric_status_and_bypass_MONITOR(string operation)
    {
        Display(Screen); Compile(File("usropn") + "monitor;\n" + operation + "\nif %error and %status=1211 and %status(SCREEN)=1211;\n" +
            "sndpgmmsg 'WORKSTN STATUS';\nendif;\non-error *file;\nsndpgmmsg 'BAD';\nendmon;\nseton LR;");
        using var menu = Menu(); Command(menu, "CALL QGPL/APP"); Assert.Equal("WORKSTN STATUS", Status(menu));
    }

    [Theory]
    [InlineData("2.5", "2.50")]
    [InlineData("-12.3", "-12.30")]
    [InlineData("0.1", "0.10")]
    public void Decimal_display_literals_retain_their_numeric_value(string expression, string expected)
    {
        Display(Screen); Compile(File() + "eval AMOUNT = " + expression + ";\nexfmt ENTRY;\nseton LR;");
        using var menu = Menu(); Assert.Equal("ProgramDisplay", Command(menu, "CALL QGPL/APP").State);
        Assert.Contains(expected, menu.Buffer.RowText(6)); menu.Handle(new(AidKey.Enter));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Display_input_preserves_literal_text_that_matches_a_figurative_constant(int ccsid)
    {
        Display(Screen); Compile(File() + "exfmt ENTRY;\nsndpgmmsg %trim(NAME);\nseton LR;");
        var profile = _system.Security.Profiles.Get("QSECOFR"); profile.Ccsid = ccsid; _system.Security.Profiles.Update(profile);
        using var menu = Menu(); Command(menu, "CALL QGPL/APP"); Type(menu, "*BLANK"); menu.Handle(new(AidKey.Enter));
        Assert.Equal("*BLANK", Status(menu));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_free_WORKSTN_exchanges_fields_and_function_indicators(int ccsid)
    {
        Display(Screen); Compile(File() + "eval NAME = 'START';\neval AMOUNT = 1.25;\nexfmt ENTRY;\n" +
            "if *in06 and NAME = 'ABSTART' and AMOUNT = 2.5;\nsndpgmmsg %trim(NAME) + ':' + %char(AMOUNT);\nendif;\nseton LR;\nreturn;");
        var profile = _system.Security.Profiles.Get("QSECOFR"); profile.Ccsid = ccsid; _system.Security.Profiles.Update(profile);
        using var menu = Menu(); Assert.Equal("ProgramDisplay", Command(menu, "CALL QGPL/APP").State);
        Assert.Contains("START", menu.Buffer.RowText(4)); Assert.Contains("1.25", menu.Buffer.RowText(6));
        Type(menu, "AB"); menu.Handle(new(AidKey.None, Edit: CursorEdit.Tab)); menu.Handle(new(AidKey.None, Edit: CursorEdit.FieldEraseToEnd)); Type(menu, "2.50");
        Assert.Equal("Menu", menu.Handle(new(AidKey.Pf6)).State); Assert.Contains("ABSTART:2.5", Status(menu));
    }

    [Fact]
    public void Separate_WRITE_and_READ_preserve_changes_on_CA_and_output_selectors()
    {
        Display(Screen + Dds(keywords: "DSPATR(HI)", indicators: " 21"));
        Compile(File() + "eval NAME = 'BEFORE';\nseton 21;\nwrite ENTRY;\neval NAME = 'AFTER';\nsetoff 21;\nread SCREEN;\n" +
            "if *in03 and not *in21;\nsndpgmmsg %trim(NAME);\nendif;\nseton LR;");
        using var menu = Menu(); Command(menu, "CALL QGPL/APP"); Type(menu, "EDIT"); menu.Handle(new(AidKey.Pf3)); Assert.Equal("AFTER", Status(menu));
    }

    [Fact]
    public void Subfile_READC_CHAIN_UPDATE_return_RRNs_hidden_fields_and_real_status()
    {
        Display(Rows);
        Compile(File("sfile(ROWS:RRN)") + "dcl-s RRN packed(4:0);\n" +
            "eval RRN = 1;\neval VALUE = 'ONE';\neval SECRET = 'HIDE';\nwrite ROWS;\neval RRN = 2;\neval VALUE = 'TWO';\nwrite ROWS;\n" +
            "exfmt CTL;\nreadc ROWS;\nif not %eof(SCREEN);\nsndpgmmsg %char(RRN) + ':' + %trim(VALUE) + ':' + SECRET;\n" +
            "eval VALUE = 'UPDATED';\nupdate ROWS;\nendif;\nreadc ROWS;\nif %eof;\nsndpgmmsg 'EOF';\nendif;\n" +
            "chain 1 ROWS;\nif %found(SCREEN) and not %eof(SCREEN) and %eof;\nsndpgmmsg %trim(VALUE);\nendif;\n" +
            "chain 9 ROWS;\nif not %found;\nsndpgmmsg 'MISS:' + %trim(VALUE);\nendif;\nseton LR;");
        var terminal = new Terminal(display =>
        {
            Assert.Contains("ONE", display.Session.Buffer.RowText(4)); Assert.Contains("TWO", display.Session.Buffer.RowText(5));
            display.Session.Handle(new(AidKey.None, Character: 'X')); return display.Session.Handle(new(AidKey.Enter));
        });
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), _timeout.Token, terminal);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message);
        Assert.Contains("1:XONE:HIDE", result.Message); Assert.Contains("EOF", result.Message); Assert.Contains("MISS:UPDATED", result.Message);
    }

    [Fact]
    public void SFLNXTCHG_survives_UPDATE_and_is_read_on_the_next_display_cycle()
    {
        Display(Rows); Compile(File("sfile(ROWS:RRN)") + "dcl-s RRN packed(4:0);\neval RRN = 1;\neval VALUE = 'FIRST';\nwrite ROWS;\n" +
            "exfmt CTL;\nreadc ROWS;\nseton 21;\neval VALUE = 'RETRY';\nupdate ROWS;\nreadc ROWS;\nif not %eof;\nsndpgmmsg 'BAD CURSOR';\nendif;\n" +
            "exfmt CTL;\nreadc ROWS;\nif not %eof;\nsndpgmmsg 'RETRY:' + %trim(VALUE);\nendif;\nseton LR;");
        var calls = 0;
        var terminal = new Terminal(display =>
        {
            if (++calls == 1) display.Session.Handle(new(AidKey.None, Character: 'X'));
            return display.Session.Handle(new(AidKey.Enter));
        });
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), _timeout.Token, terminal);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message);
        Assert.Contains("RETRY:RETRY", result.Message); Assert.DoesNotContain("BAD CURSOR", result.Message); Assert.Equal(2, calls);
    }

    [Fact]
    public void Native_fixed_F_spec_with_free_calculations_uses_RENAME_and_PREFIX()
    {
        Display(Screen);
        Compile("     FSCREEN    cf   e             workstn    extdesc('QGPL/SCREEN') extfile(*extdesc) rename(ENTRY:FORM) prefix(DSP_)\n" +
            "/free\neval DSP_NAME = 'RENAMED';\nexfmt FORM;\nsndpgmmsg %trim(DSP_NAME);\nseton LR;\n/end-free");
        using var menu = Menu(); Assert.Equal("ProgramDisplay", Command(menu, "CALL QGPL/APP").State); Assert.Contains("RENAMED", menu.Buffer.RowText(4));
        menu.Handle(new(AidKey.Enter)); Assert.Equal("RENAMED", Status(menu));
    }

    [Fact]
    public void RETURN_retains_subfiles_and_open_allocations_and_LR_releases_them_across_new_commands()
    {
        Display(Rows); Compile(File("sfile(ROWS:RRN)") + "dcl-s RRN packed(4:0);\ndcl-s COUNT packed(4:0) inz(0);\neval COUNT = COUNT + 1;\n" +
            "if COUNT = 1;\neval RRN = 1;\neval VALUE = 'RETAINED';\nwrite ROWS;\nendif;\nexfmt CTL;\nif COUNT = 2;\nseton LR;\nendif;\nreturn;");
        using var menu = Menu(); Command(menu, "CALL QGPL/APP"); menu.Handle(new(AidKey.Enter));
        Assert.Contains(Allocations(), a => a.Lifetime == "RpgDisplay");
        Command(menu, "CALL QGPL/APP"); Assert.Contains("RETAINED", menu.Buffer.RowText(4)); menu.Handle(new(AidKey.Enter));
        Assert.DoesNotContain(Allocations(), a => a.Lifetime == "RpgDisplay");
    }

    [Fact]
    public void USROPN_explicit_CLOSE_and_reopen_start_a_fresh_display()
    {
        Display(Screen); Compile(File("usropn") + "open SCREEN;\neval NAME = 'FIRST';\nexfmt ENTRY;\nclose SCREEN;\nopen SCREEN;\neval NAME = 'SECOND';\nexfmt ENTRY;\nseton LR;");
        using var menu = Menu(); Command(menu, "CALL QGPL/APP"); Assert.Contains("FIRST", menu.Buffer.RowText(4));
        menu.Handle(new(AidKey.Enter)); Assert.Contains("SECOND", menu.Buffer.RowText(4)); menu.Handle(new(AidKey.Enter));
        Assert.DoesNotContain(Allocations(), a => a.Lifetime == "RpgDisplay");
    }

    [Fact]
    public void Changed_external_layout_requires_recompile_of_the_saved_RPG_binding()
    {
        Display(Screen); Compile(File() + "exfmt ENTRY;\nseton LR;");
        new DisplayFileStore(_system.Connections).Create("QGPL", "SCREEN", Screen.Replace("   10A", "   11A", StringComparison.Ordinal), replace: true);
        using var menu = Menu(); Assert.Equal("Menu", Command(menu, "CALL QGPL/APP").State); Assert.Contains("recompile", Status(menu), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Allocations(), a => a.Lifetime == "RpgDisplay");
    }

    [Fact]
    public void Headless_execution_has_a_specific_diagnostic_and_no_display_leases()
    {
        Display(Screen); Compile(File() + "exfmt ENTRY;\nseton LR;");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), _timeout.Token);
        var result = session.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Contains("interactive terminal", result.Message);
        Assert.DoesNotContain(Allocations(), a => a.Lifetime == "RpgDisplay");
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Message_subfile_KEYS_remain_opaque_through_CL_entry_CHAIN_and_CL_writeback(int ccsid)
    {
        Display(Dds(keywords: "CA03(03)") + Dds("ROWS", record: true, keywords: "SFL SFLMSGRCD(4)") +
            Dds("KEY", keywords: "SFLMSGKEY") + Dds("QUEUE", keywords: "SFLPGMQ") +
            Dds("CTL", record: true, keywords: "SFLCTL(ROWS) SFLSIZ(2) SFLPAG(2) SFLDSP SFLDSPCTL"));
        Compile("     FSCREEN    cf   e             workstn    extdesc('QGPL/SCREEN') extfile(*extdesc) sfile(ROWS:RRN)\n" +
            ClExternalCallTests.Declaration("RRN", "P", "4") + ClExternalCallTests.Calculation("*ENTRY", "PLIST") + ClExternalCallTests.Calculation("KEY", "PARM") +
            "/free\neval QUEUE = '*EXT';\neval RRN = 1;\nwrite ROWS;\nexfmt CTL;\nchain 1 ROWS;\ncallp CHECKKEY(KEY);\nseton LR;\n/end-free");
        CompileCl("PGM PARM(&KEY)\nDCL &KEY *CHAR LEN(4)\nIF (&KEY *EQ X'8000FF01') THEN(SNDPGMMSG MSG('OPAQUE KEY ACCEPTED'))\nENDPGM", "CHECKKEY");
        var terminal = new Terminal(display =>
        { Assert.Contains("Opaque message", display.Session.Buffer.RowText(4)); return display.Session.Handle(new(AidKey.Enter)); });
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), _timeout.Token, terminal);
        using (OperationIdentity.Enter("QSECOFR", session.Job.Key))
        {
            var queue = _system.JobRuntime.Environment(session.Job.Key)!.MessageQueue();
            new MessageQueueStore(_system.Connections).SendTo([queue], new(Ipc.Core.Text.CodePage.ToBytes(ccsid, "Opaque message"), ccsid), "INFO");
            using var connection = _system.Connections.Open(); using var update = connection.CreateCommand();
            update.CommandText = "UPDATE sys_message_entries SET key=$key WHERE program_queue=$queue";
            update.Parameters.AddWithValue("$key", (long)0x8000ff01); update.Parameters.AddWithValue("$queue", queue.ProgramQueue); update.ExecuteNonQuery();
        }
        var result = session.Execute("CALL QGPL/APP PARM(X'8000FF01')"); Assert.False(result.IsError, result.Message); Assert.Contains("OPAQUE KEY ACCEPTED", result.Message);
    }

    [Fact]
    public void RPG_message_subfile_initialization_reads_the_current_program_queue_and_saved_help()
    {
        Display(Dds(keywords: "HELP CA03(03)") + Dds("ROWS", record: true, keywords: "SFL SFLMSGRCD(4)") +
            Dds("KEY", keywords: "SFLMSGKEY") + Dds("QUEUE", keywords: "SFLPGMQ") +
            Dds("CTL", record: true, keywords: "SFLCTL(ROWS) SFLSIZ(2) SFLPAG(2) SFLDSP SFLDSPCTL SFLINZ SFLEND(*MORE)") + Dds("PGMQ", keywords: "SFLPGMQ"));
        CompileCl("PGM\nSNDPGMMSG MSGID(ERR0001) MSGF(QGPL/ERRORS) MSGDTA('ABC') TOPGMQ(*PRV)\n" +
            "SNDPGMMSG MSG('Second') TOPGMQ(*PRV)\nSNDPGMMSG MSG('Third') TOPGMQ(*PRV)\nENDPGM", "PREP");
        var messages = new MessageDescriptionStore(_system.Connections); messages.Create("QGPL", "ERRORS", "");
        messages.Add("QGPL", "ERRORS", "ERR0001", new MessageDescription("Row &1", "Correct &1.", 30, [new("*CHAR", 3)], 37));
        Compile(File("sfile(ROWS:RRN)") + "dcl-s RRN packed(4:0);\ncallp PREP();\neval PGMQ = '*';\nexfmt CTL;\nsndpgmmsg 'RPG MESSAGES RETURNED';\nseton LR;");
        using var menu = Menu(); Assert.Equal("ProgramDisplay", Command(menu, "CALL QGPL/APP").State); Assert.Contains("Row ABC", menu.Buffer.RowText(4));
        Assert.Equal("Help", menu.Handle(new(AidKey.Pf1)).State); Assert.Contains("Correct ABC.", string.Concat(menu.Buffer.Positions().Select(p => menu.Buffer[p.Row, p.Column].Value)));
        menu.Handle(new(AidKey.Pf3)); menu.Handle(new(AidKey.RollUp)); Assert.Contains("Third", menu.Buffer.RowText(4));
        menu.Handle(new(AidKey.Pf3)); Assert.Contains("RPG MESSAGES RETURNED", Status(menu));
    }

    [Fact]
    public void Date_time_and_timestamp_external_fields_use_typed_input_values()
    {
        Display(Dds(keywords: "CA03(03)") + Dds("ENTRY", record: true) + Dds("DAY", 10, type: 'L', usage: 'B', row: 3, column: 2) +
            Dds("CLOCK", 8, type: 'T', usage: 'B', row: 5, column: 2) + Dds("STAMP", 26, type: 'Z', usage: 'B', row: 7, column: 2));
        Compile(File() + "eval DAY = %date('2026-10-17');\neval CLOCK = %time('12:34:56');\neval STAMP = %timestamp('2026-10-17T12:34:56');\n" +
            "exfmt ENTRY;\nsndpgmmsg %char(DAY) + ':' + %char(CLOCK);\nseton LR;");
        using var menu = Menu(); Assert.Equal("ProgramDisplay", Command(menu, "CALL QGPL/APP").State);
        Assert.Contains("2026-10-17", menu.Buffer.RowText(3)); Assert.Contains("12:34:56", menu.Buffer.RowText(5));
        menu.Handle(new(AidKey.Enter)); Assert.Contains("2026-10-17:12:34:56", Status(menu));
    }

    [Fact]
    public void Runtime_EXTFILE_can_select_a_compatible_file_without_changing_the_compiled_description()
    {
        Display(Screen); Display(Screen, "ACTUAL");
        Compile("**free\ndcl-f DSP workstn extdesc('QGPL/SCREEN') extfile('QGPL/ACTUAL');\neval NAME = 'ACTUAL';\nexfmt ENTRY;\nseton LR;");
        using var menu = Menu(); Assert.Equal("ProgramDisplay", Command(menu, "CALL QGPL/APP").State); Assert.Contains("ACTUAL", menu.Buffer.RowText(4)); menu.Handle(new(AidKey.Enter));
    }

    [Fact]
    public void Public_program_binds_a_job_private_DSPF_and_checks_each_callers_own_layout()
    {
        var calls = 0; var terminal = new Terminal(display => { calls++; return display.Session.Handle(new(AidKey.Enter)); });
        using var first = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), _timeout.Token, terminal);
        using var second = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), _timeout.Token, terminal);
        using (OperationIdentity.Enter("QSECOFR", first.Job.Key)) new DisplayFileStore(_system.Connections).Create("QTEMP", "SCREEN", Screen);
        using (OperationIdentity.Enter("QSECOFR", second.Job.Key)) new DisplayFileStore(_system.Connections).Create("QTEMP", "SCREEN", Screen.Replace("   10A", "   12A", StringComparison.Ordinal));
        _sources.SaveSourceMember("QGPL", "SOURCE", "APP", "**free\ndcl-f DSP workstn extdesc('QTEMP/SCREEN') extfile(*extdesc);\n" +
            "eval NAME = 'PRIVATE';\nexfmt ENTRY;\nsndpgmmsg %trim(NAME);\nseton LR;", null);
        var created = first.Execute("CRTBNDRPG PGM(QGPL/APP) SRCFILE(QGPL/SOURCE)"); Assert.False(created.IsError, created.Message);
        var result = first.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Contains("PRIVATE", result.Message);
        result = second.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Contains("recompile", result.Message); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("*NEW")]
    [InlineData("SCREENS")]
    public void Activation_group_return_and_reclaim_release_retained_workstation_allocations(string group)
    {
        Display(Screen); Compile(File() + "exfmt ENTRY;\nreturn;");
        var descriptor = _system.Objects.GetRequired("QGPL", "APP", ObjectType.Program);
        descriptor.ExtendedAttributes = new Dictionary<string, string>(descriptor.ExtendedAttributes!) { ["ipc.program.activationGroup"] = group }; _system.Objects.Update(descriptor);
        using var menu = Menu(); Command(menu, "CALL QGPL/APP"); menu.Handle(new(AidKey.Enter));
        if (group == "SCREENS")
        {
            Assert.Contains(Allocations(), a => a.Lifetime == "RpgDisplay"); Command(menu, "RCLACTGRP ACTGRP(SCREENS)");
        }
        Assert.DoesNotContain(Allocations(), a => a.Lifetime == "RpgDisplay");
    }

    [Fact]
    public void Live_display_authority_is_rechecked_when_a_suspended_RPG_program_resumes()
    {
        Display(Screen); Compile(File() + "exfmt ENTRY;\nsndpgmmsg 'UNREACHED';\nseton LR;");
        _system.Security.Profiles.Create(new UserProfile { Name = "READER", InitialMenu = "*MAIN" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("READER"), "RpgReader22");
        using var menu = Menu("READER"); Assert.Equal("ProgramDisplay", Command(menu, "CALL QGPL/APP").State);
        var descriptor = _system.Objects.GetRequired("QGPL", "SCREEN", ObjectType.File); descriptor.PublicAuthority = AuthorityBit.None; _system.Objects.Update(descriptor);
        menu.Handle(new(AidKey.Enter)); Assert.DoesNotContain("UNREACHED", Status(menu)); Assert.Contains("CPF9802", Status(menu));
    }

    [Fact]
    public void Failed_READC_byte_conversion_preserves_receivers_RRN_and_the_changed_record_for_retry()
    {
        Display(Rows.Replace("   10A", "    4A", StringComparison.Ordinal));
        Compile(File("sfile(ROWS:RRN)") + "dcl-s RRN packed(4:0);\ndcl-s COUNT packed(4:0);\neval COUNT = COUNT + 1;\n" +
            "if COUNT = 1;\neval RRN = 1;\neval VALUE = 'ONE';\nwrite ROWS;\nexfmt CTL;\nendif;\neval VALUE = 'KEEP';\neval RRN = 9;\n" +
            "readc ROWS;\nsndpgmmsg 'INCORRECTLY CONSUMED';\nseton LR;\non-error;\nif VALUE = 'KEEP' and RRN = 9;\n" +
            "sndpgmmsg 'PRESERVED:' + %char(COUNT);\nendif;\nif COUNT = 2;\nseton LR;\nendif;");
        var profile = _system.Security.Profiles.Get("QSECOFR"); profile.Ccsid = 1208; _system.Security.Profiles.Update(profile);
        using var menu = Menu(); Assert.Equal("ProgramDisplay", Command(menu, "CALL QGPL/APP").State);
        menu.Handle(new(AidKey.None, Edit: CursorEdit.FieldEraseToEnd)); Type(menu, "ééé");
        menu.Handle(new(AidKey.Enter)); Assert.Equal("PRESERVED:1", Status(menu));
        Assert.Equal("Menu", Command(menu, "CALL QGPL/APP").State); Assert.Equal("PRESERVED:2", Status(menu));
        Assert.DoesNotContain(Allocations(), a => a.Lifetime == "RpgDisplay");
    }

    [Fact]
    public void Multiple_workstations_keep_separate_renamed_records_and_prefixed_fields()
    {
        Display(Screen); Display(Screen, "SECOND");
        Compile(File("rename(ENTRY:FIRST) prefix(A_)") +
            "dcl-f SECOND workstn extdesc('QGPL/SECOND') extfile(*extdesc) rename(ENTRY:SECONDREC) prefix(B_);\n" +
            "eval A_NAME = 'FIRST';\neval B_NAME = 'SECOND';\nexfmt FIRST;\nexfmt SECONDREC;\nsndpgmmsg %trim(A_NAME) + ':' + %trim(B_NAME);\nseton LR;");
        using var menu = Menu(); Command(menu, "CALL QGPL/APP"); Assert.Contains("FIRST", menu.Buffer.RowText(4));
        menu.Handle(new(AidKey.Enter)); Assert.Contains("SECOND", menu.Buffer.RowText(4)); menu.Handle(new(AidKey.Enter));
        Assert.Equal("FIRST:SECOND", Status(menu));
    }

    [Theory]
    [InlineData("dcl-f SCREEN workstn bogus;")]
    [InlineData("dcl-f SCREEN workstn usropn usropn;")]
    [InlineData("dcl-f SCREEN workstn prefix();")]
    [InlineData("dcl-f SCREEN workstn extdesc(VAR);")]
    [InlineData("dcl-f SCREEN workstn usage(*UPDATE);")]
    [InlineData("dcl-s VALUE uns(10);")]
    [InlineData("dcl-s VALUE packed(40:0);")]
    [InlineData("dcl-s VALUE packed(4:0) inz(VAR);")]
    [InlineData("dcl-s VALUE packed(4:0) inz(99999);")]
    [InlineData("dcl-s VALUE date inz('invalid');")]
    [InlineData("return;\ndcl-s VALUE char(4);")]
    public void Unsupported_or_late_free_declarations_fail_instead_of_ignoring_options(string source)
    {
        Display(Screen); var result = Create("**free\n" + source); Assert.True(result.IsError);
        Assert.Contains(source.StartsWith("return;", StringComparison.Ordinal) ? "SOURCE(APP):3:1" : "SOURCE(APP):2:1", result.Message);
    }

    [Theory]
    [InlineData("exfmt ENTRY DS;")]
    [InlineData("readc ENTRY;")]
    [InlineData("update ENTRY;")]
    [InlineData("readp SCREEN;")]
    [InlineData("delete ENTRY;")]
    [InlineData("exfmt UNKNOWN;")]
    [InlineData("write ENTRY DS;")]
    [InlineData("read SCREEN DS;")]
    public void Unsupported_display_operands_fail_at_compile_time(string operation)
    {
        Display(Screen); var result = Create(File() + operation); Assert.True(result.IsError); Assert.Contains("WORKSTN", result.Message);
    }

    [Theory]
    [InlineData("sfile(ROWS:MISSING)", "")]
    [InlineData("sfile(ROWS:RRN)", "dcl-s RRN char(4);")]
    [InlineData("sfile(ROWS:RRN)", "dcl-s RRN packed(1:0);")]
    [InlineData("sfile(ROWS:RRN)", "dcl-s RRN packed(4:2);")]
    [InlineData("", "dcl-s RRN packed(4:0);")]
    public void SFILE_requires_a_declared_RRN_that_can_address_the_subfile(string options, string scalar)
    {
        Display(Rows); Assert.True(Create(File(options) + scalar + "\nreturn;").IsError);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("layout")]
    public void Saved_workstation_metadata_rejects_duplicate_properties_wrong_hash_and_version(string defect)
    {
        Display(Screen); Compile(File() + "return;");
        var descriptor = _system.Objects.GetRequired("QGPL", "APP", ObjectType.Program);
        var json = descriptor.ExtendedAttributes![RpgWorkstationBindings.AttributeName];
        json = defect switch { "key" => json.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            "hash" => json.Replace("\"textHash\":\"", "\"textHash\":\"0", StringComparison.Ordinal),
            "layout" => json.Replace("\"length\":10", "\"length\":11", StringComparison.Ordinal), _ => json.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal) };
        Assert.Throws<InvalidDataException>(() => RpgWorkstationBindings.Restore(descriptor.Source!, json));
    }

    public void Dispose() { _timeout.Dispose(); _system.Dispose(); }
}
