using System.Globalization;
using Ipc.Core.Menu;
using Ipc.Terminal;

namespace Ipc.Dsp;

public sealed record DesignCompileRequest(string Library, string Name, bool Menu, bool Replace);

/// <summary>Keyboard SDA workflow. Save/compile callbacks run in the owning terminal job's service context.</summary>
public sealed class ScreenDesignerSession
{
    private readonly ScreenDesign _design;
    private readonly Action<string> _save;
    private readonly Action<DesignCompileRequest> _compile;
    private readonly string _source;
    private readonly string _library;
    private readonly DisplayBuffer _buffer = new();
    private readonly DisplayForm _selection;
    private readonly FieldEditor _editor;
    private PanelSession? _form;
    private Action<IReadOnlyDictionary<string, object?>>? _apply;
    private DisplayFileSession? _preview;
    private string? _record;
    private bool _menu;
    private bool _discard;
    private int _page;
    private string _status = "Select a record to edit its fields.";
    public DisplayBuffer Buffer => _preview?.Buffer ?? _form?.Buffer ?? _buffer;
    public ScreenDesign Design => _design;
    public ScreenDesignerSession(ScreenDesign design, string source, string library, Action<string> save, Action<DesignCompileRequest> compile)
    {
        _design = design; _source = source; _library = library; _save = save; _compile = compile;
        _selection = new(_buffer, new[] { new InputField { Row = 22, Column = 12, Length = 6 } }); _editor = new(_selection); Paint();
    }
    public bool Handle(KeyPress key)
    {
        try
        {
            if (_preview is not null)
            {
                if (key.Aid is AidKey.Pf3 or AidKey.Pf12 or AidKey.Pa1) { _preview = null; return false; }
                _preview.Handle(key); return false;
            }
            if (_form is not null)
            {
                var response = _form.Handle(key); if (!response.Accepted) return false;
                if (response.Aid != AidKey.Pf3) _apply!(response.Values);
                _form = null; _apply = null; _status = response.Aid == AidKey.Pf3 ? "Edit cancelled." : _design.Dirty ? "Draft updated. F2 saves the source member." : "Operation completed. Source is saved."; Paint(); return false;
            }
            if (_discard)
            {
                if (key.Aid == AidKey.Pf12) return true;
                if (key.Aid == AidKey.Pf2) { Save(); return true; }
                if (key.Aid == AidKey.Pf3) { _discard = false; Paint(); }
                return false;
            }
            switch (key.Aid)
            {
                case AidKey.Pf3: case AidKey.Pa1:
                    if (!_design.Dirty) return true; _discard = true; Paint(); return false;
                case AidKey.Pf2: Save(); break;
                case AidKey.Pf4: Preview(); return false;
                case AidKey.Pf5: CompileForm(); return false;
                case AidKey.Pf6: Add(); return false;
                case AidKey.Pf7: Edit(); return false;
                case AidKey.Pf8: WindowForm(); return false;
                case AidKey.Pf9: SubfileForm(); return false;
                case AidKey.Pf10: MenuForm(); return false;
                case AidKey.Pf11: Delete(); break;
                case AidKey.Pf12: _record = null; _menu = false; _page = 0; break;
                case AidKey.RollUp: _page++; break;
                case AidKey.RollDown: _page = Math.Max(0, _page - 1); break;
                case AidKey.Enter:
                    if (_record is null && !_menu) { _record = SelectedRecord().Name; _page = 0; }
                    else { Edit(); return false; }
                    break;
                default:
                    if (key.Character != '\0') _editor.Apply(key.Character);
                    else if (key.Edit is { } edit) _editor.ApplyEdit(edit);
                    return false;
            }
            Paint(); return false;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or PanelCompileException or Ipc.Core.Messages.CpfException)
        { _status = error.Message; if (_form is not null) Status(_form.Buffer, _status); else Paint(); return false; }
    }
    private void Save() { _save(_design.Source()); _design.MarkSaved(); _status = "Source member saved."; }
    private int Selection(int count)
    {
        var value = _selection.ReadValue(0).Trim();
        if (value.Length > 0 && !int.TryParse(value, out _)) throw new ArgumentException("Select a listed item number.");
        var index = value.Length == 0 ? _page * 15 : int.Parse(value, CultureInfo.InvariantCulture) - 1;
        if (index < 0 || index >= count) throw new ArgumentException("Select a listed item number."); return index;
    }
    private PanelRecord SelectedRecord() => _menu && _design.Menu is { } menu ? _design.Definition.Records.Single(r => r.Name == menu.Record) : _record is null ? _design.Definition.Records[Selection(_design.Definition.Records.Count)] : _design.Definition.Records.Single(r => r.Name == _record);
    private void Add()
    {
        if (_menu) { MenuOptionForm(null); return; }
        if (_record is not null) { FieldForm(null); return; }
        Form("Add record", new[] { ("NAME", "Record name", "", 10), ("TITLE", "Title", "New record", 48) }, values => _design.AddRecord(Text(values, "NAME").ToUpperInvariant(), Text(values, "TITLE")));
    }
    private void Edit()
    {
        if (_menu) { MenuOptionForm(Selection(_design.Menu!.Options.Count)); return; }
        if (_record is not null) { var record = SelectedRecord(); FieldForm(record.Fields[Selection(record.Fields.Count)]); return; }
        var selected = SelectedRecord();
        Form("Rename record", new[] { ("NAME", "Record name", selected.Name, 10) }, values => _design.RenameRecord(selected.Name, Text(values, "NAME").ToUpperInvariant()));
    }
    private void Delete()
    {
        if (_menu)
        {
            var options = _design.Menu!.Options.ToList(); options.RemoveAt(Selection(options.Count)); _design.SetMenu(_design.Menu with { Options = options });
        }
        else if (_record is not null) { var record = SelectedRecord(); _design.RemoveField(record.Name, record.Fields[Selection(record.Fields.Count)].Name); }
        else _design.RemoveRecord(SelectedRecord().Name);
        _status = "Item removed from the draft. F2 saves.";
    }
    private void FieldForm(PanelField? previous)
    {
        var record = SelectedRecord();
        if (_design.Menu?.Record == record.Name) throw new ArgumentException("Use F10 for menu options.");
        var type = previous?.Type switch { PanelFieldType.Numeric => previous.KeyboardShift is 'S' or 'Y' ? previous.KeyboardShift.ToString() : "Y", PanelFieldType.Date => "L", PanelFieldType.Time => "T", PanelFieldType.Timestamp => "Z", _ => "A" };
        var usage = previous?.Usage switch { PanelFieldUsage.Input => "I", PanelFieldUsage.Both => "B", PanelFieldUsage.Hidden => "H", _ => "O" };
        Form(previous is null ? "Add field" : "Edit field", new[] {
            ("NAME", "Name or *CONST", previous?.Constant == true ? "*CONST" : previous?.Name ?? "", 10),
            ("TYPE", "Type A/S/Y/L/T/Z", type, 1), ("USAGE", "Usage O/I/B/H", usage, 1),
            ("LENGTH", "Length", (previous?.Length ?? 10).ToString(), 4), ("DECIMALS", "Decimal positions", (previous?.Decimals ?? 0).ToString(), 2),
            ("ROW", "Row", (previous?.Row ?? 4).ToString(), 2), ("COLUMN", "Column", (previous?.Column ?? 2).ToString(), 3),
            ("TEXT", "Default/literal", previous?.Keywords.FirstOrDefault(k => k.Name == "DFT")?.Arguments[0] ?? "", 48),
            ("ATTR", "HI RI UL BL CS ND PR", string.Join(' ', previous?.Keywords.FirstOrDefault(k => k.Name == "DSPATR" && k.Conditions.Length == 0)?.Arguments ?? Array.Empty<string>()), 24),
            ("IND", "Indicator or Nnn", previous?.Conditions.FirstOrDefault() is { } condition ? (condition.Negated ? "N" : "") + condition.Indicator.ToString("00") : "", 3) }, values =>
        {
            var name = Text(values, "NAME").ToUpperInvariant(); var constant = name == "*CONST"; var shift = Text(values, "TYPE").ToUpperInvariant();
            var fieldType = shift switch { "A" => PanelFieldType.Character, "S" or "Y" => PanelFieldType.Numeric, "L" => PanelFieldType.Date, "T" => PanelFieldType.Time, "Z" => PanelFieldType.Timestamp, _ => throw new ArgumentException("Invalid field type.") };
            var fieldUsage = Text(values, "USAGE").ToUpperInvariant() switch { "O" => PanelFieldUsage.Output, "I" => PanelFieldUsage.Input, "B" => PanelFieldUsage.Both, "H" => PanelFieldUsage.Hidden, _ => throw new ArgumentException("Invalid field usage.") };
            var keywords = previous?.Keywords.Where(k => k.Name != "DFT" && !(k.Name == "DSPATR" && k.Conditions.Length == 0)).ToList() ?? new();
            var text = Text(values, "TEXT"); if (text.Length > 0 || constant) keywords.Add(ScreenDesign.Keyword("DFT", text));
            var attributes = Text(values, "ATTR").ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries); if (attributes.Length > 0) keywords.Add(ScreenDesign.Keyword("DSPATR", attributes));
            var indicator = Text(values, "IND").ToUpperInvariant(); var conditions = previous?.Conditions.Skip(1).ToList() ?? new();
            if (indicator.Length > 0)
            {
                if (!int.TryParse(indicator.TrimStart('N'), out var number) || number is < 1 or > 99) throw new ArgumentException("Indicator must be 01–99 or N01–N99.");
                conditions.Insert(0, new(number, indicator.StartsWith('N')));
            }
            _design.PutField(record.Name, previous?.Name, new() { Name = constant ? "$LABEL" : name, Constant = constant,
                Length = constant ? text.Length : Number(values, "LENGTH"), Decimals = Number(values, "DECIMALS"), Type = fieldType, KeyboardShift = shift[0], Usage = fieldUsage,
                Row = Number(values, "ROW"), Column = Number(values, "COLUMN"), Conditions = conditions.ToArray(), Keywords = keywords });
        });
    }
    private void WindowForm()
    {
        var record = SelectedRecord(); var window = record.Keywords.FirstOrDefault(k => k.Name == "WINDOW")?.Arguments;
        Form("Window (height 0 removes)", new[] { ("ROW", "Border row", window?[0] ?? "3", 2), ("COLUMN", "Border column", window?[1] ?? "10", 3),
            ("HEIGHT", "Interior height", window?[2] ?? "10", 2), ("WIDTH", "Interior width", window?[3] ?? "40", 3) }, values =>
            _design.SetWindow(record.Name, Number(values, "ROW"), Number(values, "COLUMN"), Number(values, "HEIGHT"), Number(values, "WIDTH")));
    }
    private void SubfileForm() => Form("Add subfile and control", new[] { ("DATA", "Subfile record", "ROWS", 10), ("CONTROL", "Control record", "CONTROL", 10),
        ("PAGE", "Records per page", "5", 2), ("SIZE", "Maximum records", "100", 4), ("TOP", "First data row", "5", 2) }, values =>
        _design.AddSubfile(Text(values, "DATA").ToUpperInvariant(), Text(values, "CONTROL").ToUpperInvariant(), Number(values, "PAGE"), Number(values, "SIZE"), Number(values, "TOP")));
    private void MenuForm() => Form("Menu layout", new[] { ("RECORD", "Record name", _design.Menu?.Record ?? "MENU", 10), ("TITLE", "Menu title", _design.Menu?.Title ?? "Application menu", 48) }, values =>
    {
        _design.SetMenu(new(Text(values, "RECORD").ToUpperInvariant(), Text(values, "TITLE"), _design.Menu?.Options ?? Array.Empty<DesignMenuOption>())); _menu = true; _record = null; _page = 0;
    });
    private void MenuOptionForm(int? index)
    {
        var option = index is null ? null : _design.Menu!.Options[index.Value];
        Form("Menu option", new[] { ("NUMBER", "Option number", option?.Number ?? "1", 3), ("TEXT", "Option text", option?.Text ?? "", 48),
            ("KIND", "Command/SubMenu/Exit", option?.Kind.ToString() ?? "Command", 10), ("TARGET", "Command or menu", option?.Target ?? "", 48), ("AUTH", "Required special authority", option?.RequiredAuthority ?? "*NONE", 10) }, values =>
        {
            if (!Enum.TryParse<MenuOptionKind>(Text(values, "KIND"), true, out var kind) || !Enum.IsDefined(kind)) throw new ArgumentException("Invalid menu option kind.");
            var options = _design.Menu!.Options.ToList(); var item = new DesignMenuOption(Text(values, "NUMBER"), Text(values, "TEXT"), Text(values, "TARGET"), kind, Text(values, "AUTH").ToUpperInvariant());
            if (index is null) options.Add(item); else options[index.Value] = item; _design.SetMenu(_design.Menu with { Options = options });
        });
    }
    private void CompileForm() => Form("Compile saved design", new[] { ("LIBRARY", "Target library", _library, 10), ("NAME", "Target object", _design.Name, 10),
        ("TYPE", "DSPF or MENU", _design.Menu is null ? "DSPF" : "MENU", 4), ("REPLACE", "Replace YES/NO", "NO", 3) }, values =>
    {
        var type = Text(values, "TYPE").ToUpperInvariant(); var replace = Text(values, "REPLACE").ToUpperInvariant();
        if (type is not ("DSPF" or "MENU") || replace is not ("YES" or "NO")) throw new ArgumentException("Choose DSPF/MENU and YES/NO.");
        Save(); _compile(new(Text(values, "LIBRARY").ToUpperInvariant(), Text(values, "NAME").ToUpperInvariant(), type == "MENU", replace == "YES"));
    });
    private void Preview()
    {
        var definition = _design.Definition; var record = SelectedRecord();
        if (record.Keywords.Any(k => k.Name == "SFL")) record = definition.Records.Single(r => r.Keywords.Any(k => k.Name == "SFLCTL" && k.Arguments[0] == record.Name));
        var preview = new DisplayFileSession(definition);
        if (record.Keywords.FirstOrDefault(k => k.Name == "SFLCTL") is { } control)
        {
            var data = definition.Records.Single(r => r.Name == control.Arguments[0]); var state = preview.Subfile(data.Name);
            for (var n = 1; n <= Math.Min(state.Capacity, 20); n++) state.Write(n, Sample(data, n));
        }
        preview.Write(record.Name, Sample(record, 1)); _preview = preview;
    }
    private static Dictionary<string, object?> Sample(PanelRecord record, int number) => record.Fields.Where(f => !f.Constant).ToDictionary(f => f.BindingName, f => f.Type switch {
        PanelFieldType.Numeric => (object?)number, PanelFieldType.Date => new DateOnly(2024, 1, 1), PanelFieldType.Time => new TimeOnly(12, 0), PanelFieldType.Timestamp => new DateTime(2024, 1, 1), _ => "" });
    private void Form(string title, (string Name, string Label, string Value, int Length)[] fields, Action<IReadOnlyDictionary<string, object?>> apply)
    {
        var record = new PanelRecord { Name = "EDIT" }; var values = new Dictionary<string, object?>();
        for (var i = 0; i < fields.Length; i++)
        {
            var item = fields[i]; var row = 4 + i * 2;
            record.Fields.Add(new() { Name = "$LABEL" + i, Constant = true, Length = item.Label.Length, Row = row, Column = 2, Keywords = new() { ScreenDesign.Keyword("DFT", item.Label) } });
            record.Fields.Add(new() { Name = item.Name, Length = item.Length, Usage = PanelFieldUsage.Both, Row = row, Column = 26, Keywords = new() { ScreenDesign.Keyword("CHECK", "LC") } }); values[item.Name] = item.Value;
        }
        var definition = new PanelDefinition { Name = "SDA", Records = new() { record }, Keywords = new() { ScreenDesign.Keyword("CA03"), ScreenDesign.Keyword("CF06") } };
        var form = new PanelSession(definition); form.Write("EDIT", values); Write(form.Buffer, 1, title); Status(form.Buffer, "Enter/F6 Apply  F3 Cancel  Tab Next  Ctrl-K Erase field");
        _form = form; _apply = apply;
    }
    private void Paint()
    {
        _buffer.ClearScreen(); Write(_buffer, 1, "Screen Design Aid - " + _source + (_design.Dirty ? "  *Changed" : ""));
        var definition = _design.Definition;
        var items = _menu ? _design.Menu!.Options.Select(o => o.Number + "  " + o.Text + "  [" + o.Kind + "]").ToArray() :
            _record is null ? definition.Records.Select(r => r.Name + "  " + (r.Keywords.Any(k => k.Name == "SFL") ? "SFL" : r.Keywords.Any(k => k.Name == "SFLCTL") ? "SFLCTL" : "RECORD") + "  " + r.Fields.Count + " fields").ToArray() :
            definition.Records.Single(r => r.Name == _record).Fields.Select(f => (f.Constant ? "*CONST" : f.Name) + "  " + f.Type + " " + f.Usage + "  " + f.Length + "  (" + f.Row + "," + f.Column + ")").ToArray();
        _page = Math.Clamp(_page, 0, Math.Max(0, (items.Length - 1) / 15));
        Write(_buffer, 2, _menu ? "Menu options" : _record is null ? "Records" : "Fields in " + _record);
        for (var i = _page * 15; i < Math.Min(items.Length, (_page + 1) * 15); i++) Write(_buffer, 5 + i % 15, (i + 1).ToString().PadLeft(3) + "  " + items[i]);
        Write(_buffer, 20, "F2 Save F3 Exit F4 Preview F5 Compile F6 Add F7 Edit");
        Write(_buffer, 21, "F8 Window F9 Subfile F10 Menu F11 Delete F12 Back");
        Write(_buffer, 22, "Selection:"); _selection.ClearAll(); _selection.Activate(0);
        Status(_buffer, _discard ? "Unsaved changes: F2 Save and exit  F12 Discard  F3 Continue" : _status);
    }
    private static string Text(IReadOnlyDictionary<string, object?> values, string name) => Convert.ToString(values[name], CultureInfo.InvariantCulture)?.TrimEnd() ?? "";
    private static int Number(IReadOnlyDictionary<string, object?> values, string name) => int.TryParse(Text(values, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException(name + " requires an integer.");
    private static void Write(DisplayBuffer buffer, int row, string text) { for (var i = 0; i < Math.Min(text.Length, buffer.Columns - 2); i++) buffer.Set(row, i + 2, text[i]); }
    private static void Status(DisplayBuffer buffer, string text) { buffer.ClearRegion(buffer.Rows, 1, 1, buffer.Columns); Write(buffer, buffer.Rows, text); }
}
