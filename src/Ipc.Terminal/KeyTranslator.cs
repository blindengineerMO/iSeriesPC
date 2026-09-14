using System.Text;

namespace Ipc.Terminal;

public static class KeyTranslator
{
    public static bool IsSequenceStart(char ch) => ch == '\u001b';

    public static KeyPress? Translate(string sequence, char? pendingChar = null)
    {
        if (pendingChar is { } pc && !char.IsControl(pc) && pc != '\r' && pc != '\b')
        {
            return new KeyPress(AidKey.None, CursorEdit.None, pc);
        }

        if (string.IsNullOrEmpty(sequence))
        {
            return null;
        }

        switch (sequence)
        {
            case "\r":
            case "\n":
                return new KeyPress(AidKey.Enter);
            case "\b":
            case "\u007f":
                return new KeyPress(AidKey.None, CursorEdit.Delete);
            case "\t":
                return new KeyPress(AidKey.None, CursorEdit.NextField);
            case "\u001b[Z":
                return new KeyPress(AidKey.None, CursorEdit.BackTab);
            case "\u001b[A":
                return new KeyPress(AidKey.None, CursorEdit.CursorUp);
            case "\u001b[B":
                return new KeyPress(AidKey.None, CursorEdit.CursorDown);
            case "\u001b[C":
                return new KeyPress(AidKey.None, CursorEdit.CursorRight);
            case "\u001b[D":
                return new KeyPress(AidKey.None, CursorEdit.CursorLeft);
            case "\u001b[H":
                return new KeyPress(AidKey.None, CursorEdit.CursorLeft);
            case "\u001b[F":
                return new KeyPress(AidKey.None, CursorEdit.CursorRight);
            case "\u001b[1;5C":
                return new KeyPress(AidKey.None, CursorEdit.NextField);
            case "\u001b[1;5D":
                return new KeyPress(AidKey.None, CursorEdit.PreviousField);
        }

        for (var i = 0; i < KeyCodes.FunctionKeys.Length; i++)
        {
            if (sequence.StartsWith(KeyCodes.FunctionKeys[i], StringComparison.Ordinal))
            {
                var suffix = sequence[KeyCodes.FunctionKeys[i].Length..];
                var aid = (byte)((int)AidKey.Pf1 + i);
                if (string.IsNullOrEmpty(suffix))
                {
                    return new KeyPress((AidKey)aid);
                }

                if (suffix.Length > 1 || !ParseDigit(suffix, out var digit))
                {
                    return null;
                }

                if (digit >= 1 && digit <= 5)
                {
                    return new KeyPress((AidKey)aid, i is 0 or 1 ? CursorEdit.CursorLeft : (CursorEdit?)digit);
                }
            }
        }

        return null;
    }

    public static string CollectPending(string[] fragments)
    {
        var sb = new StringBuilder();
        foreach (var fragment in fragments)
        {
            sb.Append(fragment);
        }

        var result = sb.ToString();
        return result.Length <= 12 ? result : result[..12];
    }

    private static bool ParseDigit(string text, out int digit)
    {
        if (text.Length == 1 && text[0] is >= '0' and <= '9')
        {
            digit = text[0] - '0';
            return true;
        }

        digit = 0;
        return false;
    }
}