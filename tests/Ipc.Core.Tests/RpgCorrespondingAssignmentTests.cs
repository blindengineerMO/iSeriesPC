using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgCorrespondingAssignmentTests
{
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "CORRESPOND", "**free\nctl-opt alwnull(*usrctl);\n" + source));
    private static object? Read(RpgRuntimeContext context, string text) => RpgExpressionParser.Parse(text).Eval(context);

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Correspondence_uses_names_compatible_types_and_source_order_instead_of_byte_positions(int ccsid)
    {
        var context = Run("dcl-ds FROMDATA qualified inz;\nN packed(5:2);\nTEXT char(3);\nDIFFERENT char(3);\nend-ds;\ndcl-ds TODATA qualified inz;\nTEXT char(5);\nUNMATCHED char(2) inz('ZZ');\nN int(10);\nDIFFERENT int(10) inz(8);\nend-ds;\neval FROMDATA.N=12.75;\neval FROMDATA.TEXT='ABC';\neval-corr TODATA=FROMDATA;", ccsid);
        Assert.Equal(12L, Read(context, "TODATA.N")); Assert.Equal("ABC  ", Read(context, "TODATA.TEXT"));
        Assert.Equal("ZZ", Read(context, "TODATA.UNMATCHED")); Assert.Equal(8L, Read(context, "TODATA.DIFFERENT"));
    }
    [Theory]
    [InlineData(false, -12L)] [InlineData(true, -13L)]
    public void Half_adjust_applies_to_selected_numeric_assignments(bool halfAdjust, long expected)
    {
        var context = Run("dcl-ds A qualified inz;\nN packed(5:2);\nend-ds;\ndcl-ds B qualified inz;\nN int(10);\nend-ds;\neval A.N=-12.50;\neval-corr" + (halfAdjust ? "(H)" : "") + " B=A;"); Assert.Equal(expected, Read(context, "B.N"));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Null_indicators_copy_clear_or_are_ignored_according_to_each_corresponding_leaf(int ccsid)
    {
        var context = Run("dcl-ds A qualified inz;\nBOTH char(3) nullind;\nONLYSOURCE char(3) nullind;\nONLYTARGET char(3);\nDIFFERENT char(3) nullind;\nend-ds;\ndcl-ds B qualified inz;\nBOTH char(3) nullind;\nONLYSOURCE char(3);\nONLYTARGET char(3) nullind;\nDIFFERENT int(3) nullind;\nend-ds;\neval A.BOTH='ABC';\neval %nullind(A.BOTH)=*on;\neval %nullind(A.ONLYSOURCE)=*on;\neval %nullind(B.ONLYTARGET)=*on;\neval %nullind(B.DIFFERENT)=*on;\neval-corr B=A;", ccsid);
        Assert.Equal("ABC", Read(context, "B.BOTH")); Assert.Equal(true, Read(context, "%nullind(B.BOTH)")); Assert.Equal(false, Read(context, "%nullind(B.ONLYTARGET)")); Assert.Equal(true, Read(context, "%nullind(B.DIFFERENT)"));
    }
    [Theory]
    [InlineData(2, 4)] [InlineData(4, 2)]
    public void Unequal_subfield_arrays_update_only_the_common_prefix_and_preserve_tail_flags(int sourceCount, int targetCount)
    {
        var context = Run($"dcl-ds A qualified inz;\nN int(3) dim({sourceCount}) nullind inz(3);\nend-ds;\ndcl-ds B qualified inz;\nN int(3) dim({targetCount}) nullind inz(8);\nend-ds;\neval %nullind(A.N(2))=*on;\neval %nullind(B.N({targetCount}))=*on;\neval-corr B=A;");
        Assert.Equal(3L, Read(context, "B.N(1)")); Assert.Equal(true, Read(context, "%nullind(B.N(2))"));
        Assert.Equal(targetCount > sourceCount ? 8L : 3L, Read(context, $"B.N({targetCount})")); Assert.Equal(true, Read(context, $"%nullind(B.N({targetCount}))"));
    }
    [Fact]
    public void Scalar_array_subfield_mismatches_do_not_correspond()
    {
        var context = Run("dcl-ds A qualified inz;\nN int(3) dim(2) inz(3);\nend-ds;\ndcl-ds B qualified inz;\nN int(3) inz(8);\nend-ds;\neval-corr B=A;"); Assert.Equal(8L, Read(context, "B.N"));
    }
    [Fact]
    public void Nested_structures_recurse_by_local_names_and_ignore_structure_scalar_mismatches()
    {
        var context = Run("dcl-ds A qualified inz;\ndcl-ds CHILD;\nN int(3) nullind;\nTEXT char(2);\nend-ds;\nMISMATCH char(2);\nend-ds;\ndcl-ds B qualified inz;\ndcl-ds CHILD;\nTEXT char(4);\nN packed(3:0) nullind;\nend-ds;\ndcl-ds MISMATCH;\nN int(3) inz(8);\nend-ds;\nend-ds;\neval A.CHILD.N=7;\neval A.CHILD.TEXT='AB';\neval %nullind(A.CHILD.N)=*on;\neval-corr B=A;");
        Assert.Equal(7m, Read(context, "B.CHILD.N")); Assert.Equal("AB  ", Read(context, "B.CHILD.TEXT")); Assert.Equal(true, Read(context, "%nullind(B.CHILD.N)")); Assert.Equal(8L, Read(context, "B.MISMATCH.N"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Structure_array_slices_copy_common_elements_or_broadcast_a_scalar(bool broadcast)
    {
        var context = Run("dcl-ds A qualified dim(3) inz;\nN int(3) nullind;\nend-ds;\ndcl-ds B qualified dim(4) inz;\nN packed(3:0) nullind;\nend-ds;\neval A(2).N=7;\neval A(3).N=9;\neval %nullind(A(2).N)=*on;\neval-corr %subarr(B:2:3)=" + (broadcast ? "A(2)" : "%subarr(A:2:2)") + ";");
        Assert.Equal(0m, Read(context, "B(1).N")); Assert.Equal(7m, Read(context, "B(2).N")); Assert.Equal(true, Read(context, "%nullind(B(2).N)"));
        Assert.Equal(broadcast ? 7m : 9m, Read(context, "B(3).N")); Assert.Equal(broadcast ? 7m : 0m, Read(context, "B(4).N"));
    }
    [Fact]
    public void Same_layout_moves_the_complete_image_and_snapshots_overlapping_array_slices()
    {
        var context = Run("dcl-ds MODEL qualified template;\nN int(3) nullind;\n*n char(2);\nend-ds;\ndcl-ds A likeds(MODEL) dim(3) inz;\neval A(1).N=3;\neval A(2).N=7;\neval %nullind(A(1).N)=*on;\neval-corr %subarr(A:2:2)=%subarr(A:1:2);");
        Assert.Equal(3L, Read(context, "A(2).N")); Assert.Equal(7L, Read(context, "A(3).N")); Assert.Equal(true, Read(context, "%nullind(A(2).N)")); Assert.Equal(false, Read(context, "%nullind(A(3).N)"));
    }
    [Fact]
    public void Source_declaration_order_controls_destination_overlays()
    {
        var context = Run("dcl-ds A qualified inz;\nFIRST char(2);\nSECOND char(2);\nend-ds;\ndcl-ds B qualified inz;\nSECOND char(2);\nFIRST char(2) overlay(SECOND);\nend-ds;\neval A.FIRST='AA';\neval A.SECOND='ZZ';\neval-corr B=A;"); Assert.Equal("ZZ", Read(context, "B.FIRST"));
    }
    [Fact]
    public void Named_indicators_follow_live_source_order_when_they_are_also_corresponding_data()
    {
        var context = Run("dcl-ds A qualified inz;\nN int(3) nullind(FLAG);\nFLAG ind;\nend-ds;\ndcl-ds B qualified inz;\nN int(10) nullind(FLAG);\nFLAG ind;\nend-ds;\neval A.N=7;\neval A.FLAG=*on;\neval-corr B=A;"); Assert.Equal(7L, Read(context, "B.N")); Assert.Equal(true, Read(context, "B.FLAG"));
    }
    [Fact]
    public void Unqualified_targets_match_local_source_names()
    {
        var context = Run("dcl-ds A qualified inz;\nN int(3);\nend-ds;\ndcl-ds B inz;\nN int(10);\nend-ds;\neval A.N=11;\neval-corr B=A;"); Assert.Equal(11L, Read(context, "N"));
    }
    [Fact]
    public void Current_occurrences_are_selected_independently()
    {
        var context = Run("dcl-s OTHER int(10);\ndcl-ds A qualified occurs(2) inz;\nN int(3) nullind;\nend-ds;\ndcl-ds B qualified occurs(3) inz;\nN int(10) nullind;\nend-ds;\neval %occur(A)=2;\neval %occur(B)=3;\neval A.N=13;\neval %nullind(A.N)=*on;\neval-corr B=A;\neval %occur(B)=1;\neval OTHER=B.N;\neval %occur(B)=3;");
        Assert.Equal(0L, Read(context, "OTHER")); Assert.Equal(13L, Read(context, "B.N")); Assert.Equal(true, Read(context, "%nullind(B.N)"));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Native_fixed_extended_factor_two_supports_corresponding_assignment(int ccsid)
    {
        var source = "       dcl-ds A qualified inz;\n         N int(3);\n       end-ds;\n       dcl-ds B qualified inz;\n         N int(10);\n       end-ds;\n" + RpgFixedCalculationTests.C("EVAL", expression: "A.N=7") + RpgFixedCalculationTests.C("EVAL-CORR", expression: "B=A");
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "FIXCORR", source)); Assert.Equal(7L, Read(context, "B.N"));
    }
    [Fact]
    public void Borrowed_maps_and_data_are_forwarded_through_corresponding_assignments()
    {
        var context = Run("dcl-ds MODEL qualified template;\nN int(3) nullind;\nend-ds;\ndcl-ds A likeds(MODEL) inz;\ndcl-ds B likeds(MODEL) inz;\neval A.N=9;\neval %nullind(A.N)=*on;\ncallp COPY(B:A);\nreturn;\ndcl-proc COPY;\ndcl-pi *n;\nP likeds(MODEL) options(*nullind);\nQ likeds(MODEL) const options(*nullind);\nend-pi;\neval-corr P=Q;\nend-proc;"); Assert.Equal(9L, Read(context, "B.N")); Assert.Equal(true, Read(context, "%nullind(B.N)"));
    }
    [Fact]
    public void Subscript_procedures_are_evaluated_once_for_each_operand()
    {
        var context = Run("dcl-s COUNT int(10);\ndcl-ds A qualified dim(2) inz;\nN int(3) inz(7);\nend-ds;\ndcl-ds B qualified dim(2) inz;\nN int(10);\nend-ds;\neval-corr B(NEXT())=A(NEXT());\nreturn;\ndcl-proc NEXT;\ndcl-pi *n int(10) end-pi;\neval COUNT=COUNT+1;\nreturn COUNT;\nend-proc;"); Assert.Equal(2L, Read(context, "COUNT")); Assert.Equal(7L, Read(context, "B(2).N")); Assert.Equal(0L, Read(context, "B(1).N"));
    }
    [Fact]
    public void Unmatched_invalid_numeric_data_is_never_decoded()
    {
        var context = Run("dcl-ds A qualified inz;\nN packed(5:0);\nRAW char(3) overlay(N);\nTEXT char(2);\nend-ds;\ndcl-ds B qualified inz;\nTEXT char(3);\nend-ds;\neval A.RAW='BAD';\neval A.TEXT='OK';\neval-corr B=A;"); Assert.Equal("OK ", Read(context, "B.TEXT"));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Related_layout_optimization_moves_padding_without_decoding_overlaid_numeric_bytes(int ccsid)
    {
        var context = Run("dcl-ds MODEL qualified template;\nN packed(5:0) pos(3);\nend-ds;\ndcl-ds A likeds(MODEL) inz;\ndcl-ds B likeds(MODEL) inz;\neval %subst(A:1:5)='XXBAD';\neval-corr B=A;", ccsid);
        Assert.Equal("XXBAD", Assert.IsType<ProgramBuffer>(Read(context, "B")).ToText());
    }
    [Fact]
    public void A_later_conversion_error_preserves_completed_earlier_field_assignments()
    {
        var context = Run("dcl-ds A qualified inz;\nTEXT char(2);\nN packed(5:0);\nRAW char(3) overlay(N);\nend-ds;\ndcl-ds B qualified inz;\nTEXT char(2);\nN int(10) inz(8);\nend-ds;\neval A.TEXT='OK';\neval A.RAW='BAD';\nmonitor;\neval-corr B=A;\non-error;\nendmon;"); Assert.Equal("OK", Read(context, "B.TEXT")); Assert.Equal(8L, Read(context, "B.N"));
    }
    [Fact]
    public void Unused_source_array_tail_is_not_decoded()
    {
        var context = Run("dcl-ds A qualified inz;\nN packed(5:0) dim(2);\nRAW char(3) overlay(N);\nend-ds;\ndcl-ds B qualified inz;\nN packed(3:0) dim(1);\nend-ds;\neval A.N(1)=7;\neval A.RAW(2)='BAD';\neval-corr B=A;"); Assert.Equal(7m, Read(context, "B.N(1)"));
    }
    [Theory]
    [InlineData("1", true)] [InlineData("X", false)]
    public void Character_indicator_assignments_use_native_logical_validation(string input, bool expected)
    {
        var context = Run("dcl-ds A qualified inz;\nFLAG char(1);\nend-ds;\ndcl-ds B qualified inz;\nFLAG ind;\nend-ds;\neval A.FLAG='" + input + "';\nmonitor;\neval-corr B=A;\non-error 105;\nendmon;"); Assert.Equal(expected, Read(context, "B.FLAG"));
    }
    [Fact]
    public void Half_adjust_numeric_overflow_has_the_native_monitor_status_and_preserves_the_field()
    {
        var context = Run("dcl-s CAUGHT ind;\ndcl-ds A qualified inz;\nN float(8);\nend-ds;\ndcl-ds B qualified inz;\nN packed(5:0) inz(8);\nend-ds;\neval A.N=1E100;\nmonitor;\neval-corr(H) B=A;\non-error 103;\neval CAUGHT=*on;\nendmon;"); Assert.Equal(8m, Read(context, "B.N")); Assert.Equal(true, Read(context, "CAUGHT"));
    }
    [Theory]
    [InlineData("eval-corr N=A;")] [InlineData("eval-corr A=N;")]
    [InlineData("eval-corr A=A+A;")] [InlineData("eval-corr A A;")]
    [InlineData("eval-corr %subarr(A:1)=A;")] [InlineData("eval-corr(M) A=A;")]
    [InlineData("eval-corr(E) A=A;")] [InlineData("eval-corr(HH) A=A;")]
    public void Invalid_operands_and_extenders_fail_at_the_source_statement(string statement)
    {
        var error = Assert.Throws<RpgCompileException>(() => Run("dcl-ds A qualified inz;\nN int(3);\nend-ds;\ndcl-s N int(3);\n" + statement)); Assert.Equal(7, error.Location!.Line);
    }
    [Fact]
    public void An_array_source_requires_an_array_target()
    {
        Assert.Throws<RpgCompileException>(() => Run("dcl-ds A qualified dim(2) inz;\nN int(3);\nend-ds;\ndcl-ds B qualified inz;\nN int(3);\nend-ds;\neval-corr B=A;"));
    }
    [Fact]
    public void Const_targets_reject_before_execution()
    {
        Assert.Throws<RpgCompileException>(() => Run("dcl-ds A qualified inz;\nN int(3);\nend-ds;\ncallp CHANGE(A);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP likeds(A) const;\nend-pi;\neval-corr P=A;\nend-proc;"));
    }
}
