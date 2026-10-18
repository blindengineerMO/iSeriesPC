using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgOccurrenceTests
{
    private const string Declaration = "dcl-ds BANK qualified occurs(3) inz;\nTEXT char(2) inz('AB');\nNUMBER packed(3:0) inz(17);\nFLAGS ind dim(2);\nend-ds;\n";
    private static RpgProgram Compile(string body) => RpgCompiler.Compile("QGPL", "OCCURS", "**free\n" + body);
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private static RpgRuntimeContext Run(string body, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(body));

    [Fact]
    public void Whole_assignment_and_SUBST_address_only_the_current_occurrence()
    {
        var context = Run("dcl-ds BANK qualified occurs(2);\nTEXT char(4) inz('ABCD');\nend-ds;\n" +
            "dcl-s SECOND char(4);\neval %occur(BANK)=2;\neval BANK='EFGH';\neval %subst(BANK:2:2)='XY';\n" +
            "eval SECOND=BANK.TEXT;\neval %occur(BANK)=1;\nreturn;");
        Assert.Equal("EXYH", context.ReadValue("SECOND")); Assert.Equal("ABCD", Read(context, "BANK.TEXT"));
    }
    [Fact]
    public void Occurrence_error_updates_PSDS_before_MONITOR_recovery()
    {
        var context = Run(Declaration + "dcl-ds STATUS qualified psds;\nCODE *status;\nend-ds;\n" +
            "monitor;\neval %occur(BANK)=4;\non-error 122;\neval BANK.NUMBER=STATUS.CODE;\nendmon;\nreturn;");
        Assert.Equal(122m, Read(context, "BANK.NUMBER")); Assert.Equal(122m, Read(context, "STATUS.CODE"));
    }
    [Fact]
    public void Cancelled_CLEAR_ALL_preserves_selection_and_every_occurrence()
    {
        using var stop = new CancellationTokenSource(); var host = new RpgHost { CancellationToken = stop.Token };
        var context = new RpgInterpreter(host).Run(Compile(Declaration + "eval %occur(BANK)=2;\nreturn;"));
        var before = Assert.IsType<ProgramBuffer>(Read(context, "BANK")).ToArray(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => context.ClearValue("BANK", true));
        Assert.Equal(2, Read(context, "%occur(BANK)")); Assert.Equal(before, Assert.IsType<ProgramBuffer>(Read(context, "BANK")).ToArray());
    }
    [Fact]
    public void Aggregate_storage_bound_includes_every_occurrence()
    {
        var declarations = string.Join('\n', Enumerable.Range(0, 17).Select(i =>
            $"dcl-ds BANK{i} qualified occurs(32) len(32767);\nTEXT char(1);\nend-ds;"));
        Assert.Contains("16 MiB", Assert.Throws<RpgCompileException>(() => Compile(declarations)).Message);
    }

    [Theory]
    [InlineData(37, "C1C2017CF0F0")]
    [InlineData(1208, "4142017C3030")]
    public void Each_occurrence_has_independent_native_bytes_and_initializers(int ccsid, string expected)
    {
        var program = Compile(Declaration + "return;"); var host = new RpgHost { Ccsid = () => ccsid };
        var context = new RpgInterpreter(host).Run(program);
        Assert.Equal(6, Read(context, "%size(BANK)")); Assert.Equal(18, Read(context, "%size(BANK:*all)"));
        Assert.Equal(1, Read(context, "%occur(BANK)")); Assert.Equal(2, Read(context, "%elem(BANK.FLAGS)"));
        Assert.Equal(expected, Convert.ToHexString(Assert.IsType<ProgramBuffer>(Read(context, "BANK")).ToArray()));
        var changed = Compile(Declaration + "eval %occur(BANK)=2;\neval BANK.TEXT='CD';\neval BANK.NUMBER=42;\neval BANK.FLAGS(2)=*on;\nreturn;");
        new RpgInterpreter(host).Run(changed, existingContext: context);
        Assert.Equal(2, Read(context, "%occur(BANK)")); Assert.Equal("CD", Read(context, "BANK.TEXT")); Assert.Equal(42m, Read(context, "BANK.NUMBER"));
        var select = Compile(Declaration + "eval %occur(BANK)=3;\nreturn;"); new RpgInterpreter(host).Run(select, existingContext: context);
        Assert.Equal(expected, Convert.ToHexString(Assert.IsType<ProgramBuffer>(Read(context, "BANK")).ToArray()));
    }
    [Fact]
    public void Unqualified_fields_and_arrays_follow_selection_without_becoming_DS_arrays()
    {
        var context = Run("dcl-ds BANK occurs(2) inz;\nTEXT char(2);\nFLAGS ind dim(2);\nend-ds;\n" +
            "eval TEXT='AA';\neval FLAGS(1)=*on;\neval %occur(BANK)=2;\neval TEXT='BB';\nreturn;");
        Assert.Equal("BB", context.ReadValue("TEXT")); Assert.False(Assert.IsType<bool>(Read(context, "FLAGS(1)")));
        Assert.Equal(2, Read(context, "%elem(BANK)"));
        Assert.Equal(121, Assert.Throws<RpgRuntimeException>(() => Read(context, "BANK(1)")).StatusCode);
    }
    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("9999999999999999999999999999")]
    public void Out_of_range_selection_reports_122_without_changing_current_occurrence(string value)
    {
        var messages = new List<string>();
        var context = new RpgInterpreter(new() { SendMessage = messages.Add }).Run(Compile(Declaration +
            "eval %occur(BANK)=2;\nmonitor;\neval %occur(BANK)=" + value + ";\non-error 122;\nsndpgmmsg %char(%status());\nendmon;\nreturn;"));
        Assert.Equal(new[] { "122" }, messages); Assert.Equal(2, Read(context, "%occur(BANK)"));
    }
    [Theory]
    [InlineData("'2'")]
    [InlineData("*on")]
    [InlineData("1.5")]
    [InlineData("2.0")]
    [InlineData("SCALED")]
    [InlineData("+SCALED")]
    [InlineData("SCALED+1")]
    [InlineData("BANK.NUMBER/2")]
    [InlineData("FLOATING")]
    [InlineData("%abs(SMALL.VALUE)")]
    [InlineData("%dec(2:3:1)")]
    public void Selection_requires_zero_scale_nonfloat_numeric_values(string value)
    {
        var context = Run(Declaration + "dcl-s SCALED packed(3:1) inz(1);\ndcl-s FLOATING float(8) inz(2);\n" +
            "dcl-ds SMALL qualified inz;\nVALUE float(4) inz(2);\nend-ds;\n" +
            "monitor;\neval %occur(BANK)=" + value + ";\non-error 105;\neval BANK.TEXT='OK';\nendmon;\nreturn;");
        Assert.Equal("OK", Read(context, "BANK.TEXT")); Assert.Equal(1, Read(context, "%occur(BANK)"));
    }
    [Theory]
    [InlineData("%occur(BANK)+1")]
    [InlineData("%int(2.0)")]
    [InlineData("%elem(BANK.FLAGS)")]
    public void Integer_expressions_can_select_an_occurrence(string value)
    {
        Assert.Equal(2, Read(Run(Declaration + "eval %occur(BANK)=" + value + ";\nreturn;"), "%occur(BANK)"));
    }
    [Theory]
    [InlineData("%occur()")]
    [InlineData("%occur(BANK:1)")]
    [InlineData("%occur(BANK.TEXT)")]
    [InlineData("%occur(UNKNOWN)")]
    public void Invalid_OCCUR_operands_have_program_status_105(string expression)
    {
        var context = Run(Declaration + "monitor;\neval BANK.NUMBER=" + expression + ";\non-error 105;\neval BANK.TEXT='OK';\nendmon;\nreturn;");
        Assert.Equal("OK", Read(context, "BANK.TEXT"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CLEAR_uses_type_defaults_and_ALL_also_resets_the_current_occurrence(bool all)
    {
        var context = Run(Declaration + "dcl-s FIRST char(2);\ndcl-s SECOND char(2);\ndcl-s SELECTED int(10);\n" +
            "eval BANK.TEXT='AA';\neval %occur(BANK)=2;\neval BANK.TEXT='BB';\nclear " + (all ? "*all " : "") + "BANK;\n" +
            "eval SELECTED=%occur(BANK);\neval SECOND=BANK.TEXT;\neval %occur(BANK)=1;\neval FIRST=BANK.TEXT;\nreturn;");
        Assert.Equal(all ? 1L : 2L, context.ReadValue("SELECTED")); Assert.Equal("  ", context.ReadValue("SECOND"));
        Assert.Equal(all ? "  " : "AA", context.ReadValue("FIRST"));
        Assert.Equal(all ? 0m : 17m, Read(context, "BANK.NUMBER"));
    }
    [Fact]
    public void CLEAR_of_a_subfield_array_changes_only_the_selected_occurrence()
    {
        var context = Run(Declaration + "dcl-s SAVED ind;\neval BANK.FLAGS=*on;\neval %occur(BANK)=2;\neval BANK.FLAGS=*on;\n" +
            "clear BANK.FLAGS;\neval SAVED=BANK.FLAGS(2);\neval %occur(BANK)=1;\nreturn;");
        Assert.False(Assert.IsType<bool>(context.ReadValue("SAVED"))); Assert.True(Assert.IsType<bool>(Read(context, "BANK.FLAGS(2)")));
    }
    [Fact]
    public void LIKEDS_copies_layout_but_requires_its_own_OCCURS_and_initialization()
    {
        var context = Run(Declaration + "dcl-ds COPY likeds(BANK);\ndcl-ds MANY likeds(BANK) occurs(2) inz(*likeds);\n" +
            "eval %occur(MANY)=2;\nreturn;");
        Assert.Equal(6, Read(context, "%size(COPY)")); Assert.Equal(12, Read(context, "%size(MANY:*all)"));
        Assert.Equal("AB", Read(context, "MANY.TEXT")); Assert.Equal(17m, Read(context, "MANY.NUMBER"));
        Assert.Equal(105, Assert.Throws<RpgRuntimeException>(() => Read(context, "%occur(COPY)")).StatusCode);
        Assert.All(Assert.IsType<ProgramBuffer>(Read(context, "COPY")).ToArray(), b => Assert.Equal(0x40, b));
    }
    [Theory]
    [InlineData("occurs(0)")]
    [InlineData("occurs(-1)")]
    [InlineData("occurs(1.5)")]
    [InlineData("occurs(32768)")]
    [InlineData("occurs")]
    [InlineData("occurs(2) occurs(3)")]
    [InlineData("occurs(2) dim(2)")]
    [InlineData("occurs(2) psds")]
    [InlineData("occurs(32767) len(100)")]
    public void Invalid_occurrence_declarations_fail_at_the_original_line(string options)
    {
        var error = Assert.Throws<RpgCompileException>(() => Compile("dcl-ds BANK qualified " + options + ";\nTEXT char(2);\nend-ds;\nreturn;"));
        Assert.Equal(2, error.Location!.Line);
    }
    [Theory]
    [InlineData("dcl-ds BANK qualified;\nTEXT char(2);\nend-ds;", "BANK")]
    [InlineData("dcl-ds BANK qualified dim(2);\nTEXT char(2);\nend-ds;", "BANK")]
    [InlineData(Declaration, "BANK.TEXT")]
    [InlineData(Declaration, "BANK(1)")]
    public void CLEAR_ALL_requires_a_whole_multiple_occurrence_structure(string declaration, string target)
    {
        Assert.Throws<RpgCompileException>(() => Compile(declaration + "\nclear *all " + target + ";\nreturn;"));
    }
    [Fact]
    public void Borrowed_whole_structure_contains_all_occurrences_and_subfield_address_is_frozen()
    {
        ProgramArgument? captured = null; RpgRuntimeContext? context = null;
        var program = Compile(Declaration + "eval %occur(BANK)=2;\ncallp MODIFY(BANK:BANK.TEXT);\nreturn;");
        var host = new RpgHost { TypedProgramCaller = (_, _, values, borrow) =>
        {
            Assert.Equal(string.Concat(Enumerable.Repeat("C1C2017CF0F0", 3)), Convert.ToHexString(Assert.IsType<ProgramBuffer>(values[0]).ToArray()));
            // Change the current occurrence before lazy borrowing: actual addresses are captured at call entry.
            context!.ClearValue("BANK", true);
            var args = borrow!(); var root = Assert.IsType<ProgramArgument>(args[0]); captured = root;
            var text = Assert.IsType<ProgramArgument>(args[1]);
            Assert.Equal(18, root.Length); Assert.Equal(0, root.Storage!.Offset); Assert.Equal(6, text.Storage!.Offset);
            Assert.True(ProgramArgument.Overlaps(root, text)); text.Value = "CD";
            Assert.Equal(0xC3, root.ToBuffer()!.ToArray()[6]);
            return new() { Success = true };
        } };
        context = new(program, host); new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(1, Read(context, "%occur(BANK)")); Assert.Equal("  ", Read(context, "BANK.TEXT"));
        Assert.Throws<ObjectDisposedException>(() => captured!.ToBuffer());
    }
    [Fact]
    public void Copied_host_arguments_and_writeback_use_all_occurrences_with_the_original_subfield_address()
    {
        RpgRuntimeContext? context = null;
        var program = Compile(Declaration + "eval %occur(BANK)=2;\ncallp MODIFY(BANK:BANK.TEXT);\nreturn;");
        var host = new RpgHost { ProgramCaller = (_, _, values) =>
        {
            var root = Assert.IsType<ProgramBuffer>(values[0]); Assert.Equal(18, root.Length);
            context!.ClearValue("BANK", true);
            return new() { Success = true, UpdatedParameters = [root, "CD"] };
        } };
        context = new(program, host); new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(1, Read(context, "%occur(BANK)")); Assert.Equal("AB", Read(context, "BANK.TEXT"));
        Assert.Equal(17m, Read(context, "BANK.NUMBER"));
        var select = Compile(Declaration + "eval %occur(BANK)=2;\nreturn;");
        new RpgInterpreter(host).Run(select, existingContext: context); Assert.Equal("CD", Read(context, "BANK.TEXT"));
    }
}
