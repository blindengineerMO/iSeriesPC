using System.Text;
using Ipc.Cl.Parsing;
using Ipc.Core.Objects;

namespace Ipc.Cl.Interpreter;

// Keep list/qualification syntax separate from variable contents. A value must
// never introduce another list element, parameter, quote or variable reference.
internal static class ClCommandArguments
{
    internal static string Resolve(string value, Func<string, string> read)
    {
        value = value.Trim();
        if (IsVariable(value)) return read(value);
        var rendered = Render(value, read);
        return CommandParser.Tokenize(rendered).Count == 1 ? CommandParser.Unquote(rendered) : rendered;
    }

    internal static string Render(string value, Func<string, string> read, int depth = 0)
    {
        if (value.Length > 32768 || depth > 64) throw new ClRuntimeException("Command argument exceeds its size or nesting limit.");
        var result = new StringBuilder();
        foreach (var token in CommandParser.Tokenize(value))
        {
            if (result.Length != 0) result.Append(' ');
            string expanded;
            if (token.StartsWith('\'')) expanded = token;
            else if (token.StartsWith('(') && token.EndsWith(')')) expanded = "(" + Render(token[1..^1], read, depth + 1) + ")";
            else if (IsVariable(token)) expanded = QuoteValue(read(token).TrimEnd(' '));
            else if (token.Contains('/') && token.Contains('&'))
            {
                var parts = token.Split('/');
                if (parts.Length != 2) throw new ClRuntimeException("A qualified name requires two components.");
                expanded = string.Join('/', parts.Select(part =>
                {
                    var name = (IsVariable(part) ? read(part).TrimEnd(' ') : part).ToUpperInvariant();
                    if (!ObjectName.IsValid(name) && name is not ("*LIBL" or "*CURLIB" or "*ALL" or "*ALLUSR") &&
                        !(name.EndsWith('*') && ObjectName.IsValid(name[..^1])))
                        throw new ClRuntimeException("Invalid qualified-name component.");
                    return name;
                }));
            }
            else expanded = token;
            if ((long)result.Length + expanded.Length > 32768) throw new ClRuntimeException("Expanded command argument exceeds 32768 characters.");
            result.Append(expanded);
        }
        return result.ToString();
    }

    private static bool IsVariable(string value) => value.Length is >= 2 and <= 22 && value[0] == '&' && char.IsAsciiLetter(value[1]) &&
        value.AsSpan(2).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_".AsSpan()) < 0;

    private static string QuoteValue(string value) => value.Length != 0 && value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '_' or '*' or '-' or '+' or '.' or '$' or '#' or '@')
        ? value : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
