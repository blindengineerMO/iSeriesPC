using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class ClCallConstantTests
{
    [Theory]
    [InlineData("12345", "*DEC", 15, 5, "000001234500000C")]
    [InlineData("-25.5", "*DEC", 15, 5, "000000002550000D")]
    [InlineData("(25.509 (*DEC 5 2))", "*DEC", 5, 2, "02550C")]
    [InlineData("X'02550F'", "*RAW", 3, 0, "02550F")]
    [InlineData("(-32768.9 (*INT 2))", "*INT", 2, 0, "8000")]
    [InlineData("(-2147483648 (*INT 4))", "*INT", 4, 0, "80000000")]
    [InlineData("(-9223372036854775808 (*INT 8))", "*INT", 8, 0, "8000000000000000")]
    [InlineData("(65535 (*UINT 2))", "*UINT", 2, 0, "FFFF")]
    [InlineData("(4294967295 (*UINT 4))", "*UINT", 4, 0, "FFFFFFFF")]
    [InlineData("(18446744073709551615 (*UINT 8))", "*UINT", 8, 0, "FFFFFFFFFFFFFFFF")]
    [InlineData("(1.5 (*FLT 4))", "*FLT", 4, 0, "3FC00000")]
    [InlineData("(-2.5 (*FLT 8))", "*FLT", 8, 0, "C004000000000000")]
    [InlineData("('1' (*LGL 1))", "*LGL", 1, 0, "F1")]
    [InlineData("(999999999999999.123456789 (*DEC 24 9))", "*DEC", 24, 9, "0999999999999999123456789C")]
    public void Constants_use_independent_reference_byte_layouts(string source, string type, int length, int decimals, string hex)
    {
        var value = ClCallArgument.Compile(source).Evaluate(_ => throw new Exception("Unexpected variable"));
        Assert.Equal(type, value.Type); Assert.Equal(length, value.Length); Assert.Equal(decimals, value.Decimals);
        Assert.Equal(hex, Convert.ToHexString(value.Buffer.ToArray()));
    }

    [Theory]
    [InlineData(37, "C1C2C3", "40")]
    [InlineData(1208, "414243", "20")]
    public void Character_literals_pad_to_32_and_explicit_lengths_pad_or_truncate_bytes(int ccsid, string prefix, string blank)
    {
        var value = ClCallArgument.Compile("'ABC'").Evaluate(_ => "", ccsid);
        Assert.Equal(prefix + string.Concat(Enumerable.Repeat(blank, 29)), Convert.ToHexString(value.Buffer.ToArray()));
        var longText = new string('A', 33); Assert.Equal(33, ClCallArgument.Compile("'" + longText + "'").Evaluate(_ => "", ccsid).Buffer.Length);
        Assert.Equal(prefix[..4], Convert.ToHexString(ClCallArgument.Compile("('ABC' (*CHAR 2))").Evaluate(_ => "", ccsid).Buffer.ToArray()));
        Assert.Equal(50, ClCallArgument.Compile("('ABC' (*CHAR 50))").Evaluate(_ => "", ccsid).Buffer.Length);
    }

    [Theory]
    [InlineData("10000000000")]
    [InlineData("(32768 (*INT 2))")]
    [InlineData("(-1 (*UINT 4))")]
    [InlineData("(18446744073709551616 (*UINT 8))")]
    [InlineData("(1000 (*DEC 3 0))")]
    public void Overflow_prevents_host_execution_and_is_monitorable(string value)
    {
        var called = false;
        var program = new ClCompiler().Compile("BOUNDS", "QGPL", "CALL HOST PARM(" + value + ")\nMONMSG MCH1210\nRETURN");
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), externalCaller: (_, _, _) => { called = true; return CommandResult.Ok(); }).Run(program);
        Assert.False(result.IsError, result.Message); Assert.False(called);
    }

    [Theory]
    [InlineData("(1 (*INT 3))")]
    [InlineData("(1 (*CHAR 0))")]
    [InlineData("(1 (*DEC 25 0))")]
    [InlineData("(1 (*DEC 15 10))")]
    [InlineData("(1 (*INT 4 0))")]
    [InlineData("(1 (*BOGUS 4))")]
    [InlineData("(1 (*DEC &N 0))")]
    [InlineData("*N")]
    public void Invalid_temporary_attributes_fail_compilation(string value)
        => Assert.Throws<ClCompileException>(() => new ClCompiler().Compile("BAD", "QGPL", "CALL HOST PARM(" + value + ")"));

    [Fact]
    public void Default_numeric_layout_mismatch_fails_before_child_side_effects_and_hex_can_supply_the_receiver_layout()
    {
        var child = new ClCompiler().Compile("CHILD", "QGPL", "PGM PARM(&N)\nDCL &N *INT\nSNDPGMMSG MSG(&N)");
        var messages = new List<string>(); var interpreter = new ClInterpreter((_, _) => child, _ => CommandResult.Ok(), messages.Add);
        var bad = interpreter.Run(new ClCompiler().Compile("CALLER", "QGPL", "CALL CHILD PARM(4)"));
        Assert.True(bad.IsError); Assert.Empty(messages);
        var good = interpreter.Run(new ClCompiler().Compile("CALLER", "QGPL", "CALL CHILD PARM(X'00000004')"));
        Assert.False(good.IsError, good.Message); Assert.Equal("4", Assert.Single(messages));
    }

    [Fact]
    public void Expression_arguments_use_fresh_temporaries_and_explicit_variable_attributes_keep_live_references()
    {
        var child = new ClCompiler().Compile("CHILD", "QGPL", "PGM PARM(&N)\nDCL &N *INT\nCHGVAR &N (&N + 1)");
        var caller = new ClCompiler().Compile("CALLER", "QGPL", """
            PGM PARM(&N)
            DCL &N *INT
            CALL CHILD PARM(((&N + 1) (*INT 4)))
            CALL CHILD PARM((&N (*CHAR 50)))
            ENDPGM
            """);
        var result = new ClInterpreter((_, _) => child, _ => CommandResult.Ok()).RunWithArguments(caller, new object?[] { 3m });
        Assert.False(result.Result.IsError, result.Result.Message); Assert.Equal(4m, Assert.Single(result.Parameters));
    }

    [Fact]
    public void Byte_function_expressions_have_character_temporary_padding_and_utf8_lengths_are_bytes()
    {
        var program = new ClCompiler().Compile("BYTES", "QGPL", "DCL &RAW *CHAR LEN(4) VALUE(X'FF00FE01')\nCALL HOST PARM(%SST(&RAW 1 2) 'é' ('é' (*CHAR 1)))");
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), ccsid: () => 1208, externalCaller: (_, _, values) => {
            Assert.Equal("FF00" + string.Concat(Enumerable.Repeat("20", 30)), Convert.ToHexString(values[0].ToBuffer()!.ToArray()));
            Assert.Equal(32, values[1].ToBuffer()!.Length); Assert.Equal("C3A9", Convert.ToHexString(values[1].ToBuffer()!.ToArray()[..2]));
            Assert.Equal("C3", Convert.ToHexString(values[2].ToBuffer()!.ToArray())); return CommandResult.Ok();
        }).Run(program);
        Assert.False(result.IsError, result.Message);
    }

    [Fact]
    public void Interactive_and_compiled_CALL_use_the_same_CL_and_RPG_constant_layouts()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "ConstantFixture22");
        ClExternalCallTests.Create(system, "CONSTS", "CLP", "PGM PARM(&TEXT &N)\nDCL &TEXT *CHAR LEN(4)\nDCL &N *DEC LEN(15 5)\nSNDPGMMSG MSG(&TEXT *TCAT ':' *CAT &N)");
        ClExternalCallTests.Create(system, "NUMBER", "RPG", ClExternalCallTests.Declaration("N", "P", "5") + ClExternalCallTests.Calculation("*ENTRY", "PLIST") + ClExternalCallTests.Calculation("N", "PARM") + ClExternalCallTests.Calculation("N", "DSPLY"));
        using var session = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("CALL QGPL/CONSTS PARM('ABCDEFG' 25.5)"); Assert.False(result.IsError, result.Message); Assert.Equal("ABCD:25.50000", result.Message);
        ClExternalCallTests.Create(system, "CALLER", "CLP", "CALL QGPL/CONSTS PARM('ABCDEFG' 25.5)");
        result = session.Execute("CALL QGPL/CALLER"); Assert.False(result.IsError, result.Message); Assert.Equal("ABCD:25.50000", result.Message);
        Assert.True(session.Execute("CALL QGPL/NUMBER PARM(4)").IsError);
        result = session.Execute("CALL QGPL/NUMBER PARM((4 (*DEC 5 0)))"); Assert.False(result.IsError, result.Message); Assert.Equal("4", result.Message);
        result = session.Execute("CALL QGPL/NUMBER PARM(X'00004F')"); Assert.False(result.IsError, result.Message); Assert.Equal("4", result.Message);
        Assert.True(session.Execute("CALL QGPL/NUMBER PARM(&MISSING)").IsError);
    }

    [Theory]
    [InlineData("I", "20", "(-9223372036854775808 (*INT 8))", "-9223372036854775808")]
    [InlineData("F", "4", "(1.5 (*FLT 4))", "1.5")]
    [InlineData("F", "8", "X'C004000000000000'", "-2.5")]
    public void RPG_receives_eight_byte_integer_and_floating_constants(string type, string length, string argument, string expected)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        ClExternalCallTests.Create(system, "WIDE", "RPG", ClExternalCallTests.Declaration("N", type, length) + ClExternalCallTests.Calculation("*ENTRY", "PLIST") + ClExternalCallTests.Calculation("N", "PARM") + ClExternalCallTests.Calculation("N", "DSPLY"));
        var result = new Ipc.Console.Session.CommandService(system).Execute("CALL QGPL/WIDE PARM(" + argument + ")");
        Assert.False(result.IsError, result.Message); Assert.Equal(expected, result.Message);
    }

    [Fact]
    public void Short_character_storage_and_bad_numeric_hex_fail_before_callee_runs()
    {
        foreach (var (declaration, argument) in new[] { ("*CHAR LEN(33)", "'ABC'"), ("*DEC LEN(5 2)", "X'12A45C'"), ("*LGL", "'X'") })
        {
            var child = new ClCompiler().Compile("CHILD", "QGPL", "PGM PARM(&P)\nDCL &P " + declaration + "\nSNDPGMMSG MSG('RAN')");
            var ran = false;
            var result = new ClInterpreter((_, _) => child, _ => CommandResult.Ok(), _ => ran = true)
                .Run(new ClCompiler().Compile("CALLER", "QGPL", "CALL CHILD PARM(" + argument + ")"));
            Assert.True(result.IsError); Assert.False(ran);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Aggregate_argument_bytes_are_bounded_for_internal_and_host_calls(bool host)
    {
        var compiler = new ClCompiler(); var names = Enumerable.Range(0, 33).Select(index => "&P" + index).ToArray();
        var child = compiler.Compile("CHILD", "QGPL", "PGM PARM(" + string.Join(' ', names) + ")\n" + string.Join('\n', names.Select(name => "DCL " + name + " *CHAR LEN(32767)")) + "\nSNDPGMMSG MSG('RAN')");
        var parent = compiler.Compile("PARENT", "QGPL", "DCL &RAW *CHAR LEN(32767)\nCALL CHILD PARM(" + string.Join(' ', Enumerable.Repeat("&RAW", 33)) + ")");
        var ran = false;
        var interpreter = new ClInterpreter((_, _) => host ? null : child, _ => CommandResult.Ok(), _ => ran = true,
            externalCaller: (_, _, _) => { ran = true; return CommandResult.Ok(); });
        var result = interpreter.Run(parent); Assert.True(result.IsError); Assert.Contains("1 MiB", result.Message); Assert.False(ran);
    }

    [Fact]
    public void CALL_accepts_255_arguments_and_rejects_256_before_execution()
    {
        var compiler = new ClCompiler(); var received = 0;
        var interpreter = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), externalCaller: (_, _, values) => { received = values.Count; return CommandResult.Ok(); });
        Assert.False(interpreter.Run(compiler.Compile("MAX", "QGPL", "CALL HOST PARM(" + string.Join(' ', Enumerable.Repeat("1", 255)) + ")")).IsError);
        Assert.Equal(255, received);
        Assert.Throws<ClCompileException>(() => compiler.Compile("OVER", "QGPL", "CALL HOST PARM(" + string.Join(' ', Enumerable.Repeat("1", 256)) + ")"));
    }

    [Fact]
    public void Untyped_legacy_receiver_preserves_hex_bytes_when_passing_them_to_a_byte_host()
    {
        var compiler = new ClCompiler(); var child = compiler.Compile("CHILD", "QGPL", "PGM PARM(&RAW)\nCALL HOST PARM(&RAW)");
        var parent = compiler.Compile("PARENT", "QGPL", "CALL CHILD PARM(X'FF00')"); var ran = false;
        var result = new ClInterpreter((_, name) => name == "CHILD" ? child : null, _ => CommandResult.Ok(), ccsid: () => 1208,
            externalCaller: (_, _, values) => { ran = true; Assert.Equal("FF00", Convert.ToHexString(values[0].ToBuffer()!.ToArray())); return CommandResult.Ok(); }).Run(parent);
        Assert.False(result.IsError, result.Message); Assert.True(ran);
    }
}
