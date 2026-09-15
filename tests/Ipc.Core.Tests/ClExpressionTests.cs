using Ipc.Cl.Interpreter;

namespace Ipc.Core.Tests;

public sealed class ClExpressionTests
{
    private static object Evaluate(string expression, IReadOnlyDictionary<string, object>? variables = null, int ccsid = 37) =>
        ClExpression.Compile(expression).Evaluate(name => variables is not null && variables.TryGetValue(name, out var value) ? value : throw new ClRuntimeException("Undefined variable " + name), ccsid);
    [Theory]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("-3 + +2", "-1")]
    [InlineData("(12.5 - 2.5) / 4", "2.5")]
    [InlineData("10000000000000000000000000.02 - 10000000000000000000000000.01", "0.01")]
    public void Arithmetic_precedence_and_large_decimal_differences_are_exact(string expression, string expected) =>
        Assert.Equal(decimal.Parse(expected, global::System.Globalization.CultureInfo.InvariantCulture), Evaluate(expression));
    [Fact]
    public void Numeric_variables_compare_numerically_and_boolean_branches_short_circuit()
    {
        Assert.Equal(true, Evaluate("&N *GT 2 *AND (*NOT &FLAG)", new Dictionary<string, object> { ["&N"] = 10m, ["&FLAG"] = false }));
        Assert.Equal(true, Evaluate("*ON *OR (1 / 0 *EQ 1)"));
        Assert.Equal(false, Evaluate("*OFF *AND &MISSING"));
        Assert.Equal(true, Evaluate("'X' *EQ 'X   '"));
    }
    [Fact]
    public void Quoted_plus_apostrophes_concatenation_and_CCSID_order_are_preserved()
    {
        Assert.Equal("a+b's", Evaluate("'a+b''s'"));
        Assert.Equal("ab  cd", Evaluate("'ab  ' *CAT 'cd'"));
        Assert.Equal("abcd", Evaluate("'ab  ' *TCAT 'cd'"));
        Assert.Equal("ab cd", Evaluate("'ab  ' *BCAT 'cd'"));
        Assert.Equal(true, Evaluate("'A' *LT '0'")); // C1 sorts below F0 in CCSID 37.
        Assert.Equal(false, Evaluate("'A' *LT '0'", ccsid: 1208));
    }
    [Theory]
    [InlineData("1 / 0")]
    [InlineData("79228162514264337593543950335 + 1")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("1.2.3")]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("'unclosed")]
    [InlineData("%UNKNOWN(1)")]
    public void Invalid_expressions_or_arithmetic_fail_explicitly(string expression) => Assert.Throws<ClRuntimeException>(() => Evaluate(expression));
    [Fact]
    public void Deep_or_long_expression_trees_are_rejected_before_evaluation()
    {
        Assert.Throws<ClRuntimeException>(() => ClExpression.Compile(new string('(', 65) + "1" + new string(')', 65)));
        Assert.Throws<ClRuntimeException>(() => ClExpression.Compile(string.Join('+', Enumerable.Repeat("1", 100))));
    }
}
