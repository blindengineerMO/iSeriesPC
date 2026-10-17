using System.Buffers.Binary;
using System.Diagnostics;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class SystemValueApiIntegrationTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _sources;
    public SystemValueApiIntegrationTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "SystemValueApi27");
        _sources = new(_system.Connections, _system.Objects); _sources.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
    }
    private void Compile(string source, bool rpg = false)
    {
        _sources.SaveSourceMember("QGPL", "SOURCE", "GETSYSVAL", source, null);
        var compiled = new CommandService(_system).Execute((rpg ? "CRTBNDRPG" : "CRTCLPGM") + " PGM(QGPL/GETSYSVAL) SRCFILE(QGPL/SOURCE)");
        Assert.False(compiled.IsError, compiled.Message);
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_CL_uses_native_binary_offsets_and_monitors_invalid_names(int ccsid)
    {
        Compile("""
            PGM
            DCL &INFO *CHAR LEN(128)
            CALL QSYS/QWCRSVAL PARM(&INFO (128 (*INT 4)) (3 (*INT 4)) 'QCCSID    QSECURITY QPWDLVL   ' X'00000000')
            IF (%BIN(&INFO 1 4) *NE 3 *OR %BIN(&INFO 5 4) *NE 16 *OR %BIN(&INFO 9 4) *NE 36 *OR %BIN(&INFO 13 4) *NE 56) THEN(RETURN)
            IF (%BIN(&INFO 33 4) *NE 37 *OR %SST(&INFO 53 2) *NE '40' *OR %BIN(&INFO 73 4) *NE 3) THEN(RETURN)
            CALL QSYS/QWCRSVAL PARM(&INFO (128 (*INT 4)) (1 (*INT 4)) 'UNKNOWN' X'00000000')
            MONMSG CPF1860 EXEC(SNDPGMMSG MSG('CL SYSTEM VALUE API ACCEPTED'))
            ENDPGM
            """);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var result = session.Execute("CALL QGPL/GETSYSVAL"); Assert.False(result.IsError, result.Message); Assert.Contains("CL SYSTEM VALUE API ACCEPTED", result.Message);
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Direct_RPG_receives_binary_and_character_values_and_later_calls_observe_changes(int ccsid)
    {
        Compile("""
            **free
            dcl-s INFO char(128) ccsid(*hex);
            dcl-s SIZE int(10) inz(128);
            dcl-s COUNT int(10) inz(3);
            callp QWCRSVAL(INFO:SIZE:COUNT:'QCCSID    QSECURITY QPWDLVL   ':X'00000000');
            if %subst(INFO:1:4) <> X'00000003' or %subst(INFO:5:4) <> X'00000010';
              return;
            endif;
            if %subst(INFO:33:4) <> X'00000025' or %subst(INFO:73:4) <> X'00000003';
              return;
            endif;
            if %subst(INFO:53:2) = '40';
              sndpgmmsg 'RPG SYSTEM VALUE 40 ACCEPTED';
            else;
              sndpgmmsg 'RPG SYSTEM VALUE 50 ACCEPTED';
            endif;
            seton LR;
            """, rpg: true);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var result = session.Execute("CALL QGPL/GETSYSVAL"); Assert.False(result.IsError, result.Message); Assert.Contains("RPG SYSTEM VALUE 40 ACCEPTED", result.Message);
        _system.SetSystemValue("QSECURITY", "50"); result = session.Execute("CALL QGPL/GETSYSVAL");
        Assert.False(result.IsError, result.Message); Assert.Contains("RPG SYSTEM VALUE 50 ACCEPTED", result.Message);
    }
    [Fact]
    public void Live_API_authority_manifest_and_signing_checks_precede_all_receiver_writes()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "SystemValueReader27");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        var bytes = Enumerable.Repeat((byte)0xAA, 128).ToArray(); ProgramBuffer value = new(bytes, 37);
        var output = new ProgramArgument(() => value, received => value = (ProgramBuffer)received!, v => v, "*CHAR", 128, buffer: () => value);
        object?[] parameters = [output, 128, 1, "QCCSID", new ProgramBuffer(new byte[4], 37)];
        var ca = _system.Certificates.CreateAuthority("SVROOT", "System value API signer");
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true);
        var signer = _system.Certificates.Issue("SVSIGN", "System value API", ca.Id, CertificatePurpose.ObjectSigning);
        _system.ObjectSigning.Sign("QSYS", "QWCRSVAL", ObjectType.Program, signer.Id);
        Assert.False(session.ExecuteProgram("QSYS/QWCRSVAL", parameters).IsError);
        var before = value.ToArray();
        _system.Security.Authority.Grant("QSYS", "QWCRSVAL", ObjectType.Program, "QUSER", AuthorityBit.None);
        Assert.Equal("CPF9802", session.ExecuteProgram("QSYS/QWCRSVAL", parameters).MessageId); Assert.Equal(before, value.ToArray());
        _system.Security.Authority.Grant("QSYS", "QWCRSVAL", ObjectType.Program, "QUSER", Authorities.UseBits);
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, false);
        Assert.True(session.ExecuteProgram("QSYS/QWCRSVAL", parameters).IsError); Assert.Equal(before, value.ToArray());
        _system.Certificates.SetTrust(ca.Id, CertificatePurpose.ObjectSigning, true);
        using var connection = _system.Connections.Open(); using var corrupt = connection.CreateCommand();
        corrupt.CommandText = "UPDATE sys_objects SET source='invalid manifest' WHERE lib='QSYS' AND name='QWCRSVAL' AND type='*PGM'"; corrupt.ExecuteNonQuery();
        Assert.True(session.ExecuteProgram("QSYS/QWCRSVAL", parameters).IsError); Assert.Equal(before, value.ToArray());
    }
    [Fact]
    public void Ordinary_users_can_read_public_values_without_special_authority()
    {
        using var identity = OperationIdentity.Enter("QUSER"); var output = new byte[100]; ProgramBuffer value = new(output, 37);
        var receiver = new ProgramArgument(() => value, v => value = (ProgramBuffer)v!, v => v, "*CHAR", 100, buffer: () => value);
        new Ipc.Api.SystemValueApi(_system.Connections).Retrieve([receiver, 100, 1, "QSECURITY", new ProgramBuffer(new byte[4], 37)]);
        Assert.Equal("40", new ProgramBuffer(value.ToArray().AsSpan(24, 2), 37).ToText());
    }
    [Fact]
    public void Independent_live_hosts_and_reopened_storage_share_committed_system_values()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-shared-sysvals-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = IpcSystem.Create(directory))
            using (var second = IpcSystem.Create(directory))
            {
                first.Start(); second.Start(); first.SetSystemValue("QCCSID", "1208");
                Assert.Equal("1208", second.SystemValues.Get("QCCSID").Value);
                var snapshot = second.SystemValues; snapshot.Set("QCCSID", "37"); Assert.Equal("1208", second.SystemValues.Get("QCCSID").Value);
                ProgramBuffer data = new(new byte[28], 37);
                var receiver = new ProgramArgument(() => data, v => data = (ProgramBuffer)v!, v => v, "*CHAR", 28, buffer: () => data);
                new Ipc.Api.SystemValueApi(second.Connections).Retrieve([receiver, 28, 1, "QCCSID", new ProgramBuffer(new byte[4], 37)]);
                Assert.Equal(1208, BinaryPrimitives.ReadInt32BigEndian(data.ToArray().AsSpan(24)));
                Assert.Contains(new CommandService(second).Execute("WRKSYSVAL SYSVAL(QCCSID)").WorkList!.Rows, row => row.Text.Contains("1208", StringComparison.Ordinal));
            }
            using var restarted = IpcSystem.Create(directory); restarted.Start(); Assert.Equal("1208", restarted.SystemValues.Get("QCCSID").Value);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task A_separate_process_cannot_tear_a_native_multi_value_snapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-system-value-process-" + Guid.NewGuid().ToString("N"));
        Process? writer = null;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using var system = IpcSystem.Create(directory); system.Start();
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { typeof(WorkFixture.Program).Assembly.Location, "system-value-pairs", directory, "100" }) start.ArgumentList.Add(argument);
            writer = Process.Start(start)!; var output = writer.StandardOutput.ReadToEndAsync(); var errors = writer.StandardError.ReadToEndAsync();
            while (!File.Exists(Path.Combine(directory, "ready-" + writer.Id)))
            { Assert.False(writer.HasExited, await ErrorIfExited()); await Task.Delay(10, stop.Token); }
            File.WriteAllText(Path.Combine(directory, "start"), "start");
            ProgramBuffer data = new(new byte[100], 37);
            var receiver = new ProgramArgument(() => data, v => data = (ProgramBuffer)v!, v => v, "*CHAR", 100, buffer: () => data);
            var api = new Ipc.Api.SystemValueApi(system.Connections, cancellationToken: stop.Token); var reads = 0;
            do
            {
                api.Retrieve([receiver, 100, 2, "QCCSID    QPWDLVL   ", new ProgramBuffer(new byte[4], 37)]);
                var bytes = data.ToArray(); var firstOffset = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4));
                var secondOffset = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8));
                var pair = (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(firstOffset + 16)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(secondOffset + 16)));
                Assert.True(pair is (37, 3) or (1208, 4), pair.ToString()); reads++;
            } while (!writer.HasExited);
            await writer.WaitForExitAsync(stop.Token); Assert.True(writer.ExitCode == 0, await errors); Assert.Equal("100", await output); Assert.True(reads > 0);
            async Task<string> ErrorIfExited() => writer.HasExited ? await errors : "";
        }
        finally
        {
            stop.Cancel(); if (writer is { HasExited: false }) { writer.Kill(true); await writer.WaitForExitAsync(); }
            writer?.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    [Theory]
    [InlineData("QSYSLIBL", 15)]
    [InlineData("QUSRLIBL", 25)]
    public void System_value_library_limits_differ_from_the_larger_running_job_list(string name, int maximum)
    {
        var names = Enumerable.Range(1, maximum + 1).Select(n => "LIB" + n).ToArray();
        foreach (var library in names) _system.Libraries.CreateLibrary(library);
        _system.SetSystemValue(name, string.Join(',', names.Take(maximum)));
        Assert.Equal("IPC0003", Assert.Throws<CpfException>(() => _system.SetSystemValue(name, string.Join(',', names))).MessageId);
        Assert.Equal(maximum, _system.SystemValues.Get(name).Value.Split(',').Length);
    }
    public void Dispose() => _system.Dispose();
}
