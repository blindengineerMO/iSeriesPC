namespace Ipc.Terminal;

public interface IDisplayWriter
{
    void Render(DisplayBuffer buffer);

    void RenderDiff(DisplayBuffer buffer, DisplayBuffer prior);

    void MoveCursor(int row, int column);

    void ClearScreen();

    void WriteStatusLine(string text);
}

public sealed class NullDisplayWriter : IDisplayWriter
{
    public void Render(DisplayBuffer buffer)
    {
    }

    public void RenderDiff(DisplayBuffer buffer, DisplayBuffer prior)
    {
    }

    public void MoveCursor(int row, int column)
    {
    }

    public void ClearScreen()
    {
    }

    public void WriteStatusLine(string text)
    {
    }
}

public sealed class ScrollingDisplay
{
    private readonly DisplayBuffer _buffer;
    private readonly int _scrollHeight;

    public ScrollingDisplay(int rows = 24, int columns = 80, int scrollHeight = 7)
    {
        _buffer = new DisplayBuffer(rows, columns);
        _scrollHeight = scrollHeight;
    }

    public DisplayBuffer Buffer => _buffer;

    public void AddLine(string text, DisplayAttribute attributes = DisplayAttribute.None, int? foreground = null)
    {
        _buffer.ScrollUp(1);
        for (var i = 0; i < text.Length && i < _buffer.Columns; i++)
        {
            _buffer.Set(_buffer.Rows, i + 1, text[i], attributes, foreground);
        }
    }

    public void Clear() => _buffer.ClearScreen();
}