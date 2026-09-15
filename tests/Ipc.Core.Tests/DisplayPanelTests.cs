using Ipc.Dsp;
using Ipc.Terminal;
using Ipc.Services;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Session;
using Ipc.Console.Session;

namespace Ipc.Core.Tests;

public sealed class DisplayPanelTests
{
    internal static string Dds(string name = "", int length = 0, char type = 'A', char usage = 'O', int row = 0, int column = 0,
        string keywords = "", bool record = false, string indicators = "", int? decimals = null)
    {
        var chars = new string(' ', 44).ToCharArray(); chars[5] = 'A';
        if (record) chars[16] = 'R';
        name.CopyTo(0, chars, 18, name.Length);
        if (!record && name.Length > 0)
        {
            length.ToString().PadLeft(5).CopyTo(0, chars, 29, 5); chars[34] = type; chars[37] = usage;
            if (decimals is { } number) number.ToString().PadLeft(2).CopyTo(0, chars, 35, 2);
        }
        if (row != 0) row.ToString().PadLeft(3).CopyTo(0, chars, 38, 3);
        if (column != 0) column.ToString().PadLeft(3).CopyTo(0, chars, 41, 3);
        indicators.CopyTo(0, chars, 7, indicators.Length);
        return new string(chars) + keywords + "\n";
    }
    private static string Source => Dds(keywords: "DSPSIZ(24 80) INDARA CA03(03) CF06(06)") +
        Dds("ENTRY", record: true) + Dds(row: 2, column: 4, keywords: "'Customer entry' COLOR(WHT)") +
        Dds("NAME", 10, usage: 'B', row: 4, column: 4, keywords: "CHECK(ME LC) ALIAS(CUSTOMER)") +
        Dds("SECRET", 8, usage: 'H') + Dds("NOTICE", 12, row: 6, column: 4, keywords: "DFT('Needs review') DSPATR(RI)", indicators: " 21");
    [Fact]
    public void Compiler_uses_real_DDS_columns_and_reports_source_locations_for_unsupported_keywords()
    {
        var definition = new DisplayDdsCompiler().Compile("ENTRY", Source, "QDDSSRC/ENTRY");
        Assert.Equal(24, definition.Rows); Assert.Equal(4, definition.Records[0].Fields.Count);
        Assert.Equal("CUSTOMER", definition.Records[0].Fields[1].BindingName);
        Assert.Equal(definition.ToJson(), PanelDefinition.FromJson(definition.ToJson()).ToJson());
        var error = Assert.Throws<PanelCompileException>(() => new DisplayDdsCompiler().Compile("BAD", Source + Dds(keywords: "UNSUPPORTED(1)"), "QDDSSRC/BAD"));
        Assert.Equal("UNSUPPORTED", error.Token); Assert.Equal(7, error.Line); Assert.Contains("QDDSSRC/BAD:7:45", error.Message);
    }
    [Fact]
    public void CA_bypasses_input_transfer_and_validation_while_CF_returns_edited_aliases_and_indicators()
    {
        var panel = new PanelSession(new DisplayDdsCompiler().Compile("ENTRY", Source));
        var values = new Dictionary<string, object?> { ["CUSTOMER"] = "", ["SECRET"] = "hidden" };
        panel.Write("ENTRY", values); var rejected = panel.Handle(new(AidKey.Pf6));
        Assert.False(rejected.Accepted); Assert.Contains("Enter", rejected.Error);
        panel.Handle(new(AidKey.None, Character: 'a'));
        var cancelled = panel.Handle(new(AidKey.Pf3)); Assert.True(cancelled.Accepted); Assert.True(cancelled.Indicators[3]);
        Assert.Equal("", cancelled.Values["CUSTOMER"]); Assert.Equal("hidden", cancelled.Values["SECRET"]);
        panel.Write("ENTRY", values);
        panel.Handle(new(AidKey.None, Character: 'b'));
        var accepted = panel.Handle(new(AidKey.Pf6)); Assert.True(accepted.Accepted); Assert.True(accepted.Indicators[6]); Assert.False(accepted.Indicators[3]);
        Assert.Equal("b", accepted.Values["CUSTOMER"]); Assert.Equal(new CellPosition(4, 5), accepted.Cursor);
    }
    [Fact]
    public void Indicator_conditions_defaults_attributes_and_hidden_data_are_preserved()
    {
        var panel = new PanelSession(new DisplayDdsCompiler().Compile("ENTRY", Source));
        var values = new Dictionary<string, object?> { ["CUSTOMER"] = "A", ["SECRET"] = "private" };
        var indicators = new bool[100]; panel.Write("ENTRY", values, indicators);
        Assert.Equal(' ', panel.Buffer[6, 4].Value);
        indicators[21] = true; panel.Write("ENTRY", values, indicators);
        Assert.Equal('N', panel.Buffer[6, 4].Value); Assert.True(panel.Buffer[6, 4].Attributes.HasFlag(DisplayAttribute.ReverseVideo));
        var ansi = new AnsiRenderer().Render(panel.Buffer); Assert.DoesNotContain("private", ansi); Assert.Contains("Customer entry", ansi);
        var defaults = Dds("REC", record: true) + Dds("VALUE", 5, usage: 'B', row: 2, column: 2, keywords: "DFTVAL('FIRST')");
        var initialized = new PanelSession(new DisplayDdsCompiler().Compile("TEST", defaults));
        initialized.Write("REC", new Dictionary<string, object?> { ["VALUE"] = "LAST" }); Assert.Equal('F', initialized.Buffer[2, 2].Value);
        initialized.Write("REC", new Dictionary<string, object?> { ["VALUE"] = "LAST" }); Assert.Equal('L', initialized.Buffer[2, 2].Value);
    }
    [Fact]
    public void Numeric_validation_and_message_identifiers_use_typed_values_and_the_message_resolver()
    {
        var source = Dds("REC", record: true) + Dds("QTY", 3, 'S', 'B', 2, 2, "COMP(GT 0) CHKMSGID(MSG0001 QGPL/MSGF)", decimals: 0);
        var panel = new PanelSession(new DisplayDdsCompiler().Compile("TEST", source), (id, file) => id == "MSG0001" ? "Quantity must be positive." : null);
        panel.Write("REC", new Dictionary<string, object?> { ["QTY"] = 0m });
        var rejected = panel.Handle(new(AidKey.Enter)); Assert.False(rejected.Accepted); Assert.Equal("Quantity must be positive.", rejected.Error);
        panel.Handle(new(AidKey.None, Character: '2')); var accepted = panel.Handle(new(AidKey.Enter));
        Assert.True(accepted.Accepted); Assert.Equal(20m, accepted.Values["QTY"]);
    }
    [Fact]
    public void Persisted_display_files_follow_typed_rename_and_terminal_preview_uses_the_panel_runtime()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        var store = new DisplayFileStore(system.Connections); store.Create("QGPL", "ENTRY", Source);
        system.ObjectOperations.Relocate(new("QGPL", "ENTRY"), ObjectType.File, new("QGPL", "RENAMED"));
        Assert.Equal("ENTRY", store.Load("QGPL", "RENAMED").Name);
        using var menu = new MenuController(system, system.Security.Profiles.Get("QUSER"));
        foreach (var character in "RUNPNL FILE(QGPL/RENAMED)") menu.Handle(new(AidKey.None, Character: character));
        var opened = menu.Handle(new(AidKey.Enter)); Assert.Equal("DisplayPanel", opened.State); Assert.Equal('C', menu.Buffer[2, 4].Value);
        menu.Handle(new(AidKey.Pf3)); Assert.NotEqual('C', menu.Buffer[2, 4].Value);
        using var batch = new ExecutionSession(system, system.Jobs.CreateCommunication("QUSER"), CancellationToken.None);
        Assert.True(batch.Execute("RUNPNL FILE(QGPL/RENAMED)").IsError);
    }
    [Fact]
    public void Source_member_compilation_binds_message_constants_and_reopens_the_persisted_panel()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "DisplayPassword2");
        var messages = new Ipc.Services.Messages.MessageDescriptionStore(system.Connections); messages.Create("QGPL", "TEXTS"); messages.Add("QGPL", "TEXTS", "DSP0001", "Bound message text");
        var source = Dds("REC", record: true) + Dds(row: 2, column: 2, keywords: "MSGCON(5 DSP0001 QGPL/TEXTS)") +
            Dds(row: 3, column: 2, keywords: "MSGCON(18 DSP0001 QGPL/TEXTS)");
        var files = new Ipc.Db.Store.SqliteFileStore(system.Connections, system.Objects); files.CreateSourceFile("QGPL", "QDDSSRC");
        foreach (var line in source.Split('\n', StringSplitOptions.RemoveEmptyEntries)) files.Insert("QGPL", "QDDSSRC", "QDDSSRC", "QDDSSRC", new Dictionary<string, object?> { ["SRCDTA"] = line });
        using var session = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var compiled = session.Execute("CRTDSPF FILE(QGPL/PANEL) SRCFILE(QGPL/QDDSSRC) SRCMBR(QDDSSRC)"); Assert.False(compiled.IsError, compiled.Message);
        system.ObjectOperations.Delete(new("QGPL", "TEXTS"), ObjectType.MessageFile);
        var panel = new PanelSession(new DisplayFileStore(system.Connections).Load("QGPL", "PANEL")); panel.Write("REC", new Dictionary<string, object?>());
        Assert.Equal('B', panel.Buffer[2, 2].Value); Assert.Equal('t', panel.Buffer[3, 19].Value);
        var preview = session.Execute("RUNPNL FILE(QGPL/PANEL) RCDFMT(REC)"); Assert.False(preview.IsError, preview.Message);
    }

    [Theory]
    [InlineData("1", "1,234.50")]
    [InlineData("3", "1234.50")]
    [InlineData("Z", "123450")]
    public void Numeric_edit_codes_reserve_display_columns_and_use_exact_decimal_values(string code, string expected)
    {
        var source = Dds("REC", record: true) + Dds("AMOUNT", 7, 'Y', 'O', 2, 2, "EDTCDE(" + code + ")", decimals: 2);
        var panel = new PanelSession(new DisplayDdsCompiler().Compile("TEST", source));
        panel.Write("REC", new Dictionary<string, object?> { ["AMOUNT"] = 1234.50m });
        Assert.Contains(expected, new AnsiRenderer().Render(panel.Buffer));
    }

    [Fact]
    public void Panel_output_rejects_terminal_control_sequences()
    {
        var panel = new PanelSession(new DisplayDdsCompiler().Compile("ENTRY", Source));
        Assert.Throws<ArgumentException>(() => panel.Write("ENTRY", new Dictionary<string, object?> { ["CUSTOMER"] = "\u001b[2J" }));
        Assert.Throws<PanelCompileException>(() => new DisplayDdsCompiler().Compile("BAD", Dds("REC", record: true) + Dds(row: 2, column: 2, keywords: "'\u001b[2J'")));
    }

    [Fact]
    public void Alternative_page_keys_return_page_indicators_and_errors_clear_their_response_indicator()
    {
        var source = Dds(keywords: "ALTPAGEDWN(CF08) ALTPAGEUP(CF07)") + Dds("REC", record: true, keywords: "PAGEDOWN(25) PAGEUP(26)") +
            Dds("VALUE", 8, usage: 'B', row: 2, column: 2) + Dds(keywords: "ERRMSG('Try again' 31)", indicators: " 31");
        var panel = new PanelSession(new DisplayDdsCompiler().Compile("TEST", source)); var indicators = new bool[100]; indicators[31] = true;
        panel.Write("REC", new Dictionary<string, object?>(), indicators); Assert.Equal("Try again", panel.Error);
        var down = panel.Handle(new(AidKey.Pf8)); Assert.True(down.Accepted); Assert.Equal(AidKey.RollUp, down.Aid); Assert.True(down.Indicators[25]); Assert.False(down.Indicators[31]);
        var up = panel.Handle(new(AidKey.Pf7)); Assert.True(up.Accepted); Assert.True(up.Indicators[26]); Assert.False(up.Indicators[25]);
    }
    [Fact]
    public void Date_validation_cursor_return_and_help_preserve_separate_input_and_response_state()
    {
        var source = Dds(keywords: "HELP") + Dds("REC", record: true, keywords: "RTNCSRLOC(&FORMAT &FIELD &POS)") +
            Dds("DAY", 10, 'L', 'B', 2, 2, "DATFMT(*ISO) HLPID(DATEHELP)") + Dds("FORMAT", 10, usage: 'H') +
            Dds("FIELD", 10, usage: 'H') + Dds("POS", 3, 'S', 'H', decimals: 0);
        var panel = new PanelSession(new DisplayDdsCompiler().Compile("TEST", source));
        panel.Write("REC", new Dictionary<string, object?> { ["DAY"] = "2025-02-29" }); Assert.False(panel.Handle(new(AidKey.Enter)).Accepted);
        panel.Write("REC", new Dictionary<string, object?> { ["DAY"] = "2024-02-29" });
        var help = panel.Handle(new(AidKey.Pf1)); Assert.False(help.Accepted); Assert.Equal("DATEHELP", help.HelpId);
        var response = panel.Handle(new(AidKey.Enter)); Assert.True(response.Accepted); Assert.Equal(new DateOnly(2024, 2, 29), response.Values["DAY"]);
        Assert.Equal("REC", response.Values["FORMAT"]); Assert.Equal("DAY", response.Values["FIELD"]); Assert.Equal(1, response.Values["POS"]);
    }
    [Fact]
    public void Edit_words_use_stop_zero_suppression_and_Z_input_restores_implied_decimal_positions()
    {
        var source = Dds("REC", record: true) + Dds("OUTPUT", 5, 'Y', 'O', 2, 2, "EDTWRD('  0.  -')", decimals: 2) +
            Dds("INPUT", 5, 'Y', 'B', 3, 2, "EDTCDE(Z)", decimals: 2);
        var panel = new PanelSession(new DisplayDdsCompiler().Compile("TEST", source));
        panel.Write("REC", new Dictionary<string, object?> { ["OUTPUT"] = -12.34m, ["INPUT"] = 12.34m });
        Assert.Contains("12.34-", new AnsiRenderer().Render(panel.Buffer));
        var result = panel.Handle(new(AidKey.Enter)); Assert.True(result.Accepted); Assert.Equal(12.34m, result.Values["INPUT"]);
    }

    [Fact]
    public void Explicit_cursor_location_selects_the_correct_input_field_and_fraction_editing_suppresses_leading_zero()
    {
        var source = Dds("REC", record: true, keywords: "CSRLOC(ROW COL)") + Dds("FIRST", 5, usage: 'B', row: 2, column: 2) +
            Dds("SECOND", 5, usage: 'B', row: 3, column: 2) + Dds("ROW", 3, 'S', 'H', decimals: 0) + Dds("COL", 3, 'S', 'H', decimals: 0) +
            Dds("FRACTION", 5, 'Y', 'O', 4, 2, "EDTCDE(1)", decimals: 2);
        var panel = new PanelSession(new DisplayDdsCompiler().Compile("TEST", source));
        panel.Write("REC", new Dictionary<string, object?> { ["ROW"] = 3, ["COL"] = 2, ["FRACTION"] = .04m });
        panel.Handle(new(AidKey.None, Character: 'x')); var result = panel.Handle(new(AidKey.Enter));
        Assert.True(result.Accepted); Assert.Equal("X", result.Values["SECOND"]); Assert.Equal("", result.Values["FIRST"]);
        var output = new AnsiRenderer().Render(panel.Buffer); Assert.Contains(".04", output); Assert.DoesNotContain("0.04", output);
    }

    [Fact]
    public void File_runtime_overlay_preserves_underlying_cells_and_resumes_after_a_window()
    {
        var source = Dds("BASE", record: true) + Dds(row: 2, column: 2, keywords: "'Base'") +
            Dds("WIN", record: true, keywords: "WINDOW(4 4 5 20)") + Dds("INPUT", 5, usage: 'B', row: 1, column: 1) +
            Dds("OVER", record: true, keywords: "OVERLAY") + Dds("EDIT", 5, usage: 'B', row: 3, column: 2);
        var file = new DisplayFileSession(new DisplayDdsCompiler().Compile("TEST", source));
        file.Write("BASE", new Dictionary<string, object?>()); file.Write("WIN", new Dictionary<string, object?>());
        file.Write("OVER", new Dictionary<string, object?>()); Assert.Equal(0, file.WindowDepth);
        Assert.Contains("Base", file.Buffer.RowText(2)); Assert.Equal(' ', file.Buffer[4, 4].Value);
        file.Handle(new(AidKey.None, Character: 'X')); Assert.Equal("X", file.Handle(new(AidKey.Enter)).Values["EDIT"]);
    }

}
