using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Cl.Parsing;
using Ipc.Services;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class ClCommandArgumentTests
{
    [Fact]
    public void Qualified_and_nested_list_variables_expand_once_without_reinterpreting_text()
    {
        var program = new ClCompiler().Compile("ARGS", "QGPL", """
            DCL &LIB *CHAR LEN(10) VALUE('QGPL')
            DCL &FILE *CHAR LEN(10) VALUE('THING')
            DCL &TEXT *CHAR LEN(40) VALUE('O''Brien (&LIB) OTHER(X)')
            HOST FILE(&LIB/&FILE) LIST((&LIB &FILE) (&TEXT 'Keep &LIB')) TEXT(&TEXT)
            """);
        CommandCall? received = null;
        var result = new ClInterpreter((_, _) => null, call => { received = call; return CommandResult.Ok(); }).Run(program);
        Assert.False(result.IsError, result.Message);
        Assert.Equal("QGPL/THING", received!.GetOption("FILE"));
        Assert.Equal("O'Brien (&LIB) OTHER(X)", CommandParser.Unquote(received.GetOption("TEXT")));
        Assert.Equal("(QGPL THING) ('O''Brien (&LIB) OTHER(X)' 'Keep &LIB')", received.GetOption("LIST"));
        var reparsed = CommandParser.Parse(received.ToString());
        Assert.Equal(3, reparsed.Keywords.Count); Assert.Equal(received.GetOption("LIST"), reparsed.GetOption("LIST"));
    }

    [Theory]
    [InlineData("&MISSING")]
    [InlineData("&LIB/&MISSING")]
    [InlineData("&TEXT/THING")]
    public void Invalid_variable_inputs_fail_before_the_command_is_dispatched(string value)
    {
        var program = new ClCompiler().Compile("BADARGS", "QGPL", "DCL &LIB *CHAR VALUE('QGPL')\nDCL &TEXT *CHAR VALUE('QGPL/OTHER')\nHOST FILE(" + value + ")");
        var called = false;
        var result = new ClInterpreter((_, _) => null, _ => { called = true; return CommandResult.Ok(); }).Run(program);
        Assert.True(result.IsError); Assert.False(called);
    }

    [Fact]
    public void Expansion_rejects_excessive_size_and_nesting_before_dispatch()
    {
        foreach (var input in new[] { "&TEXT &TEXT", new string('(', 65) + "&TEXT" + new string(')', 65) })
        {
            var called = false;
            var result = new ClInterpreter((_, _) => null, _ => { called = true; return CommandResult.Ok(); })
                .RunWithArguments(new ClCompiler().Compile("BIGARGS", "QGPL", "PGM PARM(&TEXT)\nDCL &TEXT *CHAR LEN(32767)\nHOST PARM(" + input + ")\nENDPGM"), new object?[] { new string('X', 32767) });
            Assert.True(result.Result.IsError); Assert.False(called);
        }
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Real_commands_resolve_qualified_names_dimensions_lists_and_preserve_raw_hex(int ccsid)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "ArgumentFixture22");
        system.Libraries.CreateLibrary("ARGONE"); system.Libraries.CreateLibrary("ARGTWO");
        ClExternalCallTests.Create(system, "ARGS", "CLP", """
            PGM
            DCL &LIB *CHAR LEN(10) VALUE('QGPL')
            DCL &AREA *CHAR LEN(10) VALUE('ARGBYTES')
            DCL &LEN *DEC LEN(3 0) VALUE(4)
            DCL &FIRST *CHAR LEN(10) VALUE('ARGONE')
            DCL &SECOND *CHAR LEN(10) VALUE('ARGTWO')
            DCL &LIST *CHAR LEN(30) VALUE('ARGTWO ARGONE')
            DCL &RAW *CHAR LEN(4)
            CHGLIBL LIBL(&FIRST &SECOND)
            CHGLIBL LIBL(&LIST)
            MONMSG IPC0003
            CRTDTAARA DTAARA(&LIB/&AREA) TYPE(*CHAR) LEN(&LEN) VALUE(X'FF00FE01')
            RTVDTAARA DTAARA(&LIB/&AREA) RTNVAR(&RAW)
            IF (&RAW *NE X'FF00FE01') THEN(RETURN)
            CHGDTAARA DTAARA(&LIB/&AREA (2 2)) VALUE(X'ABCD')
            RTVDTAARA DTAARA(&LIB/&AREA) RTNVAR(&RAW)
            IF (&RAW *NE X'FFABCD01') THEN(RETURN)
            DLTDTAARA DTAARA(&LIB/&AREA)
            SNDPGMMSG MSG('COMMAND ARGUMENTS ACCEPTED')
            ENDPGM
            """);
        using var session = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var result = session.Execute("CALL QGPL/ARGS"); Assert.False(result.IsError, result.Message);
        Assert.Contains("COMMAND ARGUMENTS ACCEPTED", result.Message); Assert.Equal("ARGONE ARGTWO", session.Job.LibraryList);
        Assert.Throws<Ipc.Core.Messages.CpfException>(() => new DataAreaStore(system.Connections).Read("QGPL", "ARGBYTES"));
    }

    [Fact]
    public void Variable_case_and_apostrophes_survive_the_real_CMD_binder_and_CPP()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "ArgumentFixture22");
        ClExternalCallTests.Create(system, "ARGCPP", "CLP", "PGM PARM(&TEXT)\nDCL &TEXT *CHAR LEN(40)\nSNDPGMMSG MSG(&TEXT)\nENDPGM");
        const string source = "CMD\nPARM KWD(TEXT) TYPE(*CHAR) LEN(40) MIN(1)";
        var definition = new Ipc.Cl.Definitions.CommandDefinitionCompiler().Compile(source, "QGPL/ARGCPP");
        new Ipc.Services.Commands.CommandDefinitionStore(system.Connections).Create("QGPL", "ARGCMD", source, definition, "QSECOFR");
        using var session = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        foreach (var text in new[] { "mixedCase", "O'Brien (&OTHER)", "'quoted'", "a b" })
        {
            ClExternalCallTests.Create(system, "ARGS" + text.Length, "CLP", "DCL &TEXT *CHAR LEN(40) VALUE('" + text.Replace("'", "''") + "')\nARGCMD TEXT(&TEXT)");
            var result = session.Execute("CALL QGPL/ARGS" + text.Length);
            Assert.False(result.IsError, result.Message); Assert.Equal(text, result.Message?.TrimEnd());
        }
    }

    [Fact]
    public void Program_messages_resolve_each_qualified_destination_without_combining_list_elements()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "ArgumentFixture22");
        var queues = new Ipc.Services.Messages.MessageQueueStore(system.Connections);
        queues.Create(new("QGPL", "ONE")); queues.Create(new("QGPL", "TWO"));
        ClExternalCallTests.Create(system, "DESTS", "CLP", """
            DCL &LIB *CHAR LEN(10) VALUE('qgpl')
            DCL &ONE *CHAR LEN(10) VALUE('one')
            DCL &TWO *CHAR LEN(10) VALUE('two')
            SNDPGMMSG MSG('TWO DESTINATIONS') TOMSGQ(&LIB/&ONE &LIB/&TWO)
            """);
        using var session = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = session.Execute("CALL QGPL/DESTS"); Assert.False(result.IsError, result.Message);
        foreach (var name in new[] { "ONE", "TWO" }) Assert.Equal("TWO DESTINATIONS", Assert.Single(queues.List(new("QGPL", name))).Data.ToText());
    }

    [Fact]
    public void Qualified_call_target_uses_individual_padded_variables()
    {
        var compiler = new ClCompiler();
        var child = compiler.Compile("CHILD", "QGPL", "SNDPGMMSG MSG('CHILD CALLED')");
        var caller = compiler.Compile("CALLER", "QGPL", "DCL &LIB *CHAR LEN(10) VALUE('QGPL')\nDCL &PGM *CHAR LEN(10) VALUE('CHILD')\nCALL &LIB/&PGM");
        var seen = false;
        var result = new ClInterpreter((library, name) => { Assert.Equal("QGPL", library); Assert.Equal("CHILD", name); seen = true; return child; }, _ => CommandResult.Ok()).Run(caller);
        Assert.False(result.IsError, result.Message); Assert.True(seen);
    }
}
