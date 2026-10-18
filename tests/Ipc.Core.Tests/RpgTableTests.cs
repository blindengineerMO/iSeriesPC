using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedDeclarationTests;
using static Ipc.Core.Tests.RpgFixedCalculationTests;

namespace Ipc.Core.Tests;

public sealed class RpgTableTests
{
    private static RpgProgram Compile(string source) => RpgCompiler.Compile("QGPL", "TABLETEST", source);
    private static RpgRuntimeContext Run(string source, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(source));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private static string Declarations(string sequence = "") => Fixed("TABKEY", "S", "1", 'A', keywords: "DIM(7) CTDATA " + sequence) +
        Fixed("TABVALUE", "S", "1", 'A', keywords: "DIM(7) CTDATA");
    private static string Data(bool descending = false) => "**CTDATA TABKEY\n" + (descending ? "G\nE\nE\nC\nC\nC\nA\n" : "A\nC\nC\nC\nE\nE\nG\n") + "**CTDATA TABVALUE\n1\n2\n3\n4\n5\n6\n7\n";
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Standalone_TAB_DIM_definitions_are_tables_while_DS_subfields_remain_arrays(bool fixedForm)
    {
        var source = fixedForm ? Fixed("TABONE", "S", "3", 'I', "0", "DIM(2)") : "       dcl-s TABONE int(3) dim(2);\n";
        var context = Run(source + "       dcl-ds DS qualified;\n       TABARR int(3) dim(2) inz(0);\n       end-ds;\n" + C("RETURN"));
        var field = context.Program.FindField("TABONE")!; Assert.True(field.IsTable); Assert.False(field.IsArray); Assert.False(field.NativeArray); Assert.False(field.NativeScalar);
        Assert.True(context.HasVariable("TABONE")); Assert.Equal(0L, context.ReadValue("TABONE"));
        Assert.Equal(2, Read(context, "%elem(TABONE)")); Assert.Equal(1, Read(context, "%size(TABONE)")); Assert.Equal(2, Read(context, "%size(TABONE:*all)"));
        Assert.Equal(2, Read(context, "%elem(DS.TABARR)")); Assert.True(context.Program.FindDs("DS")!.Find("TABARR")!.IsArray);
        Assert.Equal(1, Read(context, "%lookup(0:DS.TABARR)"));
    }
    [Fact]
    public void Table_layout_queries_are_available_to_later_declaration_constants()
    {
        var context = Run("       dcl-s TABNUM packed(3:1) dim(3);\n       dcl-c COUNT %elem(TABNUM);\n       dcl-c SIZE %size(TABNUM:*all);\n" +
            "       dcl-c DIGITS %len(TABNUM);\n" + C("RETURN"));
        Assert.Equal(3m, context.ReadValue("COUNT")); Assert.Equal(6m, context.ReadValue("SIZE")); Assert.Equal(3m, context.ReadValue("DIGITS"));
    }
    [Theory]
    [InlineData("TLOOKUP", "C", false, "C", "2")]
    [InlineData("TLOOKUPGT", "C", false, "E", "5")]
    [InlineData("TLOOKUPLT", "E", false, "C", "4")]
    [InlineData("TLOOKUPGE", "C", false, "C", "2")]
    [InlineData("TLOOKUPLE", "C", false, "C", "2")]
    [InlineData("TLOOKUPGE", "D", false, "E", "5")]
    [InlineData("TLOOKUPLE", "D", false, "C", "4")]
    [InlineData("TLOOKUP", "C", true, "C", "4")]
    [InlineData("TLOOKUPGT", "C", true, "E", "3")]
    [InlineData("TLOOKUPLT", "E", true, "C", "4")]
    [InlineData("TLOOKUPGE", "C", true, "C", "4")]
    [InlineData("TLOOKUPLE", "C", true, "C", "4")]
    [InlineData("TLOOKUPGE", "D", true, "E", "3")]
    [InlineData("TLOOKUPLE", "D", true, "C", "4")]
    [InlineData("TLOOKUPNE", "A", false, "C", "2")]
    [InlineData("TLOOKUPNE", "G", true, "E", "2")]
    public void Table_builtins_select_both_tables_and_apply_duplicate_boundaries(string operation, string key, bool descending, string expected, string alternate)
    {
        var context = Run(Declarations(descending ? "DESCEND" : "ASCEND") + C("RETURN") + Data(descending));
        Assert.Equal(true, Read(context, "%" + operation + "('" + key + "':TABKEY:TABVALUE)"));
        Assert.Equal(expected, context.ReadValue("TABKEY")); Assert.Equal(alternate, context.ReadValue("TABVALUE"));
        Assert.Equal(false, Read(context, "%found")); Assert.Equal(false, Read(context, "%equal"));
    }
    [Fact]
    public void Failed_table_builtins_preserve_both_current_elements_and_native_search_status()
    {
        var context = Run(Declarations("ASCEND") + C("LOOKUP", "'C'", "TABKEY", "TABVALUE", eq: "11") + C("RETURN") + Data());
        Assert.Equal(false, Read(context, "%tlookup('Z':TABKEY:TABVALUE)"));
        Assert.Equal("C", context.ReadValue("TABKEY")); Assert.Equal("2", context.ReadValue("TABVALUE"));
        Assert.True(context.Indicators[11]); Assert.Equal(true, Read(context, "%found")); Assert.Equal(true, Read(context, "%equal"));
    }
    [Fact]
    public void Native_table_LOOKUP_preserves_selection_after_a_miss_but_resets_search_flags()
    {
        var context = Run(Declarations() + C("LOOKUP", "'C'", "TABKEY", "TABVALUE", eq: "11") + C("LOOKUP", "'Z'", "TABKEY", "TABVALUE", eq: "11") + Data());
        Assert.Equal("C", context.ReadValue("TABKEY")); Assert.Equal("2", context.ReadValue("TABVALUE"));
        Assert.False(context.Indicators[11]); Assert.Equal(false, Read(context, "%found")); Assert.Equal(false, Read(context, "%equal"));
    }
    [Theory]
    [InlineData("'C'", "C", "2", true)] [InlineData("'D'", "E", "5", false)]
    public void Native_HI_EQ_table_search_combines_shared_indicators(string key, string expected, string alternate, bool equal)
    {
        var context = Run(Declarations("ASCEND") + C("LOOKUP", key, "TABKEY", "TABVALUE", hi: "11", eq: "11") + Data());
        Assert.Equal(expected, context.ReadValue("TABKEY")); Assert.Equal(alternate, context.ReadValue("TABVALUE"));
        Assert.True(context.Indicators[11]); Assert.Equal(equal, Read(context, "%equal"));
    }
    [Fact]
    public void Table_elements_can_be_modified_and_cleared_as_scalars_without_resetting_selection()
    {
        var context = Run(Declarations() + C("LOOKUP", "'C'", "TABKEY", "TABVALUE", eq: "11") + C("EVAL", expression: "TABVALUE='X'") + C("CLEAR", result: "TABKEY") + Data());
        Assert.Equal(" ", context.ReadValue("TABKEY")); Assert.Equal("X", context.ReadValue("TABVALUE"));
        Assert.Equal(true, Read(context, "%tlookup('A':TABKEY:TABVALUE)")); Assert.Equal("1", context.ReadValue("TABVALUE"));
        Assert.Equal(true, Read(context, "%tlookup('':TABKEY:TABVALUE)")); Assert.Equal("X", context.ReadValue("TABVALUE"));
    }
    [Fact]
    public void Native_arithmetic_uses_only_the_current_table_element()
    {
        var source = Fixed("TABNUM", "S", "3", 'P', "0", "DIM(3) CTDATA") + Fixed("TABTXT", "S", "1", 'A', keywords: "DIM(3) CTDATA") +
            C("LOOKUP", "2", "TABNUM", "TABTXT", eq: "11") + C("ADD", factor2: "1", result: "TABNUM", hi: "12") + "**CTDATA TABNUM\n001\n002\n003\n**CTDATA TABTXT\nA\nB\nC\n";
        var context = Run(source); Assert.Equal(3m, context.ReadValue("TABNUM")); Assert.Equal("B", context.ReadValue("TABTXT")); Assert.True(context.Indicators[12]);
        Assert.Equal(true, Read(context, "%tlookup(1:TABNUM:TABTXT)")); Assert.Equal("A", context.ReadValue("TABTXT"));
        Assert.Equal(true, Read(context, "%tlookup(3:TABNUM:TABTXT)")); Assert.Equal("B", context.ReadValue("TABTXT"));
    }
    [Fact]
    public void Native_table_LOOKUP_ignores_decimal_positions_while_builtins_align_values()
    {
        var context = Run(Fixed("TABNUM", "S", "3", 'P', "1", "DIM(2) CTDATA") + Fixed("KEY", "S", "3", 'P', "0", "INZ(20)") +
            C("LOOKUP", "KEY", "TABNUM", eq: "11") + "**CTDATA TABNUM\n010\n020\n");
        Assert.Equal(2m, context.ReadValue("TABNUM")); Assert.True(context.Indicators[11]);
        Assert.Equal(false, Read(context, "%tlookup(KEY:TABNUM)")); Assert.Equal(2m, context.ReadValue("TABNUM"));
        Assert.Equal(true, Read(context, "%tlookup(1:TABNUM)")); Assert.Equal(1m, context.ReadValue("TABNUM"));
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Alternating_inferred_tables_load_through_the_existing_native_codec(int ccsid)
    {
        var context = Run("       dcl-s TABNUM packed(3:1) dim(*ctdata) ascend;\n       dcl-s TABTXT char(2) dim(*ctdata) alt(TABNUM);\n" +
            C("LOOKUP", "20", "TABNUM", "TABTXT", eq: "11") + "**CTDATA TABNUM\n010AA\n020BB\n", ccsid);
        Assert.Equal(2m, context.ReadValue("TABNUM")); Assert.Equal("BB", context.ReadValue("TABTXT")); Assert.Equal(2, Read(context, "%elem(TABNUM)"));
    }
    [Theory]
    [InlineData(37, "1")] [InlineData(1208, "2")]
    public void Character_table_relative_searches_use_unsigned_job_bytes(int ccsid, string expected)
    {
        var data = ccsid == 37 ? "z\nA\n" : "A\nz\n";
        var context = Run(Fixed("TABCHAR", "S", "1", 'A', keywords: "DIM(2) CTDATA ASCEND") + Fixed("TABPOS", "S", "1", 'A', keywords: "DIM(2) CTDATA") +
            C("RETURN") + "**CTDATA TABCHAR\n" + data + "**CTDATA TABPOS\n1\n2\n", ccsid);
        Assert.Equal(true, Read(context, "%tlookupgt('a':TABCHAR:TABPOS)")); Assert.Equal("z", context.ReadValue("TABCHAR")); Assert.Equal(expected, context.ReadValue("TABPOS"));
    }
    [Fact]
    public void Unequal_alternate_dimensions_fail_atomically_when_the_selected_position_is_absent()
    {
        var source = Fixed("TABKEY", "S", "1", 'A', keywords: "DIM(3) CTDATA") + Fixed("TABALT", "S", "1", 'A', keywords: "DIM(1) CTDATA") +
            C("LOOKUP", "'B'", "TABKEY", eq: "11") + C("RETURN") + "**CTDATA TABKEY\nA\nB\nC\n**CTDATA TABALT\nX\n";
        var context = Run(source);
        var error = Assert.Throws<RpgRuntimeException>(() => Read(context, "%tlookup('C':TABKEY:TABALT)"));
        Assert.Equal(121, error.StatusCode); Assert.Equal("B", context.ReadValue("TABKEY")); Assert.Equal("X", context.ReadValue("TABALT"));
        Assert.True(context.Indicators[11]); Assert.Equal(true, Read(context, "%found")); Assert.Equal(true, Read(context, "%equal"));
    }
    [Fact]
    public void A_table_name_can_supply_the_search_argument_from_its_current_element()
    {
        var context = Run(Declarations() + C("LOOKUP", "'C'", "TABKEY", eq: "11") + C("LOOKUP", "TABKEY", "TABKEY", "TABVALUE", eq: "11") + Data());
        Assert.Equal("2", context.ReadValue("TABVALUE"));
    }
    [Fact]
    public void Typed_API_arguments_remain_on_the_captured_table_element_when_selection_changes()
    {
        var program = Compile(Declarations() + C("CALLP", expression: "CHANGE(TABKEY)") + C("RETURN") + Data());
        RpgRuntimeContext context = null!; ProgramArgument? captured = null;
        var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) =>
        {
            captured = Assert.IsType<ProgramArgument>(borrow!().Single()); Assert.Equal("A", captured.Value);
            Assert.Equal(true, Read(context, "%tlookup('C':TABKEY)")); captured.Value = "X";
            Assert.Equal("C", context.ReadValue("TABKEY")); return new() { Success = true };
        } };
        context = new(program, host); new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Throws<ObjectDisposedException>(() => captured!.Value);
        Assert.Equal(true, Read(context, "%tlookup('X':TABKEY:TABVALUE)")); Assert.Equal("1", context.ReadValue("TABVALUE"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Borrowed_character_search_arguments_refresh_caller_bytes_before_comparing(bool builtin)
    {
        var program = Compile(Declarations() + Fixed("KEY", "S", "1", 'A') + C("PLIST", factor1: "*ENTRY") + C("PARM", result: "KEY") +
            C("CALLP", expression: "CHANGE()") + (builtin ? C("EVAL", expression: "*IN11=%tlookup(KEY:TABKEY:TABVALUE)") : C("LOOKUP", "KEY", "TABKEY", "TABVALUE", eq: "11")) + Data());
        object? value = "A"; var argument = new ProgramArgument(() => value, v => value = v, v => v, "*CHAR", 1);
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, _) => { value = "E"; return new() { Success = true }; } }).Run(program, [argument]);
        Assert.Equal("E", context.ReadValue("TABKEY")); Assert.Equal("5", context.ReadValue("TABVALUE")); Assert.True(context.Indicators[11]);
    }
    [Theory]
    [InlineData("LOOKUP")] [InlineData("LOOKUPNE")] [InlineData("NATIVE")] [InlineData("COMP")]
    public void Shared_array_and_compare_paths_refresh_borrowed_character_keys(string operation)
    {
        var calculation = operation switch
        {
            "COMP" => C("COMP", "KEY", "'B'", eq: "11"),
            "NATIVE" => C("LOOKUP", "KEY", "ARR(I)", eq: "11"),
            _ => C("EVAL", expression: "I=%" + operation + "(KEY:ARR)")
        };
        var program = Compile(Fixed("ARR", "S", "1", 'A', keywords: "DIM(2) CTDATA") + Fixed("KEY", "S", "1", 'A') + Fixed("I", "S", "5", 'P', "0", "INZ(1)") +
            C("PLIST", factor1: "*ENTRY") + C("PARM", result: "KEY") + C("CALLP", expression: "CHANGE()") + calculation + "**CTDATA ARR\nA\nB\n");
        object? value = "A"; var argument = new ProgramArgument(() => value, v => value = v, v => v, "*CHAR", 1);
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, _) => { value = "B"; return new() { Success = true }; } }).Run(program, [argument]);
        if (operation == "COMP") Assert.True(context.Indicators[11]); else Assert.Equal(operation == "LOOKUPNE" ? 1m : 2m, context.ReadValue("I"));
    }
    [Fact]
    public void Table_builtin_character_arguments_compare_native_bytes_without_decoding()
    {
        var context = Run(Fixed("TABARG", "S", "2", 'A', keywords: "DIM(1)") + Fixed("TABKEY", "S", "2", 'A', keywords: "DIM(2)") +
            C("EVAL", expression: "TABARG=X'FF00'") + C("EVAL", expression: "TABKEY=X'FF00'") + C("RETURN"), 1208);
        Assert.Equal(true, Read(context, "%tlookup(TABARG:TABKEY)"));
    }
    [Fact]
    public void Failed_native_paired_table_lookup_preserves_both_selections_and_indicators()
    {
        var program = Compile(Fixed("TABKEY", "S", "1", 'A', keywords: "DIM(3) CTDATA") + Fixed("TABALT", "S", "1", 'A', keywords: "DIM(1) CTDATA") +
            C("LOOKUP", "'B'", "TABKEY", eq: "11") + C("LOOKUP", "'C'", "TABKEY", "TABALT", eq: "11") + "**CTDATA TABKEY\nA\nB\nC\n**CTDATA TABALT\nX\n");
        var context = new RpgRuntimeContext(program, new());
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new()).Run(program, existingContext: context));
        Assert.Equal(121, error.StatusCode); Assert.Equal("B", context.ReadValue("TABKEY")); Assert.Equal("X", context.ReadValue("TABALT"));
        Assert.True(context.Indicators[11]); Assert.Equal(true, Read(context, "%found")); Assert.Equal(true, Read(context, "%equal"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Cancellation_during_key_read_preserves_both_table_selections(bool builtin)
    {
        using var stop = new CancellationTokenSource(); var armed = false;
        var argument = new ProgramArgument(() => { if (armed) stop.Cancel(); return "E"; }, _ => { }, value => value, "*CHAR", 1);
        var program = Compile(Declarations() + Fixed("KEY", "S", "1", 'A') + C("PLIST", factor1: "*ENTRY") + C("PARM", result: "KEY") +
            C("LOOKUP", "'C'", "TABKEY", "TABVALUE", eq: "11") + C("CALLP", expression: "ARM()") +
            (builtin ? C("EVAL", expression: "*IN12=%tlookup(KEY:TABKEY:TABVALUE)") : C("LOOKUP", "KEY", "TABKEY", "TABVALUE", eq: "11")) + Data());
        var host = new RpgHost { CancellationToken = stop.Token, TypedProgramCaller = (_, _, _, _) => { armed = true; return new() { Success = true }; } };
        var context = new RpgRuntimeContext(program, host);
        Assert.Throws<OperationCanceledException>(() => new RpgInterpreter(host).Run(program, [argument], context));
        Assert.Equal("C", context.ReadValue("TABKEY")); Assert.Equal("2", context.ReadValue("TABVALUE")); Assert.True(context.Indicators[11]);
        Assert.Equal(true, Read(context, "%found")); Assert.Equal(true, Read(context, "%equal"));
    }
    [Theory]
    [InlineData("%lookup('A':TABKEY)")] [InlineData("%subarr(TABKEY:1:2)")] [InlineData("%xfoot(TABKEY)")]
    [InlineData("TABKEY(1)")] [InlineData("%tlookup('A':TABKEY(1))")]
    public void Tables_cannot_be_used_as_ordinary_arrays(string expression)
    {
        var context = Run(Declarations() + C("RETURN") + Data());
        var error = Assert.Throws<RpgRuntimeException>(() => Read(context, expression)); Assert.Equal(105, error.StatusCode);
    }
    [Theory]
    [InlineData("%tlookup('A')")] [InlineData("%tlookup('A':TABKEY:TABVALUE:4)")]
    [InlineData("%tlookup('A':UNKNOWN)")] [InlineData("%tlookup('A':TABKEY:UNKNOWN)")]
    [InlineData("%tlookupgt('A':TABKEY)")]
    public void Invalid_table_builtin_operands_fail_explicitly(string expression)
    {
        var context = Run(Declarations() + C("RETURN") + Data());
        var error = Assert.Throws<RpgRuntimeException>(() => Read(context, expression)); Assert.Equal(105, error.StatusCode);
    }
    [Theory]
    [InlineData("TABTOOLONGX")] [InlineData("TABLELONGER")]
    public void Standalone_table_names_cannot_exceed_ten_characters(string name)
    {
        Assert.Throws<RpgCompileException>(() => Compile("       dcl-s " + name + " char(1) dim(2);\n"));
    }
    [Fact]
    public void Native_table_indexes_non_table_results_and_entry_declarations_reject()
    {
        foreach (var suffix in new[] { C("LOOKUP", "'A'", "TABKEY(1)", eq: "11"), C("LOOKUP", "'A'", "TABKEY", result: "MISSING", eq: "11"),
            C("PLIST", factor1: "*ENTRY") + C("PARM", result: "TABKEY"), C("SORTA", expression: "TABKEY") })
            Assert.Throws<RpgCompileException>(() => Compile(Declarations() + suffix + Data()));
    }
}
