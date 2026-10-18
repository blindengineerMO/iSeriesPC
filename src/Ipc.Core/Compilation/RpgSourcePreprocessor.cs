using System.Text.RegularExpressions;

namespace Ipc.Core.Compilation;

/// <summary>Bounded RPG directive grammar, sharing source accounting with the CL extension.</summary>
public sealed class RpgSourcePreprocessor(Func<SourceDocument, string, SourceDocument>? resolve = null)
{
    public PreprocessedSource Expand(SourceDocument source, CancellationToken cancellationToken = default)
        => new SourcePreprocessor(resolve, rpg: true).Expand(source, cancellationToken);

    public static bool IsDataBoundary(string line) => line.StartsWith("**", StringComparison.Ordinal) &&
        (line.Length == 2 || char.IsWhiteSpace(line[2]) || line.AsSpan(2).StartsWith("CTDATA", StringComparison.OrdinalIgnoreCase) ||
         line.AsSpan(2).StartsWith("ALTSEQ", StringComparison.OrdinalIgnoreCase) || line.AsSpan(2).StartsWith("FTRANS", StringComparison.OrdinalIgnoreCase));

    // Preserve physical columns and carry quoted literals across source/include lines.
    // Callers must bypass this preparation after the first program-data boundary.
    public static string PrepareLine(string line, ref bool quoted)
    {
        var text = line.TrimStart(); var startedQuoted = quoted;
        var traditional = line.Length > 5 && line[..5].All(c => c == ' ' || char.IsAsciiDigit(c)) && "HFDICOP".Contains(char.ToUpperInvariant(line[5]));
        var shifted = line.Length > 6 && line[..6].All(c => c == ' ' || char.IsAsciiDigit(c)) && "HFDICOP".Contains(char.ToUpperInvariant(line[6]));
        if (!quoted && (line.Length > 6 && line[6] == '*' && (traditional || line[..6].All(c => c == ' ' || char.IsAsciiDigit(c))) ||
            shifted && line.Length > 7 && line[7] == '*' ||
            text.StartsWith('*') && !text.StartsWith("**FREE", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(text, @"\A\*[Nn](?:\s|\z)")))
            return new string(' ', line.Length);
        var result = line.ToCharArray();
        if (!quoted && (traditional && char.ToUpperInvariant(line[5]) is 'D' or 'C' || shifted && char.ToUpperInvariant(line[6]) is 'D' or 'C'))
        {
            var end = traditional ? 80 : 81;
            if (result.Length > end) Array.Fill(result, ' ', end, result.Length - end);
        }
        if (!quoted && line.Length > 6 && line[..6].All(c => c == ' ' || char.IsAsciiDigit(c)) && line[6..].TrimStart().StartsWith('/'))
            Array.Fill(result, ' ', 0, 6);
        for (var index = 0; index < result.Length; index++)
        {
            if (result[index] == '\'')
            {
                if (quoted && index + 1 < result.Length && result[index + 1] == '\'') index++;
                else quoted = !quoted;
            }
            else if (quoted && result[index] == '\\' && index + 1 < result.Length) index++;
            else if (!quoted && result[index] == '/' && index + 1 < result.Length && result[index + 1] == '/')
            { Array.Fill(result, ' ', index, result.Length - index); break; }
        }
        if (!startedQuoted && new string(result).TrimStart().StartsWith('/') && !text.StartsWith("//", StringComparison.Ordinal)) quoted = false;
        return new string(result);
    }
}
