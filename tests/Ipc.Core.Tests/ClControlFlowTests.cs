using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Xunit;

namespace Ipc.Core.Tests;

public sealed class ClControlFlowTests
{
    private static (CommandResult Result, List<string> Messages) Run(string source, int ccsid = 37)
    {
        var messages = new List<string>();
        var interpreter = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), messages.Add, ccsid: () => ccsid);
        return (interpreter.Run(new ClCompiler().Compile("FLOW", "QGPL", source)), messages);
    }
    [Fact]
    public void For_rechecks_upper_bound_and_iterate_performs_increment()
    {
        var run = Run("""
            pgm
            dcl &i *int
            dcl &limit *int value(5)
            dcl &sum *dec len(8 0)
            dofor &i from(1) to(&limit)
              if (&i *eq 2) then(iterate)
              chgvar &sum (&sum + &i)
              if (&i *eq 3) then(chgvar &limit 3)
            enddo
            sndpgmmsg msg(&sum)
            sndpgmmsg msg(&i)
            endpgm
            """);
        Assert.False(run.Result.IsError, run.Result.Message);
        Assert.Equal(new[] { "4", "4" }, run.Messages);
    }
    [Theory]
    [InlineData("5", "1", "-2", "531")]
    [InlineData("1", "5", "2", "135")]
    [InlineData("5", "1", "1", "")]
    [InlineData("1", "5", "-1", "")]
    public void For_bounds_and_direction(string from, string to, string by, string expected)
    {
        var run = Run($"DCL &I *INT\nDCL &S VALUE('')\nDOFOR &I FROM({from}) TO({to}) BY({by})\nCHGVAR &S (&S *CAT &I)\nENDDO\nSNDPGMMSG MSG(&S)");
        Assert.False(run.Result.IsError, run.Result.Message);
        Assert.Equal(expected, Assert.Single(run.Messages));
    }
    [Fact]
    public void Named_leave_and_iterate_cross_nested_groups_and_zero_increment()
    {
        var run = Run("""
            DCL &I *INT
            DCL &J *INT
            DCL &N *INT
            ALIAS:
            OUTER: DOFOR &I FROM(1) TO(4)
              DOFOR &J FROM(0) TO(0) BY(0)
                DO
                  CHGVAR &N (&N + 1)
                  IF (&N *EQ 2) THEN(LEAVE ALIAS)
                  ITERATE OUTER
                ENDDO
              ENDDO
            ENDDO
            SNDPGMMSG MSG(&I *CAT ':' *CAT &J *CAT ':' *CAT &N)
            """);
        Assert.False(run.Result.IsError, run.Result.Message);
        Assert.Equal("2:0:2", Assert.Single(run.Messages));
    }
    [Fact]
    public void While_checks_before_body_until_checks_after_and_iterate_rechecks_condition()
    {
        var run = Run("""
            DCL &N *INT
            DOWHILE ('0')
              CHGVAR &N 99
            ENDDO
            DOUNTIL (&N *GE 3)
              CHGVAR &N (&N + 1)
              ITERATE
              CHGVAR &N 99
            ENDDO
            DOWHILE (&N *LT 5)
              CHGVAR &N (&N + 1)
            ENDDO
            SNDPGMMSG MSG(&N)
            """);
        Assert.False(run.Result.IsError, run.Result.Message);
        Assert.Equal("5", Assert.Single(run.Messages));
    }
    [Theory]
    [InlineData("1", "FIRST")]
    [InlineData("2", "")]
    [InlineData("3", "OTHER")]
    public void Select_first_match_empty_action_and_otherwise(string value, string expected)
    {
        var run = Run($"""
            DCL &N *INT VALUE({value})
            SELECT
              WHEN (&N *EQ 1) THEN(DO)
                SNDPGMMSG MSG('FIRST')
              ENDDO
              WHEN (&N *LE 2)
              WHEN (&N *EQ 1) THEN(SNDPGMMSG MSG('WRONG'))
              OTHERWISE CMD(SNDPGMMSG MSG('OTHER'))
            ENDSELECT
            """);
        Assert.False(run.Result.IsError, run.Result.Message);
        Assert.Equal(expected, string.Join("", run.Messages));
    }
    [Fact]
    public void Nested_if_binds_else_to_nearest_if_and_return_exits_group()
    {
        var run = Run("""
            IF ('1') THEN(IF ('0') THEN(SNDPGMMSG MSG('WRONG')))
            ELSE CMD(DO)
              SNDPGMMSG MSG('RIGHT')
              RETURN
            ENDDO
            ELSE CMD(SNDPGMMSG MSG('WRONG'))
            SNDPGMMSG MSG('WRONG')
            """);
        Assert.False(run.Result.IsError, run.Result.Message);
        Assert.Equal("RIGHT", Assert.Single(run.Messages));
    }
    [Fact]
    public void Typed_values_preserve_scale_pad_truncate_convert_and_compare_in_job_ccsid()
    {
        var run = Run("""
            DCL &N *DEC LEN(5 2) VALUE(-3.90)
            DCL &C *CHAR LEN(7)
            CHGVAR &C &N
            SNDPGMMSG MSG(&C)
            CHGVAR &C 'ABCDEFGHI'
            SNDPGMMSG MSG(&C)
            CHGVAR &N '12.345'
            SNDPGMMSG MSG(&N)
            DCL &F *LGL VALUE('0')
            CHGVAR &F ('A' *LT '0' *AND 10 *GT 2)
            SNDPGMMSG MSG(&F)
            """);
        Assert.False(run.Result.IsError, run.Result.Message);
        Assert.Equal(new[] { "-003.90", "ABCDEFG", "12.34", "1" }, run.Messages);
        Assert.Equal("0", Assert.Single(Run("SNDPGMMSG MSG('A' *LT '0')", 1208).Messages));
    }
    [Theory]
    [InlineData("DCL &N *INT LEN(2)\nCHGVAR &N 32768", "range")]
    [InlineData("DCL &N *UINT\nCHGVAR &N -1", "range")]
    [InlineData("DCL &N *DEC LEN(5 2)\nCHGVAR &N 1000", "precision")]
    [InlineData("DCL &N *DEC LEN(5 2)\nCHGVAR &N (1 / 0)", "Division by zero")]
    [InlineData("DCL &N *DEC LEN(5 2)\nCHGVAR &N 0.001", "scale")]
    [InlineData("DCL &N *CHAR LEN(4)\nCHGVAR &N 12345", "does not fit")]
    [InlineData("DCL &N *INT\nCHGVAR &N &UNSET", "not initialized")]
    public void Runtime_errors_retain_physical_location(string source, string message)
    {
        var run = Run(source);
        Assert.True(run.Result.IsError);
        Assert.Contains("QGPL/FLOW:2:", run.Result.Message);
        Assert.Contains(message, run.Result.Message);
    }
    [Theory]
    [InlineData("ENDDO")]
    [InlineData("DO\nLEAVE\nENDDO")]
    [InlineData("DOFOR &I FROM(1) TO(3)\nENDDO")]
    [InlineData("DCL &I *CHAR\nDOFOR &I FROM(1) TO(3)\nENDDO")]
    [InlineData("DOWHILE ('1')\nLEAVE MISSING\nENDDO")]
    [InlineData("SELECT\nOTHERWISE\nENDSELECT")]
    [InlineData("SELECT\nWHEN ('0')\nOTHERWISE\nWHEN ('1')\nENDSELECT")]
    [InlineData("IF ('1') THEN(DO)\nENDPGM")]
    [InlineData("DCL &I *INT\nDCL &I *INT")]
    [InlineData("DCL &I *DEC LEN(16 0)")]
    [InlineData("CHGVAR VALUE(1)")]
    [InlineData("IF ('1') COND('0') THEN(RETURN)")]
    [InlineData("SNDPGMMSG MSG('A') +")]
    public void Malformed_control_flow_is_rejected_at_compile_time(string source)
    {
        Assert.Throws<ClCompileException>(() => new ClCompiler().Compile("BAD", "QGPL", source));
    }
    [Fact]
    public void Continuations_preserve_quoted_plus_and_minus_spacing()
    {
        var run = Run("SNDPGMMSG MSG('A+ +\n   B')\nSNDPGMMSG MSG('X-\n Y')");
        Assert.False(run.Result.IsError, run.Result.Message);
        Assert.Equal(new[] { "A+ B", "X Y" }, run.Messages);
    }
    [Fact]
    public void Infinite_zero_increment_loop_observes_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        var interpreter = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), _ => { if (++count == 3) cancellation.Cancel(); }, cancellation.Token);
        var program = new ClCompiler().Compile("CANCEL", "QGPL", "DCL &I *INT\nDOFOR &I FROM(0) TO(0) BY(0)\nSNDPGMMSG MSG(&I)\nENDDO");
        Assert.Throws<OperationCanceledException>(() => interpreter.Run(program));
        Assert.Equal(3, count);
    }
}
