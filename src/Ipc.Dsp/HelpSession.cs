using Ipc.Core.Messages;
using Ipc.Terminal;

namespace Ipc.Dsp;

/// <summary>Keyboard help viewer shared by menus, command entry and compiled display-file help.</summary>
public sealed class HelpSession
{
    private sealed record Location(string Group, string Module, int Page, int Link);
    private sealed record HelpCell(char Value, DisplayAttribute Attributes, int Link = -1);
    private readonly Func<string, HelpDefinition> _load;
    private readonly Stack<Location> _history = new();
    private string _group;
    private string _module;
    private HelpDefinition _definition;
    private List<List<HelpCell>> _lines = new();
    private readonly List<HelpLink> _links = new();
    private int _page;
    private int _selectedLink = -1;
    private bool _index;
    private string _query = "";
    private int _indexSelection;
    private string? _error;
    private int PageHeight => Buffer.Rows - 6;
    public DisplayBuffer Buffer { get; }
    public string CurrentModule => _module;
    public string CurrentGroup => _group;
    public HelpSession(string group, string module, Func<string, HelpDefinition> load, int rows = 24, int columns = 80)
    {
        if (rows < 12 || columns < 40) throw new ArgumentException("Help requires at least 40x12 cells.");
        _load = load; _group = group; _module = module.ToUpperInvariant(); _definition = load(group); Buffer = new(rows, columns);
        Layout(); Paint();
    }
    /// <returns>True when the viewer should close; the caller's editor is never modified.</returns>
    public bool Handle(KeyPress key)
    {
        _definition = _load(_group); // Recheck authority/signature policy on every interaction.
        Layout(); _page = Math.Min(_page, Math.Max(0, (_lines.Count - 1) / PageHeight));
        _selectedLink = Math.Min(_selectedLink, _links.Count - 1);
        if (key.Aid is AidKey.Pf3 or AidKey.Pa1 or AidKey.Pa2 or AidKey.Pa3) return true;
        _error = null;
        try
        {
            if (key.Aid == AidKey.Pf12)
            {
                if (_index) _index = false;
                else if (_history.TryPeek(out var previous))
                {
                    var previousDefinition = _load(previous.Group);
                    if (!previousDefinition.Modules.Any(m => m.Name == previous.Module)) throw new InvalidOperationException("Previous help module no longer exists.");
                    _history.Pop(); _definition = previousDefinition; _group = previous.Group; _module = previous.Module; Layout(); _page = previous.Page; _selectedLink = previous.Link;
                }
                else return true;
            }
            else if (key.Aid == AidKey.Pf5) { _index = true; _query = ""; _indexSelection = 0; }
            else if (_index) HandleIndex(key);
            else if (key.Aid == AidKey.RollUp) _page = Math.Min(Math.Max(0, (_lines.Count - 1) / PageHeight), _page + 1);
            else if (key.Aid == AidKey.RollDown) _page = Math.Max(0, _page - 1);
            else if (key.Edit is CursorEdit.Tab or CursorEdit.NextField or CursorEdit.BackTab or CursorEdit.PreviousField && _links.Count > 0)
            {
                var delta = key.Edit is CursorEdit.BackTab or CursorEdit.PreviousField ? -1 : 1;
                _selectedLink = (_selectedLink + delta + _links.Count) % _links.Count;
                var line = _lines.FindIndex(l => l.Any(c => c.Link == _selectedLink)); _page = line / PageHeight;
            }
            else if (key.Aid == AidKey.Enter && _selectedLink >= 0)
            {
                var target = _links[_selectedLink]; Open(target.PanelGroup ?? _group, target.Module);
            }
        }
        catch (Exception error) when (error is CpfException or ArgumentException or InvalidOperationException)
        { _error = error.Message; }
        Paint(); return false;
    }
    private void Open(string group, string module)
    {
        if (_history.Count == 64) throw new InvalidOperationException("Help history limit reached; use F12 to go back.");
        var definition = _load(group);
        if (!definition.Modules.Any(m => m.Name == module)) throw new InvalidOperationException("Help module not found: " + module);
        _history.Push(new(_group, _module, _page, _selectedLink));
        _group = group; _module = module; _definition = definition; _index = false; _page = 0; _selectedLink = -1; Layout();
    }
    private HelpModule[] Matches() => _definition.Modules.Where(m => _query.Length == 0 || m.Title.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
        m.Name.Contains(_query, StringComparison.OrdinalIgnoreCase) || m.IndexWords.Any(w => w.StartsWith(_query, StringComparison.OrdinalIgnoreCase))).OrderBy(m => m.Title, StringComparer.Ordinal).ToArray();
    private void HandleIndex(KeyPress key)
    {
        if (key.Character != '\0' && TerminalGlyph.IsSingleCell(key.Character) && _query.Length < 64) { _query += key.Character; _indexSelection = 0; }
        else if (key.Edit is CursorEdit.FieldBackspace or CursorEdit.Delete && _query.Length > 0) { _query = _query[..^1]; _indexSelection = 0; }
        else if (key.Edit is CursorEdit.CursorDown or CursorEdit.NextField or CursorEdit.Tab) _indexSelection++;
        else if (key.Edit is CursorEdit.CursorUp or CursorEdit.PreviousField or CursorEdit.BackTab) _indexSelection--;
        else if (key.Aid == AidKey.RollUp) _indexSelection += PageHeight;
        else if (key.Aid == AidKey.RollDown) _indexSelection -= PageHeight;
        var matches = Matches(); _indexSelection = Math.Clamp(_indexSelection, 0, Math.Max(0, matches.Length - 1));
        if (key.Aid == AidKey.Enter && matches.Length > 0) Open(_group, matches[_indexSelection].Name);
    }
    private void Layout()
    {
        var module = _definition.Modules.SingleOrDefault(m => m.Name == _module) ?? throw new InvalidOperationException("Help module not found: " + _module);
        _lines = new(); _links.Clear(); var width = Buffer.Columns - 4;
        foreach (var block in module.Blocks)
        {
            var cells = new List<HelpCell>();
            foreach (var span in block.Spans)
            {
                var attributes = block.Kind.StartsWith("XH", StringComparison.Ordinal) ? DisplayAttribute.HighIntensity : DisplayAttribute.None;
                attributes |= span.Style switch { "HP1" => DisplayAttribute.Underline, "HP2" => DisplayAttribute.HighIntensity,
                    "HP3" => DisplayAttribute.Underline | DisplayAttribute.HighIntensity, _ => DisplayAttribute.None };
                var link = -1;
                if (span.Link is { } target)
                {
                    link = _links.FindIndex(l => l == target); if (link < 0) { link = _links.Count; _links.Add(target); }
                    attributes |= DisplayAttribute.Underline;
                }
                cells.AddRange(span.Text.Select(c => new HelpCell(c, attributes, link)));
            }
            var position = 0;
            while (position < cells.Count && cells[position].Value == ' ') position++;
            while (position < cells.Count)
            {
                var count = Math.Min(width, cells.Count - position);
                if (position + count < cells.Count)
                {
                    var space = cells.FindLastIndex(position + count - 1, count, c => c.Value == ' '); if (space > position) count = space - position;
                }
                _lines.Add(cells.GetRange(position, count)); position += count;
                while (position < cells.Count && cells[position].Value == ' ') position++;
            }
            _lines.Add(new());
        }
        if (_lines.Count == 0) _lines.Add(new());
    }
    private void Paint()
    {
        Buffer.ClearScreen(); Buffer.MoveCursor(1, 1);
        var module = _definition.Modules.Single(m => m.Name == _module);
        Write(1, 2, _index ? "Help index" : module.Title, DisplayAttribute.HighIntensity);
        Write(2, 2, _error ?? (_index ? "Search: " + _query : _group + " / " + _module), _error is null ? DisplayAttribute.None : DisplayAttribute.HighIntensity);
        if (_index)
        {
            var matches = Matches(); var first = _indexSelection / PageHeight * PageHeight;
            for (var i = first; i < Math.Min(matches.Length, first + PageHeight); i++)
                Write(4 + i - first, 2, matches[i].Title, i == _indexSelection ? DisplayAttribute.ReverseVideo : DisplayAttribute.None);
            if (matches.Length == 0) Write(4, 2, "No matching help topics.");
            Buffer.MoveCursor(2, Math.Min(Buffer.Columns - 1, 10 + _query.Length));
        }
        else
        {
            var cursorSet = false;
            for (var i = _page * PageHeight; i < Math.Min(_lines.Count, (_page + 1) * PageHeight); i++)
            {
                var row = 4 + i - _page * PageHeight;
                for (var column = 0; column < _lines[i].Count; column++)
                {
                    var cell = _lines[i][column]; var selected = cell.Link >= 0 && cell.Link == _selectedLink;
                    Buffer.Set(row, column + 2, cell.Value, cell.Attributes | (selected ? DisplayAttribute.ReverseVideo : DisplayAttribute.None));
                    if (selected && !cursorSet) { Buffer.MoveCursor(row, column + 2); cursorSet = true; }
                }
            }
            Write(Buffer.Rows - 2, 2, $"Page {_page + 1} of {Math.Max(1, (_lines.Count + PageHeight - 1) / PageHeight)}");
        }
        Write(Buffer.Rows, 2, "F3 Exit  F12 Back  F5 Index  Tab Link  Enter Open  PgUp/PgDn");
    }
    private void Write(int row, int column, string text, DisplayAttribute attributes = DisplayAttribute.None)
    {
        for (var i = 0; i < Math.Min(text.Length, Buffer.Columns - column); i++) Buffer.Set(row, column + i, text[i], attributes);
    }
}
