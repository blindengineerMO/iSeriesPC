using Ipc.Cl.Parsing;
using Ipc.Core.Objects;
using Ipc.Terminal;

namespace Ipc.Dsp;

/// <summary>Strict fixed-column display DDS. Every keyword is preserved with its indicator conditions.</summary>
public sealed class DisplayDdsCompiler(Func<string, string, string?>? messageResolver = null)
{
    private static readonly HashSet<string> FileKeywords = new(StringComparer.Ordinal) {
        "HLPPNLGRP", "HLPTITLE", "DSPSIZ", "INDARA", "PAGEDOWN", "PAGEUP", "ALTPAGEDWN", "ALTPAGEUP", "HELP", "TEXT" };
    private static readonly HashSet<string> RecordKeywords = new(StringComparer.Ordinal) {
        "HLPTITLE", "PAGEDOWN", "PAGEUP", "HELP", "CSRLOC", "RTNCSRLOC", "TEXT", "OVERLAY",
        "SFLMSGRCD", "SFL", "SFLCTL", "SFLSIZ", "SFLPAG", "SFLDSP", "SFLDSPCTL", "SFLCLR", "SFLEND", "SFLNXTCHG", "SFLFOLD", "SFLDROP", "WINDOW" };
    private static readonly HashSet<string> HelpKeywords = new(StringComparer.Ordinal) { "HLPARA", "HLPPNLGRP" };
    private static readonly HashSet<string> FieldKeywords = new(StringComparer.Ordinal) {
        "DSPATR", "COLOR", "DFT", "DFTVAL", "ERRMSG", "ERRMSGID", "MSGCON", "COMP", "CHECK", "CHKMSGID",
        "SFLMSGKEY", "SFLPGMQ", "DATE", "TIME", "DATFMT", "TIMFMT", "EDTCDE", "EDTWRD", "ALIAS", "TEXT", "HLPID" };
    public PanelDefinition Compile(string name, string source, string sourceName = "<DDS>")
    {
        if (!ObjectName.IsValid(name)) throw Error(sourceName, 1, "NAME", "Invalid display file name.");
        if (source.Length > 1024 * 1024) throw Error(sourceName, 1, "SOURCE", "Display DDS exceeds 1 MiB.");
        var messageBindings = new Dictionary<(string, string), string>();
        var definition = new PanelDefinition { Name = name }; PanelRecord? record = null; PanelField? field = null; PanelHelpArea? help = null;
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > 10000) throw Error(sourceName, 1, "SOURCE", "Display DDS exceeds 10000 lines.");
        for (var index = 0; index < lines.Length; index++)
        {
            var original = lines[index]; var line = index + 1;
            if (string.IsNullOrWhiteSpace(original)) continue;
            if (original.Length > 5000) throw Error(sourceName, line, "SOURCE", "DDS line exceeds 5000 columns.");
            var raw = original.PadRight(44);
            if (raw[5] != 'A') throw Error(sourceName, line, "FORM", "Display DDS requires A in column 6.", 6);
            if (raw[6] == '*') continue;
            if (raw[6] != ' ') throw Error(sourceName, line, "CONDITION", "AND/OR continuation records are not supported.", 7);
            var conditions = Conditions(raw[7..16], sourceName, line);
            var fieldName = raw[18..28].Trim().ToUpperInvariant();
            if (raw[16] == 'R')
            {
                if (!ObjectName.IsValid(fieldName) || definition.Records.Any(x => x.Name == fieldName))
                    throw Error(sourceName, line, fieldName, "Missing, invalid or duplicate record name.", 19);
                if (conditions.Length > 0) throw Error(sourceName, line, "RECORD", "Condition fields/keywords rather than record declarations.");
                if (definition.Records.Count >= 256) throw Error(sourceName, line, "RECORD", "Display record limit is 256.");
                record = new PanelRecord { Name = fieldName, Line = line }; definition.Records.Add(record); field = null; help = null;
            }
            else if (raw[16] == 'H')
            {
                if (record is null || record.Fields.Count > 0 || fieldName.Length > 0) throw Error(sourceName, line, "HLPARA", "H specifications must precede the record fields.");
                help = new() { Line = line }; record.HelpAreas.Add(help); field = null;
            }
            else if (raw[16] != ' ' || raw[17] != ' ' || raw[28] != ' ')
                throw Error(sourceName, line, "REFERENCE", "Unsupported record/reference specification.", 17);
            else if (fieldName.Length > 0 || raw[38..44].Trim().Length > 0)
            {
                if (record is null) throw Error(sourceName, line, "FIELD", "A record must be declared first.");
                help = null;
                var constant = fieldName.Length == 0;
                if (!constant && (!ObjectName.IsValid(fieldName) || record.Fields.Any(x => x.Name == fieldName)))
                    throw Error(sourceName, line, fieldName, "Invalid or duplicate field name.", 19);
                if (record.Fields.Count >= 1024) throw Error(sourceName, line, "FIELD", "Record field limit is 1024.");
                var type = raw[34] switch { ' ' or 'A' => PanelFieldType.Character, 'S' or 'Y' => PanelFieldType.Numeric,
                    'L' => PanelFieldType.Date, 'T' => PanelFieldType.Time, 'Z' => PanelFieldType.Timestamp,
                    _ => throw Error(sourceName, line, raw[34].ToString(), "Unsupported display field type.", 35) };
                var decimals = Number(raw[35..37], 0, sourceName, line, "DECIMALS");
                if (raw[34] == ' ' && raw[35..37].Trim().Length > 0) type = PanelFieldType.Numeric;
                var usage = raw[37] switch { ' ' or 'O' => PanelFieldUsage.Output, 'I' => PanelFieldUsage.Input,
                    'B' => PanelFieldUsage.Both, 'H' => PanelFieldUsage.Hidden, _ => throw Error(sourceName, line, "USAGE", "Expected O, I, B or H.", 38) };
                field = new PanelField { Name = constant ? "$CONST" + record.Fields.Count : fieldName, Constant = constant,
                    Length = Number(raw[29..34], type == PanelFieldType.Date ? 10 : type == PanelFieldType.Time ? 8 : type == PanelFieldType.Timestamp ? 26 : 0, sourceName, line, "LENGTH"),
                    Decimals = decimals, Usage = usage, Type = type, KeyboardShift = raw[34], Row = Number(raw[38..41], 0, sourceName, line, "ROW"),
                    Column = Number(raw[41..44], 0, sourceName, line, "COLUMN"), Conditions = conditions, Line = line };
                record.Fields.Add(field);
            }
            var keywords = ParseKeywords(original.Length > 44 ? original[44..] : "", conditions, sourceName, line);
            var destination = help?.Keywords ?? field?.Keywords ?? record?.Keywords ?? definition.Keywords;
            var allowed = help is not null ? HelpKeywords : field is not null ? FieldKeywords : record is not null ? RecordKeywords : FileKeywords;
            foreach (var keyword in keywords)
            {
                var function = FunctionKey(keyword.Name);
                if (!allowed.Contains(keyword.Name) && !(help is null && field is null && function)) throw Error(sourceName, line, keyword.Name, "Unsupported keyword at this DDS level.");
                ValidateKeyword(keyword, definition, field, sourceName);
                if (keyword.Name == "MSGCON")
                {
                    var key = (keyword.Arguments[1], keyword.Arguments[2]);
                    if (!messageBindings.TryGetValue(key, out var message))
                    {
                        message = messageResolver?.Invoke(key.Item1, key.Item2)
                            ?? throw Error(sourceName, line, "MSGCON", "Message description is unavailable at compile time.");
                        if (message.Length > 512 || message.Any(c => !TerminalGlyph.IsSingleCell(c))) throw Error(sourceName, line, "MSGCON", "Invalid message constant text.");
                        messageBindings.Add(key, message);
                    }
                    destination.Add(keyword with { BoundText = message });
                }
                else destination.Add(keyword);
            }
        }
        if (definition.Records.Count == 0) throw Error(sourceName, 1, "RECORD", "Display DDS has no records.");
        foreach (var item in definition.Records)
        {
            if (item.Fields.Count == 0 && !item.Keywords.Any(k => k.Name == "SFLCTL")) throw Error(sourceName, item.Line, item.Name, "Record has no fields.");
            foreach (var value in item.Fields)
            {
                var literal = value.Keywords.FirstOrDefault(k => k.Name is "DFT" or "MSGCON");
                if (value.Constant && literal?.Name == "DFT") value.Length = literal.Arguments[0].Length;
                if (value.Constant && value.Keywords.Any(k => k.Name == "DATE")) value.Length = 10;
                if (value.Constant && value.Keywords.Any(k => k.Name == "TIME")) value.Length = 8;
                if (value.Length is < 1 or > 3563 || value.Decimals < 0 || value.Decimals > 28 || value.Type == PanelFieldType.Numeric && (value.Length > 29 || value.Decimals >= value.Length))
                    throw Error(sourceName, value.Line, value.Name, "Invalid field length or decimal positions.");
                if (value.Usage != PanelFieldUsage.Hidden && (value.Row < 1 || value.Row > definition.Rows || value.Column < 1 || value.Column + value.ScreenLength - 1 > definition.Columns || value.Row == 1 && value.Column == 1 && !item.Keywords.Any(k => k.Name is "WINDOW" or "SFL")))
                    throw Error(sourceName, value.Line, value.Name, "Field exceeds one display row or occupies reserved position 1,1.");
                if (value.Constant && value.Usage != PanelFieldUsage.Output) throw Error(sourceName, value.Line, value.Name, "Constants must be output-only.");
                if (value.Keywords.Any(k => k.Name == "DFTVAL") && (value.Constant || value.Usage is PanelFieldUsage.Input or PanelFieldUsage.Hidden || value.Keywords.Any(k => k.Name is "DFT" or "EDTCDE" or "EDTWRD")))
                    throw Error(sourceName, value.Line, "DFTVAL", "DFTVAL requires a named output-capable field without DFT/edit keywords.");
                if (value.Keywords.Any(k => k.Name == "EDTCDE") && value.Keywords.Any(k => k.Name == "EDTWRD")) throw Error(sourceName, value.Line, "EDIT", "Choose one edit code or edit word.");
            }
            foreach (var cursor in item.Keywords.Where(k => k.Name is "CSRLOC" or "RTNCSRLOC"))
            {
                for (var parameter = 0; parameter < cursor.Arguments.Length; parameter++)
                {
                    var reference = cursor.Arguments[parameter].TrimStart('&');
                    var target = item.Fields.SingleOrDefault(f => !f.Constant && f.Name == reference);
                    var numeric = cursor.Name == "CSRLOC" || parameter == 2;
                    if (target is null || target.Usage != PanelFieldUsage.Hidden ||
                        numeric && (target.Type != PanelFieldType.Numeric || target.Decimals != 0) ||
                        !numeric && (target.Type != PanelFieldType.Character || target.Length < 10))
                        throw Error(sourceName, cursor.Line, cursor.Name, "Cursor parameters require declared hidden fields of the appropriate numeric/character type.");
                }
            }
            if (item.Fields.Where(f => !f.Constant).GroupBy(f => f.BindingName, StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw Error(sourceName, item.Line, "ALIAS", "Record bindings must be unique.");
        }
        foreach (var alternative in definition.Keywords.Where(k => k.Name is "ALTPAGEDWN" or "ALTPAGEUP"))
        {
            var key = alternative.Arguments.FirstOrDefault() ?? (alternative.Name == "ALTPAGEDWN" ? "CF08" : "CF07");
            var all = definition.Keywords.Concat(definition.Records.SelectMany(r => r.Keywords)).ToArray();
            if (all.Any(k => FunctionKey(k.Name) && k.Name[2..] == key[2..])) throw Error(sourceName, alternative.Line, alternative.Name, "Alternative page key is already assigned.");
            if (!all.Any(k => k.Name is "PAGEDOWN" or "PAGEUP")) throw Error(sourceName, alternative.Line, alternative.Name, "Alternative page keys require a pageable record.");
        }
        SubfileLayout.Validate(definition, sourceName);
        PanelHelpArea.Validate(definition, sourceName);
        return definition;
    }
    private static PanelCondition[] Conditions(string value, string source, int line)
    {
        var conditions = new List<PanelCondition>();
        for (var offset = 0; offset < 9; offset += 3)
        {
            var slot = value.Substring(offset, 3); if (string.IsNullOrWhiteSpace(slot)) continue;
            if (slot[0] is not (' ' or 'N') || !int.TryParse(slot[1..], out var number) || number is < 1 or > 99)
                throw Error(source, line, slot, "Invalid indicator; expected 01–99 with optional N.", offset + 8);
            conditions.Add(new(number, slot[0] == 'N'));
        }
        return conditions.ToArray();
    }
    private static List<PanelKeyword> ParseKeywords(string text, PanelCondition[] conditions, string source, int line)
    {
        try
        {
            return CommandParser.Tokenize(text).Select(token =>
            {
                if (token.StartsWith('\'')) return new PanelKeyword("DFT", new[] { CommandParser.Unquote(token) }, conditions, line);
                var open = token.IndexOf('('); var name = (open < 0 ? token : token[..open]).ToUpperInvariant();
                name = name switch { "PAGEDWN" or "ROLLUP" => "PAGEDOWN", "ROLLDOWN" => "PAGEUP", _ => name };
                var args = open < 0 ? Array.Empty<string>() : CommandParser.Tokenize(token[(open + 1)..^1]).Select(CommandParser.Unquote).ToArray();
                return new PanelKeyword(name, args, conditions, line);
            }).ToList();
        }
        catch (ClParseException error) { throw Error(source, line, "KEYWORDS", error.Message); }
    }
    private static void ValidateKeyword(PanelKeyword keyword, PanelDefinition definition, PanelField? field, string source)
    {
        var args = keyword.Arguments; var name = keyword.Name;
        if (args.Any(arg => arg.Any(c => !TerminalGlyph.IsSingleCell(c)))) throw Error(source, keyword.Line, name, "DDS literals require supported single-cell characters; control, combining and wide characters are invalid.");
        void Require(bool condition, string message) { if (!condition) throw Error(source, keyword.Line, name, message); }
        void Count(int min, int max) => Require(args.Length >= min && args.Length <= max, $"Expected {min}–{max} arguments.");
        if (FunctionKey(name) || name is "PAGEDOWN" or "PAGEUP" or "HELP")
        {
            Count(0, 2); Require(args.Length == 0 || int.TryParse(args[0], out var indicator) && indicator is >= 1 and <= 99, "Invalid response indicator."); return;
        }
        switch (name)
        {
            case "HLPTITLE": Count(1, 1); Require(args[0].Length <= 120, "Help title exceeds 120 characters."); break;
            case "HLPPNLGRP": Count(2, 2); Require(UimCompiler.ValidModule(args[0]) && UimCompiler.ValidGroup(args[1]), "Invalid help module or panel group."); break;
            case "HLPARA":
                Require(keyword.Conditions.Length == 0, "Help areas cannot be indicator-conditioned; condition HLPPNLGRP instead.");
                Require(args.Length == 1 && args[0] is "*RCD" or "*NONE" || args.Length == 2 && args[0] == "*FLD" && ObjectName.IsValid(args[1]) ||
                    args.Length == 4 && args.All(a => int.TryParse(a, out var n) && n > 0), "Expected *RCD, *NONE, *FLD field, or top left bottom right."); break;
            case "SFLMSGKEY": case "SFLPGMQ":
                Count(0, 0); var predefinedLength = name == "SFLMSGKEY" ? 4 : 10;
                Require(field is not null && field.Type == PanelFieldType.Character && field.Length is 0 || field is not null && field.Type == PanelFieldType.Character && field.Length == predefinedLength, "Predefined message fields require their fixed character length.");
                Require(keyword.Conditions.Length == 0 && field!.Row == 0 && field.Column == 0, "Predefined message fields are unconditional and hidden.");
                field!.Length = predefinedLength; field.Usage = PanelFieldUsage.Hidden; break;
            case "SFLMSGRCD": Count(1, 1); Require(int.TryParse(args[0], out var messageRow) && messageRow is >= 1 and <= 27 && keyword.Conditions.Length == 0, "Expected unconditional message start line."); break;
            case "SFL": case "SFLDSP": case "SFLDSPCTL": case "SFLCLR": case "SFLNXTCHG": Count(0, 0); break;
            case "SFLEND": Count(0, 1); Require(args.Length == 0 || args[0] == "*MORE", "Supported option is *MORE."); break;
            case "SFLCTL": Count(1, 1); Require(ObjectName.IsValid(args[0]), "Invalid subfile record name."); break;
            case "SFLSIZ": case "SFLPAG": Count(1, 1); Require(int.TryParse(args[0], out var count) && count is >= 1 and <= 9999 && keyword.Conditions.Length == 0, "Expected unconditional size 1–9999."); break;
            case "SFLFOLD": case "SFLDROP": Count(1, 1); Require(FunctionKey(args[0]), "Expected CF01–CF24 or CA01–CA24."); break;
            case "WINDOW":
                Count(4, 5); Require(keyword.Conditions.Length == 0 && args.Take(4).All(a => int.TryParse(a, out var n) && n > 0) &&
                    (args.Length == 4 || args[4] == "*MSGLIN"), "Supported window syntax is constant row column height width [*MSGLIN]."); break;
            case "ALTPAGEDWN": case "ALTPAGEUP":
                Count(0, 1); Require(keyword.Conditions.Length == 0 && (args.Length == 0 || args[0].StartsWith("CF", StringComparison.Ordinal) && FunctionKey(args[0])), "Alternative paging requires an optional CF01–CF24 key without indicators."); break;
            case "DSPSIZ":
                Count(1, 2); Require(keyword.Conditions.Length == 0, "Display size cannot be conditioned.");
                if (args.SequenceEqual(new[] { "24", "80" }) || args.SequenceEqual(new[] { "*DS3" })) { definition.Rows = 24; definition.Columns = 80; }
                else if (args.SequenceEqual(new[] { "27", "132" }) || args.SequenceEqual(new[] { "*DS4" })) { definition.Rows = 27; definition.Columns = 132; }
                else Require(false, "Supported sizes are 24 80 (*DS3) and 27 132 (*DS4)."); break;
            case "INDARA": case "OVERLAY": case "DATE": case "TIME": Count(0, 0); break;
            case "DSPATR": Count(1, 7); Require(args.All(a => a is "HI" or "RI" or "UL" or "BL" or "CS" or "ND" or "PR"), "Unsupported display attribute."); break;
            case "COLOR": Count(1, 1); Require(args[0] is "GRN" or "WHT" or "RED" or "TRQ" or "YLW" or "PNK" or "BLU", "Unsupported color."); break;
            case "DFT": case "DFTVAL": case "TEXT": case "HLPID": Count(1, 1); break;
            case "ALIAS": Count(1, 1); Require(args[0].Length is >= 1 and <= 30 && args[0].All(c => char.IsAsciiLetterOrDigit(c) || c == '_'), "Invalid alias."); break;
            case "ERRMSG": Count(1, 2); Require(args.Length == 1 || int.TryParse(args[1], out var errorIndicator) && errorIndicator is >= 1 and <= 99, "Invalid response indicator."); break;
            case "ERRMSGID": Count(2, 3); Require(args.Length == 2 || int.TryParse(args[2], out var idIndicator) && idIndicator is >= 1 and <= 99, "Only an optional response indicator is supported after the message file."); break;
            case "CHKMSGID": Count(2, 2); break;
            case "MSGCON": Count(3, 3); Require(int.TryParse(args[0], out var size) && size is > 0 and <= 132, "Invalid message constant length."); if (field is not null) field.Length = int.Parse(args[0]); break;
            case "COMP": Count(2, 2); Require(args[0] is "EQ" or "NE" or "GT" or "GE" or "LT" or "LE", "Unsupported comparison."); break;
            case "CHECK": Count(1, 4); Require(args.All(a => a is "ME" or "MF" or "LC" or "AB"), "Supported checks are ME, MF, LC and AB."); break;
            case "DATFMT": Count(1, 1); Require(args[0] is "*ISO" or "*USA" or "*EUR" or "*JIS" or "*YMD" or "*DMY" or "*MDY", "Unsupported date format."); break;
            case "TIMFMT": Count(1, 1); Require(args[0] is "*ISO" or "*HMS", "Supported time formats are *ISO and *HMS."); break;
            case "EDTCDE": Count(1, 1); Require(args[0] is "1" or "2" or "3" or "4" or "Z", "Supported edit codes are 1, 2, 3, 4 and Z."); Require(field?.Type == PanelFieldType.Numeric && field.KeyboardShift is 'Y' or ' ', "Edit codes require numeric-only Y/blank keyboard shift."); break;
            case "EDTWRD":
                Count(1, 1); Require(field?.Type == PanelFieldType.Numeric && field.KeyboardShift is 'Y' or ' ' && keyword.Conditions.Length == 0 && field.Usage == PanelFieldUsage.Output, "Edit words currently require unconditional output-only numeric fields.");
                Require(NumericEditWord.IsSupported(args[0], field!.Length), "Unsupported edit word or incorrect number of digit positions."); break;
            case "CSRLOC": Count(2, 2); break;
            case "RTNCSRLOC": Count(2, 3); break;
            default: Require(false, "Keyword is not implemented."); break;
        }
    }
    internal static bool FunctionKey(string name) => name.Length == 4 && name[..2] is "CF" or "CA" && int.TryParse(name[2..], out var key) && key is >= 1 and <= 24;
    private static int Number(string value, int fallback, string source, int line, string token) => string.IsNullOrWhiteSpace(value) ? fallback :
        int.TryParse(value.Trim(), out var result) ? result : throw Error(source, line, token, "Expected an integer.");
    private static PanelCompileException Error(string source, int line, string token, string message, int column = 45) => new(source, line, column, token, message);
}
