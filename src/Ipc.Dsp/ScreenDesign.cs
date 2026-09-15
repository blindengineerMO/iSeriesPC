using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Core.Menu;
using Ipc.Core.Objects;

namespace Ipc.Dsp;

public sealed record DesignMenuOption(string Number, string Text, string Target, MenuOptionKind Kind, string RequiredAuthority = "*NONE");
public sealed record DesignMenu(string Record, string Title, IReadOnlyList<DesignMenuOption> Options);

/// <summary>Validated screen-design edits with DDS as the persisted source of truth.</summary>
public sealed class ScreenDesign
{
    private const string MenuPrefix = "     A*IPC.SDA.MENU ";
    private readonly Func<string, string, string?>? _messages;
    private PanelDefinition _definition;
    public PanelDefinition Definition => PanelDefinition.FromJson(_definition.ToJson());
    private DesignMenu? _menu;
    public DesignMenu? Menu { get => _menu is null ? null : _menu with { Options = _menu.Options.ToArray() }; private set => _menu = value is null ? null : value with { Options = value.Options.ToArray() }; }
    public string Name => _definition.Name;
    public bool Dirty { get; private set; }
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter() } };
    private ScreenDesign(PanelDefinition definition, DesignMenu? menu, Func<string, string, string?>? messages) { _definition = definition; Menu = menu; _messages = messages; }
    public static ScreenDesign New(string name, Func<string, string, string?>? messages = null)
    {
        var source = "     A                                      DSPSIZ(24 80) CA03(03)\n" + RecordLine("MAIN") + FieldLine(new PanelField { Name = "$TITLE", Constant = true, Length = 10, Row = 2, Column = 2, Keywords = new() { Keyword("DFT", "New screen") } });
        return new(new DisplayDdsCompiler(messages).Compile(name, source), null, messages) { Dirty = true };
    }
    public static ScreenDesign Open(string name, string source, Func<string, string, string?>? messages = null)
    {
        var definition = new DisplayDdsCompiler(messages).Compile(name, source); DesignMenu? menu = null;
        var encoded = string.Concat(source.Split('\n').Where(l => l.StartsWith(MenuPrefix, StringComparison.Ordinal)).Select(l => l[MenuPrefix.Length..].Trim()));
        if (encoded.Length > 65536) throw new ArgumentException("SDA menu metadata exceeds 64 KiB.");
        if (encoded.Length > 0)
        {
            try { menu = JsonSerializer.Deserialize<DesignMenu>(Convert.FromBase64String(encoded), Json) ?? throw new ArgumentException("Missing SDA menu."); }
            catch (Exception error) when (error is FormatException or JsonException) { throw new ArgumentException("Invalid SDA menu metadata.", error); }
            ValidateMenu(menu);
            if (!definition.Records.Any(r => r.Name == menu.Record)) throw new ArgumentException("SDA menu record does not exist.");
        }
        if (menu is not null)
        {
            var actual = new PanelDefinition { Name = name, Records = new() { definition.Records.Single(r => r.Name == menu.Record) } };
            var expected = new PanelDefinition { Name = name, Records = new() { MenuRecord(menu) } };
            if (Emit(actual, null) != Emit(expected, null)) throw new ArgumentException("Menu metadata and DDS layout disagree; edit menu options through SDA.");
        }
        return new(definition, menu, messages);
    }
    public void MarkSaved() => Dirty = false;
    public string Source() => Emit(_definition, Menu);
    private void Change(Action<PanelDefinition> change, DesignMenu? menu = null, bool replaceMenu = false)
    {
        var definition = Definition; change(definition); var nextMenu = replaceMenu ? menu : Menu;
        var compiled = new DisplayDdsCompiler(_messages).Compile(Name, Emit(definition, nextMenu));
        _definition = compiled; Menu = nextMenu; Dirty = true;
    }
    public void AddRecord(string name, string title)
    {
        ValidateName(name); Change(d => d.Records.Add(new() { Name = name.ToUpperInvariant(), Fields = new() { Label(title, 2, 2) } }));
    }
    public void RemoveRecord(string name)
    {
        if (Menu?.Record == name) throw new ArgumentException("Remove or move the menu design before deleting its record.");
        Change(d => { if (d.Records.RemoveAll(r => r.Name == name) != 1) throw new ArgumentException("Record not found."); });
    }
    public void RenameRecord(string name, string replacement)
    {
        ValidateName(replacement); replacement = replacement.ToUpperInvariant();
        var menu = Menu?.Record == name ? Menu with { Record = replacement } : Menu;
        Change(d =>
        {
            var index = d.Records.FindIndex(r => r.Name == name); if (index < 0) throw new ArgumentException("Record not found.");
            var previous = d.Records[index]; d.Records[index] = new() { Name = replacement, Fields = previous.Fields, Keywords = previous.Keywords, HelpAreas = previous.HelpAreas };
            foreach (var record in d.Records)
                for (var i = 0; i < record.Keywords.Count; i++)
                    if (record.Keywords[i] is { Name: "SFLCTL" } control && control.Arguments[0] == name) record.Keywords[i] = control with { Arguments = new[] { replacement } };
        }, menu, replaceMenu: true);
    }
    public void PutField(string record, string? previousName, PanelField field)
    {
        if (Menu?.Record == record) throw new ArgumentException("Use the menu option editor for menu fields.");
        Change(d =>
        {
            var fields = d.Records.Single(r => r.Name == record).Fields;
            if (previousName is null) fields.Add(field);
            else { var index = fields.FindIndex(f => f.Name == previousName); if (index < 0) throw new ArgumentException("Field not found."); fields[index] = field; }
        });
    }
    public void RemoveField(string record, string field) => Change(d =>
    {
        if (Menu?.Record == record) throw new ArgumentException("Use the menu option editor for menu fields.");
        if (d.Records.Single(r => r.Name == record).Fields.RemoveAll(f => f.Name == field) != 1) throw new ArgumentException("Field not found.");
    });
    public void SetWindow(string record, int row, int column, int height, int width) => Change(d =>
    {
        if (Menu?.Record == record) throw new ArgumentException("Menus use the standard full-screen menu layout.");
        var format = d.Records.Single(r => r.Name == record); format.Keywords.RemoveAll(k => k.Name == "WINDOW");
        if (height > 0) format.Keywords.Add(Keyword("WINDOW", row.ToString(), column.ToString(), height.ToString(), width.ToString()));
    });
    public void AddSubfile(string data, string control, int pageSize, int capacity, int top)
    {
        ValidateName(data); ValidateName(control);
        Change(d =>
        {
            d.Records.Add(new() { Name = data.ToUpperInvariant(), Keywords = new() { Keyword("SFL") }, Fields = new() {
                new PanelField { Name = "ITEM", Length = 20, Usage = PanelFieldUsage.Both, Row = top, Column = 2 },
                new PanelField { Name = "KEY", Length = 10, Usage = PanelFieldUsage.Hidden } } });
            d.Records.Add(new() { Name = control.ToUpperInvariant(), Keywords = new() { Keyword("SFLCTL", data.ToUpperInvariant()), Keyword("SFLPAG", pageSize.ToString()),
                Keyword("SFLSIZ", capacity.ToString()), Keyword("SFLDSP"), Keyword("SFLDSPCTL"), Keyword("PAGEDOWN", "90"), Keyword("PAGEUP", "91") },
                Fields = new() { Label("Subfile " + data.ToUpperInvariant(), 2, 2) } });
        });
    }
    public void SetMenu(DesignMenu menu)
    {
        menu = menu with { Record = menu.Record.ToUpperInvariant() };
        ValidateMenu(menu);
        Change(d =>
        {
            var record = MenuRecord(menu);
            var index = d.Records.FindIndex(r => r.Name == menu.Record); if (index < 0) d.Records.Add(record); else d.Records[index] = record;
        }, menu, replaceMenu: true);
    }
    private static PanelRecord MenuRecord(DesignMenu menu)
    {
        var record = new PanelRecord { Name = menu.Record, Fields = new() { Label(menu.Title, 2, 2) } };
        for (var i = 0; i < menu.Options.Count; i++) record.Fields.Add(Label(menu.Options[i].Number + ". " + menu.Options[i].Text, 5 + i, 4));
        record.Fields.Add(new() { Name = "SELECTION", Length = 10, Usage = PanelFieldUsage.Both, Row = 23, Column = 12 });
        return record;
    }
    public ApplicationMenu CompileMenu(string library, string name)
    {
        if (Menu is null) throw new ArgumentException("This design does not contain a menu.");
        ValidateMenu(Menu); _ = new DisplayDdsCompiler(_messages).Compile(Name, Source());
        return new() { Library = library, Name = name, Title = Menu.Title, Options = Menu.Options.Select(o => new MenuOption { Number = o.Number, Text = o.Text, Target = o.Target, Kind = o.Kind, RequiredAuthority = o.RequiredAuthority }).ToArray() };
    }
    private static void ValidateMenu(DesignMenu menu)
    {
        ValidateName(menu.Record);
        foreach (var option in menu.Options ?? Array.Empty<DesignMenuOption>()) _ = Ipc.Core.Security.SpecialAuthorities.Parse(option.RequiredAuthority);
        if (string.IsNullOrWhiteSpace(menu.Title) || menu.Title.Length > 60 || menu.Title.Any(c => !Ipc.Terminal.TerminalGlyph.IsSingleCell(c)) || menu.Options is null || menu.Options.Count > 16 || menu.Options.Select(o => o.Number).Distinct().Count() != menu.Options.Count)
            throw new ArgumentException("Menu requires a title of at most 60 characters and at most 16 distinct options.");
        foreach (var option in menu.Options)
            if (!int.TryParse(option.Number, out var n) || n is < 1 or > 999 || option.Number != n.ToString(System.Globalization.CultureInfo.InvariantCulture) || string.IsNullOrWhiteSpace(option.Text) || option.Text.Length > 60 || option.Text.Any(c => !Ipc.Terminal.TerminalGlyph.IsSingleCell(c)) ||
                option.Target is null || (option.Kind is MenuOptionKind.Command or MenuOptionKind.SubMenu or MenuOptionKind.Prompt && string.IsNullOrWhiteSpace(option.Target)) || option.Target.Length > 512 || option.Target.Any(char.IsControl) || !Enum.IsDefined(option.Kind)) throw new ArgumentException("Invalid menu option.");
    }
    public static PanelKeyword Keyword(string name, params string[] args) => new(name, args, Array.Empty<PanelCondition>(), 0);
    private static PanelField Label(string text, int row, int column) => new() { Name = "$LABEL", Constant = true, Length = text.Length, Row = row, Column = column, Keywords = new() { Keyword("DFT", text) } };
    private static void ValidateName(string name) { if (!ObjectName.IsValid(name.ToUpperInvariant())) throw new ArgumentException("Invalid DDS name."); }
    private static string Emit(PanelDefinition definition, DesignMenu? menu)
    {
        var source = new StringBuilder();
        if (menu is not null)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(menu, Json)));
            for (var offset = 0; offset < encoded.Length; offset += 60) source.AppendLine(MenuPrefix + encoded.Substring(offset, Math.Min(60, encoded.Length - offset)));
        }
        foreach (var keyword in definition.Keywords) source.Append(KeywordLine(keyword));
        foreach (var record in definition.Records)
        {
            source.Append(RecordLine(record.Name)); foreach (var keyword in record.Keywords) source.Append(KeywordLine(keyword));
            foreach (var area in record.HelpAreas)
            {
                var header = Blank(); header[16] = 'H'; source.AppendLine(new string(header));
                foreach (var keyword in area.Keywords) source.Append(KeywordLine(keyword));
            }
            foreach (var field in record.Fields) source.Append(FieldLine(field));
        }
        return source.ToString();
    }
    private static char[] Blank() { var line = new string(' ', 44).ToCharArray(); line[5] = 'A'; return line; }
    private static string RecordLine(string name) { var line = Blank(); line[16] = 'R'; name.CopyTo(0, line, 18, name.Length); return new string(line) + "\n"; }
    private static void Conditions(char[] line, IReadOnlyList<PanelCondition> conditions)
    {
        if (conditions.Count > 3) throw new ArgumentException("At most three DDS indicator conditions are supported.");
        for (var i = 0; i < conditions.Count; i++) ((conditions[i].Negated ? "N" : " ") + conditions[i].Indicator.ToString("00")).CopyTo(0, line, 7 + i * 3, 3);
    }
    private static string KeywordLine(PanelKeyword keyword)
    {
        var line = Blank(); Conditions(line, keyword.Conditions);
        string Arg(string value, int index)
        {
            var literal = keyword.Name is "DFT" or "DFTVAL" or "TEXT" or "HLPTITLE" or "EDTWRD" || keyword.Name == "ERRMSG" && index == 0;
            return !literal && value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '*' or '_' or '/' or '&' or '.' or '+' or '-' or '#' or '@' or '$') ? value : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }
        return new string(line) + keyword.Name + (keyword.Arguments.Length == 0 ? "" : "(" + string.Join(' ', keyword.Arguments.Select(Arg)) + ")") + "\n";
    }
    private static string FieldLine(PanelField field)
    {
        if (field.Row is < 0 or > 999 || field.Column is < 0 or > 999 || field.Length is < 1 or > 3563 || field.Decimals is < 0 or > 28 || !Enum.IsDefined(field.Type) || !Enum.IsDefined(field.Usage))
            throw new ArgumentException("Invalid display field dimensions/type.");
        if (field.Constant && (field.Usage != PanelFieldUsage.Output || field.Type != PanelFieldType.Character)) throw new ArgumentException("Constants require character output fields.");
        var line = Blank(); Conditions(line, field.Conditions);
        if (!field.Constant)
        {
            ValidateName(field.Name); field.Name.CopyTo(0, line, 18, field.Name.Length); field.Length.ToString().PadLeft(5).CopyTo(0, line, 29, 5);
            line[34] = field.Type switch { PanelFieldType.Numeric => field.KeyboardShift is 'S' or 'Y' ? field.KeyboardShift : 'Y', PanelFieldType.Date => 'L', PanelFieldType.Time => 'T', PanelFieldType.Timestamp => 'Z', _ => 'A' };
            if (field.Type == PanelFieldType.Numeric) field.Decimals.ToString().PadLeft(2).CopyTo(0, line, 35, 2);
            line[37] = field.Usage switch { PanelFieldUsage.Input => 'I', PanelFieldUsage.Both => 'B', PanelFieldUsage.Hidden => 'H', _ => 'O' };
        }
        if (field.Row != 0) field.Row.ToString().PadLeft(3).CopyTo(0, line, 38, 3);
        if (field.Column != 0) field.Column.ToString().PadLeft(3).CopyTo(0, line, 41, 3);
        return new string(line) + "\n" + string.Concat(field.Keywords.Select(KeywordLine));
    }
}
