namespace Ipc.Terminal;

public sealed class FieldEditor
{
    private readonly DisplayForm _form;
    public bool InsertMode { get; private set; } = true;
    public FieldEditor(DisplayForm form)
    {
        _form = form;
        if (_form.Fields.Count == 0) throw new ArgumentException("An editor requires at least one field.");
        _form.Activate(Math.Max(0, _form.Fields.ToList().FindIndex(f => !f.Protect)));
    }
    public EditingResult Apply(char ch)
    {
        if (!TerminalGlyph.IsSingleCell(ch)) return EditingResult.Rejected;
        var field = _form.Active.Field;
        if (field.Protect) return EditingResult.Consumed;
        if (field.Usage is FieldUsage.AlphaOnlyShiftLock or FieldUsage.NumericOnlyShiftLock) ch = char.ToUpperInvariant(ch);
        if (field.Usage is FieldUsage.Numeric or FieldUsage.NumericOnlyShiftLock && !char.IsAsciiDigit(ch) && ch != '.' ||
            field.Usage == FieldUsage.SignedNumeric && !char.IsAsciiDigit(ch) && ch is not ('-' or '+' or '.') ||
            field.Usage is FieldUsage.Alpha or FieldUsage.AlphaOnlyShiftLock && !char.IsLetter(ch) && ch != ' ')
            return EditingResult.Rejected;
        var offset = _form.Active.CursorOffset;
        if (offset >= field.Length) return EditingResult.Full;
        var text = _form.Active.Text.PadRight(Math.Max(_form.Active.Text.Length, offset), field.Fill);
        if (InsertMode)
        {
            if (text.Length == field.Length && text[^1] != field.Fill) return EditingResult.Full;
            text = text.Insert(offset, ch.ToString());
            if (text.Length > field.Length) text = text[..field.Length];
        }
        else text = text.PadRight(Math.Max(text.Length, offset + 1), field.Fill)[..offset] + ch + (offset + 1 < text.Length ? text[(offset + 1)..] : "");
        _form.WriteValue(_form.ActiveIndex, text); _form.Active.CursorOffset = offset + 1; PaintCursor();
        return _form.Active.CursorOffset >= field.Length && field.AutoEnter ? EditingResult.AcceptedAndMoved : EditingResult.Accepted;
    }
    public void ApplyEdit(CursorEdit edit)
    {
        var value = _form.Active; var field = value.Field;
        switch (edit)
        {
            case CursorEdit.Insert: InsertMode = !InsertMode; break;
            case CursorEdit.FieldBackspace:
                if (!field.Protect && value.CursorOffset > 0) Remove(value.CursorOffset - 1, move: true);
                break;
            case CursorEdit.Delete:
                if (!field.Protect) Remove(value.CursorOffset, move: false);
                break;
            case CursorEdit.FieldEraseToEnd:
                if (!field.Protect) EraseToEnd();
                break;
            case CursorEdit.Home: value.CursorOffset = 0; PaintCursor(); break;
            case CursorEdit.End: value.CursorOffset = value.Text.Length; PaintCursor(); break;
            case CursorEdit.CursorLeft: value.CursorOffset = Math.Max(0, value.CursorOffset - 1); PaintCursor(); break;
            case CursorEdit.CursorRight: value.CursorOffset = Math.Min(field.Length, value.CursorOffset + 1); PaintCursor(); break;
            case CursorEdit.CursorUp: Vertical(-1); break;
            case CursorEdit.CursorDown: Vertical(1); break;
            case CursorEdit.NextField: case CursorEdit.Tab: Advance(1); break;
            case CursorEdit.PreviousField: case CursorEdit.BackTab: Advance(-1); break;
            case CursorEdit.FieldExit:
                if (!field.Protect) EraseToEnd();
                Advance(1); break;
        }
    }
    public KeyPress? TranslateSequence(string sequence)
    {
        var key = KeyTranslator.Translate(sequence, sequence.Length == 1 ? sequence[0] : null);
        if (key is { Character: not '\0' } character) Apply(character.Character);
        return key;
    }
    private void Remove(int offset, bool move)
    {
        var value = _form.Active; var cursor = value.CursorOffset;
        var text = value.Text.PadRight(Math.Max(value.Text.Length, offset + 1), value.Field.Fill);
        _form.WriteValue(_form.ActiveIndex, text.Remove(offset, 1)); value.CursorOffset = move ? offset : cursor; PaintCursor();
    }
    private void EraseToEnd()
    {
        var value = _form.Active; var cursor = value.CursorOffset;
        _form.WriteValue(_form.ActiveIndex, value.Text[..Math.Min(cursor, value.Text.Length)]);
        value.CursorOffset = cursor; PaintCursor();
    }
    private void Advance(int delta)
    {
        var count = _form.Fields.Count;
        for (var step = 1; step <= count; step++)
        {
            var index = _form.ActiveIndex + step * delta;
            if (delta < 0 && index < 0) return;
            index %= count;
            if (_form.Fields[index].Protect) continue;
            _form.Activate(index); _form.Active.CursorOffset = 0; PaintCursor(); return;
        }
    }
    private void Vertical(int direction)
    {
        var current = _form.Active; var column = current.Field.Column + current.CursorOffset;
        var candidates = _form.Fields.Select((f, i) => (Field: f, Index: i)).Where(p => !p.Field.Protect && (p.Field.Row - current.Field.Row) * direction > 0)
            .OrderBy(p => Math.Abs(p.Field.Row - current.Field.Row)).ThenBy(p => Math.Abs(p.Field.Column - column)).ToArray();
        if (candidates.Length == 0) return;
        var target = candidates[0]; _form.Activate(target.Index); _form.Active.CursorOffset = Math.Clamp(column - target.Field.Column, 0, target.Field.Length - 1); PaintCursor();
    }
    private void PaintCursor()
    {
        var value = _form.Active; _form.RepaintActive();
        _form.CursorPosition(value.Field.Row, value.Field.Column + Math.Clamp(value.CursorOffset - value.ViewOffset, 0, value.Field.VisibleLength - 1));
    }
}

public enum EditingResult { Accepted, AcceptedAndMoved, Consumed, Rejected, Full }
