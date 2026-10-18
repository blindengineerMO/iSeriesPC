using Ipc.Core.Compilation;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed class RpgConstParameterTests
{
    private static string Proto(string parameters) => "dcl-pr RUN extpgm('TARGET');\n" + parameters + "\nend-pr;\n";
    private static Ipc.Rpg.Model.RpgProgram Compile(string source) => RpgCompiler.Compile("QGPL", "CONSTARG", source);
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Exact_CONST_references_observe_mutable_aliases_and_keep_storage_identity(bool fixedForm)
    {
        var source = fixedForm ? D("RUN", "PR", keywords: "EXTPGM('TARGET')") + D("INPUT", length: "4", keywords: "CONST") + D("OUTPUT", length: "4") + "**free\n" :
            "**free\n" + Proto("INPUT char(4) const;\nOUTPUT char(4);");
        source += "dcl-s TEXT char(4) inz('ABCD');\ncallp RUN(TEXT:TEXT);";
        ProgramArgument? retained = null;
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) =>
        {
            var args = borrow!(); var input = Assert.IsType<ProgramArgument>(args[0]); var output = Assert.IsType<ProgramArgument>(args[1]); retained = input;
            Assert.True(input.IsReadOnly); Assert.False(output.IsReadOnly); Assert.True(ProgramArgument.Overlaps(input, output));
            Assert.Equal("ABCD", input.Value); output.Value = "WXYZ"; Assert.Equal("WXYZ", input.Value);
            Assert.Equal("CPF3C90", Assert.Throws<CpfException>(() => input.Value = "FAIL").MessageId);
            Assert.Throws<CpfException>(() => input.Normalize("WXYZ")); return new() { Success = true };
        } }).Run(Compile(source));
        Assert.Equal("WXYZ", context.ReadValue("TEXT")); Assert.Throws<ObjectDisposedException>(() => retained!.Value);
    }

    [Fact]
    public void Two_CONST_aliases_share_one_scoped_reference_without_becoming_snapshots()
    {
        var source = "**free\n" + Proto("A int(3) const;\nB int(3) const;") + "dcl-s N int(3) inz(7);\ncallp RUN(N:N);";
        new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) =>
        { var args = borrow!(); Assert.Same(args[0], args[1]); return new() { Success = true }; } }).Run(Compile(source));
    }

    [Theory]
    [InlineData("char(4)", "'AB'", "*CHAR", 4, 0, "C1C24040")]
    [InlineData("char(2) ccsid(*hex)", "X'FF00'", "*CHAR", 2, 0, "FF00")]
    [InlineData("packed(5:1)", "12.34", "*DEC", 5, 1, "00123C")]
    [InlineData("zoned(3:1)", "-12.34", "*ZONED", 3, 1, "F1F2D3")]
    [InlineData("int(3)", "127", "*INT", 1, 0, "7F")]
    [InlineData("float(4)", "1.5", "*FLT", 4, 0, "3FC00000")]
    [InlineData("ind", "*on", "*LGL", 1, 0, "F1")]
    [InlineData("date", "'2026-10-18'", "*DATE", 10, 0, "F2F0F2F660F1F060F1F8")]
    public void Literal_CONST_temporaries_use_the_formal_native_codec_and_reject_writes(string declaration, string expression, string type, int length, int scale, string expected)
    {
        var source = "**free\n" + Proto("INPUT " + declaration + " const;") + "callp RUN(" + expression + ");";
        new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) =>
        {
            var input = Assert.IsType<ProgramArgument>(borrow!().Single()); Assert.True(input.IsReadOnly);
            Assert.Equal(type, input.Type); Assert.Equal(length, input.Length); Assert.Equal(scale, input.Decimals);
            Assert.Equal(expected, Convert.ToHexString(input.ToBuffer()!.ToArray())); Assert.Throws<CpfException>(() => input.Value = input.Value);
            Assert.NotNull(input.NormalizeReturnedValue(input.ToBuffer())); return new() { Success = true };
        } }).Run(Compile(source));
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Different_numeric_formats_make_a_private_CONST_temporary_before_target_dispatch(int ccsid)
    {
        var program = Compile("**free\n" + Proto("INPUT packed(5:1) const;\nOUTPUT zoned(7:3);") + "dcl-s N zoned(7:3) inz(42.125);\ncallp RUN(N:N);");
        RpgRuntimeContext? context = null;
        var host = new RpgHost { Ccsid = () => ccsid, TypedProgramCaller = (_, _, _, borrow) =>
        {
            context!.WriteValue("N", 99.125m); var args = borrow!(); var input = (ProgramArgument)args[0]!; var output = (ProgramArgument)args[1]!;
            Assert.False(ProgramArgument.Overlaps(input, output)); Assert.Equal(42.1m, input.Value); Assert.Equal(99.125m, output.Value);
            output.Value = 77.125m; Assert.Equal(42.1m, input.Value); return new() { Success = true };
        } };
        context = new(program, host); new RpgInterpreter(host).Run(program, existingContext: context); Assert.Equal(77.125m, context.ReadValue("N"));
    }

    [Theory]
    [InlineData("N+1", 8)] [InlineData("%abs(-7)", 7)] [InlineData("CONSTANT", 9)]
    public void Expressions_and_named_constants_are_formatted_as_private_readonly_values(string argument, int expected)
    {
        var source = "**free\n" + Proto("INPUT int(3) const;") + "dcl-s N int(3) inz(7);\ndcl-c CONSTANT 9;\ncallp RUN(" + argument + ");";
        new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) =>
        { var input = (ProgramArgument)borrow!().Single()!; Assert.True(input.IsReadOnly); Assert.Equal((long)expected, input.Value); return new() { Success = true }; } }).Run(Compile(source));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Character_prefix_references_keep_the_original_tail_and_overlap_range(bool readOnly)
    {
        var source = "**free\n" + Proto("PREFIX char(2)" + (readOnly ? " const" : "") + ";\nWHOLE char(6);") + "dcl-s TEXT char(6) inz('ABCDEF');\ncallp RUN(TEXT:TEXT);";
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, values, borrow) =>
        {
            Assert.Equal("AB", values[0]); var args = borrow!(); var prefix = (ProgramArgument)args[0]!; var whole = (ProgramArgument)args[1]!;
            Assert.Equal(2, prefix.Length); Assert.Equal(2, prefix.Storage!.Length); Assert.True(ProgramArgument.Overlaps(prefix, whole));
            if (readOnly) { whole.Value = "UVWXYZ"; Assert.Equal("UV", prefix.Value); }
            else { prefix.Value = "XY"; Assert.Equal("XYCDEF", whole.Value); }
            return new() { Success = true };
        } }).Run(Compile(source));
        Assert.Equal(readOnly ? "UVWXYZ" : "XYCDEF", context.ReadValue("TEXT"));
    }

    [Fact]
    public void UTF8_character_prefixes_use_bytes_and_preserve_the_parent_tail()
    {
        var source = "**free\n" + Proto("PREFIX char(2);") + "dcl-s TEXT char(3) inz('éZ');\ncallp RUN(TEXT);";
        var context = new RpgInterpreter(new() { Ccsid = () => 1208, TypedProgramCaller = (_, _, _, borrow) =>
        { var prefix = (ProgramArgument)borrow!().Single()!; Assert.Equal("C3A9", Convert.ToHexString(prefix.ToBuffer()!.ToArray())); prefix.Value = "ø"; return new() { Success = true }; } }).Run(Compile(source));
        Assert.Equal("øZ", context.ReadValue("TEXT"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CONST_PI_keeps_live_aliases_but_runtime_and_byte_writes_are_blocked(bool fixedForm)
    {
        var source = fixedForm ? D(definition: "PI") + D("INPUT", length: "4", keywords: "CONST") + D("OUTPUT", length: "4") + "**free\n" :
            "**free\ndcl-pi *n;\nINPUT char(4) const;\nOUTPUT char(4);\nend-pi;\n";
        source += "eval OUTPUT='WXYZ';\nreturn;";
        var bytes = CodePage.ToBytes(37, "ABCD");
        var cell = new ProgramArgument(() => new ProgramBuffer(bytes, 37), v => bytes = ((ProgramBuffer)v!).ToArray(), v => v, "*CHAR", 4, 0, () => new(bytes, 37));
        var context = new RpgInterpreter(new()).Run(Compile(source), [cell, cell]);
        Assert.Equal("WXYZ", context.ReadValue("INPUT")); Assert.Equal(105, Assert.Throws<RpgRuntimeException>(() => context.WriteValue("INPUT", "FAIL")).StatusCode);
        Assert.Equal("WXYZ", CodePage.FromBytes(37, bytes));
    }

    [Theory]
    [InlineData("eval INPUT='FAIL';")] [InlineData("eval %subst(INPUT:1:1)='Z';")] [InlineData("clear INPUT;")]
    [InlineData("move 'FAIL' INPUT;")]
    public void Direct_CONST_PI_writes_are_rejected_at_the_statement_source_line(string statement)
    {
        var error = Assert.Throws<RpgCompileException>(() => Compile("**free\ndcl-pi *n;\nINPUT char(4) const;\nend-pi;\n" + statement));
        Assert.Equal(5, error.Location!.Line); Assert.Contains("CONST", error.Message);
    }

    [Theory]
    [InlineData("int(3)", "128", 103)] [InlineData("packed(3:0)", "1000", 103)]
    [InlineData("int(3)", "'7'", 105)] [InlineData("char(2)", "7", 105)] [InlineData("int(3)", "1/0", 102)]
    public void CONST_conversion_errors_precede_the_final_call_and_are_not_consumed_by_E(string declaration, string argument, int status)
    {
        var called = false; var source = "**free\n" + Proto("INPUT " + declaration + " const;") + "dcl-s HIT ind;\nmonitor;\ncallp(e) RUN(" + argument + ");\non-error " + status + ";\neval HIT=*on;\nendmon;";
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, _) => { called = true; return new() { Success = true }; } }).Run(Compile(source));
        Assert.False(called); Assert.Equal(true, context.ReadValue("HIT")); Assert.Equal(status, Read(context, "%status()")); Assert.Equal(false, Read(context, "%error()"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Returned_CONST_echoes_are_accepted_but_changed_values_prevent_every_response_write(bool changed)
    {
        var source = "**free\n" + Proto("INPUT int(3) const;\nOUTPUT int(3);") + "dcl-s N int(3) inz(7);\ncallp(e) RUN(3:N);";
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) =>
        { _ = borrow!(); return new() { Success = true, UpdatedParameters = [changed ? 4L : 3L, 9L] }; } }).Run(Compile(source));
        Assert.Equal(changed ? 7L : 9L, context.ReadValue("N")); Assert.Equal(changed, Read(context, "%error()"));
    }

    [Theory]
    [InlineData("const const")] [InlineData("const(7)")] [InlineData("const value")] [InlineData("const options(*omit:*omit)")]
    public void Invalid_CONST_modes_keep_declaration_diagnostics(string options)
    { Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile("**free\n" + Proto("INPUT int(3) " + options + ";"))).Location); }

    [Fact]
    public void Named_PI_and_PR_must_agree_on_CONST_and_readonly_actuals_cannot_supply_mutable_formals()
    {
        Assert.Throws<RpgCompileException>(() => Compile("**free\n" + Proto("INPUT int(3) const;") + "dcl-pi RUN;\nINPUT int(3);\nend-pi;"));
        Assert.Throws<RpgCompileException>(() => Compile("**free\n" + Proto("INPUT int(3);") + "dcl-pi *n;\nN int(3) const;\nend-pi;\ncallp RUN(N);"));
    }

    [Fact]
    public void Converted_CONST_arguments_enforce_the_aggregate_byte_bound_before_dispatch()
    {
        var source = "**free\n" + Proto(string.Concat(Enumerable.Repeat("INPUT char(32767) const;\n", 33))) + "callp(e) RUN(" + string.Join(':', Enumerable.Repeat("'A'", 33)) + ");";
        var called = false; var host = new RpgHost { TypedProgramCaller = (_, _, _, _) => { called = true; return new() { Success = true }; } };
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(Compile(source))).MessageId); Assert.False(called);
    }

    [Theory]
    [InlineData("time", "'12:34:56'", "12.34.56")]
    [InlineData("timestamp", "'2026-10-18T12:34:56.123456'", "2026-10-18-12.34.56.123456")]
    public void Date_time_CONST_values_keep_native_ISO_bytes(string declaration, string literal, string expected)
    {
        new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) =>
        { var input = (ProgramArgument)borrow!().Single()!; Assert.Equal(expected, input.ToBuffer()!.ToText()); return new() { Success = true }; } })
            .Run(Compile("**free\n" + Proto("INPUT " + declaration + " const;") + "callp RUN(" + literal + ");"));
    }

    [Fact]
    public void Readonly_character_prefixes_remain_captured_when_the_index_changes()
    {
        var program = Compile("**free\n" + Proto("INPUT char(2) const;") + "dcl-s ARR char(4) dim(2) inz('ABCD');\ndcl-s I int(3) inz(1);\ncallp RUN(ARR(I));");
        RpgRuntimeContext? context = null; var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) =>
        { context!.WriteValue("I", 2L); context.WriteArrayValue("ARR", 2, "WXYZ"); Assert.Equal("AB", ((ProgramArgument)borrow!().Single()!).Value); return new() { Success = true }; } };
        context = new(program, host); new RpgInterpreter(host).Run(program, existingContext: context); Assert.Equal("ABCD", Read(context, "ARR(1)"));
    }

    [Fact]
    public void Invalid_runtime_formal_metadata_rejects_before_temporary_allocation()
    {
        var program = Compile("**free\n" + Proto("INPUT char(2) const;") + "callp RUN('A');");
        program.Prototypes[0].NativeParameters[0] = new() { Name = "BAD", Kind = Ipc.Rpg.Model.RpgFieldKind.Character, Length = int.MaxValue, ReadOnlyParameter = true };
        var called = false; Assert.Throws<CpfException>(() => new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, _) => { called = true; return new() { Success = true }; } }).Run(program)); Assert.False(called);
    }

    [Fact]
    public void A_CONST_PI_parameter_cannot_be_a_native_LOOKUP_writeback_index()
    {
        var source = D(definition: "PI") + D("I", length: "3", type: 'I', decimals: "0", keywords: "CONST") +
            D("ARR", "S", length: "3", type: 'I', decimals: "0", keywords: "DIM(2) INZ(7)") + RpgFixedCalculationTests.C("LOOKUP", "7", "ARR(I)", eq: "11");
        Assert.Contains("CONST", Assert.Throws<RpgCompileException>(() => Compile(source)).Message);
    }
}
