using Ipc.Console.Session;
using Ipc.Core.Menu;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed class InteractiveGroupTests
{
    private static SessionEvent Command(MenuController menu, string command)
    {
        var parser = new TerminalParser();
        foreach (var c in command) foreach (var key in parser.Feed(c)) menu.Handle(key);
        return menu.Handle(new(AidKey.Enter));
    }
    [Fact]
    public void Initial_program_runs_in_the_authenticated_job_before_menu_resolution()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "INITIAL"), ObjectType = ObjectType.Program, Attribute = "CLP", Source = "PGM\nCHGCURLIB CURLIB(QUSRSYS)\nENDPGM" });
        system.Menus.Register(new ApplicationMenu { Name = "CUSTOM", Library = "QUSRSYS", Title = "Custom startup" });
        var profile = system.Security.Profiles.Get("QUSER"); profile.InitialProgram = "QGPL/INITIAL"; profile.InitialMenu = "CUSTOM"; profile.InitialCurrentLibrary = "QGPL";
        using var menu = new MenuController(system, profile);
        var job = system.Jobs.GetRequired(menu.JobKey);
        Assert.Equal("QUSRSYS", job.CurrentLibrary); Assert.Equal("QGPL QUSRSYS", job.LibraryList); Assert.Equal(37, job.Ccsid);
        Assert.Contains("Custom startup", menu.Buffer.RowText(1));
        Assert.Equal(1, new JobInformationStore(system.Connections).Accounting(job.Key).Commands);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Startup_failure_or_initial_signoff_revokes_the_session_and_completes_its_job(bool signoff)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        var profile = new UserProfile { Name = "STARTUSER", InitialMenu = signoff ? "*SIGNOFF" : "QGPL/MISSING" };
        system.Security.Profiles.Create(profile); system.Security.Profiles.SetPassword(profile, "Password22");
        if (signoff) system.Security.Authority.Grant("QSYS", "MAIN", ObjectType.Menu, "STARTUSER", AuthorityBit.None);
        using var signon = new SignOnController(system); signon.SignOn.Form.WriteValue(0, "STARTUSER"); signon.SignOn.Form.WriteValue(1, "Password22");
        var result = signon.Handle(new(AidKey.Enter)); Assert.True(result.EndSession); Assert.Null(result.Next);
        var job = Assert.Single(system.Jobs.List()); Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(signoff ? JobCompletion.Normal : JobCompletion.Abnormal, job.CompletionCode);
        using var connection = system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM sys_auth_sessions WHERE revoked=0";
        Assert.Equal(0L, command.ExecuteScalar());
    }
    [Fact]
    public void Group_transfer_preserves_screens_LDA_GDA_libraries_and_ends_every_job()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); using var menu = new MenuController(system, system.Security.Profiles.Get("QUSER"));
        var root = menu.JobKey; Command(menu, "CHGGRPA GRPJOB(HOME) TEXT('Home job')");
        var areas = new JobDataAreaStore(system.Connections); areas.Write(root, JobDataArea.Local, 0, new byte[] { 65 }); areas.Write(root, JobDataArea.Group, 0, new byte[] { 71 });
        Command(menu, "TFRGRPJOB GRPJOB(SECOND)"); var second = menu.JobKey; Assert.NotEqual(root, second);
        Assert.Equal(JobStatus.Held, system.Jobs.GetRequired(root).Status); Assert.Equal(JobStatus.Active, system.Jobs.GetRequired(second).Status);
        Assert.Throws<CpfException>(() => system.Jobs.Release(root));
        Assert.Equal(64, areas.Read(second, JobDataArea.Local)[0]); Assert.Equal(71, areas.Read(second, JobDataArea.Group)[0]);
        Command(menu, "CHGCURLIB CURLIB(QUSRSYS)"); areas.Write(second, JobDataArea.Local, 0, new byte[] { 66 });
        Command(menu, "TFRGRPJOB GRPJOB(HOME)"); Assert.Equal(root, menu.JobKey); Assert.Equal(65, areas.Read(root, JobDataArea.Local)[0]);
        Assert.Equal("QGPL", system.Jobs.GetRequired(root).CurrentLibrary);
        Command(menu, "TFRGRPJOB GRPJOB(*PRV)"); Assert.Equal(second, menu.JobKey); Assert.Equal("QUSRSYS", system.Jobs.GetRequired(second).CurrentLibrary);
        Command(menu, "ENDGRPJOB GRPJOB(*CURRENT)"); Assert.Equal(root, menu.JobKey); Assert.Equal(JobStatus.Completed, system.Jobs.GetRequired(second).Status);
        Assert.True(Command(menu, "SIGNOFF").EndSession); menu.Dispose();
        Assert.All(system.Jobs.List(), j => Assert.Equal(JobCompletion.Normal, j.CompletionCode));
        using var connection = system.Connections.Open(); using var count = connection.CreateCommand(); count.CommandText = "SELECT count(*) FROM sys_terminal_jobs"; Assert.Equal(0L, count.ExecuteScalar());
        count.CommandText = "SELECT count(*) FROM sys_job_groups"; Assert.Equal(0L, count.ExecuteScalar());
        count.CommandText = "SELECT count(*) FROM sys_events WHERE kind='terminal.transferred'"; Assert.True((long)count.ExecuteScalar()! >= 4);
    }
    [Fact]
    public void SysReq_alternate_job_keeps_unsent_input_and_separates_group_data()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); using var menu = new MenuController(system, system.Security.Profiles.Get("QUSER"));
        Command(menu, "CHGGRPA GRPJOB(HOME)"); var root = menu.JobKey;
        foreach (var c in "UNSENT") menu.Handle(new(AidKey.None, Character: c)); var cursor = menu.Buffer.Cursor;
        menu.Handle(new(AidKey.SysReq)); Assert.Contains("System Request", menu.Buffer.RowText(1));
        Command(menu, "1"); var alternate = menu.JobKey; Assert.NotEqual(root, alternate);
        Command(menu, "CHGGRPA GRPJOB(ALTERNATE)"); var areas = new JobDataAreaStore(system.Connections);
        areas.Write(alternate, JobDataArea.Group, 0, new byte[] { 88 }); Assert.Equal(64, areas.Read(root, JobDataArea.Group)[0]);
        menu.Handle(new(AidKey.SysReq)); Command(menu, "1"); Assert.Equal(root, menu.JobKey);
        Assert.Contains("UNSENT", menu.Buffer.RowText(23)); Assert.Equal(cursor, menu.Buffer.Cursor);
        menu.RequestDisconnect(); menu.Dispose(); Assert.All(system.Jobs.List(), j => Assert.Equal(JobCompletion.Abnormal, j.CompletionCode));
    }
    [Fact]
    public void Live_SysReq_authority_revocation_hides_the_request_and_preserves_the_calling_screen()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); using var menu = new MenuController(system, system.Security.Profiles.Get("QUSER"));
        foreach (var c in "UNSENT") menu.Handle(new(AidKey.None, Character: c));
        menu.Handle(new(AidKey.SysReq)); Assert.Contains("System Request", menu.Buffer.RowText(1));
        system.Security.Authority.Grant("QSYS", "SYSREQ", ObjectType.Menu, "QUSER", AuthorityBit.None);
        menu.Handle(new(AidKey.Enter)); Assert.DoesNotContain("System Request", menu.Buffer.RowText(1));
        Assert.Contains("UNSENT", menu.Buffer.RowText(23)); Assert.Contains("CPF9802", menu.Buffer.RowText(24));
        Assert.Single(system.Jobs.List());
    }
    [Fact]
    public void Failed_transfer_rolls_back_suspension_and_crash_recovery_reclaims_group_metadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-groups-" + Guid.NewGuid().ToString("N"));
        try
        {
            JobKey root, child;
            using (var system = IpcSystem.Create(directory))
            {
                system.Start(); root = system.Jobs.CreateInteractive("QUSER").Key;
                var groups = new InteractiveSessionStore(system.Connections); groups.Attach(root); groups.Rename(root, "HOME", "");
                child = system.Jobs.CreateGroupJob(root, "CHILD").Key; groups.Add(root, child, "CHILD");
                using var connection = system.Connections.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_transfer BEFORE INSERT ON sys_events WHEN NEW.kind='terminal.transferred' BEGIN SELECT RAISE(ABORT,'fixture failure'); END"; command.ExecuteNonQuery();
                Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => groups.Transfer(child, root));
                Assert.Equal(JobStatus.Held, system.Jobs.GetRequired(root).Status); Assert.Equal(JobStatus.Active, system.Jobs.GetRequired(child).Status);
                command.CommandText = "DROP TRIGGER fail_transfer"; command.ExecuteNonQuery();
            }
            using (var restored = IpcSystem.Create(directory))
            {
                restored.Start(); restored.Jobs.RecoverInterruptedInteractiveJobs();
                Assert.All(restored.Jobs.List(), job => Assert.Equal(JobCompletion.Abnormal, job.CompletionCode));
                using var connection = restored.Connections.Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM sys_terminal_jobs"; Assert.Equal(0L, command.ExecuteScalar());
                command.CommandText = "SELECT count(*) FROM sys_job_groups"; Assert.Equal(0L, command.ExecuteScalar());
            }
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    [Fact]
    public void Transfers_are_owned_bounded_and_failed_startup_returns_to_the_previous_job()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); using var menu = new MenuController(system, system.Security.Profiles.Get("QUSER"));
        var root = menu.JobKey; Command(menu, "CHGGRPA GRPJOB(HOME)");
        Command(menu, "TFRGRPJOB GRPJOB(BAD) INLGRPPGM(QGPL/MISSING)"); Assert.Equal(root, menu.JobKey); Assert.Equal(JobStatus.Active, system.Jobs.GetRequired(root).Status);
        Assert.Contains(system.Jobs.List(), j => j.Key != root && j.CompletionCode == JobCompletion.Abnormal);
        using var other = new MenuController(system, system.Security.Profiles.Get("QUSER")); Command(other, "CHGGRPA GRPJOB(OTHER)");
        using (OperationIdentity.Enter("QUSER", root)) Assert.Throws<CpfException>(() => new InteractiveSessionStore(system.Connections).Transfer(root, other.JobKey));
        for (var n = 1; n < 16; n++) Command(menu, $"TFRGRPJOB GRPJOB(G{n})");
        var before = menu.JobKey; Command(menu, "TFRGRPJOB GRPJOB(OVERFLOW)"); Assert.Equal(before, menu.JobKey);
        using (OperationIdentity.Enter("QUSER", before)) Assert.Equal(16, new InteractiveSessionStore(system.Connections).List(before).Count);
        Assert.Single(system.Jobs.List(), j => j.Status == JobStatus.Active && j.Key != other.JobKey);
    }
}
