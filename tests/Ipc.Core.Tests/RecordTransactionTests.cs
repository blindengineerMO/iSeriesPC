using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Db.Dds;
using Ipc.Rpg.Runtime;
using Ipc.Services.Work;
using Ipc.Console.Session;
using Ipc.Session;
using Ipc.Services;
using Ipc.Services.Events;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class RecordTransactionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-record-tx-" + Guid.NewGuid().ToString("N"));
    private readonly IpcSystem _system;
    private readonly SqliteFileStore _files;
    private readonly Job _job;
    private static readonly QualifiedName First = new("QGPL", "FIRST");
    private static readonly QualifiedName Second = new("QGPL", "SECOND");
    private static readonly QualifiedName Journal = new("QGPL", "TXJRN");
    public RecordTransactionTests()
    {
        _system = IpcSystem.Create(_directory); _system.Start(); _files = new(_system.Connections, _system.Objects);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "RecordTxAdministrator31");
        _system.Journals.CreateReceiver(new("QGPL", "TXRCV0001")); _system.Journals.Create(Journal, new("QGPL", "TXRCV0001"));
        foreach (var name in new[] { "FIRST", "SECOND" })
        {
            var format = new RecordFormat { Name = "REC", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8, Sequence = 1 }, new() { Name = "TEXT", Type = FieldType.Alpha, Length = 100 }] }; format.AssignPositions();
            _files.CreatePhysicalFile("QGPL", name, new() { Name = name, Attribute = FileAttribute.Physical, Formats = [format] }, "");
            _system.Journals.StartFile(new("QGPL", name), Journal, "*AFTER");
            _files.Insert("QGPL", name, name, "REC", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "original" });
        }
        _job = _system.Jobs.CreateInteractive("QSECOFR");
    }
    private object? Sql(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private void Begin()
    { _system.RecordTransactions.Begin(_job.Key); _system.RecordTransactions.Enlist(_job.Key, First); _system.RecordTransactions.Enlist(_job.Key, Second); }
    private void Change(string table, string text)
    { using var statement = _system.RecordTransactions.Open(_job.Key); statement.Execute("UPDATE \"QGPL." + table + "." + table + "\" SET TEXT=$text WHERE ID=1", new Dictionary<string, object?> { ["$text"] = text }); statement.Complete(); }
    private string WorkspacePath() => Path.Combine(_directory, ".record-units", _system.RecordTransactions.Inspect(_job.Key).Definition, "records.db");
    private static object? WorkspaceValue(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }
    [Fact]
    public void Workspace_copies_record_data_without_copying_catalog_security_logs_or_unrelated_payloads()
    {
        Sql("CREATE TABLE irrelevant_payload(body BLOB); INSERT INTO irrelevant_payload VALUES(zeroblob(16777216))");
        Assert.True((long)Sql("SELECT count(*) FROM sys_profiles")! > 0); Assert.True((long)Sql("SELECT count(*) FROM sys_events")! > 0);
        Begin(); var path = WorkspacePath();
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            Assert.Equal("original", Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""))[0]);
            foreach (var table in new[] { "sys_profiles", "sys_jobs", "sys_events", "sys_journal_entries", "sys_objects", "irrelevant_payload" })
                Assert.Equal(0L, WorkspaceValue(path, "SELECT count(*) FROM " + table));
        }
        var bytes = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "records.db*").Sum(file => new FileInfo(file).Length);
        Assert.True(bytes < 4 * 1024 * 1024, "Unrelated 16 MiB payload must not expand the record workspace: " + bytes);
        Assert.Equal(16777216L, Sql("SELECT length(body) FROM irrelevant_payload"));
        Change("FIRST", "pending"); _system.RecordTransactions.Commit(_job.Key); Assert.Equal("pending", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public void Workspace_preserves_sparse_rowids_and_deleted_AUTOINCREMENT_high_water_marks()
    {
        _system.Journals.EndFile(First);
        Sql("DROP TABLE \"QGPL.FIRST.FIRST\"; CREATE TABLE \"QGPL.FIRST.FIRST\"(ID INTEGER PRIMARY KEY AUTOINCREMENT,TEXT TEXT); INSERT INTO \"QGPL.FIRST.FIRST\" VALUES(41,'kept'),(99,'deleted'); DELETE FROM \"QGPL.FIRST.FIRST\" WHERE ID=99");
        _system.Journals.StartFile(First, Journal, "*BOTH"); Begin();
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            Assert.Equal(41L, Assert.Single(statement.Query("SELECT rowid FROM \"QGPL.FIRST.FIRST\""))[0]);
            statement.Execute("INSERT INTO \"QGPL.FIRST.FIRST\"(TEXT) VALUES('next')"); statement.Complete();
        }
        using (var statement = _system.RecordTransactions.Open(_job.Key))
            Assert.Equal(new object?[] { 41L, 100L }, statement.Query("SELECT ID FROM \"QGPL.FIRST.FIRST\" ORDER BY rowid").Select(row => row[0]).ToArray());
        _system.RecordTransactions.Commit(_job.Key); Assert.Equal(100L, Sql("SELECT max(ID) FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public void Workspace_preserves_generated_values_and_enforces_the_physical_unique_index()
    {
        _system.Journals.EndFile(First);
        Sql("DROP TABLE \"QGPL.FIRST.FIRST\"; CREATE TABLE \"QGPL.FIRST.FIRST\"(ID INTEGER,TEXT TEXT,CODE TEXT GENERATED ALWAYS AS(upper(TEXT)) STORED); CREATE UNIQUE INDEX generated_key ON \"QGPL.FIRST.FIRST\"(CODE); INSERT INTO \"QGPL.FIRST.FIRST\"(ID,TEXT) VALUES(1,'original')");
        _system.Journals.StartFile(First, Journal, "*BOTH"); Begin(); Change("FIRST", "changed");
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            Assert.Equal("CHANGED", Assert.Single(statement.Query("SELECT CODE FROM \"QGPL.FIRST.FIRST\""))[0]);
            Assert.Throws<SqliteException>(() => statement.Execute("INSERT INTO \"QGPL.FIRST.FIRST\"(ID,TEXT) VALUES(2,'changed')"));
        }
        Assert.Equal(1, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("CHANGED", Sql("SELECT CODE FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public void Empty_unjournaled_schemas_still_prevent_silent_foreign_key_cascade_loss()
    {
        Sql("CREATE UNIQUE INDEX unmanaged_parent ON \"QGPL.FIRST.FIRST\"(ID); CREATE TABLE unmanaged_child(ID INTEGER REFERENCES \"QGPL.FIRST.FIRST\"(ID) ON DELETE CASCADE); INSERT INTO unmanaged_child VALUES(1)");
        Begin();
        using (var statement = _system.RecordTransactions.Open(_job.Key)) Assert.Single(statement.Query("SELECT ID FROM \"QGPL.FIRST.FIRST\""));
        using (var statement = _system.RecordTransactions.Open(_job.Key)) Assert.Throws<SqliteException>(() => statement.Execute("DELETE FROM \"QGPL.FIRST.FIRST\""));
        Assert.Equal(0, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); Assert.Equal(1L, Sql("SELECT count(*) FROM unmanaged_child")); Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public void Workspace_retains_SQLite_integer_and_blob_storage_without_affinity_conversion()
    {
        Sql("UPDATE \"QGPL.FIRST.FIRST\" SET ID=9223372036854775807,TEXT=X'0000FF4020'"); Begin();
        using var statement = _system.RecordTransactions.Open(_job.Key);
        var row = Assert.Single(statement.Query("SELECT typeof(ID),ID,typeof(TEXT),hex(TEXT) FROM \"QGPL.FIRST.FIRST\""));
        Assert.Equal(new object?[] { "integer", long.MaxValue, "blob", "0000FF4020" }, row);
    }
    [Fact]
    public void SQLIte_library_names_do_not_get_confused_with_internal_sqlite_tables()
    {
        _system.Libraries.CreateLibrary("SQLITE"); _files.CreatePhysicalFile("SQLITE", "ROWS", _files.GetDefinition("QGPL", "FIRST")!, "");
        _system.Journals.StartFile(new("SQLITE", "ROWS"), Journal);
        _files.Insert("SQLITE", "ROWS", "ROWS", "REC", new Dictionary<string, object?> { ["ID"] = 7L, ["TEXT"] = "library" });
        _system.RecordTransactions.Begin(_job.Key); _system.RecordTransactions.Enlist(_job.Key, new("SQLITE", "ROWS"));
        for (var index = 0; index < 2; index++)
            using (var statement = _system.RecordTransactions.Open(_job.Key)) Assert.Equal("library", Assert.Single(statement.Query("SELECT TEXT FROM \"SQLITE.ROWS.ROWS\""))[0]);
    }
    [Fact]
    public async Task Record_workspace_reads_one_committed_snapshot_across_multiple_files()
    {
        Begin(); using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () => {
            using var connection = _system.Connections.Open(); connection.CreateFunction("ipc_journal_replay", () => 1);
            for (var index = 0; !stop.IsCancellationRequested; index++)
            {
                using var transaction = connection.BeginTransaction(deferred: false); using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE \"QGPL.FIRST.FIRST\" SET TEXT=$value; UPDATE \"QGPL.SECOND.SECOND\" SET TEXT=$value"; command.Parameters.AddWithValue("$value", index.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
                command.ExecuteNonQuery(); transaction.Commit(); await Task.Delay(1);
            }
        });
        try
        {
            for (var index = 0; index < 20; index++)
            {
                using var statement = _system.RecordTransactions.Open(_job.Key);
                var first = Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""))[0];
                Assert.Equal(first, Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""))[0]);
            }
        }
        finally { stop.Cancel(); await writer.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_in_memory_catalog_uses_the_same_record_only_workspace_and_publish_contract(bool authenticated)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
        var definition = _files.GetDefinition("QGPL", "FIRST")!; files.CreatePhysicalFile("QGPL", "MEMORY", definition, "");
        system.Journals.CreateReceiver(new("QGPL", "MEMRCV")); system.Journals.Create(new("QGPL", "MEMJRN"), new("QGPL", "MEMRCV")); system.Journals.StartFile(new("QGPL", "MEMORY"), new("QGPL", "MEMJRN"));
        files.Insert("QGPL", "MEMORY", "MEMORY", "REC", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "before" });
        system.Security.Authority.Grant("QGPL", "MEMORY", ObjectType.File, "QUSER", Authorities.ChangeBits);
        var job = system.Jobs.CreateInteractive("QUSER");
        using var identity = authenticated ? OperationIdentity.Enter("QUSER", job.Key) : null;
        system.RecordTransactions.Begin(job.Key); system.RecordTransactions.Enlist(job.Key, new("QGPL", "MEMORY"));
        using (var statement = system.RecordTransactions.Open(job.Key)) { statement.Execute("UPDATE \"QGPL.MEMORY.MEMORY\" SET TEXT='after'"); statement.Complete(); }
        system.RecordTransactions.Commit(job.Key); Assert.Equal("after", Assert.Single(files.ReadAll("QGPL", "MEMORY", "MEMORY"))["TEXT"]);
    }
    [Theory]
    [InlineData("profile")]
    [InlineData("session")]
    [InlineData("mapping")]
    [InlineData("cancel")]
    [InlineData("job")]
    public void Final_publish_validation_uses_the_writer_and_rolls_back_invalid_credentials_or_job_state(string invalidation)
    {
        var job = _system.Jobs.CreateInteractive("QUSER");
        _system.Security.Authority.Grant("QGPL", "FIRST", ObjectType.File, "QUSER", Authorities.ChangeBits);
        var credential = new Ipc.Services.Security.SessionTokenStore(_system.Connections, TimeProvider.System)
            .Issue(_system.Security.Profiles.Get("QUSER"), null);
        using (OperationIdentity.Enter("QUSER", job.Key, credential.Id))
        {
            _system.RecordTransactions.Begin(job.Key); _system.RecordTransactions.Enlist(job.Key, First);
            using var statement = _system.RecordTransactions.Open(job.Key);
            statement.Execute("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='pending'"); statement.Complete();
        }
        var injection = invalidation switch {
            "profile" => "UPDATE sys_profiles SET password_expires='2000-01-01T00:00:00+00:00' WHERE name='QUSER'",
            "session" => "UPDATE sys_auth_sessions SET expires=0 WHERE profile='QUSER'",
            "mapping" => "INSERT INTO sys_eim_mappings(profile,directory,distinguished_name,entry_id,principal,linux_account,revision,enabled,directory_state,checked_at) VALUES('QUSER','directory','dn','entry','user@example.com','account','revision',1,'Active','2000-01-01T00:00:00+00:00')",
            "cancel" => "UPDATE sys_jobs SET cancel_requested=1 WHERE number=" + job.Key.Number,
            _ => "UPDATE sys_jobs SET execution_state='Succeeded' WHERE number=" + job.Key.Number
        };
        Sql("CREATE TRIGGER invalidate_commit AFTER INSERT ON sys_journal_entries WHEN NEW.code='C' AND NEW.entry_type='CM' BEGIN " + injection + "; END");
        using (OperationIdentity.Enter("QUSER", job.Key, credential.Id))
        {
            if (invalidation == "cancel") Assert.Throws<OperationCanceledException>(() => _system.RecordTransactions.Commit(job.Key));
            else Assert.Throws<CpfException>(() => _system.RecordTransactions.Commit(job.Key));
            Assert.Equal(1, _system.RecordTransactions.Inspect(job.Key).PendingChanges);
        }
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal(2, _system.Journals.Read(Journal).Count);
        Sql("DROP TRIGGER invalidate_commit");
        using (OperationIdentity.Enter("QUSER", job.Key, credential.Id)) _system.RecordTransactions.Commit(job.Key);
        Assert.Equal("pending", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public void Shared_job_unit_reads_its_writes_and_publishes_multiple_files_and_journals_atomically()
    {
        Begin(); Change("FIRST", "first pending"); Change("SECOND", "second pending");
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal(2, _system.Journals.Read(Journal).Count);
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        { Assert.Equal("first pending", Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""))[0]); statement.Complete(); }
        Assert.Equal(2, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("first pending", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal("second pending", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
        var entries = _system.Journals.Read(Journal, 3); Assert.Equal(new[] { "SC", "UB", "UP", "UB", "UP", "CM" }, entries.Select(entry => entry.Type));
        Assert.All(entries, entry => Assert.NotNull(entry.CommitCycle)); Assert.Single(entries.Select(entry => entry.CommitCycle).Distinct());
        Assert.Equal(0, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
    }
    [Fact]
    public void Rollback_and_uncompleted_statement_restore_the_unit_without_touching_live_records()
    {
        Begin(); Change("FIRST", "kept pending");
        using (var statement = _system.RecordTransactions.Open(_job.Key)) statement.Execute("UPDATE \"QGPL.SECOND.SECOND\" SET TEXT='discarded'");
        Assert.Equal(1, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(0, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        _system.RecordTransactions.Commit(_job.Key); Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal(2, _system.Journals.Read(Journal).Count);
        _system.RecordTransactions.End(_job.Key);
    }
    [Fact]
    public void Failed_publish_rolls_back_live_changes_and_keeps_the_pending_unit_retryable()
    {
        Begin(); Change("FIRST", "pending"); Change("SECOND", "pending");
        Sql("CREATE TRIGGER fail_tx_commit BEFORE INSERT ON sys_journal_entries WHEN NEW.code='C' AND NEW.entry_type='CM' BEGIN SELECT RAISE(ABORT,'injected'); END");
        Assert.Throws<SqliteException>(() => _system.RecordTransactions.Commit(_job.Key));
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal(2, _system.Journals.Read(Journal).Count); Assert.Equal(2, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        Sql("DROP TRIGGER fail_tx_commit"); _system.RecordTransactions.Commit(_job.Key); Assert.Equal("pending", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
    }
    [Fact]
    public void Catalog_messages_and_job_logging_remain_independent_while_data_is_pending()
    {
        Begin(); Change("FIRST", "pending");
        _system.Jobs.WriteLog(_job.Key, "INFO", "IPCS0001", 0, "independent");
        _system.Journals.Send(Journal, "AA", [0, 255]);
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal("AA", _system.Journals.Read(Journal)[^1].Type);
    }
    [Theory]
    [InlineData("UPDATE sys_profiles SET status='Disabled'")]
    [InlineData("SELECT * FROM sys_profiles")]
    [InlineData("DELETE FROM sys_journal_entries")]
    [InlineData("COMMIT")]
    [InlineData("DROP TABLE \"QGPL.FIRST.FIRST\"")]
    [InlineData("SELECT ipc_record_change('QGPL','FIRST','FIRST',1,NULL,NULL)")]
    public void Record_SQL_cannot_modify_catalog_or_bypass_the_unit_boundary(string sql)
    {
        Begin(); using (var statement = _system.RecordTransactions.Open(_job.Key)) Assert.Throws<SqliteException>(() => statement.Execute(sql));
        Assert.Equal(0, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
    }
    [Fact]
    public void Other_jobs_cannot_use_the_unit_and_an_ordinary_reopen_shares_its_owner_in_process()
    {
        Begin(); Change("FIRST", "pending"); var other = _system.Jobs.CreateInteractive("QSECOFR");
        using (OperationIdentity.Enter("QSECOFR", other.Key)) Assert.Throws<CpfException>(() => _system.RecordTransactions.Commit(_job.Key));
        using var reopened = IpcSystem.Create(_directory); reopened.Start();
        Assert.Equal(1, reopened.RecordTransactions.Inspect(_job.Key).PendingChanges); reopened.RecordTransactions.Commit(_job.Key);
        Assert.Equal("pending", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public void Job_completion_discards_pending_data_and_removes_the_definition()
    {
        Begin(); Change("FIRST", "pending"); _system.Jobs.Complete(_job.Key);
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal(2, _system.Journals.Read(Journal).Count);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_directory, ".record-units")));
    }
    [Fact]
    public void File_store_RPG_adapter_SQL_and_cursor_share_the_explicitly_enlisted_unit()
    {
        Begin();
        using (OperationIdentity.Enter("QSECOFR", _job.Key))
        using (new JobLockStore(_system.Connections).EnterCommand(_job.Key))
        {
            var rpg = new RpgSqliteFileAccess(_files);
            rpg.Update("QGPL", "FIRST", "FIRST", "REC", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "RPG pending" });
            _files.Insert("QGPL", "FIRST", "FIRST", "REC", new Dictionary<string, object?> { ["ID"] = 2L, ["TEXT"] = "insert pending" });
            Assert.Equal(2, rpg.RowCount("QGPL", "FIRST", "FIRST"));
            Assert.Equal("RPG pending", Assert.Single(rpg.ReadKeyPrefix("QGPL", "FIRST", "FIRST", new Dictionary<string, object?> { ["ID"] = 1L }))["TEXT"]);
            Change("SECOND", "SQL pending");
            using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
            Assert.Equal("RPG pending", cursor.Read()!["TEXT"]); Assert.Equal(2L, cursor.Read()!["ID"]); Assert.Null(cursor.Read());
            rpg.Delete("QGPL", "FIRST", "FIRST", "REC", new Dictionary<string, object?> { ["ID"] = 2L });
            Assert.Single(rpg.ReadAll("QGPL", "FIRST", "FIRST"));
        }
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
        _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("RPG pending", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal("SQL pending", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_RPG_writes_and_CL_receives_pending_records_in_the_same_job(int ccsid)
    {
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "TXRPG", "**free\ndcl-s TEXT char(100);\nchain 1 FIRST;\neval TEXT = 'compiled pending';\nupdate FIRST;\nreturn;", null);
        _files.SaveSourceMember("QGPL", "SOURCE", "TXCL", "PGM\nDCLF FILE(QGPL/FIRST)\nRCVF\nSNDPGMMSG MSG(&TEXT)\nCLOSE\nENDPGM", null);
        var compiler = new CommandService(_system);
        foreach (var command in new[] { "CRTBNDRPG PGM(QGPL/TXRPG) SRCFILE(QGPL/SOURCE)", "CRTCLPGM PGM(QGPL/TXCL) SRCFILE(QGPL/SOURCE)" })
        { var result = compiler.Execute(command); Assert.False(result.IsError, result.Message); }
        var job = _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid);
        using var session = new ExecutionSession(_system, job, CancellationToken.None);
        _system.RecordTransactions.Begin(job.Key); _system.RecordTransactions.Enlist(job.Key, First); _system.RecordTransactions.Enlist(job.Key, Second);
        var changed = session.Execute("CALL QGPL/TXRPG"); Assert.False(changed.IsError, changed.Message);
        var read = session.Execute("CALL QGPL/TXCL"); Assert.False(read.IsError, read.Message); Assert.Equal("compiled pending", read.Message);
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal(2, _system.Journals.Read(Journal).Count);
        using (OperationIdentity.Enter("QSECOFR", job.Key))
        {
            using (var statement = _system.RecordTransactions.Open(job.Key))
            { statement.Execute("UPDATE \"QGPL.SECOND.SECOND\" SET TEXT='SQL pending'"); statement.Complete(); }
            _system.RecordTransactions.Commit(job.Key);
        }
        Assert.Equal("compiled pending", ((string)Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")!).TrimEnd());
        Assert.Equal("SQL pending", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
        Assert.All(_system.Journals.Read(Journal, 3), entry => Assert.Equal(job.Key.ToString(), entry.Job));
    }
    [Fact]
    public void Logical_projection_reads_and_updates_the_same_pending_physical_record()
    {
        var source = LogicalFileTests.Line('R', "VIEWREC", "PFILE(QGPL/FIRST)") + "\n" + LogicalFileTests.Line('K', "ID");
        var definition = new LogicalDdsCompiler(_ => (First, _files.GetDefinition("QGPL", "FIRST")!)).Compile("VIEW", source);
        _files.CreateLogicalFile("QGPL", "VIEW", definition, source);
        _files.CreatePhysicalFile("QGPL", "COPYTO", _files.GetDefinition("QGPL", "FIRST")!, "");
        Begin();
        using (OperationIdentity.Enter("QSECOFR", _job.Key))
        {
            _files.Update("QGPL", "VIEW", "VIEW", "VIEWREC", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "logical pending" });
            Assert.Equal("logical pending", Assert.Single(_files.ReadKeyed("QGPL", "VIEW", "VIEW"))["TEXT"]);
            Assert.Equal("logical pending", Assert.Single(_files.ReadAll("QGPL", "FIRST", "FIRST"))["TEXT"]);
            Assert.Throws<CpfException>(() => _files.CopyRecords(new("QGPL", "VIEW"), "VIEW", new("QGPL", "COPYTO"), "COPYTO"));
        }
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
        Assert.Equal(0L, Sql("SELECT count(*) FROM \"QGPL.COPYTO.COPYTO\""));
    }
    [Fact]
    public void Enlisted_file_writes_preserve_large_decimal_text_nullable_values_and_float_bits()
    {
        var format = new RecordFormat { Name = "REC", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8, Sequence = 1 },
            new() { Name = "AMOUNT", Type = FieldType.Packed, Length = 29 }, new() { Name = "VALUE", Type = FieldType.Float, Length = 8 },
            new() { Name = "OPTIONAL", Type = FieldType.Alpha, Length = 10, NullCapable = true }] }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "EXACT", new() { Name = "EXACT", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        _system.Journals.StartFile(new("QGPL", "EXACT"), Journal);
        _system.RecordTransactions.Begin(_job.Key); _system.RecordTransactions.Enlist(_job.Key, new("QGPL", "EXACT"));
        var number = BitConverter.Int64BitsToDouble(0x3FF0000000000001);
        using (OperationIdentity.Enter("QSECOFR", _job.Key))
        {
            _files.Insert("QGPL", "EXACT", "EXACT", "REC", new Dictionary<string, object?> { ["ID"] = long.MaxValue, ["AMOUNT"] = "12345678901234567890123456789", ["VALUE"] = number, ["OPTIONAL"] = null });
            Assert.Single(_files.ReadKeyed("QGPL", "EXACT", "EXACT"));
        }
        Assert.Equal(0L, Sql("SELECT count(*) FROM \"QGPL.EXACT.EXACT\"")); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal(long.MaxValue, Sql("SELECT ID FROM \"QGPL.EXACT.EXACT\""));
        Assert.Equal("12345678901234567890123456789.0", Sql("SELECT AMOUNT FROM \"QGPL.EXACT.EXACT\""));
        Assert.Equal("text", Sql("SELECT typeof(AMOUNT) FROM \"QGPL.EXACT.EXACT\""));
        Assert.Equal(BitConverter.DoubleToInt64Bits(number), BitConverter.DoubleToInt64Bits((double)Sql("SELECT VALUE FROM \"QGPL.EXACT.EXACT\"")!));
        Assert.Equal(DBNull.Value, Sql("SELECT OPTIONAL FROM \"QGPL.EXACT.EXACT\""));
    }
    [Fact]
    public void Dependent_trigger_changes_are_staged_and_published_once()
    {
        Sql("CREATE TRIGGER dependent_update AFTER UPDATE ON \"QGPL.FIRST.FIRST\" BEGIN UPDATE \"QGPL.SECOND.SECOND\" SET TEXT=NEW.TEXT; END");
        Begin(); Change("FIRST", "trigger pending"); Assert.Equal(2, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        using (var statement = _system.RecordTransactions.Open(_job.Key))
            Assert.Equal("trigger pending", Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""))[0]);
        _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("trigger pending", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
        Assert.Equal(2, _system.Journals.Read(Journal, 3).Count(entry => entry.Type == "UP"));
        Sql("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='after commit'"); Assert.Equal("after commit", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Foreign_key_cascades_join_the_unit_and_unjournaled_dependents_are_rejected(bool journalChild)
    {
        _system.Journals.EndFile(Second);
        Sql("CREATE UNIQUE INDEX tx_parent_id ON \"QGPL.FIRST.FIRST\"(ID); DROP TABLE \"QGPL.SECOND.SECOND\"; CREATE TABLE \"QGPL.SECOND.SECOND\"(ID INTEGER,TEXT TEXT,FOREIGN KEY(ID) REFERENCES \"QGPL.FIRST.FIRST\"(ID) ON DELETE CASCADE); INSERT INTO \"QGPL.SECOND.SECOND\" VALUES(1,'child')");
        if (journalChild) _system.Journals.StartFile(Second, Journal);
        _system.RecordTransactions.Begin(_job.Key); _system.RecordTransactions.Enlist(_job.Key, First);
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            if (journalChild) { statement.Execute("DELETE FROM \"QGPL.FIRST.FIRST\""); statement.Complete(); }
            else Assert.Throws<SqliteException>(() => statement.Execute("DELETE FROM \"QGPL.FIRST.FIRST\""));
        }
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
        Assert.Equal(journalChild ? 2 : 0, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal(journalChild ? 0L : 1L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
        Assert.Equal(journalChild ? 0L : 1L, Sql("SELECT count(*) FROM \"QGPL.SECOND.SECOND\""));
        Assert.Equal(journalChild ? 2 : 0, _system.Journals.Read(Journal).Count(entry => entry.Type == "DL"));
    }
    [Fact]
    public void Outside_changes_conflict_without_overwriting_the_live_record_or_losing_pending_work()
    {
        Begin(); Change("FIRST", "pending"); Sql("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='outside'");
        Assert.Throws<CpfException>(() => _system.RecordTransactions.Commit(_job.Key));
        Assert.Equal("outside", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal(1, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        Assert.DoesNotContain(_system.Journals.Read(Journal), entry => entry.Code == "C");
        _system.RecordTransactions.Rollback(_job.Key);
    }
    [Fact]
    public void Changed_record_locks_survive_statements_and_release_at_rollback()
    {
        Begin(); Change("FIRST", "pending"); var other = _system.Jobs.CreateInteractive("QSECOFR"); var locks = new JobLockStore(_system.Connections);
        var resource = new JobLockResource("QGPL", "FIRST", ObjectType.File, "FIRST", 1);
        Assert.Contains(locks.Inspect(resource), holder => holder.Job == _job.Key && holder.Lifetime == "Commitment");
        Assert.Throws<CpfException>(() => locks.Acquire(other.Key, resource, JobLockMode.Exclusive, TimeSpan.Zero));
        _system.RecordTransactions.Rollback(_job.Key);
        using var lease = locks.Acquire(other.Key, resource, JobLockMode.Exclusive, TimeSpan.Zero);
    }
    [Fact]
    public void A_failed_multirow_statement_cannot_complete_or_retain_its_rolled_back_rows_in_the_intent_log()
    {
        _files.Insert("QGPL", "FIRST", "FIRST", "REC", new Dictionary<string, object?> { ["ID"] = 2L, ["TEXT"] = "second original" });
        var other = _system.Jobs.CreateInteractive("QSECOFR"); var locks = new JobLockStore(_system.Connections);
        using var blocked = locks.Acquire(other.Key, new("QGPL", "FIRST", ObjectType.File, "FIRST", 2), JobLockMode.Exclusive, TimeSpan.Zero);
        Begin();
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            Assert.Throws<SqliteException>(() => statement.Execute("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='must roll back'"));
            Assert.Throws<CpfException>(statement.Complete);
            Assert.Throws<CpfException>(() => statement.Execute("UPDATE \"QGPL.SECOND.SECOND\" SET TEXT='also forbidden'"));
        }
        Assert.Equal(0, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\" WHERE ID=1")); Assert.Equal("second original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\" WHERE ID=2"));
        Assert.DoesNotContain(_system.Journals.Read(Journal), entry => entry.Code == "C");
    }
    [Fact]
    public void Cancelled_publish_leaves_pending_work_and_live_journals_unchanged()
    {
        Begin(); Change("FIRST", "pending"); using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => _system.RecordTransactions.Commit(_job.Key, stop.Token));
        Assert.Equal(1, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); Assert.Equal(2, _system.Journals.Read(Journal).Count);
        _system.RecordTransactions.Commit(_job.Key); Assert.Equal("pending", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public void Recovery_does_not_use_forced_commitment_images_to_remove_an_AFTER_only_file()
    {
        Begin(); Change("FIRST", "committed"); _system.RecordTransactions.Commit(_job.Key);
        var before = _system.Journals.Read(Journal).Single(entry => entry.Type == "UB");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, [new(First)], before.Sequence, before.Sequence, remove: true));
        Assert.Equal("committed", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal(6, _system.Journals.Inspect(Journal).Sequence);
        Sql("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='ordinary'"); Assert.Null(_system.Journals.Read(Journal)[^1].CommitCycle);
    }
    [Fact]
    public void File_utilities_cannot_silently_publish_changes_outside_an_enlisted_unit()
    {
        Begin();
        using (OperationIdentity.Enter("QSECOFR", _job.Key))
        {
            Assert.Throws<CpfException>(() => _files.ClearMember("QGPL", "FIRST", "FIRST"));
            Assert.Throws<CpfException>(() => _files.CopyRecords(First, "FIRST", Second, "SECOND"));
        }
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public async Task A_killed_owner_leaves_live_records_unchanged_and_job_recovery_removes_its_workspace()
    {
        var start = new global::System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(WorkFixture.Program).Assembly.Location, "record-stage", _directory, "1", _job.Key.Number.ToString(), _job.Key.Name, _job.Key.User }) start.ArgumentList.Add(argument);
        using var process = global::System.Diagnostics.Process.Start(start)!;
        try
        {
            await JobLockTests.WaitFor(() => File.Exists(Path.Combine(_directory, "ready-" + process.Id)) || process.HasExited);
            File.WriteAllText(Path.Combine(_directory, "start"), "start");
            await JobLockTests.WaitFor(() => File.Exists(Path.Combine(_directory, "staged-" + process.Id)) || process.HasExited);
            Assert.False(process.HasExited, process.HasExited ? await process.StandardError.ReadToEndAsync() : "");
            Assert.Throws<CpfException>(() => _system.RecordTransactions.Open(_job.Key));
            process.Kill(true); await process.WaitForExitAsync();
            using var reopened = IpcSystem.Create(_directory); reopened.Start(); reopened.Jobs.RecoverInterruptedInteractiveJobs();
            Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal(2, reopened.Journals.Read(Journal).Count);
            Assert.Empty(Directory.GetDirectories(Path.Combine(_directory, ".record-units")));
            Assert.Empty(new JobLockStore(reopened.Connections).Inspect(new("QGPL", "FIRST", ObjectType.File)));
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    [Fact]
    public void Live_read_and_publish_authority_are_rechecked_after_enlistment()
    {
        _system.Security.Authority.Grant("QGPL", "FIRST", ObjectType.File, "QUSER", Authorities.ChangeBits);
        var job = _system.Jobs.CreateInteractive("QUSER");
        using (OperationIdentity.Enter("QUSER", job.Key))
        {
            _system.RecordTransactions.Begin(job.Key); _system.RecordTransactions.Enlist(job.Key, First);
            using var statement = _system.RecordTransactions.Open(job.Key); statement.Execute("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='pending'"); statement.Complete();
        }
        _system.Security.Authority.Grant("QGPL", "FIRST", ObjectType.File, "QUSER", AuthorityBit.ObjectOperate);
        using (OperationIdentity.Enter("QUSER", job.Key))
        {
            using (var statement = _system.RecordTransactions.Open(job.Key)) Assert.Throws<SqliteException>(() => statement.Query("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
            Assert.Throws<CpfException>(() => _system.RecordTransactions.Commit(job.Key));
            Assert.Equal(1, _system.RecordTransactions.Inspect(job.Key).PendingChanges); _system.RecordTransactions.Rollback(job.Key);
        }
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal(2, _system.Journals.Read(Journal).Count);
    }
    [Fact]
    public void Independent_jobs_commit_different_records_without_holding_a_live_writer_between_statements()
    {
        _files.Insert("QGPL", "FIRST", "FIRST", "REC", new Dictionary<string, object?> { ["ID"] = 2L, ["TEXT"] = "original second" });
        Begin(); Change("FIRST", "first job"); var other = _system.Jobs.CreateInteractive("QSECOFR");
        using (OperationIdentity.Enter("QSECOFR", other.Key))
        {
            _system.RecordTransactions.Begin(other.Key); _system.RecordTransactions.Enlist(other.Key, First);
            using (var statement = _system.RecordTransactions.Open(other.Key))
            { statement.Execute("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='second job' WHERE ID=2"); statement.Complete(); }
            _system.RecordTransactions.Commit(other.Key);
        }
        _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("first job", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\" WHERE ID=1")); Assert.Equal("second job", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\" WHERE ID=2"));
        Assert.Equal(2, _system.Journals.Read(Journal).Where(entry => entry.CommitCycle is not null).Select(entry => entry.CommitCycle).Distinct().Count());
    }
    [Fact]
    public void Reopened_definition_end_then_restart_does_not_lose_factory_cleanup_ownership()
    {
        Begin();
        using (var reopened = IpcSystem.Create(_directory)) { reopened.Start(); reopened.RecordTransactions.End(_job.Key); }
        Begin(); Change("FIRST", "must discard"); _system.Dispose();
        using var next = IpcSystem.Create(_directory); next.Start();
        Assert.Empty(Directory.GetDirectories(Path.Combine(_directory, ".record-units")));
        Assert.Equal("original", new SqliteFileStore(next.Connections, next.Objects).ReadAll("QGPL", "FIRST", "FIRST")[0]["TEXT"]);
    }
    [Fact]
    public void Ending_a_definition_inside_a_statement_is_rejected_without_disposing_its_connection()
    {
        Begin(); using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            Assert.Throws<CpfException>(() => _system.RecordTransactions.End(_job.Key));
            statement.Execute("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='still usable'"); statement.Complete();
        }
        Assert.Equal(1, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); _system.RecordTransactions.Rollback(_job.Key);
    }
    [Fact]
    public async Task Waiting_for_a_live_writer_can_be_cancelled_without_losing_the_pending_unit()
    {
        Begin(); Change("FIRST", "pending");
        using (var blocker = _system.Connections.Open())
        using (var transaction = blocker.BeginTransaction(deferred: false))
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAsync<OperationCanceledException>(() => Task.Run(() => _system.RecordTransactions.Commit(_job.Key, stop.Token)));
        }
        Assert.Equal(1, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); Assert.Equal(2, _system.Journals.Read(Journal).Count);
        _system.RecordTransactions.Commit(_job.Key);
    }
    [Fact]
    public async Task Waiting_for_a_statement_gate_can_be_cancelled()
    {
        Begin(); using var opened = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
        var holder = Task.Run(() => { using var statement = _system.RecordTransactions.Open(_job.Key); opened.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); });
        try
        {
            Assert.True(opened.Wait(TimeSpan.FromSeconds(10)));
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            Assert.Throws<OperationCanceledException>(() => _system.RecordTransactions.Commit(_job.Key, stop.Token));
        }
        finally { finish.Set(); await holder; }
    }
    [Fact]
    public async Task Cancelled_SQL_rolls_back_its_scope_and_does_not_poison_later_enlistment()
    {
        _system.RecordTransactions.Begin(_job.Key); _system.RecordTransactions.Enlist(_job.Key, First);
        using var ready = new ManualResetEventSlim(); using var stop = new CancellationTokenSource();
        var query = Task.Run(() => {
            using var statement = _system.RecordTransactions.Open(_job.Key, stop.Token);
            statement.Execute("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='must roll back'"); ready.Set();
            statement.Query("WITH RECURSIVE n(x) AS(VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000000000) SELECT sum(x) FROM n");
            statement.Complete();
        });
        try { Assert.True(ready.Wait(TimeSpan.FromSeconds(10))); stop.CancelAfter(TimeSpan.FromMilliseconds(100)); await Assert.ThrowsAsync<OperationCanceledException>(() => query); }
        finally { stop.Cancel(); }
        _system.RecordTransactions.Enlist(_job.Key, Second);
        Assert.Equal(0, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
        Change("SECOND", "after cancellation"); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("after cancellation", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
    }
    [Fact]
    public async Task Killing_publish_during_WAL_spill_recovers_all_records_and_journal_cycles_atomically()
    {
        Sql("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT=printf('%08000d',1); WITH RECURSIVE n(x) AS(VALUES(2) UNION ALL SELECT x+1 FROM n WHERE x<1000) INSERT INTO \"QGPL.FIRST.FIRST\" SELECT x,printf('%08000d',x) FROM n");
        var before = _system.Journals.Inspect(Journal).Sequence; Sql("PRAGMA wal_checkpoint(TRUNCATE)");
        var start = new global::System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(WorkFixture.Program).Assembly.Location, "record-publish", _directory, "1", _job.Key.Number.ToString(), _job.Key.Name, _job.Key.User }) start.ArgumentList.Add(argument);
        using var process = global::System.Diagnostics.Process.Start(start)!;
        try
        {
            await JobLockTests.WaitFor(() => File.Exists(Path.Combine(_directory, "ready-" + process.Id)) || process.HasExited);
            File.WriteAllText(Path.Combine(_directory, "start"), "start");
            using (var staging = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                while (!File.Exists(Path.Combine(_directory, "staged-" + process.Id)) && !process.HasExited) await Task.Delay(20, staging.Token);
            Assert.False(process.HasExited, process.HasExited ? await process.StandardError.ReadToEndAsync() : "");
            File.WriteAllText(Path.Combine(_directory, "publish"), "publish"); var wal = _system.Connections.DatabasePath + "-wal";
            await JobLockTests.WaitFor(() => process.HasExited || File.Exists(wal) && new FileInfo(wal).Length > 32);
            Assert.False(process.HasExited, process.HasExited ? await process.StandardError.ReadToEndAsync() : "");
            process.Kill(true); await process.WaitForExitAsync();
            using var reopened = IpcSystem.Create(_directory); reopened.Start(); reopened.Jobs.RecoverInterruptedInteractiveJobs();
            Assert.Equal(before, reopened.Journals.Inspect(Journal).Sequence);
            Assert.Equal(1000L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\" WHERE length(TEXT)=8000"));
            Assert.Equal(0L, Sql("SELECT count(*) FROM sys_journal_entries WHERE commit_cycle IS NOT NULL"));
            Assert.Empty(Directory.GetDirectories(Path.Combine(_directory, ".record-units")));
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    public void Dispose() { _system.Dispose(); try { Directory.Delete(_directory, true); } catch (IOException) { } }
}
