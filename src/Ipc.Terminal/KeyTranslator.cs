using System.Text;

namespace Ipc.Terminal;

public static class KeyTranslator
{
    private static readonly IReadOnlyDictionary<string, KeyPress> Sequences = BuildSequences();
    public static bool IsSequenceStart(char ch) => ch == '\u001b';
    public static KeyPress? Translate(string sequence, char? pendingChar = null)
    {
        if (pendingChar is { } c && TerminalGlyph.IsSingleCell(c)) return new(AidKey.None, CursorEdit.None, c);
        return Sequences.TryGetValue(sequence, out var key) ? key : null;
    }
    private static IReadOnlyDictionary<string, KeyPress> BuildSequences()
    {
        var result = new Dictionary<string, KeyPress>(StringComparer.Ordinal);
        void Aid(string sequence, AidKey aid) => result.Add(sequence, new(aid));
        void Edit(string sequence, CursorEdit edit) => result.Add(sequence, new(AidKey.None, edit));
        Aid("\r", AidKey.Enter); Aid("\n", AidKey.Enter);
        Aid("\u0001", AidKey.Pa1); Aid("\u0002", AidKey.Pa2); Aid("\u0006", AidKey.Pa3);
        Aid("\u0007", AidKey.SysReq); Aid("\u000c", AidKey.Clear); Aid("\u0010", AidKey.Print);
        Edit("\b", CursorEdit.FieldBackspace); Edit("\u007f", CursorEdit.FieldBackspace);
        Edit("\t", CursorEdit.NextField); Edit("\u001b[Z", CursorEdit.BackTab);
        foreach (var prefix in new[] { "\u001b[", "\u001bO" })
        {
            Edit(prefix + "A", CursorEdit.CursorUp); Edit(prefix + "B", CursorEdit.CursorDown);
            Edit(prefix + "C", CursorEdit.CursorRight); Edit(prefix + "D", CursorEdit.CursorLeft);
            Edit(prefix + "H", CursorEdit.Home); Edit(prefix + "F", CursorEdit.End);
        }
        Edit("\u001b[1~", CursorEdit.Home); Edit("\u001b[4~", CursorEdit.End);
        Edit("\u001b[7~", CursorEdit.Home); Edit("\u001b[8~", CursorEdit.End);
        Edit("\u001b[2~", CursorEdit.Insert); Edit("\u001b[3~", CursorEdit.Delete);
        Edit("\u001b[1;5C", CursorEdit.NextField); Edit("\u001b[1;5D", CursorEdit.PreviousField);
        Edit("\u000b", CursorEdit.FieldEraseToEnd); Edit("\u0005", CursorEdit.FieldExit);
        Aid("\u001b[5~", AidKey.RollDown); Aid("\u001b[6~", AidKey.RollUp);
        for (var i = 0; i < KeyCodes.FunctionKeys.Length; i++)
        {
            Aid(KeyCodes.FunctionKeys[i], (AidKey)((int)AidKey.Pf1 + i));
            var shifted = i < 4 ? "\u001b[1;2" + "PQRS"[i] : KeyCodes.FunctionKeys[i][..^1] + ";2~";
            Aid(shifted, (AidKey)((int)AidKey.Pf13 + i));
            if (i < 4) Aid("\u001b[1;1" + "PQRS"[i], (AidKey)((int)AidKey.Pf1 + i));
        }
        var legacy = new[] { 25, 26, 28, 29, 31, 32, 33, 34 };
        for (var i = 0; i < legacy.Length; i++) Aid("\u001b[" + legacy[i] + "~", (AidKey)((int)AidKey.Pf13 + i));
        return result;
    }
    public static string CollectPending(string[] fragments)
    {
        var result = new StringBuilder(12);
        foreach (var fragment in fragments)
        {
            result.Append(fragment.AsSpan(0, Math.Min(fragment.Length, 12 - result.Length)));
            if (result.Length == 12) break;
        }
        return result.ToString();
    }
}
