using System.Globalization;

namespace Ipc.Terminal;

/// <summary>The fixed-cell terminal contract accepts single-width BMP characters; other glyphs cannot change grid geometry.</summary>
public static class TerminalGlyph
{
    public static bool IsSingleCell(char c)
    {
        var category = char.GetUnicodeCategory(c);
        if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned)
            return false;
        return c is not (>= '\u1100' and <= '\u115f' or '\u2329' or '\u232a' or >= '\u2e80' and <= '\ua4cf' or >= '\uac00' and <= '\ud7a3' or
            >= '\uf900' and <= '\ufaff' or >= '\ufe10' and <= '\ufe19' or >= '\ufe30' and <= '\ufe6f' or >= '\uff00' and <= '\uff60' or
            >= '\uffe0' and <= '\uffe6' or >= '\u2600' and <= '\u27bf');
    }
    public static char Render(Cell cell) => cell.Attributes.HasFlag(DisplayAttribute.NonDisplay) ? ' ' :
        cell.Attributes.HasFlag(DisplayAttribute.ColumnSeparator) ? '|' : IsSingleCell(cell.Value) ? cell.Value : '?';
}
