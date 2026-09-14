using System.Globalization;
using Ipc.Db.Definitions;

namespace Ipc.Db.Dds;

public sealed class DdsCompileException : Exception
{
    public DdsCompileException(string message) : base(message)
    {
    }
}

public sealed class DdsCompiler
{
    public FileDefinition CompilePhysical(string name, string source, int ccsid = 37)
    {
        var definition = Compile(name, source, FileAttribute.Physical, ccsid);
        if (definition.Formats.Count == 0)
        {
            throw new DdsCompileException("No record formats were declared for file " + name + ".");
        }

        return definition;
    }

    public FileDefinition Compile(string name, string source, FileAttribute attribute, int ccsid = 37)
    {
        var definition = new FileDefinition { Name = name, Attribute = attribute, Ccsid = ccsid };
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        RecordFormat? record = null;
        var keySequence = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = PadLine(lines[index]);
            if (raw.Length < 8)
            {
                continue;
            }

            if (raw[6] != 'A')
            {
                continue;
            }

            var isKey = raw[7] == 'K';
            var isRecord = raw.Length >= 17 && raw[16] == 'R' && !isKey;
            var nameSlot = raw.Length >= 28 ? raw[18..28].Trim() : string.Empty;

            try
            {
                if (isRecord)
                {
                    if (nameSlot.Length == 0)
                    {
                        throw new DdsCompileException($"Line {index + 1}: record format name is missing.");
                    }

                    record = new RecordFormat { Name = nameSlot.ToUpperInvariant(), Text = FindText(Keywords(raw)) };
                    definition.Formats.Add(record);
                    continue;
                }

                if (record is null)
                {
                    throw new DdsCompileException($"Line {index + 1}: field declared before a record format.");
                }

                var keywords = Keywords(raw);

                if (isKey)
                {
                    if (nameSlot.Length == 0)
                    {
                        throw new DdsCompileException($"Line {index + 1}: key field name is missing.");
                    }

                    var keyField = record.Find(nameSlot)
                        ?? throw new DdsCompileException($"Line {index + 1}: key field {nameSlot} is not defined.");
                    keySequence++;
                    keyField.Sequence = keySequence;
                    keyField.Descending = Descending(keywords);
                    continue;
                }

                if (nameSlot.Length == 0)
                {
                    ApplyFieldKeywords(record.Fields[^1], keywords);
                    continue;
                }

                var field = ParseField(nameSlot, raw, keywords);
                ApplyFieldKeywords(field, keywords);
                if (record.Find(field.Name) is not null)
                {
                    throw new DdsCompileException($"Line {index + 1}: field {field.Name} is declared twice.");
                }

                record.Fields.Add(field);
            }
            catch (DdsCompileException)
            {
                throw;
            }
        }

        foreach (var format in definition.Formats)
        {
            if (format.Fields.Count == 0)
            {
                throw new DdsCompileException($"Record format {format.Name} has no fields.");
            }

            format.AssignPositions();
        }

        return definition;
    }

    private static FieldSpec ParseField(string name, string raw, IReadOnlyList<KeywordToken> keywords)
    {
        var typeChar = raw.Length >= 35 ? raw[34] : ' ';
        var type = ParseType(typeChar);
        var length = ParseInt(raw, 35, 39, "length");
        var decimals = ParseInt(raw, 39, 41, "decimal positions");

        if (length <= 0)
        {
            throw new DdsCompileException($"Field {name} has an invalid length.");
        }

        if (type is FieldType.Packed or FieldType.Zoned && (length - decimals) < 1)
        {
            throw new DdsCompileException($"Field {name} length is too small for its decimal positions.");
        }

        return new FieldSpec
        {
            Name = name.ToUpperInvariant(),
            Type = type,
            Length = length,
            Decimals = decimals,
            Ccsid = Ccsid(keywords) ?? 37,
            Text = FindText(keywords),
        };
    }

    private static void ApplyFieldKeywords(FieldSpec field, IReadOnlyList<KeywordToken> keywords)
    {
        if (keywords.Any(k => string.Equals(k.Name, "VARLEN", StringComparison.OrdinalIgnoreCase)))
        {
            field.VariableLength = true;
        }

        if (keywords.Any(k => string.Equals(k.Name, "ALWNULL", StringComparison.OrdinalIgnoreCase)))
        {
            field.NullCapable = true;
        }
    }

    private static FieldType ParseType(char c) => c switch
    {
        'A' => FieldType.Alpha,
        'S' => FieldType.Zoned,
        'P' => FieldType.Packed,
        'B' => FieldType.Binary,
        'F' => FieldType.Float,
        'L' => FieldType.Logic,
        'D' => FieldType.Date,
        'T' => FieldType.Time,
        'Z' => FieldType.Timestamp,
        _ => throw new DdsCompileException($"Unsupported DDS data type '{c}'."),
    };

    private static int ParseInt(string raw, int start, int end, string what)
    {
        if (raw.Length < end)
        {
            throw new DdsCompileException($"Missing {what}.");
        }

        var text = raw[start..end].Trim(new[] { ' ' });
        if (text.Length == 0)
        {
            return 0;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new DdsCompileException($"Invalid {what} '{text}'.");
        }

        return value;
    }

    private static bool Descending(IReadOnlyList<KeywordToken> keywords) =>
        keywords.Any(k => string.Equals(k.Name, "DESC", StringComparison.OrdinalIgnoreCase));

    private static int? Ccsid(IReadOnlyList<KeywordToken> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (string.Equals(keyword.Name, "CCSID", StringComparison.OrdinalIgnoreCase) &&
                keyword.Value is { Length: > 0 } value &&
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ccsid))
            {
                return ccsid;
            }
        }

        return null;
    }

    private static string? FindText(IReadOnlyList<KeywordToken> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (string.Equals(keyword.Name, "TEXT", StringComparison.OrdinalIgnoreCase) &&
                keyword.Value is { Length: > 0 } value)
            {
                if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
                {
                    return value[1..^1];
                }

                return value;
            }
        }

        return null;
    }

    private static string PadLine(string line)
    {
        if (line.Length >= 45)
        {
            return line;
        }

        return line.PadRight(45);
    }

    private static IReadOnlyList<KeywordToken> Keywords(string raw)
    {
        var tokens = new List<KeywordToken>();
        if (raw.Length < 45)
        {
            return tokens;
        }

        var text = raw[44..];
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= text.Length)
            {
                break;
            }

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] != '(')
            {
                index++;
            }

            var name = text[start..index];
            string? value = null;
            if (index < text.Length && text[index] == '(')
            {
                var groupStart = ++index;
                var depth = 1;
                while (index < text.Length && depth > 0)
                {
                    if (text[index] == '(')
                    {
                        depth++;
                    }
                    else if (text[index] == ')')
                    {
                        depth--;
                    }
                    else if (text[index] == '\'')
                    {
                        var quoteEnd = text.IndexOf('\'', index + 1);
                        if (quoteEnd < 0)
                        {
                            throw new DdsCompileException("Unterminated quoted keyword value.");
                        }

                        index = quoteEnd + 1;
                        continue;
                    }

                    index++;
                }

                value = text[groupStart..(index - 1)].Trim();
            }

            tokens.Add(new KeywordToken(name, value));
        }

        return tokens;
    }
}

public sealed class KeywordToken
{
    public KeywordToken(string name, string? value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string? Value { get; }
}