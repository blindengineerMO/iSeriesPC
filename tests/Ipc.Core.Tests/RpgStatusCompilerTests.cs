using Ipc.Core.Compilation;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData("monitor;\nread DATA;")]
    [InlineData("endmon;")]
    [InlineData("monitor;\nendmon;")]
    [InlineData("monitor;\nif *on;\non-error *all;\nendif;\nendmon;")]
    [InlineData("monitor;\non-error 0;\nendmon;")]
    [InlineData("monitor;\non-error 99;\nendmon;")]
    [InlineData("monitor;\non-error 99999;\nendmon;")]
    [InlineData("monitor;\non-error 12345;\nendmon;")]
    [InlineData("monitor;\non-error ID;\nendmon;")]
    [InlineData("monitor;\non-error 1211:;\nendmon;")]
    [InlineData("monitor;\ngoto OUT;\non-error *all;\nendmon;\ntag OUT;")]
    [InlineData("goto INNER;\nmonitor;\ntag INNER;\non-error *all;\nendmon;")]
    [InlineData("goto INNER;\nmonitor;\non-error *all;\ntag INNER;\nendmon;")]
    [InlineData("read(ee) DATA;")]
    [InlineData("read(nen) DATA;")]
    [InlineData("update(ne) ROW;")]
    [InlineData("read(h) DATA;")]
    public void Unsupported_monitor_shapes_filters_transfers_and_extenders_fail_at_compile_time(string body)
    { Assert.Throws<RpgCompileException>(() => Parse(UpdateFile() + body)); }
    [Fact]
    public void Native_monitor_errors_keep_the_original_include_location()
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "APP", new SourceDocument("MAIN", "**free\n/include BODY"),
            (_, _) => new("BODY", "monitor;\non-error 99;\nendmon;")));
        Assert.Equal("BODY", error.Location!.Source); Assert.Equal(2, error.Location.Line);
    }
    [Theory]
    [InlineData("%error(DATA)")]
    [InlineData("%status(ROW)")]
    [InlineData("%status(DATA:DATA)")]
    [InlineData("%status('DATA')")]
    public void Status_builtins_reject_bad_arguments_instead_of_ignoring_them(string expression)
    {
        Compile(UpdateFile() + "sndpgmmsg %char(" + expression + ");\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.True(result.IsError);
    }
    [Fact]
    public void E_and_an_explicit_error_indicator_cannot_be_combined_in_fixed_source()
    {
        var chars = Enumerable.Repeat(' ', 80).ToArray(); chars[6] = 'C'; "READ(E)".CopyTo(0, chars, 35, 7);
        "DATA".CopyTo(0, chars, 42, 4); "20".CopyTo(0, chars, 66, 2);
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "APP", new string(chars)));
        Assert.Contains("cannot be combined", error.Message);
    }
    [Fact]
    public void Nested_SELECT_state_survives_a_monitor_transfer_out_of_an_inner_SELECT()
    {
        Compile(UpdateFile(extra: "usropn") + "select;\nwhen *on;\nmonitor;\nselect;\nwhen *on;\nread DATA;\nendsl;\n" +
            "on-error 1211;\nsndpgmmsg 'HANDLED';\nendmon;\nwhen *on;\nsndpgmmsg 'BAD';\nendsl;\nsndpgmmsg 'AFTER';\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Ok(result, "HANDLED"); Assert.Contains("AFTER", result.Message); Assert.DoesNotContain("BAD", result.Message);
    }
    [Fact]
    public void Compact_FOR_assignment_and_quoted_nested_TO_expressions_keep_monitor_loop_state()
    {
        Compile(UpdateFile() + "dcl-s I int(10);\nfor I=%len('TO')-1 to %len('BY') by 1;\nmonitor;\nread DATA;\n" +
            "on-error *file;\nsndpgmmsg 'BAD';\nendmon;\nendfor;\nsndpgmmsg %char(I);\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Ok(result, "3"); Assert.DoesNotContain("BAD", result.Message);
    }
}
