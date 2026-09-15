using System.Globalization;
using Ipc.Terminal;

namespace Ipc.Dsp;

public sealed record PanelResponse(bool Accepted, AidKey Aid, IReadOnlyDictionary<string, object?> Values,
    bool[] Indicators, CellPosition Cursor, string? Error = null, string? HelpId = null, IReadOnlyList<string>? ModifiedBindings = null, Ipc.Core.Menu.HelpRequest? Help = null);

/// <summary>A single display-file open, with caller-owned values copied at each output/input boundary.</summary>
public sealed class PanelSession
{
    private static readonly CultureInfo DateCulture = CreateDateCulture();
    private static CultureInfo CreateDateCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone(); culture.DateTimeFormat.Calendar.TwoDigitYearMax = 2049; return culture;
    }
    private readonly PanelDefinition _definition;
    private readonly Func<string, string, string?>? _messages;
    private readonly TimeProvider _time;
    private PanelRecord? _record;
    private bool _ready;
    private bool _paintedError;
    private readonly HashSet<int> _errorResponses = new();
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);
    private readonly HashSet<string> _modified = new(StringComparer.Ordinal);
    private List<PanelField> _inputs = new();
    private DisplayForm? _form;
    private FieldEditor? _editor;
    public DisplayBuffer Buffer { get; }
    public bool[] Indicators { get; } = new bool[100];
    public string? Error { get; private set; }
    public string Record => _record?.Name ?? "";
    public PanelSession(PanelDefinition definition, Func<string, string, string?>? messageResolver = null, TimeProvider? time = null)
    {
        _definition = definition; _messages = messageResolver; _time = time ?? TimeProvider.System;
        Buffer = new DisplayBuffer(definition.Rows, definition.Columns);
    }
    public void Write(string record, IReadOnlyDictionary<string, object?> values, IReadOnlyList<bool>? indicators = null)
    {
        _ready = false; _errorResponses.Clear();
        _record = _definition.Records.SingleOrDefault(x => x.Name == record) ?? throw new ArgumentException("Display record not found.", nameof(record));
        if (_record.Keywords.Any(k => k.Name is "SFL" or "SFLCTL" or "WINDOW")) throw new InvalidOperationException("Use DisplayFileSession for subfiles and windows.");
        if (indicators is not null)
        {
            if (indicators.Count != 100) throw new ArgumentException("Display indicators require 100 entries; positions 1–99 are used.");
            for (var i = 1; i < 100; i++) Indicators[i] = indicators[i];
        }
        _values.Clear();
        foreach (var field in _record.Fields.Where(f => !f.Constant))
        {
            var value = values.GetValueOrDefault(field.BindingName);
            if (value is not (null or string or char or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan))
                throw new ArgumentException("Display bindings require scalar values.");
            _values[field.BindingName] = value;
        }
        _text.Clear(); _modified.Clear(); Error = null;
        var first = !_written.Contains(record);
        foreach (var field in _record.Fields)
        {
            _values.TryGetValue(field.BindingName, out var value);
            var active = Active(field.Keywords).ToArray();
            var initial = active.LastOrDefault(k => k.Name == "DFTVAL" && first || k.Name == "DFT");
            if (initial is not null) value = initial.Arguments[0];
            if (active.Any(k => k.Name == "DATE")) value = _time.GetLocalNow().Date;
            if (active.Any(k => k.Name == "TIME")) value = _time.GetLocalNow().TimeOfDay;
            if (active.LastOrDefault(k => k.Name == "MSGCON") is { } message)
            {
                var bound = message.BoundText ?? throw new InvalidOperationException("Message constant is not bound.");
                value = bound.Length > field.Length ? bound[..field.Length] : bound;
            }
            _text[field.Name] = Format(field, value);
            if (Enabled(field) && active.FirstOrDefault(k => k.Name is "ERRMSG" or "ERRMSGID") is { } error)
            {
                Error ??= error.Name == "ERRMSG" ? error.Arguments[0] : Resolve(error.Arguments[0], error.Arguments[1]);
                var responseIndex = error.Name == "ERRMSG" ? 1 : 2;
                if (error.Arguments.Length > responseIndex) _errorResponses.Add(int.Parse(error.Arguments[responseIndex]));
            }
        }
        _inputs = _record.Fields.Where(f => Enabled(f) && f.Usage is PanelFieldUsage.Input or PanelFieldUsage.Both &&
            !Active(f.Keywords).Any(k => k.Name == "DSPATR" && k.Arguments.Contains("PR"))).ToList();
        _form = _inputs.Count == 0 ? null : new DisplayForm(Buffer, _inputs.Select((f, index) => new InputField {
            Row = f.Row, Column = f.Column, Length = f.ScreenLength, TabOrder = index, Usage = f.Type == PanelFieldType.Numeric ? FieldUsage.SignedNumeric : FieldUsage.AlphaNumeric }).ToArray());
        _editor = _form is null ? null : new FieldEditor(_form);
        if (_form is not null) for (var i = 0; i < _inputs.Count; i++) _form.WriteValue(i, _text[_inputs[i].Name]);
        if (_form is not null) { foreach (var value in _form.Values) value.CursorOffset = 0; _form.Activate(0); }
        Paint(clear: !Active(_record.Keywords).Any(k => k.Name == "OVERLAY"));
        if (Active(_record.Keywords).LastOrDefault(k => k.Name == "CSRLOC") is { } cursor &&
            _values.TryGetValue(cursor.Arguments[0].TrimStart('&'), out var row) && _values.TryGetValue(cursor.Arguments[1].TrimStart('&'), out var column))
        {
            var r = Convert.ToInt32(row, CultureInfo.InvariantCulture); var c = Convert.ToInt32(column, CultureInfo.InvariantCulture);
            if (r < 1 || r > Buffer.Rows || c < 1 || c > Buffer.Columns) throw new ArgumentException("CSRLOC is outside the display.");
            var input = _inputs.FindIndex(f => f.Row == r && c >= f.Column && c < f.Column + f.ScreenLength);
            if (input >= 0) { _form!.Activate(input); _form.Active.CursorOffset = c - _inputs[input].Column; }
            Buffer.MoveCursor(r, c);
        }
        _written.Add(record); _ready = true;
    }
    public void SetCursor(int row, int column)
    {
        if (row < 1 || row > Buffer.Rows || column < 1 || column > Buffer.Columns) throw new ArgumentOutOfRangeException(nameof(row));
        var input = _inputs.FindIndex(f => f.Row == row && column >= f.Column && column < f.Column + f.ScreenLength);
        if (input >= 0) { _form!.Activate(input); _form.Active.CursorOffset = column - _inputs[input].Column; }
        Buffer.MoveCursor(row, column);
    }
    public PanelResponse Handle(KeyPress key)
    {
        if (_record is null || !_ready) throw new InvalidOperationException("Write a record before reading input.");
        if (key.Aid == AidKey.None)
        {
            if (_form is not null && _editor is not null)
            {
                var field = _inputs[_form.ActiveIndex]; var before = _form.Active.Text;
                if (key.Character != '\0')
                {
                    var lower = Active(field.Keywords).Any(k => k.Name == "CHECK" && k.Arguments.Contains("LC"));
                    var editResult = _editor.Apply(lower ? key.Character : char.ToUpperInvariant(key.Character));
                    Error = editResult switch { EditingResult.Full => "Field is full; use Replace mode or delete characters.", EditingResult.Rejected => "Character is not valid for this field.", _ => null };
                }
                else if (key.Edit is { } edit) _editor.ApplyEdit(edit);
                if (before != _form.ReadValue(_inputs.IndexOf(field))) _modified.Add(field.Name);
                CopyText(); Paint();
            }
            if (_form is null && key.Edit is { } cursorEdit)
            {
                var row = Buffer.Cursor.Row; var column = Buffer.Cursor.Column;
                if (cursorEdit == CursorEdit.CursorUp) row--; if (cursorEdit == CursorEdit.CursorDown) row++;
                if (cursorEdit == CursorEdit.CursorLeft) column--; if (cursorEdit == CursorEdit.CursorRight) column++;
                SetCursor(Math.Clamp(row, 1, Buffer.Rows), Math.Clamp(column, 1, Buffer.Columns));
            }
            return Response(false, key.Aid);
        }
        if (key.Aid is AidKey.Pa1 or AidKey.Pa2 or AidKey.Pa3 or AidKey.SysReq or AidKey.Clear or AidKey.Print)
        {
            _modified.Clear(); return Response(true, key.Aid) with { ModifiedBindings = Array.Empty<string>() };
        }
        var keys = Active(_definition.Keywords.Concat(_record.Keywords)).ToArray();
        foreach (var alternative in keys.Where(k => k.Name is "ALTPAGEDWN" or "ALTPAGEUP"))
        {
            var name = alternative.Arguments.FirstOrDefault() ?? (alternative.Name == "ALTPAGEDWN" ? "CF08" : "CF07");
            if ((int)key.Aid == (int)AidKey.Pf1 + int.Parse(name[2..]) - 1)
                key = key with { Aid = alternative.Name == "ALTPAGEDWN" ? AidKey.RollUp : AidKey.RollDown };
        }
        var keywordName = key.Aid is >= AidKey.Pf1 and <= AidKey.Pf24 ? ((int)key.Aid - (int)AidKey.Pf1 + 1).ToString("00") : null;
        var option = keys.LastOrDefault(k => keywordName is not null && (k.Name == "CA" + keywordName || k.Name == "CF" + keywordName) ||
            key.Aid == AidKey.RollUp && k.Name == "PAGEDOWN" || key.Aid == AidKey.RollDown && k.Name == "PAGEUP");
        if (key.Aid == AidKey.Pf1 && keys.Any(k => k.Name == "HELP"))
        {
            var help = _form is null ? null : Active(_inputs[_form.ActiveIndex].Keywords).LastOrDefault(k => k.Name == "HLPID")?.Arguments[0];
            var target = PanelHelpArea.Resolve(_definition, _record, Buffer.Cursor, Indicators);
            return Response(false, key.Aid, help: target?.Module ?? help ?? Record) with { Help = target };
        }
        if (key.Aid != AidKey.Enter && option is null) return Response(false, key.Aid, "Key is not enabled for this record.");
        var attention = option?.Name.StartsWith("CA", StringComparison.Ordinal) == true;
        CopyText();
        var next = new Dictionary<string, object?>(_values, StringComparer.Ordinal);
        if (!attention)
        {
            foreach (var field in _inputs)
            {
                var text = _text[field.Name];
                try { next[field.BindingName] = Validate(field, text); }
                catch (ArgumentException error)
                {
                    Error = ValidationMessage(field) ?? error.Message;
                    _form!.Activate(_inputs.IndexOf(field)); _form.Active.CursorOffset = 0; Paint(); return Response(false, key.Aid, Error);
                }
            }
        }
        foreach (var indicator in _errorResponses) Indicators[indicator] = false;
        foreach (var k in keys.Where(k => DisplayDdsCompiler.FunctionKey(k.Name) || k.Name is "PAGEUP" or "PAGEDOWN"))
            if (k.Arguments.Length > 0) Indicators[int.Parse(k.Arguments[0], CultureInfo.InvariantCulture)] = false;
        if (option?.Arguments.Length > 0) Indicators[int.Parse(option.Arguments[0], CultureInfo.InvariantCulture)] = true;
        if (Active(_record.Keywords).LastOrDefault(k => k.Name == "RTNCSRLOC") is { } returned)
        {
            var located = _record.Fields.FirstOrDefault(f => !f.Constant && Enabled(f) && f.Usage != PanelFieldUsage.Hidden &&
                f.Row == Buffer.Cursor.Row && Buffer.Cursor.Column >= f.Column && Buffer.Cursor.Column < f.Column + f.ScreenLength);
            next[returned.Arguments[0].TrimStart('&')] = located is null ? "" : Record;
            next[returned.Arguments[1].TrimStart('&')] = located?.Name ?? "";
            if (returned.Arguments.Length == 3) next[returned.Arguments[2].TrimStart('&')] = located is null ? 0 : Buffer.Cursor.Column - located.Column + 1;
        }
        Error = null; _values.Clear(); foreach (var pair in next) _values[pair.Key] = pair.Value;
        var response = Response(true, key.Aid) with { ModifiedBindings = attention ? Array.Empty<string>() :
            _inputs.Where(f => _modified.Contains(f.Name)).Select(f => f.BindingName).ToArray() };
        _modified.Clear(); return response;
    }
    private void CopyText() { if (_form is not null) for (var i = 0; i < _inputs.Count; i++) _text[_inputs[i].Name] = _form.ReadValue(i); }
    private bool Enabled(PanelField field) => field.Conditions.All(c => c.Matches(Indicators));
    private IEnumerable<PanelKeyword> Active(IEnumerable<PanelKeyword> keywords) => keywords.Where(k => k.Enabled(Indicators));
    private string Resolve(string id, string file) => _messages?.Invoke(id, file) ?? throw new InvalidOperationException($"Message {id} in {file} is unavailable.");
    private PanelResponse Response(bool accepted, AidKey aid, string? error = null, string? help = null) => new(accepted, aid,
        new Dictionary<string, object?>(_values, StringComparer.Ordinal), Indicators.ToArray(), Buffer.Cursor, error, help);
    private void Paint(bool clear = false)
    {
        var cursor = Buffer.Cursor;
        if (_paintedError) Buffer.ClearRegion(Buffer.Rows, 1, 1, Buffer.Columns);
        _paintedError = false;
        if (clear) Buffer.ClearRegion(1, 1, Buffer.Rows, Buffer.Columns);
        foreach (var field in _record!.Fields.Where(f => Enabled(f) && f.Usage != PanelFieldUsage.Hidden))
        {
            var keywords = Active(field.Keywords).ToArray(); var flags = keywords.Where(k => k.Name == "DSPATR").SelectMany(k => k.Arguments).ToHashSet();
            var attributes = flags.Aggregate(DisplayAttribute.None, (a, f) => a | (f switch {
                "HI" => DisplayAttribute.HighIntensity, "RI" => DisplayAttribute.ReverseVideo, "UL" => DisplayAttribute.Underline,
                "BL" => DisplayAttribute.Blink, "CS" => DisplayAttribute.ColumnSeparator, "ND" => DisplayAttribute.NonDisplay, _ => DisplayAttribute.None }));
            if (_inputs.Contains(field)) attributes |= DisplayAttribute.InputField;
            var color = keywords.LastOrDefault(k => k.Name == "COLOR")?.Arguments[0] switch {
                "WHT" => Ansicolor.White, "RED" => Ansicolor.Red, "TRQ" => Ansicolor.Cyan, "YLW" => Ansicolor.Yellow,
                "PNK" => Ansicolor.Magenta, "BLU" => Ansicolor.Blue, _ => Ansicolor.Green };
            var text = flags.Contains("ND") ? "" : _text[field.Name];
            text = field.Type == PanelFieldType.Numeric && field.Usage == PanelFieldUsage.Output ? text.PadLeft(field.ScreenLength) : text.PadRight(field.ScreenLength);
            for (var i = 0; i < field.ScreenLength; i++) Buffer.Set(field.Row, field.Column + i, text[i], attributes, color);
        }
        if (Error is { } error)
        {
            _paintedError = true;
            var message = error.PadRight(Buffer.Columns);
            for (var i = 0; i < Buffer.Columns; i++) Buffer.Set(Buffer.Rows, i + 1, message[i], DisplayAttribute.HighIntensity, Ansicolor.Red);
        }
        Buffer.MoveCursor(cursor.Row, Math.Min(cursor.Column, Buffer.Columns));
    }
    private string? ValidationMessage(PanelField field) => Active(field.Keywords).LastOrDefault(k => k.Name == "CHKMSGID") is { } message
        ? Resolve(message.Arguments[0], message.Arguments[1]) : null;
    private object Validate(PanelField field, string text)
    {
        var checks = Active(field.Keywords).Where(k => k.Name == "CHECK").SelectMany(k => k.Arguments).ToHashSet();
        if (string.IsNullOrWhiteSpace(text) && checks.Contains("AB")) return field.Type == PanelFieldType.Numeric ? 0m : "";
        if (checks.Contains("ME") && !_modified.Contains(field.Name)) throw new ArgumentException($"Enter a value for {field.BindingName}.");
        if (checks.Contains("MF") && text.Length != field.Length) throw new ArgumentException($"Fill all {field.Length} positions of {field.BindingName}.");
        object value = text;
        if (field.Type == PanelFieldType.Numeric)
        {
            if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowTrailingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number))
                throw new ArgumentException($"{field.BindingName} requires a number.");
            if (Active(field.Keywords).Any(k => k.Name == "EDTCDE" && k.Arguments[0] == "Z"))
                for (var place = 0; place < field.Decimals; place++) number /= 10m;
            if (decimal.Round(number, field.Decimals) != number || Math.Abs(number).ToString("F" + field.Decimals, CultureInfo.InvariantCulture).Replace(".", "").TrimStart('0').Length > field.Length)
                throw new ArgumentException($"{field.BindingName} exceeds its numeric precision.");
            value = number;
        }
        else if (field.Type == PanelFieldType.Date)
        {
            if (!DateTime.TryParseExact(text, DatePattern(field), DateCulture, DateTimeStyles.None, out var date)) throw new ArgumentException($"{field.BindingName} requires a valid date.");
            value = DateOnly.FromDateTime(date);
        }
        else if (field.Type == PanelFieldType.Time)
        {
            if (!TimeOnly.TryParseExact(text, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) throw new ArgumentException($"{field.BindingName} requires a valid time.");
            value = time;
        }
        else if (field.Type == PanelFieldType.Timestamp)
        {
            if (!DateTime.TryParseExact(text, "yyyy-MM-dd-HH.mm.ss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)) throw new ArgumentException($"{field.BindingName} requires a valid timestamp.");
            value = stamp;
        }
        foreach (var comparison in Active(field.Keywords).Where(k => k.Name == "COMP"))
        {
            var operand = comparison.Arguments[1]; int order;
            if (value is decimal number)
            {
                if (!decimal.TryParse(operand, NumberStyles.Number, CultureInfo.InvariantCulture, out var expected)) throw new ArgumentException("Invalid numeric comparison constant.");
                order = number.CompareTo(expected);
            }
            else order = string.CompareOrdinal(text.PadRight(field.Length), operand.PadRight(field.Length));
            if (!(comparison.Arguments[0] switch { "EQ" => order == 0, "NE" => order != 0, "GT" => order > 0, "GE" => order >= 0, "LT" => order < 0, _ => order <= 0 }))
                throw new ArgumentException($"{field.BindingName} does not satisfy {comparison.Arguments[0]} {operand}.");
        }
        return value;
    }
    private string Format(PanelField field, object? value)
    {
        var keywords = Active(field.Keywords).ToArray(); string text;
        if (value is null) text = "";
        else if (value is DateTime date) text = date.ToString(field.Type == PanelFieldType.Timestamp ? "yyyy-MM-dd-HH.mm.ss.ffffff" : DatePattern(field), CultureInfo.InvariantCulture);
        else if (value is DateOnly day) text = day.ToString(DatePattern(field), CultureInfo.InvariantCulture);
        else if (value is TimeOnly time) text = time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        else if (value is TimeSpan span) text = span.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        else text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (field.Type == PanelFieldType.Numeric && text.Length > 0 && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            if (decimal.Round(number, field.Decimals) != number || Math.Abs(number).ToString("F" + field.Decimals, CultureInfo.InvariantCulture).Replace(".", "").TrimStart('0').Length > field.Length)
                throw new ArgumentException($"Value for {field.BindingName} exceeds its numeric precision.");
            var code = keywords.LastOrDefault(k => k.Name == "EDTCDE")?.Arguments[0];
            text = code switch {
                "2" or "4" when number == 0 => "",
                "1" or "2" => Math.Abs(number).ToString("N" + field.Decimals, CultureInfo.InvariantCulture),
                "3" or "4" => Math.Abs(number).ToString("F" + field.Decimals, CultureInfo.InvariantCulture),
                "Z" => Math.Abs(number).ToString("F" + field.Decimals, CultureInfo.InvariantCulture).Replace(".", "").TrimStart('0'),
                _ => number.ToString("F" + field.Decimals, CultureInfo.InvariantCulture) };
            if (code is "1" or "2" or "3" or "4" && text.StartsWith("0.", StringComparison.Ordinal)) text = text[1..];
            if (keywords.LastOrDefault(k => k.Name == "EDTWRD") is { } word) text = NumericEditWord.Format(number, field.Length, field.Decimals, word.Arguments[0]);
        }
        if (text.Any(c => !TerminalGlyph.IsSingleCell(c))) throw new ArgumentException("Display values require supported single-cell characters; control, combining and wide characters are invalid.");
        if (text.Length > field.ScreenLength) throw new ArgumentException($"Value for {field.BindingName} exceeds its display length.");
        return text;
    }
    private string DatePattern(PanelField field) => Active(field.Keywords).LastOrDefault(k => k.Name == "DATFMT")?.Arguments[0] switch {
        "*USA" => "MM/dd/yyyy", "*EUR" => "dd.MM.yyyy", "*JIS" => "yyyy-MM-dd", "*YMD" => "yy/MM/dd", "*DMY" => "dd/MM/yy", "*MDY" => "MM/dd/yy", _ => "yyyy-MM-dd" };
}
