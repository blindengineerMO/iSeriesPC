using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Terminal;

namespace Ipc.Dsp;

public sealed record CommandPromptResponse(bool Cancelled = false, string? Command = null, bool Help = false);

/// <summary>Bounded paged F4 editor. Parsing and execution remain with the shared command runtime.</summary>
public sealed class CommandPromptSession
{
    private const int PageSize = 12;
    private readonly CommandMetadata _metadata;
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _provided = new(StringComparer.OrdinalIgnoreCase);
    private DisplayForm? _form;
    private FieldEditor? _editor;
    private int _page;
    public DisplayBuffer Buffer { get; } = new();
    public string Name => _metadata.Name;
    public string? Revision => _metadata.Revision;
    public CommandPromptSession(CommandMetadata metadata, CommandCall initial)
    {
        _metadata = metadata;
        if (metadata.Parameters.Count > 128 || metadata.Parameters.Any(p => p.MaximumLength is < 1 or > 4096) || initial.Positional.Count > metadata.MaximumPositional) throw new ArgumentException("Unsupported parameter count.");
        var supplied = initial.Keywords.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < initial.Positional.Count; i++)
        {
            var parameter = metadata.Parameters.Select((p, index) => (Parameter: p, Position: p.Position < 0 ? index : p.Position)).SingleOrDefault(p => p.Position == i).Parameter;
            if (parameter is null || !supplied.TryAdd(parameter.Keyword, initial.Positional[i])) throw new ArgumentException("Parameter supplied by both position and keyword.");
        }
        foreach (var entry in supplied)
            if (!metadata.Parameters.Any(p => p.Keyword == entry.Key.ToUpperInvariant())) throw new ArgumentException("Unsupported parameter " + entry.Key + ".");
        foreach (var parameter in metadata.Parameters)
        {
            var present = supplied.TryGetValue(parameter.Keyword, out var raw);
            raw = present ? raw : parameter.DefaultValue;
            var value = raw is null ? "" : parameter.Literal ? CommandParser.Unquote(raw) : raw;
            if (value.Length > parameter.MaximumLength || value.Any(c => !TerminalGlyph.IsSingleCell(c))) throw new ArgumentException("Parameter " + parameter.Keyword + " exceeds the prompt's character/length limits.");
            _values[parameter.Keyword] = value; if (present) _provided.Add(parameter.Keyword);
        }
        Paint();
    }
    public CommandPromptResponse Handle(KeyPress key)
    {
        if (key.Aid is AidKey.Pf3 or AidKey.Pf12 or AidKey.Pa1) return new(Cancelled: true);
        if (key.Aid == AidKey.Pf1) return new(Help: true);
        if (key.Aid is AidKey.RollUp or AidKey.RollDown)
        {
            Capture(); _page = Math.Clamp(_page + (key.Aid == AidKey.RollUp ? 1 : -1), 0, Math.Max(0, (_metadata.Parameters.Count - 1) / PageSize)); Paint(); return new();
        }
        if (key.Aid == AidKey.Enter)
        {
            try { return new(Command: Build()); }
            catch (ClParseException error) { Error(error.Message); return new(); }
            catch (ArgumentException error) { Error(error.Message); return new(); }
        }
        if (_editor is not null)
        {
            if (key.Character != '\0' && !char.IsControl(key.Character))
            {
                var result = _editor.Apply(key.Character);
                if (result is EditingResult.Full or EditingResult.Rejected) Error("Value is full or the character is not supported.");
            }
            else if (key.Edit is { } edit && edit != CursorEdit.None) _editor.ApplyEdit(edit);
        }
        return new();
    }
    private void Capture()
    {
        if (_form is null) return;
        for (var i = 0; i < _form.Fields.Count; i++) _values[_metadata.Parameters[_page * PageSize + i].Keyword] = _form.ReadValue(i);
    }
    public string Build()
    {
        Capture(); var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in _metadata.Parameters)
        {
            var value = _values[parameter.Keyword];
            if (value.Length == 0 && !_provided.Contains(parameter.Keyword)) continue;
            parameters[parameter.Keyword] = parameter.Literal ? "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'" : value;
        }
        var line = new CommandCall { Name = Name, Keywords = parameters }.ToString();
        if (line.Length > 32768) throw new ArgumentException("Command exceeds 32 KiB.");
        var parsed = CommandParser.Parse(line);
        if (parsed.Name != Name || parsed.Positional.Count != 0 || parsed.Keywords.Count != parameters.Count || parameters.Any(p => parsed.GetOption(p.Key) != p.Value))
            throw new ArgumentException("Unbalanced parameter value. Check quotes and parentheses.");
        return line;
    }
    public void Error(string message)
    {
        foreach (var parameter in _metadata.Parameters.Where(p => p.Secret))
            if (_values[parameter.Keyword] is { Length: > 0 } secret) message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        Buffer.ClearRegion(24, 1, 1, 80); Write(24, 1, message, DisplayAttribute.HighIntensity);
    }
    private void Paint()
    {
        Buffer.ClearScreen(); Write(1, 2, "Prompt command - " + Name, DisplayAttribute.HighIntensity);
        Write(2, 2, "Page " + (_page + 1) + " of " + Math.Max(1, (_metadata.Parameters.Count + PageSize - 1) / PageSize));
        var parameters = _metadata.Parameters.Skip(_page * PageSize).Take(PageSize).ToArray();
        var fields = parameters.Select((p, index) => new InputField { Row = 4 + index, Column = 25, Length = p.MaximumLength, DisplayLength = 54, Hidden = p.Secret, TabOrder = index }).ToArray();
        _form = fields.Length == 0 ? null : new DisplayForm(Buffer, fields); _editor = _form is null ? null : new FieldEditor(_form);
        for (var i = 0; i < parameters.Length; i++)
        { var label = parameters[i].Prompt + (parameters[i].Literal ? " (text)" : ""); Write(4 + i, 2, label[..Math.Min(22, label.Length)]); _form!.WriteValue(i, _values[parameters[i].Keyword]); }
        Write(19, 2, "Text fields: enter text without surrounding quotes.");
        Write(20, 2, "Other fields: CL values, lists or nested commands. Blank uses the default.");
        Write(22, 2, "Enter=Run  F3/F12=Cancel  F1=Help  PgUp/PgDn=Parameters");
        if (_form is not null) { _form.Activate(0); _editor!.ApplyEdit(CursorEdit.Home); }
    }
    private void Write(int row, int column, string value, DisplayAttribute attribute = DisplayAttribute.None)
    { for (var i = 0; i < Math.Min(value.Length, 81 - column); i++) Buffer.Set(row, column + i, TerminalGlyph.IsSingleCell(value[i]) ? value[i] : '?', attribute); }
}
