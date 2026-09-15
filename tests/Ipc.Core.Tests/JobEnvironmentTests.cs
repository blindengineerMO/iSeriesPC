using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class JobEnvironmentTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly JobDataAreaStore _areas;
    public JobEnvironmentTests() { _system.Start(); _areas = new(_system.Connections); }
    private ExecutionSession Session() => new(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
    [Fact]
    public void Lda_is_copied_at_submission_and_never_shares_storage_with_the_parent()
    {
        using var parent = Session(); using var stranger = Session();
        Assert.All(_areas.Read(parent.Job.Key, JobDataArea.Local), value => Assert.Equal((byte)0x40, value));
        _areas.Write(parent.Job.Key, JobDataArea.Local, 0, new byte[] { 1, 2, 3 });
        _system.JobRuntime.Environment(parent.Job.Key)!.Override(new("INPUT", "QGPL", "PARENT", "PARENT", true, JobEnvironmentScope.Job));
        using (OperationIdentity.Enter("QUSER", parent.Job.Key))
        {
            Assert.Throws<CpfException>(() => _areas.Read(stranger.Job.Key, JobDataArea.Local));
            Assert.Throws<CpfException>(() => _system.Jobs.SetLibraries(stranger.Job, "QGPL", "QGPL"));
        }
        var result = parent.Execute("SBMJOB CMD(DSPJOB) JOB(CHILD) INLLIBL(*CURRENT)"); Assert.False(result.IsError, result.Message);
        var child = Assert.Single(_system.Jobs.List(), x => x.Key.Name == "CHILD");
        Assert.Equal(parent.Job.CurrentLibrary, child.CurrentLibrary); Assert.Equal(parent.Job.LibraryList, child.LibraryList);
        _areas.Write(parent.Job.Key, JobDataArea.Local, 0, new byte[] { 9 });
        child = new BatchQueue(_system.Connections, _system.Jobs).ClaimNext()!.Value.Job;
        using var childExecution = new ExecutionSession(_system, child, CancellationToken.None);
        Assert.Empty(_system.JobRuntime.Environment(child.Key)!.Overrides());
        Assert.Equal(0, _system.JobRuntime.Environment(child.Key)!.OpenPathCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, _areas.Read(child.Key, JobDataArea.Local)[..3]);
        _areas.Write(child.Key, JobDataArea.Local, 0, new byte[] { 7 });
        Assert.Equal(9, _areas.Read(parent.Job.Key, JobDataArea.Local)[0]); Assert.Null(_areas.Group(child.Key));
        Assert.All(_areas.Read(child.Key, JobDataArea.Initialization), value => Assert.Equal((byte)0x40, value));
        childExecution.End(JobCompletion.Normal, "done"); Assert.Throws<CpfException>(() => _areas.Read(child.Key, JobDataArea.Local));
    }
    [Fact]
    public void Group_identity_is_explicit_and_gda_lives_until_the_last_group_member_ends()
    {
        using var parent = Session(); using var unrelated = Session();
        var child = _system.Jobs.CreateGroupJob(parent.Job.Key, "GROUP2");
        Assert.Equal(_areas.Group(parent.Job.Key), _areas.Group(child.Key)); Assert.Null(_areas.Group(unrelated.Job.Key));
        _areas.Write(parent.Job.Key, JobDataArea.Group, 511, new byte[] { 17 });
        Assert.Equal(17, _areas.Read(child.Key, JobDataArea.Group)[511]);
        Assert.Throws<CpfException>(() => _areas.Read(unrelated.Job.Key, JobDataArea.Group));
        using (OperationIdentity.Enter("QUSER", unrelated.Job.Key)) Assert.Throws<CpfException>(() => _system.Jobs.CreateGroupJob(parent.Job.Key, "INTRUDER"));
        parent.End(JobCompletion.Normal, "done"); Assert.Equal(17, _areas.Read(child.Key, JobDataArea.Group)[511]);
        _system.Jobs.Complete(child.Key);
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand(); query.CommandText = "SELECT count(*) FROM sys_job_groups";
        Assert.Equal(0L, (long)query.ExecuteScalar()!);
    }
    [Fact]
    public void Routing_initialization_is_bounded_and_independent_of_the_LDA()
    {
        var bytes = new byte[] { 0, 1, 2, 255 };
        var job = _system.Jobs.Submit("PIP", "", "QBATCH", profile: "QUSER", command: "DSPJOB", initializationParameters: bytes);
        bytes[0] = 99; job = new BatchQueue(_system.Connections, _system.Jobs).ClaimNext()!.Value.Job;
        var data = _areas.Read(job.Key, JobDataArea.Initialization); Assert.Equal(2000, data.Length); Assert.Equal(new byte[] { 0, 1, 2, 255 }, data[..4]);
        Assert.Equal(0x40, _areas.Read(job.Key, JobDataArea.Local)[0]);
        Assert.Throws<CpfException>(() => _areas.Write(job.Key, JobDataArea.Initialization, 1999, new byte[] { 1, 2 }));
        Assert.Throws<CpfException>(() => _system.Jobs.Submit("BADPIP", "", "QBATCH", profile: "QUSER", initializationParameters: new byte[2001]));
    }
    [Fact]
    public void Call_overrides_and_open_paths_unwind_without_leaking_to_another_job()
    {
        using var session = Session(); using var other = Session();
        var environment = _system.JobRuntime.Environment(session.Job.Key)!;
        var root = new JobFileOverride("INPUT", "QGPL", "ROOT", "ROOT", true, JobEnvironmentScope.Job);
        environment.Override(root); var disposed = 0;
        using var jobPath = environment.OpenPath("root", true, JobEnvironmentScope.Job, () => new Probe(() => disposed++));
        using (environment.EnterCall())
        {
            environment.Override(root with { Name = "CHILD", Scope = JobEnvironmentScope.Call });
            using var local = environment.OpenPath("child", false, JobEnvironmentScope.Call, () => new Probe(() => disposed++));
            using (environment.EnterCall())
            {
                Assert.Equal("CHILD", environment.Resolve("INPUT")!.Name);
                using var shared = environment.OpenPath<Probe>("root", true, JobEnvironmentScope.Call, () => throw new Exception("Must reuse parent path."));
                Assert.Same(jobPath.Value, shared.Value);
            }
            Assert.Equal(0, disposed);
        }
        Assert.Equal(1, disposed); Assert.Equal(root, environment.Resolve("INPUT"));
        Assert.Null(_system.JobRuntime.Environment(other.Job.Key)!.Resolve("INPUT"));
        using (OperationIdentity.Enter("QUSER", other.Job.Key))
        {
            Assert.Throws<CpfException>(() => environment.Resolve("INPUT"));
            Assert.Throws<CpfException>(() => _ = jobPath.Value);
            Assert.Throws<CpfException>(() => jobPath.Dispose());
        }
        session.End(JobCompletion.Normal, "done"); Assert.Equal(2, disposed);
        Assert.Throws<ObjectDisposedException>(() => _ = jobPath.Value);
    }
    [Fact]
    public void Nested_CL_calls_share_data_and_library_changes_but_restore_call_overrides_on_error()
    {
        var files = new Ipc.Db.Store.SqliteFileStore(_system.Connections, _system.Objects);
        files.CreateSourceFile("QGPL", "FIRST"); files.CreateSourceFile("QGPL", "SECOND");
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "CHILD"), ObjectType = ObjectType.Program, Attribute = "CLP", Source = "PGM\nOVRDBF FILE(INPUT) TOFILE(QGPL/SECOND)\nCHGDTAARA DTAARA(*LDA 1 5) VALUE('child')\nCHGCURLIB CURLIB(QUSRSYS)\nCALL QGPL/MISSING\nENDPGM" });
        using var session = Session();
        var result = session.Execute("OVRDBF FILE(INPUT) TOFILE(QGPL/FIRST) OVRSCOPE(*JOB)"); Assert.False(result.IsError, result.Message);
        result = session.Execute("CALL PGM(QGPL/CHILD)"); Assert.True(result.IsError);
        Assert.Equal("FIRST", _system.JobRuntime.Environment(session.Job.Key)!.Resolve("INPUT")!.Name);
        Assert.Equal("QUSRSYS", session.Job.CurrentLibrary);
        result = session.Execute("DSPDTAARA DTAARA(*LDA 1 5)"); Assert.False(result.IsError, result.Message); Assert.Equal("child", Assert.Single(result.Listing!));
    }
    [Fact]
    public void RPG_shared_open_paths_preserve_position_across_calls_and_are_isolated_by_job()
    {
        var files = new Ipc.Db.Store.SqliteFileStore(_system.Connections, _system.Objects);
        var format = new Ipc.Db.Definitions.RecordFormat { Name = "ROWS", Fields = new() {
            new() { Name = "ID", Type = Ipc.Db.Definitions.FieldType.Binary, Length = 4, Sequence = 1 } } };
        format.AssignPositions();
        files.CreatePhysicalFile("QGPL", "ROWS", new() { Name = "ROWS", Attribute = Ipc.Db.Definitions.FileAttribute.Physical, Formats = new() { format } }, "");
        foreach (var id in new[] { 1, 2, 3 }) files.Insert("QGPL", "ROWS", "ROWS", "ROWS", new Dictionary<string, object?> { ["ID"] = id });
        var declaration = new string(' ', 80).ToCharArray(); declaration[6] = 'D'; "ID".CopyTo(0, declaration, 7, 2);
        declaration[21] = 'S'; declaration[38] = '9'; declaration[40] = '0';
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "READER"), ObjectType = ObjectType.Program,
            Attribute = "RPG", Source = new string(declaration) + "\n**free\nread INPUT;\ndsply ID;\nreturn;" });
        using var first = Session(); using var second = Session();
        foreach (var session in new[] { first, second })
        {
            var setup = session.Execute("OVRDBF FILE(INPUT) TOFILE(QGPL/ROWS) SHARE(*YES) OVRSCOPE(*JOB)"); Assert.False(setup.IsError, setup.Message);
        }
        string Read(ExecutionSession session) { var result = session.Execute("CALL PGM(QGPL/READER)"); Assert.False(result.IsError, result.Message); return result.Message!.Trim(); }
        Assert.Equal("1", Read(first)); Assert.Equal("2", Read(first)); Assert.Equal("1", Read(second));
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "CLOSER"), ObjectType = ObjectType.Program,
            Attribute = "RPG", Source = new string(declaration) + "\n**free\nread INPUT;\nclose INPUT;\nreturn;" });
        Assert.False(first.Execute("CALL PGM(QGPL/CLOSER)").IsError);
        Assert.Equal("1", Read(first));
        var resource = new JobLockResource("QGPL", "ROWS", ObjectType.File);
        Assert.Contains(new JobLockStore(_system.Connections).Inspect(resource), x => x.Lifetime == "OpenPath" && x.Job == first.Job.Key);
        first.End(JobCompletion.Normal, "done");
        Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(resource), x => x.Job == first.Job.Key);
        Assert.Equal("2", Read(second));
    }
    [Fact]
    public void Failed_submission_rolls_back_the_environment_with_the_job()
    {
        using var parent = Session();
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_environment_submit BEFORE INSERT ON sys_joblog WHEN new.message_id='CPC1228' BEGIN SELECT RAISE(ABORT,'fixture'); END";
        command.ExecuteNonQuery();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _system.Jobs.Submit("FAIL", "", "QBATCH", profile: "QUSER", command: "DSPJOB", submittingJob: parent.Job.Key));
        command.CommandText = "SELECT count(*) FROM sys_job_environment"; Assert.Equal(1L, (long)command.ExecuteScalar()!);
        Assert.Single(_system.Jobs.List());
    }
    private sealed class Probe(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    public void Dispose() => _system.Dispose();
}
