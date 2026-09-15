using Ipc.Terminal;

namespace Ipc.Dsp;

/// <summary>Composes one page into the shared field editor; validates the entire page before transferring any row.</summary>
public sealed class SubfilePanel
{
    private readonly PanelDefinition _definition;
    private readonly PanelRecord _control;
    private readonly PanelRecord _data;
    private readonly Func<string, string, string?>? _messages;
    private readonly Func<string, string, DisplayMessage?>? _programMessages;
    private readonly bool _messageSubfile;
    private Dictionary<int, DisplayMessage> _visibleMessages = new();
    private PanelSession? _panel;
    private Dictionary<string, (int Number, PanelField Field)> _bindings = new();
    private Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private bool[] _indicators = new bool[100];
    private int _start;
    private readonly int _top;
    private readonly int _height;
    private readonly int _pageSize;
    public SubfileState State { get; }
    public bool Folded { get; private set; }
    public int FirstRecord => State.RecordNumbers.Skip(_start).FirstOrDefault();
    public int PageCapacity => _pageSize * (Folded ? 1 : _height);
    public DisplayBuffer Buffer => _panel?.Buffer ?? throw new InvalidOperationException("Write control record first.");
    public SubfilePanel(PanelDefinition definition, string control, SubfileState? state = null, Func<string, string, string?>? messages = null, Func<string, string, DisplayMessage?>? programMessages = null)
    {
        _programMessages = programMessages;
        _definition = definition; _control = definition.Records.Single(r => r.Name == control); _messages = messages;
        if (SubfileLayout.Keyword(_control, "WINDOW") is not null) throw new InvalidOperationException("Use DisplayFileSession for window composition.");
        _data = definition.Records.Single(r => r.Name == SubfileLayout.Keyword(_control, "SFLCTL")!.Arguments[0]);
        State = state ?? new SubfileState(_data, SubfileLayout.Number(_control, "SFLSIZ"));
        var visible = _data.Fields.Where(f => f.Usage != PanelFieldUsage.Hidden).ToArray();
        var message = SubfileLayout.Keyword(_data, "SFLMSGRCD"); _messageSubfile = message is not null;
        _top = message is null ? visible.Min(f => f.Row) : int.Parse(message.Arguments[0]);
        _height = message is null ? visible.Max(f => f.Row) - _top + 1 : 1;
        _pageSize = SubfileLayout.Number(_control, "SFLPAG");
        Folded = SubfileLayout.Keyword(_control, "SFLDROP") is null;
    }
    public void Write(IReadOnlyDictionary<string, object?> values, IReadOnlyList<bool>? indicators = null)
    {
        if (indicators is not null && indicators.Count != 100) throw new ArgumentException("Expected 100 indicators.");
        if (indicators is not null) _indicators = indicators.ToArray();
        _values = new(values, StringComparer.Ordinal);
        if (Active("SFLCLR")) { State.Clear(); _start = 0; }
        _start = Math.Min(_start, Math.Max(0, State.Count - 1));
        Compose();
    }
    public PanelResponse Handle(KeyPress key)
    {
        if (_panel is null) throw new InvalidOperationException("Write control record first.");
        var aid = Normalize(key.Aid);
        if (_messageSubfile && aid == AidKey.Pf1 && _visibleMessages.TryGetValue(Buffer.Cursor.Row, out var help))
            return new(false, aid, new Dictionary<string, object?>(_values), _indicators.ToArray(), Buffer.Cursor, HelpId: help.HelpId);
        var fold = _control.Keywords.FirstOrDefault(k => k.Name is "SFLFOLD" or "SFLDROP" && k.Enabled(_indicators));
        var toggle = fold is not null && aid == FunctionAid(fold.Arguments[0]);
        var page = aid is AidKey.RollUp or AidKey.RollDown;
        var response = _panel.Handle(key with { Aid = aid });
        if (!response.Accepted) return Public(response);
        // CA keys do not transfer edits; PanelSession returns the original page bindings for them.
        foreach (var group in _bindings.GroupBy(p => p.Value.Number))
        {
            var changed = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var pair in group.Where(p => response.ModifiedBindings?.Contains(p.Key) == true))
                changed[pair.Value.Field.BindingName] = response.Values[pair.Key];
            State.Accept(group.Key, changed);
        }
        foreach (var field in _control.Fields.Where(f => !f.Constant))
            if (response.Values.TryGetValue(field.BindingName, out var value)) _values[field.BindingName] = value;
        _indicators = response.Indicators.ToArray();
        if (toggle)
        {
            Folded = !Folded; try { Compose(); } catch { Folded = !Folded; throw; } return Public(response with { Accepted = false, Cursor = Buffer.Cursor });
        }
        if (page)
        {
            var next = aid == AidKey.RollUp ? _start + PageCapacity : Math.Max(0, _start - PageCapacity);
            if (Active("SFLDSP") && next != _start && next < State.Count)
            {
                var previous = _start; _start = next;
                ClearPageResponse(aid); try { Compose(); } catch { _start = previous; throw; } return Public(response with { Accepted = false, Cursor = Buffer.Cursor, Indicators = _indicators.ToArray() });
            }
            var enabled = _definition.Keywords.Concat(_control.Keywords).Any(k => k.Name == (aid == AidKey.RollUp ? "PAGEDOWN" : "PAGEUP") && k.Enabled(_indicators));
            if (!enabled) return Public(response with { Accepted = false, Error = "No further subfile records." });
        }
        return Public(response);
    }
    private PanelResponse Public(PanelResponse response) => response with { Values = new Dictionary<string, object?>(_values), Indicators = _indicators.ToArray(),
        ModifiedBindings = response.ModifiedBindings?.Where(k => _control.Fields.Any(f => f.BindingName == k)).ToArray() };
    private bool Active(string name) => _control.Keywords.Any(k => k.Name == name && k.Enabled(_indicators));
    private AidKey Normalize(AidKey aid)
    {
        foreach (var k in _definition.Keywords.Where(k => k.Name is "ALTPAGEDWN" or "ALTPAGEUP"))
            if (aid == FunctionAid(k.Arguments.FirstOrDefault() ?? (k.Name == "ALTPAGEDWN" ? "CF08" : "CF07")))
                return k.Name == "ALTPAGEDWN" ? AidKey.RollUp : AidKey.RollDown;
        return aid;
    }
    private static AidKey FunctionAid(string name) => (AidKey)((int)AidKey.Pf1 + int.Parse(name[2..]) - 1);
    private void ClearPageResponse(AidKey aid)
    {
        foreach (var keyword in _definition.Keywords.Concat(_control.Keywords).Where(k => k.Name == (aid == AidKey.RollUp ? "PAGEDOWN" : "PAGEUP") && k.Arguments.Length > 0))
            _indicators[int.Parse(keyword.Arguments[0])] = false;
    }
    private void Compose()
    {
        var record = new PanelRecord { Name = _control.Name, Line = _control.Line, HelpAreas = _control.HelpAreas,
            Keywords = _control.Keywords.Where(k => !k.Name.StartsWith("SFL", StringComparison.Ordinal) && k.Name != "WINDOW").ToList() };
        var definition = new PanelDefinition { Name = _definition.Name, Rows = _definition.Rows, Columns = _definition.Columns,
            Keywords = _definition.Keywords.Where(k => k.Name is not ("ALTPAGEDWN" or "ALTPAGEUP")).ToList(), Records = new() { record } };
        // The editor needs to accept paging even when the host has not enabled boundary returns.
        foreach (var name in new[] { "PAGEDOWN", "PAGEUP" })
            if (!definition.Keywords.Concat(record.Keywords).Any(k => k.Name == name && k.Enabled(_indicators))) record.Keywords.Add(new(name, Array.Empty<string>(), Array.Empty<PanelCondition>(), 0));
        foreach (var fold in _control.Keywords.Where(k => k.Name is "SFLFOLD" or "SFLDROP" && k.Enabled(_indicators)))
            record.Keywords.Add(new(fold.Arguments[0], Array.Empty<string>(), Array.Empty<PanelCondition>(), 0));
        if (Active("SFLDSPCTL")) record.Fields.AddRange(_control.Fields);
        var values = new Dictionary<string, object?>(_values);
        var bindings = new Dictionary<string, (int Number, PanelField Field)>();
        var visibleMessages = new Dictionary<int, DisplayMessage>();
        if (Active("SFLDSP"))
        {
            var index = 0;
            foreach (var number in State.RecordNumbers.Skip(_start).Take(PageCapacity))
            {
                var row = State.Read(number);
                if (_messageSubfile)
                {
                    var key = row.Values.GetValueOrDefault(_data.Fields[0].BindingName) as string;
                    var queue = row.Values.GetValueOrDefault(_data.Fields[1].BindingName) as string;
                    if (key is null || key.Length != 4 || key.Any(c => c > 255) || string.IsNullOrWhiteSpace(queue) || queue.Length > 10 || queue.Any(char.IsControl))
                        throw new ArgumentException("Message subfiles require a four-byte key and program queue name of at most 10 characters.");
                    var message = _programMessages?.Invoke(queue.TrimEnd(), key) ?? throw new InvalidOperationException("Subfile message is unavailable from the program queue.");
                    if (message.Text.Length > 32768 || message.Text.Any(char.IsControl)) throw new ArgumentException("Invalid display message text.");
                    var messageRow = _top + index; visibleMessages[messageRow] = message;
                    var width = Math.Min(definition.Columns - 4, definition.Columns == 132 ? 128 : 76);
                    record.Fields.Add(new PanelField { Name = "$MSG" + number, Constant = true, Length = width, Usage = PanelFieldUsage.Output, Row = messageRow, Column = 2,
                        Keywords = new() { new("DFT", new[] { message.Text[..Math.Min(width, message.Text.Length)] }, Array.Empty<PanelCondition>(), 0), new("DSPATR", new[] { "HI" }, Array.Empty<PanelCondition>(), 0) } });
                    index++; continue;
                }
                foreach (var field in _data.Fields)
                {
                    if (field.Usage == PanelFieldUsage.Hidden || !field.Conditions.All(c => c.Matches(row.Indicators)) || !Folded && field.Row != _top) continue;
                    var binding = "$SFL" + number + "_" + field.Name;
                    var keywords = field.Keywords.Where(k => k.Name != "ALIAS" && k.Enabled(row.Indicators)).Select(k => k with { Conditions = Array.Empty<PanelCondition>() }).ToList();
                    var clone = new PanelField { Name = binding, Constant = field.Constant, Length = field.Length, Decimals = field.Decimals,
                        Usage = field.Usage, Type = field.Type, KeyboardShift = field.KeyboardShift, Row = field.Row + index * (Folded ? _height : 1),
                        Column = field.Column, Line = field.Line, Keywords = keywords };
                    record.Fields.Add(clone);
                    if (!field.Constant) { values[binding] = row.Values.GetValueOrDefault(field.BindingName); bindings[binding] = (number, field); }
                }
                index++;
            }
        }
        var cursor = _panel?.Buffer.Cursor;
        var panel = new PanelSession(definition, _messages); panel.Write(record.Name, values, _indicators);
        _panel = panel; _bindings = bindings; _visibleMessages = visibleMessages;
        if (_messageSubfile) _panel.SetCursor(cursor?.Row ?? _top, cursor?.Column ?? 2);
        if (Active("SFLDSP") && _control.Keywords.Any(k => k.Name == "SFLEND"))
        {
            var label = _start + PageCapacity < State.Count || !Active("SFLEND") ? "More..." : "Bottom ";
            var row = _top + _height * _pageSize - 1;
            for (var i = 0; i < label.Length; i++) Buffer.Set(row, Buffer.Columns - label.Length + i + 1, label[i]);
        }
    }
}
