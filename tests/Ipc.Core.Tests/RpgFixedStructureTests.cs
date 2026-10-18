using System.Globalization;
using Ipc.Core.Compilation;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgFixedStructureTests
{
    internal static string D(string name = "", string definition = "", string from = "", string length = "", char type = ' ', string decimals = "", string keywords = "", int shift = 0, char status = ' ', char external = ' ')
    {
        var chars = RpgFixedDeclarationTests.Fixed(name, definition, length, type, decimals, keywords, shift).TrimEnd('\n').PadRight(80 + shift).ToCharArray();
        if (from.StartsWith('*')) from.PadRight(14).CopyTo(0, chars, 25 + shift, 14);
        else from.PadLeft(7).CopyTo(0, chars, 25 + shift, 7);
        chars[21 + shift] = external; chars[22 + shift] = status;
        return new string(chars) + "\n";
    }
    private static RpgProgram Compile(string source) => RpgCompiler.Compile("QGPL", "FIXEDDS", source);
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(source));
    private static string Body(string text = "return;") => "/free\n" + text + "\n/end-free\n";

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Physical_columns_define_byte_positions_and_blank_numeric_subfields_are_zoned(int shift)
    {
        var source = D("DATA", "DS", length: "32", keywords: "QUALIFIED INZ", shift: shift) +
            D("TEXT", from: "1", length: "4", keywords: "INZ('ABCD')", shift: shift) +
            D("NUMBER", from: "5", length: "8", type: 'I', decimals: "0", keywords: "INZ(16909060)", shift: shift) +
            D("PACK", from: "9", length: "11", type: 'P', decimals: "1", keywords: "INZ(1234.5)", shift: shift) +
            D("ZONE", from: "12", length: "14", decimals: "1", keywords: "INZ(-12.3)", shift: shift) +
            D(from: "15", length: "16", shift: shift) + D("TAIL", length: "2", keywords: "INZ('EF')", shift: shift) + Body();
        var program = Compile(source); var ds = program.FindDs("DATA")!;
        Assert.True(ds.NativeStorage); Assert.True(ds.ExplicitLength); Assert.Equal(32, ds.Length);
        Assert.Equal(RpgFieldKind.Zoned, ds.Find("ZONE")!.Kind); Assert.Equal(5, ds.Find("PACK")!.Length);
        var context = new RpgInterpreter(new()).Run(program);
        Assert.Equal(16909060L, Read(context, "DATA.NUMBER")); Assert.Equal(1234.5m, Read(context, "DATA.PACK")); Assert.Equal(-12.3m, Read(context, "DATA.ZONE"));
        Assert.Equal("EF", Read(context, "DATA.TAIL"));
        Assert.Equal("C1C2C3C40102030412345CF1F2D34040C5C6", Convert.ToHexString(Assert.IsType<ProgramBuffer>(Read(context, "DATA")).ToArray().AsSpan(0, 18)));
        Assert.Equal(3, ds.Find("NUMBER")!.DeclarationLine);
    }

    [Theory]
    [InlineData(1, 3, "F9")] [InlineData(2, 5, "FFF9")] [InlineData(4, 10, "FFFFFFF9")] [InlineData(8, 20, "FFFFFFFFFFFFFFF9")]
    public void Integer_From_To_width_is_bytes_and_LEN_is_digits(int bytes, int digits, string expected)
    {
        var context = Run(D("DATA", "DS", keywords: "QUALIFIED INZ") + D("N", from: "1", length: bytes.ToString(), type: 'I', decimals: "0", keywords: "INZ(-7)") + Body());
        Assert.Equal(digits, Read(context, "%len(DATA.N)")); Assert.Equal(bytes, Read(context, "%size(DATA.N)"));
        Assert.Equal(expected, Convert.ToHexString(Assert.IsType<ProgramBuffer>(Read(context, "DATA")).ToArray()));
    }

    [Theory]
    [InlineData(false, 5)] [InlineData(true, 4)]
    public void PACKEVEN_selects_the_even_digit_count_for_a_positioned_packed_field(bool even, int digits)
    {
        var context = Run(D("DATA", "DS", keywords: "QUALIFIED INZ") + D("N", from: "1", length: "3", type: 'P', decimals: "1", keywords: (even ? "PACKEVEN " : "") + "INZ(-123.4)") + Body());
        Assert.Equal(digits, Read(context, "%len(DATA.N)")); Assert.Equal(-123.4m, Read(context, "DATA.N"));
        Assert.Equal("01234D", Convert.ToHexString(Assert.IsType<ProgramBuffer>(Read(context, "DATA")).ToArray()));
        var error = Assert.Throws<RpgRuntimeException>(() => context.WriteNativeReference(RpgExpressionParser.Parse("DATA.N"), even ? 1234.5m : 12345.6m));
        Assert.Equal(103, error.StatusCode); Assert.Equal(-123.4m, Read(context, "DATA.N"));
    }

    [Theory]
    [InlineData('A', "", "'AB'", "AB", 2)]
    [InlineData(' ', "", "'AB'", "AB", 2)]
    [InlineData('S', "1", "12.3", "12.3", 3)]
    [InlineData(' ', "1", "12.3", "12.3", 3)]
    [InlineData('F', "", "1.5", "1.5", 4)]
    [InlineData('N', "", "*on", "True", 1)]
    [InlineData('D', "", "'2026-10-18'", "2026-10-18", 10)]
    [InlineData('T', "", "'12:34:56'", "12.34.56", 8)]
    [InlineData('Z', "", "'2026-10-18T12:34:56.123456'", "2026-10-18-12.34.56.123456", 26)]
    public void Native_subfield_types_use_the_shared_codecs(char type, string decimals, string initial, string expected, int bytes)
    {
        var context = Run(D("DATA", "DS", keywords: "QUALIFIED INZ") + D("N", from: "1", length: bytes.ToString(), type: type, decimals: decimals, keywords: "INZ(" + initial + ")") + Body());
        var value = Read(context, "DATA.N");
        if (value is bool boolean) Assert.Equal(expected, boolean.ToString());
        else if (type is 'D' or 'T' or 'Z') Assert.Equal(expected, Ipc.Core.Text.CodePage.FromBytes(37, Assert.IsType<ProgramBuffer>(Read(context, "DATA")).ToArray()));
        else Assert.Equal(expected, Convert.ToString(value, CultureInfo.InvariantCulture));
        Assert.Equal(bytes, Read(context, "%size(DATA)"));
    }

    [Theory]
    [InlineData(37, "C1C2")] [InlineData(1208, "4142")]
    public void Unqualified_and_anonymous_structures_export_fields_and_keep_native_character_bytes(int ccsid, string expected)
    {
        var program = Compile(D(definition: "DS", length: "5") + D("TEXT", length: "2", keywords: "INZ('AB')") + D(length: "3") + Body());
        var ds = Assert.Single(program.DataStructures); Assert.True(ds.Anonymous); Assert.False(ds.Qualified); Assert.Equal(2, ds.Elements.Count);
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(program);
        Assert.Equal("AB", context.ReadValue("TEXT"));
        ProgramArgument? borrowed = null;
        new RpgInterpreter(new() { Ccsid = () => ccsid, TypedProgramCaller = (_, _, _, borrow) =>
        { borrowed = Assert.IsType<ProgramArgument>(borrow!().Single()); Assert.Equal(expected, Convert.ToHexString(borrowed.ToBuffer()!.ToArray())); return new() { Success = true }; } })
            .Run(Compile(D(definition: "DS") + D("TEXT", length: "2", keywords: "INZ('AB')") + Body("callp SNAP(TEXT);")));
        Assert.Throws<ObjectDisposedException>(() => borrowed!.ToBuffer());
    }

    [Fact]
    public void Structure_and_subfield_overlays_share_live_storage_and_keep_the_next_free_position()
    {
        var context = Run(D("DATA", "DS", keywords: "QUALIFIED") + D("TEXT", length: "4", keywords: "INZ('ABCD')") +
            D("MIDDLE", length: "2", keywords: "OVERLAY(DATA:2)") + D("FIRST", length: "1", keywords: "OVERLAY(TEXT)") +
            D("SECOND", length: "1", keywords: "OVERLAY(TEXT:*NEXT)") + D("TAIL", length: "1", keywords: "OVERLAY(DATA:*NEXT) INZ('Z')") +
            Body("eval DATA.MIDDLE='xy';"));
        Assert.Equal("AxyD", Read(context, "DATA.TEXT")); Assert.Equal("A", Read(context, "DATA.FIRST")); Assert.Equal("x", Read(context, "DATA.SECOND"));
        Assert.Equal("Z", Read(context, "DATA.TAIL")); Assert.Equal(5, Read(context, "%size(DATA)"));
    }

    [Fact]
    public void Native_subfield_arrays_and_strided_overlays_keep_their_array_identity()
    {
        var context = Run(D("DATA", "DS", keywords: "QUALIFIED") + D("ROWS", length: "4", keywords: "DIM(3) INZ('ABCD')") +
            D("LEFT", length: "2", keywords: "OVERLAY(ROWS)") + D("RIGHT", length: "2", keywords: "OVERLAY(ROWS:*NEXT)") +
            Body("eval DATA.LEFT(2)='XY';\neval DATA.RIGHT(3)='ZZ';"));
        Assert.Equal("XYCD", Read(context, "DATA.ROWS(2)")); Assert.Equal("ABZZ", Read(context, "DATA.ROWS(3)"));
        Assert.Equal(3, Read(context, "%elem(DATA.LEFT)")); Assert.Equal(12, Read(context, "%size(DATA)"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void From_To_arrays_apply_the_width_to_each_element_and_advance_past_the_whole_array(bool packed)
    {
        var context = Run(D("DATA", "DS", keywords: "QUALIFIED INZ") + D("ITEMS", from: "3", length: "4", type: packed ? 'P' : 'A', decimals: packed ? "0" : "", keywords: "DIM(3) INZ(" + (packed ? "123" : "'AB'") + ")") +
            D("TAIL", length: "1", keywords: "INZ('Z')") + Body());
        Assert.Equal(9, Read(context, "%size(DATA)")); Assert.Equal("Z", Read(context, "DATA.TAIL"));
        Assert.Equal(packed ? (object)123m : "AB", Read(context, "DATA.ITEMS(3)"));
    }

    [Fact]
    public void Definitions_end_at_the_next_definition_and_share_layout_constants_and_LIKEDS()
    {
        var context = Run(D("BASE", "DS", keywords: "QUALIFIED INZ") + D("N", length: "5", type: 'I', decimals: "0", keywords: "INZ(17)") +
            D("BYTES", "C", keywords: "%size(BASE)") + D("COPY", "DS", keywords: "LIKEDS(BASE) INZ(*LIKEDS)") +
            D("THIRD", "DS", keywords: "QUALIFIED DIM(2) INZ") + D("TEXT", length: "2", keywords: "INZ('OK')") +
            D("OUT", "S", length: "2") + Body("eval OUT=THIRD(2).TEXT;"));
        Assert.Equal(2m, context.ReadValue("BYTES")); Assert.Equal(17L, Read(context, "COPY.N")); Assert.Equal("OK", context.ReadValue("OUT"));
        Assert.Equal(4, Read(context, "%size(THIRD:*all)"));
    }

    [Fact]
    public void Multiple_occurrence_structures_select_and_clear_independent_byte_images()
    {
        var context = Run(D("BANK", "DS", keywords: "QUALIFIED OCCURS(2) INZ") + D("N", length: "3", type: 'P', decimals: "0", keywords: "INZ(7)") +
            Body("eval %occur(BANK)=2;\neval BANK.N=9;\neval %occur(BANK)=1;\nclear BANK;"));
        Assert.Equal(0m, Read(context, "BANK.N")); Assert.Equal(1, Read(context, "%occur(BANK)"));
        new RpgInterpreter(new()).Run(Compile(D("BANK", "DS", keywords: "QUALIFIED OCCURS(2) INZ") + D("N", length: "3", type: 'P', decimals: "0", keywords: "INZ(7)") + Body("eval %occur(BANK)=2;")), existingContext: context);
        Assert.Equal(9m, Read(context, "BANK.N"));
    }

    [Fact]
    public void Native_program_status_S_and_reserved_From_entries_bind_to_shared_feedback()
    {
        var context = Run(D("PS", "DS", keywords: "QUALIFIED", status: 'S') +
            D("CODE", from: "*STATUS") + D("PROC", from: "*PROC") + D("COUNT", from: "*PARM") +
            D("ROUTINE", from: "*ROUTINE") +
            Body("monitor;\neval X=1/0;\non-error 102;\nendmon;"));
        Assert.Equal(102m, Read(context, "PS.CODE")); Assert.Equal("FIXEDDS   ", Read(context, "PS.PROC")); Assert.Equal(0m, Read(context, "PS.COUNT"));
        Assert.Equal("*DETC   ", Read(context, "PS.ROUTINE"));
    }

    [Fact]
    public void Native_structures_without_INZ_retain_blanks_until_CLEAR_initializes_numeric_fields()
    {
        var source = D("DATA", "DS") + D("N", length: "3", type: 'P', decimals: "0");
        var context = Run(source + Body()); Assert.Equal(907, Assert.Throws<RpgRuntimeException>(() => Read(context, "N")).StatusCode);
        context.ClearValue("DATA"); Assert.Equal(0m, context.ReadValue("N"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Keyword_continuations_and_commentary_retain_the_original_subfield_line(int shift)
    {
        var comment = new string(' ', 5 + shift) + "D* ignored ' /COPY BAD\n";
        var source = D("DATA", "DS", shift: shift) + D(keywords: "QUALIFIED INZ", shift: shift) +
            D("N", length: "3", type: 'P', decimals: "0", shift: shift) + comment + D(keywords: "INZ(7)", shift: shift) + Body();
        var program = Compile(source); Assert.Equal(3, program.FindDs("DATA")!.Find("N")!.DeclarationLine);
        Assert.Equal(7m, Read(new RpgInterpreter(new()).Run(program), "DATA.N"));
    }

    [Fact]
    public void Included_subfield_diagnostics_use_the_original_member_and_line()
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "FIXEDDS", new SourceDocument("ROOT", D("DATA", "DS") + "/copy FIELDS\n" + Body()),
            (_, _) => new("FIELDS", D("GOOD", length: "1") + D("BAD", from: "1", length: "3", type: 'I', decimals: "0"))));
        Assert.Equal("FIELDS", error.Location!.Source); Assert.Equal(2, error.Location.Line); Assert.NotEmpty(error.Location.IncludeStack);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Trimmed_records_and_implicit_end_at_EOF_keep_the_final_subfield(int shift)
    {
        var source = D("DATA", "DS", keywords: "QUALIFIED INZ", shift: shift) + D("N", length: "3", type: 'I', decimals: "0", keywords: "INZ(7)", shift: shift);
        source = string.Join('\n', source.TrimEnd().Split('\n').Select(line => line.TrimEnd()));
        var context = Run(source); Assert.Equal(7L, Read(context, "DATA.N")); Assert.Equal(1, Read(context, "%size(DATA)"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Commentary_after_column_80_cannot_change_DS_keywords_or_quote_state(int shift)
    {
        var source = D("DATA", "DS", keywords: "QUALIFIED", shift: shift).TrimEnd('\n') + "ignored ' /COPY BAD\n" +
            D("TEXT", length: "2", keywords: "INZ('AB')", shift: shift).TrimEnd('\n') + "ignored ' //\n" + Body();
        Assert.Equal("AB", Read(Run(source), "DATA.TEXT"));
    }

    [Fact]
    public void TAB_named_native_subfield_arrays_do_not_become_standalone_tables()
    {
        var program = Compile(D("DATA", "DS", keywords: "QUALIFIED INZ") + D("TABNUM", length: "3", type: 'I', decimals: "0", keywords: "DIM(2) INZ(7)") + Body("eval DATA.TABNUM(2)=9;"));
        Assert.True(program.FindDs("DATA")!.Find("TABNUM")!.IsArray);
        Assert.Equal(9L, Read(new RpgInterpreter(new()).Run(program), "DATA.TABNUM(2)"));
    }

    [Fact]
    public void Native_POS_ranges_keep_gap_bytes_and_failed_conversion_preserves_overlapping_views()
    {
        var context = Run(D("DATA", "DS", length: "8") + D("N", from: "3", length: "4", type: 'I', decimals: "0", keywords: "INZ(7)") +
            D("IMAGE", from: "1", length: "8", type: 'A', keywords: "CCSID(*HEX)") + Body());
        var before = Assert.IsType<ProgramBuffer>(Read(context, "DATA")).ToArray();
        Assert.Equal("4040000740404040", Convert.ToHexString(before));
        Assert.Equal(103, Assert.Throws<RpgRuntimeException>(() => context.WriteValue("N", 32768m)).StatusCode);
        Assert.Equal(before, Assert.IsType<ProgramBuffer>(Read(context, "IMAGE")).ToArray());
    }

    [Theory]
    [InlineData("ADD", 9)] [InlineData("SUB", 5)] [InlineData("MULT", 14)] [InlineData("DIV", 3)] [InlineData("Z-ADD", 2)] [InlineData("Z-SUB", -2)]
    public void Native_arithmetic_accepts_qualified_numeric_DS_subfields_and_publishes_stored_result_flags(string operation, int expected)
    {
        var context = Run(D("DATA", "DS", keywords: "QUALIFIED INZ") + D("N", length: "3", type: 'I', decimals: "0", keywords: "INZ(7)") +
            RpgFixedCalculationTests.C(operation, factor2: "2", result: "DATA.N", hi: "11", lo: "12", eq: "13"));
        Assert.Equal((long)expected, Read(context, "DATA.N")); Assert.Equal(expected > 0, context.Indicators[11]); Assert.Equal(expected < 0, context.Indicators[12]); Assert.False(context.Indicators[13]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Native_XFOOT_and_arithmetic_accept_free_or_traditional_DS_arrays_and_indexed_subfields(bool traditional)
    {
        var declaration = traditional ? D("DATA", "DS", keywords: "QUALIFIED INZ DIM(2)") + D("ARR", length: "3", type: 'P', decimals: "0", keywords: "DIM(3) INZ(7)") + D("TOTAL", length: "5", type: 'P', decimals: "0") :
            "       dcl-ds DATA qualified inz dim(2);\n       ARR packed(3:0) dim(3) inz(7);\n       TOTAL packed(5:0);\n       end-ds;\n";
        var context = Run(declaration + RpgFixedCalculationTests.C("ADD", factor2: "1", result: "DATA(2).ARR") +
            RpgFixedCalculationTests.C("XFOOT", factor2: "DATA(2).ARR", result: "DATA(2).TOTAL", hi: "11") +
            RpgFixedCalculationTests.C("SUB", factor1: "DATA(2).TOTAL", factor2: "4", result: "DATA(1).ARR(2)"));
        Assert.Equal(24m, Read(context, "DATA(2).TOTAL")); Assert.Equal(20m, Read(context, "DATA(1).ARR(2)")); Assert.Equal(7m, Read(context, "DATA(1).ARR(1)")); Assert.True(context.Indicators[11]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Native_DO_uses_a_qualified_DS_counter_and_dynamic_DS_step(bool array)
    {
        var root = array ? "DATA(2)" : "DATA";
        var context = Run(D("DATA", "DS", keywords: "QUALIFIED INZ" + (array ? " DIM(2)" : "")) + D("I", length: "5", type: 'I', decimals: "0") + D("STEP", length: "3", type: 'P', decimals: "0", keywords: "INZ(2)") +
            D("TOTAL", length: "5", type: 'P', decimals: "0") + RpgFixedCalculationTests.C("DO", "1", "5", root + ".I") +
            RpgFixedCalculationTests.C("ADD", factor2: root + ".I", result: root + ".TOTAL") + RpgFixedCalculationTests.C("ENDDO", factor2: root + ".STEP"));
        Assert.Equal(9m, Read(context, root + ".TOTAL")); Assert.Equal(7L, Read(context, root + ".I"));
        if (array) Assert.Equal(0L, Read(context, "DATA(1).I"));
    }

    [Fact]
    public void Native_DO_also_writes_indexed_standalone_array_counters()
    {
        var context = Run(RpgFixedDeclarationTests.Fixed("COUNTS", "S", "5", 'I', "0", "DIM(2)") + RpgFixedDeclarationTests.Fixed("TOTAL", "S", "5", 'P', "0", "INZ(0)") +
            RpgFixedCalculationTests.C("DO", "1", "3", "COUNTS(2)") + RpgFixedCalculationTests.C("ADD", factor2: "COUNTS(2)", result: "TOTAL") + RpgFixedCalculationTests.C("ENDDO"));
        Assert.Equal(6m, context.ReadValue("TOTAL")); Assert.Equal(4L, Read(context, "COUNTS(2)")); Assert.Equal(0L, Read(context, "COUNTS(1)"));
    }

    [Theory]
    [InlineData("DATA.TEXT", false)] [InlineData("DATA.ARR", true)] [InlineData("DATA.N(1)", false)] [InlineData("DATA.ARR(1)", true)]
    [InlineData("DATA.FLOAT", true)] [InlineData("DATA.SCALE", true)] [InlineData("DATA.MISSING", false)]
    public void Native_arithmetic_and_counter_layout_guards_still_reject_invalid_DS_operands(string operand, bool counter)
    {
        var source = D("DATA", "DS", keywords: "QUALIFIED INZ") + D("TEXT", length: "2") + D("N", length: "3", type: 'I', decimals: "0") +
            D("ARR", length: "3", type: 'P', decimals: "0", keywords: "DIM(2)") + D("FLOAT", length: "4", type: 'F') + D("SCALE", length: "3", type: 'P', decimals: "1");
        if (operand == "DATA.ARR(1)")
        {
            // Indexed numeric cells are valid counters; using a whole array as
            // the loop limit must still fail before execution.
            source += RpgFixedCalculationTests.C("DO", "1", "DATA.ARR", operand) + RpgFixedCalculationTests.C("ENDDO");
        }
        else source += counter ? RpgFixedCalculationTests.C("DO", "1", "2", operand) + RpgFixedCalculationTests.C("ENDDO") : RpgFixedCalculationTests.C("ADD", factor2: "1", result: operand);
        Assert.Throws<RpgCompileException>(() => Compile(source));
    }

    [Theory]
    [InlineData(" 1 ")] [InlineData("-1")] [InlineData("1.0")]
    public void Invalid_physical_positions_reject_at_the_subfield_source_line(string from)
    {
        var error = Assert.Throws<RpgCompileException>(() => Compile(D("DATA", "DS") + D("N", from: from, length: "3") + Body()));
        Assert.Equal(2, error.Location!.Line);
    }

    public static IEnumerable<object[]> InvalidLayouts()
    {
        yield return [D("DATA", "DS", external: 'E')];
        yield return [D("DATA", "DS", status: 'U')];
        yield return [D("DATA", "DS", from: "1", length: "2")];
        yield return [D("DATA", "DS", type: 'A', length: "2")];
        yield return [D("DATA", "DS", length: "0")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED DIM(0)") + D("N", length: "1")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", from: "2", length: "1")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", from: "0", length: "1")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", from: "1")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", from: "1", length: "2", keywords: "OVERLAY(DATA)")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", length: "2", keywords: "POS(1)")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", length: "2", keywords: "PACKEVEN")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", length: "3", type: 'P', decimals: "0", keywords: "PACKEVEN")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", from: "1", length: "1", type: 'P', decimals: "0", keywords: "PACKEVEN")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", from: "1", length: "16", type: 'P', decimals: "0")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", length: "2", external: 'E')];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", length: "2", status: 'S')];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", from: "*STATUS", type: 'P')];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", from: "*STATUS", keywords: "INZ")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", from: "*BOGUS")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", length: "1") + D("N", length: "1")];
        yield return [D("DATA", "DS", length: "1", keywords: "QUALIFIED") + D("N", from: "1", length: "2")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", length: "1", keywords: "OVERLAY(DATA) OVERLAY(DATA)")];
        yield return [D("BASE", "DS") + D("N", length: "1") + D("COPY", "DS", keywords: "LIKEDS(BASE)") + D("MORE", length: "1")];
        yield return [D(definition: "DS", keywords: "QUALIFIED") + D("N", length: "1")];
        yield return [D("DATA", "DS", keywords: "QUALIFIED") + D("N", length: "1", keywords: "CTDATA DIM(2)")];
    }
    [Theory]
    [MemberData(nameof(InvalidLayouts))]
    public void Invalid_native_layouts_reject_before_any_body_executes(string source)
    {
        var error = Assert.Throws<RpgCompileException>(() => Compile(source + Body("sndpgmmsg 'MUST NOT RUN';")));
        Assert.NotNull(error.Location); Assert.True(error.Location.Line > 0);
    }

    [Fact]
    public void Native_structure_storage_bounds_include_the_whole_array_and_occurrences()
    {
        Assert.Contains("1 MiB", Assert.Throws<RpgCompileException>(() => Compile(D("DATA", "DS", length: "32767", keywords: "QUALIFIED DIM(33)") + D("N", length: "1") + Body())).Message);
        var source = string.Concat(Enumerable.Range(0, 17).Select(n => D("DATA" + n, "DS", length: "32767", keywords: "QUALIFIED OCCURS(32)") + D("N", length: "1")));
        Assert.Contains("16 MiB", Assert.Throws<RpgCompileException>(() => Compile(source + Body())).Message);
    }
}
