using Ipc.Cl.Definitions;
using Ipc.Cl.Parsing;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Commands;
using Ipc.Services.Events;
using Ipc.Session.Transport;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed class CommandObjectTests
{
    private const string Source = "CMD PROMPT('Customer report')\nPARM KWD(CUSTOMER) TYPE(*NAME) MIN(1) PROMPT('Customer' 2)\nPARM KWD(COUNT) TYPE(*DEC) LEN(3 0) DFT(10) RANGE(1 100) PROMPT('Count' 1)";
    private static void Seed(IpcSystem system)
    {
        system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "CPP"), ObjectType = ObjectType.Program, Attribute = "CLP", Source = "PGM PARM(&NAME &COUNT)\nSNDPGMMSG MSG(&NAME + ':' + &COUNT)\nENDPGM" });
        var files = new SqliteFileStore(system.Connections, system.Objects); files.CreateSourceFile("QGPL", "CMDSRC", sourceWidth: 240);
        files.SaveSourceMember("QGPL", "CMDSRC", "REPORT", Source, null);
        var result = new CommandService(system).Execute("CRTCMD CMD(QGPL/REPORT) PGM(QGPL/CPP) SRCFILE(QGPL/CMDSRC)"); Assert.False(result.IsError, result.Message);
    }
    [Fact]
    public void Command_compilation_persists_and_shared_text_and_structured_calls_have_identical_results()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-command-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var system = IpcSystem.Create(directory)) { system.Start(); Seed(system); }
            using var reopened = IpcSystem.Create(directory); reopened.Start(); var service = new CommandService(reopened);
            var text = service.Execute("REPORT acme COUNT(12)"); Assert.False(text.IsError, text.Message); Assert.Equal("ACME:12", text.Message);
            Assert.Equal(text.Message, service.RunCommand(CommandParser.Parse("QGPL/REPORT CUSTOMER(ACME) COUNT(12)")).Message);
            Assert.Equal("ACME:10", service.Execute("REPORT ACME").Message);
            Assert.True(service.Execute("REPORT ACME COUNT(1000)").IsError);
            Assert.Contains("CUSTOMER", string.Join(' ', service.Execute("DSPCMD CMD(QGPL/REPORT)").Listing!));
            Assert.Contains("REPORT", service.AvailableCommands);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public void F4_reorders_only_the_prompt_and_preserves_processing_program_argument_order()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); Seed(system);
        using var menu = new MenuController(system, system.Security.Profiles.Get("QUSER"));
        void Type(string value) { var parser = new TerminalParser(); foreach (var c in value) foreach (var key in parser.Feed(c)) menu.Handle(key); }
        Type("REPORT ACME"); menu.Handle(new(AidKey.Pf4)); Assert.Contains("Count", menu.Buffer.RowText(4)); Assert.Contains("ACME", menu.Buffer.RowText(5));
        Type("\u001b[H\u000b12"); menu.Handle(new(AidKey.Enter)); Assert.Contains("ACME:12", menu.Buffer.RowText(24));
    }
    [Fact]
    public void Live_command_and_CPP_authority_metadata_tampering_and_bound_dependencies_are_enforced()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); Seed(system); var service = new CommandService(system);
        using (OperationIdentity.Enter("QUSER")) Assert.False(service.Execute("REPORT ACME").IsError);
        system.Security.Authority.Grant("QGPL", "CPP", ObjectType.Program, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER")) Assert.Contains("CPF9802", service.Execute("REPORT ACME").Message);
        system.Security.Authority.Grant("QGPL", "REPORT", ObjectType.Command, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER")) Assert.Contains("CPF9802", service.Execute("REPORT ACME").Message);
        Assert.True(service.Execute("DLTOBJ OBJ(QGPL/CPP) OBJTYPE(*PGM)").IsError);
        Assert.True(service.Execute("RNMOBJ OBJ(QGPL/CPP) OBJTYPE(*PGM) NEWOBJ(MOVED)").IsError);
        var descriptor = system.Objects.GetRequired("QGPL", "REPORT", ObjectType.Command); descriptor.Source = Source.Replace("RANGE(1 100)", "RANGE(1 200)");
        Assert.Throws<CpfException>(() => system.Objects.Update(descriptor));
        using (var connection = system.Connections.Open())
        using (var tamper = connection.CreateCommand())
        { tamper.CommandText = "UPDATE sys_objects SET source=$source WHERE lib='QGPL' AND name='REPORT' AND type='*CMD'"; tamper.Parameters.AddWithValue("$source", descriptor.Source); tamper.ExecuteNonQuery(); }
        Assert.Contains("IPC0136", service.Execute("REPORT ACME").Message);
    }
    [Fact]
    public void Generic_command_updates_rebind_dependencies_atomically_and_reject_missing_targets()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); Seed(system);
        system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "CPP2"), ObjectType = ObjectType.Program, Attribute = "CLP", Source = "PGM\nENDPGM" });
        var descriptor = system.Objects.GetRequired("QGPL", "REPORT", ObjectType.Command);
        var definition = new CommandDefinitionCompiler().Compile(Source, "QGPL/CPP2");
        descriptor.ExtendedAttributes = new Dictionary<string, string> { [CommandDefinitionStore.MetadataAttribute] = definition.ToJson() };
        system.Objects.Update(descriptor);
        system.Objects.Delete("QGPL", "CPP", ObjectType.Program);
        Assert.Throws<CpfException>(() => system.Objects.Delete("QGPL", "CPP2", ObjectType.Program));
        descriptor.ExtendedAttributes = new Dictionary<string, string> { [CommandDefinitionStore.MetadataAttribute] = (definition with { ProcessingProgram = "QGPL/MISSING" }).ToJson() };
        Assert.Throws<CpfException>(() => system.Objects.Update(descriptor));
        Assert.Equal("QGPL/CPP2", new CommandDefinitionStore(system.Connections).Load("QGPL", "REPORT").ProcessingProgram);
    }
    [Fact]
    public void Failed_replacement_keeps_existing_definition_and_copy_retains_payload_and_dependencies()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); Seed(system); var service = new CommandService(system);
        var store = new CommandDefinitionStore(system.Connections); var original = store.Load("QGPL", "REPORT").ToJson();
        var invalid = new CommandDefinitionCompiler().Compile(Source, "QGPL/MISSING");
        Assert.Throws<CpfException>(() => store.Create("QGPL", "REPORT", Source, invalid, "QSECOFR", replace: true)); Assert.Equal(original, store.Load("QGPL", "REPORT").ToJson());
        Assert.True(service.Execute("CRTCMD CMD(QGPL/REPORT) PGM(QGPL/CPP) SRCFILE(QGPL/CMDSRC)").IsError);
        var copy = service.Execute("CRTDUPOBJ OBJ(REPORT) FROMLIB(QGPL) OBJTYPE(*CMD) TOLIB(QGPL) NEWOBJ(REPORT2)"); Assert.False(copy.IsError, copy.Message);
        Assert.Equal("ACME:10", service.Execute("REPORT2 ACME").Message);
        Assert.False(service.Execute("DLTOBJ OBJ(QGPL/REPORT) OBJTYPE(*CMD)").IsError); Assert.True(service.Execute("DLTOBJ OBJ(QGPL/CPP) OBJTYPE(*PGM)").IsError);
    }
    [Fact]
    public void Changed_command_definition_invalidates_an_open_prompt_and_help_uses_its_declared_group()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); Seed(system);
        new Ipc.Dsp.HelpPanelStore(system.Connections).Create("QGPL", "CMDHELP", ":PNLGRP.:HELP NAME=GENERAL.Report help:P.Enter the customer code.:EHELP.:EPNLGRP.");
        var service = new CommandService(system);
        Assert.False(service.Execute("CRTCMD CMD(QGPL/REPORT) PGM(QGPL/CPP) SRCFILE(QGPL/CMDSRC) HLPPNLGRP(QGPL/CMDHELP) HLPID(GENERAL) REPLACE(*YES)").IsError);
        using var menu = new MenuController(system, system.Security.Profiles.Get("QUSER"));
        foreach (var c in "REPORT ACME") menu.Handle(new(AidKey.None, Character: c)); menu.Handle(new(AidKey.Pf4));
        menu.Handle(new(AidKey.Pf1)); Assert.Contains("Report help", menu.Buffer.RowText(1)); menu.Handle(new(AidKey.Pf3));
        var files = new SqliteFileStore(system.Connections, system.Objects); var snapshot = files.ReadSourceMember("QGPL", "CMDSRC", "REPORT");
        files.SaveSourceMember("QGPL", "CMDSRC", "REPORT", Source.Replace("DFT(10)", "DFT(20)"), snapshot.Revision);
        Assert.False(service.Execute("CRTCMD CMD(QGPL/REPORT) PGM(QGPL/CPP) SRCFILE(QGPL/CMDSRC) REPLACE(*YES)").IsError);
        menu.Handle(new(AidKey.Enter)); Assert.Contains("definition changed", menu.Buffer.RowText(24));
        Assert.DoesNotContain("ACME:", menu.Buffer.RowText(24));
    }

    [Fact]
    public void Builtin_command_object_authority_applies_to_direct_execution_and_F4()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        system.Security.Authority.Grant("QSYS", "DSPJOB", ObjectType.Command, "QUSER", AuthorityBit.None);
        using var menu = new MenuController(system, system.Security.Profiles.Get("QUSER"));
        foreach (var c in "DSPJOB") menu.Handle(new(AidKey.None, Character: c));
        menu.Handle(new(AidKey.Pf4)); Assert.Contains("CPF9802", menu.Buffer.RowText(24)); Assert.DoesNotContain("Prompt command", menu.Buffer.RowText(1));
        menu.Handle(new(AidKey.Enter)); Assert.Contains("CPF9802", menu.Buffer.RowText(24));
    }

    [Fact]
    public async Task Authenticated_headless_client_uses_the_same_stored_definition_and_validation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-command-api-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var setup = IpcSystem.Create(directory)) { setup.Start(); Seed(setup); setup.Security.Profiles.Create(new UserProfile { Name = "CMDUSER" }); setup.Security.Profiles.SetPassword(setup.Security.Profiles.Get("CMDUSER"), "ClientPassword22"); }
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var server = new SessionServer(directory); var run = server.RunAsync(stop.Token);
            await server.Ready.WaitAsync(stop.Token);
            try
            {
                await using var client = await CommandConnection.ConnectAsync(server.SocketPath, "CMDUSER", "ClientPassword22", stop.Token);
                var result = await client.ExecuteAsync("REPORT ACME COUNT(12)", stop.Token); Assert.True(result.Success, result.Result?.Message); Assert.Equal("ACME:12", result.Result!.Message);
                Assert.False((await client.ExecuteAsync("REPORT ACME COUNT(101)", stop.Token)).Success);
                await client.SignoffAsync(stop.Token);
            }
            finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
