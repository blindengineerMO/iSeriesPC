using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed partial class RpgNativeStructureTests
{
    private static RpgRuntimeContext Run(string body, int ccsid = 37) => new RpgInterpreter(new RpgHost { Ccsid = () => ccsid }).Run(RpgCompiler.Compile("QGPL", "NATIVEDS", "**free\n" + body));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private static string Hex(RpgRuntimeContext context, string expression)
    {
        var value = Read(context, expression);
        return value is object?[] array ? Convert.ToHexString(array.Cast<ProgramBuffer>().SelectMany(b => b.ToArray()).ToArray()) : Convert.ToHexString(Assert.IsType<ProgramBuffer>(value).ToArray());
    }

    [Theory]
    [InlineData(37, "C1C2", "F1")]
    [InlineData(1208, "4142", "31")]
    public void Qualified_fields_use_native_packed_zoned_signed_integer_and_IEEE_big_endian_bytes(int ccsid, string letters, string indicator)
    {
        var context = Run("""
            dcl-ds DATA qualified inz;
              TEXT char(2) inz('AB');
              BYTE int(3) inz(-128);
              SHORT int(5) inz(-32768);
              NUMBER int(10) inz(-2147483648);
              WIDE int(20) inz(-9223372036854775808);
              MONEY packed(5:2) inz(-12.34);
              ZONEDNUM zoned(3:1) inz(-12.3);
              SINGLE float(4) inz(1.5);
              DOUBLE float(8) inz(-2.5);
              FLAG ind inz(*on);
            end-ds     DATA;
            """, ccsid);
        Assert.Equal(letters + "80800080000000800000000000000001234DF1F2D33FC00000C004000000000000" + indicator, Hex(context, "DATA"));
        Assert.Equal(-128L, Read(context, "DATA.BYTE")); Assert.Equal(-32768L, Read(context, "DATA.SHORT"));
        Assert.Equal(long.MinValue, Read(context, "DATA.WIDE")); Assert.Equal(-12.34m, Read(context, "DATA.MONEY"));
        Assert.Equal(-12.3m, Read(context, "DATA.ZONEDNUM")); Assert.Equal(1.5f, Read(context, "DATA.SINGLE")); Assert.Equal(-2.5d, Read(context, "DATA.DOUBLE"));
    }
    [Fact]
    public void Uninitialized_numeric_subfields_keep_blank_storage_and_raise_907_in_MONITOR()
    {
        var messages = new List<string>();
        var program = RpgCompiler.Compile("QGPL", "NATIVEDS", "**free\ndcl-ds DATA qualified;\nN packed(5:2);\nend-ds;\nmonitor;\nsndpgmmsg %char(DATA.N);\non-error 907;\nsndpgmmsg 'DECIMAL ERROR';\nendmon;");
        var context = new RpgInterpreter(new RpgHost { SendMessage = messages.Add }).Run(program);
        Assert.Equal(new[] { "DECIMAL ERROR" }, messages); Assert.Equal("404040", Hex(context, "DATA"));
    }
    [Fact]
    public void Initialized_subfields_preserve_quoted_special_text_and_ordered_overlays()
    {
        var context = Run("""
            dcl-ds DATA;
              TEXT char(10) inz('*BLANKS');
              FIRST char(3) overlay(TEXT);
              SECOND char(6) overlay(TEXT:*next);
              LAST char(1) overlay(TEXT:*next);
              N packed(5:2) inz(12.34);
            end-ds;
            eval SECOND='hello!';
            """);
        Assert.Equal("*BLhello! ", context.ReadValue("TEXT")); Assert.Equal("*BL", context.ReadValue("FIRST")); Assert.Equal(12.34m, context.ReadValue("N"));
        Assert.Equal(13, Read(context, "%size(DATA)")); Assert.Equal(5, Read(context, "%len(N)")); Assert.Equal(3, Read(context, "%size(N)"));
    }
    [Fact]
    public void Structure_and_subfield_arrays_use_contiguous_storage_and_independent_indexes()
    {
        var context = Run("""
            dcl-ds DATA qualified dim(2) inz;
              VALUES int(5) dim(3);
            end-ds;
            eval DATA(2).VALUES(3)=-2;
            eval DATA(1).VALUES(1)=7;
            """);
        Assert.Equal("00070000000000000000FFFE", Hex(context, "DATA"));
        Assert.Equal(0L, Read(context, "DATA(1).VALUES(3)")); Assert.Equal(-2L, Read(context, "DATA(2).VALUES(3)"));
        Assert.Equal(6, Read(context, "%size(DATA)")); Assert.Equal(12, Read(context, "%size(DATA:*all)")); Assert.Equal(6, Read(context, "%size(DATA(1))"));
        Assert.Equal(2, Read(context, "%elem(DATA)")); Assert.Equal(3, Read(context, "%elem(DATA(2).VALUES)"));
        Assert.Equal(2, Read(context, "%size(DATA(2).VALUES(3))"));
        Assert.Equal(121, Assert.Throws<RpgRuntimeException>(() => Read(context, "DATA(3).VALUES(1)")).StatusCode);
        Assert.Equal(121, Assert.Throws<RpgRuntimeException>(() => Read(context, "DATA(1).VALUES(0)")).StatusCode);
    }
    [Fact]
    public void LIKEDS_copies_layout_and_explicitly_requested_initialization_without_aliasing()
    {
        var context = Run("""
            dcl-ds PARENT qualified;
              N packed(5:2) inz(12.34);
              NAME char(4) inz('Joe');
            end-ds;
            dcl-ds COPY likeds(PARENT) inz(*likeds);
            dcl-ds DEFAULTS likeds(PARENT) inz;
            dcl-ds BLANKS likeds(PARENT);
            eval COPY.NAME='Sam';
            """);
        Assert.Equal(12.34m, Read(context, "COPY.N")); Assert.Equal("Joe ", Read(context, "PARENT.NAME")); Assert.Equal("Sam ", Read(context, "COPY.NAME"));
        Assert.Equal(0m, Read(context, "DEFAULTS.N")); Assert.Equal("    ", Read(context, "DEFAULTS.NAME"));
        Assert.Equal(907, Assert.Throws<RpgRuntimeException>(() => Read(context, "BLANKS.N")).StatusCode);
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void POS_preserves_gaps_and_raw_assignment_changes_live_typed_overlays(int ccsid)
    {
        var context = Run("""
            dcl-ds DATA qualified len(10);
              BYTES char(4) ccsid(*hex) pos(3) inz(X'01020304');
              NUMBER int(10) overlay(BYTES);
            end-ds;
            eval DATA.NUMBER=-2;
            """, ccsid);
        var blank = ccsid == 37 ? "40" : "20";
        Assert.Equal(blank + blank + "FFFFFFFE" + string.Concat(Enumerable.Repeat(blank, 4)), Hex(context, "DATA"));
        Assert.Equal("FFFFFFFE", Hex(context, "DATA.BYTES")); Assert.Equal(-2L, Read(context, "DATA.NUMBER"));
    }
    [Fact]
    public void Numeric_overflow_preserves_the_entire_structure_and_reports_103()
    {
        var program = RpgCompiler.Compile("QGPL", "NATIVEDS", "**free\ndcl-ds DATA qualified inz;\nN int(3) inz(7);\nP packed(3:1) inz(1.2);\nend-ds;\neval DATA.N=128;");
        var host = new RpgHost(); var context = new RpgRuntimeContext(program, host);
        Assert.Equal(103, Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(host).Run(program, existingContext: context)).StatusCode);
        Assert.Equal("07012C", Hex(context, "DATA"));
    }
    [Fact]
    public void Numeric_assignment_discards_excess_fraction_without_rounding()
    {
        var context = Run("dcl-ds DATA qualified inz;\nN packed(3:1);\nend-ds;\neval DATA.N=-1.29;");
        Assert.Equal(-1.2m, Read(context, "DATA.N")); Assert.Equal("012D", Hex(context, "DATA"));
    }
    [Fact]
    public void Array_overlays_inherit_DIM_and_preserve_noncontiguous_element_stride()
    {
        var context = Run("""
            dcl-ds DATA;
              ROWS char(6) dim(3) inz('ABCDEF');
              FIRST char(2) overlay(ROWS);
              LAST char(4) overlay(ROWS:*next);
            end-ds;
            eval FIRST(2)='XY';
            eval LAST(3)='1234';
            """);
        Assert.Equal(new object?[] { "AB", "XY", "AB" }, context.ReadValue("FIRST"));
        Assert.Equal(new object?[] { "ABCDEF", "XYCDEF", "AB1234" }, context.ReadValue("ROWS"));
        Assert.Equal(3, Read(context, "%elem(LAST)")); Assert.Equal(4, Read(context, "%size(LAST)")); Assert.Equal(12, Read(context, "%size(LAST:*all)"));
    }
    [Fact]
    public void Scalar_array_assignment_and_array_copy_update_all_elements_and_preserve_other_storage()
    {
        var context = Run("""
            dcl-ds DATA qualified inz;
              A int(5) dim(3);
              B int(5) dim(3);
            end-ds;
            eval DATA.A=7;
            eval DATA.B=DATA.A;
            eval DATA.B(2)=9;
            """);
        Assert.Equal(new object?[] { 7L, 7L, 7L }, Read(context, "DATA.A")); Assert.Equal(new object?[] { 7L, 9L, 7L }, Read(context, "DATA.B"));
    }
    [Fact]
    public void Numeric_array_expressions_apply_arithmetic_and_unary_operations_element_by_element()
    {
        var context = Run("dcl-ds DATA qualified inz;\nA int(5) dim(3);\nB int(5) dim(3);\nend-ds;\neval DATA.A=7;\neval DATA.A(2)=9;\n" +
            "eval DATA.B=DATA.A+2;\neval DATA.B=DATA.B*2;\neval DATA.A=-DATA.B;");
        Assert.Equal(new object?[] { 18L, 22L, 18L }, Read(context, "DATA.B")); Assert.Equal(new object?[] { -18L, -22L, -18L }, Read(context, "DATA.A"));
    }
    [Theory]
    [InlineData("sndpgmmsg %char(DATA.A);")]
    [InlineData("if DATA.A;\nendif;")]
    [InlineData("if DATA.A=DATA.A;\nendif;")]
    public void Unsupported_array_conversions_and_comparisons_fail_explicitly_with_locations(string operation)
    {
        var error = Assert.Throws<RpgRuntimeException>(() => Run("dcl-ds DATA qualified inz;\nA int(5) dim(3);\nend-ds;\n" + operation));
        Assert.Contains("array", error.Message, StringComparison.OrdinalIgnoreCase); Assert.NotNull(error.Location);
    }
    [Theory]
    [InlineData("packed(9:2)", 9, 5)]
    [InlineData("int(3)", 3, 1)]
    [InlineData("int(20)", 20, 8)]
    [InlineData("zoned(5:2)", 5, 5)]
    public void Scalar_size_uses_storage_bytes_and_numeric_length_uses_declared_digits(string type, int length, int size)
    {
        var context = Run("dcl-s VALUE " + type + ";"); Assert.Equal(length, Read(context, "%len(VALUE)")); Assert.Equal(size, Read(context, "%size(VALUE)"));
    }
    [Fact]
    public void Anonymous_fillers_and_DCL_SUBF_names_define_storage_without_exporting_filler_names()
    {
        var context = Run("dcl-s __ANON_DS_1 char(1);\ndcl-s __FILLER_1 char(1);\ndcl-ds *n;\n*n char(2) inz('AB');\n*n char(2) inz('CD');\nDCL-SUBF WRITE char(4) pos(1);\nend-ds;");
        Assert.Equal("ABCD", context.ReadValue("WRITE")); Assert.Equal(" ", context.ReadValue("__FILLER_1"));
        context.WriteValue("__FILLER_1", "X"); Assert.Equal("ABCD", context.ReadValue("WRITE"));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Date_time_and_timestamp_subfields_use_their_declared_ISO_byte_formats(int ccsid)
    {
        var context = Run("""
            dcl-ds DATA qualified inz;
              DAY date inz('2024-02-29');
              CLOCK time inz('13:14:15');
              STAMP timestamp inz('2024-02-29T13:14:15.123456+00:00');
            end-ds;
            """, ccsid);
        var expected = Ipc.Core.Text.CodePage.FromCcsid(ccsid).GetBytes("2024-02-2913.14.152024-02-29-13.14.15.123456");
        Assert.Equal(Convert.ToHexString(expected), Hex(context, "DATA")); Assert.Equal(44, Read(context, "%size(DATA)"));
        Assert.Equal(TimeSpan.FromHours(13) + TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(15), Read(context, "DATA.CLOCK"));
    }
    [Fact]
    public void Invalid_datetime_assignment_reports_112_and_preserves_the_old_field()
    {
        var context = Run("dcl-ds DATA qualified inz;\nDAY date inz('2024-02-29');\nOK ind;\nend-ds;\nmonitor;\neval DATA.DAY='bad';\non-error 112;\neval DATA.OK=*on;\nendmon;");
        Assert.True(Assert.IsType<bool>(Read(context, "DATA.OK"))); Assert.Equal(29, Assert.IsType<DateTimeOffset>(Read(context, "DATA.DAY")).Day);
    }
    [Theory]
    [InlineData("dcl-ds DATA;\nN int(3);\n")]
    [InlineData("dcl-ds DATA len(2);\nN int(10);\nend-ds;")]
    [InlineData("dcl-ds DATA qualified dim(0);\nN char(1);\nend-ds;")]
    [InlineData("dcl-ds DATA;\nN int(3) inz(128);\nend-ds;")]
    [InlineData("dcl-ds DATA;\nN char(2);\nX int(10) overlay(N);\nend-ds;")]
    [InlineData("dcl-ds DATA;\nN char(2) overlay(MISSING);\nend-ds;")]
    [InlineData("dcl-ds DATA;\nN int(10) pos(0);\nend-ds;")]
    [InlineData("dcl-ds DATA;\nN char(1) pos(1) overlay(N);\nend-ds;")]
    [InlineData("dcl-ds DATA;\nN int(10);\nN char(2);\nend-ds;")]
    [InlineData("dcl-ds DATA align;\nN int(10);\nend-ds;")]
    [InlineData("dcl-ds DATA likeds(MISSING);")]
    [InlineData("dcl-ds DATA;\nN char(2);\nend-ds;\ndcl-s N char(1);")]
    [InlineData("dcl-ds DATA dim(2);\nN int(10);\nend-ds;")]
    [InlineData("dcl-ds DATA;\nA char(4) dim(2);\nB char(2) overlay(A) dim(2);\nend-ds;")]
    [InlineData("dcl-ds DATA;\nA char(32767) dim(32767);\nend-ds;")]
    [InlineData("dcl-ds DATA;\nWRITE char(4);\nend-ds;")]
    public void Invalid_or_unimplemented_declarations_fail_with_a_source_location(string body)
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "BADDS", "**free\n" + body)); Assert.NotNull(error.Location);
    }
}
