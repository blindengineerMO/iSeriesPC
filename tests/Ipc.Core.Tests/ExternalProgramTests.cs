using System.Diagnostics;
using System.Text.Json;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session.Transport;

namespace Ipc.Core.Tests;

public sealed class ExternalProgramTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-external-test-" + Guid.NewGuid().ToString("N"));
    private readonly IpcSystem _system;
    private static string Runtime => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!;
    private string Fixture => Path.Combine(_directory, "fixture", "Ipc.NativeFixture.dll");
    public ExternalProgramTests()
    {
        _system = IpcSystem.Create(_directory); _system.Start();
        Directory.CreateDirectory(Path.GetDirectoryName(Fixture)!);
        foreach (var extension in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
        {
            var target = Path.ChangeExtension(Fixture, extension);
            File.Copy(Path.ChangeExtension(typeof(NativeFixture.Program).Assembly.Location, extension), target);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private ObjectDescriptor Register(string name, string mode = "echo", int timeout = 60, string? path = null, int protocol = 1)
    {
        _system.ExternalPrograms.Register("QGPL", name, Runtime, new[] { Fixture, mode }.Concat(path is null ? Array.Empty<string>() : new[] { path }),
            new[] { Path.ChangeExtension(Fixture, ".runtimeconfig.json"), Path.ChangeExtension(Fixture, ".deps.json") }, timeout, protocol);
        return _system.Objects.GetRequired("QGPL", name, ObjectType.Program);
    }

    [Fact]
    public void Defined_command_passes_a_native_CPP_typed_scalars_and_binary_list_and_file_layouts()
    {
        var path = Path.Combine(_directory, "command-arguments.json"); Register("CMDCPP", "effect", path: path);
        var source = "CMD\nPARM KWD(LIST) TYPE(*CHAR) LEN(3) MAX(3)\nPARM KWD(FILE) TYPE(QFILE) FILE(*IN)\nPARM KWD(COUNT) TYPE(*INT4)\nQFILE: QUAL TYPE(*NAME) LEN(10)\nQUAL TYPE(*NAME) LEN(10) DFT(*LIBL)";
        var definition = new Ipc.Cl.Definitions.CommandDefinitionCompiler().Compile(source, "QGPL/CMDCPP");
        new Ipc.Services.Commands.CommandDefinitionStore(_system.Connections).Create("QGPL", "NATIVECMD", source, definition, "QSECOFR");
        var result = new CommandService(_system).Execute("NATIVECMD LIST(A B) FILE(QGPL/ORDERS) COUNT(12)"); Assert.False(result.IsError, result.Message);
        using var document = JsonDocument.Parse(File.ReadAllText(path)); var arguments = document.RootElement;
        Assert.Equal("0002C14040C24040", Convert.ToHexString(Ipc.Core.Text.CodePage.ToBytes(37, arguments[0].GetString()!)));
        Assert.Equal("ORDERS    QGPL      ", arguments[1].GetString()); Assert.Equal(12, arguments[2].GetInt32());
    }

    [Fact]
    public void Defined_command_uses_version_two_binary_lists_in_a_UTF8_job()
    {
        var path = Path.Combine(_directory, "utf8-command.json"); Register("UTF8CPP", "effect", path: path, protocol: 2);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "BufferFixture9!");
        var job = _system.Jobs.CreateInteractive("QSECOFR", ccsid: 1208);
        var source = "CMD\nPARM KWD(NUMBERS) TYPE(*INT2) MAX(3)\nPARM KWD(TEXT) TYPE(*CHAR) LEN(8)";
        var definition = new Ipc.Cl.Definitions.CommandDefinitionCompiler().Compile(source, "QGPL/UTF8CPP");
        new Ipc.Services.Commands.CommandDefinitionStore(_system.Connections).Create("QGPL", "UTF8CMD", source, definition, "QSECOFR");
        var result = new CommandService(_system, job).Execute("UTF8CMD (-2 255) TEXT('é')"); Assert.False(result.IsError, result.Message);
        using var document = JsonDocument.Parse(File.ReadAllText(path)); var arguments = document.RootElement;
        Assert.Equal("buffer", arguments[0].GetProperty("type").GetString()); Assert.Equal(1208, arguments[0].GetProperty("ccsid").GetInt32());
        Assert.Equal("0002FFFE00FF", Convert.ToHexString(arguments[0].GetProperty("data").GetBytesFromBase64()));
        Assert.Equal("é", arguments[1].GetString());
    }

    [Fact]
    public void Version_two_native_buffers_preserve_all_bytes_under_UTF8_and_return_them_without_text_conversion()
    {
        var program = Register("BUFFERS", protocol: 2);
        var input = new Ipc.Core.Work.ProgramBuffer(Enumerable.Range(0, 256).Select(n => (byte)n).ToArray(), 1208);
        var result = _system.ExternalPrograms.Execute(program, new object?[] { input, 12m, "plain" });
        Assert.True(result.Success, result.Message);
        var output = Assert.IsType<Ipc.Core.Work.ProgramBuffer>(result.Parameters[0]);
        Assert.Equal(1208, output.Ccsid); Assert.Equal(input.ToArray(), output.ToArray());
        Assert.Equal(12m, result.Parameters[1]); Assert.Equal("plain", result.Parameters[2]);
        var copy = output.ToArray(); copy[0] = 99; Assert.Equal(0, output.ToArray()[0]);
        Assert.Throws<CpfException>(() => input.ToText());
        var legacy = Register("LEGACY");
        Assert.Throws<CpfException>(() => _system.ExternalPrograms.Execute(legacy, new object?[] { input }));
    }

    [Fact]
    public void UTF8_command_list_bytes_pass_through_CL_native_call_and_named_data_area()
    {
        Register("RAWECHO", protocol: 2);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "RawCharacterFixture9");
        ClExternalCallTests.Create(_system, "RAWCL", "CLP", "PGM PARM(&RAW)\nDCL &RAW *CHAR LEN(6)\nCALL QGPL/RAWECHO PARM(&RAW)\nCRTDTAARA DTAARA(QGPL/BYTES) TYPE(*CHAR) LEN(6) VALUE(&RAW)\nRETURN");
        var source = "CMD\nPARM KWD(NUMBERS) TYPE(*INT2) MAX(3)";
        var definition = new Ipc.Cl.Definitions.CommandDefinitionCompiler().Compile(source, "QGPL/RAWCL");
        new Ipc.Services.Commands.CommandDefinitionStore(_system.Connections).Create("QGPL", "RAWCMD", source, definition, "QSECOFR");
        var job = _system.Jobs.CreateInteractive("QSECOFR", ccsid: 1208);
        var result = new CommandService(_system, job).Execute("RAWCMD NUMBERS(-2 255)"); Assert.False(result.IsError, result.Message);
        var data = new DataAreaStore(_system.Connections).Read("QGPL", "BYTES");
        Assert.Equal("0002FFFE00FF", Convert.ToHexString(Assert.IsType<ProgramBuffer>(data.Value).ToArray()));
    }

    [Fact]
    public void Native_process_receives_typed_parameters_and_CL_preserves_spaces_and_quotes()
    {
        var descriptor = Register("ECHO");
        var result = _system.ExternalPrograms.Execute(descriptor, new object?[] { "-c 'danger' with spaces", 12.25m, true, null });
        Assert.True(result.Success);
        Assert.Equal(new object?[] { "-c 'danger' with spaces", 12.25m, true, null }, result.Parameters);
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "WRAPPER"), ObjectType = ObjectType.Program, Attribute = "CLP",
            Source = "PGM\nCALL PGM(QGPL/ECHO) PARM('two words' 'it''s intact')\nENDPGM" });
        var call = new CommandService(_system).Execute("CALL PGM(QGPL/WRAPPER)");
        Assert.False(call.IsError, call.Message);
        Assert.Contains("two words", call.Message);
        Assert.Contains("intact", call.Message);
        // Direct CALL and the CL fallback must both use the same scalar pipe protocol.
        call = new CommandService(_system).Execute("CALL PGM(QGPL/ECHO) PARM('two words' 'it''s intact')");
        Assert.False(call.IsError, call.Message);
        Assert.Contains("two words", call.Message);
        Assert.Contains("intact", call.Message);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Native_protocol_two_CALL_constants_have_identical_direct_and_compiled_wire_bytes(int ccsid)
    {
        var path = Path.Combine(_directory, "call-constants.json"); Register("CONSTABI", "effect", path: path, protocol: 2);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ConstantFixture22");
        var job = _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid);
        const string command = "CALL QGPL/CONSTABI PARM('ABC' 25.5 X'FF00' (-2.99 (*INT 4)) (1.5 (*FLT 8)))";
        ClExternalCallTests.Create(_system, "CONSTWRAP", "CLP", command);
        var service = new CommandService(_system, job); byte[][]? previous = null;
        foreach (var call in new[] { command, "CALL QGPL/CONSTWRAP" })
        {
            var result = service.Execute(call); Assert.False(result.IsError, result.Message);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var buffers = document.RootElement.EnumerateArray().Select(value => { Assert.Equal("buffer", value.GetProperty("type").GetString()); Assert.Equal(ccsid, value.GetProperty("ccsid").GetInt32()); return value.GetProperty("data").GetBytesFromBase64(); }).ToArray();
            Assert.Equal(Ipc.Core.Text.CodePage.ToBytes(ccsid, "ABC" + new string(' ', 29)), buffers[0]);
            Assert.Equal("000000002550000C", Convert.ToHexString(buffers[1])); Assert.Equal("FF00", Convert.ToHexString(buffers[2]));
            Assert.Equal("FFFFFFFE", Convert.ToHexString(buffers[3])); Assert.Equal("3FF8000000000000", Convert.ToHexString(buffers[4]));
            if (previous is not null) for (var index = 0; index < buffers.Length; index++) Assert.Equal(previous[index], buffers[index]);
            previous = buffers;
        }
    }

    [Fact]
    public void Invalid_native_temporary_response_prevents_all_reference_writeback()
    {
        Register("BADCONST", "cl-update-bad", protocol: 2);
        ClExternalCallTests.Create(_system, "CALLER", "CLP", "DCL &N *INT VALUE(3)\nCALL QGPL/BADCONST PARM(&N (4 (*INT 4)))\nMONMSG IPC0006\nSNDPGMMSG MSG(&N)");
        var result = new CommandService(_system).Execute("CALL QGPL/CALLER");
        Assert.False(result.IsError, result.Message); Assert.Equal("3", result.Message?.Trim());
    }

    [Theory]
    [InlineData("cl-update", "12:4")]
    [InlineData("cl-update-fail", "12:4")]
    [InlineData("cl-update-bad", "3:4")]
    public void CL_native_buffer_writeback_is_validated_before_assignment_and_survives_reported_failure(string mode, string expected)
    {
        Register("UPDATE", mode, protocol: 2);
        ClExternalCallTests.Create(_system, "CALLER", "CLP", "PGM\nDCL &N *INT VALUE(3)\nDCL &OTHER *INT VALUE(4)\nCALL PGM(QGPL/UPDATE) PARM(&N &OTHER)\nMONMSG (CPF9898 IPC0006)\nSNDPGMMSG MSG(&N *CAT ':' *CAT &OTHER)\nENDPGM");
        var result = new CommandService(_system).Execute("CALL PGM(QGPL/CALLER)");
        Assert.False(result.IsError, result.Message); Assert.EndsWith(expected, result.Message?.Trim());
    }

    [Fact]
    public void Native_aliases_are_rejected_before_process_side_effects()
    {
        var path = Path.Combine(_directory, "aliased-effect.json"); Register("ALIAS", "effect", path: path, protocol: 2);
        ClExternalCallTests.Create(_system, "CALLER", "CLP", "PGM\nDCL &N *INT VALUE(3)\nCALL PGM(QGPL/ALIAS) PARM(&N &N)\nENDPGM");
        var result = new CommandService(_system).Execute("CALL PGM(QGPL/CALLER)");
        Assert.True(result.IsError); Assert.Contains("aliased", result.Message); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("malformed")]
    [InlineData("flood")]
    public void Native_failures_are_command_errors(string mode)
    {
        // This case verifies exit/protocol failures, not startup timing under parallel load.
        // The separate timeout case retains its one-second deadline.
        Register("FAILURE", mode, timeout: 15);
        var result = new CommandService(_system).Execute("CALL PGM(QGPL/FAILURE)");
        Assert.True(result.IsError, result.Message);
        if (mode == "fail") Assert.Contains("code 7", result.Message);
    }

    [Fact]
    public async Task Timeout_and_cancellation_terminate_the_actual_process()
    {
        var pid = Path.Combine(_directory, "pid");
        var program = Register("TIMEOUT", "sleep", 1, pid);
        Assert.Throws<CpfException>(() => _system.ExternalPrograms.Execute(program, Array.Empty<object?>()));
        var processId = int.Parse(File.ReadAllText(pid));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
        File.Delete(pid);
        program = Register("CANCEL", "sleep", 60, pid);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => _system.ExternalPrograms.Execute(program, Array.Empty<object?>(), cancellation.Token));
        while (!File.Exists(pid)) await Task.Delay(20, cancellation.Token);
        processId = int.Parse(File.ReadAllText(pid));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
    }

    [Fact]
    public void Changed_dependencies_and_unprivileged_native_registration_are_rejected()
    {
        var dependency = Path.Combine(_directory, "dependency"); File.WriteAllText(dependency, "one");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(dependency, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _system.ExternalPrograms.Register("QGPL", "PINNED", Runtime, new[] { Fixture, "echo" }, new[] { dependency });
        File.WriteAllText(dependency, "two");
        Assert.Throws<CpfException>(() => _system.ExternalPrograms.Execute(_system.Objects.GetRequired("QGPL", "PINNED", ObjectType.Program), Array.Empty<object?>()));
        _system.Security.Profiles.Create(new UserProfile { Name = "NATIVEUSER", SpecialAuthorities = SpecialAuthority.AllObject });
        using var identity = OperationIdentity.Enter("NATIVEUSER");
        Assert.Throws<CpfException>(() => Register("DENIED"));
        Assert.Throws<CpfException>(() => _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "FORGED"),
            ObjectType = ObjectType.Program, Attribute = ExternalProgramService.Attribute, Owner = "NATIVEUSER", Source = "{}" }));
    }

    [Theory]
    [InlineData("NATIVE")]
    [InlineData("RPG")]
    [InlineData("CLROUTE")]
    [InlineData("UNMATCHED")]
    public async Task SBMJOB_executes_programs_and_routes_with_durable_effect_and_completion_log(string mode)
    {
        var effect = Path.Combine(_directory, "native-effect.json"); Register("NATIVE", "effect", path: effect);
        if (mode == "RPG") _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "RPGPGM"), ObjectType = ObjectType.Program,
            Attribute = "RPG", Source = "**free\ncallp NATIVE('batch value');" });
        if (mode == "CLROUTE")
        {
            _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "ROUTEPGM"), ObjectType = ObjectType.Program, Attribute = "CLP",
                Source = "PGM PARM(&CMD)\nDCL VAR(&CMD) TYPE(*CHAR) LEN(1024)\nSNDPGMMSG MSG(&CMD)\nCALL PGM(QGPL/NATIVE) PARM('batch value')\nENDPGM" });
            new RoutingTable(_system.Connections).EnsureEntry("QBATCH", 1, "QCMDB", "QGPL/ROUTEPGM");
        }
        if (mode == "UNMATCHED") new RoutingTable(_system.Connections).RemoveEntry("QBATCH", 9999);
        _system.Security.Profiles.Create(new UserProfile { Name = "TESTER", PasswordHash = Password("PASS1234") });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token);
        await server.Ready.WaitAsync(stop.Token);
        try
        {
            await using var api = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", stop.Token);
            var command = mode == "RPG" ? "CALL PGM(QGPL/RPGPGM)" : "CALL PGM(QGPL/NATIVE) PARM('batch value')";
            var reply = await api.ExecuteAsync($"SBMJOB CMD({command}) JOB(NATIVEJOB)", stop.Token);
            Assert.True(reply.Success, reply.Result?.Message);
            Job job;
            do { await Task.Delay(25, stop.Token); job = Assert.Single(_system.Jobs.List(), j => j.Key.Name == "NATIVEJOB"); }
            while (job.Status != JobStatus.Completed);
            if (mode == "UNMATCHED")
            {
                Assert.Equal(JobCompletion.Abnormal, job.CompletionCode);
                Assert.Contains("No matching", job.CompletionMessage);
                Assert.False(File.Exists(effect));
                Assert.Contains(_system.Jobs.GetLog(job.Key), e => e.MessageType == "COMPLETION");
                return;
            }
            Assert.Equal(JobCompletion.Normal, job.CompletionCode);
            if (mode == "CLROUTE") Assert.Contains(_system.Jobs.GetLog(job.Key), e => e.Text?.Contains(command) == true);
            Assert.Equal("batch value", JsonDocument.Parse(File.ReadAllText(effect)).RootElement[0].GetString());
            Assert.Contains(_system.Jobs.GetLog(job.Key), e => e.Text?.Contains("native:") == true);
            Assert.Contains(_system.Jobs.GetLog(job.Key), e => e.MessageType == "COMPLETION");
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private static string Password(string value) => Ipc.Services.Security.PasswordHasher.Hash(value);

    [Fact]
    public void Native_job_accounting_records_process_cpu_threads_and_exit()
    {
        Register("CPU", "cpu");
        var job = _system.Jobs.CreateInteractive("QUSER");
        using var execution = new Ipc.Session.ExecutionSession(_system, job, CancellationToken.None);
        var result = execution.Execute("CALL PGM(QGPL/CPU)"); Assert.False(result.IsError, result.Message);
        var info = new JobInformationStore(_system.Connections); var process = Assert.Single(info.Processes(job.Key));
        Assert.Equal("Exited", process.State); Assert.Equal(0, process.ExitCode); Assert.True(process.CpuNanoseconds > 0);
        Assert.True(process.PeakThreads > 0); Assert.Equal(0, info.Accounting(job.Key).ActiveProcesses);
        Assert.Equal(process.CpuNanoseconds, info.Accounting(job.Key).NativeCpuNanoseconds);
    }

    [Fact]
    public async Task Native_timeout_terminates_descendant_processes()
    {
        var pid = Path.Combine(_directory, "tree-pid"); var program = Register("TREE", "tree", 2, pid);
        Assert.Throws<CpfException>(() => _system.ExternalPrograms.Execute(program, Array.Empty<object?>()));
        var childPid = int.Parse(File.ReadAllText(pid + ".child"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            try { using var child = Process.GetProcessById(childPid); if (child.HasExited) break; }
            catch (ArgumentException) { break; }
            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task Server_shutdown_kills_native_batch_process_and_persists_cancelled_outcome()
    {
        var pidFile = Path.Combine(_directory, "native-pid"); Register("SLEEP", "sleep", 60, pidFile);
        _system.Security.Profiles.Create(new UserProfile { Name = "TESTER", PasswordHash = Password("PASS1234") });
        using var stop = new CancellationTokenSource(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token); await server.Ready.WaitAsync(deadline.Token);
        try
        {
            await using var api = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", deadline.Token);
            Assert.True((await api.ExecuteAsync("SBMJOB CMD(CALL PGM(QGPL/SLEEP)) JOB(STOPNATIVE)", deadline.Token)).Success);
            while (!File.Exists(pidFile)) await Task.Delay(20, deadline.Token);
            var processId = int.Parse(File.ReadAllText(pidFile));
            stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10));
            var job = Assert.Single(_system.Jobs.List(), j => j.Key.Name == "STOPNATIVE");
            Assert.Equal(JobExecutionState.Cancelled, job.ExecutionState);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
            Assert.Single(_system.Jobs.GetLog(job.Key), e => e.MessageType == "COMPLETION");
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    public void Dispose() { _system.Dispose(); Directory.Delete(_directory, true); }
}
