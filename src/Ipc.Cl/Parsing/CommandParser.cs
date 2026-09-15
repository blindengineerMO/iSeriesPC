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
            : CommandParser.Tokenize(value);
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
            if (open > 0 && token.EndsWith(')') && !token.StartsWith('%'))
            {
                var keyword = token[..open];
                var value = token[(open + 1)..^1];
                if (!keywords.TryAdd(keyword, value))
                    throw new ClParseException($"Parameter {keyword} is repeated; use a parenthesized value list.");
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
            return text[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return text;
    }

    public static IReadOnlyList<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var depth = 0;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '\'')
            {
                current.Append(ch);
                if (quoted && i + 1 < line.Length && line[i + 1] == '\'')
                {
                    current.Append(line[++i]);
                    continue;
                }
                quoted = !quoted;
                continue;
            }
            if (!quoted)
            {
                if (ch == '(') depth++;
                if (ch == ')' && --depth < 0)
                    throw new ClParseException("Unexpected closing parenthesis.");
                if (char.IsWhiteSpace(ch) && depth == 0)
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }
            }
            current.Append(ch);
        }
        if (quoted) throw new ClParseException("Unterminated quoted string.");
        if (depth != 0) throw new ClParseException("Unbalanced parentheses.");
        if (current.Length > 0) tokens.Add(current.ToString());

        return tokens;
    }
}

public sealed class ClParseException : Exception
{
    public ClParseException(string message) : base(message)
    {
    }
}
