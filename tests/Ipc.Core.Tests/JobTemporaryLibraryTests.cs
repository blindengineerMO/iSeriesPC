using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.System;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class JobTemporaryLibraryTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly QualifiedName _space = new("QTEMP", "SPACE");
    private UserSpaceStore Spaces => new(_system.Connections);
    private JobTemporaryLibrary Temporary => new(_system.Connections);
    public JobTemporaryLibraryTests()
    {
        _system.Start();
        foreach (var user in new[] { "QSECOFR", "QUSER" }) _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get(user), "JobTemporaryFixture26");
    }
    private Job Job(string user = "QSECOFR") => _system.Jobs.CreateInteractive(user);
    private static IDisposable Enter(Job job) => OperationIdentity.Enter(job.UserProfile ?? job.Key.User, job.Key);
    private string Backing(Job job) { using var identity = Enter(job); return Temporary.ResolveLibrary("QTEMP"); }
    private static void Ok(Ipc.Cl.Commands.CommandResult result) => Assert.False(result.IsError, result.Message);

    [Fact]
    public void Every_job_has_a_private_library_with_logical_catalog_names()
    {
        var first = Job(); var second = Job(); Assert.NotEqual(Backing(first), Backing(second));
        using (Enter(first))
        {
            var root = _system.Objects.GetRequired("QSYS", "QTEMP", ObjectType.Library);
            Assert.Equal(new QualifiedName("QSYS", "QTEMP"), root.Key); Assert.Equal("QSECOFR", root.Owner);
            Assert.Equal(AuthorityBit.None, root.PublicAuthority);
            Spaces.Create(_space, 3, 0xFE, authority: "*ALL");
            Assert.Equal("QTEMP", _system.Objects.GetRequired("QTEMP", "SPACE", ObjectType.UserSpace).Library);
            Assert.Equal("QTEMP", Assert.Single(_system.Objects.Find("QTEMP", null, ObjectType.UserSpace, null)).Library);
            var libraries = _system.Objects.ListLibraries(); Assert.Contains("QTEMP", libraries);
            Assert.DoesNotContain(libraries, name => name.StartsWith("@T", StringComparison.Ordinal));
        }
        using (Enter(second))
        {
            Assert.Empty(_system.Objects.Find("QTEMP", null, ObjectType.UserSpace, null));
            Spaces.Create(_space, 2, 0xAA);
            Assert.Equal("AAAA", Convert.ToHexString(Spaces.Read(_space, 1, 2)));
        }
        using (Enter(first)) Assert.Equal("FEFEFE", Convert.ToHexString(Spaces.Read(_space, 1, 3)));
        Assert.False(_system.Objects.Exists("QGPL", "SPACE", ObjectType.UserSpace));
    }

    [Theory]
    [InlineData("QSECOFR")]
    [InlineData("QUSER")]
    public void Another_job_cannot_access_a_backing_name_even_with_special_authority(string user)
    {
        var owner = Job("QUSER"); var other = Job(user); var backing = Backing(owner);
        using (Enter(owner)) Spaces.Create(_space, 4, 0xFE, authority: "*ALL");
        using (Enter(other))
        {
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => Spaces.Read(new(backing, "SPACE"), 1, 1)).MessageId);
            Assert.Throws<CpfException>(() => _system.Objects.GetRequired(backing, "SPACE", ObjectType.UserSpace));
            Assert.Throws<CpfException>(() => _system.Objects.GetRequired("QSYS", backing, ObjectType.Library));
            Assert.Throws<CpfException>(() => _system.Objects.Find(backing, null, null, null));
            Assert.Throws<CpfException>(() => new ObjectCatalogOperations(_system.Connections).Delete(new(backing, "SPACE"), ObjectType.UserSpace));
            Assert.DoesNotContain(_system.Objects.Find("QSYS", null, ObjectType.Library, null), d => d.Name == backing);
            Assert.Throws<CpfException>(() => Spaces.Create(new(backing, "NEW"), 1));
        }
        using (OperationIdentity.Enter(user)) Assert.Throws<CpfException>(() => Spaces.Read(new(backing, "SPACE"), 1, 1));
        using (Enter(owner)) Assert.Equal(0xFE, Spaces.Read(_space, 1, 1)[0]);
    }

    [Fact]
    public void Logical_QTEMP_requires_a_live_owned_job_and_cannot_be_reopened_after_completion()
    {
        Assert.Equal("CPF1241", Assert.Throws<CpfException>(() => Spaces.Create(_space, 1)).MessageId);
        var job = Job(); var backing = Backing(job);
        using (Enter(job)) Spaces.Create(_space, 1);
        _system.Jobs.Complete(job.Key);
        using (Enter(job)) Assert.Equal("CPF1241", Assert.Throws<CpfException>(() => Spaces.Read(_space, 1, 1)).MessageId);
        Assert.False(_system.Objects.Exists("QSYS", backing, ObjectType.Library));
        Assert.False(_system.Objects.Exists(backing, "SPACE", ObjectType.UserSpace));
    }

    [Theory]
    [InlineData(JobCompletion.Normal)]
    [InlineData(JobCompletion.Warning)]
    [InlineData(JobCompletion.Abnormal)]
    public void Terminal_outcomes_delete_all_private_objects_and_preserve_permanent_exports(JobCompletion completion)
    {
        var job = Job(); var backing = Backing(job); var catalog = new ObjectCatalogOperations(_system.Connections);
        using (Enter(job))
        {
            Spaces.Create(_space, 2, 0xFE);
            catalog.Relocate(_space, ObjectType.UserSpace, new("QGPL", "EXPORTED"), copy: true);
            _system.Objects.Create(new ObjectDescriptor { Key = new("QTEMP", "ONE"), ObjectType = ObjectType.Program, Source = "PGM\nENDPGM", Attribute = "CLP" });
            _system.Objects.Create(new ObjectDescriptor { Key = new("QTEMP", "TWO"), ObjectType = ObjectType.Program, Source = "PGM\nENDPGM", Attribute = "CLP" });
            catalog.AddDependency(new("QTEMP", "ONE"), ObjectType.Program, new("QTEMP", "TWO"), ObjectType.Program);
            catalog.AddDependency(new("QTEMP", "TWO"), ObjectType.Program, new("QTEMP", "ONE"), ObjectType.Program);
        }
        _system.Jobs.Complete(job.Key, completion); _system.Jobs.Complete(job.Key, completion);
        Assert.Empty(_system.Objects.Find(backing, null, null, null));
        Assert.False(_system.Objects.Exists("QSYS", backing, ObjectType.Library));
        Assert.Equal("FEFE", Convert.ToHexString(Spaces.Read(new("QGPL", "EXPORTED"), 1, 2)));
    }

    [Fact]
    public void Group_jobs_share_GDA_but_have_separate_temporary_objects()
    {
        var parent = Job(); Job child;
        using (Enter(parent)) { Spaces.Create(_space, 1, 255); child = _system.Jobs.CreateGroupJob(parent.Key, "SECONDARY"); }
        using (Enter(child))
        {
            Assert.False(_system.Objects.Exists("QTEMP", "SPACE", ObjectType.UserSpace)); Spaces.Create(_space, 1, 254);
            new JobDataAreaStore(_system.Connections).Write(child.Key, JobDataArea.Group, 0, [0xAA]);
        }
        _system.Jobs.Complete(child.Key);
        using (Enter(parent))
        {
            Assert.Equal(255, Spaces.Read(_space, 1, 1)[0]);
            Assert.Equal(0xAA, new JobDataAreaStore(_system.Connections).Read(parent.Key, JobDataArea.Group)[0]);
        }
    }

    [Fact]
    public void Batch_submission_inherits_library_names_but_starts_with_its_own_empty_QTEMP()
    {
        var parent = Job(); Job child;
        using (Enter(parent))
        {
            Spaces.Create(_space, 1, 255);
            child = _system.Jobs.Submit("CHILD", "", "QBATCH", profile: "QSECOFR", currentLibrary: "QTEMP", libraryList: "QTEMP QGPL", command: "DSPJOB");
        }
        Assert.NotEqual(Backing(parent), BackingForQueued(child));
        var claim = new BatchQueue(_system.Connections, _system.Jobs).ClaimNext(); Assert.NotNull(claim);
        using (Enter(claim.Value.Job))
        {
            Assert.False(_system.Objects.Exists("QTEMP", "SPACE", ObjectType.UserSpace)); Spaces.Create(_space, 1, 254);
        }
        _system.Jobs.Complete(child.Key);
        using (Enter(parent)) Assert.Equal(255, Spaces.Read(_space, 1, 1)[0]);
    }
    private string BackingForQueued(Job job)
    {
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT library FROM sys_job_temp_libraries WHERE job_number=$job"; query.Parameters.AddWithValue("$job", job.Key.Number);
        return (string)query.ExecuteScalar()!;
    }

    [Fact]
    public void Library_identity_is_protected_and_permanent_dependencies_cannot_point_into_QTEMP()
    {
        var job = Job(); var operations = new ObjectCatalogOperations(_system.Connections);
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "PERMANENT"), ObjectType = ObjectType.Program });
        using (Enter(job))
        {
            Spaces.Create(_space, 1);
            Assert.Throws<CpfException>(() => operations.Delete(new("QSYS", "QTEMP"), ObjectType.Library));
            Assert.Throws<CpfException>(() => operations.DeleteLibrary("QTEMP"));
            Assert.Throws<CpfException>(() => operations.Relocate(new("QSYS", "QTEMP"), ObjectType.Library, new("QSYS", "RENAMED")));
            Assert.Equal("IPC0201", Assert.Throws<CpfException>(() => operations.AddDependency(new("QGPL", "PERMANENT"), ObjectType.Program, _space, ObjectType.UserSpace)).MessageId);
            Assert.Throws<CpfException>(() => operations.AddDependency(new("QGPL", "PERMANENT"), ObjectType.Program, new("QSYS", "QTEMP"), ObjectType.Library));
            _system.Objects.Create(new ObjectDescriptor { Key = new("QTEMP", "PRIVATE"), ObjectType = ObjectType.Program });
            operations.AddDependency(new("QTEMP", "PRIVATE"), ObjectType.Program, _space, ObjectType.UserSpace);
            Assert.Throws<CpfException>(() => operations.Relocate(new("QTEMP", "PRIVATE"), ObjectType.Program, new("QGPL", "COPY"), copy: true));
            Assert.Throws<CpfException>(() => operations.Relocate(new("QGPL", "PERMANENT"), ObjectType.Program, new("QTEMP", "PRIVATE")));
            _system.SetSystemValue(SystemValueNames.AllowUserDomain, "QTEMP");
            Spaces.Create(new("QTEMP", "USER"), 1, domain: "*USER");
            Assert.Equal("*USER", Spaces.Inspect(new("QTEMP", "USER")).Domain);
        }
        _system.Jobs.Complete(job.Key);
    }

    [Fact]
    public void Locks_use_private_storage_identity_and_are_reported_with_logical_names()
    {
        var one = Job(); var two = Job(); var locks = new JobLockStore(_system.Connections); IDisposable held;
        using (Enter(one))
        {
            Spaces.Create(_space, 1); held = locks.Acquire(one.Key, new("QTEMP", "SPACE", ObjectType.UserSpace), JobLockMode.Exclusive, TimeSpan.Zero);
            Assert.Equal("QTEMP", Assert.Single(locks.Inspect(new("QTEMP", "SPACE", ObjectType.UserSpace))).Resource.Library);
        }
        try
        {
            using (Enter(two))
            {
                Spaces.Create(_space, 1); using var second = locks.Acquire(two.Key, new("QTEMP", "SPACE", ObjectType.UserSpace), JobLockMode.Exclusive, TimeSpan.Zero);
                Spaces.Write(_space, 1, [255]);
            }
            using (Enter(one)) locks.Release(one.Key, new("QTEMP", "SPACE", ObjectType.UserSpace), JobLockMode.Exclusive);
        }
        finally { held.Dispose(); }
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Native_APIs_and_catalog_commands_use_QTEMP_in_receivers_and_job_library_lookup(int ccsid)
    {
        ClExternalCallTests.Create(_system, "TEMPAPI", "CLP", """
            PGM
            DCL &TEXT *CHAR LEN(50)
            DCL &DATA *CHAR LEN(8)
            DCL &ATTR *CHAR LEN(24)
            DCL &LIB *CHAR LEN(10)
            DCL &CHG *CHAR LEN(16) VALUE(X'0000000100000001000000040000000C')
            CALL QSYS/QUSCRTUS PARM('SPACE     QTEMP     ' '' (8 (*INT 4)) X'FE' '*CHANGE' &TEXT)
            CALL QSYS/QUSCHGUS PARM('SPACE     QTEMP     ' (3 (*INT 4)) (4 (*INT 4)) X'FF00FE01' '0' X'00000000' &LIB)
            IF (&LIB *NE 'QTEMP') THEN(RETURN)
            CALL QSYS/QUSCUSAT PARM(&LIB 'SPACE     QTEMP     ' &CHG X'00000000')
            CALL QSYS/QUSRTVUS PARM('SPACE     *LIBL     ' (1 (*INT 4)) (8 (*INT 4)) &DATA)
            CALL QSYS/QUSRUSAT PARM(&ATTR (24 (*INT 4)) 'SPCA0100' 'SPACE     QTEMP     ' X'00000000')
            IF (&DATA *EQ X'FEFEFF00FE01FEFE' *AND %BIN(&ATTR 9 4) *EQ 12 *AND %SST(&ATTR 15 10) *EQ 'QTEMP') THEN(SNDPGMMSG MSG('PRIVATE QTEMP API ACCEPTED'))
            CALL QSYS/QUSDLTUS PARM('SPACE     QTEMP     ' X'00000000')
            ENDPGM
            """);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        Ok(session.Execute("ADDLIBLE QTEMP"));
        Assert.Contains("CRTUSRSPC", session.AvailableCommands); Assert.NotEmpty(session.DescribeCommand("CRTUSRSPC")); Assert.NotNull(session.CommandMetadata("CRTUSRSPC"));
        var result = session.Execute("CALL QGPL/TEMPAPI"); Ok(result);
        Assert.Contains("PRIVATE QTEMP API ACCEPTED", result.Message);
        Ok(session.Execute("CRTUSRSPC QTEMP/COMMAND 2 VALUE(X'FE')"));
        Ok(session.Execute("DLTUSRSPC COMMAND"));
    }

    [Fact]
    public void Temporary_CL_programs_execute_in_the_same_job_and_move_out_as_permanent_objects()
    {
        var job = Job();
        using (Enter(job)) _system.Objects.Create(new ObjectDescriptor { Key = new("QTEMP", "PRIVATE"), ObjectType = ObjectType.Program, Attribute = "CLP",
            Source = "PGM\nCRTUSRSPC QTEMP/EFFECT 1 VALUE(X'FE')\nSNDPGMMSG MSG('TEMP PROGRAM ACCEPTED')\nENDPGM" });
        using var session = new ExecutionSession(_system, job, CancellationToken.None); var result = session.Execute("CALL QTEMP/PRIVATE");
        Ok(result); Assert.Contains("TEMP PROGRAM ACCEPTED", result.Message);
        using (Enter(job))
        {
            Assert.Equal(0xFE, Spaces.Read(new("QTEMP", "EFFECT"), 1, 1)[0]);
            new ObjectCatalogOperations(_system.Connections).Relocate(new("QTEMP", "PRIVATE"), ObjectType.Program, new("QGPL", "EXPORTED"));
        }
        Assert.True(_system.Objects.Exists("QGPL", "EXPORTED", ObjectType.Program));
    }
    [Fact]
    public void Data_areas_data_queues_and_authorities_use_the_same_private_namespace()
    {
        var first = Job(); var second = Job(); var areas = new DataAreaStore(_system.Connections);
        var queues = new Ipc.Services.Messages.DataQueueStore(_system.Connections);
        using (Enter(first))
        {
            areas.Create("QTEMP", "AREA", "*CHAR", 2, value: "AB"); areas.Change("QTEMP", "AREA", "XY");
            queues.Create(new("QTEMP", "QUEUE"), 2); queues.Send(new("QTEMP", "QUEUE"), new(new byte[] { 255, 0 }, 37));
            Spaces.Create(_space, 1, authority: "*EXCLUDE");
            _system.Security.Authority.Grant("QTEMP", "SPACE", ObjectType.UserSpace, "QUSER", Authorities.UseBits);
            _system.Security.Authority.Revoke("QTEMP", "SPACE", ObjectType.UserSpace, "QUSER");
            Assert.Equal(1, queues.Inspect(new("QTEMP", "QUEUE")).Entries);
        }
        using (Enter(second))
        {
            areas.Create("QTEMP", "AREA", "*CHAR", 2, value: "CD"); queues.Create(new("QTEMP", "QUEUE"), 2);
            Assert.Null(queues.Receive(new("QTEMP", "QUEUE"))); Assert.Equal("CD", ((ProgramBuffer)areas.Read("QTEMP", "AREA").Value).ToText());
        }
        using (Enter(first))
        {
            Assert.Equal("XY", ((ProgramBuffer)areas.Read("QTEMP", "AREA").Value).ToText());
            Assert.Equal("FF00", Convert.ToHexString(queues.Receive(new("QTEMP", "QUEUE"))!.Data.ToArray()));
        }
        _system.Jobs.Complete(first.Key);
        using (Enter(second)) Assert.Equal("CD", ((ProgramBuffer)areas.Read("QTEMP", "AREA").Value).ToText());
    }
    [Fact]
    public void Namespace_allocation_preserves_an_existing_library_with_the_preferred_backing_name()
    {
        _system.Libraries.CreateLibrary("@T00000001");
        var job = Job(); Assert.NotEqual("@T00000001", Backing(job));
        using (Enter(job)) Spaces.Create(_space, 1);
        _system.Jobs.Complete(job.Key);
        Assert.True(_system.Objects.Exists("QSYS", "@T00000001", ObjectType.Library));
    }
    [Fact]
    public void Private_program_signatures_bind_the_backing_identity_and_reject_modified_source()
    {
        var ca = _system.Certificates.CreateAuthority("TEMPCA", "Temporary object signer");
        var signer = _system.Certificates.Issue("TEMPSIGN", "Temporary signer", ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning);
        _system.Certificates.SetTrust(ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning, true);
        var job = Job();
        using (Enter(job))
        {
            _system.Objects.Create(new ObjectDescriptor { Key = new("QTEMP", "SIGNED"), ObjectType = ObjectType.Program, Attribute = "CLP",
                Source = "PGM\nSNDPGMMSG MSG('PRIVATE SIGNATURE ACCEPTED')\nENDPGM" });
            _system.ObjectSigning.Sign("QTEMP", "SIGNED", ObjectType.Program, signer.Id);
            Assert.Equal("VALID", _system.ObjectSigning.Check("QTEMP", "SIGNED", ObjectType.Program).Result);
        }
        using var session = new ExecutionSession(_system, job, CancellationToken.None);
        var result = session.Execute("CALL QTEMP/SIGNED"); Ok(result); Assert.Contains("PRIVATE SIGNATURE ACCEPTED", result.Message);
        using (Enter(job))
        {
            var descriptor = _system.Objects.GetRequired("QTEMP", "SIGNED", ObjectType.Program);
            descriptor.Source = "PGM\nSNDPGMMSG MSG('MODIFIED PRIVATE BODY')\nENDPGM"; _system.Objects.Update(descriptor);
            Assert.Equal("ALTERED", _system.ObjectSigning.Check("QTEMP", "SIGNED", ObjectType.Program).Result);
        }
        result = session.Execute("CALL QTEMP/SIGNED"); Assert.True(result.IsError, result.Message); Assert.DoesNotContain("MODIFIED PRIVATE BODY", result.Message);
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Native_queue_APIs_resolve_QTEMP_and_leave_no_global_queue(int ccsid)
    {
        ClExternalCallTests.Create(_system, "TEMPQUEUE", "CLP", """
            PGM
            DCL &DATA *CHAR LEN(4)
            DCL &LEN *DEC LEN(5 0)
            CRTDTAQ QTEMP/QUEUE MAXLEN(4)
            CALL QSYS/QSNDDTAQ PARM('QUEUE' 'QTEMP' (4 (*DEC 5 0)) X'FF00FE01')
            CALL QSYS/QRCVDTAQ PARM('QUEUE' 'QTEMP' &LEN &DATA (0 (*DEC 5 0)))
            CALL QSYS/QCLRDTAQ PARM('QUEUE' 'QTEMP')
            DLTDTAQ QTEMP/QUEUE
            IF (&LEN *EQ 4 *AND &DATA *EQ X'FF00FE01') THEN(SNDPGMMSG MSG('PRIVATE QUEUE API ACCEPTED'))
            ENDPGM
            """);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var result = session.Execute("CALL QGPL/TEMPQUEUE"); Ok(result); Assert.Contains("PRIVATE QUEUE API ACCEPTED", result.Message);
        Assert.False(_system.Objects.Exists("QGPL", "QUEUE", ObjectType.DataQueue));
    }
    public void Dispose() => _system.Dispose();
}
