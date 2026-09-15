using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Core.Work;

namespace Ipc.Core.Tests;

public sealed class ClRawCharacterTests
{
    private static ClProgram Compile(string name, string source) => new ClCompiler().Compile(name, "QGPL", source);
    [Fact]
    public void Character_capacity_and_initial_value_limits_are_distinct()
    {
        var program = Compile("MAXIMUM", "PGM PARM(&A)\nDCL &A *CHAR LEN(32767)\nRETURN");
        var input = new ProgramBuffer(Enumerable.Range(0, 32767).Select(n => (byte)n).ToArray(), 1208);
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), ccsid: () => 1208).RunWithArguments(program, new object?[] { input });
        Assert.False(result.Result.IsError, result.Result.Message);
        Assert.Equal(input.ToArray(), Assert.IsType<ProgramBuffer>(Assert.Single(result.Parameters)).ToArray());
        Assert.Throws<ClCompileException>(() => Compile("TOOLONG", "DCL &A *CHAR LEN(32768)"));
        Assert.Throws<ClCompileException>(() => Compile("INITIAL", "DCL &A *CHAR LEN(32767) VALUE('" + new string('A', 5001) + "')"));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Hex_constants_preserve_bytes_in_declarations_assignments_and_call_buffers(int ccsid)
    {
        var child = Compile("CHILD", "PGM PARM(&A)\nDCL &A *CHAR LEN(2)\nCALL HOST PARM(&A)\nENDPGM");
        var parent = Compile("HEX", """
            PGM
            DCL &RAW *CHAR VALUE(X'FF00')
            CALL HOST PARM(&RAW)
            CHGVAR &RAW X'FE01'
            CALL HOST PARM(&RAW)
            CALL CHILD PARM(X'FD02')
            CALL HOST PARM(X'FC03')
            ENDPGM
            """);
        var observed = new List<string>();
        var result = new ClInterpreter((_, name) => name == "CHILD" ? child : null, _ => CommandResult.Ok(), ccsid: () => ccsid,
            externalCaller: (_, _, arguments) => { observed.Add(Convert.ToHexString(arguments[0].ToBuffer()!.ToArray())); return CommandResult.Ok(); }).Run(parent);
        Assert.False(result.IsError, result.Message); Assert.Equal(new[] { "FF00", "FE01", "FD02", "FC03" }, observed);
    }
    [Fact]
    public void Character_length_inference_counts_job_bytes_and_does_not_reinterpret_quoted_hex_text()
    {
        var program = Compile("LENGTH", "PGM\nDCL &A *CHAR VALUE('éA')\nDCL &B *CHAR VALUE('X''FF''')\nCALL HOST PARM(&A &B)\nENDPGM");
        var observed = new List<ProgramBuffer>();
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), ccsid: () => 1208,
            externalCaller: (_, _, arguments) => { observed.AddRange(arguments.Select(a => a.ToBuffer()!)); return CommandResult.Ok(); }).Run(program);
        Assert.False(result.IsError, result.Message); Assert.Equal("C3A941", Convert.ToHexString(observed[0].ToArray()));
        Assert.Equal("X'FF'", observed[1].ToText()); Assert.Equal(5, observed[1].Length);
    }
    [Fact]
    public void Hex_initial_value_limit_counts_bytes_and_rejects_invalid_constants()
    {
        var source = "DCL &RAW *CHAR VALUE(X'" + string.Concat(Enumerable.Repeat("FF", 2500)) + "+\n" + string.Concat(Enumerable.Repeat("FF", 2500)) + "')";
        Assert.False(new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), ccsid: () => 1208).Run(Compile("MAXHEX", source)).IsError);
        Assert.Throws<ClCompileException>(() => Compile("TOOBIG", source.Replace("')", "FF')")));
        foreach (var value in new[] { "X'F'", "X'FG'", "X'FF", "X'FF''00'" })
            Assert.Throws<ClCompileException>(() => Compile("BADHEX", "DCL &RAW *CHAR VALUE(" + value + ")"));
        Assert.Throws<ClCompileException>(() => Compile("BADTYPE", "DCL &N *DEC LEN(3 0) VALUE(X'F1')"));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Every_byte_survives_CL_entry_nested_aliases_and_return(int ccsid)
    {
        var child = Compile("CHILD", "PGM PARM(&A &B)\nDCL &A *CHAR LEN(256)\nDCL &B *CHAR LEN(256)\nCHGVAR &A &B\nRETURN");
        var parent = Compile("PARENT", "PGM PARM(&BYTES)\nDCL &BYTES *CHAR LEN(256)\nCALL CHILD PARM(&BYTES &BYTES)\nRETURN");
        var input = new ProgramBuffer(Enumerable.Range(0, 256).Select(n => (byte)n).ToArray(), ccsid);
        var result = new ClInterpreter((_, name) => name == "CHILD" ? child : null, _ => CommandResult.Ok(), ccsid: () => ccsid)
            .RunWithArguments(parent, new object?[] { input });
        Assert.False(result.Result.IsError, result.Result.Message);
        Assert.Equal(input.ToArray(), Assert.IsType<ProgramBuffer>(Assert.Single(result.Parameters)).ToArray());
    }
    [Theory]
    [InlineData("*CAT", "FF20200120", 5)]
    [InlineData("*TCAT", "FF0120", 3)]
    [InlineData("*BCAT", "FF200120", 4)]
    public void Binary_character_concatenation_trims_and_inserts_encoded_blanks(string operation, string expected, int length)
    {
        var program = Compile("CAT", $"PGM PARM(&A &B &OUT)\nDCL &A *CHAR LEN(3)\nDCL &B *CHAR LEN(2)\nDCL &OUT *CHAR LEN({length})\nCHGVAR &OUT (&A {operation} &B)\nRETURN");
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), ccsid: () => 1208).RunWithArguments(program, new object?[] {
            new ProgramBuffer(Convert.FromHexString("FF2020"), 1208), new ProgramBuffer(Convert.FromHexString("0120"), 1208), new ProgramBuffer(new byte[length], 1208)
        });
        Assert.False(result.Result.IsError, result.Result.Message); Assert.Equal(expected, Convert.ToHexString(Assert.IsType<ProgramBuffer>(result.Parameters[2]).ToArray()));
    }
    [Fact]
    public void Binary_copy_pads_truncates_and_compares_bytes_without_decoding()
    {
        var program = Compile("COPY", "PGM PARM(&A &OUT)\nDCL &A *CHAR LEN(4)\nDCL &OUT *CHAR LEN(6)\nDCL &SHORT *CHAR LEN(2)\n" +
            "CHGVAR &SHORT &A\nCHGVAR &OUT &SHORT\nIF (&SHORT *GE &A) THEN(SNDPGMMSG MSG('wrong comparison'))\nRETURN");
        var messages = new List<string>();
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), messages.Add, ccsid: () => 1208).RunWithArguments(program, new object?[] {
            new ProgramBuffer(Convert.FromHexString("00FFFF00"), 1208), new ProgramBuffer(new byte[6], 1208)
        });
        Assert.False(result.Result.IsError, result.Result.Message); Assert.Empty(messages);
        Assert.Equal("00FF20202020", Convert.ToHexString(Assert.IsType<ProgramBuffer>(result.Parameters[1]).ToArray()));
    }
    [Fact]
    public void Text_operation_on_undecodable_bytes_is_monitorable_without_losing_the_buffer()
    {
        var program = Compile("TEXT", "PGM PARM(&A)\nDCL &A *CHAR LEN(2)\nSNDPGMMSG MSG(&A)\nMONMSG IPC0136\nSNDPGMMSG MSG('handled')\nRETURN");
        var messages = new List<string>(); var input = new ProgramBuffer(new byte[] { 255, 0 }, 1208);
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), messages.Add, ccsid: () => 1208).RunWithArguments(program, new object?[] { input });
        Assert.False(result.Result.IsError, result.Result.Message); Assert.Equal(new[] { "handled" }, messages);
        Assert.Equal(input.ToArray(), Assert.IsType<ProgramBuffer>(Assert.Single(result.Parameters)).ToArray());
    }
    [Fact]
    public void Host_can_write_opaque_character_bytes_and_invalid_lengths_leave_the_cell_unchanged()
    {
        var program = Compile("HOST", "PGM PARM(&A)\nDCL &A *CHAR LEN(2)\nCALL HOST PARM(&A)\nRETURN");
        var interpreter = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), ccsid: () => 1208,
            externalCaller: (_, _, arguments) => {
                arguments[0].Value = new ProgramBuffer(new byte[] { 255, 254 }, 1208);
                Assert.Throws<ClRuntimeException>(() => arguments[0].Value = new ProgramBuffer(new byte[] { 1 }, 1208));
                Assert.Equal(new byte[] { 255, 254 }, arguments[0].ToBuffer()!.ToArray()); return CommandResult.Ok();
            });
        var result = interpreter.RunWithArguments(program, new object?[] { "AB" });
        Assert.False(result.Result.IsError, result.Result.Message); Assert.Equal(new byte[] { 255, 254 }, Assert.IsType<ProgramBuffer>(Assert.Single(result.Parameters)).ToArray());
    }
}
