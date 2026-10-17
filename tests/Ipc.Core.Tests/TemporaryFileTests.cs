using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class TemporaryFileTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public TemporaryFileTests()
    {
        _system.Start();
        foreach (var user in new[] { "QSECOFR", "QUSER" }) _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get(user), "TemporaryFilesFixture26");
    }
    private Job Job(string user = "QSECOFR", int ccsid = 37) => _system.Jobs.CreateInteractive(user, ccsid: ccsid);
    private static IDisposable Enter(Job job) => OperationIdentity.Enter(job.UserProfile ?? job.Key.User, job.Key);
    private SqliteFileStore Files(Job? job = null) => new(_system.Connections, _system.Objects, job?.UserProfile ?? "QSECOFR");
    internal static FileDefinition Definition(string name = "ROWS", bool unique = false)
    {
        var format = new RecordFormat { Name = "ROW", Fields = new() {
            new() { Name = "ID", Type = FieldType.Binary, Length = 4, Sequence = 1 },
            new() { Name = "AMOUNT", Type = FieldType.Packed, Length = 29, Decimals = 2, NullCapable = true },
            new() { Name = "TEXT", Type = FieldType.Alpha, Length = 12, NullCapable = true }
        } }; format.AssignPositions();
        return new() { Name = name, Attribute = FileAttribute.Physical, Unique = unique, Formats = new() { format } };
    }
    private static Dictionary<string, object?> Values(int id, decimal? amount = 0, string? text = "row") => new() { ["ID"] = id, ["AMOUNT"] = amount, ["TEXT"] = text };
    private static void Add(SqliteFileStore files, string library, int id, string file = "ROWS", string? member = null, decimal? amount = 0)
        => files.Insert(library, file, member ?? file, "ROW", Values(id, amount));
    private string Backing(Job job) { using var identity = Enter(job); return new JobTemporaryLibrary(_system.Connections).ResolveLibrary("QTEMP"); }
    private static void Ok(Ipc.Cl.Commands.CommandResult result) => Assert.False(result.IsError, result.Message);
    private void Logical(SqliteFileStore files, string targetLibrary = "QTEMP", string name = "VIEW", string sourceLibrary = "QTEMP", bool unique = false)
    {
        var source = string.Join('\n', (unique ? new[] { LogicalFileTests.Line(' ', "", "UNIQUE") } : Array.Empty<string>()).Concat(new[] {
            LogicalFileTests.Line('R', "VIEWREC", "PFILE(" + sourceLibrary + "/ROWS)"), LogicalFileTests.Line(' ', "ID"), LogicalFileTests.Line(' ', "TEXT"),
            LogicalFileTests.Line('K', "ID"), LogicalFileTests.Line('S', "ID", "COMP(GT 0)") }));
        var definition = new LogicalDdsCompiler(_ => (new(sourceLibrary, "ROWS"), files.GetDefinition(sourceLibrary, "ROWS")!)).Compile(name, source);
        files.CreateLogicalFile(targetLibrary, name, definition, source, maximumMembers: 4);
    }

    [Theory]
    [InlineData("QSECOFR")]
    [InlineData("QUSER")]
    public void Same_names_and_members_are_private_to_each_job(string user)
    {
        var first = Job(user); var second = Job(user); var files = Files(first);
        using (Enter(first)) { files.CreatePhysicalFile("QTEMP", "ROWS", Definition(), ""); Add(files, "QTEMP", 1); }
        using (Enter(second)) { files.CreatePhysicalFile("QTEMP", "ROWS", Definition(), ""); Add(files, "QTEMP", 2); }
        using (Enter(first))
        {
            Assert.True(files.FileExists("QTEMP", "ROWS")); Assert.True(files.MemberExists("QTEMP", "ROWS", "ROWS"));
            Assert.Equal("ROWS", files.FirstMember("QTEMP", "ROWS")); Assert.Equal(new[] { "ROWS" }, files.ListMembers("QTEMP", "ROWS"));
            Assert.Equal(1L, Assert.Single(files.ReadAll("QTEMP", "ROWS", "ROWS"))["ID"]);
        }
        using (Enter(second)) Assert.Equal(2L, Assert.Single(files.ReadAll("QTEMP", "ROWS", "ROWS"))["ID"]);
        Assert.False(files.FileExists("QGPL", "ROWS"));
    }
    [Fact]
    public void Physical_member_limits_clear_remove_and_CRUD_use_byte_and_decimal_safe_private_tables()
    {
        var job = Job(); var files = Files(job);
        using (Enter(job))
        {
            files.CreatePhysicalFile("QTEMP", "ROWS", Definition(), "", member: "*NONE", maximumMembers: 2);
            files.AddMember("QTEMP", "ROWS", "FIRST"); files.AddMember("QTEMP", "ROWS", "SECOND");
            Assert.Throws<CpfException>(() => files.AddMember("QTEMP", "ROWS", "THIRD"));
            files.ChangePhysicalMemberLimit("QTEMP", "ROWS", 3); files.AddMember("QTEMP", "ROWS", "THIRD");
            Add(files, "QTEMP", 2, member: "FIRST", amount: 9007199254740992.02m); Add(files, "QTEMP", 1, member: "FIRST", amount: null);
            Assert.Equal(new long[] { 1, 2 }, files.ReadKeyed("QTEMP", "ROWS", "FIRST").Select(row => (long)row["ID"]!));
            Assert.Equal(9007199254740992.02m, Assert.Single(files.ReadKeyPrefix("QTEMP", "ROWS", "FIRST", new Dictionary<string, object?> { ["ID"] = 2 }))["AMOUNT"]);
            files.Update("QTEMP", "ROWS", "FIRST", "ROW", Values(2, -123.45m, "changed"));
            Assert.Equal(-123.45m, Assert.Single(files.ReadKeyPrefix("QTEMP", "ROWS", "FIRST", new Dictionary<string, object?> { ["ID"] = 2 }))["AMOUNT"]);
            files.Delete("QTEMP", "ROWS", "FIRST", "ROW", new Dictionary<string, object?> { ["ID"] = 1 });
            Assert.Equal(1, files.RowCount("QTEMP", "ROWS", "FIRST")); files.ClearMember("QTEMP", "ROWS", "FIRST");
            Assert.Equal(0, files.RowCount("QTEMP", "ROWS", "FIRST")); files.RemoveMember("QTEMP", "ROWS", "THIRD");
            Assert.Equal(new[] { "FIRST", "SECOND" }, files.ListMembers("QTEMP", "ROWS")); files.DeleteFile("QTEMP", "ROWS");
            Assert.False(files.FileExists("QTEMP", "ROWS"));
        }
    }
    [Fact]
    public void Source_revisions_and_members_are_isolated_and_conflicting_saves_do_not_overwrite()
    {
        var first = Job(); var second = Job(); var files = Files(); SourceMemberSnapshot saved;
        using (Enter(first))
        {
            files.CreateSourceFile("QTEMP", "SOURCE", sourceWidth: 240);
            saved = files.SaveSourceMember("QTEMP", "SOURCE", "MAIN", "PGM\r\nSNDPGMMSG MSG('FIRST')\r\nENDPGM", null);
            Assert.Equal(saved, files.ReadSourceMember("QTEMP", "SOURCE", "MAIN"));
            var next = files.SaveSourceMember("QTEMP", "SOURCE", "MAIN", "PGM\nENDPGM", saved.Revision);
            Assert.Throws<CpfException>(() => files.SaveSourceMember("QTEMP", "SOURCE", "MAIN", "bad", saved.Revision));
            Assert.Equal(next, files.ReadSourceMember("QTEMP", "SOURCE", "MAIN"));
        }
        using (Enter(second))
        {
            files.CreateSourceFile("QTEMP", "SOURCE"); files.SaveSourceMember("QTEMP", "SOURCE", "MAIN", "SECOND", null);
            Assert.Equal("SECOND\n", files.ReadSourceMember("QTEMP", "SOURCE", "MAIN").Source);
        }
        using (Enter(first)) Assert.Equal("PGM\nENDPGM\n", files.ReadSourceMember("QTEMP", "SOURCE", "MAIN").Source);
    }
    [Theory]
    [InlineData("QSECOFR")]
    [InlineData("QUSER")]
    public void Known_foreign_backing_names_cannot_read_or_mutate_files(string user)
    {
        var owner = Job("QUSER"); var other = Job(user); var files = Files(owner); var backing = Backing(owner);
        using (Enter(owner)) { files.CreatePhysicalFile("QTEMP", "ROWS", Definition(), ""); Add(files, "QTEMP", 1); files.CreateSourceFile("QTEMP", "SOURCE"); }
        using (Enter(other))
        {
            Action[] denied = [ () => files.GetDefinition(backing, "ROWS"), () => files.ReadAll(backing, "ROWS", "ROWS"),
                () => files.ReadKeyed(backing, "ROWS", "ROWS"), () => files.ReadKeyPrefix(backing, "ROWS", "ROWS", new Dictionary<string, object?> { ["ID"] = 1 }),
                () => files.RowCount(backing, "ROWS", "ROWS"), () => files.ListMembers(backing, "ROWS"), () => files.AddMember(backing, "ROWS", "OTHER"),
                () => files.ClearMember(backing, "ROWS", "ROWS"), () => Add(files, backing, 2), () => files.Update(backing, "ROWS", "ROWS", "ROW", Values(1)),
                () => files.Delete(backing, "ROWS", "ROWS", "ROW", Values(1)), () => files.ChangePhysicalMemberLimit(backing, "ROWS", 3),
                () => files.ReadSourceMember(backing, "SOURCE", "MAIN"), () => files.SaveSourceMember(backing, "SOURCE", "MAIN", "bad", null),
                () => files.OpenSequentialCursor(backing, "ROWS", "ROWS"), () => files.DeleteFile(backing, "ROWS") ];
            foreach (var action in denied) Assert.Equal("CPF9802", Assert.Throws<CpfException>(action).MessageId);
        }
        using (Enter(owner)) Assert.Equal(1, files.RowCount("QTEMP", "ROWS", "ROWS"));
    }
    [Fact]
    public void Logical_members_selection_updates_and_unique_paths_bind_the_private_physical_file()
    {
        var job = Job(); var files = Files(job); var backing = Backing(job);
        using (Enter(job))
        {
            files.CreatePhysicalFile("QTEMP", "ROWS", Definition(), ""); Add(files, "QTEMP", -1); Add(files, "QTEMP", 2); Add(files, "QTEMP", 1);
            Logical(files, unique: true); Assert.Equal(new long[] { 1, 2 }, files.ReadKeyed("QTEMP", "VIEW", "VIEW").Select(row => (long)row["ID"]!));
            files.Update("QTEMP", "VIEW", "VIEW", "VIEWREC", new Dictionary<string, object?> { ["ID"] = 2, ["TEXT"] = "changed" });
            Assert.Equal("changed", ((string)Assert.Single(files.ReadKeyPrefix("QTEMP", "ROWS", "ROWS", new Dictionary<string, object?> { ["ID"] = 2 }))["TEXT"]!).TrimEnd());
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Add(files, "QTEMP", 2));
            files.AddMember("QTEMP", "ROWS", "OTHER"); Add(files, "QTEMP", 9, member: "OTHER");
            files.AddLogicalMember("QTEMP", "VIEW", "EXTRA", ["OTHER"]); Assert.Equal(9L, Assert.Single(files.ReadAll("QTEMP", "VIEW", "EXTRA"))["ID"]);
            Assert.Throws<CpfException>(() => files.RemoveMember("QTEMP", "ROWS", "OTHER"));
            files.RemoveMember("QTEMP", "VIEW", "EXTRA"); files.RemoveMember("QTEMP", "ROWS", "OTHER");
            using var cursor = files.OpenSequentialCursor("QTEMP", "VIEW", "VIEW"); Assert.Equal(1L, cursor.Read()!["ID"]); Assert.Equal(2L, cursor.Read()!["ID"]); Assert.Null(cursor.Read());
        }
        _system.Jobs.Complete(job.Key); Assert.False(files.FileExists(backing, "ROWS"));
    }
    [Fact]
    public void Permanent_logical_files_cannot_capture_temporary_physical_storage()
    {
        var job = Job(); var files = Files(job);
        using (Enter(job))
        {
            files.CreatePhysicalFile("QTEMP", "ROWS", Definition(), ""); Add(files, "QTEMP", 1);
            Assert.Equal("IPC0201", Assert.Throws<CpfException>(() => Logical(files, targetLibrary: "QGPL", unique: true)).MessageId);
            Assert.False(files.FileExists("QGPL", "VIEW"));
        }
        _system.Jobs.Complete(job.Key);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cleanup_releases_private_unique_paths_on_permanent_files_and_preserves_shared_paths(bool shared)
    {
        var files = Files(); files.CreatePhysicalFile("QGPL", "ROWS", Definition(), ""); Add(files, "QGPL", 1);
        if (shared) Logical(files, "QGPL", "GLOBAL", "QGPL", unique: true);
        var job = Job(); var backing = Backing(job);
        using (Enter(job)) Logical(files, sourceLibrary: "QGPL", unique: true);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Add(files, "QGPL", 1));
        _system.Jobs.Complete(job.Key); Assert.False(files.FileExists(backing, "VIEW"));
        if (shared) Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Add(files, "QGPL", 1));
        else Add(files, "QGPL", 1);
        Assert.Equal(shared ? 1 : 2, files.RowCount("QGPL", "ROWS", "ROWS"));
    }
    [Fact]
    public void Record_copy_and_object_export_preserve_data_and_reject_private_dependencies()
    {
        var job = Job(); var files = Files(); files.CreatePhysicalFile("QGPL", "ROWS", Definition(), ""); Add(files, "QGPL", 1);
        using (Enter(job))
        {
            files.CreatePhysicalFile("QTEMP", "ROWS", Definition(), "");
            Assert.Equal(1, files.CopyRecords(new("QGPL", "ROWS"), "ROWS", new("QTEMP", "ROWS"), "ROWS"));
            Assert.Equal(1, files.CopyRecords(new("QTEMP", "ROWS"), "ROWS", new("QTEMP", "ROWS"), "ROWS"));
            Assert.Equal(2, files.RowCount("QTEMP", "ROWS", "ROWS"));
            Assert.Equal(2, files.CopyRecords(new("QTEMP", "ROWS"), "ROWS", new("QGPL", "ROWS"), "ROWS", replace: true));
            Logical(files); Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(new("QTEMP", "VIEW"), ObjectType.File, new("QGPL", "INVALID"), copy: true));
            _system.ObjectOperations.Relocate(new("QTEMP", "ROWS"), ObjectType.File, new("QGPL", "EXPORTED"), copy: true);
            Add(files, "QTEMP", 2); Assert.Equal(2, files.RowCount("QGPL", "EXPORTED", "ROWS"));
        }
        _system.Jobs.Complete(job.Key); Assert.Equal(2, files.RowCount("QGPL", "EXPORTED", "ROWS"));
    }
    [Fact]
    public void Private_cursors_are_owned_and_job_completion_removes_tables_indexes_and_definitions()
    {
        var first = Job(); var second = Job(); var files = Files(); var backing = Backing(first); DatabaseRecordCursor cursor;
        using (Enter(first))
        {
            files.CreatePhysicalFile("QTEMP", "ROWS", Definition(unique: true), ""); Add(files, "QTEMP", 2); Add(files, "QTEMP", 1);
            cursor = files.OpenSequentialCursor("QTEMP", "ROWS", "ROWS"); Assert.Equal(1L, cursor.Read()!["ID"]);
        }
        using (cursor)
        {
            using (Enter(second)) Assert.Throws<CpfException>(() => cursor.Read());
            using (Enter(first)) Assert.Equal(2L, cursor.Read()!["ID"]);
            _system.Jobs.Complete(first.Key);
            using (Enter(first)) Assert.Throws<CpfException>(() => cursor.Read());
        }
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE tbl_name LIKE $prefix"; query.Parameters.AddWithValue("$prefix", backing + ".%"); Assert.Equal(0L, query.ExecuteScalar());
        query.CommandText = "SELECT count(*) FROM sys_file_defs WHERE lib=$lib"; query.Parameters.AddWithValue("$lib", backing); Assert.Equal(0L, query.ExecuteScalar());
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Private_sources_compile_CL_RPG_and_DDS_and_execute_real_database_effects(int ccsid)
    {
        var job = Job(ccsid: ccsid); var files = Files(job);
        using (Enter(job))
        {
            files.CreateSourceFile("QTEMP", "SOURCE", sourceWidth: 240);
            files.SaveSourceMember("QTEMP", "SOURCE", "ROWS", string.Join('\n', NativePhysicalDdsTests.Line('R', "ROW"), NativePhysicalDdsTests.Line(' ', "ID", "9", 'B', "0"), NativePhysicalDdsTests.Line('K', "ID")), null);
            files.SaveSourceMember("QTEMP", "SOURCE", "READER", "PGM\nDCLF FILE(QTEMP/ROWS)\nRCVF\nIF (&ID *EQ 7) THEN(SNDPGMMSG MSG('PRIVATE CL FILE ACCEPTED'))\nCLOSE\nENDPGM", null);
            files.SaveSourceMember("QTEMP", "SOURCE", "RPGCOPY", "sndpgmmsg 'PRIVATE RPG SOURCE ACCEPTED';", null);
            files.SaveSourceMember("QTEMP", "SOURCE", "RPGMAIN", "**free\n/COPY QTEMP/SOURCE,RPGCOPY\nreturn;", null);
        }
        using var session = new ExecutionSession(_system, job, CancellationToken.None);
        Ok(session.Execute("CRTPF FILE(QTEMP/ROWS) SRCFILE(QTEMP/SOURCE)"));
        using (Enter(job)) files.Insert("QTEMP", "ROWS", "ROWS", "ROW", new Dictionary<string, object?> { ["ID"] = 7 });
        Ok(session.Execute("CRTCLPGM PGM(QTEMP/READER) SRCFILE(QTEMP/SOURCE)")); var result = session.Execute("CALL QTEMP/READER"); Ok(result); Assert.Contains("PRIVATE CL FILE ACCEPTED", result.Message);
        Ok(session.Execute("CRTBNDRPG PGM(QTEMP/RPGMAIN) SRCFILE(QTEMP/SOURCE)")); result = session.Execute("CALL QTEMP/RPGMAIN"); Ok(result); Assert.Contains("PRIVATE RPG SOURCE ACCEPTED", result.Message);
        Ok(session.Execute("CHGPF FILE(QTEMP/ROWS) MAXMBRS(3)")); Ok(session.Execute("ADDPFM FILE(QTEMP/ROWS) MBR(OTHER)"));
        Ok(session.Execute("CPYF FROMFILE(QTEMP/ROWS) TOFILE(QTEMP/ROWS) FROMMBR(ROWS) TOMBR(OTHER) MBROPT(*ADD)"));
        using (Enter(job)) Assert.Equal(1, files.RowCount("QTEMP", "ROWS", "OTHER"));
    }
    [Fact]
    public void Logical_member_commands_accept_logical_QTEMP_names_for_stored_physical_bindings()
    {
        var job = Job(); var files = Files(job);
        using (Enter(job))
        {
            files.CreatePhysicalFile("QTEMP", "ROWS", Definition(), ""); Logical(files);
            files.AddMember("QTEMP", "ROWS", "OTHER"); Add(files, "QTEMP", 9, member: "OTHER");
        }
        using var session = new ExecutionSession(_system, job, CancellationToken.None);
        Ok(session.Execute("ADDLFM FILE(QTEMP/VIEW) MBR(EXTRA) DTAMBRS((QTEMP/ROWS (OTHER)))"));
        using (Enter(job)) Assert.Equal(9L, Assert.Single(files.ReadAll("QTEMP", "VIEW", "EXTRA"))["ID"]);
        Ok(session.Execute("RMVM FILE(QTEMP/VIEW) MBR(EXTRA)")); Ok(session.Execute("RMVM FILE(QTEMP/ROWS) MBR(OTHER)"));
    }
    [Fact]
    public void RPG_shared_open_paths_keep_private_positions_and_job_end_removes_only_owned_storage()
    {
        var declaration = new string(' ', 80).ToCharArray(); declaration[6] = 'D'; "ID".CopyTo(0, declaration, 7, 2);
        declaration[21] = 'S'; declaration[38] = '9'; declaration[40] = '0';
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "READER"), ObjectType = ObjectType.Program, Attribute = "RPG",
            Source = new string(declaration) + "\n**free\nread INPUT;\ndsply ID;\nreturn;" });
        using var first = new ExecutionSession(_system, Job("QUSER"), CancellationToken.None);
        using var second = new ExecutionSession(_system, Job("QUSER"), CancellationToken.None);
        var files = Files(first.Job); var backing = Backing(first.Job);
        foreach (var (session, offset) in new[] { (first, 0), (second, 10) })
        {
            using (Enter(session.Job))
            {
                files.CreatePhysicalFile("QTEMP", "ROWS", Definition(), ""); Add(files, "QTEMP", offset + 1); Add(files, "QTEMP", offset + 2);
            }
            Ok(session.Execute("OVRDBF FILE(INPUT) TOFILE(QTEMP/ROWS) SHARE(*YES) OVRSCOPE(*JOB)"));
        }
        string Read(ExecutionSession session) { var result = session.Execute("CALL QGPL/READER"); Ok(result); return result.Message!.Trim(); }
        Assert.Equal("1", Read(first)); Assert.Equal("11", Read(second)); Assert.Equal("2", Read(first));
        first.End(JobCompletion.Normal, "done"); Assert.False(_system.Objects.Exists("QSYS", backing, ObjectType.Library));
        Assert.Equal("12", Read(second));
    }
    public void Dispose() => _system.Dispose();
}
