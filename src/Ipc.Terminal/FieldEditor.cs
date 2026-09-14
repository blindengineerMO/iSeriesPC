using System.Text;

namespace Ipc.Terminal;

public sealed class FieldEditor
{
    private readonly DisplayForm _form;

    public FieldEditor(DisplayForm form)
    {
        _form = form;
        _form.Activate(0);
    }

    public EditingResult Apply(char ch)
    {
        if (char.IsControl(ch))
        {
            return EditingResult.Consumed;
        }

        var field = _form.Active.Field;
        if (field.Protect)
        {
            return EditingResult.Consumed;
        }

        if (field.Usage is FieldUsage.Numeric or FieldUsage.SignedNumeric && !char.IsDigit(ch) && ch is not ('-' or '+' or '.'))
        {
            return EditingResult.Rejected;
        }

        if (_form.Active.CursorOffset >= field.Length)
        {
            return EditingResult.Full;
        }

        var offset = _form.Active.CursorOffset;
        var padded = _form.Active.Text.PadRight(field.Length, field.Fill);
        var inserted = InsertChar(padded, ch, offset);
        if (inserted.Length > field.Length)
        {
            inserted = inserted[..field.Length];
        }

        _form.WriteValue(_form.ActiveIndex, inserted);
        _form.Active.CursorOffset = Math.Min(offset + 1, field.Length);
        _bufferCursor();

        return _form.Active.CursorOffset >= field.Length && field.AutoEnter ? EditingResult.AcceptedAndMoved : EditingResult.Accepted;
    }

    public void ApplyEdit(CursorEdit edit)
    {
        var value = _form.Active;
        var field = value.Field;

        switch (edit)
        {
            case CursorEdit.FieldBackspace:
            case CursorEdit.Delete when edit == CursorEdit.Delete && value.CursorOffset > 0:
                DeleteAt(value);
                break;
            case CursorEdit.CursorLeft:
                if (value.CursorOffset > 0)
                {
                    value.CursorOffset--;
                    _bufferCursor();
                }
                break;
            case CursorEdit.CursorRight:
                if (value.CursorOffset < field.Length)
                {
                    value.CursorOffset++;
                    _bufferCursor();
                }
                break;
            case CursorEdit.NextField:
            case CursorEdit.Tab:
                Advance(1);
                break;
            case CursorEdit.PreviousField:
            case CursorEdit.BackTab:
                if (_form.ActiveIndex > 0)
                {
                    _form.Activate(_form.ActiveIndex - 1);
                    _bufferCursor();
                }
                break;
            case CursorEdit.FieldExit:
                if (value.CursorOffset > 0 && value.CursorOffset < field.Length)
                {
                    value.CursorOffset = field.Length;
                    _bufferCursor();
                }
                else
                {
                    Advance(1);
                }
                break;
            default:
                break;
        }
    }

    public KeyPress? TranslateSequence(string sequence)
    {
        if (string.IsNullOrEmpty(sequence))
        {
            return null;
        }

        if (sequence.Length == 1 && !char.IsControl(sequence[0]))
        {
            Apply(sequence[0]);
            return new KeyPress(AidKey.None, CursorEdit.None, sequence[0]);
        }

        return null;
    }

    private void DeleteAt(FieldValue value)
    {
        if (value.CursorOffset == 0)
        {
            return;
        }

        var text = value.Text;
        value.Text = text[..(value.CursorOffset - 1)] + text[value.CursorOffset..];
        value.CursorOffset = Math.Max(0, value.CursorOffset - 1);
        _form.WriteValue(_form.ActiveIndex, value.Text);
        _bufferCursor();
    }

    private void Advance(int delta)
    {
        if (_form.ActiveIndex + delta < _form.Fields.Count)
        {
            _form.Activate(_form.ActiveIndex + delta);
            _bufferCursor();
        }
        else
        {
            _form.Activate(0);
            _bufferCursor();
        }
    }

    private void _bufferCursor()
    {
        var field = _form.Active.Field;
        _form.Activate(_form.ActiveIndex);
        _form.CursorPosition(field.Row, field.Column + Math.Min(_form.Active.CursorOffset, field.Length));
    }

    private static string InsertChar(string text, char ch, int offset)
    {
        if (offset >= text.Length)
        {
            return text + ch;
        }

        var sb = new StringBuilder(text);
        sb.Insert(offset, ch);
        return sb.ToString();
    }
}

public enum EditingResult
{
    Accepted,
    AcceptedAndMoved,
    Consumed,
    Rejected,
    Full,
}