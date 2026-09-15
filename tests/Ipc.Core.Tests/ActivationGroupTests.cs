using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class ActivationGroupTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public ActivationGroupTests() => _system.Start();

    [Theory]
    [InlineData("APP", "2")]
    [InlineData("*CALLER", "2")]
    [InlineData("*NEW", "1")]
    public void Activation_groups_control_RPG_static_storage_and_remain_job_scoped(string group, string second)
    {
        Counter(group);
        using var first = Session(); using var other = Session();
        Assert.Equal("1", Call(first)); Assert.Equal(second, Call(first)); Assert.Equal("1", Call(other));
        var info = new JobInformationStore(_system.Connections);
        if (group == "*NEW") Assert.Empty(info.ActivationGroups(first.Job.Key));
        else Assert.All(info.ActivationGroups(first.Job.Key), g => Assert.Equal(0, g.ActiveCalls));
        first.End(JobCompletion.Normal, "done"); Assert.Empty(info.ActivationGroups(first.Job.Key));
    }

    [Fact]
    public void Reclaim_and_LR_reset_storage_and_changed_source_does_not_reuse_old_layout()
    {
        Counter("APP"); using var session = Session();
        Assert.Equal("1", Call(session)); Assert.Equal("2", Call(session));
        Assert.False(session.Execute("RCLACTGRP ACTGRP(APP)").IsError); Assert.Equal("1", Call(session));
        var descriptor = _system.Objects.GetRequired("QGPL", "COUNTER", ObjectType.Program);
        descriptor.Source = CounterSource("10", "seton LR;\n"); _system.Objects.Update(descriptor);
        Assert.Equal("10", Call(session)); Assert.Equal("10", Call(session));
    }

    [Fact]
    public void Caller_groups_share_with_their_children_and_reclaim_rejects_an_active_group()
    {
        Counter("*CALLER"); Program("FIRST", "APP1", "CALL QGPL/COUNTER"); Program("SECOND", "APP2", "CALL QGPL/COUNTER");
        using var session = Session();
        Assert.Equal("1", Call(session, "FIRST")); Assert.Equal("2", Call(session, "FIRST")); Assert.Equal("1", Call(session, "SECOND"));
        Program("RECLAIM", "APP1", "RCLACTGRP ACTGRP(APP1)");
        var denied = session.Execute("CALL PGM(QGPL/RECLAIM)"); Assert.True(denied.IsError); Assert.Contains("active", denied.Message);
        Assert.False(session.Execute("RCLACTGRP ACTGRP(APP1)").IsError); Assert.Equal("1", Call(session, "FIRST"));
        Program("FAIL", "*NEW", "CALL QGPL/MISSING");
        Assert.True(session.Execute("CALL PGM(QGPL/FAIL)").IsError);
        Assert.DoesNotContain(new JobInformationStore(_system.Connections).ActivationGroups(session.Job.Key), g => g.Name.StartsWith("NEW"));
    }

    private ExecutionSession Session() => new(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
    private static string Call(ExecutionSession session, string name = "COUNTER")
    {
        var result = session.Execute("CALL PGM(QGPL/" + name + ")"); Assert.False(result.IsError, result.Message); return result.Message!.Trim();
    }
    private void Counter(string group) => _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "COUNTER"), ObjectType = ObjectType.Program,
        Attribute = "RPG", Source = CounterSource("1"), ExtendedAttributes = new Dictionary<string, string> { ["ipc.program.activationGroup"] = group } });
    private static string CounterSource(string increment, string ending = "")
    {
        var declaration = new string(' ', 80).ToCharArray(); declaration[6] = 'D'; "COUNT".CopyTo(0, declaration, 7, 5);
        declaration[21] = 'S'; declaration[38] = '9'; declaration[40] = '0';
        return new string(declaration) + "\n**free\neval COUNT = COUNT + " + increment + ";\ndsply COUNT;\n" + ending + "return;";
    }
    private void Program(string name, string group, string body) => _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", name), ObjectType = ObjectType.Program,
        Attribute = "CLP", Source = "PGM\n" + body + "\nENDPGM", ExtendedAttributes = new Dictionary<string, string> { ["ipc.program.activationGroup"] = group } });
    public void Dispose() => _system.Dispose();
}
