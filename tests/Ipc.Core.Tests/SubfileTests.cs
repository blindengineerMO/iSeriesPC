using Ipc.Dsp;
using Ipc.Terminal;
using static Ipc.Core.Tests.DisplayPanelTests;

namespace Ipc.Core.Tests;

public sealed class SubfileTests
{
    private static PanelDefinition Definition(string extra = "", string check = "", bool window = false) => new DisplayDdsCompiler().Compile("LIST", 
        Dds(keywords: "CA03(03)") + Dds("ROWS", record: true, keywords: "SFL") +
        Dds("VALUE", 4, usage: 'B', row: 3, column: 2, keywords: check) +
        Dds("DETAIL", 6, row: 4, column: 2) + Dds("KEY", 8, usage: 'H') +
        Dds("CTL", record: true, keywords: "SFLCTL(ROWS) SFLSIZ(10) SFLPAG(2) SFLDSP SFLDSPCTL PAGEDOWN(90) PAGEUP(91) " + extra + (window ? " WINDOW(5 10 8 30)" : "")) +
        Dds(row: 1, column: 2, keywords: "'Items'"));
    private static Dictionary<string, object?> Values(int n) => new() { ["VALUE"] = n.ToString(), ["DETAIL"] = "Row" + n, ["KEY"] = "secret" + n };
    [Fact]
    public void Bounded_storage_copies_values_and_indicators_and_consumes_changed_records_in_RRN_order()
    {
        var definition = Definition(); var state = new SubfileState(definition.Records[0], 10);
        var flags = new bool[100]; flags[21] = true; var values = Values(5);
        state.Write(5, values, flags); flags[21] = false; values["KEY"] = "replaced";
        Assert.True(state.Read(5).Indicators[21]); Assert.Equal("secret5", state.Read(5).Values["KEY"]);
        Assert.Throws<InvalidOperationException>(() => state.Write(5, values));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Write(11, values));
        Assert.Throws<InvalidOperationException>(() => state.Update(3, values));
        Assert.Throws<ArgumentException>(() => state.Write(1, new Dictionary<string, object?> { ["VALUE"] = new object() }));
        Assert.Null(state.ReadNextChanged());
        var changedDefinition = new DisplayDdsCompiler().Compile("NEXT", Dds("ROWS", record: true, keywords: "SFL") +
            Dds(keywords: "SFLNXTCHG", indicators: " 21") + Dds("VALUE", 4, row: 2, column: 2));
        state = new(changedDefinition.Records[0], 10); flags[21] = true;
        state.Write(8, Values(8), flags); state.Write(2, Values(2), flags);
        Assert.Equal(2, state.ReadNextChanged()!.Number); Assert.Equal(8, state.ReadNextChanged(2)!.Number); Assert.Null(state.ReadNextChanged());
        state.Update(2, Values(2), flags); Assert.Equal(2, state.ReadNextChanged()!.Number);
        state.Clear(); Assert.Empty(state.RecordNumbers);
    }
    [Fact]
    public void Paging_saves_edits_without_exposing_hidden_keys_and_returns_only_at_the_boundary()
    {
        var panel = new SubfilePanel(Definition(), "CTL");
        for (var n = 1; n <= 5; n++) panel.State.Write(n, Values(n));
        panel.Write(new Dictionary<string, object?>());
        Assert.Contains("Row1", panel.Buffer.RowText(4)); Assert.DoesNotContain("secret", new AnsiRenderer().Render(panel.Buffer));
        panel.Handle(new(AidKey.None, Character: 'X'));
        var page = panel.Handle(new(AidKey.RollUp)); Assert.False(page.Accepted); Assert.False(page.Indicators[90]); Assert.Equal(3, panel.FirstRecord);
        var changed = panel.State.ReadNextChanged(); Assert.Equal(1, changed!.Number); Assert.Equal("X1", changed.Values["VALUE"]); Assert.Equal("secret1", changed.Values["KEY"]);
        Assert.Null(panel.State.ReadNextChanged()); Assert.Empty(page.Values);
        panel.Handle(new(AidKey.RollUp)); Assert.Equal(5, panel.FirstRecord);
        var boundary = panel.Handle(new(AidKey.RollUp)); Assert.True(boundary.Accepted); Assert.True(boundary.Indicators[90]);
        var back = panel.Handle(new(AidKey.RollDown)); Assert.False(back.Accepted); Assert.False(back.Indicators[91]); Assert.Equal(3, panel.FirstRecord);
        panel.Handle(new(AidKey.RollDown)); Assert.Contains("X1", panel.Buffer.RowText(3));
    }
    [Fact]
    public void Validation_is_atomic_across_rows_and_CA_discards_edited_page()
    {
        var panel = new SubfilePanel(Definition(check: "COMP(NE 'BAD')"), "CTL");
        panel.State.Write(1, Values(1)); panel.State.Write(2, new Dictionary<string, object?> { ["VALUE"] = "BAD" });
        panel.Write(new Dictionary<string, object?>()); panel.Handle(new(AidKey.None, Character: 'X'));
        var invalid = panel.Handle(new(AidKey.RollUp)); Assert.False(invalid.Accepted); Assert.NotNull(invalid.Error);
        Assert.Equal("1", panel.State.Read(1).Values["VALUE"]); Assert.Null(panel.State.ReadNextChanged());
        var cancel = panel.Handle(new(AidKey.Pf3)); Assert.True(cancel.Accepted); Assert.True(cancel.Indicators[3]); Assert.Null(panel.State.ReadNextChanged());
    }
    [Fact]
    public void Folding_retains_truncated_values_and_row_specific_indicators()
    {
        var definition = Definition("SFLDROP(CF11)");
        definition.Records[0].Fields[0].Keywords.Add(new("DSPATR", new[] { "RI" }, new[] { new PanelCondition(21) }, 0));
        var panel = new SubfilePanel(definition, "CTL");
        for (var n = 1; n <= 5; n++) { var flags = new bool[100]; flags[21] = n == 2; panel.State.Write(n, Values(n), flags); }
        panel.Write(new Dictionary<string, object?>()); Assert.False(panel.Folded); Assert.Equal(4, panel.PageCapacity);
        Assert.Contains("2", panel.Buffer.RowText(4)); Assert.True(panel.Buffer[4, 2].Attributes.HasFlag(DisplayAttribute.ReverseVideo));
        Assert.False(panel.Buffer[3, 2].Attributes.HasFlag(DisplayAttribute.ReverseVideo));
        var toggle = panel.Handle(new(AidKey.Pf11)); Assert.False(toggle.Accepted); Assert.True(panel.Folded); Assert.Equal(2, panel.PageCapacity);
        Assert.Contains("Row1", panel.Buffer.RowText(4)); Assert.Contains("Row2", panel.Buffer.RowText(6));
        Assert.Equal("Row3", panel.State.Read(3).Values["DETAIL"]); Assert.True(panel.State.Read(2).Indicators[21]); Assert.Null(panel.State.ReadNextChanged());
    }
    [Fact]
    public void Window_subfile_restores_underlying_unsent_text_and_cursor()
    {
        var definition = Definition(window: true);
        definition.Records.Add(new() { Name = "BASE", Fields = new() { new PanelField { Name = "EDIT", Length = 10, Usage = PanelFieldUsage.Both, Row = 2, Column = 2 } } });
        var file = new DisplayFileSession(definition); file.Write("BASE", new Dictionary<string, object?>()); file.Handle(new(AidKey.None, Character: 'X'));
        var before = file.Buffer.Clone(); file.Subfile("ROWS").Write(1, Values(1));
        file.Write("CTL", new Dictionary<string, object?>()); Assert.Equal(1, file.WindowDepth);
        Assert.Equal('+', file.Buffer[5, 10].Value); Assert.Contains("Items", file.Buffer.RowText(6)); Assert.Contains("Row1", file.Buffer.RowText(9));
        file.Handle(new(AidKey.None, Character: 'Y')); file.Handle(new(AidKey.Enter)); Assert.Equal("Y1", file.Subfile("ROWS").Read(1).Values["VALUE"]);
        file.CloseWindow(); Assert.Equal(0, file.WindowDepth); Assert.Equal(before.Cursor, file.Buffer.Cursor);
        foreach (var position in before.Positions()) Assert.Equal(before[position.Row, position.Column], file.Buffer[position.Row, position.Column]);
        file.Handle(new(AidKey.None, Character: 'Z')); Assert.Equal("XZ", file.Handle(new(AidKey.Enter)).Values["EDIT"]);
    }
    [Fact]
    public void Message_subfiles_keep_opaque_keys_hidden_page_without_input_and_select_context_help()
    {
        var definition = new DisplayDdsCompiler().Compile("MESSAGES", Dds(keywords: "HELP CA03(03)") +
            Dds("MSGS", record: true, keywords: "SFL SFLMSGRCD(4)") + Dds("KEY", keywords: "SFLMSGKEY") + Dds("QUEUE", keywords: "SFLPGMQ") +
            Dds("CTL", record: true, keywords: "SFLCTL(MSGS) SFLSIZ(10) SFLPAG(2) SFLDSP SFLDSPCTL"));
        var requests = new List<(string, string)>();
        var file = new DisplayFileSession(definition, programMessages: (queue, key) =>
        {
            requests.Add((queue, key)); return new DisplayMessage("Message " + (int)key[3], "HELP" + (int)key[3]);
        });
        for (var n = 1; n <= 3; n++) file.Subfile("MSGS").Write(n, new Dictionary<string, object?> { ["KEY"] = "\0\u00ff\0" + (char)n, ["QUEUE"] = "PGMQUEUE" });
        file.Write("CTL", new Dictionary<string, object?>());
        Assert.Equal(new CellPosition(4, 2), file.Buffer.Cursor); Assert.Contains("Message 1", file.Buffer.RowText(4));
        Assert.True(file.Buffer[4, 2].Attributes.HasFlag(DisplayAttribute.HighIntensity));
        Assert.DoesNotContain("PGMQUEUE", new AnsiRenderer().Render(file.Buffer)); Assert.Equal("HELP1", file.Handle(new(AidKey.Pf1)).HelpId);
        file.Handle(new(AidKey.None, Edit: CursorEdit.CursorDown)); Assert.Equal("HELP2", file.Handle(new(AidKey.Pf1)).HelpId);
        var cursor = file.Buffer.Cursor; Assert.False(file.Handle(new(AidKey.RollUp)).Accepted); Assert.Equal(cursor, file.Buffer.Cursor);
        Assert.Contains("Message 3", file.Buffer.RowText(4)); Assert.Null(file.Subfile("MSGS").ReadNextChanged());
        Assert.Equal("\0\u00ff\0\u0003", requests.Last().Item2);
        var entered = file.Handle(new(AidKey.Enter)); Assert.True(entered.Accepted); Assert.Empty(entered.Values);
    }
    [Fact]
    public void Rewriting_control_preserves_current_page_and_clear_removes_all_records()
    {
        var definition = Definition(); definition.Records[1].Keywords.Add(new("SFLCLR", Array.Empty<string>(), new[] { new PanelCondition(25) }, 0));
        var file = new DisplayFileSession(definition);
        for (var n = 1; n <= 5; n++) file.Subfile("ROWS").Write(n, Values(n));
        file.Write("CTL", new Dictionary<string, object?>()); file.Handle(new(AidKey.RollUp));
        file.Write("CTL", new Dictionary<string, object?>()); Assert.Contains("Row3", file.Buffer.RowText(4));
        var flags = new bool[100]; flags[25] = true; file.Write("CTL", new Dictionary<string, object?>(), flags);
        Assert.Equal(0, file.Subfile("ROWS").Count); Assert.DoesNotContain("Row3", file.Buffer.RowText(4));
    }
    [Fact]
    public void Failed_window_output_does_not_replace_underlying_editor()
    {
        var definition = Definition(window: true); var file = new DisplayFileSession(definition);
        file.Subfile("ROWS").Write(1, Values(1)); file.Write("CTL", new Dictionary<string, object?>());
        file.Handle(new(AidKey.None, Character: 'X')); var before = file.Buffer.Clone();
        definition.Records.Add(new PanelRecord { Name = "BAD", Keywords = new() { new("WINDOW", new[] { "2", "2", "5", "20" }, Array.Empty<PanelCondition>(), 0) },
            Fields = new() { new PanelField { Name = "VALUE", Length = 5, Row = 1, Column = 1 } } });
        Assert.Throws<ArgumentException>(() => file.Write("BAD", new Dictionary<string, object?> { ["VALUE"] = new object() }));
        Assert.Equal(1, file.WindowDepth); Assert.Equal(before.Cursor, file.Buffer.Cursor);
        Assert.Equal("X1", file.Handle(new(AidKey.Enter)).Accepted ? file.Subfile("ROWS").Read(1).Values["VALUE"] : null);
    }
    [Fact]
    public void Unedited_numeric_input_is_not_changed_but_editing_back_to_the_original_value_is_changed()
    {
        var definition = new DisplayDdsCompiler().Compile("NUMBERS", Dds("ROWS", record: true, keywords: "SFL") +
            Dds("VALUE", 4, 'S', 'B', 2, 2, decimals: 0) +
            Dds("CTL", record: true, keywords: "SFLCTL(ROWS) SFLSIZ(10) SFLPAG(2) SFLDSP SFLDSPCTL"));
        var panel = new SubfilePanel(definition, "CTL"); panel.State.Write(1, new Dictionary<string, object?> { ["VALUE"] = 7 });
        panel.Write(new Dictionary<string, object?>()); Assert.True(panel.Handle(new(AidKey.Enter)).Accepted); Assert.Null(panel.State.ReadNextChanged());
        panel.Handle(new(AidKey.None, Character: '1')); panel.Handle(new(AidKey.None, Edit: CursorEdit.FieldBackspace));
        Assert.True(panel.Handle(new(AidKey.Enter)).Accepted); Assert.Equal(7m, panel.State.ReadNextChanged()!.Values["VALUE"]);
        Assert.True(panel.Handle(new(AidKey.Enter)).Accepted); Assert.Null(panel.State.ReadNextChanged());
    }
    [Fact]
    public void Independent_file_opens_and_modal_windows_do_not_share_record_state()
    {
        var definition = Definition(window: true); var first = new DisplayFileSession(definition); var second = new DisplayFileSession(definition);
        first.Subfile("ROWS").Write(1, Values(1)); Assert.Equal(0, second.Subfile("ROWS").Count);
        first.Write("CTL", new Dictionary<string, object?>()); first.Write("CTL", new Dictionary<string, object?>()); Assert.Equal(1, first.WindowDepth);
        for (var i = 0; i < 12; i++) definition.Records.Add(new() { Name = "WIN" + i, Keywords = new() { new("WINDOW", new[] { "2", "2", "5", "20" }, Array.Empty<PanelCondition>(), 0) },
            Fields = new() { new PanelField { Name = "VALUE", Length = 5, Usage = PanelFieldUsage.Both, Row = 1, Column = 1 } } });
        for (var i = 0; i < 11; i++) first.Write("WIN" + i, new Dictionary<string, object?>());
        Assert.Equal(12, first.WindowDepth); Assert.Throws<InvalidOperationException>(() => first.Write("WIN11", new Dictionary<string, object?>()));
        first.CloseWindow(); Assert.Equal(11, first.WindowDepth);
    }
    [Theory]
    [InlineData("SFLPAG(20)")]
    [InlineData("SFLSIZ(1)")]
    [InlineData("WINDOW(20 70 8 30)")]
    [InlineData("SFLFOLD(CA03)")]
    public void Invalid_layouts_fail_at_compile_time(string keyword)
    {
        Assert.Throws<PanelCompileException>(() => Definition(keyword));
    }
}
