using System.Text;

namespace Ipc.Terminal;

public sealed class TerminalParser
{
    private readonly StringBuilder _escape = new();
    private bool _inEscape;

    public IReadOnlyList<KeyPress> Feed(char ch)
    {
        var output = new List<KeyPress>();

        if (_inEscape)
        {
            _escape.Append(ch);
            if (IsCompleteEscape(_escape.ToString()))
            {
                var translated = KeyTranslator.Translate(_escape.ToString());
                if (translated is { } key)
                {
                    output.Add(key);
                }

                _inEscape = false;
                _escape.Clear();
            }

            return output;
        }

        if (ch == '\u001b')
        {
            _inEscape = true;
            _escape.Append(ch);
            return output;
        }

        var plain = KeyTranslator.Translate(ch.ToString(), ch);
        if (plain is { } plainKey)
        {
            output.Add(plainKey);
        }

        return output;
    }

    public void Reset()
    {
        _inEscape = false;
        _escape.Clear();
    }

    private static bool IsCompleteEscape(string buffer)
    {
        foreach (var sequence in KnownSequences)
        {
            if (string.Equals(sequence, buffer, StringComparison.Ordinal))
            {
                return true;
            }

            if (sequence.StartsWith(buffer, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return buffer.Length > 3;
    }

    private static readonly string[] KnownSequences =
    {
        "\u001b[A", "\u001b[B", "\u001b[C", "\u001b[D", "\u001b[H", "\u001b[F",
        "\u001b[Z", "\u001b[1;5C", "\u001b[1;5D",
        "\r", "\n", "\b", "\u007f", "\t",
        "\u001bOP", "\u001bOQ", "\u001bOR", "\u001bOS",
        "\u001b[15~", "\u001b[17~", "\u001b[18~", "\u001b[19~",
        "\u001b[20~", "\u001b[21~", "\u001b[23~", "\u001b[24~",
    };
}