namespace Ipc.Cl.Parsing;

public sealed class CommandCall
{
    public required string Name { get; init; }

    public IReadOnlyList<string> Positional { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Keywords { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? GetOption(params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (Keywords.TryGetValue(keyword, out var value))
            {
                return value;
            }
        }

        return null;
    }

    public IReadOnlyList<string> Split(string keyword)
    {
        var value = GetOption(keyword);
        return value is null
            ? Array.Empty<string>()
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public override string ToString()
    {
        var parts = new List<string> { Name };
        parts.AddRange(Positional);
        parts.AddRange(Keywords.Select(kv => $"{kv.Key}({kv.Value})"));
        return string.Join(' ', parts);
    }
}

public static class CommandParser
{
    public static CommandCall Parse(string line)
    {
        var tokens = Tokenize(line);
        if (tokens.Count == 0)
        {
            throw new ClParseException("Command is empty.");
        }

        var name = tokens[0];
        var positional = new List<string>();
        var keywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var open = token.IndexOf('(');
            if (open > 0 && token.EndsWith(')'))
            {
                var keyword = token[..open];
                var value = token[(open + 1)..^1];
                keywords[keyword] = value;
            }
            else
            {
                positional.Add(token);
            }
        }

        return new CommandCall
        {
            Name = name,
            Positional = positional,
            Keywords = keywords,
        };
    }

    public static string Unquote(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            return text[1..^1];
        }

        return text;
    }

    public static IReadOnlyList<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var started = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (ch == '\'')
            {
                if (quoted)
                {
                    if (i + 1 < line.Length && line[i + 1] == '\'')
                    {
                        current.Append('\'');
                        i++;
                        continue;
                    }

                    quoted = false;
                    started = true;
                    continue;
                }

                quoted = true;
                started = true;
                continue;
            }

            if (!quoted && ch == '(')
            {
                var prefix = started ? current.ToString() : string.Empty;
                if (started)
                {
                    current.Clear();
                    started = false;
                }

                var depth = 1;
                var group = new System.Text.StringBuilder("(");
                i++;
                while (i < line.Length && depth > 0)
                {
                    var gch = line[i];
                    if (gch == '\'')
                    {
                        group.Append(gch);
                        if (i + 1 < line.Length && line[i + 1] == '\'')
                        {
                            group.Append('\'');
                            i++;
                        }

                        i++;
                        continue;
                    }

                    if (gch == '(')
                    {
                        depth++;
                    }
                    else if (gch == ')')
                    {
                        depth--;
                    }

                    group.Append(gch);
                    i++;
                }

                if (depth != 0)
                {
                    throw new ClParseException("Unbalanced parentheses.");
                }

                tokens.Add(prefix + group.ToString());
                started = false;
                current.Clear();
                continue;
            }

            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (started)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }

                continue;
            }

            current.Append(ch);
            started = true;
        }

        if (quoted)
        {
            throw new ClParseException("Unterminated quoted string.");
        }

        if (started)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}

public sealed class ClParseException : Exception
{
    public ClParseException(string message) : base(message)
    {
    }
}