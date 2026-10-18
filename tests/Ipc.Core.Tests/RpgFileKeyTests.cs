using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Db.Definitions;
using Ipc.Services.Events;
using Ipc.Core.Objects;
using Ipc.Core.Messages;

namespace Ipc.Core.Tests;

public sealed class RpgFileKeyCompilerTests
{
    internal static string D(string name, string type, int length = 0, int? decimals = null, string keywords = "")
    {
        var chars = Enumerable.Repeat(' ', 90).ToArray(); chars[6] = 'D';
        void Put(int at, string text) => text.CopyTo(0, chars, at, text.Length);
        Put(7, name); Put(21, type); if (length != 0) Put(38, length.ToString());
        if (decimals.HasValue) Put(40, decimals.Value.ToString()); Put(50, keywords); return new string(chars).TrimEnd();
    }
    internal static string KeyC(string opcode, string factor1 = "", string result = "", string factor2 = "")
    {
        var chars = Enumerable.Repeat(' ', 80).ToArray(); chars[5] = 'C';
        void Put(int at, string text) => text.CopyTo(0, chars, at, text.Length);
        Put(11, factor1); Put(25, opcode); Put(35, factor2); Put(49, result); return new string(chars).TrimEnd();
    }
    internal static string DsSource(string body) => string.Join('\n', D("KEYDS", "DS"), D("KAMT", "", 12, 2), D("KLABEL", "", 8), D("N", "S", 2, 0),
        D("ID", "I", 8), "/free", body, "return;", "/end-free");
    [Theory]
    [InlineData("chain (1:'B') NAV;")]
    [InlineData("chain (%int(1+0):%subst('B: C':1:1)) NAV;")]
    [InlineData("setll (1+0:'it''s: A') NAV;")]
    [InlineData("setgt (1) NAV;")]
    [InlineData("reade (1:'A') NAV;")]
    [InlineData("readpe (1:'A') NAV;")]
    [InlineData("delete (1:'A') NAV;")]
    public void Free_expression_lists_preserve_nested_functions_literals_and_all_components(string source)
    {
        var statement = RpgCompiler.Compile("QGPL", "KEYTEST", "**free\n" + source).MainStatements.Single();
        Assert.Equal("NAV", statement.Factor2); Assert.True(statement.IsFreeForm); Assert.Equal(RpgFileSearchKind.Values, statement.FileSearch!.Kind);
        Assert.InRange(statement.FileSearch.Expressions.Count, 1, 2);
    }
    [Theory]
    [InlineData("chain (1:'B') NAV extra;")]
    [InlineData("chain (1:) NAV;")]
    [InlineData("chain (:1) NAV;")]
    [InlineData("chain () NAV;")]
    [InlineData("chain (1:'B' NAV;")]
    [InlineData("chain (1:'B')) NAV;")]
    [InlineData("chain (%int(1):'B) NAV;")]
    [InlineData("chain (1:'B') ;")]
    [InlineData("chain NAV;")]
    [InlineData("chain 1 NAV extra;")]
    [InlineData("chain 1+0 NAV;")]
    [InlineData("read NAV extra;")]
    [InlineData("readp NAV extra;")]
    [InlineData("setll *KEY NAV;")]
    public void Malformed_file_operands_fail_at_the_original_source_line(string source)
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "KEYBAD", "**free\n" + source));
        Assert.NotNull(error.Location); Assert.Equal(2, error.Location.Line);
    }
    [Theory]
    [InlineData("%kds(KEYDS)")]
    [InlineData("%KDS(KEYDS:2)")]
    [InlineData("%KDS(KEYDS:N+1)")]
    public void KDS_binds_to_a_declared_structure_and_retains_the_dynamic_count(string operand)
    {
        var program = RpgCompiler.Compile("QGPL", "KDSKEY", DsSource("chain " + operand + " NAV;"));
        var key = program.MainStatements.First().FileSearch!; Assert.Equal("KEYDS", key.DataStructure); Assert.Equal(RpgFileSearchKind.DataStructure, key.Kind);
    }
    [Theory]
    [InlineData("%KDS(UNKNOWN)")]
    [InlineData("%KDS(KEYDS:)")]
    [InlineData("%KDS(KEYDS:1:2)")]
    [InlineData("%KDS(KEYDS:N+)")]
    [InlineData("%KDS()")]
    [InlineData("%KDS(KEYDS)extra")]
    public void Invalid_KDS_declarations_or_counts_are_not_silently_discarded(string operand)
        => Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "KDSBAD", DsSource("chain " + operand + " NAV;")));
    [Fact]
    public void Native_KLIST_declarations_bind_field_names_in_source_order_without_executing_them()
    {
        var source = string.Join('\n', D("FIRST", "P", 29, 2), D("SECOND", "S", 8), KeyC("KLIST", "SEARCH"), KeyC("KFLD", result: "FIRST"), KeyC("KFLD", result: "SECOND"),
            "/free", "chain SEARCH NAV;", "return;", "/end-free");
        var program = RpgCompiler.Compile("QGPL", "KLKEY", source);
        Assert.Equal(2, program.MainStatements.Count); Assert.Equal(new[] { "FIRST", "SECOND" }, program.MainStatements[0].FileSearch!.Expressions);
        Assert.Equal(RpgFileSearchKind.KeyList, program.MainStatements[0].FileSearch!.Kind);
    }
    [Theory]
    [InlineData("missing field")]
    [InlineData("orphan")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("array")]
    [InlineData("null flag")]
    [InlineData("free")]
    public void Invalid_or_unsupported_key_declarations_are_rejected(string kind)
    {
        var lines = new List<string> { D("A", "S", 8, keywords: kind == "array" ? "DIM(2)" : "") };
        if (kind == "free") lines.Add("/free");
        if (kind != "orphan") lines.Add(KeyC("KLIST", "SEARCH"));
        if (kind != "empty") lines.Add(KeyC("KFLD", result: kind == "missing field" ? "MISSING" : "A", factor2: kind == "null flag" ? "*IN02" : ""));
        if (kind == "duplicate") { lines.Add(KeyC("KLIST", "SEARCH")); lines.Add(KeyC("KFLD", result: "A")); }
        Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "KLBAD", string.Join('\n', lines)));
    }
    [Theory]
    [InlineData("1+2*3", 7)]
    [InlineData("10-2-3", 5)]
    [InlineData("2*-3", -6)]
    [InlineData("1.5+0.5", 2)]
    [InlineData("1e2+3", 103)]
    [InlineData(".5+.25", 0.75)]
    [InlineData("(1+2)*3", 9)]
    public void Symbol_operators_without_spaces_parse_in_key_expressions(string expression, double expected)
    {
        var program = RpgCompiler.Compile("QGPL", "EXPRKEY", "**free\ndcl-s N packed(10:2);\neval N=" + expression + ";\nreturn;");
        Assert.Equal((decimal)expected, RpgValues.ToDecimal(new RpgInterpreter(new()).Run(program).ReadValue("N")));
    }
    [Fact]
    public void Local_key_lists_shadow_global_lists_without_crossing_procedure_scopes()
    {
        string P(string name, char marker)
        { var chars = Enumerable.Repeat(' ', 80).ToArray(); chars[6] = 'P'; name.CopyTo(0, chars, 7, name.Length); chars[23] = marker; return new(chars); }
        var source = string.Join('\n', D("A", "S", 8), D("B", "S", 8), KeyC("KLIST", "SEARCH"), KeyC("KFLD", result: "A"),
            "/free", "chain SEARCH NAV;", "return;", "/end-free", P("LOCAL", 'B'), KeyC("KLIST", "SEARCH"), KeyC("KFLD", result: "B"),
            "/free", "chain SEARCH NAV;", "return;", "/end-free", P("LOCAL", 'E'));
        var program = RpgCompiler.Compile("QGPL", "KLSCOPE", source);
        Assert.Equal("A", Assert.Single(program.MainStatements.First().FileSearch!.Expressions));
        Assert.Equal("B", Assert.Single(program.Subprocedures.Single().Statements.First().FileSearch!.Expressions));
    }
    [Fact]
    public void A_key_declaration_error_in_an_include_retains_its_original_location()
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "KLMAP",
            new Ipc.Core.Compilation.SourceDocument("ROOT", "/copy KEYS"), (_, _) => new("KEYS", KeyC("KLIST", "SEARCH"))));
        Assert.Equal("KEYS", error.Location!.Source); Assert.Equal(1, error.Location.Line); Assert.NotEmpty(error.Location.IncludeStack);
    }
    [Theory]
    [InlineData("9007199254740992.01000000000001")]
    [InlineData("123456789012345678901234567890")]
    [InlineData("1e999")]
    public void Key_literals_cannot_silently_round_or_become_infinite(string literal)
        => Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "KEYPREC", "**free\nchain (" + literal + ") NAV;"));
    [Fact]
    public void Excessive_unary_nesting_is_rejected_before_execution()
        => Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "KEYDEPTH", "**free\nchain (" + new string('-', 1000) + "1) NAV;"));
}

public sealed partial class DatabaseNavigationTests
{
    private RpgRuntimeContext RunKeySource(string source) => new RpgInterpreter(new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL" })
        .Run(RpgCompiler.Compile("QGPL", "KEYARGS", source));
    private long RunKey(string body) => (long)RpgValues.ToDecimal(RunKeySource("**free\ndcl-s ID int(20);\n" + body + "\nreturn;").ReadValue("ID"));
    [Theory]
    [InlineData("(1:'B')", 3)]
    [InlineData("(%int(1+0):%subst('B: A':1:1))", 3)]
    [InlineData("(9007199254740992.02:'B')", 8)]
    [InlineData("(1)", 1)]
    [InlineData("(1.009:'B')", 3)]
    public void Compiled_RPG_searches_the_whole_compound_key_with_exact_values(string key, int expected)
        => Assert.Equal(expected, RunKey("chain " + key + " NAV;"));
    [Fact]
    public void Compound_limits_and_equal_reads_share_the_database_access_path()
    {
        Assert.Equal(3, RunKey("setll (1:'B') NAV;\nreade (1:'B') NAV;"));
        Assert.Equal(4, RunKey("setgt (1:'B') NAV;\nread NAV;"));
        Assert.Equal(2, RunKey("setgt (1:'A') NAV;\nreadpe (1:'A') NAV;"));
        Assert.Equal(2, RunKey("setll (1:'A') NAV;\nreade *KEY NAV;\nreade *KEY NAV;"));
    }
    [Theory]
    [InlineData("%KDS(KEYDS)", 3)]
    [InlineData("%KDS(KEYDS:1)", 1)]
    [InlineData("%KDS(KEYDS:N+1)", 3)]
    public void KDS_reads_live_subfields_and_uses_the_selected_prefix_count(string operand, int expected)
    {
        var source = RpgFileKeyCompilerTests.DsSource("eval KAMT=1;\neval KLABEL='B';\neval N=1;\nchain " + operand + " NAV;");
        Assert.Equal(expected, RpgValues.ToDecimal(RunKeySource(source).ReadValue("ID")));
    }
    [Fact]
    public void A_KLIST_evaluates_its_current_fields_at_each_operation()
    {
        var source = string.Join('\n', RpgFileKeyCompilerTests.D("A", "P", 29, 2), RpgFileKeyCompilerTests.D("B", "S", 8), RpgFileKeyCompilerTests.D("ID", "I", 8),
            RpgFileKeyCompilerTests.KeyC("KLIST", "SEARCH"), RpgFileKeyCompilerTests.KeyC("KFLD", result: "A"), RpgFileKeyCompilerTests.KeyC("KFLD", result: "B"),
            "/free", "eval A=1;", "eval B='A';", "chain SEARCH NAV;", "eval B='B';", "chain SEARCH NAV;", "return;", "/end-free");
        Assert.Equal(3m, RpgValues.ToDecimal(RunKeySource(source).ReadValue("ID")));
    }
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("3")]
    [InlineData("'2'")]
    [InlineData("1e100")]
    public void Invalid_dynamic_KDS_counts_fail_without_changing_records(string count)
    {
        Assert.Throws<RpgRuntimeException>(() => RunKeySource(RpgFileKeyCompilerTests.DsSource("chain %KDS(KEYDS:" + count + ") NAV;")));
        Assert.Equal(8, _files.RowCount("QGPL", "NAV", "NAV"));
    }
    [Theory]
    [InlineData("('1':'A')")]
    [InlineData("(1:2)")]
    [InlineData("(1:'A':0)")]
    public void Invalid_key_types_or_excess_components_are_rejected(string key)
        => Assert.Throws<RpgRuntimeException>(() => RunKey("chain " + key + " NAV;"));
    [Fact]
    public void Compound_key_DELETE_removes_only_one_selected_duplicate_and_reports_FOUND()
    {
        var context = RunKeySource("**free\ndcl-s FOUND ind;\ndelete (1:'A') NAV;\neval FOUND=%found(NAV);\nreturn;");
        Assert.True(RpgValues.ToBool(context.ReadValue("FOUND"))); Assert.Equal("row 2", Stored(2)["TEXT"]);
        Assert.DoesNotContain(_files.ReadAll("QGPL", "NAV", "NAV"), row => (long)row["ID"]! == 1);
        context = RunKeySource("**free\ndcl-s FOUND ind;\ndelete (123:'A') NAV;\neval FOUND=%found(NAV);\nreturn;");
        Assert.False(RpgValues.ToBool(context.ReadValue("FOUND"))); Assert.Equal(7, _files.RowCount("QGPL", "NAV", "NAV"));
    }
    [Fact]
    public void Free_character_search_components_convert_to_the_key_width()
    {
        Assert.Equal(3, RunKey("chain (1:'B       ignored') NAV;"));
        Assert.Equal(3, RunKey("chain (1:'B') NAV;"));
    }
    [Fact]
    public void UTF8_key_conversion_preserves_character_boundaries_at_the_byte_width()
    {
        var format = new RecordFormat { Name = "UROW", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8 },
            new() { Name = "CODE", Type = FieldType.Alpha, Length = 3, Ccsid = 1208, Sequence = 1 }] }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "UTFKEY", new() { Name = "UTFKEY", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        _files.Insert("QGPL", "UTFKEY", "UTFKEY", "UROW", new Dictionary<string, object?> { ["ID"] = 99L, ["CODE"] = "éA" });
        Assert.Equal(99, RunKey("chain ('éAextra') UTFKEY;"));
    }
    [Fact]
    public void A_failed_keyed_DELETE_preserves_input_fields_and_the_prior_position()
        => Assert.Equal(2, RunKey("read NAV;\ndelete (123:'missing') NAV;\nread NAV;"));
    [Fact]
    public void Keyed_DELETE_checks_delete_authority_even_for_a_missing_record()
    {
        _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "QUSER", Authorities.UseBits);
        using var identity = OperationIdentity.Enter("QUSER");
        var error = Assert.Throws<CpfException>(() => RunKey("delete (123:'missing') NAV;"));
        Assert.Equal("CPF9802", error.MessageId);
    }
    [Fact]
    public void The_database_keyed_delete_keeps_its_previous_current_record_after_a_miss()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.Equal(1, Id(cursor.Read()));
        Assert.False(cursor.Delete(Key(123))); Assert.Equal(1, Id(cursor.Current)); Assert.Equal(2, Id(cursor.Read()));
        Assert.True(cursor.Delete(Key(1, "A"))); Assert.Null(cursor.Current); Assert.Equal(2, Id(cursor.Read()));
    }
    [Fact]
    public void KLIST_rejects_an_incompatible_field_shape_instead_of_converting_it()
    {
        var source = string.Join('\n', RpgFileKeyCompilerTests.D("A", "P", 29, 2), RpgFileKeyCompilerTests.D("B", "S", 7),
            RpgFileKeyCompilerTests.KeyC("KLIST", "SEARCH"), RpgFileKeyCompilerTests.KeyC("KFLD", result: "A"), RpgFileKeyCompilerTests.KeyC("KFLD", result: "B"),
            "/free", "eval A=1;", "eval B='A';", "delete SEARCH NAV;", "return;", "/end-free");
        Assert.Throws<RpgRuntimeException>(() => RunKeySource(source)); Assert.Equal(8, _files.RowCount("QGPL", "NAV", "NAV"));
    }
}
