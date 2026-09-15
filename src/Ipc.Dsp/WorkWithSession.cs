using Ipc.Core.Menu;
using Ipc.Terminal;

namespace Ipc.Dsp;

public sealed record WorkWithResponse(bool Close = false, bool Refresh = false, bool Help = false, WorkAction? Action = null);

/// <summary>Shared bounded work-with/listing screen. Only the host executes row actions.</summary>
public sealed class WorkWithSession
{
    private const int PageSize = 15;
    private WorkList _list;
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private DisplayForm _form = null!;
    private FieldEditor _editor = null!;
    private WorkAction? _confirm;
    private int _page;
    private string _command = "";
    private string _status = "";
    public DisplayBuffer Buffer { get; } = new();
    public WorkList Definition => _list;
    public WorkWithSession(WorkList list) { _list = Validate(list); Paint(); }
    private static WorkList Validate(WorkList list)
    {
        if (list.Rows.Count > 10000 || list.Rows.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != list.Rows.Count ||
            list.Rows.Any(r => r.Actions.Select(a => a.Option).Distinct().Count() != r.Actions.Count || r.Actions.Any(a => a.Option.Length is < 1 or > 2)))
            throw new ArgumentException("Work list must have at most 10000 unique rows and distinct one/two-character actions.");
        return list;
    }
    public void Replace(WorkList list)
    {
        Capture(); _list = Validate(list);
        var live = _list.Rows.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _options.Keys.Where(k => !live.Contains(k)).ToArray()) _options.Remove(key);
        _page = Math.Clamp(_page, 0, Math.Max(0, (_list.Rows.Count - 1) / PageSize)); Paint();
    }
    public void Completed(string? message)
    {
        _options.Clear(); _command = ""; _confirm = null; _status = message ?? "Action completed."; Paint();
    }
    public void Error(string message) { _status = message; Status(); }
    public WorkWithResponse Handle(KeyPress key)
    {
        if (_confirm is { } pending)
        {
            if (key.Aid == AidKey.Pf6) { _confirm = null; Paint(); return new(Action: pending with { Confirm = false }); }
            if (key.Aid is AidKey.Pf3 or AidKey.Pf12 or AidKey.Pa1) { _confirm = null; Paint(); }
            return new();
        }
        if (key.Aid is AidKey.Pf3 or AidKey.Pf12 or AidKey.Pa1) return new(Close: true);
        if (key.Aid == AidKey.Pf1) return new(Help: true);
        if (key.Aid == AidKey.Pf5) { Capture(); return new(Refresh: true); }
        if (key.Aid == AidKey.Pf9) { _form.Activate(_form.Fields.Count - 1); _editor.ApplyEdit(CursorEdit.End); return new(); }
        if (key.Aid is AidKey.RollUp or AidKey.RollDown)
        {
            Capture(); _page = Math.Clamp(_page + (key.Aid == AidKey.RollUp ? 1 : -1), 0, Math.Max(0, (_list.Rows.Count - 1) / PageSize)); Paint(); return new();
        }
        if (key.Aid is AidKey.Enter or AidKey.Pf4)
        {
            Capture();
            var selected = _options.Where(p => p.Value.Length > 0).ToArray();
            if (selected.Length > 1 || selected.Length > 0 && _command.Length > 0) { Error("Choose one action before pressing Enter."); return new(); }
            WorkAction? action = null;
            if (_command.Length > 0 || key.Aid == AidKey.Pf4) action = new("", "Command", _command, Prompt: key.Aid == AidKey.Pf4);
            else if (selected.Length == 1)
            {
                var item = selected[0]; action = _list.Rows.Single(r => r.Key == item.Key).Actions.FirstOrDefault(a => a.Option == item.Value);
                if (action is null) { Error("That option is not available for the selected row."); return new(); }
            }
            if (action is null) { Error("Enter a row option, or press F9 to enter a command."); return new(); }
            if (action.Confirm)
            {
                _confirm = action; Buffer.ClearScreen(); Write(2, 2, "Confirm " + action.Text, DisplayAttribute.HighIntensity);
                Write(5, 2, action.Command); Write(22, 2, "F6=Confirm  F12=Cancel"); return new();
            }
            return new(Action: action);
        }
        if (key.Character != '\0' && !char.IsControl(key.Character))
        {
            if (_editor.Apply(key.Character) is EditingResult.Full or EditingResult.Rejected) Error("Value is full or the character is not supported.");
        }
        else if (key.Edit is { } edit && edit != CursorEdit.None) _editor.ApplyEdit(edit);
        return new();
    }
    private void Capture()
    {
        var rows = _list.Rows.Skip(_page * PageSize).Take(PageSize).ToArray();
        for (var i = 0; i < rows.Length; i++) _options[rows[i].Key] = _form.ReadValue(i).Trim();
        _command = _form.ReadValue(_form.Fields.Count - 1).Trim();
    }
    private void Paint()
    {
        Buffer.ClearScreen(); Write(1, 2, _list.Title, DisplayAttribute.HighIntensity);
        Write(2, 2, $"Page {_page + 1}/{Math.Max(1, (_list.Rows.Count + PageSize - 1) / PageSize)}   {_list.Rows.Count} row(s)");
        Write(3, 2, string.Join("  ", _list.Rows.SelectMany(r => r.Actions).Select(a => a.Option + "=" + a.Text).Distinct()));
        var rows = _list.Rows.Skip(_page * PageSize).Take(PageSize).ToArray();
        var fields = rows.Select((r, index) => new InputField { Row = 5 + index, Column = 2, Length = 2, TabOrder = index, Protect = r.Actions.Count == 0 }).ToList();
        fields.Add(new InputField { Row = 23, Column = 12, Length = 32768, DisplayLength = 66, TabOrder = rows.Length });
        _form = new(Buffer, fields); _editor = new(_form);
        for (var i = 0; i < rows.Length; i++) { Write(5 + i, 6, rows[i].Text); _form.WriteValue(i, _options.GetValueOrDefault(rows[i].Key, "")); }
        _form.WriteValue(fields.Count - 1, _command);
        Write(22, 2, "F1=Help F3/F12=Back F4=Prompt F5=Refresh F9=Command PgUp/PgDn=Page");
        Write(23, 1, "Command:"); Status();
        var first = fields.FindIndex(f => !f.Protect); _form.Activate(first); _editor.ApplyEdit(CursorEdit.Home);
    }
    private void Status() { Buffer.ClearRegion(24, 1, 1, 80); Write(24, 1, _status); }
    private void Write(int row, int column, string value, DisplayAttribute attributes = DisplayAttribute.None)
    { for (var i = 0; i < Math.Min(value.Length, 81 - column); i++) Buffer.Set(row, column + i, TerminalGlyph.IsSingleCell(value[i]) ? value[i] : '?', attributes); }
}
