using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Core.Text;
using Ipc.Core.Work;

namespace Ipc.Core.Tests;

public sealed class ClBorrowedArgumentTests
{
    private static ClProgram Compile(string source) => new ClCompiler().Compile("BORROWED", "QGPL", source);
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Incoming_reference_aliases_are_live_and_keep_writes_after_an_unhandled_error(bool fail)
    {
        object? value = 3m;
        var argument = new ProgramArgument(() => value, updated => value = updated, updated => Convert.ToDecimal(updated), "*DEC", 5);
        var source = "PGM PARM(&A &B)\nDCL &A *DEC LEN(5 0)\nDCL &B *DEC LEN(5 0)\nCHGVAR &A (&A + 1)\nCHGVAR &B (&A + &B)\n" + (fail ? "FAIL\n" : "") + "ENDPGM";
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Error("expected", "CPF9898")).RunWithArguments(Compile(source), [argument, argument]);
        Assert.Equal(fail, result.Result.IsError); Assert.Equal(8m, value);
    }
    [Fact]
    public void Nested_calls_forward_the_original_reference_identity_and_storage()
    {
        object? value = 3m; var owner = new object();
        var argument = new ProgramArgument(() => value, updated => value = updated, updated => Convert.ToDecimal(updated), "*DEC", 5, 0, null, null, new(owner, 4, 3));
        var program = Compile("PGM PARM(&A &B)\nDCL &A *DEC LEN(5 0)\nDCL &B *DEC LEN(5 0)\nCALL HOST PARM(&A &B)\nENDPGM");
        var interpreter = new ClInterpreter((_, _) => null, _ => throw new Exception("Unexpected text call."), externalCaller: (_, _, arguments) =>
        {
            Assert.Same(argument, arguments[0]); Assert.Same(argument, arguments[1]); Assert.Same(owner, arguments[0].Storage!.Owner);
            arguments[0].Value = 7m; Assert.Equal(7m, arguments[1].Value); return CommandResult.Ok();
        });
        Assert.False(interpreter.RunWithArguments(program, [argument, argument]).Result.IsError); Assert.Equal(7m, value);
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Raw_character_references_preserve_invalid_bytes_during_substring_assignment(int ccsid)
    {
        var data = Convert.FromHexString("FF00ABCD");
        ProgramBuffer Read() => new(data, ccsid);
        var argument = new ProgramArgument(Read, updated => data = ((ProgramBuffer)updated!).ToArray(), updated => updated, "*CHAR", 4, 0, Read);
        var program = Compile("PGM PARM(&A)\nDCL &A *CHAR LEN(4)\nCHGVAR %SST(&A 2 1) X'FE'\nENDPGM");
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), ccsid: () => ccsid).RunWithArguments(program, [argument]);
        Assert.False(result.Result.IsError, result.Result.Message); Assert.Equal("FFFEABCD", Convert.ToHexString(data));
    }
    [Fact]
    public void Partially_overlapping_reference_cells_observe_each_others_updates()
    {
        var data = CodePage.ToBytes(37, "ABCDEF"); var owner = new object();
        ProgramArgument Cell(int start) => new(() => new ProgramBuffer(data.AsSpan(start, 4).ToArray(), 37),
            updated => ((ProgramBuffer)updated!).ToArray().CopyTo(data, start), updated => updated,
            "*CHAR", 4, 0, () => new(data.AsSpan(start, 4).ToArray(), 37), null, new(owner, start, 4));
        var first = Cell(0); var second = Cell(2);
        var program = Compile("PGM PARM(&A &B)\nDCL &A *CHAR LEN(4)\nDCL &B *CHAR LEN(4)\nCHGVAR &A 'WXYZ'\nCALL HOST PARM(&B)\nENDPGM");
        var interpreter = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), externalCaller: (_, _, args) =>
        { Assert.Equal("YZEF", args[0].ToBuffer()!.ToText()); Assert.Same(second, args[0]); return CommandResult.Ok(); });
        Assert.False(interpreter.RunWithArguments(program, [first, second]).Result.IsError); Assert.Equal("WXYZEF", CodePage.FromBytes(37, data));
    }
    [Theory]
    [InlineData("*INT", 4, 0)] [InlineData("*DEC", 6, 0)] [InlineData("*DEC", 5, 1)]
    public void Reference_layout_mismatch_is_rejected_before_the_body(string type, int length, int scale)
    {
        var entered = false; var written = false;
        var argument = new ProgramArgument(() => 3m, _ => written = true, value => value, type, length, scale);
        var interpreter = new ClInterpreter((_, _) => null, _ => { entered = true; return CommandResult.Ok(); });
        var result = interpreter.RunWithArguments(Compile("PGM PARM(&A)\nDCL &A *DEC LEN(5 0)\nBODY\nENDPGM"), [argument]);
        Assert.True(result.Result.IsError); Assert.False(entered); Assert.False(written);
    }
    [Theory]
    [InlineData(3, 37)] [InlineData(4, 1208)]
    public void Reference_buffer_width_and_CCSID_are_validated_before_execution(int width, int ccsid)
    {
        var entered = false;
        var argument = new ProgramArgument(() => new ProgramBuffer(new byte[width], ccsid), _ => { }, value => value, "*CHAR", 4, 0, () => new(new byte[width], ccsid));
        var interpreter = new ClInterpreter((_, _) => null, _ => { entered = true; return CommandResult.Ok(); });
        var result = interpreter.RunWithArguments(Compile("PGM PARM(&A)\nDCL &A *CHAR LEN(4)\nBODY\nENDPGM"), [argument]);
        Assert.True(result.Result.IsError); Assert.False(entered);
    }
}
