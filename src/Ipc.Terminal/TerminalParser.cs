using System.Text;

namespace Ipc.Terminal;

/// <summary>Bounded streaming VT parser. Unknown escape strings never become command text.</summary>
public sealed class TerminalParser
{
    private enum State { Plain, Escape, Csi, Ss3, String, StringEscape }
    private State _state;
    private readonly StringBuilder _escape = new(64);
    private bool _overflow;
    private bool _paste;
    public bool IsPasting => _paste;
    public bool HasPendingEscape => _state != State.Plain;
    public IReadOnlyList<KeyPress> Feed(char ch)
    {
        var output = new List<KeyPress>(1);
        if (_state is State.String or State.StringEscape)
        {
            if (ch == '\a' || _state == State.StringEscape && ch == '\\') EndEscape();
            else _state = ch == '\u001b' ? State.StringEscape : State.String;
            return output;
        }
        if (ch == '\u001b') { EndEscape(); _state = State.Escape; _escape.Append(ch); return output; }
        if (_state == State.Plain)
        {
            var key = _paste && ch is '\r' or '\n' or '\t' ? new KeyPress(AidKey.None, Character: ' ') : KeyTranslator.Translate(ch.ToString(), ch);
            if (key is { } plain && (!_paste || plain.Aid == AidKey.None && plain.Character != '\0')) output.Add(plain);
            return output;
        }
        if (_escape.Length < 64) _escape.Append(ch); else _overflow = true;
        if (_state == State.Escape)
        {
            _state = ch switch { '[' => State.Csi, 'O' => State.Ss3, ']' or 'P' or '^' or '_' => State.String, _ => State.Plain };
            if (_state == State.Plain) EndEscape();
            return output;
        }
        var complete = ch is >= '@' and <= '~';
        if (complete)
        {
            var sequence = _escape.ToString();
            if (!_overflow && sequence == "\u001b[200~") _paste = true;
            else if (!_overflow && sequence == "\u001b[201~") _paste = false;
            else if (!_overflow && !_paste && KeyTranslator.Translate(sequence) is { } key) output.Add(key);
            EndEscape();
        }
        else if (ch is < ' ' or > '~') EndEscape();
        return output;
    }
    /// <summary>Called after an input idle timeout. Lone Escape is Attention; incomplete control strings remain bounded until their terminator.</summary>
    public IReadOnlyList<KeyPress> FlushPending()
    {
        var attention = _state == State.Escape && !_paste;
        if (_state == State.Escape) EndEscape(); return attention ? new[] { new KeyPress(AidKey.Pa1) } : Array.Empty<KeyPress>();
    }
    private void EndEscape() { _state = State.Plain; _escape.Clear(); _overflow = false; }
    public void Reset() { EndEscape(); _paste = false; }
}
