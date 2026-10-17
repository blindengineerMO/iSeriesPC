using Ipc.Console.Session;
using Ipc.Core.Compilation;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services;

namespace Ipc.Core.Tests;

public sealed class RpgCompilerDiagnosticsTests
{
    private static string Fixed(char specification, params (int Column, string Text)[] values)
    {
        var line = new string(' ', 100).ToCharArray(); line[6] = specification;
        foreach (var value in values) value.Text.CopyTo(0, line, value.Column, value.Text.Length);
        return new string(line).TrimEnd();
    }
    private static string Calculation(string operation, string first = "", string second = "", string result = "")
        => Fixed('C', (17, first), (35, operation), (42, second), (47, result));
    private static string Procedure(string name, string marker) => Fixed('P', (7, name), (23, marker));
    private static string Character(string name, string width = "12") => Fixed('D', (7, name), (21, "A"), (38, width));
    private static Ipc.Rpg.Model.RpgProgram Compile(string source)
        => RpgCompiler.Compile("QGPL", "MAIN", new SourceDocument("ROOT", source));

    [Theory]
    [InlineData("BOGUS 'ignored';")]
    [InlineData("COMMIT;")]
    [InlineData("ROLBK;")]
    [InlineData("MONITOR;")]
    [InlineData("ENDMON;")]
    [InlineData("CALLP(E) TARGET();")]
    [InlineData("DATA = 'ignored';")]
    [InlineData("; visible after separator")]
    public void Unsupported_free_source_fails_at_the_original_included_line(string statement)
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "MAIN", new SourceDocument("ROOT", "**free\n/COPY PART"),
            (_, _) => new("PART", "\n  " + statement)));
        Assert.Contains("Unsupported free RPG operation", error.Message);
        Assert.Equal("PART", error.Location!.Source); Assert.Equal(2, error.Location.Line); Assert.Equal(3, error.Location.Column);
        Assert.Equal(new[] { "ROOT:2:1" }, error.Location.IncludeStack);
    }

    [Theory]
    [InlineData("BOGUS")]
    [InlineData("COMMIT")]
    [InlineData("ROLBK")]
    [InlineData("DOUEQ")]
    [InlineData("SETONLR")]
    [InlineData("")]
    public void Unsupported_or_missing_fixed_operations_fail_instead_of_becoming_noops(string operation)
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "MAIN", new SourceDocument("ROOT", "/INCLUDE PART"),
            (_, _) => new("PART", "\n" + Calculation(operation, "DATA"))));
        Assert.Contains(operation.Length == 0 ? "Missing fixed RPG operation" : "Unsupported fixed RPG operation " + operation, error.Message);
        Assert.Equal("PART", error.Location!.Source); Assert.Equal(2, error.Location.Line); Assert.Equal(7, error.Location.Column);
    }

    [Theory]
    [InlineData('F')]
    [InlineData('H')]
    [InlineData('I')]
    [InlineData('E')]
    [InlineData('O')]
    [InlineData('X')]
    public void Unsupported_fixed_specifications_are_explicit_errors(char specification)
    {
        var error = Assert.Throws<RpgCompileException>(() => Compile("\n" + Fixed(specification, (7, "UNSUPPORTED"))));
        Assert.Contains(specification == 'F' ? "Supported F-specs" : "Unsupported RPG specification " + specification, error.Message); Assert.Equal(2, error.Location!.Line);
    }

    [Theory]
    [InlineData("Q")]
    [InlineData("U")]
    [InlineData("ZZ")]
    public void Unsupported_declaration_types_do_not_silently_become_character_fields(string type)
    {
        var error = Assert.Throws<RpgCompileException>(() => Compile(Fixed('D', (7, "DATA"), (21, type), (38, "12"))));
        Assert.Contains("Unsupported RPG D-spec type " + type, error.Message); Assert.Equal(1, error.Location!.Line);
    }

    [Theory]
    [InlineData("**free extra\nreturn;", "Invalid or repeated")]
    [InlineData("**free\n**FREE\nreturn;", "Invalid or repeated")]
    [InlineData("/free\n/free\n/end-free\n/end-free", "Nested /FREE")]
    [InlineData("/end-free", "without a matching")]
    [InlineData("/free\nreturn;", "without a matching /END-FREE")]
    [InlineData("**free\n/free\nreturn;\n/end-free", "in a **FREE")]
    [InlineData("ordinary source without a mode", "Expected a supported fixed")]
    [InlineData("      Dbad", "Malformed fixed RPG D")]
    public void Invalid_source_modes_and_unclassified_lines_fail_explicitly(string source, string message)
        => Assert.Contains(message, Assert.Throws<RpgCompileException>(() => Compile(source)).Message);

    [Theory]
    [InlineData("else;", "ELSE without")]
    [InlineData("if 1 = 1;\nelse;\nelse;\nendif;", "Repeated ELSE")]
    [InlineData("if 1 = 1;\ndo;\nendif;\nenddo;", "ENDIF without")]
    [InlineData("do;\nif 1 = 1;\nenddo;\nendif;", "ENDDO without")]
    [InlineData("select;\nif 1 = 1;\nendsl;\nendif;", "ENDSL without")]
    [InlineData("select;\nother;\nother;\nendsl;", "repeated OTHER")]
    [InlineData("select;\nother;\nwhen 1 = 1;\nendsl;", "after OTHER")]
    [InlineData("select;\ndo;\nwhen 1 = 1;\nenddo;\nendsl;", "branch boundary")]
    [InlineData("iter;", "outside of a loop")]
    [InlineData("leave;", "outside of a loop")]
    [InlineData("begsr;\nendsr;", "requires a name")]
    [InlineData("begsr FIRST;\nbegsr SECOND;\nendsr;\nendsr;", "Nested BEGSR")]
    [InlineData("endsr;", "ENDSR without")]
    [InlineData("begsr FIRST;", "BEGSR without")]
    [InlineData("begsr FIRST;\nendsr;\nbegsr first;\nendsr;", "Duplicate RPG subroutine")]
    [InlineData("if 1 = 1;\nbegsr FIRST;\nendif;\nendsr;", "routine boundary")]
    [InlineData("begsr FIRST;\ndo;\nendsr;\nenddo;", "routine boundary")]
    [InlineData("tag DONE;\ntag done;", "Duplicate RPG TAG")]
    [InlineData("goto;", "requires a name")]
    [InlineData("exsr;", "requires a name")]
    public void Invalid_control_structures_fail_with_original_include_context(string body, string message)
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "MAIN", new SourceDocument("ROOT", "**free\n/COPY BODY"),
            (_, _) => new("BODY", "\n" + body)));
        Assert.Contains(message, error.Message); Assert.Equal("BODY", error.Location!.Source); Assert.Contains("ROOT:2:1", error.Message);
    }

    [Theory]
    [InlineData("ELSE extra;")]
    [InlineData("ENDIF extra;")]
    [InlineData("SELECT extra;")]
    [InlineData("OTHER extra;")]
    [InlineData("ENDSL extra;")]
    [InlineData("ENDDO extra;")]
    [InlineData("ENDFOR extra;")]
    [InlineData("ITER extra;")]
    [InlineData("LEAVE extra;")]
    [InlineData("ENDSR extra;")]
    [InlineData("RETRN 5;")]
    [InlineData("RETURN 5;")]
    [InlineData("ON-ERROR 123;")]
    public void Ignored_control_operands_are_rejected(string statement)
        => Assert.Contains("Operands are unsupported", Assert.Throws<RpgCompileException>(() => Compile("**free\n" + statement)).Message);

    [Theory]
    [InlineData("IF;")]
    [InlineData("WHEN;")]
    [InlineData("DOU;")]
    [InlineData("DOW;")]
    [InlineData("FOR;")]
    public void Missing_control_expressions_fail_during_compilation(string statement)
        => Assert.Contains("requires a condition or loop expression", Assert.Throws<RpgCompileException>(() => Compile("**free\n" + statement)).Message);

    [Fact]
    public void Free_text_in_column_seven_is_not_a_fixed_specification()
    {
        var source = Character("DATA") + "\n**free\n" + "eval DATA = 'visible';\ndsply DATA;\ncallp PRIVATE();\n" +
            "      DSPLY DATA;\n      CLEAR DATA;\n      EVAL DATA = 'again';\n      CALLP PRIVATE('a long argument containing words':5);\n      DSPLY DATA;";
        var displays = new List<string>(); var calls = new List<string>();
        var host = new RpgHost { Display = displays.Add, ProgramCaller = (_, name, _) => { calls.Add(name); return null; } };
        var context = new RpgInterpreter(host).Run(Compile(source));
        Assert.Equal(new[] { "visible     ", "visible     ", "again       " }, displays);
        Assert.Equal(new[] { "PRIVATE", "PRIVATE" }, calls); Assert.Equal("again       ", context.ReadValue("DATA"));
    }

    [Fact]
    public void Mixed_free_regions_and_fixed_declarations_keep_source_positions()
    {
        var source = Character("DATA") + "\n" + Calculation("MOVE", "'before'", result: "DATA") +
            "\n/free\n      DSPLY DATA;\n/end-free\n" + Calculation("MOVE", "'after'", result: "DATA");
        var displays = new List<string>(); var program = Compile(source);
        var context = new RpgInterpreter(new RpgHost { Display = displays.Add }).Run(program);
        Assert.Equal("before      ", Assert.Single(displays)); Assert.Equal("after       ", context.ReadValue("DATA"));
        Assert.Equal(4, program.MainStatements[1].Location!.Line); Assert.Equal(7, program.MainStatements[1].Location!.Column);
    }

    [Fact]
    public void Fixed_calculations_inside_global_free_source_fail_instead_of_being_ignored()
        => Assert.Contains("Fixed calculation specifications", Assert.Throws<RpgCompileException>(() => Compile("**free\n" + Calculation("RETURN"))).Message);

    [Fact]
    public void Fixed_subroutines_have_distinct_names_and_both_are_called()
    {
        var source = string.Join('\n', Calculation("EXSR", second: "FIRST"), Calculation("EXSR", second: "NEXT"),
            Calculation("BEGSR", second: "FIRST"), Calculation("SNDMSG", "'first'"), Calculation("ENDSR"),
            Calculation("BEGSR", second: "NEXT"), Calculation("SNDMSG", "'next'"), Calculation("ENDSR"));
        var messages = new List<string>(); var program = Compile(source);
        new RpgInterpreter(new RpgHost { SendMessage = messages.Add }).Run(program);
        Assert.Equal(new[] { "FIRST", "NEXT" }, program.Subroutines.Select(s => s.Name)); Assert.Equal(new[] { "first", "next" }, messages);
    }

    [Fact]
    public void Labels_in_main_and_procedure_have_independent_scopes()
    {
        var source = "**free\ncallp PROC();\ngoto DONE;\nsndpgmmsg 'bad';\ntag DONE;\nsndpgmmsg 'main';\n" +
            Procedure("PROC", "B") + "\ngoto DONE;\nsndpgmmsg 'bad';\ntag DONE;\nsndpgmmsg 'procedure';\n" + Procedure("PROC", "E");
        var messages = new List<string>(); new RpgInterpreter(new RpgHost { SendMessage = messages.Add }).Run(Compile(source));
        Assert.Equal(new[] { "procedure", "main" }, messages);
    }

    [Theory]
    [InlineData("end")]
    [InlineData("open")]
    [InlineData("nested")]
    [InlineData("duplicate")]
    [InlineData("name")]
    [InlineData("marker")]
    [InlineData("block")]
    public void Invalid_procedure_boundaries_are_compile_errors(string kind)
    {
        var source = kind switch {
            "end" => Procedure("PROC", "E"),
            "open" => Procedure("PROC", "B"),
            "nested" => Procedure("PROC", "B") + "\n" + Procedure("INNER", "B"),
            "duplicate" => Procedure("PROC", "B") + "\n" + Procedure("PROC", "E") + "\n" + Procedure("proc", "B") + "\n" + Procedure("proc", "E"),
            "name" => Procedure("PROC", "B") + "\n" + Procedure("OTHER", "E"),
            "marker" => Procedure("PROC", "X"),
            _ => "**free\n" + Procedure("PROC", "B") + "\nif 1 = 1;\n" + Procedure("PROC", "E")
        };
        var error = Assert.Throws<RpgCompileException>(() => Compile(source)); Assert.NotNull(error.Location);
    }

    [Fact]
    public void Bounded_nesting_accepts_256_blocks_and_rejects_the_next_at_its_origin()
    {
        string Source(int depth) => "**free\n" + string.Join('\n', Enumerable.Repeat("if 1 = 1;", depth).Concat(Enumerable.Repeat("endif;", depth)));
        new RpgInterpreter(new RpgHost()).Run(Compile(Source(256)));
        var error = Assert.Throws<RpgCompileException>(() => Compile(Source(257))); Assert.Contains("exceeds 256", error.Message); Assert.Equal(258, error.Location!.Line);
    }

    [Fact]
    public void Blank_fixed_calculation_and_comment_lines_remain_harmless()
    {
        var program = Compile("      C\n      * unsupported comment\n* COMMENT\n    * INDENTED COMMENT\n// comment");
        Assert.Empty(program.MainStatements);
        Assert.Empty(Compile("**free\n;").MainStatements);
    }

    [Fact]
    public void Invalid_compilation_creates_no_object_and_legacy_call_reports_the_source_error()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        files.CreateSourceFile("QGPL", "QRPGLESRC");
        files.SaveSourceMember("QGPL", "QRPGLESRC", "MAIN", "**free\nsndpgmmsg 'must not execute';\n/COPY BAD", null);
        files.SaveSourceMember("QGPL", "QRPGLESRC", "BAD", "\nBOGUS;", null);
        var commands = new CommandService(system);
        var result = commands.Execute("CRTBNDRPG PGM(QGPL/MAIN)"); Assert.True(result.IsError); Assert.Contains("QGPL/QRPGLESRC(BAD):2:1", result.Message);
        Assert.False(system.Objects.Exists("QGPL", "MAIN", ObjectType.Program));
        system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "LEGACY"), ObjectType = ObjectType.Program, Attribute = "RPG", Source = "**free\nsndpgmmsg 'must not execute';\nBOGUS;" });
        var called = commands.Execute("CALL QGPL/LEGACY"); Assert.True(called.IsError); Assert.Contains("QGPL/LEGACY:3:1", called.Message); Assert.DoesNotContain("must not execute", called.Message);
    }
}
