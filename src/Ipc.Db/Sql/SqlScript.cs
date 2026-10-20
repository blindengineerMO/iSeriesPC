using System.Text;
using System.Text.RegularExpressions;
using Ipc.Core.Messages;

namespace Ipc.Db.Sql;

public sealed record SqlScriptStatement(string Text, int Line, int Column, bool Terminated);

/// <summary>Separate statements without interpreting delimiters in literals/comments.
/// Parsing stays per statement so a later syntax error does not undo *NONE work.</summary>
public static class SqlScript
{
    public static IReadOnlyList<SqlScriptStatement> Read(string source, CancellationToken token = default)
    {
        if (source.Length > 1048576 || source.Replace("\r\n", "\n", StringComparison.Ordinal).Count(c => c is '\n' or '\r') > 10000)
            throw new CpfException("SQL0904", "SQL source exceeds one MiB or 10000 lines.");
        var statements = new List<SqlScriptStatement>(); var text = new StringBuilder();
        var line = 1; var column = 1; var firstLine = 1; var firstColumn = 1;
        var block = 0; var simple = false; var quote = '\0'; var started = false;
        for (var index = 0; index < source.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var current = source[index]; var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (current == '\0') throw new CpfException("SQL0104", "Null character in SQL source.");
            if (simple)
            { if (current is '\r' or '\n') { simple = false; text.Append(current); } }
            else if (block > 0)
            {
                if (current == '/' && next == '*') { block++; index++; column++; }
                else if (current == '*' && next == '/') { block--; index++; column++; }
                else if (current is '\r' or '\n') text.Append(current);
            }
            else if (quote != '\0')
            {
                text.Append(current);
                if (current == quote) { if (next == quote) { text.Append(next); index++; column++; } else quote = '\0'; }
            }
            else if (current == '-' && next == '-') { simple = true; text.Append(' '); index++; column++; }
            else if (current == '/' && next == '*') { block = 1; text.Append(' '); index++; column++; }
            else if (current == ';') { Finish(true); }
            else
            {
                if (!started && !char.IsWhiteSpace(current)) { started = true; firstLine = line; firstColumn = column; }
                text.Append(current); if (current is '\'' or '"') quote = current;
            }
            if (current == '\n' || current == '\r' && next != '\n') { line++; column = 1; } else column++;
        }
        // Preserve an unterminated comment so the statement parser diagnoses it.
        if (block != 0) { if (!started) { started = true; firstLine = line; firstColumn = column; } text.Append(" /*"); }
        Finish(false); return statements;

        void Finish(bool terminated)
        {
            if (started)
            {
                // Routine/compound bodies have their own internal semicolons.
                // Until that grammar is implemented, reject the source before
                // any execution; body fragments must never become standalone DML.
                if (Regex.IsMatch(text.ToString(), @"^\s*(CREATE\s+(OR\s+REPLACE\s+)?(PROCEDURE|FUNCTION|TRIGGER)\b|((""(?:[^""]|"""")*""|[^\s:""]+)\s*:\s*)?(BEGIN|DECLARE|IF|WHILE|REPEAT|LOOP|FOR|CASE)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw new CpfException("SQL0104", "Procedural SQL source is not implemented; the script was not executed.");
                if (statements.Count >= 10000) throw new CpfException("SQL0904", "SQL source exceeds 10000 statements.");
                statements.Add(new(text.ToString().Trim(), firstLine, firstColumn, terminated));
            }
            text.Clear(); started = false;
        }
    }
}
