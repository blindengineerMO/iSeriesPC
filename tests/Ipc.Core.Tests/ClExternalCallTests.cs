using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Console.Session;
using Ipc.Services;

namespace Ipc.Core.Tests;

public sealed class ClExternalCallTests
{
    [Fact]
    public void Host_receives_resolved_target_aliased_cells_and_exact_buffers_before_monitored_error()
    {
        var program = new ClCompiler().Compile("CALLER", "QGPL", "PGM PARM(&N)\nDCL &N *INT\nDCL &TARGET *CHAR LEN(20) VALUE('QGPL/EXTERNAL')\nCALL PGM(&TARGET) PARM(&N &N)\nMONMSG CPF9898\nRETURN");
        var interpreter = new ClInterpreter((_, _) => null, _ => throw new Exception("Unexpected text fallback"), externalCaller: (lib, name, arguments) => {
            Assert.Equal("QGPL", lib); Assert.Equal("EXTERNAL", name); Assert.Same(arguments[0], arguments[1]);
            Assert.Equal("00000003", Convert.ToHexString(arguments[0].ToBuffer()!.ToArray()));
            arguments[0].Value = 4m; Assert.Equal(4m, arguments[1].Value);
            arguments[1].Value = new ProgramBuffer(Convert.FromHexString("00000008"), 37);
            return CommandResult.Error("expected", "CPF9898");
        });
        var result = interpreter.RunWithArguments(program, new object?[] { 3m });
        Assert.False(result.Result.IsError, result.Result.Message); Assert.Equal(8m, Assert.Single(result.Parameters));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CL_to_RPG_aliases_are_live_and_write_back_on_return_or_error(bool fail)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        Create(system, "CALLEE", "RPG", Declaration("A", "P", "5") + Declaration("B", "P", "5") +
            Calculation("*ENTRY", "PLIST") + Calculation("A", "PARM") + Calculation("B", "PARM") +
            Calculation("", "ADD", "1", "A") + Calculation("A", "ADD", "B", "B") +
            (fail ? Calculation("", "DIV", "0", "B") : Calculation("", "RETURN")));
        Create(system, "CALLER", "CLP", "PGM\nDCL &N *DEC LEN(5 0) VALUE(3)\nCALL PGM(QGPL/CALLEE) PARM(&N &N)\nMONMSG IPC0006\nSNDPGMMSG MSG(&N)\nENDPGM");
        var result = new CommandService(system).Execute("CALL PGM(QGPL/CALLER)");
        Assert.False(result.IsError, result.Message); Assert.Equal("8", result.Message?.Trim());
    }

    [Fact]
    public void CL_to_RPG_rejects_entry_shape_before_callee_side_effects()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        Create(system, "CALLEE", "RPG", Declaration("A", "P", "7") + Calculation("*ENTRY", "PLIST") + Calculation("A", "PARM") + Calculation("", "DSPLY", "'RAN'"));
        Create(system, "CALLER", "CLP", "PGM\nDCL &N *DEC LEN(5 0) VALUE(3)\nCALL PGM(QGPL/CALLEE) PARM(&N)\nENDPGM");
        var result = new CommandService(system).Execute("CALL PGM(QGPL/CALLER)");
        Assert.True(result.IsError); Assert.Contains("types or lengths", result.Message); Assert.DoesNotContain("RAN", result.Message);
        Create(system, "MISSING", "CLP", "PGM\nCALL QGPL/CALLEE\nENDPGM");
        result = new CommandService(system).Execute("CALL QGPL/MISSING");
        Assert.True(result.IsError); Assert.Contains("parameter count", result.Message); Assert.DoesNotContain("RAN", result.Message);
    }

    internal static void Create(IpcSystem system, string name, string attribute, string source) => system.Objects.Create(new ObjectDescriptor {
        Key = new("QGPL", name), ObjectType = "*PGM", Attribute = attribute, Source = source
    });
    [Fact]
    public void RPG_releases_borrowed_cells_and_rebinds_new_calls_in_retained_storage()
    {
        var program = Ipc.Rpg.Parsing.RpgCompiler.Compile("QGPL", "INC",
            Declaration("A", "P", "5") + Calculation("*ENTRY", "PLIST") + Calculation("A", "PARM") + Calculation("", "ADD", "1", "A"));
        var host = new Ipc.Rpg.Runtime.RpgHost(); var interpreter = new Ipc.Rpg.Runtime.RpgInterpreter(host);
        object? first = 3m, second = 10m;
        var argument = new ProgramArgument(() => first, value => first = value, value => value, "*DEC", 5, 0);
        var context = interpreter.Run(program, new object?[] { argument }); Assert.Equal(4m, first);
        first = 99m; Assert.Equal(4m, context.ReadValue("A"));
        var next = new ProgramArgument(() => second, value => second = value, value => value, "*DEC", 5, 0);
        interpreter.Run(program, new object?[] { next }, context); Assert.Equal(11m, second); Assert.Equal(99m, first);
        Assert.Throws<Ipc.Rpg.Runtime.RpgRuntimeException>(() => interpreter.Run(program, new object?[] { next, next }, context));
        second = 100m; Assert.Equal(11m, context.ReadValue("A"));
    }
    [Fact]
    public void Invalid_CALL_shape_fails_compilation()
    {
        foreach (var command in new[] { "CALL", "CALL PGM(X) BAD(Y)", "CALL PGM(X) PARM(" + string.Join(' ', Enumerable.Repeat("1", 257)) + ")" })
            Assert.Throws<ClCompileException>(() => new ClCompiler().Compile("BAD", "QGPL", command));
    }
    internal static string Declaration(string name, string type, string length) => Line('D', (7, name), (21, type), (38, length), (40, "0"));
    internal static string Calculation(string factor1, string operation, string factor2 = "", string result = "") => Line('C', (17, factor1), (35, operation), (42, factor2), (47, result));
    private static string Line(char specification, params (int Column, string Value)[] parts)
    {
        var line = new char[100]; Array.Fill(line, ' '); line[6] = specification;
        foreach (var part in parts) part.Value.CopyTo(0, line, part.Column, part.Value.Length);
        return new string(line).TrimEnd() + "\n";
    }
}
