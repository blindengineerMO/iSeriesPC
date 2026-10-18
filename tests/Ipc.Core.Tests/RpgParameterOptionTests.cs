using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgParameterOptionTests
{
    private static RpgRuntimeContext Run(string source, RpgHost? host = null) => new RpgInterpreter(host ?? new()).Run(RpgCompiler.Compile("QGPL", "OPTIONS", source));
    private static string Call(string actual, string formal, string value = "A", string body = "return;") =>
        "**free\ndcl-s A " + actual + ";\ncallp GET(" + value + ");\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP " + formal + ";\nend-pi;\n" + body + "\nend-proc;";

    [Theory]
    [InlineData(37, "*trim", "AB    ")]
    [InlineData(37, "*rightadj", "  AB  ")]
    [InlineData(37, "*trim:*rightadj", "    AB")]
    [InlineData(1208, "*trim", "AB    ")]
    [InlineData(1208, "*rightadj", "  AB  ")]
    [InlineData(1208, "*trim:*rightadj", "    AB")]
    public void Character_transformations_use_private_padded_images(int ccsid, string options, string expected)
    {
        var c = Run("**free\ndcl-s A char(4) inz('AB');\ndcl-s OUT char(6);\ncallp GET(A);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP char(6) const options(" + options + ");\nend-pi;\neval OUT=P;\nreturn;\nend-proc;", new() { Ccsid = () => ccsid });
        Assert.Equal(expected, c.ReadValue("OUT")); Assert.Equal("AB  ", c.ReadValue("A"));
    }

    [Theory]
    [InlineData("*trim", "AB  ")]
    [InlineData("*rightadj", "AB  ")]
    [InlineData("*trim:*rightadj", "  AB")]
    public void Literal_arguments_preserve_or_trim_blanks_before_adjustment(string options, string expected)
    {
        var c = Run("**free\ndcl-s OUT char(4);\ncallp GET('  AB  ');\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP char(4) const options(" + options + ");\nend-pi;\neval OUT=P;\nreturn;\nend-proc;"); Assert.Equal(expected, c.ReadValue("OUT"));
    }

    [Fact]
    public void VALUE_is_writable_and_transformed_CONST_is_not_a_live_alias()
    {
        var c = Run("**free\ndcl-s A char(4) inz('AB');\ndcl-s OUT char(4);\ncallp GET(A:A:A);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP char(4) const options(*trim:*rightadj);\nQ char(4);\nV char(4) value options(*trim);\nend-pi;\neval Q='ZZ';\neval V='XX';\neval OUT=P;\nreturn;\nend-proc;"); Assert.Equal("  AB", c.ReadValue("OUT")); Assert.Equal("ZZ  ", c.ReadValue("A"));
    }

    [Theory]
    [InlineData("char(5)", "char(4) options(*exact)")]
    [InlineData("char(5) inz('A')", "char(4) const options(*exact)")]
    [InlineData("int(3) dim(3)", "int(3) dim(2) options(*exact)")]
    [InlineData("char(2) dim(2) ascend", "char(2) dim(2) descend const options(*exact)")]
    [InlineData("char(2) dim(2)", "char(2) dim(2) ascend const options(*exact)")]
    [InlineData("packed(7:3)", "packed(7:2) const options(*exact)")]
    [InlineData("packed(7:1)", "packed(7:2) value options(*exact)")]
    [InlineData("float(8)", "float(4) const options(*exact)")]
    [InlineData("float(4)", "packed(7:2) const options(*exact)")]
    [InlineData("char(4) ccsid(*hex)", "char(4) const options(*exact)")]
    public void Known_incompatible_actuals_reject_at_compile_time(string actual, string formal)
        => Assert.Contains("*EXACT", Assert.Throws<RpgCompileException>(() => Run(Call(actual, formal))).Message);

    [Theory]
    [InlineData("zoned(5:2) inz(12.34)", "packed(7:3) const options(*exact)")]
    [InlineData("int(3) inz(12)", "packed(7:2) value options(*exact)")]
    [InlineData("float(4) inz(1)", "float(8) const options(*exact)")]
    [InlineData("char(2) dim(2) descend inz('AB')", "char(3) dim(2) descend const options(*exact)")]
    [InlineData("char(2) dim(2) ascend inz('AB')", "char(3) dim(2) ascend const options(*exact)")]
    public void Compatible_numeric_and_array_declarations_accept(string actual, string formal) => Run(Call(actual, formal));

    [Theory]
    [InlineData("'ABCDE'", "char(4)")]
    [InlineData("12.345", "packed(5:2)")]
    [InlineData("1234", "packed(5:2)")]
    [InlineData("1.0", "int(3)")]
    [InlineData("'ABC'+'DE'", "char(4)")]
    public void Unknown_expression_values_fail_before_the_target_runs(string value, string type)
    {
        var c = Run("**free\ndcl-s COUNT int(3) inz(0);\ndcl-s STATUS int(10);\nmonitor;\ncallp GET(" + value + ");\non-error;\neval STATUS=%status();\nendmon;\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP " + type + " const options(*exact);\nend-pi;\neval COUNT=1;\nreturn;\nend-proc;"); Assert.Equal(105L, c.ReadValue("STATUS")); Assert.Equal(0L, c.ReadValue("COUNT"));
    }

    [Theory]
    [InlineData("'  AB  '", "    AB", 0)]
    [InlineData("'  ABCDEFG  '", "", 105)]
    public void EXACT_checks_the_trimmed_value_before_any_truncation(string value, string expected, int status)
    {
        var c = Run("**free\ndcl-s OUT char(6);\ndcl-s STATUS int(10);\nmonitor;\ncallp GET(" + value + ");\non-error;\neval STATUS=%status();\nendmon;\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP char(6) const options(*trim:*rightadj:*exact);\nend-pi;\neval OUT=P;\nreturn;\nend-proc;"); Assert.Equal((long)status, c.ReadValue("STATUS")); if (status == 0) Assert.Equal(expected, c.ReadValue("OUT"));
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Arrays_trim_each_actual_element_without_using_a_formal_prefix(int ccsid)
    {
        var c = Run("**free\ndcl-s A char(8) dim(2) inz(' AB');\ndcl-s OUT char(4);\ncallp GET(A);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP char(4) dim(2) const options(*trim:*rightadj:*exact);\nend-pi;\neval OUT=P(2);\nreturn;\nend-proc;", new() { Ccsid = () => ccsid }); Assert.Equal("  AB", c.ReadValue("OUT"));
    }

    [Fact]
    public void UTF8_exact_lengths_count_encoded_bytes()
    {
        var c = Run("**free\ndcl-s STATUS int(10);\nmonitor;\ncallp GET('ééé');\non-error;\neval STATUS=%status();\nendmon;\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP char(4) const options(*trim:*exact);\nend-pi;\nreturn;\nend-proc;", new() { Ccsid = () => 1208 }); Assert.Equal(105L, c.ReadValue("STATUS"));
    }

    [Fact]
    public void LIKEDS_chains_share_an_exact_type_origin()
    {
        var c = Run("**free\ndcl-ds ROOT qualified;\nX int(3) inz(7);\nend-ds;\ndcl-ds A likeds(ROOT);\ndcl-ds B likeds(A);\ncallp GET(B);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP likeds(ROOT) options(*exact);\nend-pi;\neval P.X=9;\nreturn;\nend-proc;"); Assert.Equal(9L, RpgExpressionParser.Parse("B.X").Eval(c));
    }

    [Theory]
    [InlineData("")] [InlineData("const")]
    public void Equal_byte_layouts_with_unrelated_definitions_are_not_exact(string mode)
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-ds ROOT qualified;\nX int(3);\nend-ds;\ndcl-ds A qualified;\nX int(3);\nend-ds;\ncallp GET(A);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP likeds(ROOT) " + mode + " options(*exact);\nend-pi;\nreturn;\nend-proc;"));

    [Fact]
    public void Explicit_prototype_options_apply_when_the_PI_omits_them()
    {
        var c = Run("**free\ndcl-pr GET;\nP char(4) const options(*trim:*rightadj:*exact);\nend-pr;\ndcl-s A char(8) inz('AB');\ndcl-s OUT char(4);\ncallp GET(A);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP char(4) const;\nend-pi;\neval OUT=P;\nreturn;\nend-proc;"); Assert.Equal("  AB", c.ReadValue("OUT"));
    }

    [Theory]
    [InlineData("char(4) options(*trim)")]
    [InlineData("char(4) options(*rightadj)")]
    [InlineData("packed(5:2) const options(*trim)")]
    [InlineData("int(3) value options(*rightadj)")]
    [InlineData("char(4) const options(*trim:*trim)")]
    [InlineData("char(4) const options(*exact:*exact)")]
    public void Invalid_parameter_option_combinations_reject(string formal) => Assert.Throws<RpgCompileException>(() => Run(Call("char(4)", formal)));

    [Fact]
    public void Transformed_host_arguments_are_read_only_and_expire()
    {
        ProgramArgument? saved = null;
        Run("**free\ndcl-pr RUN extpgm('TARGET');\nP char(4) const options(*trim:*rightadj:*exact);\nend-pr;\ncallp RUN('AB');", new() { TypedProgramCaller = (_, _, _, borrow) => {
            saved = Assert.IsType<ProgramArgument>(borrow!().Single()); Assert.True(saved.IsReadOnly); Assert.Equal("  AB", saved.ToBuffer()!.ToText());
            Assert.Throws<CpfException>(() => saved.Value = "XXXX"); return new() { Success = true };
        } }); Assert.Throws<ObjectDisposedException>(() => saved!.ToBuffer());
    }
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Fixed_prototype_columns_apply_character_options(int shift)
    {
        var source = RpgFixedStructureTests.D("RUN", "PR", keywords: "EXTPGM('TARGET')", shift: shift) +
            RpgFixedStructureTests.D("P", length: "4", keywords: "CONST OPTIONS(*TRIM:*RIGHTADJ:*EXACT)", shift: shift) +
            RpgFixedCalculationTests.C("CALLP", expression: "RUN(' AB ')");
        Run(source, new() { TypedProgramCaller = (_, _, _, borrow) => { Assert.Equal("  AB", Assert.IsType<ProgramArgument>(borrow!().Single()).ToBuffer()!.ToText()); return new() { Success = true }; } });
    }

    [Fact]
    public void Variable_character_arrays_transform_actual_widths_and_default_missing_elements()
    {
        var c = Run("**free\ndcl-s A char(8) dim(2) inz(' AB');\ndcl-s OUT char(4);\ndcl-s LAST char(4);\ncallp GET(A);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP char(4) dim(3) const options(*varsize:*trim:*rightadj);\nend-pi;\neval OUT=P(2);\neval LAST=P(3);\nreturn;\nend-proc;"); Assert.Equal("  AB", c.ReadValue("OUT")); Assert.Equal("    ", c.ReadValue("LAST"));
    }

    [Fact]
    public void Optional_omitted_and_RTNPARM_positions_survive_character_options()
    {
        var c = Run("**free\ndcl-s N int(3);\neval N=GET(*omit)+GET('AB':' X ');\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) rtnparm;\nP char(4) const options(*trim:*rightadj:*exact:*omit);\nQ char(4) const options(*trim:*nopass);\nend-pi;\nif %passed(P) and %parms()=3 and P='  AB' and Q='X';\nreturn 7;\nendif;\nif not %passed(P) and %parms()=2;\nreturn 3;\nendif;\nreturn 0;\nend-proc;"); Assert.Equal(10L, c.ReadValue("N"));
    }

    [Fact]
    public void Returned_related_structure_metadata_satisfies_EXACT()
    {
        var c = Run("**free\ndcl-ds ROOT qualified;\nX int(3) inz(7);\nend-ds;\ndcl-s N int(3);\neval N=GET(MAKE());\nreturn;\ndcl-proc MAKE;\ndcl-pi *n likeds(ROOT) rtnparm end-pi;\nreturn ROOT;\nend-proc;\ndcl-proc GET;\ndcl-pi *n int(3);\nP likeds(ROOT) const options(*exact);\nend-pi;\nreturn P.X;\nend-proc;"); Assert.Equal(7L, c.ReadValue("N"));
    }

    [Fact]
    public void Local_shadowed_structure_names_do_not_share_an_exact_origin()
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-ds ROOT qualified;\nX int(3);\nend-ds;\ncallp OUTER();\nreturn;\ndcl-proc OUTER;\ndcl-pi *n end-pi;\ndcl-ds ROOT qualified;\nX int(3);\nend-ds;\ncallp GET(ROOT);\nreturn;\nend-proc;\ndcl-proc GET;\ndcl-pi *n;\nP likeds(ROOT) const options(*exact);\nend-pi;\nreturn;\nend-proc;"));

    [Theory]
    [InlineData(37, "400040C140", "40400040C1")]
    [InlineData(1208, "2000204120", "2020002041")]
    public void Opaque_transforms_remove_encoded_blanks_and_preserve_null_bytes(int ccsid, string input, string expected)
    {
        Run("**free\ndcl-pr RUN extpgm('TARGET');\nP char(5) ccsid(*hex) const options(*trim:*rightadj:*exact);\nend-pr;\ndcl-s A char(5) ccsid(*hex) inz(x'" + input + "');\ncallp RUN(A);", new() { Ccsid = () => ccsid, TypedProgramCaller = (_, _, _, borrow) => { Assert.Equal(expected, Convert.ToHexString(Assert.IsType<ProgramArgument>(borrow!().Single()).ToBuffer()!.ToArray())); return new() { Success = true }; } });
    }

    [Fact]
    public void Exact_writable_output_receivers_do_not_require_valid_incoming_numeric_data()
    {
        Run("**free\ndcl-pr RUN extpgm('TARGET');\nP packed(5:0) options(*exact);\nend-pr;\ndcl-ds RAW qualified;\nP packed(5:0);\nend-ds;\neval RAW=x'FFFFFF';\ncallp RUN(RAW.P);", new() { TypedProgramCaller = (_, _, _, borrow) => { var arg = Assert.IsType<ProgramArgument>(borrow!().Single()); arg.Value = 12m; return new() { Success = true }; } });
    }

    [Fact]
    public void Exact_rejects_oversize_literals_before_external_dispatch()
    {
        var called = false;
        var error = Assert.Throws<RpgRuntimeException>(() => Run("**free\ndcl-pr RUN extpgm('TARGET');\nP char(4) const options(*exact);\nend-pr;\ncallp RUN('ABCDE');", new() { TypedProgramCaller = (_, _, _, _) => { called = true; return new() { Success = true }; } })); Assert.Equal(105, error.StatusCode); Assert.False(called);
    }

    [Fact]
    public void Transformed_temporaries_are_admitted_before_dispatch()
    {
        var called = false;
        var error = Assert.Throws<CpfException>(() => Run("**free\ndcl-pr RUN extpgm('TARGET');\nP char(32767) dim(32) const options(*trim:*varsize);\nQ char(32767) dim(32) const options(*trim:*varsize);\nend-pr;\ndcl-s A char(1) dim(1) inz('A');\ncallp RUN(A:A);", new() { TypedProgramCaller = (_, _, _, _) => { called = true; return new() { Success = true }; } })); Assert.Equal("CPF3C3A", error.MessageId); Assert.False(called);
    }

    [Fact]
    public void Named_PR_PI_array_order_must_agree()
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-pr GET;\nP char(4) dim(2) descend const options(*exact);\nend-pr;\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP char(4) dim(2) ascend const;\nend-pi;\nreturn;\nend-proc;"));

    [Fact]
    public void Unrelated_structure_return_metadata_rejects_during_compilation()
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-ds ROOT qualified;\nX int(3);\nend-ds;\ndcl-ds OTHER qualified;\nX int(3);\nend-ds;\ncallp GET(MAKE());\nreturn;\ndcl-proc MAKE;\ndcl-pi *n likeds(OTHER) end-pi;\nreturn OTHER;\nend-proc;\ndcl-proc GET;\ndcl-pi *n;\nP likeds(ROOT) const options(*exact);\nend-pi;\nreturn;\nend-proc;"));

    [Fact]
    public void Exact_structure_arrays_require_equal_dimensions()
        => Assert.Throws<RpgCompileException>(() => Run("**free\ndcl-ds ROOT qualified;\nX int(3);\nend-ds;\ndcl-ds A likeds(ROOT) dim(3);\ncallp GET(A);\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP likeds(ROOT) dim(2) const options(*exact);\nend-pi;\nreturn;\nend-proc;"));

    [Fact]
    public void Array_expressions_do_not_inherit_a_declared_sort_order()
    {
        var c = Run("**free\ndcl-s A int(3) dim(2) descend inz(1);\ndcl-s N int(3);\nmonitor;\ncallp GET(A+0);\non-error 105;\neval N=1;\nendmon;\nreturn;\ndcl-proc GET;\ndcl-pi *n;\nP int(3) dim(2) descend const options(*exact);\nend-pi;\neval N=2;\nreturn;\nend-proc;"); Assert.Equal(1L, c.ReadValue("N"));
    }

}
