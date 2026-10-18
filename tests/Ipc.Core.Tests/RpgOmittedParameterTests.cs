using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed class RpgOmittedParameterTests
{
    private static RpgProgram Compile(string source) => RpgCompiler.Compile("QGPL", "OMITTED", source);
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private const string Pi = "**free\ndcl-pi *n;\nA int(3) options(*omit);\nB char(4) const options(*omit:*nopass);\nend-pi;\n";

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void Native_OMIT_arguments_keep_their_position_but_have_no_storage_and_expire_with_the_call(bool fixedForm, bool readOnly)
    {
        var options = (readOnly ? "CONST " : "") + "OPTIONS(*OMIT)";
        var source = fixedForm ? D("RUN", "PR", keywords: "EXTPGM('TARGET')") + D(length: "3", type: 'I', decimals: "0", keywords: options) + D(length: "4", keywords: "CONST") + "**free\n" :
            "**free\ndcl-pr RUN extpgm('TARGET');\nN int(3) " + options + ";\nTEXT char(4) const;\nend-pr;\n";
        ProgramArgument? retained = null; var called = false;
        new RpgInterpreter(new() { TypedProgramCaller = (_, _, values, borrow) =>
        {
            called = true; Assert.Equal(2, values.Count); Assert.Null(values[0]); Assert.Equal("TAIL", values[1]);
            var args = borrow!(); retained = (ProgramArgument)args[0]!;
            Assert.True(retained.IsOmitted); Assert.True(retained.IsReadOnly); Assert.Equal("*OMIT", retained.Type);
            Assert.Equal(0, retained.Length); Assert.Null(retained.Storage); Assert.Null(retained.Value); Assert.Null(retained.ToBuffer());
            Assert.Null(retained.NormalizeReturnedValue(null)); Assert.Throws<CpfException>(() => retained.Value = null);
            Assert.Throws<CpfException>(() => retained.NormalizeReturnedValue(1L));
            return new() { Success = true, UpdatedParameters = [null, "TAIL"] };
        } }).Run(Compile(source + "callp RUN(*omit:'TAIL');"));
        Assert.True(called); Assert.Throws<ObjectDisposedException>(() => retained!.Value); Assert.Throws<ObjectDisposedException>(() => retained!.ToBuffer());
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void Main_PI_distinguishes_present_omitted_and_unpassed_parameters(bool references, int state)
    {
        var a = references ? (object?)ProgramArgument.Omitted() : null;
        object?[] parameters = state == 0 ? [a] : state == 1 ? [a, references ? ProgramArgument.Omitted() : null] : [a, "TEXT"];
        var context = new RpgInterpreter(new()).Run(Compile(Pi + "dcl-s GUARD ind;\neval GUARD=not %passed(B) or B='TEXT';"), parameters);
        Assert.Equal(parameters.Length, Read(context, "%parms()")); Assert.False((bool)Read(context, "%passed(A)")!); Assert.True((bool)Read(context, "%omitted(A)")!);
        Assert.Equal(state == 1, Read(context, "%omitted(B)")); Assert.Equal(state == 2, Read(context, "%passed(B)")); Assert.Equal(2, Read(context, "%parmnum(B)"));
        Assert.Equal(true, context.ReadValue("GUARD")); Assert.Equal(4, Read(context, "%size(B)"));
        Assert.Equal(105, Assert.Throws<RpgRuntimeException>(() => context.ReadValue("A")).StatusCode);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Both_native_column_origins_support_OMIT_and_presence_builtins(int shift)
    {
        var source = D(definition: "PI", shift: shift) + D("N", length: "3", type: 'I', decimals: "0", keywords: "OPTIONS(*OMIT:*NOPASS)", shift: shift) +
            "**free\ndcl-s FLAG ind;\neval FLAG=%omitted(N) and not %passed(N);";
        var context = new RpgInterpreter(new()).Run(Compile(source), [ProgramArgument.Omitted()]); Assert.Equal(true, context.ReadValue("FLAG"));
    }

    [Fact]
    public void Retained_activation_recomputes_omission_without_exposing_old_or_null_backing_storage()
    {
        var program = Compile(Pi + "return;"); var interpreter = new RpgInterpreter(new()); var context = interpreter.Run(program, [7L, "OLD!"]);
        Assert.True((bool)Read(context, "%passed(A)")!); Assert.False((bool)Read(context, "%omitted(A)")!);
        interpreter.Run(program, [ProgramArgument.Omitted(), ProgramArgument.Omitted()], context);
        Assert.True((bool)Read(context, "%omitted(B)")!); Assert.Equal(105, Assert.Throws<RpgRuntimeException>(() => context.ReadValue("B")).StatusCode);
        interpreter.Run(program, [8L], context); Assert.False((bool)Read(context, "%omitted(B)")!); Assert.False((bool)Read(context, "%passed(B)")!);
        interpreter.Run(program, [9L, "NEW!"], context); Assert.Equal("NEW!", context.ReadValue("B")); Assert.True((bool)Read(context, "%passed(B)")!);
    }

    [Theory]
    [InlineData("char(32767)")] [InlineData("packed(5:1)")] [InlineData("zoned(3:0)")]
    [InlineData("int(3)")] [InlineData("float(4)")] [InlineData("ind")] [InlineData("date")] [InlineData("time")] [InlineData("timestamp")]
    public void OMIT_does_not_convert_or_allocate_the_formal_scalar_image(string type)
    {
        var program = Compile("**free\ndcl-pr RUN extpgm('TARGET');\nN " + type + " const options(*omit);\nend-pr;\ncallp RUN(*omit);"); Assert.Empty(program.DataStructures);
        new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) => { var argument = (ProgramArgument)borrow!().Single()!; Assert.True(argument.IsOmitted); Assert.Null(argument.ToBuffer()); return new() { Success = true }; } }).Run(program);
    }

    [Fact]
    public void Multiple_omissions_are_not_overlapping_storage_even_when_the_same_descriptor_is_reused()
    {
        var omitted = ProgramArgument.Omitted(); Assert.False(ProgramArgument.Overlaps(omitted, omitted)); Assert.False(ProgramArgument.Overlaps(omitted, ProgramArgument.Omitted()));
        new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) =>
        { var args = borrow!(); Assert.False(ProgramArgument.Overlaps(args[0], args[1])); return new() { Success = true }; } }).Run(
            Compile("**free\ndcl-pr RUN extpgm('TARGET');\nA int(3) options(*omit);\nB int(3) options(*omit);\nend-pr;\ncallp RUN(*omit:*omit);"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Omitted_response_values_accept_null_and_reject_all_mutable_writeback_if_altered(bool changed)
    {
        var program = Compile("**free\ndcl-pr RUN extpgm('TARGET');\nA int(3);\nB int(3) options(*omit);\nend-pr;\ndcl-s N int(3) inz(7);\ncallp(e) RUN(N:*omit);");
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, _) => new() { Success = true, UpdatedParameters = [12L, changed ? 1L : null] } }).Run(program);
        Assert.Equal(changed ? 7L : 12L, context.ReadValue("N")); Assert.Equal(changed, Read(context, "%error()"));
    }

    [Theory]
    [InlineData("eval A=A+1;")] [InlineData("clear A;")]
    [InlineData("eval %subst(B:1:1)='X';")] [InlineData("clear B;")]
    public void Omitted_storage_access_is_a_monitorable_status_105(string operation)
    {
        var context = new RpgInterpreter(new()).Run(Compile(Pi.Replace("B char(4) const", "B char(4)") + "dcl-s CAUGHT ind;\nmonitor;\n" + operation + "\non-error 105;\neval CAUGHT=*on;\nendmon;"), [null, null]);
        Assert.Equal(true, context.ReadValue("CAUGHT"));
    }

    [Theory]
    [InlineData("%omitted(A)")] [InlineData("%omitted(B)")] [InlineData("%omitted(UNKNOWN)")]
    [InlineData("%omitted('B')")] [InlineData("%omitted(B(1))")] [InlineData("%omitted()")]
    [InlineData("%omitted(B:B)")] [InlineData("%omitted(B+1)")]
    public void OMITTED_requires_an_OMIT_parameter_name_at_the_statement_source(string expression)
    {
        var source = Pi.Replace("options(*omit);", ";").Replace("options(*omit:*nopass)", "options(*nopass)") + "dcl-s FLAG ind;\neval FLAG=" + expression + ";";
        var error = Assert.Throws<RpgCompileException>(() => Compile(source)); Assert.Equal(7, error.Location!.Line);
    }

    [Theory]
    [InlineData("")] [InlineData("const")] [InlineData("options(*nopass)")]
    public void OMIT_without_the_prototype_option_rejects_at_the_call_source(string options)
    {
        var error = Assert.Throws<RpgCompileException>(() => Compile("**free\ndcl-pr RUN extpgm('TARGET');\nN int(3) " + options + ";\nend-pr;\ncallp RUN(*omit);"));
        Assert.Equal(5, error.Location!.Line); Assert.Contains("OPTIONS(*OMIT)", error.Message);
    }

    [Fact]
    public void OMIT_on_a_required_parameter_does_not_make_the_position_optional()
    {
        Assert.Throws<RpgCompileException>(() => Compile("**free\ndcl-pr RUN extpgm('TARGET');\nN int(3) options(*omit);\nend-pr;\ncallp RUN();"));
        Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new()).Run(Compile(Pi)));
        Assert.Equal(105, Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new()).Run(Compile(Pi.Replace("A int(3) options(*omit)", "A int(3)")), [null])).StatusCode);
    }

    [Theory]
    [InlineData("*omit+1")] [InlineData("%char(*omit)")]
    public void OMIT_is_not_a_value_inside_an_expression_and_fails_before_dispatch(string expression)
    {
        var called = false; var program = Compile("**free\ndcl-pr RUN extpgm('TARGET');\nN char(8) const options(*omit);\nend-pr;\ncallp RUN(" + expression + ");");
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, _) => { called = true; return new() { Success = true }; } }).Run(program));
        Assert.Equal(105, error.StatusCode); Assert.False(called);
    }

    [Fact]
    public void Quoted_OMIT_is_character_data_and_named_PI_modes_must_match_the_prototype()
    {
        new RpgInterpreter(new() { TypedProgramCaller = (_, _, values, borrow) => { Assert.Equal("*OMIT", values[0]); Assert.False(((ProgramArgument)borrow!()[0]!).IsOmitted); return new() { Success = true }; } }).Run(
            Compile("**free\ndcl-pr RUN extpgm('TARGET');\nN char(5) const options(*omit);\nend-pr;\ncallp RUN('*OMIT');"));
        Assert.Throws<RpgCompileException>(() => Compile("**free\ndcl-pr RUN extpgm('TARGET');\nN int(3) options(*omit);\nend-pr;\ndcl-pi RUN;\nN int(3);\nend-pi;"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Native_C_spec_CALLP_accepts_OMIT_in_both_definition_column_origins(int shift)
    {
        var source = D("RUN", "PR", keywords: "EXTPGM('TARGET')", shift: shift) + D(length: "3", type: 'I', decimals: "0", keywords: "OPTIONS(*OMIT)", shift: shift) +
            RpgFixedCalculationTests.C("CALLP", expression: "RUN(*OMIT)");
        new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) => { Assert.True(((ProgramArgument)borrow!().Single()!).IsOmitted); return new() { Success = true }; } }).Run(Compile(source));
    }

    [Theory]
    [InlineData("options(*omit)")] [InlineData("const options(*omit)")]
    public void OMIT_only_parameters_cannot_follow_a_NOPASS_parameter(string options)
    {
        var error = Assert.Throws<RpgCompileException>(() => Compile("**free\ndcl-pi *n;\nA int(3) options(*nopass);\nB int(3) " + options + ";\nend-pi;"));
        Assert.Equal(4, error.Location!.Line); Assert.Contains("following", error.Message);
    }

    [Fact]
    public void The_maximum_number_of_omissions_does_not_allocate_formal_width_images()
    {
        var program = Compile("**free\ndcl-pr RUN extpgm('TARGET');\n" + string.Concat(Enumerable.Repeat("N char(32767) const options(*omit);\n", 255)) +
            "end-pr;\ncallp RUN(" + string.Join(':', Enumerable.Repeat("*omit", 255)) + ");"); Assert.Empty(program.DataStructures);
        new RpgInterpreter(new() { TypedProgramCaller = (_, _, values, borrow) => { Assert.Equal(255, values.Count); Assert.All(values, Assert.Null); Assert.All(borrow!(), arg => Assert.True(((ProgramArgument)arg!).IsOmitted)); return new() { Success = true }; } }).Run(program);
    }

    [Fact]
    public void Unprototyped_OMIT_calls_reject_during_argument_preparation()
    {
        var called = false; var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, _) => { called = true; return new() { Success = true }; } }).Run(Compile("**free\ncallp RUN(*omit);")));
        Assert.Equal(105, error.StatusCode); Assert.False(called);
    }
}
