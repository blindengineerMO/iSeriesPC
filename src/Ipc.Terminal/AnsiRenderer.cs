using System.Text;

namespace Ipc.Terminal;

public sealed class AnsiRenderer
{
    public const string ClearSequence = "\u001b[2J\u001b[H";

    public string Render(DisplayBuffer buffer)
    {
        var sb = new StringBuilder();
        sb.Append(ClearSequence);
        var previous = default(Cell?);
        var previousFg = 0;

        for (var row = 1; row <= buffer.Rows; row++)
        {
            for (var col = 1; col <= buffer.Columns; col++)
            {
                var cell = buffer[row, col];
                var changeDirection = cell.Attributes != previous?.Attributes || cell.Foreground != previousFg;

                if (changeDirection)
                {
                    sb.Append(Sgr(cell.Attributes, cell.Foreground));
                }

                sb.Append(TerminalGlyph.Render(cell));

                previous = cell;
                previousFg = cell.Foreground;
            }

            if (row < buffer.Rows)
            {
                sb.Append($"\u001b[{row + 1};1H");
            }
        }

        sb.Append(Cursor(buffer));
        return sb.ToString();
    }

    public string RenderDiff(DisplayBuffer buffer, DisplayBuffer prior, out bool changed)
    {
        changed = false;

        if (prior.Rows != buffer.Rows || prior.Columns != buffer.Columns)
        {
            changed = true; return Render(buffer);
        }

        var sb = new StringBuilder();

        for (var row = 1; row <= buffer.Rows; row++)
        {
            for (var col = 1; col <= buffer.Columns; col++)
            {
                var priorCell = prior[row, col];
                var cell = buffer[row, col];
                if (priorCell.Value != cell.Value || priorCell.Attributes != cell.Attributes || priorCell.Foreground != cell.Foreground)
                {
                    changed = true;
                    sb.Append($"\u001b[{row};{col}H");
                    sb.Append(Sgr(cell.Attributes, cell.Foreground));
                    sb.Append(TerminalGlyph.Render(cell));
                }
            }
        }

        if (changed || buffer.Cursor != prior.Cursor) { changed = true; sb.Append(Cursor(buffer)); }
        return sb.ToString();
    }

    private static string Cursor(DisplayBuffer buffer) => $"\u001b[{Math.Clamp(buffer.Cursor.Row, 1, buffer.Rows)};{Math.Clamp(buffer.Cursor.Column, 1, buffer.Columns)}H";
    public static string RenderCell(Cell cell) => Sgr(cell.Attributes, cell.Foreground) + TerminalGlyph.Render(cell);

    private static string Sgr(DisplayAttribute attributes, int foreground)
    {
        var codes = new List<int>();
        if (attributes.HasFlag(DisplayAttribute.HighIntensity))
        {
            codes.Add(1);
        }
        else
        {
            codes.Add(22);
        }

        if (attributes.HasFlag(DisplayAttribute.Underline))
        {
            codes.Add(4);
        }
        else
        {
            codes.Add(24);
        }

        if (attributes.HasFlag(DisplayAttribute.Blink))
        {
            codes.Add(5);
        }
        else
        {
            codes.Add(25);
        }

        if (attributes.HasFlag(DisplayAttribute.ReverseVideo))
        {
            codes.Add(7);
        }
        else
        {
            codes.Add(27);
        }

        codes.Add(foreground == 0 ? Ansicolor.Green : foreground);
        return $"\u001b[{string.Join(';', codes)}m";
    }
}