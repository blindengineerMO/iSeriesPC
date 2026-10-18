using Ipc.Core.Compilation;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed class RpgProgramSignatureTests
{
    private static RpgProgram Compile(string source) => RpgCompiler.Compile("QGPL", "SIGNATURE", source);
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private static string Proto(string parameters = "ITEM int(3);", string target = "TARGET") => "dcl-pr RUN extpgm('" + target + "');\n" + parameters + "\nend-pr;\n";

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Native_prototype_parameters_have_private_type_metadata_and_do_not_allocate_variable_storage(int shift)
    {
        var program = Compile(D("RUN", "PR", keywords: "EXTPGM('TARGET')", shift: shift) +
            D("N", length: "3", type: 'I', decimals: "0", shift: shift) + D("N", length: "3", type: 'I', decimals: "0", shift: shift) +
            "**free\ndcl-s N int(3) inz(7);\ncallp RUN(N:N);");
        var prototype = Assert.Single(program.Prototypes); Assert.True(prototype.NativeSignature); Assert.Equal("TARGET", prototype.ExternalName);
        Assert.Equal(2, prototype.Parameters.Count); Assert.Equal(2, prototype.NativeParameters.Count);
        Assert.Equal("N", Assert.Single(program.Fields).Name); Assert.Single(program.DataStructures);
        Assert.All(prototype.NativeParameters, formal => Assert.Null(program.FindField(formal.Name)));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void Native_reference_prototypes_keep_live_aliases_and_expire_factories_on_success_or_failure(bool fixedForm, bool fail)
    {
        var source = fixedForm ? D("RUN", "PR", keywords: "EXTPGM('TARGET')") + D(length: "3", type: 'I', decimals: "0") + D(length: "3", type: 'I', decimals: "0") + "**free\n" :
            "**free\n" + Proto("A int(3);\nB int(3);");
        source += "dcl-s N int(3) inz(7);\ncallp(e) RUN(N:N);\nreturn;";
        ProgramArgument? retained = null; Func<IReadOnlyList<object?>>? retainedFactory = null;
        var host = new RpgHost { TypedProgramCaller = (library, name, _, borrow) =>
        {
            Assert.Equal("*LIBL", library); Assert.Equal("TARGET", name); retainedFactory = borrow;
            var arguments = borrow!(); Assert.Same(arguments[0], arguments[1]); retained = Assert.IsType<ProgramArgument>(arguments[0]);
            retained.Value = 8L; retained.Value = (long)retained.Value! + (long)((ProgramArgument)arguments[1]!).Value!;
            if (fail) throw new RpgRuntimeException("Expected callee failure.") { StatusCode = 102 };
            return new() { Success = true };
        } };
        var context = new RpgInterpreter(host).Run(Compile(source));
        Assert.Equal(16L, context.ReadValue("N")); Assert.Equal(fail, Read(context, "%error()")); Assert.Equal(fail ? 202 : 0, Read(context, "%status()"));
        Assert.Throws<ObjectDisposedException>(() => retainedFactory!()); Assert.Throws<ObjectDisposedException>(() => retained!.Value);
    }

    public static IEnumerable<object[]> ScalarTypes()
    {
        foreach (var fixedForm in new[] { false, true })
        {
            yield return [fixedForm, "char(2)", 'A', "2", "", "", "'AB'", "*CHAR", 2, 0];
            yield return [fixedForm, "char(2) ccsid(*hex)", 'A', "2", "", "CCSID(*HEX)", "X'FF00'", "*CHAR", 2, 0];
            yield return [fixedForm, "packed(5:1)", 'P', "5", "1", "", "-123.4", "*DEC", 5, 1];
            yield return [fixedForm, "zoned(3:1)", 'S', "3", "1", "", "-12.3", "*ZONED", 3, 1];
            yield return [fixedForm, "int(3)", 'I', "3", "0", "", "-7", "*INT", 1, 0];
            yield return [fixedForm, "float(4)", 'F', "4", "", "", "1.5", "*FLT", 4, 0];
            yield return [fixedForm, "ind", 'N', "", "", "", "*on", "*LGL", 1, 0];
            yield return [fixedForm, "date", 'D', "", "", "", "'2026-10-18'", "*DATE", 10, 0];
            yield return [fixedForm, "time", 'T', "", "", "", "'12:34:56'", "*TIME", 8, 0];
            yield return [fixedForm, "timestamp", 'Z', "", "", "", "'2026-10-18T12:34:56.123456'", "*TIMESTAMP", 26, 0];
        }
    }
    [Theory]
    [MemberData(nameof(ScalarTypes))]
    public void Native_signatures_preserve_each_supported_scalar_ABI(bool fixedForm, string declaration, char type, string length, string decimals, string keywords, string initial, string expectedType, int expectedLength, int expectedScale)
    {
        var source = fixedForm ? D("RUN", "PR", keywords: "EXTPGM('TARGET')") + D("IGNORED", length: length, type: type, decimals: decimals, keywords: keywords) + "**free\n" : "**free\n" + Proto("ITEM " + declaration + ";");
        source += "dcl-s N " + declaration + " inz(" + initial + ");\ncallp RUN(N);";
        var called = false; var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) =>
        {
            var argument = Assert.IsType<ProgramArgument>(borrow!().Single()); called = true;
            Assert.Equal(expectedType, argument.Type); Assert.Equal(expectedLength, argument.Length); Assert.Equal(expectedScale, argument.Decimals);
            var before = argument.ToBuffer()!.ToArray(); argument.Value = argument.ToBuffer(); Assert.Equal(before, argument.ToBuffer()!.ToArray());
            return new() { Success = true };
        } };
        new RpgInterpreter(host).Run(Compile(source)); Assert.True(called);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Main_PI_parameters_borrow_the_original_native_cells_and_preserve_aliases(bool fixedForm)
    {
        var source = fixedForm ? D(definition: "PI") + D("A", length: "3", type: 'I', decimals: "0") + D("B", length: "3", type: 'I', decimals: "0") + "**free\n" :
            "**free\ndcl-pi *n;\nA int(3);\nB int(3);\nend-pi;\n";
        source += "eval A=A+1;\neval B=A+B;\nreturn;";
        object? value = 7L; var owner = new object();
        var cell = new ProgramArgument(() => value, updated => value = updated, updated => Convert.ToInt64(updated), "*INT", 1, 0, null, null, new(owner, 0, 1));
        var program = Compile(source); Assert.Equal(new[] { "A", "B" }, RpgInterpreter.EntryParameterNames(program));
        var context = new RpgInterpreter(new()).Run(program, [cell, cell]); Assert.Equal(16L, value); Assert.Equal(16L, context.ReadValue("A")); Assert.Equal(16L, context.ReadValue("B"));
        Assert.Equal(2, Read(context, "%parms()")); cell.Value = 99L; Assert.Equal(16L, context.ReadValue("A"));
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(2)]
    public void PI_parameter_count_is_required_even_for_an_empty_caller_and_precedes_the_body(int count)
    {
        var program = Compile("**free\ndcl-pi *n;\nA int(3);\nend-pi;\nsndpgmmsg 'BODY';"); var entered = false;
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new() { SendMessage = _ => entered = true }).Run(program, count < 0 ? null : Enumerable.Repeat<object?>(7L, count).ToArray()));
        Assert.Contains("parameter count", error.Message); Assert.False(entered);
    }

    [Fact]
    public void Named_main_PI_matches_its_prototype_by_layout_and_not_parameter_name()
    {
        var source = "**free\ndcl-pr MAIN extpgm('TARGET');\nDOCUMENTATION int(3);\nend-pr;\ndcl-pi MAIN;\nACTUAL int(3);\nend-pi MAIN;\nreturn;";
        var context = new RpgInterpreter(new()).Run(Compile(source), [7L]); Assert.Equal(7L, context.ReadValue("ACTUAL")); Assert.False(context.HasField("DOCUMENTATION"));
    }

    [Fact]
    public void Column_limited_signatures_and_inline_zero_parameter_forms_share_the_native_parser()
    {
        var source = "       dcl-pr RUN extpgm('QGPL/TARGET') end-pr;\n       dcl-pi *n end-pi;\n" + RpgFixedCalculationTests.C("CALLP", expression: "RUN()");
        var called = false; var host = new RpgHost { TypedProgramCaller = (library, name, _, borrow) =>
        { Assert.Equal("QGPL", library); Assert.Equal("TARGET", name); Assert.Empty(borrow!()); called = true; return new() { Success = true }; } };
        var program = Compile(source); Assert.True(program.HasNativeEntryInterface); new RpgInterpreter(host).Run(program); Assert.True(called);
    }

    [Fact]
    public void Captured_indexed_cells_and_returned_updates_keep_the_address_selected_before_the_call()
    {
        var program = Compile("**free\n" + Proto() + "dcl-s ARR int(3) dim(2) inz(7);\ndcl-s I int(3) inz(1);\ncallp RUN(ARR(I));");
        RpgRuntimeContext? context = null;
        var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) =>
        { var argument = Assert.IsType<ProgramArgument>(borrow!().Single()); context!.WriteValue("I", 2L); argument.Value = 9L; return new() { Success = true, UpdatedParameters = [10L] }; } };
        context = new(program, host); new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(10L, Read(context, "ARR(1)")); Assert.Equal(7L, Read(context, "ARR(2)"));
    }

    [Fact]
    public void A_native_scalar_raw_formal_can_receive_a_whole_DS_without_initial_numeric_decoding()
    {
        var program = Compile("**free\n" + Proto("ITEM char(4) ccsid(*hex);") + "dcl-ds DATA qualified;\nN int(10);\nend-ds;\ncallp RUN(DATA);");
        var context = new RpgInterpreter(new() { TypedProgramCaller = (_, _, _, borrow) =>
        { Assert.IsType<ProgramArgument>(borrow!().Single()).Value = new ProgramBuffer(Convert.FromHexString("01020304"), 37); return new() { Success = true }; } }).Run(program);
        Assert.Equal(16909060L, Read(context, "DATA.N"));
    }

    [Fact]
    public void Include_origins_are_retained_for_invalid_native_prototype_parameters()
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "SIGNATURE", new SourceDocument("ROOT", D("RUN", "PR", keywords: "EXTPGM('TARGET')") + "/copy PARAMS\n**free\nreturn;"),
            (_, _) => new("PARAMS", D("DOCUMENT", length: "3", type: 'I', decimals: "0", keywords: "VALUE"))));
        Assert.Equal("PARAMS", error.Location!.Source); Assert.Equal(1, error.Location.Line); Assert.NotEmpty(error.Location.IncludeStack);
    }

    public static IEnumerable<object[]> InvalidSignatures()
    {
        yield return ["dcl-pr RUN;\nend-pr;"];
        yield return ["dcl-pr RUN extproc('TARGET');\nend-pr;"];
        yield return ["dcl-pr RUN int(10) extpgm('TARGET');\nend-pr;"];
        yield return ["dcl-pr RUN extpgm(TARGET);\nend-pr;"];
        yield return ["dcl-pr RUN extpgm('LONGPROGRAMNAME');\nend-pr;"];
        yield return ["dcl-pr RUN extpgm('A/B/C');\nend-pr;"];
        yield return ["dcl-pr RUN extpgm('TARGET');\nN int(3) const(7);\nend-pr;"];
        yield return ["dcl-pr RUN extpgm('TARGET');\nN int(3) value;\nend-pr;"];
        yield return ["dcl-pr RUN extpgm('TARGET');\nN int(3) options(*omit:*omit);\nend-pr;"];
        yield return ["dcl-pr RUN extpgm('TARGET');\nN int(3) dim(*var);\nend-pr;"];
        yield return ["dcl-pr RUN extpgm('TARGET');\nN int(3) inz(7);\nend-pr;"];
        yield return ["dcl-pr RUN extpgm('TARGET');\nN int(3);\nend-pi;"];
        yield return ["dcl-pr RUN extpgm('TARGET');\nN int(3);"];
        yield return ["dcl-pr RUN extpgm('TARGET');\nreturn int(3);\nend-pr;"];
        yield return ["dcl-pi *n;\nA int(3);\nA int(3);\nend-pi;"];
        yield return ["dcl-pi NAMED;\nA int(3);\nend-pi;"];
        yield return ["dcl-pi *n end-pi;\ndcl-pi *n end-pi;"];
        yield return [Proto() + "dcl-pi RUN;\nA int(5);\nend-pi;"];
        yield return [Proto() + "dcl-s RUN int(3);"];
        yield return [Proto() + Proto()];
        yield return ["dcl-pr RUN extpgm('TARGET');\nend-pr WRONG;"];
    }
    [Theory]
    [MemberData(nameof(InvalidSignatures))]
    public void Unsupported_or_inconsistent_signatures_reject_at_the_original_source_location(string source)
    { Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile("**free\n" + source + "\nreturn;")).Location); }

    [Theory]
    [InlineData("", "N")]
    [InlineData("N:N", "N")]
    [InlineData("1", "N")]
    [InlineData("N+1", "N")]
    [InlineData("MISSING", "N")]
    [InlineData("N", "char(1)")]
    [InlineData("N", "int(5)")]
    [InlineData("N", "packed(3:0)")]
    [InlineData("N", "packed(3:1)")]
    [InlineData("N", "int(3) dim(2)")]
    public void Native_call_count_type_width_scale_and_variable_guards_run_before_execution(string arguments, string declaration)
    {
        var type = declaration == "N" ? "int(3)" : declaration;
        var source = "**free\n" + Proto() + "dcl-s N " + type + ";\ncallp(e) RUN(" + arguments + ");";
        Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile(source)).Location);
    }

    [Fact]
    public void PI_cannot_be_combined_with_an_ENTRY_PLIST()
    {
        Assert.Throws<RpgCompileException>(() => Compile(D(definition: "PI") + D("N", length: "3", type: 'I', decimals: "0") +
            RpgFixedCalculationTests.C("PLIST", factor1: "*ENTRY") + RpgFixedCalculationTests.C("PARM", result: "N")));
    }

    [Fact]
    public void Native_program_signature_counts_and_prototype_counts_are_bounded_without_allocating_formal_storage()
    {
        Assert.Contains("255", Assert.Throws<RpgCompileException>(() => Compile("**free\n" + Proto(string.Concat(Enumerable.Repeat("A int(3);\n", 256))))).Message);
        var source = "**free\n" + string.Concat(Enumerable.Range(0, 257).Select(i => "dcl-pr P" + i + " extpgm('TARGET') end-pr;\n"));
        Assert.Contains("256", Assert.Throws<RpgCompileException>(() => Compile(source)).Message);
    }

    [Fact]
    public void Native_prototypes_do_not_fall_back_to_untyped_hosts_or_rebind_local_procedure_names()
    {
        var source = "**free\ndcl-pr RUN extpgm('TARGET') end-pr;\ncallp RUN();";
        var called = false; var host = new RpgHost { ProgramCaller = (_, _, _) => { called = true; return new() { Success = true }; } };
        Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(host).Run(Compile(source))); Assert.False(called);
        var program = Compile(source); program.Subprocedures.Add(new() { Name = "RUN" });
        new RpgInterpreter(new() { TypedProgramCaller = (_, name, _, borrow) =>
        { Assert.Equal("TARGET", name); Assert.Empty(borrow!()); called = true; return new() { Success = true }; } }).Run(program);
        Assert.True(called);
    }

    [Fact]
    public void Aggregate_native_argument_bytes_reject_before_the_target_is_entered()
    {
        var parameters = string.Concat(Enumerable.Repeat("ITEM char(32767);\n", 33));
        var program = Compile("**free\n" + Proto(parameters) + "dcl-s N char(32767);\ncallp(e) RUN(" + string.Join(':', Enumerable.Repeat("N", 33)) + ");");
        var called = false; var host = new RpgHost { TypedProgramCaller = (_, _, _, _) => { called = true; return new() { Success = true }; } };
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program)).MessageId); Assert.False(called);
    }

    [Fact]
    public void Native_prototype_arguments_are_variables_even_when_a_PLIST_has_the_same_name()
    {
        var source = D("RUN", "PR", keywords: "EXTPGM('TARGET')") + D(length: "3", type: 'I', decimals: "0") +
            D("N", "S", length: "3", type: 'I', decimals: "0", keywords: "INZ(7)") + D("OTHER", "S", length: "3", type: 'I', decimals: "0", keywords: "INZ(9)") +
            RpgFixedCalculationTests.C("PLIST", factor1: "N") + RpgFixedCalculationTests.C("PARM", result: "OTHER") + RpgFixedCalculationTests.C("CALLP", expression: "RUN(N)");
        var called = false;
        new RpgInterpreter(new() { TypedProgramCaller = (_, name, _, borrow) =>
        { Assert.Equal("TARGET", name); Assert.Equal(7L, Assert.IsType<ProgramArgument>(borrow!().Single()).Value); called = true; return new() { Success = true }; } }).Run(Compile(source));
        Assert.True(called);
    }

    [Theory]
    [InlineData("RUN(N")] [InlineData("RUN N")] [InlineData("RUN(N))")] [InlineData("RUN(N:)")] [InlineData("RUN(N+)")]
    public void Malformed_native_call_syntax_is_a_compile_error_even_with_E(string call)
    { Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile("**free\n" + Proto() + "dcl-s N int(3);\ncallp(e) " + call + ";")).Location); }
}
