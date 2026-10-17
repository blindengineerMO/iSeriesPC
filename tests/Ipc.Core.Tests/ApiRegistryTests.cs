using Ipc.Api;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services;
using Ipc.Services.Work;
using Ipc.Session;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed class ApiRegistryTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public ApiRegistryTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ApiRegistry27");
    }
    [Fact]
    public void Registry_documents_every_callable_adapter_and_filters_real_contracts()
    {
        Assert.Equal(BuiltinProgramService.Names, ApiContractCatalog.All.Select(a => a.Name).Order(StringComparer.Ordinal));
        var commands = new CommandService(_system); var result = commands.Execute("WRKAPILS"); Assert.False(result.IsError, result.Message);
        Assert.Equal(BuiltinProgramService.Names.Count, result.WorkList!.Rows.Count);
        result = commands.Execute("WRKAPILS API(QMH*)"); Assert.Equal(6, result.WorkList!.Rows.Count);
        var receive = Assert.Single(result.WorkList.Rows, r => r.Key == "QMHRCVPM");
        Assert.Contains(receive.Actions.Single().Details!, line => line.Contains("RCVM0100, RCVM0200", StringComparison.Ordinal));
        Assert.Contains(receive.Actions.Single().Details!, line => line.Contains("10, 12, 14, 15", StringComparison.Ordinal));
        Assert.Empty(commands.Execute("WRKAPILS API(UNAVAILABLE)").WorkList!.Rows);
    }
    [Fact]
    public void Registry_reports_missing_or_invalid_slots_without_replacing_or_invoking_programs()
    {
        _system.Objects.Delete("QSYS", "QMHSNDPM", ObjectType.Program);
        var result = new CommandService(_system).Execute("WRKAPILS API(QMHSNDPM)");
        Assert.Contains("Not installed", Assert.Single(result.WorkList!.Rows).Text); Assert.False(_system.Objects.Exists("QSYS", "QMHSNDPM", ObjectType.Program));
        var descriptor = _system.Objects.GetRequired("QSYS", "QMHRCVPM", ObjectType.Program);
        descriptor.Attribute = "CLP"; descriptor.Source = "PGM\nSNDPGMMSG MSG('UNREACHED')\nENDPGM"; _system.Objects.Update(descriptor);
        result = new CommandService(_system).Execute("WRKAPILS API(QMHRCVPM)"); Assert.Contains("Blocked IPC0136", Assert.Single(result.WorkList!.Rows).Text);
    }
    [Fact]
    public void Registry_obeys_command_authority_and_shows_live_API_authority_failures()
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "READER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("READER"), "RegistryReader27");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("READER"), CancellationToken.None);
        _system.Security.Authority.Grant("QSYS", "QMHRCVPM", ObjectType.Program, "READER", AuthorityBit.None);
        var result = session.Execute("WRKAPILS API(QMHRCVPM)"); Assert.False(result.IsError, result.Message);
        Assert.Contains("Blocked CPF9802", Assert.Single(result.WorkList!.Rows).Text);
        _system.Security.Authority.Grant("QSYS", "WRKAPILS", ObjectType.Command, "READER", AuthorityBit.None);
        Assert.Equal("CPF9802", session.Execute("WRKAPILS").MessageId);
    }
    [Fact]
    public void Terminal_opens_and_returns_from_the_live_API_contract_view()
    {
        using var menu = new MenuController(_system, _system.Security.Profiles.Get("QSECOFR"));
        foreach (var c in "WRKAPILS API(QMHRCVPM)") menu.Handle(new(AidKey.None, Character: c));
        Assert.Equal("WorkWith", menu.Handle(new(AidKey.Enter)).State);
        menu.Handle(new(AidKey.None, Character: '5')); menu.Handle(new(AidKey.Enter));
        Assert.Contains("RCVM0100", string.Concat(menu.Buffer.Positions().Select(p => menu.Buffer[p.Row, p.Column].Value)));
        menu.Handle(new(AidKey.Pf3)); menu.Handle(new(AidKey.Pf3)); Assert.Contains("Selection", menu.Buffer.RowText(23));
    }
    [Theory]
    [InlineData("WRKAPILS API(QMH*) UNKNOWN(*YES)")]
    [InlineData("WRKAPILS QMHRCVPM")]
    [InlineData("WRKAPILS API(QMH*RCV*)")]
    public void Registry_rejects_unsupported_parameters_and_invalid_selectors(string command)
        => Assert.True(new CommandService(_system).Execute(command).IsError);
    public void Dispose() => _system.Dispose();
}
