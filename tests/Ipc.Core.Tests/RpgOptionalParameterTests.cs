using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Core.Work;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed class RpgOptionalParameterTests
{
    private static RpgProgram Compile(string source) => RpgCompiler.Compile("QGPL", "OPTIONAL", source);
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private const string Pi = "**free\ndcl-pi *n;\nA int(3);\nB char(4) options(*nopass);\nend-pi;\n";

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void Native_prototypes_pass_only_present_arguments_and_write_back_present_references(bool fixedForm, int count)
    {
        var source = fixedForm ? D("RUN", "PR", keywords: "EXTPGM('TARGET')") + D(length: "3", type: 'I', decimals: "0", keywords: "OPTIONS(*NOPASS)") +
            D(length: "4", keywords: "CONST OPTIONS(*NOPASS)") + "**free\n" :
            "**free\ndcl-pr RUN extpgm('TARGET');\nA int(3) options(*nopass);\nB char(4) const options(*nopass);\nend-pr;\n";
        source += "dcl-s N int(3) inz(7);\ncallp RUN(" + (count == 0 ? "" : count == 1 ? "N" : "N:'HI'") + ");";
        var called = false;
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, values, borrow) =>
        {
            called = true; Assert.Equal(count, values.Count); var args = borrow!(); Assert.Equal(count, args.Count);
            if (count > 0) ((ProgramArgument)args[0]!).Value = 12L;
            if (count == 2) { var input = (ProgramArgument)args[1]!; Assert.True(input.IsReadOnly); Assert.Equal("HI  ", input.Value); }
            return new() { Success = true };
        } }).Run(Compile(source));
        Assert.True(called); Assert.Equal(count == 0 ? 7L : 12L, context.ReadValue("N"));
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void Optional_main_PI_supports_every_present_prefix_and_reports_real_counts(bool fixedForm, int count)
    {
        var source = fixedForm ? D(definition: "PI") + D("A", length: "3", type: 'I', decimals: "0", keywords: "OPTIONS(*NOPASS)") +
            D("B", length: "4", keywords: "OPTIONS(*NOPASS)") + "**free\n" :
            "**free\ndcl-pi *n;\nA int(3) options(*nopass);\nB char(4) options(*nopass);\nend-pi;\n";
        source += "dcl-s COUNT int(3);\neval COUNT=%parms();\nif %passed(A);\neval A=A+1;\nendif;\nif %passed(B);\neval B='DONE';\nendif;";
        var program = Compile(source); var interpreter = new RpgInterpreter(new());
        var context = interpreter.Run(program, count == 0 ? null : count == 1 ? [7L] : [7L, "ABCD"]);
        Assert.Equal((long)count, context.ReadValue("COUNT")); Assert.Equal(count >= 1, Read(context, "%passed(A)")); Assert.Equal(count == 2, Read(context, "%passed(B)"));
        Assert.Equal(1, Read(context, "%parmnum(A)")); Assert.Equal(2, Read(context, "%parmnum(B)"));
        if (count > 0) Assert.Equal(8L, context.ReadValue("A"));
        else Assert.Equal(105, Assert.Throws<RpgRuntimeException>(() => context.ReadValue("A")).StatusCode);
        if (count == 2) Assert.Equal("DONE", context.ReadValue("B"));
        Assert.Equal(count > 0, RpgInterpreter.AcceptsEntryParameterCount(Compile(Pi), count));
    }

    [Fact]
    public void Retained_activation_rebinds_presence_without_exposing_a_previous_optional_value()
    {
        var program = Compile(Pi + "dcl-s CALLS int(3) inz(0);\neval CALLS=CALLS+1;\nreturn;"); var interpreter = new RpgInterpreter(new());
        var context = interpreter.Run(program, [1L, "OLD!"]); Assert.Equal("OLD!", context.ReadValue("B"));
        interpreter.Run(program, [2L], context); Assert.False((bool)Read(context, "%passed(B)")!);
        Assert.Equal(105, Assert.Throws<RpgRuntimeException>(() => context.ReadValue("B")).StatusCode);
        interpreter.Run(program, [3L, "NEW!"], context); Assert.True((bool)Read(context, "%passed(B)")!);
        Assert.Equal("NEW!", context.ReadValue("B")); Assert.Equal(3L, context.ReadValue("CALLS"));
    }

    [Theory]
    [InlineData("%passed(B) and B='ABCD'", false)]
    [InlineData("not %passed(B) or B='ABCD'", true)]
    [InlineData("%parms() >= %parmnum(B) and B='ABCD'", false)]
    [InlineData("%parms() < %parmnum(B) or B='ABCD'", true)]
    public void Boolean_guards_short_circuit_before_reading_absent_parameters(string expression, bool expected)
    {
        var context = new RpgInterpreter(new()).Run(Compile(Pi + "dcl-s FLAG ind;\neval FLAG=" + expression + ";"), [1L]);
        Assert.Equal(expected, context.ReadValue("FLAG")); Assert.Equal(4, Read(context, "%size(B)"));
    }

    [Theory]
    [InlineData("*off and 1/0=0", false)] [InlineData("*on or 1/0=0", true)]
    [InlineData("I<=%elem(ARR) and I>0 and ARR(I)>10", false)]
    [InlineData("I>%elem(ARR) or ARR(I)>10", true)]
    public void Short_circuit_guards_also_skip_invalid_indexes_and_arithmetic(string expression, bool expected)
    {
        var context = new RpgInterpreter(new()).Run(Compile("**free\ndcl-s ARR int(3) dim(2);\ndcl-s I int(3) inz(3);\ndcl-s FLAG ind;\neval FLAG=" + expression + ";"));
        Assert.Equal(expected, context.ReadValue("FLAG"));
    }

    [Fact]
    public void Parameter_numbers_are_compile_time_constants_without_reading_optional_storage()
    {
        var context = new RpgInterpreter(new()).Run(Compile(Pi + "dcl-c POSITION %parmnum(B);\ndcl-s ARR char(POSITION);\neval ARR='OK';"), [1L]);
        Assert.Equal(2m, context.ReadValue("POSITION")); Assert.Equal("OK", context.ReadValue("ARR"));
    }

    [Theory]
    [InlineData("eval B='FAIL';")] [InlineData("clear B;")] [InlineData("eval %subst(B:1:1)='F';")]
    public void Unpassed_parameter_writes_fail_with_status_105_and_can_be_monitored(string operation)
    {
        var context = new RpgInterpreter(new()).Run(Compile(Pi + "dcl-s CAUGHT ind;\nmonitor;\n" + operation + "\non-error 105;\neval CAUGHT=*on;\nendmon;"), [1L]);
        Assert.Equal(true, context.ReadValue("CAUGHT"));
    }

    [Fact]
    public void Forwarding_an_absent_reference_fails_during_argument_preparation_before_target_dispatch()
    {
        var called = false;
        var source = Pi + "dcl-pr RUN extpgm('TARGET');\nINPUT char(4);\nend-pr;\ndcl-s CAUGHT ind;\nmonitor;\ncallp(e) RUN(B);\non-error 105;\neval CAUGHT=*on;\nendmon;";
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, _) => { called = true; return new() { Success = true }; } }).Run(Compile(source), [1L]);
        Assert.False(called); Assert.Equal(true, context.ReadValue("CAUGHT")); Assert.Equal(false, Read(context, "%error()"));
    }

    [Theory]
    [InlineData("options")] [InlineData("options()")] [InlineData("options(*omit:*omit)")] [InlineData("options(*varsize)")]
    [InlineData("options(*nopass:*nopass)")] [InlineData("options(*nopass) options(*nopass)")]
    public void Unsupported_or_duplicate_options_reject_at_the_declaration(string options)
    {
        var error = Assert.Throws<RpgCompileException>(() => Compile("**free\ndcl-pi *n;\nA int(3) " + options + ";\nend-pi;"));
        Assert.Equal(3, error.Location!.Line);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void A_required_parameter_cannot_follow_an_optional_one(bool fixedForm, bool entry)
    {
        var source = fixedForm ? D(entry ? "" : "RUN", entry ? "PI" : "PR", keywords: entry ? "" : "EXTPGM('TARGET')") +
            D("A", length: "3", type: 'I', decimals: "0", keywords: "OPTIONS(*NOPASS)") + D("B", length: "3", type: 'I', decimals: "0") :
            "**free\ndcl-" + (entry ? "pi *n" : "pr RUN extpgm('TARGET')") + ";\nA int(3) options(*nopass);\nB int(3);\nend-" + (entry ? "pi" : "pr") + ";";
        var error = Assert.Throws<RpgCompileException>(() => Compile(source)); Assert.Equal(fixedForm ? 3 : 4, error.Location!.Line); Assert.Contains("following", error.Message);
    }

    [Theory]
    [InlineData("%passed(A)")] [InlineData("%passed(N)")] [InlineData("%passed(B(1))")] [InlineData("%passed('B')")]
    [InlineData("%passed()")] [InlineData("%passed(B:B)")] [InlineData("%parmnum(N)")] [InlineData("%parmnum(B+1)")]
    [InlineData("%parmnum()")] [InlineData("%parmnum(B:B)")]
    public void Parameter_builtins_require_current_interface_parameter_names(string expression)
    {
        var error = Assert.Throws<RpgCompileException>(() => Compile(Pi + "dcl-s N int(3);\neval N=" + expression + ";"));
        Assert.Equal(7, error.Location!.Line);
    }

    [Theory]
    [InlineData("%parmnum(N)")] [InlineData("%passed(N)")]
    public void Entry_PLIST_fields_are_not_native_interface_operands(string expression)
    {
        Assert.Throws<RpgCompileException>(() => Compile("**free\ndcl-s N int(3);\nplist *entry;\nparm N;\neval N=" + expression + ";"));
    }

    [Fact]
    public void Parameter_builtin_spelling_in_literals_is_not_treated_as_an_operand()
    {
        var context = new RpgInterpreter(new()).Run(Compile("**free\ndcl-s TEXT char(40);\neval TEXT='It''s %passed(N) and %parmnum(N)';"));
        Assert.StartsWith("It's %passed(N)", (string)context.ReadValue("TEXT")!);
    }

    [Fact]
    public void Named_PI_and_PR_must_agree_on_optional_parameter_modes()
    {
        Assert.Throws<RpgCompileException>(() => Compile("**free\ndcl-pr RUN extpgm('TARGET');\nN int(3) options(*nopass);\nend-pr;\ndcl-pi RUN;\nN int(3);\nend-pi;"));
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void Calls_outside_the_required_optional_range_reject_before_the_body(int count)
    {
        Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new()).Run(Compile(Pi + "eval A=1/0;"), Enumerable.Repeat<object?>(1L, count).ToArray()));
        Assert.Throws<RpgCompileException>(() => Compile("**free\ndcl-pr RUN extpgm('TARGET');\nA int(3);\nB int(3) options(*nopass);\nend-pr;\ndcl-s N int(3);\ncallp RUN(" + string.Join(':', Enumerable.Repeat("N", count)) + ");"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Both_native_column_origins_support_optional_references_and_C_spec_calls(int shift)
    {
        var source = D("RUN", "PR", keywords: "EXTPGM('TARGET')", shift: shift) + D(length: "3", type: 'I', decimals: "0", keywords: "OPTIONS(*NOPASS)", shift: shift) +
            RpgFixedCalculationTests.C("CALLP", expression: "RUN()");
        var called = false; new RpgInterpreter(new() { TypedProgramCaller = (_, _, values, borrow) =>
        { called = true; Assert.Empty(values); Assert.Empty(borrow!()); return new() { Success = true }; } }).Run(Compile(source)); Assert.True(called);
    }

    [Theory]
    [InlineData("RUN()")] [InlineData("RUN(N:N:N)")]
    public void Runtime_count_validation_rejects_forged_calls_before_dispatch(string call)
    {
        var program = Compile("**free\ndcl-pr RUN extpgm('TARGET');\nA int(3);\nB int(3) options(*nopass);\nend-pr;\ndcl-s N int(3);\ncallp RUN(N);");
        program.MainStatements.Single().Factor1 = call;
        var called = false; var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, _) =>
        { called = true; return new() { Success = true }; } }).Run(program)); Assert.Equal(105, error.StatusCode); Assert.False(called);
    }

    [Fact]
    public void Absent_optional_formals_do_not_allocate_buffers_or_count_toward_actual_argument_bytes()
    {
        var program = Compile("**free\ndcl-pr RUN extpgm('TARGET');\n" + string.Concat(Enumerable.Repeat("N char(32767) const options(*nopass);\n", 255)) + "end-pr;\ncallp RUN();");
        Assert.Empty(program.DataStructures);
        new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) => { Assert.Empty(borrow!()); return new() { Success = true }; } }).Run(program);
    }
}
