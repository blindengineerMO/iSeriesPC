namespace Ipc.Db.Dds;

internal static class DdsStatements
{
    internal static IReadOnlyList<(string Text, int Line)> Read(string[] lines, Func<int, string, DdsCompileException> error)
    {
        var result = new List<(string, int)>();
        string? pending = null; var first = 0; char continuation = '\0';
        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            if (string.IsNullOrWhiteSpace(raw)) continue;
            raw = raw.PadRight(44);
            if (raw[5] == 'A' && raw[6] == '*') continue;
            if (raw[5] != 'A') throw error(index + 1, "DDS requires A in column 6.");
            if (raw[6..].All(char.IsWhiteSpace)) continue;
            var blank = raw[6..44].All(c => c == ' ');
            if (continuation != '\0' && !blank) throw error(index + 1, "Continued DDS keywords require blank specification columns.");
            var keywords = raw[44..].TrimEnd();
            var next = keywords.Length > 0 && keywords[^1] is '+' or '-' ? keywords[^1] : '\0';
            if (next != '\0') keywords = keywords[..^1];
            if (pending is not null && blank)
                pending += continuation == '+' ? keywords.TrimStart() : continuation == '-' ? keywords : " " + keywords;
            else
            {
                if (pending is not null) result.Add((pending, first));
                pending = raw[..44] + keywords; first = index + 1;
            }
            if (pending.Length > 5000) throw error(first, "DDS statement exceeds 5000 characters.");
            continuation = next;
        }
        if (continuation != '\0') throw error(first, "Unfinished DDS keyword continuation.");
        if (pending is not null) result.Add((pending, first));
        return result;
    }
}
