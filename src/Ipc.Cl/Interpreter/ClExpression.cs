using System.Globalization;
using System.Text;
using Ipc.Core.Text;

namespace Ipc.Cl.Interpreter;

/// <summary>Bounded CL scalar expressions, compiled once and evaluated against call-local values.</summary>
public sealed partial class ClExpression
{
    private readonly Node _root;
    private ClExpression(Node root) { _root = root; }
    public static ClExpression Compile(string text) => new(new Parser(text).Parse());
    public object Evaluate(Func<string, object> variable, int ccsid = 37)
    {
        if (!CodePage.IsSupported(ccsid)) throw new ClRuntimeException("Unsupported job CCSID.");
        return _root.Evaluate(variable, ccsid);
    }
    public static string Text(object value) => value is Ipc.Core.Work.ProgramBuffer buffer ? buffer.ToText() : value is bool flag ? flag ? "1" : "0" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    public static bool Logical(object value) => value switch {
        bool flag => flag, decimal number when number is 0 or 1 => number == 1,
        Ipc.Core.Work.ProgramBuffer buffer => Logical(buffer.ToText()),
        string text when text.Trim() is "0" or "1" => text.Trim() == "1", _ => throw new ClRuntimeException("Logical expression requires 0 or 1.") };
    public static decimal Number(object value) => value is decimal number ? number : throw new ClRuntimeException("Arithmetic requires numeric operands.");
    private abstract record Node(int Depth)
    {
        public abstract object Evaluate(Func<string, object> variable, int ccsid);
    }
    private sealed record Literal(object Value) : Node(1)
    {
        public override object Evaluate(Func<string, object> variable, int ccsid) => Value;
    }
    private sealed record Variable(string Name) : Node(1)
    {
        public override object Evaluate(Func<string, object> variable, int ccsid) => variable(Name);
    }
    private sealed record Unary(string Operator, Node Operand) : Node(Operand.Depth + 1)
    {
        public override object Evaluate(Func<string, object> variable, int ccsid) => Operator switch {
            "+" => Number(Operand.Evaluate(variable, ccsid)), "-" => -Number(Operand.Evaluate(variable, ccsid)),
            "*NOT" => !Logical(Operand.Evaluate(variable, ccsid)), _ => throw new ClRuntimeException("Invalid unary operator.") };
    }
    private sealed record Binary(string Operator, Node Left, Node Right) : Node(Math.Max(Left.Depth, Right.Depth) + 1)
    {
        public override object Evaluate(Func<string, object> variable, int ccsid)
        {
            var left = Left.Evaluate(variable, ccsid);
            if (Operator == "*AND") return Logical(left) && Logical(Right.Evaluate(variable, ccsid));
            if (Operator == "*OR") return Logical(left) || Logical(Right.Evaluate(variable, ccsid));
            var right = Right.Evaluate(variable, ccsid);
            try
            {
                return Operator switch {
                    "+" when left is decimal a && right is decimal b => a + b,
                    "+" or "*CAT" or "*TCAT" or "*BCAT" => Concatenate(left, right, Operator, ccsid), // '+' on text is the existing extension.
                    "-" => Number(left) - Number(right), "*" => Number(left) * Number(right), "/" => Number(left) / Number(right),
                    "*EQ" => Compare(left, right, ccsid) == 0, "*NE" => Compare(left, right, ccsid) != 0,
                    "*LT" => Compare(left, right, ccsid) < 0, "*LE" => Compare(left, right, ccsid) <= 0,
                    "*GT" => Compare(left, right, ccsid) > 0, "*GE" => Compare(left, right, ccsid) >= 0,
                    _ => throw new ClRuntimeException("Invalid binary operator.") };
            }
            catch (OverflowException) { throw new ClRuntimeException("Arithmetic overflow.", "MCH1210"); }
            catch (DivideByZeroException) { throw new ClRuntimeException("Division by zero.", "MCH1211"); }
        }
    }
    private static string Concatenate(string left, string right) => left.Length + right.Length <= 32768 ? left + right : throw new ClRuntimeException("Character expression exceeds 32768 characters.");
    private static object Concatenate(object left, object right, string operation, int ccsid)
    {
        if (left is not Ipc.Core.Work.ProgramBuffer && right is not Ipc.Core.Work.ProgramBuffer)
            return Concatenate(operation is "*TCAT" or "*BCAT" ? Text(left).TrimEnd(' ') + (operation == "*BCAT" ? " " : "") : Text(left), Text(right));
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        var first = left is Ipc.Core.Work.ProgramBuffer a ? a.ToArray() : encoding.GetBytes(Text(left));
        var second = right is Ipc.Core.Work.ProgramBuffer b ? b.ToArray() : encoding.GetBytes(Text(right));
        var count = first.Length; var blank = encoding.GetBytes(" ")[0];
        if (operation is "*TCAT" or "*BCAT") while (count > 0 && first[count - 1] == blank) count--;
        var separator = operation == "*BCAT" ? 1 : 0;
        if ((long)count + separator + second.Length > 32768) throw new ClRuntimeException("Binary character expression exceeds 32768 bytes.");
        var bytes = new byte[count + separator + second.Length]; first.AsSpan(0, count).CopyTo(bytes);
        if (separator != 0) bytes[count] = blank;
        second.CopyTo(bytes, count + separator); return new Ipc.Core.Work.ProgramBuffer(bytes, ccsid);
    }
    private static int Compare(object left, object right, int ccsid)
    {
        if (left is decimal a && right is decimal b) return a.CompareTo(b);
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        try
        {
            var first = left is Ipc.Core.Work.ProgramBuffer firstBuffer ? firstBuffer.ToArray() : encoding.GetBytes(Text(left));
            var second = right is Ipc.Core.Work.ProgramBuffer secondBuffer ? secondBuffer.ToArray() : encoding.GetBytes(Text(right)); var blank = encoding.GetBytes(" ")[0];
            for (var index = 0; index < Math.Max(first.Length, second.Length); index++)
            {
                var order = (index < first.Length ? first[index] : blank).CompareTo(index < second.Length ? second[index] : blank);
                if (order != 0) return order;
            }
            return 0;
        }
        catch (EncoderFallbackException) { throw new ClRuntimeException("Character comparison is not representable in the job CCSID."); }
    }

    private sealed class Parser
    {
        private readonly string _text; private int _offset; private Token _current; private int _tokens;
        internal Parser(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 32768) throw new ClRuntimeException("Empty or oversized CL expression.");
            _text = text; _current = Next();
        }
        internal Node Parse()
        {
            var result = Expression(0, 0);
            if (_current.Kind != Kind.End) throw Error("Unexpected token " + _current.Text + ".");
            return result;
        }
        private Node Expression(int minimum, int depth)
        {
            if (depth >= 64) throw Error("Expression nesting exceeds 64.");
            var token = _current; Advance(); Node left;
            if (token.Text is "+" or "-" or "*NOT") left = new Unary(token.Text, Expression(token.Text == "*NOT" ? 3 : 7, depth + 1));
            else if (token.Kind == Kind.Open)
            {
                left = Expression(0, depth + 1);
                if (_current.Kind != Kind.Close) throw Error("Missing closing parenthesis."); Advance();
            }
            else if (token.Kind == Kind.Text) left = new Literal(token.Text);
            else if (token.Kind == Kind.Hex) left = new BinaryLiteral(Convert.FromHexString(token.Text));
            else if (token.Kind == Kind.Number) left = new Literal(ExactDecimal(token.Text));
            else if (token.Kind == Kind.Variable) left = new Variable(token.Text);
            else if (token.Kind == Kind.Function) left = Function(token.Text, depth + 1);
            else if (token.Kind == Kind.Name) left = new Literal(token.Text switch { "*ON" or "*TRUE" => true, "*OFF" or "*FALSE" => false, _ => (object)token.Text });
            else throw Error("Expected an operand.");
            while (_current.Kind == Kind.Operator && Precedence(_current.Text) >= minimum)
            {
                var operation = _current.Text; var precedence = Precedence(operation); Advance();
                left = new Binary(operation, left, Expression(precedence + 1, depth + 1));
                if (left.Depth > 64) throw Error("Expression tree exceeds 64 levels.");
            }
            if (left.Depth > 64) throw Error("Expression tree exceeds 64 levels.");
            return left;
        }
        private void Advance() => _current = Next();
        private Node Function(string name, int depth)
        {
            var binary = name is "%BIN" or "%BINARY";
            if (!binary && name is not ("%SST" or "%SUBSTRING")) throw Error("Unsupported built-in function " + name + ".");
            if (_current.Kind != Kind.Open) throw Error("Built-in function requires parentheses."); Advance();
            if (_current.Kind != Kind.Variable && !(!binary && _current.Text == "*LDA")) throw Error("Built-in function requires a character variable" + (binary ? "." : " or *LDA."));
            var variable = _current.Text; Advance(); Node? start = null, length = null;
            if (_current.Kind != Kind.Close)
            {
                start = Expression(0, depth); length = Expression(0, depth);
                if (binary && (length is not Literal literal || literal.Value is not decimal count || count is not (2 or 4)))
                    throw Error("%BIN requires a constant length of 2 or 4.");
            }
            else if (!binary) throw Error("%SST requires a starting position and length.");
            if (_current.Kind != Kind.Close) throw Error("Invalid built-in argument count."); Advance();
            return new ByteFunction(variable, start, length, binary);
        }
        private Token Next()
        {
            while (_offset < _text.Length && char.IsWhiteSpace(_text[_offset])) _offset++;
            if (++_tokens > 4096) throw Error("Expression exceeds 4096 tokens.");
            if (_offset == _text.Length) return new(Kind.End, "");
            var start = _offset; var character = _text[_offset++];
            if (character is 'X' or 'x' && _offset < _text.Length && _text[_offset] == '\'')
            {
                var begin = ++_offset;
                while (_offset < _text.Length && _text[_offset] != '\'')
                {
                    if (!Uri.IsHexDigit(_text[_offset++])) throw Error("Invalid hexadecimal character constant.");
                }
                if (_offset == _text.Length || (_offset - begin) % 2 != 0) throw Error("Hexadecimal constants require complete byte pairs and a closing quote.");
                var value = _text[begin.._offset]; _offset++; return new(Kind.Hex, value);
            }
            if (character == '\'')
            {
                var value = new StringBuilder();
                while (_offset < _text.Length)
                {
                    var next = _text[_offset++];
                    if (next != '\'') { value.Append(next); continue; }
                    if (_offset < _text.Length && _text[_offset] == '\'') { value.Append('\''); _offset++; continue; }
                    return new(Kind.Text, value.ToString());
                }
                throw Error("Unclosed character constant.");
            }
            if (character == '(') return new(Kind.Open, "(");
            if (character == ')') return new(Kind.Close, ")");
            if (character == '%')
            {
                while (_offset < _text.Length && char.IsAsciiLetter(_text[_offset])) _offset++;
                return new(Kind.Function, _text[start.._offset].ToUpperInvariant());
            }
            if (character is '+' or '-' or '/' || character == '*' && (_offset == _text.Length || !char.IsAsciiLetter(_text[_offset]))) return new(Kind.Operator, character.ToString());
            if (char.IsAsciiDigit(character) || character == '.')
            {
                while (_offset < _text.Length && (char.IsAsciiDigit(_text[_offset]) || _text[_offset] == '.')) _offset++;
                return new(Kind.Number, _text[start.._offset]);
            }
            if (character is '&' or '*' || char.IsAsciiLetter(character))
            {
                while (_offset < _text.Length && (char.IsAsciiLetterOrDigit(_text[_offset]) || _text[_offset] is '_' or '$' or '#' or '@')) _offset++;
                var name = _text[start.._offset].ToUpperInvariant();
                if (character == '&')
                {
                    if (name.Length is < 2 or > 22 || !char.IsAsciiLetter(name[1])) throw Error("Invalid variable name.");
                    return new(Kind.Variable, name);
                }
                return new(Precedence(name) >= 0 || name == "*NOT" ? Kind.Operator : Kind.Name, name);
            }
            throw Error("Unsupported expression token.");
        }
        private ClRuntimeException Error(string message) => new($"Expression column {_offset + 1}: {message}");
    }
    private static int Precedence(string operation) => operation switch {
        "*OR" => 0, "*AND" => 1, "*EQ" or "*NE" or "*GT" or "*GE" or "*LT" or "*LE" => 2,
        "*CAT" or "*TCAT" or "*BCAT" => 3, "+" or "-" => 4, "*" or "/" => 5, _ => -1 };
    private enum Kind { End, Open, Close, Text, Hex, Number, Variable, Name, Function, Operator }
    private readonly record struct Token(Kind Kind, string Text);
    internal static decimal ExactDecimal(string text)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) throw new ClRuntimeException("Invalid decimal constant.");
        var parts = text.Split('.'); var whole = parts[0].TrimStart('0'); var fraction = parts.Length == 2 ? parts[1].TrimEnd('0') : "";
        var canonical = (whole.Length == 0 ? "0" : whole) + (fraction.Length == 0 ? "" : "." + fraction);
        if (canonical != value.ToString("0.############################", CultureInfo.InvariantCulture)) throw new ClRuntimeException("Decimal constant exceeds exact supported precision.");
        return value;
    }
}
