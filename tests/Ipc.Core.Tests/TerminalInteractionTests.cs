using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed class TerminalInteractionTests
{
    private static KeyPress[] Parse(TerminalParser parser, string text) => text.SelectMany(parser.Feed).ToArray();
    [Fact]
    public void All_function_keys_have_exact_unmodified_and_shifted_VT_sequences()
    {
        for (var i = 0; i < 12; i++)
        {
            var parser = new TerminalParser();
            Assert.Equal((AidKey)((int)AidKey.Pf1 + i), Assert.Single(Parse(parser, KeyCodes.FunctionKeys[i])).Aid);
            var shifted = i < 4 ? "\u001b[1;2" + "PQRS"[i] : KeyCodes.FunctionKeys[i][..^1] + ";2~";
            Assert.Equal((AidKey)((int)AidKey.Pf13 + i), Assert.Single(Parse(parser, shifted)).Aid);
            Assert.Null(KeyTranslator.Translate(KeyCodes.FunctionKeys[i] + "suffix"));
        }
    }
    [Fact]
    public void Oversized_unknown_and_fragmented_control_strings_never_become_command_text()
    {
        var parser = new TerminalParser();
        Assert.Empty(Parse(parser, "\u001b[" + new string('1', 100000)));
        Assert.Empty(parser.FlushPending()); Assert.Empty(Parse(parser, ";5~"));
        Assert.Equal('X', Assert.Single(Parse(parser, "X")).Character);
        Assert.Empty(Parse(parser, "\u001b]52;c;dangerous\r")); Assert.Empty(parser.FlushPending());
        Assert.Empty(Parse(parser, "more\r\u001b\\")); Assert.Equal(AidKey.Enter, Assert.Single(Parse(parser, "\r")).Aid);
        Assert.Empty(Parse(parser, "\u001b[999~")); Assert.Empty(Parse(parser, "\u001b[1;5Q"));
        Parse(parser, "\u001b"); Assert.Equal(AidKey.Pa1, Assert.Single(parser.FlushPending()).Aid); Assert.Empty(parser.FlushPending());
    }
    [Fact]
    public void Bracketed_paste_cannot_submit_commands_or_invoke_attention_keys()
    {
        var parser = new TerminalParser(); var keys = Parse(parser, "\u001b[200~hello\r\nworld\t\u0001\u001bOR\u001b[201~");
        Assert.All(keys, key => Assert.Equal(AidKey.None, key.Aid)); Assert.Equal("hello  world ", new string(keys.Select(k => k.Character).ToArray()));
        Assert.Equal(AidKey.Enter, Assert.Single(Parse(parser, "\r")).Aid);
        Assert.Equal(AidKey.RollUp, Assert.Single(Parse(parser, "\u001b[6~")).Aid);
        Assert.Equal(AidKey.RollDown, Assert.Single(Parse(parser, "\u001b[5~")).Aid);
    }
    [Fact]
    public void Attention_and_edit_keys_are_distinct()
    {
        var parser = new TerminalParser();
        Assert.Equal(new[] { AidKey.Pa1, AidKey.Pa2, AidKey.Pa3, AidKey.SysReq, AidKey.Clear, AidKey.Print }, Parse(parser, "\u0001\u0002\u0006\u0007\u000c\u0010").Select(k => k.Aid));
        Assert.Equal(CursorEdit.FieldBackspace, KeyTranslator.Translate("\u007f")!.Value.Edit);
        Assert.Equal(CursorEdit.Delete, KeyTranslator.Translate("\u001b[3~")!.Value.Edit);
        Assert.Equal(CursorEdit.Insert, KeyTranslator.Translate("\u001b[2~")!.Value.Edit);
    }
    [Fact]
    public void Renderer_restores_cursor_on_full_diff_cursor_only_and_size_changes()
    {
        var renderer = new AnsiRenderer(); var prior = new DisplayBuffer(24, 80); var current = prior.Clone(); current.MoveCursor(4, 7);
        Assert.Equal("\u001b[4;7H", renderer.RenderDiff(current, prior, out var changed)); Assert.True(changed);
        Assert.EndsWith("\u001b[4;7H", renderer.Render(current));
        current.Set(24, 80, 'X'); Assert.EndsWith("\u001b[4;7H", renderer.RenderDiff(current, prior, out changed)); Assert.True(changed);
        var wide = new DisplayBuffer(27, 132); Assert.StartsWith(AnsiRenderer.ClearSequence, renderer.RenderDiff(wide, current, out changed)); Assert.True(changed);
    }
    [Fact]
    public void Renderer_attributes_and_hidden_or_unsafe_glyphs_have_stable_snapshots()
    {
        var attributes = DisplayAttribute.HighIntensity | DisplayAttribute.Underline | DisplayAttribute.Blink | DisplayAttribute.ReverseVideo;
        Assert.Equal("\u001b[1;4;5;7;31mA", AnsiRenderer.RenderCell(new('A', attributes, Ansicolor.Red)));
        Assert.Equal("\u001b[22;24;25;27;32m ", AnsiRenderer.RenderCell(new('S', DisplayAttribute.NonDisplay)));
        Assert.EndsWith("|", AnsiRenderer.RenderCell(new('X', DisplayAttribute.ColumnSeparator)));
        Assert.EndsWith("?", AnsiRenderer.RenderCell(new('\u001b', DisplayAttribute.None)));
        Assert.False(TerminalGlyph.IsSingleCell('中')); Assert.False(TerminalGlyph.IsSingleCell('\u0301')); Assert.True(TerminalGlyph.IsSingleCell('é'));
    }
    [Fact]
    public void Insert_replace_delete_backspace_and_protected_navigation_preserve_text()
    {
        var buffer = new DisplayBuffer(); var form = new DisplayForm(buffer, new[] {
            new InputField { Row = 2, Column = 2, Length = 4 }, new InputField { Row = 3, Column = 2, Length = 4, Protect = true }, new InputField { Row = 4, Column = 2, Length = 4 } });
        var editor = new FieldEditor(form); form.WriteValue(0, "ABCD"); editor.ApplyEdit(CursorEdit.Home);
        Assert.Equal(EditingResult.Full, editor.Apply('X')); Assert.Equal("ABCD", form.ReadValue(0));
        editor.ApplyEdit(CursorEdit.Insert); Assert.False(editor.InsertMode); editor.Apply('X'); Assert.Equal("XBCD", form.ReadValue(0));
        editor.ApplyEdit(CursorEdit.Delete); Assert.Equal("XCD", form.ReadValue(0)); Assert.Equal(1, form.Active.CursorOffset);
        editor.ApplyEdit(CursorEdit.FieldBackspace); Assert.Equal("CD", form.ReadValue(0)); Assert.Equal(0, form.Active.CursorOffset);
        editor.ApplyEdit(CursorEdit.End); editor.ApplyEdit(CursorEdit.Insert); editor.Apply('Z'); Assert.Equal("CDZ", form.ReadValue(0));
        editor.ApplyEdit(CursorEdit.CursorDown); Assert.Equal(2, form.ActiveIndex); editor.ApplyEdit(CursorEdit.PreviousField); Assert.Equal(0, form.ActiveIndex);
        editor.ApplyEdit(CursorEdit.Home); editor.ApplyEdit(CursorEdit.CursorRight); editor.ApplyEdit(CursorEdit.FieldExit);
        Assert.Equal("C", form.ReadValue(0)); Assert.Equal(2, form.ActiveIndex);
    }
}
