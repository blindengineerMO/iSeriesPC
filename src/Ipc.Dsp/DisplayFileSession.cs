using Ipc.Terminal;

namespace Ipc.Dsp;

/// <summary>One open display file, retaining subfiles and suspended editors while modal windows are active.</summary>
public sealed class DisplayFileSession
{
    private sealed record Layer(string Record, PanelSession? Panel, SubfilePanel? Subfile, int Row, int Column, bool Window)
    {
        public DisplayBuffer Buffer => Panel?.Buffer ?? Subfile!.Buffer;
        public PanelResponse Handle(KeyPress key) => Panel?.Handle(key) ?? Subfile!.Handle(key);
    }
    private readonly PanelDefinition _definition;
    private readonly Func<string, string, string?>? _messages;
    private readonly Func<string, string, DisplayMessage?>? _programMessages;
    private readonly List<Layer> _layers = new();
    private readonly Dictionary<string, SubfileState> _subfiles = new(StringComparer.Ordinal);
    public DisplayBuffer Buffer { get; }
    public int WindowDepth => _layers.Count(l => l.Window);
    public DisplayFileSession(PanelDefinition definition, Func<string, string, string?>? messages = null, Func<string, string, DisplayMessage?>? programMessages = null)
    {
        _definition = definition; _messages = messages; _programMessages = programMessages; Buffer = new(definition.Rows, definition.Columns);
        foreach (var control in definition.Records.Where(r => SubfileLayout.Keyword(r, "SFLCTL") is not null))
        {
            var data = definition.Records.Single(r => r.Name == SubfileLayout.Keyword(control, "SFLCTL")!.Arguments[0]);
            if (_subfiles.ContainsKey(data.Name)) throw new ArgumentException("Each subfile requires one control record.");
            _subfiles[data.Name] = new(data, SubfileLayout.Number(control, "SFLSIZ"));
        }
    }
    public SubfileState Subfile(string record) => _subfiles[record];
    public void Write(string record, IReadOnlyDictionary<string, object?> values, IReadOnlyList<bool>? indicators = null)
    {
        var format = _definition.Records.Single(r => r.Name == record);
        if (SubfileLayout.Keyword(format, "SFL") is not null) throw new InvalidOperationException("Write subfile records through Subfile(record).Write(relativeRecordNumber, values).");
        var window = SubfileLayout.Keyword(format, "WINDOW");
        var existing = _layers.FindIndex(l => l.Record == record);
        if (existing < 0 && window is not null && WindowDepth >= 12) throw new InvalidOperationException("At most 12 modal windows may be displayed.");
        var row = window is null ? 0 : int.Parse(window.Arguments[0]); var column = window is null ? 0 : int.Parse(window.Arguments[1]);
        var rows = window is null ? _definition.Rows : int.Parse(window.Arguments[2]);
        var columns = window is null ? _definition.Columns : int.Parse(window.Arguments[3]);
        var local = new PanelDefinition { Name = _definition.Name, Rows = rows, Columns = columns, Keywords = _definition.Keywords,
            Records = _definition.Records.Select(r => new PanelRecord { Name = r.Name, Line = r.Line, Fields = r.Fields, HelpAreas = r.HelpAreas, Keywords = r.Keywords.Where(k => k.Name != "WINDOW").ToList() }).ToList() };
        PanelSession? panel = existing < 0 ? null : _layers[existing].Panel;
        SubfilePanel? subfile = existing < 0 ? null : _layers[existing].Subfile;
        if (SubfileLayout.Keyword(format, "SFLCTL") is { } control)
        {
            subfile ??= new(local, record, _subfiles[control.Arguments[0]], _messages, _programMessages); subfile.Write(values, indicators);
        }
        else
        {
            if (panel is null)
            {
                panel = new(local, _messages);
                if (window is null && format.Keywords.Any(k => k.Name == "OVERLAY" && k.Enabled(indicators ?? new bool[100])) && _layers.FirstOrDefault(l => !l.Window) is { } underlying)
                    foreach (var position in underlying.Buffer.Positions())
                    {
                        var cell = underlying.Buffer[position.Row, position.Column]; panel.Buffer.Set(position.Row, position.Column, cell.Value, cell.Attributes, cell.Foreground);
                    }
            }
            panel.Write(record, values, indicators);
        }
        // Build successfully before replacing or suspending any existing editor.
        if (window is null) _layers.Clear();
        else if (existing >= 0) _layers.RemoveRange(existing, _layers.Count - existing);
        _layers.Add(new(record, panel, subfile, row, column, window is not null)); Render();
    }
    public PanelResponse Handle(KeyPress key)
    {
        if (_layers.Count == 0) throw new InvalidOperationException("Write a record before reading input.");
        var top = _layers[^1]; var response = top.Handle(key); Render();
        return response with { Cursor = Buffer.Cursor };
    }
    public void CloseWindow()
    {
        if (_layers.Count == 0 || !_layers[^1].Window) throw new InvalidOperationException("No active window.");
        _layers.RemoveAt(_layers.Count - 1); Render();
    }
    private void Render()
    {
        Buffer.ClearScreen(); Buffer.MoveCursor(1, 1);
        foreach (var layer in _layers)
        {
            if (layer.Window)
            {
                var bottom = layer.Row + layer.Buffer.Rows + 1; var right = layer.Column + layer.Buffer.Columns + 3;
                Buffer.ClearRegion(layer.Row, layer.Column, layer.Buffer.Rows + 2, layer.Buffer.Columns + 4);
                for (var c = layer.Column; c <= right; c++) { Buffer.Set(layer.Row, c, '-'); Buffer.Set(bottom, c, '-'); }
                for (var r = layer.Row; r <= bottom; r++) { Buffer.Set(r, layer.Column, '|'); Buffer.Set(r, right, '|'); }
                foreach (var r in new[] { layer.Row, bottom }) foreach (var c in new[] { layer.Column, right }) Buffer.Set(r, c, '+');
            }
            foreach (var position in layer.Buffer.Positions())
            {
                var cell = layer.Buffer[position.Row, position.Column];
                Buffer.Set(position.Row + layer.Row, position.Column + (layer.Window ? layer.Column + 1 : 0), cell.Value, cell.Attributes, cell.Foreground);
            }
            var cursor = layer.Buffer.Cursor;
            Buffer.MoveCursor(cursor.Row + layer.Row, cursor.Column + (layer.Window ? layer.Column + 1 : 0));
        }
    }
}
