using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Core.Work;
using Xunit;

namespace Ipc.Core.Tests;

public sealed class ClParameterTests
{
    private static ClProgram Compile(string name, string source) => new ClCompiler().Compile(name, "QGPL", source);
    [Theory]
    [InlineData("*DEC LEN(5 2)", "12345D", "01200C", "12.00")]
    [InlineData("*DEC LEN(4 2)", "01234F", "00120C", "1.20")]
    [InlineData("*INT LEN(2)", "8000", "7FFF", "32767")]
    [InlineData("*INT LEN(4)", "80000000", "7FFFFFFF", "2147483647")]
    [InlineData("*UINT LEN(2)", "0000", "FFFF", "65535")]
    [InlineData("*UINT LEN(4)", "00000000", "FFFFFFFF", "4294967295")]
    [InlineData("*LGL", "F0", "F1", "'1'")]
    [InlineData("*CHAR LEN(4)", "C1C2C340", "E7E84040", "'XY'")]
    public void Independent_argument_bytes_decode_and_return_in_declared_layout(string declaration, string input, string output, string assigned)
    {
        var program = Compile("BYTES", $"PGM PARM(&P)\nDCL &P {declaration}\nSNDPGMMSG MSG(&P)\nCHGVAR &P {assigned}\nRETURN");
        var messages = new List<string>();
        var interpreter = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), messages.Add);
        var result = interpreter.RunWithArguments(program, new object?[] { new ProgramBuffer(Convert.FromHexString(input), 37) });
        Assert.False(result.Result.IsError, result.Result.Message);
        Assert.Equal(output, Convert.ToHexString(Assert.IsType<ProgramBuffer>(Assert.Single(result.Parameters)).ToArray()));
        if (declaration == "*DEC LEN(5 2)") Assert.Equal("-123.45", Assert.Single(messages));
        if (declaration == "*INT LEN(2)") Assert.Equal("-32768", Assert.Single(messages));
    }
    [Theory]
    [InlineData("*DEC LEN(5 2)", "123459")]
    [InlineData("*DEC LEN(5 2)", "12A45C")]
    [InlineData("*DEC LEN(4 2)", "11234C")]
    [InlineData("*DEC LEN(5 2)", "123C")]
    [InlineData("*INT LEN(4)", "0001")]
    [InlineData("*LGL", "F2")]
    public void Malformed_or_wrong_sized_argument_fails_before_execution(string declaration, string hex)
    {
        var ran = false;
        var program = Compile("BAD", $"PGM PARM(&P)\nDCL &P {declaration}\nSNDPGMMSG MSG('ran')");
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), _ => ran = true)
            .RunWithArguments(program, new object?[] { new ProgramBuffer(Convert.FromHexString(hex), 37) });
        Assert.True(result.Result.IsError);
        Assert.False(ran);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Aliases_share_changes_and_propagate_them_on_return_or_error(bool fail)
    {
        var child = Compile("CHILD", $"PGM PARM(&A &B)\nDCL &A *INT\nDCL &B *INT\nCHGVAR &A (&A + 1)\nCHGVAR &B (&B + &A)\n{(fail ? "FAIL" : "RETURN")}\nCHGVAR &A 999");
        var parent = Compile("PARENT", "PGM PARM(&N)\nDCL &N *INT\nCALL PGM(CHILD) PARM(&N &N)\nRETURN");
        var scopes = new List<string>();
        var interpreter = new ClInterpreter((_, name) => name == "CHILD" ? child : null, _ => CommandResult.Error("CPF9898: failed"), programScope: p => { scopes.Add(p.Name); return null; });
        var result = interpreter.RunWithArguments(parent, new object?[] { 3m });
        Assert.Equal(fail, result.Result.IsError);
        Assert.Equal(8m, Assert.IsType<decimal>(Assert.Single(result.Parameters)));
        Assert.Equal(new[] { "PARENT", "CHILD" }, scopes);
        if (fail) { Assert.Contains("QGPL/CHILD:6:", result.Result.Message); Assert.Contains("QGPL/PARENT:3:", result.Result.Message); }
    }
    [Fact]
    public void Reference_declaration_mismatch_is_rejected_before_callee_runs()
    {
        var child = Compile("CHILD", "PGM PARM(&P)\nSNDPGMMSG MSG('wrong')\nDCL &P *DEC LEN(5 0)");
        var parent = Compile("PARENT", "PGM PARM(&P)\nDCL &P *INT\nCALL PGM(CHILD) PARM(&P)");
        var messages = new List<string>();
        var result = new ClInterpreter((_, _) => child, _ => CommandResult.Ok(), messages.Add).RunWithArguments(parent, new object?[] { 1m });
        Assert.True(result.Result.IsError);
        Assert.Contains("type and length", result.Result.Message);
        Assert.Equal(1m, Assert.Single(result.Parameters));
        Assert.Empty(messages);
    }
    [Fact]
    public void Constant_arguments_are_private_and_repeated_calls_start_fresh()
    {
        var child = Compile("CHILD", "PGM PARM(&N)\nDCL &N *INT\nCHGVAR &N (&N + 1)\nSNDPGMMSG MSG(&N)\nRETURN");
        var parent = Compile("PARENT", "CALL PGM(CHILD) PARM((4 (*INT 4)))\nCALL PGM(CHILD) PARM((4 (*INT 4)))");
        var messages = new List<string>();
        var result = new ClInterpreter((_, _) => child, _ => CommandResult.Ok(), messages.Add).Run(parent);
        Assert.False(result.IsError, result.Message);
        Assert.Equal(new[] { "5", "5" }, messages);
    }
    [Fact]
    public void Counts_duplicate_names_and_variable_program_targets_are_checked()
    {
        Assert.Throws<ClCompileException>(() => Compile("BAD", "PGM PARM(&P &p)"));
        var child = Compile("CHILD", "PGM PARM(&P)\nCHGVAR &P 'UPDATED'");
        var interpreter = new ClInterpreter((lib, name) => lib == "QGPL" && name == "CHILD" ? child : null, _ => CommandResult.Error("missing"));
        Assert.True(interpreter.Run(child).IsError);
        var parent = Compile("PARENT", "PGM PARM(&P)\nDCL &TARGET *CHAR LEN(20) VALUE('QGPL/CHILD')\nCALL PGM(&TARGET) PARM(&P)");
        var result = interpreter.RunWithArguments(parent, new object?[] { "OLD" });
        Assert.False(result.Result.IsError, result.Result.Message);
        Assert.Equal("UPDATED", Assert.Single(result.Parameters));
    }

    [Fact]
    public void Untyped_buffer_parameter_writes_back_updated_bytes()
    {
        var program = Compile("TEXT", "PGM PARM(&P)\nCHGVAR &P 'XY'\nRETURN");
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok()).RunWithArguments(program,
            new object?[] { new ProgramBuffer(Convert.FromHexString("C1C2C340"), 37) });
        Assert.False(result.Result.IsError, result.Result.Message);
        Assert.Equal("E7E84040", Convert.ToHexString(Assert.IsType<ProgramBuffer>(Assert.Single(result.Parameters)).ToArray()));
    }
    [Fact]
    public void Session_RPG_call_receives_CL_parameter_updates()
    {
        using var system = Ipc.Services.IpcSystem.Create(":memory:"); system.Start();
        system.Objects.Create(new Ipc.Core.Objects.ObjectDescriptor { Key = new("QGPL", "UPDATECL"), ObjectType = "*PGM", Attribute = "CLP",
            Source = "PGM PARM(&N)\nDCL &N *DEC LEN(9 0)\nCHGVAR &N (&N + 7)\nRETURN" });
        var declaration = new string(' ', 80).ToCharArray(); declaration[6] = 'D'; "COUNT".CopyTo(0, declaration, 7, 5);
        declaration[21] = 'S'; declaration[38] = '9'; declaration[40] = '0';
        system.Objects.Create(new Ipc.Core.Objects.ObjectDescriptor { Key = new("QGPL", "CALLER"), ObjectType = "*PGM", Attribute = "RPG",
            Source = new string(declaration) + "\n**free\neval COUNT = 5;\ncallp UPDATECL(COUNT);\ndsply COUNT;\nreturn;" });
        var result = new Ipc.Console.Session.CommandService(system).Execute("CALL PGM(QGPL/CALLER)");
        Assert.False(result.IsError, result.Message);
        Assert.Equal("12", result.Message?.Trim());
    }
}
