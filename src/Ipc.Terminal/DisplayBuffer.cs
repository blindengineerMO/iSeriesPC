namespace Ipc.Terminal;

public sealed class DisplayBuffer
{
    private const char Space = ' ';

    private readonly Cell[] _cells;

    public DisplayBuffer(int rows = 24, int columns = 80)
    {
        Rows = rows;
        Columns = columns;
        Cursor = new CellPosition(1, 1);
        _cells = new Cell[rows * columns];
        for (var i = 0; i < _cells.Length; i++)
        {
            _cells[i] = new Cell(Space, DisplayAttribute.None);
        }
    }

    public int Rows { get; }

    public int Columns { get; }

    public CellPosition Cursor { get; private set; }

    public Cell this[int row, int column] => _cells[CheckedIndex(row, column)];

    public void MoveCursor(int row, int column)
    {
        if (row != 0 && (row < 1 || row > Rows))
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        if (column != 0 && (column < 1 || column > Columns))
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        Cursor = new CellPosition(row, column);
    }

    public void Write(string text, DisplayAttribute attributes = DisplayAttribute.None, int? foreground = null)
    {
        foreach (var ch in text)
        {
            if (Cursor.Column > Columns)
            {
                if (Cursor.Row == Rows)
                {
                    ScrollUp();
                    Cursor = new CellPosition(Rows, 1);
                }
                else
                {
                    MoveCursor(Cursor.Row + 1, 1);
                }
            }

            Set(Cursor.Row, Cursor.Column, ch, attributes, foreground);
            Cursor = new CellPosition(Cursor.Row, Cursor.Column + 1);
        }
    }

    public void WriteLine() => WriteLine(string.Empty);

    public void WriteLine(string text, DisplayAttribute attributes = DisplayAttribute.None, int? foreground = null)
    {
        Write(text, attributes, foreground);
        if (Cursor.Row == Rows)
        {
            Cursor = new CellPosition(Rows, 1);
        }
        else
        {
            MoveCursor(Cursor.Row + 1, 1);
        }
    }

    public void Set(int row, int column, char value, DisplayAttribute attributes = DisplayAttribute.None, int? foreground = null)
    {
        _cells[CheckedIndex(row, column)] = new Cell(value, attributes, foreground ?? Ansicolor.Green);
    }

    public void SetColumn(int column, string text, int startRow = 1, DisplayAttribute attributes = DisplayAttribute.None, int? foreground = null)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (startRow + i <= Rows)
            {
                Set(startRow + i, column, text[i], attributes, foreground);
            }
        }
    }

    public void ClearRegion(int row, int column, int height, int width,
        DisplayAttribute attributes = DisplayAttribute.None, int? foreground = null)
    {
        for (var r = row; r < row + height; r++)
        {
            for (var c = column; c < column + width; c++)
            {
                if (r <= Rows && c <= Columns)
                {
                    Set(r, c, Space, attributes, foreground);
                }
            }
        }
    }

    public void ClearScreen(DisplayAttribute attributes = DisplayAttribute.None, int? foreground = null)
    {
        for (var i = 0; i < _cells.Length; i++)
        {
            _cells[i] = new Cell(Space, attributes, foreground ?? Ansicolor.Green);
        }
    }

    public void ScrollUp(int lines = 1)
    {
        Array.Copy(_cells, lines * Columns, _cells, 0, _cells.Length - lines * Columns);
        for (var c = 0; c < Columns; c++)
        {
            _cells[_cells.Length - Columns + c] = new Cell(Space, DisplayAttribute.None);
        }
    }

    public void ScrollDown(int lines = 1)
    {
        Array.Copy(_cells, 0, _cells, lines * Columns, _cells.Length - lines * Columns);
        for (var c = 0; c < Columns; c++)
        {
            _cells[c] = new Cell(Space, DisplayAttribute.None);
        }
    }

    public void InsertLine(int aboveRow, DisplayAttribute attributes = DisplayAttribute.None, int? foreground = null)
    {
        var start = CheckedIndex(aboveRow, 1);
        Array.Copy(_cells, start, _cells, start + Columns, _cells.Length - start - Columns);
        for (var c = 0; c < Columns; c++)
        {
            _cells[start + c] = new Cell(Space, attributes, foreground ?? Ansicolor.Green);
        }
    }

    public void DeleteLine(int row, DisplayAttribute attributes = DisplayAttribute.None, int? foreground = null)
    {
        var start = CheckedIndex(row, 1);
        Array.Copy(_cells, start + Columns, _cells, start, _cells.Length - start - Columns);
        for (var c = 0; c < Columns; c++)
        {
            _cells[_cells.Length - Columns + c] = new Cell(Space, attributes, foreground ?? Ansicolor.Green);
        }
    }

    public string RowText(int row)
    {
        var start = CheckedIndex(row, 1);
        var chars = new char[Columns];
        for (var c = 0; c < Columns; c++)
        {
            chars[c] = _cells[start + c].Value;
        }

        return new string(chars);
    }

    public IEnumerable<CellPosition> Positions() =>
        from r in Enumerable.Range(1, Rows)
        from c in Enumerable.Range(1, Columns)
        select new CellPosition(r, c);

    public DisplayBuffer Clone()
    {
        var copy = new DisplayBuffer(Rows, Columns) { Cursor = Cursor };
        Array.Copy(_cells, copy._cells, _cells.Length);
        return copy;
    }

    private int CheckedIndex(int row, int column)
    {
        if (row < 1 || row > Rows)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        if (column < 1 || column > Columns)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        return (row - 1) * Columns + (column - 1);
    }
}