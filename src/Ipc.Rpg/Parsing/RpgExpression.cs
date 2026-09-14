using System.Globalization;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public abstract class RpgExpr
{
    public abstract object? Eval(RpgRuntimeContext ctx);
}

public sealed class RpgLiteral : RpgExpr
{
    public RpgLiteral(object? value) => Value = value;

    public object? Value { get; }

    public override object? Eval(RpgRuntimeContext ctx) => Value;
}

public sealed class RpgFieldRef : RpgExpr
{
    public RpgFieldRef(string name) => Name = name;

    public string Name { get; }

    public override object? Eval(RpgRuntimeContext ctx) => ctx.ReadValue(Name);
}

public sealed class RpgIndicatorRef : RpgExpr
{
    public RpgIndicatorRef(int indicator) => Indicator = indicator;

    public int Indicator { get; }

    public override object? Eval(RpgRuntimeContext ctx) => ctx.Indicators[Indicator];
}

public sealed class RpgSpecialValue : RpgExpr
{
    public RpgSpecialValue(string name) => Name = name;

    public string Name { get; }

    public override object? Eval(RpgRuntimeContext ctx) => ctx.SpecialValue(Name);
}

public sealed class RpgUnary : RpgExpr
{
    public RpgUnary(string op, RpgExpr operand)
    {
        Op = op;
        Operand = operand;
    }

    public string Op { get; }

    public RpgExpr Operand { get; }

    public override object? Eval(RpgRuntimeContext ctx)
    {
        var value = Operand.Eval(ctx);
        return Op switch
        {
            "-" => RpgValues.Negate(value),
            "+" => RpgValues.Positive(value),
            "NOT" => !RpgValues.ToBool(value),
            _ => throw new RpgRuntimeException($"Unsupported unary operator '{Op}'."),
        };
    }
}

public sealed class RpgBinary : RpgExpr
{
    public RpgBinary(RpgExpr lhs, string op, RpgExpr rhs)
    {
        Lhs = lhs;
        Op = op;
        Rhs = rhs;
    }

    public RpgExpr Lhs { get; }

    public string Op { get; }

    public RpgExpr Rhs { get; }

    public override object? Eval(RpgRuntimeContext ctx)
    {
        var left = Lhs.Eval(ctx);
        var right = Rhs.Eval(ctx);

        switch (Op)
        {
            case "+":
                return RpgValues.Add(left, right);
            case "-":
                return RpgValues.Subtract(left, right);
            case "*":
                return RpgValues.Multiply(left, right);
            case "/":
                return RpgValues.Divide(left, right);
            case "AND":
                return RpgValues.ToBool(left) && RpgValues.ToBool(right);
            case "OR":
                return RpgValues.ToBool(left) || RpgValues.ToBool(right);
            case "=" or "==" or "*EQ" or "EQ":
                return RpgValues.Compare(left, right) == 0;
            case "<>" or "!=" or "*NE" or "NE":
                return RpgValues.Compare(left, right) != 0;
            case "<" or "*LT" or "LT":
                return RpgValues.Compare(left, right) < 0;
            case "<=" or "*LE" or "LE":
                return RpgValues.Compare(left, right) <= 0;
            case ">" or "*GT" or "GT":
                return RpgValues.Compare(left, right) > 0;
            case ">=" or "*GE" or "GE":
                return RpgValues.Compare(left, right) >= 0;
            default:
                throw new RpgRuntimeException($"Unsupported operator '{Op}'.");
        }
    }
}

public sealed class RpgBuiltin : RpgExpr
{
    public RpgBuiltin(string name, IReadOnlyList<RpgExpr> arguments)
    {
        Name = name;
        Arguments = arguments;
    }

    public string Name { get; }

    public IReadOnlyList<RpgExpr> Arguments { get; }

    public override object? Eval(RpgRuntimeContext ctx) => ctx.EvalBuiltin(Name, Arguments);
}

public sealed class RpgIndexedRef : RpgExpr
{
    public RpgIndexedRef(string name, long index) => (Name, Index) = (name, index);

    public string Name { get; }

    public long Index { get; }

    public override object? Eval(RpgRuntimeContext ctx) => ctx.ReadArrayValue(Name, Index);
}

public sealed class RpgDynamicIndexRef : RpgExpr
{
    public RpgDynamicIndexRef(string name, RpgExpr index) => (Name, Index) = (name, index);

    public string Name { get; }

    public RpgExpr Index { get; }

    public override object? Eval(RpgRuntimeContext ctx) => ctx.ReadArrayValue(Name, RpgValues.ToLong(Index.Eval(ctx)));
}

public sealed class RpgExpressionParser
{
    private readonly string _text;
    private int _position;

    private RpgExpressionParser(string text) => _text = text;

    public static RpgExpr Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new RpgCompileException(0, "Empty expression.");
        }

        var parser = new RpgExpressionParser(text);
        var expr = parser.ParseExpression(0);
        parser.SkipWhitespace();
        if (!parser.AtEnd)
        {
            throw new RpgCompileException(0, $"Unexpected token '{parser.PeekToken()}' in expression '{text}'.");
        }

        return expr;
    }

    private bool AtEnd => _position >= _text.Length;

    private char Peek() => AtEnd ? '\0' : _text[_position];

    private char PeekNext() => _position + 1 >= _text.Length ? '\0' : _text[_position + 1];

    private void SkipWhitespace()
    {
        while (!AtEnd && char.IsWhiteSpace(Peek()))
        {
            _position++;
        }
    }

    private bool Match(string word)
    {
        SkipWhitespace();
        if (_position + word.Length <= _text.Length &&
            string.Compare(_text, _position, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0)
        {
            var after = _position + word.Length;
            if (after < _text.Length && (char.IsLetterOrDigit(_text[after]) || _text[after] == '%'))
            {
                return false;
            }

            _position = after;
            return true;
        }

        return false;
    }

    private string PeekToken()
    {
        var start = _position;
        SkipWhitespace();
        while (!AtEnd && Peek() != ' ' && Peek() != '\t')
        {
            _position++;
        }

        var token = _text[start.._position];
        _position = start;
        return token;
    }

    private RpgExpr ParseExpression(int minPrecedence)
    {
        var lhs = ParseUnary();

        while (true)
        {
            SkipWhitespace();
            var op = ReadOperator();
            if (op is null)
            {
                break;
            }

            var precedence = Precedence(op);
            if (precedence < minPrecedence)
            {
                _position -= op.Length;
                break;
            }

            var rhs = ParseExpression(precedence + 1);
            lhs = new RpgBinary(lhs, op, rhs);
        }

        return lhs;
    }

    private static int Precedence(string op) => op switch
    {
        "OR" => 1,
        "AND" => 2,
        "=" or "==" or "*EQ" or "EQ" or "<>" or "!=" or "*NE" or "NE" or "<" or "*LT" or "LT" or "<=" or "*LE" or "LE" or ">" or "*GT" or "GT" or ">=" or "*GE" or "GE" => 3,
        "+" or "-" => 4,
        "*" or "/" => 5,
        _ => 0,
    };

    private string? ReadOperator()
    {
        SkipWhitespace();
        if (!AtEnd)
        {
            foreach (var candidate in Operators)
            {
                if (_position + candidate.Length <= _text.Length &&
                    string.Compare(_text, _position, candidate, 0, candidate.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    var after = _position + candidate.Length;
                    if (after < _text.Length && (char.IsLetterOrDigit(_text[after]) || _text[after] == '%'))
                    {
                        continue;
                    }

                    _position = after;
                    return candidate;
                }
            }
        }

        return null;
    }

    private static readonly string[] Operators =
    {
        "*GE", "*GT", "*LE", "*LT", "*NE", "*EQ", ">=", "<=", "<>", "!=", "==", "EQ", "NE", "GT", "LT", "GE", "LE", "AND", "OR", "+", "-", "*", "/", "=", "<", ">",
    };

    private RpgExpr ParseUnary()
    {
        SkipWhitespace();
        if (Match("NOT"))
        {
            return new RpgUnary("NOT", ParseUnary());
        }

        if (!AtEnd && Peek() == '(')
        {
            _position++;
            var grouped = ParseExpression(0);
            SkipWhitespace();
            if (Peek() != ')')
            {
                throw new RpgCompileException(0, "Expected ')' in expression.");
            }

            _position++;
            return grouped;
        }

        if (Peek() == '-' || Peek() == '+')
        {
            var sign = Peek();
            _position++;
            return new RpgUnary(sign.ToString(), ParseUnary());
        }

        return ParsePrimary();
    }

    private RpgExpr ParsePrimary()
    {
        SkipWhitespace();
        if (AtEnd)
        {
            throw new RpgCompileException(0, "Unexpected end of expression.");
        }

        var first = Peek();
        if (char.IsDigit(first) || first == '.')
        {
            return ParseLiteral();
        }

        if (first == '*')
        {
            _position++;
            var inner = ReadIdentifier();
            if (inner.Length > 0)
            {
                return ParseNamed("*" + inner);
            }

            _position--;
        }

        var name = ReadIdentifier();

        if (name.Length > 0 && Peek() == '(')
        {
            var upper = name.ToUpperInvariant();
            if (upper.StartsWith('%'))
            {
                _position++;
                var arguments = ParseArguments();
                return new RpgBuiltin(upper[1..], arguments);
            }

            if (upper == "NOT")
            {
                _position++;
                var inner = ParseArguments();
                return inner.Count > 0 ? new RpgUnary("NOT", inner[0]) : throw new RpgCompileException(0, "NOT() requires an argument.");
            }
        }

        if (name.Length > 0)
        {
            return ParseNamed(name);
        }

        return ParseLiteral();
    }

    private string ReadIdentifier()
    {
        SkipWhitespace();
        var start = _position;
        while (!AtEnd)
        {
            var c = Peek();
            if (char.IsLetterOrDigit(c) || c == '_' || c == '%' || c == '#' || c == '$' || c == '@')
            {
                _position++;
            }
            else
            {
                break;
            }
        }

        return _text[start.._position];
    }

    private RpgExpr ParseNamed(string name)
    {
        var upper = name.ToUpperInvariant();

        if (upper.StartsWith("*IN", StringComparison.Ordinal))
        {
            var suffix = name[3..];
            if (suffix.Equals("LR", StringComparison.OrdinalIgnoreCase))
            {
                return new RpgIndicatorRef(0);
            }

            if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var indicator))
            {
                if (indicator is < 0 or > 99)
                {
                    throw new RpgCompileException(0, $"Invalid indicator '{name}'.");
                }

                return new RpgIndicatorRef(indicator);
            }
        }

        switch (upper)
        {
            case "*BLANK":
            case "*BLANKS":
                return new RpgLiteral(string.Empty);
            case "*ZERO":
            case "*ZEROS":
                return new RpgLiteral(0m);
            case "*YES":
            case "*ON":
                return new RpgLiteral(true);
            case "*NO":
            case "*OFF":
                return new RpgLiteral(false);
            case "*LOVAL":
                return new RpgLiteral("*LOVAL");
            case "*HIVAL":
                return new RpgLiteral("*HIVAL");
            case "*DATE":
                return new RpgSpecialValue("*DATE");
            case "*TIME":
                return new RpgSpecialValue("*TIME");
            case "*TIMESTAMP":
                return new RpgSpecialValue("*TIMESTAMP");
            case "@":
                throw new RpgCompileException(0, "Address operator '@' is not supported.");
        }

        if (upper is "EQ" or "NE" or "GT" or "LT" or "N1")
        {
            throw new RpgCompileException(0, $"Operator '{name}' is not valid here.");
        }

        if (!AtEnd && Peek() == '(')
        {
            _position++;
            var index = ParseExpression(0);
            SkipWhitespace();
            if (Peek() != ')')
            {
                throw new RpgCompileException(0, $"Expected ')' after index of '{name}'.");
            }

            _position++;
            return new RpgDynamicIndexRef(name, index);
        }

        return new RpgFieldRef(name);
    }

    private List<RpgExpr> ParseArguments()
    {
        var arguments = new List<RpgExpr>();
        SkipWhitespace();
        if (Peek() == ')')
        {
            _position++;
            return arguments;
        }

        while (!AtEnd)
        {
            arguments.Add(ParseExpression(0));
            SkipWhitespace();
            if (Peek() is ',' or ':')
            {
                _position++;
                continue;
            }

            if (Peek() == ')')
            {
                _position++;
                break;
            }

            throw new RpgCompileException(0, "Expected ',' or ')' in argument list.");
        }

        return arguments;
    }

    private RpgExpr ParseLiteral()
    {
        SkipWhitespace();
        if (AtEnd)
        {
            throw new RpgCompileException(0, "Expected a value.");
        }

        var c = Peek();

        if (c == '\'')
        {
            return new RpgLiteral(ReadQuoted());
        }

        var start = _position;
        while (!AtEnd && (char.IsDigit(Peek()) || Peek() == '.' || Peek() == '+' || Peek() == '-'))
        {
            _position++;
        }

        var token = _text[start.._position];
        if (token.Length > 0 && (char.IsDigit(token[0]) || (token.Length > 1 && (token[0] == '+' || token[0] == '-') && char.IsDigit(token[1]))))
        {
            return new RpgLiteral(RpgNumbers.ParseLiteral(token));
        }

        throw new RpgCompileException(0, $"Invalid value '{token}' in expression.");
    }

    private string ReadQuoted()
    {
        _position++;
        var start = _position;
        while (!AtEnd && Peek() != '\'')
        {
            if (Peek() == '\\')
            {
                _position++;
            }

            _position++;
        }

        var value = _text[start.._position];
        if (AtEnd)
        {
            throw new RpgCompileException(0, "Unterminated string literal.");
        }

        _position++;
        return value;
    }
}

public static class RpgNumbers
{
    public static object ParseLiteral(string token)
    {
        var negative = token.StartsWith('-');
        var body = token.TrimStart('+', '-');
        var dot = body.IndexOf('.');
        if (dot > 0 && dot == body.Length - 2 && body.Length > 2)
        {
            var decimals = body[^1] - '0';
            var digits = body[..^2];
            if (decimal.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scaled))
            {
                var value = scaled;
                if (decimals > 0)
                {
                    value /= Pow10(decimals);
                }

                return negative ? -value : value;
            }
        }

        return decimal.TryParse(token, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble)
                ? parsedDouble
                : throw new RpgCompileException(0, $"Invalid numeric literal '{token}'.");
    }

    public static decimal Pow10(int exponent) => new decimal(1, 0, 0, false, (byte)exponent);
}