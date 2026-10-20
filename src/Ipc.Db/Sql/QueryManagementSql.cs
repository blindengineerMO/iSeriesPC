using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Text;

namespace Ipc.Db.Sql;

/// <summary>Query-management source records and non-recursive, case-sensitive SQL substitution.</summary>
public static class QueryManagementSql
{
    public static string SourceRecords(string source, int ccsid)
    {
        var encoding = EncodingFor(ccsid);
        if (source.Length > 1048576) throw Limit();
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines[^1].Length == 0) lines = lines[..^1];
        if (lines.Length > 10000) throw Limit();
        var result = new StringBuilder();
        foreach (var line in lines)
        {
            var bytes = Count(encoding, line);
            if (bytes > 79 || line.Contains('\0')) throw new CpfException("QWM2701", "Query source requires records of at most 79 encoded bytes without null characters.");
            result.Append(line).Append(' ', 79 - bytes).Append('\n');
        }
        if (result.Length > 1048576) throw Limit();
        return result.ToString();
    }

    public static IReadOnlyList<string> Variables(string source) => Translate(source, null).Variables;

    public static string Substitute(string source, IReadOnlyDictionary<string, string> values, int ccsid)
    {
        foreach (var (name, value) in values)
        {
            RequireName(name);
            if (name.StartsWith("DSQ", StringComparison.OrdinalIgnoreCase) || value is null || value.Length > 55 || value.Contains('\0'))
                throw new CpfException("QWM2701", "Invalid query variable value or reserved variable name.");
        }
        var translated = Translate(source, new Dictionary<string, string>(values, StringComparer.Ordinal)).Sql;
        if (Count(EncodingFor(ccsid), translated) is < 1 or > 32767) throw Limit();
        return translated;
    }

    public static void RequireName(string name)
    {
        if (name.Length is < 1 or > 30 || !char.IsAsciiLetter(name[0]) || name.Any(c => !NameCharacter(c)))
            throw new CpfException("QWM2701", "Query variable names require a letter followed by letters, digits or underscores, up to 30 characters.");
    }

    private static bool NameCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    private static (string Sql, IReadOnlyList<string> Variables) Translate(string source, IReadOnlyDictionary<string, string>? values)
    {
        if (source.Length > 1048576) throw Limit();
        source = JoinRecords(source);
        // Record boundaries are not SQL characters. Padding supplied by SourceRecords
        // preserves whitespace, including padding in a continued quoted value.
        var sql = new StringBuilder(); var variables = new List<string>(); var quote = '\0'; var block = 0; var comment = false;
        for (var index = 0; index < source.Length; index++)
        {
            var c = source[index]; var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (c is '\r' or '\n') { comment = false; continue; }
            if (comment) continue;
            if (block > 0)
            {
                if (c == '/' && next == '*') { block++; index++; }
                else if (c == '*' && next == '/') { block--; index++; }
                continue;
            }
            if (quote != '\0')
            {
                sql.Append(c);
                if (c == quote) { if (next == quote) { sql.Append(next); index++; } else quote = '\0'; }
                continue;
            }
            if (c == '-' && next == '-') { comment = true; index++; Space(); continue; }
            if (c == '/' && next == '*') { block = 1; index++; Space(); continue; }
            if (c is '\'' or '"') { quote = c; sql.Append(c); continue; }
            if (c == '&')
            {
                var start = ++index; while (index < source.Length && NameCharacter(source[index])) index++;
                var name = source[start..index]; index--; RequireName(name);
                if (!variables.Contains(name, StringComparer.Ordinal)) variables.Add(name);
                if (values is not null)
                {
                    // Do not scan the replacement again: it is SQL text, not a second template.
                    if (!values.TryGetValue(name, out var value)) throw new CpfException("QWM2701", "No value was supplied for query variable " + name + ".");
                    sql.Append(value);
                }
                continue;
            }
            if (c == '\0') throw new CpfException("QWM2701", "Null character in query source.");
            if (char.IsWhiteSpace(c)) Space(); else sql.Append(c);
        }
        if (quote != '\0' || block != 0) throw new CpfException("SQL0104", "Unclosed query literal, delimited name or comment.");
        return (sql.ToString().Trim(), variables.AsReadOnly());
        void Space() { if (sql.Length > 0 && sql[^1] != ' ') sql.Append(' '); }
    }
    private static Encoding EncodingFor(int ccsid)
    { var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback; return encoding; }
    private static int Count(Encoding encoding, string text)
    { try { return encoding.GetByteCount(text); } catch (EncoderFallbackException) { throw new CpfException("SQL0332", "Query text cannot be represented in its CCSID."); } }
    private static CpfException Limit() => new("SQL0904", "Query source exceeds one MiB/10000 records or substituted SQL exceeds 32767 encoded bytes.");

    private static string JoinRecords(string source)
    {
        var result = new StringBuilder(); var quote = '\0'; var comment = false; var block = 0;
        for (var index = 0; index < source.Length; index++)
        {
            var c = source[index]; var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (c is '\r' or '\n') { if (comment) result.Append(' '); comment = false; continue; }
            if (comment) continue;
            if (quote == '\0' && block == 0 && c == '-' && next == '-') { comment = true; index++; continue; }
            result.Append(c);
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c == '/' && next == '*') { block++; result.Append(next); index++; }
            else if (block > 0 && c == '*' && next == '/') { block--; result.Append(next); index++; }
            else if (block == 0 && c is '\'' or '"') quote = c;
        }
        return result.ToString();
    }
}
