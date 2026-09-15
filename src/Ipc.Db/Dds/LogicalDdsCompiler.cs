using System.Text;
using System.Text.Json;
using Ipc.Cl.Parsing;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;

namespace Ipc.Db.Dds;

/// <summary>Native-column simple LF DDS. Unsupported constructs fail at their source line.</summary>
public sealed class LogicalDdsCompiler(Func<string, (QualifiedName Name, FileDefinition Definition)> resolve)
{
    public FileDefinition Compile(string name, string source, string sourceName = "*SOURCE")
    {
        DdsCompileException Error(int line, string text) => new($"IPC0006: {sourceName}:{line}:1: {text}");
        if (!ObjectName.IsValid(name) || Encoding.UTF8.GetByteCount(source) > 1048576) throw Error(1, "Invalid LF identity or source exceeds 1 MiB.");
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > 10000) throw Error(1, "LF source exceeds 10000 lines.");
        RecordFormat? record = null, physical = null; QualifiedName? target = null;
        var fields = new List<string>(); var keys = new List<(string Name, bool Descending)>();
        var rules = new List<LogicalRule>(); bool? defaultSelect = null; var keyOrSelection = false; var unique = false; var excludeNullKeys = false; var fileCcsid = 37;
        foreach (var statement in DdsStatements.Read(lines, Error))
        {
            var raw = statement.Text; var line = statement.Line;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            raw = raw.PadRight(44);
            if (raw[5] == 'A' && raw[6] == '*') continue;
            if (raw[5] != 'A') throw Error(line, "LF DDS requires A in column 6.");
            if (raw[6..16].Any(c => c != ' ') || raw[17] != ' ') throw Error(line, "Unsupported LF conditioning or reserved columns.");
            var level = raw[16]; var field = raw[18..28].Trim().ToUpperInvariant();
            CommandCall keywords;
            try { keywords = CommandParser.Parse("DDS " + raw[44..]); }
            catch (ClParseException error) { throw Error(line, error.Message); }
            void Check(string allowed, string flags = "")
            {
                var names = allowed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var key in keywords.Keywords.Keys) if (!names.Contains(key.ToUpperInvariant())) throw Error(line, "Unsupported LF keyword " + key + ".");
                foreach (var flag in keywords.Positional) if (!flags.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(flag.ToUpperInvariant())) throw Error(line, "Unsupported LF flag " + flag + ".");
            }
            if (record is null && level == ' ' && field.Length == 0)
            {
                Check("UNIQUE", "UNIQUE");
                if (unique || keywords.Positional.Count + keywords.Keywords.Count != 1) throw Error(line, "Expected one UNIQUE file keyword.");
                var nulls = keywords.GetOption("UNIQUE")?.ToUpperInvariant() ?? "*INCNULL";
                if (nulls is not ("*INCNULL" or "*EXCNULL")) throw Error(line, "UNIQUE requires *INCNULL or *EXCNULL.");
                excludeNullKeys = nulls == "*EXCNULL";
                unique = true; continue;
            }
            if (level == 'R')
            {
                Check("PFILE TEXT");
                if (record is not null) throw Error(line, "Multiple LF formats are not implemented yet.");
                if (!ObjectName.IsValid(field) || keywords.Split("PFILE").Count != 1) throw Error(line, "A simple LF record requires one PFILE.");
                var resolved = resolve(CommandParser.Unquote(keywords.Split("PFILE")[0]).ToUpperInvariant());
                if (resolved.Definition.Attribute != FileAttribute.Physical || resolved.Definition.Formats.Count != 1) throw Error(line, "PFILE must resolve to a physical file with one format.");
                physical = resolved.Definition.PrimaryFormat; target = resolved.Name; fileCcsid = resolved.Definition.Ccsid;
                record = new() { Name = field, Text = keywords.GetOption("TEXT") is { } text ? CommandParser.Unquote(text) : null };
                continue;
            }
            if (record is null || physical is null) throw Error(line, "LF field precedes its record.");
            if (raw[28..44].Trim() is not ("" or "R")) throw Error(line, "Simple LF fields inherit their physical definition; field layout overrides are unsupported.");
            if (level == 'K')
            {
                Check("", "DESCEND"); keyOrSelection = true;
                if (physical.Find(field) is null || keys.Any(k => k.Name == field)) throw Error(line, "Invalid or duplicate LF key.");
                keys.Add((field, keywords.Positional.Any(p => p.Equals("DESCEND", StringComparison.OrdinalIgnoreCase)))); continue;
            }
            if (level is 'S' or 'O' || level == ' ' && keywords.Keywords.Keys.Any(k => k.ToUpperInvariant() is "COMP" or "RANGE" or "VALUES"))
            {
                Check("COMP RANGE VALUES", "ALL"); keyOrSelection = true;
                if (defaultSelect is not null) throw Error(line, "ALL must follow every selection rule.");
                if (keywords.Positional.Count != 0)
                {
                    if (level is not ('S' or 'O') || field.Length != 0 || keywords.Keywords.Count != 0 || keywords.Positional.Count != 1) throw Error(line, "ALL requires a final S/O line without a field.");
                    defaultSelect = level == 'S'; continue;
                }
                if (physical.Find(field) is null || keywords.Keywords.Count != 1) throw Error(line, "Selection requires a physical field and one predicate.");
                var clause = keywords.Keywords.Single(); var values = CommandParser.Tokenize(clause.Value).ToArray();
                var operation = clause.Key.ToUpperInvariant();
                if (operation == "COMP")
                {
                    if (values.Length != 2 || values[0].TrimStart('*').ToUpperInvariant() is not ("EQ" or "NE" or "GT" or "GE" or "LT" or "LE")) throw Error(line, "Invalid COMP operation.");
                    operation = values[0].TrimStart('*').ToUpperInvariant(); values = values[1..];
                }
                if (values.Length is < 1 or > 256 || operation == "RANGE" && values.Length != 2) throw Error(line, "Invalid selection value count.");
                var condition = new LogicalCondition(field, operation, values.Select(CommandParser.Unquote).ToArray());
                if (level == ' ')
                {
                    if (rules.Count == 0) throw Error(line, "AND continuation requires a preceding select/omit rule.");
                    rules[^1] = rules[^1] with { Conditions = rules[^1].Conditions.Append(condition).ToArray() };
                }
                else rules.Add(new(level == 'S', new[] { condition }));
                continue;
            }
            if (level != ' ') throw Error(line, "Unsupported LF specification level.");
            Check("");
            if (field.Length == 0) continue;
            if (keyOrSelection || physical.Find(field) is null || fields.Contains(field)) throw Error(line, "Invalid, duplicate, or misplaced LF field.");
            fields.Add(field);
        }
        if (record is null || physical is null || target is null) throw Error(1, "LF requires a record and PFILE.");
        if (unique && keys.Count == 0) throw Error(1, "UNIQUE requires key fields.");
        if (fields.Count == 0) fields.AddRange(physical.Fields.Select(f => f.Name));
        if (fields.Count > 1024 || rules.Count > 256 || rules.Any(r => r.Conditions.Count > 256)) throw Error(1, "LF definition exceeds its field or selection limit.");
        foreach (var field in fields)
        {
            var copy = JsonSerializer.Deserialize<FieldSpec>(JsonSerializer.Serialize(physical.Find(field)!))!;
            copy.Sequence = 0; copy.Descending = false; record.Fields.Add(copy);
        }
        for (var index = 0; index < keys.Count; index++)
        {
            var key = keys[index]; var field = record.Find(key.Name) ?? throw Error(1, "LF key must be present in its record format.");
            field.Sequence = index + 1; field.Descending = key.Descending;
        }
        record.AssignPositions();
        return new() { Name = name, Attribute = FileAttribute.Logical, Ccsid = fileCcsid, Formats = new() { record }, Text = record.Text,
            Logical = new() { SourceLibrary = target.Value.Library, SourceFile = target.Value.Name.Value, SourceFormat = physical.Name,
                Rules = rules, Unique = unique, ExcludeNullKeys = excludeNullKeys, DefaultSelect = defaultSelect ?? (rules.Count == 0 || !rules[^1].Select) } };
    }

}
