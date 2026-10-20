using Ipc.Dsp;
using Ipc.Terminal;

namespace Ipc.Session.Sql;

internal sealed class QueryVariableScreen
{
    private readonly DisplayForm _form;
    private readonly FieldEditor _editor;
    internal DisplayBuffer Buffer { get; } = new(24, 80);
    internal QueryVariableScreen(string name)
    {
        Buffer.WriteLine("                         Query Variable Value", DisplayAttribute.HighIntensity);
        Buffer.WriteLine(); Buffer.WriteLine("  Type the SQL text for the variable, press Enter.");
        Buffer.WriteLine(); Buffer.WriteLine("  Variable . . . : " + name);
        Buffer.WriteLine(); Buffer.WriteLine("  Value . . . . :");
        Buffer.MoveCursor(21, 2); Buffer.WriteLine("*BLANK=Single blank   F3=Exit   F12=Cancel");
        _form = new(Buffer, [new InputField { Row = 8, Column = 3, Length = 55 }]);
        _editor = new(_form); _form.Activate(0);
    }
    internal PanelResponse Handle(KeyPress key)
    {
        var done = key.Aid is AidKey.Enter or AidKey.Pf3 or AidKey.Pf12;
        if (!done)
        {
            if (key.Edit is { } edit && edit != CursorEdit.None) _editor.ApplyEdit(edit);
            else if (key.Character != '\0' && !char.IsControl(key.Character)) _editor.Apply(key.Character);
        }
        return new(done, key.Aid, new Dictionary<string, object?> { ["VALUE"] = _form.ReadValue(0) }, new bool[100], Buffer.Cursor);
    }
}
