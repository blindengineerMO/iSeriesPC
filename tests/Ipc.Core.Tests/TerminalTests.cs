using Ipc.Terminal;
using Xunit;

namespace Ipc.Core.Tests.Terminal;

public class DisplayBufferTests
{
    [Fact]
    public void Default_buffer_is_24x80_blanks()
    {
        var buffer = new DisplayBuffer();
        Assert.Equal(24, buffer.Rows);
        Assert.Equal(80, buffer.Columns);
        Assert.Equal(' ', buffer[1, 1].Value);
        Assert.Equal(' ', buffer[24, 80].Value);
    }

    [Fact]
    public void Write_fills_row_green_and_wraps_at_column_80()
    {
        var buffer = new DisplayBuffer();
        buffer.MoveCursor(1, 1);
        buffer.Write(new string('X', 80));

        Assert.Equal('X', buffer[1, 80].Value);

        buffer.Write("Y");
        Assert.Equal('Y', buffer[2, 1].Value);
    }

    [Fact]
    public void Write_at_end_of_screen_scrolls_up()
    {
        var buffer = new DisplayBuffer();
        buffer.MoveCursor(24, 79);
        buffer.Write("ZZZ");

        var lastLine = buffer.RowText(24);
        Assert.Equal('Z', buffer[23, 79].Value);
        Assert.Equal('Z', buffer[23, 80].Value);
        Assert.Equal('Z', buffer[24, 1].Value);
        Assert.Equal(' ', buffer[24, 2].Value);
        Assert.NotEqual('Z', buffer[1, 1].Value);
        Assert.Contains("Z", lastLine);
    }

    [Fact]
    public void Clear_screen_resets_all_cells()
    {
        var buffer = new DisplayBuffer();
        buffer.MoveCursor(1, 1);
        buffer.Write("HELLO");
        buffer.ClearScreen();

        Assert.All(buffer.Positions(), position => Assert.Equal(' ', buffer[position.Row, position.Column].Value));
    }

    [Fact]
    public void Attribute_is_applied_per_cell()
    {
        var buffer = new DisplayBuffer();
        buffer.MoveCursor(2, 10);
        buffer.Write("AB", DisplayAttribute.HighIntensity);

        Assert.Equal(DisplayAttribute.HighIntensity, buffer[2, 10].Attributes);
        Assert.Equal(DisplayAttribute.HighIntensity, buffer[2, 11].Attributes);
        Assert.Equal(DisplayAttribute.None, buffer[2, 9].Attributes);
    }

    [Fact]
    public void Scroll_up_keeps_top_content_within_buffer()
    {
        var buffer = new DisplayBuffer();
        buffer.MoveCursor(1, 1);
        buffer.Write("first");
        buffer.MoveCursor(2, 1);
        buffer.Write("second");

        buffer.ScrollUp();

        Assert.Equal('s', buffer[1, 1].Value);
        Assert.Equal(' ', buffer[24, 1].Value);
    }

    [Fact]
    public void Insert_and_delete_line_shift_content()
    {
        var buffer = new DisplayBuffer();
        buffer.MoveCursor(1, 1);
        buffer.Write("keep");
        buffer.MoveCursor(2, 1);
        buffer.Write("bottom");

        buffer.InsertLine(1);

        Assert.Equal(' ', buffer[1, 1].Value);
        Assert.Equal('k', buffer[2, 1].Value);
        Assert.Equal('b', buffer[3, 1].Value);

        buffer.DeleteLine(2);

        Assert.Equal('b', buffer[2, 1].Value);
        Assert.Equal(' ', buffer[3, 1].Value);
    }

    [Fact]
    public void Set_column_writes_vertically()
    {
        var buffer = new DisplayBuffer();
        buffer.SetColumn(10, "123");

        Assert.Equal('1', buffer[1, 10].Value);
        Assert.Equal('2', buffer[2, 10].Value);
        Assert.Equal('3', buffer[3, 10].Value);
    }
}

public class AnsiRendererTests
{
    [Fact]
    public void Render_starts_with_clear_and_home()
    {
        var buffer = new DisplayBuffer();
        var rendered = new AnsiRenderer().Render(buffer);

        Assert.StartsWith("\u001b[2J\u001b[H", rendered);
        Assert.Contains(" \u001b[", rendered);
    }

    [Fact]
    public void High_intensity_renders_as_bold()
    {
        var buffer = new DisplayBuffer(1, 4);
        buffer.MoveCursor(1, 1);
        buffer.Write("AB", DisplayAttribute.HighIntensity);
        buffer.Write("CD");

        var rendered = new AnsiRenderer().Render(buffer);
        Assert.Contains("1;", rendered);
        Assert.Contains("22;", rendered);
    }

    [Fact]
    public void Diff_returns_empty_and_no_change_when_identical()
    {
        var a = new DisplayBuffer(2, 2);
        var b = new DisplayBuffer(2, 2);
        b.MoveCursor(1, 1);
        b.Write("X");
        a.MoveCursor(1, 1);
        a.Write("X");

        var diff = new AnsiRenderer().RenderDiff(a, b, out var changed);
        Assert.False(changed);
        Assert.Equal("", diff);
    }

    [Fact]
    public void Diff_reports_only_changed_cells()
    {
        var a = new DisplayBuffer(24, 80);
        var b = new DisplayBuffer(24, 80);
        b.MoveCursor(5, 5);
        b.Write("X");

        a.MoveCursor(5, 5);
        a.Write("Y");

        var diff = new AnsiRenderer().RenderDiff(a, b, out var changed);
        Assert.True(changed);
        Assert.Contains("\u001b[5;5H", diff);
        Assert.EndsWith("\u001b[5;6H", diff); // Restore the logical cursor after painting the changed cell.
    }
}

public class DisplayFormTests
{
    private static DisplayForm CreateForm(out DisplayBuffer buffer)
    {
        buffer = new DisplayBuffer();
        var fields = new[]
        {
            new InputField { Row = 2, Column = 10, Length = 10, TabOrder = 0, Usage = FieldUsage.AlphaNumeric },
            new InputField { Row = 3, Column = 10, Length = 5, TabOrder = 1, Usage = FieldUsage.Numeric },
            new InputField { Row = 4, Column = 10, Length = 8, TabOrder = 2, Usage = FieldUsage.AlphaNumeric, Hidden = true },
        };
        return new DisplayForm(buffer, fields);
    }

    [Fact]
    public void Write_value_paints_into_buffer_with_input_attribute()
    {
        var form = CreateForm(out var buffer);
        form.WriteValue(0, "MATTHEW");

        Assert.Equal('M', buffer[2, 10].Value);
        Assert.Equal('W', buffer[2, 16].Value);
        Assert.Equal(DisplayAttribute.InputField, buffer[2, 10].Attributes & DisplayAttribute.InputField);
    }

    [Fact]
    public void Hidden_field_stores_text_but_renders_asterisks()
    {
        var form = CreateForm(out var buffer);
        form.WriteValue(2, "SECRET");

        Assert.Equal("SECRET", form.ReadValue(2));
        Assert.Equal('*', buffer[4, 10].Value);
        Assert.Equal('*', buffer[4, 15].Value);
        Assert.Equal(' ', buffer[4, 16].Value);
    }

    [Fact]
    public void Editor_types_characters_into_active_field()
    {
        var form = CreateForm(out _);
        var editor = new FieldEditor(form);

        editor.Apply('A');
        editor.Apply('B');
        editor.Apply('C');

        Assert.Equal("ABC", form.Active.Text);
    }

    [Fact]
    public void Editor_preserves_spaces_typed_mid_value()
    {
        var form = CreateForm(out _);
        var editor = new FieldEditor(form);

        foreach (var ch in "GO MAJOR")
        {
            editor.Apply(ch);
        }

        Assert.Equal("GO MAJOR", form.Active.Text);
    }

    [Fact]
    public void Numeric_field_rejects_letters()
    {
        var form = CreateForm(out _);
        var editor = new FieldEditor(form);
        editor.ApplyEdit(CursorEdit.NextField);

        Assert.Equal(EditingResult.Rejected, editor.Apply('Z'));
        Assert.Equal("", form.Active.Text);

        Assert.Equal(EditingResult.Accepted, editor.Apply('4'));
    }

    [Fact]
    public void Tab_and_shift_tab_move_between_fields()
    {
        var form = CreateForm(out _);
        var editor = new FieldEditor(form);

        editor.ApplyEdit(CursorEdit.NextField);
        Assert.Equal(1, form.ActiveIndex);

        editor.ApplyEdit(CursorEdit.PreviousField);
        Assert.Equal(0, form.ActiveIndex);

        editor.ApplyEdit(CursorEdit.PreviousField);
        Assert.Equal(0, form.ActiveIndex);
    }

    [Fact]
    public void Field_exit_clears_remainder_and_advances()
    {
        var form = CreateForm(out _);
        var editor = new FieldEditor(form);

        editor.Apply('X');
        editor.ApplyEdit(CursorEdit.FieldExit);

        Assert.Equal("X", form.ReadValue(0));
        Assert.Equal(0, form.Active.CursorOffset);
        Assert.Equal(1, form.ActiveIndex);
    }

    [Fact]
    public void KeyTranslator_maps_enter_and_function_keys()
    {
        Assert.Equal(AidKey.Enter, KeyTranslator.Translate("\r")!.Value.Aid);
        Assert.Equal(AidKey.Enter, KeyTranslator.Translate("\n")!.Value.Aid);
        Assert.Equal(AidKey.Pf3, KeyTranslator.Translate("\u001bOR")!.Value.Aid);
        Assert.Equal(AidKey.Pf12, KeyTranslator.Translate("\u001b[24~")!.Value.Aid);
        Assert.Equal(CursorEdit.CursorRight, KeyTranslator.Translate("\u001b[C")!.Value.Edit);
        Assert.Equal(CursorEdit.NextField, KeyTranslator.Translate("\t")!.Value.Edit);
    }

    [Fact]
    public void Plain_char_sequences_translate_to_character_input()
    {
        var key = KeyTranslator.Translate("X", pendingChar: 'X');
        Assert.NotNull(key);
        Assert.Equal('X', key!.Value.Character);
    }
}

public class ScrollingDisplayTests
{
    [Fact]
    public void Addline_accumulates_and_scrolls()
    {
        var display = new ScrollingDisplay(24, 80);

        for (var i = 1; i <= 30; i++)
        {
            display.AddLine($"line {i}");
        }

        var text = display.Buffer.RowText(24);
        Assert.Contains("line 30", text);
        Assert.NotEqual("", text);
    }

    [Fact]
    public void Clear_resets_buffer()
    {
        var display = new ScrollingDisplay(24, 80);
        display.AddLine("hello");
        display.Clear();

        Assert.Equal(new string(' ', 80), display.Buffer.RowText(24));
    }
}