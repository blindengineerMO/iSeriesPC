namespace Ipc.Terminal;

public enum FieldUsage
{
    Alpha,
    Numeric,
    AlphaNumeric,
    AlphaOnlyShiftLock,
    NumericOnlyShiftLock,
    SignedNumeric,
}

public sealed class InputField
{
    public required int Row { get; init; }

    public required int Column { get; init; }

    public required int Length { get; init; }
    public int? DisplayLength { get; init; }
    public int VisibleLength => Math.Min(Length, DisplayLength ?? Length);
    public bool TrimTrailingFill { get; init; } = true;

    public FieldUsage Usage { get; init; } = FieldUsage.AlphaNumeric;

    public bool Mandatory { get; init; }

    public bool AutoEnter { get; init; }

    public bool Protect { get; init; }

    public bool Hidden { get; init; }

    public int TabOrder { get; init; }

    public char Fill { get; init; } = ' ';

    public override string ToString() => $"({Row},{Column},{Length})";
}

public sealed class FieldValue
{
    private readonly char[] _buffer;
    private int _length;

    public FieldValue(InputField field)
    {
        Field = field;
        _buffer = new char[field.Length];
        Array.Fill(_buffer, field.Fill);
    }

    public InputField Field { get; }

    public int CursorOffset { get; set; }

    public string Text
    {
        get => Field.TrimTrailingFill ? new string(_buffer).TrimEnd(Field.Fill) : new string(_buffer, 0, _length);
        set
        {
            var text = value ?? string.Empty;
            if (text.Length > _buffer.Length)
            {
                text = text[.._buffer.Length];
            }

            Array.Fill(_buffer, Field.Fill);
            for (var i = 0; i < text.Length; i++)
            {
                _buffer[i] = text[i];
            }

            CursorOffset = Math.Min(text.Length, _buffer.Length);
            _length = text.Length;
        }
    }

    public bool IsBlank => string.IsNullOrWhiteSpace(Text);

    public string ValidatedText
    {
        get
        {
            if (!IsValidAfterEditing)
            {
                return Text;
            }

            return Text;
        }
    }

    public bool IsValidAfterEditing =>
        Field.Usage switch
        {
            FieldUsage.Numeric or FieldUsage.SignedNumeric => Text.Length == 0 || Text.All(c => char.IsDigit(c) || c == '-' || c == '+' || c == '.'),
            FieldUsage.Alpha => Text.Length == 0 || Text.All(c => char.IsLetter(c) || c == ' '),
            _ => true,
        };

    public string Blanks => new string(Field.Fill, _buffer.Length);

    public void Clear() { Array.Fill(_buffer, Field.Fill); _length = 0; CursorOffset = 0; }
    public int ViewOffset => Math.Max(0, Math.Min(CursorOffset, Field.Length - 1) - Field.VisibleLength + 1);

    public override string ToString() => Text;
}

public sealed class DisplayForm
{
    private readonly List<InputField> _fields;
    private readonly Dictionary<int, FieldValue> _values = new();
    private readonly DisplayBuffer _buffer;

    public DisplayForm(DisplayBuffer buffer, IEnumerable<InputField> fields)
    {
        _buffer = buffer;
        _fields = fields.OrderBy(f => f.TabOrder).ThenBy(f => f.Row).ThenBy(f => f.Column).ToList();

        for (var i = 0; i < _fields.Count; i++)
        {
            var field = _fields[i];
            _values[i] = new FieldValue(field);
            Paint(field, "");
        }
    }

    public IReadOnlyList<InputField> Fields => _fields;

    public int ActiveIndex { get; private set; }

    public FieldValue Active => _values[ActiveIndex];

    public void Activate(int index)
    {
        ActiveIndex = index;
        _buffer.MoveCursor(_fields[index].Row, _fields[index].Column);
    }

    public void WriteValue(int index, string text)
    {
        var field = _fields[index];
        _values[index].Text = text;
        Paint(field, text);
    }

    public string ReadValue(int index) => _values[index].Text;

    public void ClearAll()
    {
        for (var i = 0; i < _fields.Count; i++)
        {
            _values[i].Clear();
        }

        RepaintAll();
    }

    public void CursorPosition(int row, int column) => _buffer.MoveCursor(row, Math.Min(column, _buffer.Columns));

    public void RepaintAll()
    {
        for (var i = 0; i < _fields.Count; i++)
        {
            Paint(_fields[i], _values[i].Text);
        }
    }

    public void RepaintActive() => Paint(Active.Field, Active.Text, Active.ViewOffset);

    public IEnumerable<FieldValue> Values => _values.OrderBy(kv => kv.Key).Select(kv => kv.Value);

    private void Paint(InputField field, string text, int viewOffset = 0)
    {
        var attributes = DisplayAttribute.InputField |
                         (field.Hidden ? DisplayAttribute.NonDisplay : DisplayAttribute.None);
        var padded = text.PadRight(field.Length, field.Fill).Substring(viewOffset, field.VisibleLength);

        _buffer.MoveCursor(field.Row, field.Column);
        for (var i = 0; i < field.VisibleLength; i++)
        {
            var value = field.Hidden ? (i + viewOffset < text.Length ? '*' : field.Fill) : padded[i];
            _buffer.Set(field.Row, field.Column + i, value, attributes);
        }
    }
}
