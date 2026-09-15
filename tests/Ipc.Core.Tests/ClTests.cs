using System.Text;
using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Cl.Parsing;
using Ipc.Console.Session;
using Ipc.Core.Security;
using Ipc.Services;
using Ipc.Terminal;
using Xunit;

namespace Ipc.Core.Tests.Cl;

public class ClParserTests
{
    [Fact]
    public void Parses_keyword_and_positional_arguments()
    {
        var call = CommandParser.Parse("GO MAJOR LIB(QSYS)");

        Assert.Equal("GO", call.Name);
        Assert.Equal(new[] { "MAJOR" }, call.Positional);
        Assert.Equal("QSYS", call.GetOption("LIB"));
    }

    [Fact]
    public void Parses_quoted_strings()
    {
        var call = CommandParser.Parse("SNDPGMMSG MSG('Hello world')");
        Assert.Equal("Hello world", CommandParser.Unquote(call.GetOption("MSG")));
    }

    [Fact]
    public void Parses_multi_value_parenthesized_group()
    {
        var call = CommandParser.Parse("CALL PGM(P) PARM(&A &B)");
        Assert.Equal(new[] { "&A", "&B" }, call.Split("PARM"));
    }

    [Fact]
    public void Rejects_unterminated_quote()
    {
        Assert.Throws<ClParseException>(() => CommandParser.Parse("MSG('oops"));
    }

    [Fact]
    public void Rejects_unbalanced_parentheses()
    {
        Assert.Throws<ClParseException>(() => CommandParser.Parse("GO MAJOR LIB(QSYS"));
    }
}

public class ClCompilerTests
{
    [Fact]
    public void Compiles_flat_program_with_flow_control()
    {
        var source = new StringBuilder()
            .AppendLine("PGM PARM(&WHO)")
            .AppendLine("DCL VAR(&MSG) TYPE(*CHAR) LEN(50)")
            .AppendLine("CHGVAR VAR(&MSG) VALUE('Hello ' + &WHO)")
            .AppendLine("SNDPGMMSG MSG(&MSG)")
            .AppendLine("ENDPGM")
            .ToString();

        var program = new ClCompiler().Compile("GREET", "QSYS", source);

        Assert.Equal(new[] { "&WHO" }, program.EntryParameters);
        Assert.Contains(program.Statements, s => s.Kind == ClStatementKind.SendProgramMessage);
    }

    [Fact]
    public void Resolves_labels()
    {
        var program = new ClCompiler().Compile("G", "QSYS", "PGM\nGOTO LAB\nLAB:\nENDPGM\n");
        Assert.True(program.Labels.ContainsKey("LAB"));
    }

    [Fact]
    public void Rejects_if_without_endif()
    {
        Assert.Throws<ClCompileException>(() => new ClCompiler().Compile("X", "QSYS", "PGM\nIF COND(&X *EQ 'A')\nSNDPGMMSG MSG('x')\n"));
    }

    [Fact]
    public void Keeps_nested_block_termination()
    {
        var source = "PGM\nIF COND(&A *EQ '1')\nIF COND(&B *EQ '1')\nSNDPGMMSG MSG('B')\nENDIF\nSNDPGMMSG MSG('A')\nENDIF\nENDPGM";
        var program = new ClCompiler().Compile("N", "QSYS", source);
        Assert.All(program.Statements, s => Assert.True(s.Jump >= 0 || s.Kind is not (ClStatementKind.If or ClStatementKind.Else)));
    }
}

public class ClInterpreterTests
{
    [Fact]
    public void Sends_program_message_from_variable()
    {
        var messages = new List<string>();
        var interpreter = NewInterpreter(messages);
        var program = new ClCompiler().Compile(
            "M",
            "QSYS",
            "PGM\nDCL VAR(&MSG) VALUE('Ready')\nSNDPGMMSG MSG(&MSG)\nENDPGM");

        var result = interpreter.Run(program);

        Assert.Equal(CommandOutcome.Continue, result.Outcome);
        Assert.Equal(new[] { "Ready" }, messages);
    }

    [Fact]
    public void Binds_entry_parameters()
    {
        var messages = new List<string>();
        var interpreter = NewInterpreter(messages);
        var program = new ClCompiler().Compile(
            "G",
            "QSYS",
            "PGM PARM(&WHO)\nCHGVAR VAR(&MSG2) VALUE('Hello ' + &WHO)\nSNDPGMMSG MSG(&MSG2)\nENDPGM");

        interpreter.Run(program, new[] { "WORLD" });

        Assert.Equal(new[] { "Hello WORLD" }, messages);
    }

    [Fact]
    public void If_then_goto_controls_flow()
    {
        var messages = new List<string>();
        var interpreter = NewInterpreter(messages);
        var program = new ClCompiler().Compile(
            "N",
            "QSYS",
            "PGM PARM(&N)\n" +
            "IF COND(&N *EQ '1') THEN(GOTO ONE)\n" +
            "SNDPGMMSG MSG('ZERO')\n" +
            "GOTO DONE\n" +
            "ONE:\n" +
            "SNDPGMMSG MSG('ONE')\n" +
            "DONE:\n" +
            "ENDPGM");

        interpreter.Run(program, new[] { "1" });
        Assert.Equal(new[] { "ONE" }, messages);

        messages.Clear();
        interpreter.Run(program, new[] { "0" });
        Assert.Equal(new[] { "ZERO" }, messages);
    }

    [Fact]
    public void If_else_block_selects_branch()
    {
        var messages = new List<string>();
        var interpreter = NewInterpreter(messages);
        var program = new ClCompiler().Compile(
            "B",
            "QSYS",
            "PGM PARM(&N)\n" +
            "IF COND(&N *EQ '2')\n" +
            "SNDPGMMSG MSG('TWO')\n" +
            "ELSE\n" +
            "SNDPGMMSG MSG('OTHER')\n" +
            "ENDIF\n" +
            "ENDPGM");

        interpreter.Run(program, new[] { "2" });
        Assert.Equal(new[] { "TWO" }, messages);

        messages.Clear();
        interpreter.Run(program, new[] { "9" });
        Assert.Equal(new[] { "OTHER" }, messages);
    }

    [Fact]
    public void Calls_nested_program_through_loader()
    {
        var called = new List<string>();
        var messages = new List<string>();
        var child = new ClCompiler().Compile(
            "CHILD",
            "QSYS",
            "PGM PARM(&P)\nSNDPGMMSG MSG('child:' + &P)\nENDPGM");

        var interpreter = new ClInterpreter(
            (library, name) => name == "CHILD" ? child : null,
            _ =>
            {
                called.Add(_.ToString());
                return CommandResult.Ok();
            },
            messages.Add);

        var parent = new ClCompiler().Compile(
            "PARENT",
            "QSYS",
            "PGM\nCALL PGM(CHILD) PARM('x')\nSNDPGMMSG MSG('parent')\nENDPGM");

        var result = interpreter.Run(parent);

        Assert.Equal(CommandOutcome.Continue, result.Outcome);
        Assert.Equal(new[] { "child:x", "parent" }, messages);
        Assert.Empty(called);
    }

    [Fact]
    public void Commands_inside_program_are_substituted_and_routed()
    {
        CommandCall? routed = null;
        var interpreter = new ClInterpreter(
            (_, _) => null,
            call =>
            {
                routed = call;
                return CommandResult.Ok();
            },
            null);
        var program = new ClCompiler().Compile(
            "C",
            "QSYS",
            "PGM PARM(&MENU)\nGO MAJOR &MENU\nENDPGM");

        var result = interpreter.Run(program, new[] { "USRLIB" });

        Assert.Equal(CommandOutcome.Continue, result.Outcome);
        Assert.NotNull(routed);
        Assert.Equal("GO", routed.Name);
        Assert.Equal(new[] { "MAJOR", "USRLIB" }, routed.Positional);
    }

    [Fact]
    public void Reports_unknown_label_as_error()
    {
        var interpreter = NewInterpreter(new List<string>());
        var program = new ClCompiler().Compile("X", "QSYS", "PGM\nGOTO MISSING\nENDPGM");

        var result = interpreter.Run(program);

        Assert.Equal(CommandOutcome.Error, result.Outcome);
        Assert.Contains("MISSING", result.Message);
    }

    private static ClInterpreter NewInterpreter(List<string> messages)
    {
        return new ClInterpreter(
            (_, _) => null,
            _ => CommandResult.Ok(),
            messages.Add);
    }
}

public class CommandServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IpcSystem _system;

    public CommandServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ipc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _system = IpcSystem.Create(_tempDir, "test.db");
        _system.Start();
    }

    public void Dispose()
    {
        _system.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Command_catalog_reports_unknown_command()
    {
        var service = new CommandService(_system);
        var result = service.Execute("WOBBLE");

        Assert.Equal(CommandOutcome.Error, result.Outcome);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public void Go_command_switches_menu()
    {
        var service = new CommandService(_system);
        var result = service.Execute("GO MAJOR");

        Assert.Equal(CommandOutcome.GoMenu, result.Outcome);
        Assert.Equal("MAJOR", result.MenuName);
    }

    [Fact]
    public void Go_accepts_qualified_target()
    {
        var service = new CommandService(_system);
        var result = service.Execute("GO QSYS/MAJOR");

        Assert.Equal(CommandOutcome.GoMenu, result.Outcome);
        Assert.Equal("MAJOR", result.MenuName);
        Assert.Equal("QSYS", result.MenuLibrary);
    }

    [Fact]
    public void Sign_off_command_ends_session()
    {
        var service = new CommandService(_system);
        Assert.Equal(CommandOutcome.SignOff, service.Execute("SIGNOFF").Outcome);
    }

    [Fact]
    public void Named_messages_display_reads_the_system_operator_queue()
    {
        var service = new CommandService(_system);
        var result = service.Execute("DSPMSG MSGQ(*SYSOPR)");
        Assert.False(result.IsError, result.Message);
        Assert.Equal("Messages for QSYS/QSYSOPR", result.WorkList!.Title);
        Assert.Empty(result.WorkList.Rows);
    }

    [Fact]
    public void Display_job_reports_active_job()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ADMIN1234");
        var job = _system.Jobs.CreateInteractive("QSECOFR");
        _system.Jobs.CreateInteractive("ANOTHER");
        var service = new CommandService(_system, job);
        var result = service.Execute("DSPJOB");

        Assert.Equal(CommandOutcome.Continue, result.Outcome);
        Assert.Contains("active", result.Message);
        Assert.Contains("QDFTJOB", result.Message);
        Assert.Contains(job.Key.ToString(), result.Message);
        Assert.DoesNotContain("ANOTHER", result.Message);
    }

    [Fact]
    public void Display_job_without_session_context_does_not_select_another_users_job()
    {
        _system.Jobs.CreateInteractive("ANOTHER");
        var service = new CommandService(_system);
        var result = service.Execute("DSPJOB");
        Assert.Equal(CommandOutcome.Error, result.Outcome);
        Assert.DoesNotContain("ANOTHER", result.Message);
    }

    [Fact]
    public void Create_cl_program_then_call_it()
    {
        var sourceFile = Path.Combine(_tempDir, "hello.clp");
        File.WriteAllText(sourceFile, "PGM\nDCL VAR(&MSG) VALUE('Hello from CL')\nSNDPGMMSG MSG(&MSG)\nENDPGM\n");

        var service = new CommandService(_system);
        var created = service.Execute($"CRTCLPGM PGM(HELLO) SRCSTMF('{sourceFile}')");

        Assert.Equal(CommandOutcome.Continue, created.Outcome);
        Assert.Contains("created", created.Message);

        var descriptor = _system.Objects.Get("QSYS", "HELLO", "*PGM");
        Assert.NotNull(descriptor);
        Assert.Equal("CLP", descriptor.Attribute);

        var called = service.Execute("CALL HELLO");
        Assert.Equal(CommandOutcome.Continue, called.Outcome);
        Assert.Contains("Hello from CL", called.Message);
    }

    [Fact]
    public void Called_program_uses_call_parameters()
    {
        var sourceFile = Path.Combine(_tempDir, "greet.clp");
        File.WriteAllText(sourceFile, "PGM PARM(&WHO)\nCHGVAR VAR(&MSG) VALUE('Good day ' + &WHO)\nSNDPGMMSG MSG(&MSG)\nENDPGM\n");

        var service = new CommandService(_system);
        service.Execute($"CRTCLPGM PGM(GREET) SRCSTMF('{sourceFile}')");

        var called = service.Execute("CALL GREET PARM('MATT')");
        Assert.Equal(CommandOutcome.Continue, called.Outcome);
        Assert.Contains("Good day MATT", called.Message);
    }

    [Fact]
    public void Call_missing_program_reports_error()
    {
        var service = new CommandService(_system);
        var result = service.Execute("CALL NOPE");

        Assert.Equal(CommandOutcome.Error, result.Outcome);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public void Menu_command_line_runs_go_command()
    {
        var controller = new MenuController(_system, QsecOfr());
        var evt = FeedText(controller, "GO MAJOR\r");

        Assert.Equal("QSYS/MAJOR", controller.CurrentMenu);
        Assert.False(evt.Last!.EndSession);
    }

    [Fact]
    public void Menu_command_line_creates_and_calls_program()
    {
        var sourceFile = Path.Combine(_tempDir, "hello.clp");
        File.WriteAllText(sourceFile, "PGM\nDCL VAR(&MSG) VALUE('Hello from menu')\nSNDPGMMSG MSG(&MSG)\nENDPGM\n");

        var controller = new MenuController(_system, QsecOfr());
        FeedText(controller, $"CRTCLPGM PGM(HELLO) SRCSTMF('{sourceFile}')\r");
        Assert.Contains("created", controller.Buffer.RowText(24));

        var evt = FeedText(controller, "CALL HELLO\r");
        Assert.Contains("Hello from menu", controller.Buffer.RowText(24));
        Assert.False(evt.Last!.EndSession);
    }

    [Fact]
    public void Menu_command_line_signs_off()
    {
        var controller = new MenuController(_system, QsecOfr());
        var evt = FeedText(controller, "SIGNOFF\r");

        Assert.True(evt.Last!.EndSession);
        Assert.Equal("Sign off.", evt.Last.Message);
    }

    private UserProfile QsecOfr()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get(ProfileNames.QSecOficer), "TEST1234");
        return _system.Security.Profiles.Get(ProfileNames.QSecOficer);
    }

    private static (SessionEvent? Last, List<SessionEvent> All) FeedText(ISessionController controller, string text)
    {
        var parser = new TerminalParser();
        var all = new List<SessionEvent>();
        SessionEvent? last = null;
        foreach (var ch in text)
        {
            foreach (var key in parser.Feed(ch))
            {
                last = controller.Handle(key);
                all.Add(last);
            }
        }

        return (last, all);
    }
}
