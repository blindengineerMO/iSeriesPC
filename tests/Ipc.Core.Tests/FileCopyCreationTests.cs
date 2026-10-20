using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed class FileCopyCreationTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    public FileCopyCreationTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        Run("CREATE TABLE QGPL/DATA(ID INTEGER PRIMARY KEY,V INTEGER DEFAULT 7,CONSTRAINT POS CHECK(V>0))");
        Run("INSERT INTO QGPL/DATA VALUES(1,10),(2,20)");
    }
    private void Run(string sql) => _files.ExecuteSql(sql, Options);
    private object? Scalar(string sql)
    { using var c = _system.Connections.Open(); using var q = c.CreateCommand(); q.CommandText = sql; return q.ExecuteScalar(); }
    private void Ok(string command)
    { var result = new CommandService(_system).Execute(command); Assert.False(result.IsError, result.Message); }

    [Fact]
    public void New_file_uses_source_file_name_for_FIRST_and_ignores_member_option()
    {
        _files.CreatePhysicalFile("QGPL", "SOURCE", new() { Name = "SOURCE", Attribute = FileAttribute.Physical,
            Formats = _files.GetDefinition("QGPL", "DATA")!.Formats }, "");
        _files.AddMember("QGPL", "SOURCE", "OTHER");
        _files.Insert("QGPL", "SOURCE", "OTHER", "DATA", new Dictionary<string, object?> { ["ID"] = 3, ["V"] = 30 });
        Ok("CPYF FROMFILE(QGPL/SOURCE) TOFILE(QGPL/COPY) FROMMBR(OTHER) MBROPT(*NONE) CRTFILE(*YES)");
        Assert.Equal(new[] { "SOURCE" }, _files.ListMembers("QGPL", "COPY"));
        Assert.Equal(3L, Assert.Single(_files.ReadAll("QGPL", "COPY", "SOURCE"))["ID"]);
    }

    [Fact]
    public void SQL_source_creates_a_native_physical_file_without_constraints_defaults_or_triggers()
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(10),(20)");
        Run("ALTER TABLE QGPL/DATA ADD CONSTRAINT REF FOREIGN KEY(V) REFERENCES QGPL/PARENT");
        Scalar("CREATE TABLE copy_audit(id INTEGER); CREATE TRIGGER copy_source AFTER INSERT ON \"QGPL.DATA.DATA\" BEGIN INSERT INTO copy_audit VALUES(new.ID); END");
        Ok("CPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) CRTFILE(*YES)");
        var definition = _files.GetDefinition("QGPL", "COPY")!;
        Assert.Equal(FileAttribute.Physical, definition.Attribute); Assert.Null(definition.SqlTable);
        Assert.All(definition.PrimaryFormat.Fields, f => Assert.Null(f.DefaultValue));
        Assert.Equal(2, _files.RowCount("QGPL", "COPY", "DATA"));
        _files.Insert("QGPL", "COPY", "DATA", "DATA", new Dictionary<string, object?> { ["ID"] = 1, ["V"] = -1 });
        Assert.Equal(0L, Scalar("SELECT count(*) FROM copy_audit"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND name LIKE 'QGPL.COPY.%'"));
    }

    [Fact]
    public void Logical_selection_and_key_order_create_an_independent_physical_file()
    {
        var source = string.Join('\n', LogicalFileTests.Line('R', "LOGREC", "PFILE(DATA)"), LogicalFileTests.Line('K', "V", "DESCEND"), LogicalFileTests.Line('S', "ID", "COMP(GE 1)"));
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile("VIEW", source);
        _files.CreateLogicalFile("QGPL", "VIEW", definition, source);
        Ok("CPYF FROMFILE(QGPL/VIEW) TOFILE(QGPL/COPY) CRTFILE(*YES)");
        var copy = _files.GetDefinition("QGPL", "COPY")!;
        Assert.Null(copy.Logical); Assert.Equal(FileAttribute.Physical, copy.Attribute); Assert.Equal("LOGREC", copy.PrimaryFormat.Name);
        Assert.Equal(new long[] { 20, 10 }, _files.ReadAll("QGPL", "COPY", "VIEW").Select(r => (long)r["V"]!));
        Assert.True(copy.PrimaryFormat.Find("V")!.Descending);
    }

    [Fact]
    public void Source_files_keep_the_source_layout_and_resolved_member_name()
    {
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 200); _files.SaveSourceMember("QGPL", "SOURCE", "CODE", "PGM\nENDPGM", null);
        Ok("CPYF FROMFILE(QGPL/SOURCE) TOFILE(QGPL/COPY) FROMMBR(CODE) TOMBR(*FROMMBR) CRTFILE(*YES)");
        Assert.Equal(new[] { "CODE" }, _files.ListMembers("QGPL", "COPY"));
        Assert.Equal(FileAttribute.Source, _files.GetDefinition("QGPL", "COPY")!.Attribute);
        Assert.Equal(200, _files.GetDefinition("QGPL", "COPY")!.PrimaryFormat.Find("SRCDTA")!.Length);
        Assert.Equal(_files.RowCount("QGPL", "SOURCE", "CODE"), _files.RowCount("QGPL", "COPY", "CODE"));
    }

    [Fact]
    public void CRTFILE_YES_on_an_existing_target_keeps_its_definition_and_requires_a_member_option()
    {
        Run("CREATE TABLE QGPL/COPY(ID INTEGER PRIMARY KEY,V INTEGER)");
        Assert.True(new CommandService(_system).Execute("CPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) CRTFILE(*YES)").IsError);
        Ok("CPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) CRTFILE(*YES) MBROPT(*ADD)");
        Assert.NotNull(_files.GetDefinition("QGPL", "COPY")!.SqlTable);
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/COPY VALUES(1,30)")).MessageId);
    }
    private void User()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "CopyCreationPassword847");
        _system.Security.Authority.SetPublicAuthority("QGPL", "DATA", ObjectType.File, AuthorityBit.None);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
    }
    private void NoCopy()
    {
        Assert.False(_files.FileExists("QGPL", "COPY"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_file_defs WHERE name='COPY'"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_file_members WHERE name='COPY'"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_authorities WHERE name='COPY'"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_record_slots WHERE name='COPY'"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name LIKE 'QGPL.COPY.%'"));
    }

    [Fact]
    public void Direct_creation_and_positional_commands_share_the_native_defaults()
    {
        Assert.Equal(2, _files.CopyRecords(new("QGPL", "DATA"), "*FIRST", new("QGPL", "DIRECT"), "*FIRST", replace: null, createFile: true));
        Ok("CPYF QGPL/DATA QGPL/COPY *FIRST NAMED *UPDADD *YES");
        Assert.Equal(new[] { "NAMED" }, _files.ListMembers("QGPL", "COPY"));
        Assert.Equal(2, _files.RowCount("QGPL", "COPY", "NAMED"));
        var metadata = Ipc.Cl.Compatibility.BuiltinContract.Metadata("CPYF")!;
        Assert.Equal(6,metadata.MaximumPositional);
        Assert.Equal("*NO",metadata.Parameters.Single(p => p.Keyword == "CRTFILE").DefaultValue);
        Assert.Equal("*NONE",metadata.Parameters.Single(p => p.Keyword == "MBROPT").DefaultValue);
    }

    [Fact]
    public void A_read_only_source_caller_becomes_owner_and_keeps_public_private_and_list_authorities()
    {
        User();
        _system.Security.Authority.GrantAuthL("QGPL", "DATA", ObjectType.File, "ACCESS", Authorities.UseBits);
        var source = _system.Objects.GetRequired("QGPL", "DATA", ObjectType.File); source.UseAuthorizationListPublicAuthority = true; _system.Objects.Update(source);
        using (OperationIdentity.Enter("QUSER")) Ok("CPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) CRTFILE(*YES)");
        var copy = _system.Objects.GetRequired("QGPL", "COPY", ObjectType.File);
        Assert.Equal("QUSER",copy.Owner); Assert.Equal(AuthorityBit.None,copy.PublicAuthority); Assert.True(copy.UseAuthorizationListPublicAuthority);
        Assert.Equal(2L,Scalar("SELECT count(*) FROM sys_authorities WHERE name='COPY'"));
        Assert.Equal((long)Authorities.AllBits,Scalar("SELECT bits FROM sys_authorities WHERE name='COPY' AND holder='QUSER'"));
        Assert.Equal(1L,Scalar("SELECT is_authl FROM sys_authorities WHERE name='COPY' AND holder='ACCESS'"));
    }

    [Theory]
    [InlineData("CRTPF")]
    [InlineData("SOURCE")]
    [InlineData("LIBRARY")]
    public void Missing_creation_or_read_authority_leaves_no_object(string denied)
    {
        User();
        if (denied == "SOURCE") _system.Security.Authority.Grant("QGPL","DATA",ObjectType.File,"QUSER",AuthorityBit.ObjectOperate);
        else if (denied == "CRTPF") _system.Security.Authority.Grant("QSYS","CRTPF",ObjectType.Command,"QUSER",AuthorityBit.None);
        else _system.Security.Authority.Grant("QSYS","QGPL",ObjectType.Library,"QUSER",AuthorityBit.ObjectOperate);
        using (OperationIdentity.Enter("QUSER"))
        {
            var result = new CommandService(_system).Execute("CPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) CRTFILE(*YES)");
            Assert.True(result.IsError); Assert.Contains("CPF9802",result.Message);
        }
        NoCopy();
    }

    [Theory]
    [InlineData("DATA")]
    [InlineData("CRTPF")]
    [InlineData("QGPL")]
    public void Creation_rechecks_live_permissions_after_catalog_hooks_and_rolls_back_revocation(string name)
    {
        User(); _system.Security.Authority.Grant("QSYS","CRTPF",ObjectType.Command,"QUSER",AuthorityBit.ObjectOperate);
        Scalar("CREATE TRIGGER deny_copy AFTER INSERT ON sys_file_defs WHEN new.name='COPY' BEGIN UPDATE sys_authorities SET bits=0 WHERE name='" + name + "' AND holder='QUSER'; END");
        using (OperationIdentity.Enter("QUSER"))
        {
            var result = new CommandService(_system).Execute("CPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) CRTFILE(*YES)");
            Assert.True(result.IsError); Assert.Contains("CPF9802",result.Message);
        }
        NoCopy(); Assert.NotEqual(0L,Scalar("SELECT bits FROM sys_authorities WHERE name='" + name + "' AND holder='QUSER'"));
    }

    [Theory]
    [InlineData("UPDATE sys_file_defs SET def=json_set(def,'$.formats[0].fields[1].nullCapable',json('false')) WHERE name='COPY'")]
    [InlineData("UPDATE sys_objects SET owner='QSYS' WHERE name='COPY' AND type='*FILE'")]
    [InlineData("UPDATE sys_objects SET public_authority=0 WHERE name='DATA' AND type='*FILE'")]
    public void Late_definition_owner_or_authority_image_changes_roll_back_the_whole_copy(string change)
    {
        Scalar("CREATE TRIGGER change_copy AFTER INSERT ON sys_file_defs WHEN new.name='COPY' BEGIN " + change + "; END");
        Assert.Throws<CpfException>(() => _files.CopyRecords(new("QGPL","DATA"),"DATA",new("QGPL","COPY"),"DATA",createFile:true));
        NoCopy(); Assert.Equal(2,_files.RowCount("QGPL","DATA","DATA"));
    }

    [Fact]
    public void Created_file_capacity_extends_without_operator_inquiries_and_survives_later_writes()
    {
        Scalar("UPDATE sys_file_defs SET def=json_set(def,'$.memberSize',json('{\"initialRecords\":1,\"incrementRecords\":0,\"maximumIncrements\":0,\"unlimited\":false}')) WHERE name='DATA'");
        Ok("CPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) CRTFILE(*YES)");
        Assert.Equal(1,_files.GetDefinition("QGPL","COPY")!.MemberSize!.InitialRecords);
        Assert.Equal(2L,Scalar("SELECT capacity FROM sys_member_capacity c JOIN sys_record_slots s ON s.id=c.member_id WHERE s.name='COPY'"));
        Assert.Equal(2,_files.RowCount("QGPL","COPY","DATA"));
        Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_message_entries WHERE message_id='CPA5305'"));
    }

    [Theory]
    [InlineData("CRTFILE(*SAME)")]
    [InlineData("CRTFILE(YES)")]
    [InlineData("CRTFILE(*YES) FMTOPT(*NOCHK)")]
    [InlineData("CRTFILE(*YES) MBROPT(*BAD)")]
    public void Invalid_choices_do_not_create_a_target(string choices)
    {
        Assert.True(new CommandService(_system).Execute("CPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) " + choices).IsError); NoCopy();
    }

    [Fact]
    public void Unqualified_missing_target_is_rejected_but_current_library_creation_is_supported()
    {
        Assert.True(new CommandService(_system).Execute("CPYF FROMFILE(QGPL/DATA) TOFILE(COPY) CRTFILE(*YES)").IsError); NoCopy();
        Ok("CPYF FROMFILE(QGPL/DATA) TOFILE(*CURLIB/COPY) CRTFILE(*YES)");
        Assert.Equal(2,_files.RowCount("QGPL","COPY","DATA"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writer_wait_cancellation_leaves_no_partial_file_and_allows_retry(bool command)
    {
        using var connection = _system.Connections.Open(); using var held = connection.BeginTransaction(deferred:false);
        using var stop = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.Factory.StartNew(() => {
            entered.SetResult();
            if (command) new CommandService(_system,cancellationToken:stop.Token).Execute("CPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) CRTFILE(*YES)");
            else _files.CopyRecords(new("QGPL","DATA"),"DATA",new("QGPL","COPY"),"DATA",cancellationToken:stop.Token,createFile:true);
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(100); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            stop.Cancel(); held.Rollback();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
        NoCopy(); Ok("CPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) CRTFILE(*YES)");
    }

    [Fact]
    public void Late_record_hook_failure_rolls_back_file_rows_capacity_and_authority()
    {
        Scalar("CREATE TRIGGER stop_load AFTER INSERT ON sys_record_versions WHEN new.rrn=2 AND EXISTS(SELECT 1 FROM sys_record_slots WHERE id=new.member_id AND name='COPY') BEGIN SELECT RAISE(ABORT,'late copy failure'); END");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _files.CopyRecords(new("QGPL","DATA"),"DATA",new("QGPL","COPY"),"DATA",createFile:true));
        NoCopy(); Assert.Equal(2,_files.RowCount("QGPL","DATA","DATA"));
    }

    [Fact]
    public void New_physical_files_compact_sparse_source_RRNs_and_get_fresh_record_identities()
    {
        Scalar("UPDATE \"QGPL.DATA.DATA\" SET rowid=20 WHERE ID=2; DELETE FROM \"QGPL.DATA.DATA\" WHERE ID=1");
        Ok("CPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) CRTFILE(*YES)");
        Assert.Equal(1L,Scalar("SELECT rowid FROM \"QGPL.COPY.DATA\""));
        Assert.Equal(1L,Scalar("SELECT high_water FROM sys_record_slots WHERE name='COPY'"));
        Assert.NotEqual(Scalar("SELECT id FROM sys_record_slots WHERE name='DATA'"),Scalar("SELECT id FROM sys_record_slots WHERE name='COPY'"));
        Assert.NotEqual(Scalar("SELECT version FROM sys_record_versions WHERE member_id=(SELECT id FROM sys_record_slots WHERE name='DATA')"),
            Scalar("SELECT version FROM sys_record_versions WHERE member_id=(SELECT id FROM sys_record_slots WHERE name='COPY')"));
    }

    [Fact]
    public void Native_unique_access_paths_survive_while_SQL_constraints_and_defaults_do_not()
    {
        var format = _files.GetDefinition("QGPL","DATA")!.PrimaryFormat; format.Find("ID")!.Sequence = 1;
        _files.CreatePhysicalFile("QGPL","UNIQUEPF",new() { Name="UNIQUEPF",Attribute=FileAttribute.Physical,Unique=true,Formats=[format] },"");
        _files.Insert("QGPL","UNIQUEPF","UNIQUEPF","DATA",new Dictionary<string,object?> { ["ID"]=1,["V"]=10 });
        Ok("CPYF FROMFILE(QGPL/UNIQUEPF) TOFILE(QGPL/COPY) CRTFILE(*YES)");
        Assert.True(_files.GetDefinition("QGPL","COPY")!.Unique);
        Assert.Equal("SQL0803",Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/COPY VALUES(1,20)")).MessageId);
    }

    [Fact]
    public void Copying_a_parent_without_its_SQL_key_leaves_defined_dependents_unchanged()
    {
        Run("CREATE TABLE QGPL/CHILD(ID INTEGER REFERENCES QGPL/DATA)");
        _system.Libraries.CreateLibrary("DEST");
        _system.ObjectOperations.Relocate(new("QGPL","CHILD"),ObjectType.File,new("DEST","CHILD"),copy:true);
        Ok("CPYF FROMFILE(QGPL/DATA) TOFILE(DEST/DATA) CRTFILE(*YES)");
        Assert.True(Assert.Single(_files.GetDefinition("DEST","CHILD")!.SqlTable!.ForeignKeys!).Defined);
        Run("INSERT INTO DEST/CHILD VALUES(99)");
        Run("INSERT INTO DEST/DATA(ID) VALUES(3)");
        Assert.IsType<DBNull>(Scalar("SELECT V FROM \"DEST.DATA.DATA\" WHERE ID=3"));
    }

    [Fact]
    public void Native_replace_requires_management_and_rechecks_source_READ_after_row_hooks()
    {
        User(); Run("CREATE TABLE QGPL/COPY(ID INTEGER,V INTEGER)"); Run("INSERT INTO QGPL/COPY VALUES(9,90)");
        _system.Security.Authority.SetPublicAuthority("QGPL","COPY",ObjectType.File,AuthorityBit.None);
        _system.Security.Authority.Grant("QGPL","COPY",ObjectType.File,"QUSER",AuthorityBit.ObjectOperate | AuthorityBit.Add | AuthorityBit.Delete);
        using (OperationIdentity.Enter("QUSER"))
            Assert.Equal("CPF9802",Assert.Throws<CpfException>(() => _files.CopyRecords(new("QGPL","DATA"),"DATA",new("QGPL","COPY"),"COPY",replace:true)).MessageId);
        _system.Security.Authority.Grant("QGPL","COPY",ObjectType.File,"QUSER",AuthorityBit.ObjectOperate | AuthorityBit.Add | AuthorityBit.Delete | AuthorityBit.ObjectManagement);
        Scalar("CREATE TRIGGER deny_source AFTER INSERT ON \"QGPL.COPY.COPY\" BEGIN UPDATE sys_authorities SET bits=1 WHERE name='DATA' AND holder='QUSER'; END");
        using (OperationIdentity.Enter("QUSER"))
            Assert.Equal("CPF9802",Assert.Throws<CpfException>(() => _files.CopyRecords(new("QGPL","DATA"),"DATA",new("QGPL","COPY"),"COPY",replace:true)).MessageId);
        Assert.Equal(9L,Scalar("SELECT ID FROM \"QGPL.COPY.COPY\""));
        Assert.Equal((long)Authorities.UseBits,Scalar("SELECT bits FROM sys_authorities WHERE name='DATA' AND holder='QUSER'"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_job_canceled_or_ended_during_row_transfer_cannot_publish_a_created_file(bool cancel)
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"),"CopyLivePublisherPassword54");
        var job = _system.Jobs.CreateInteractive("QSECOFR");
        var change = cancel ? "cancel_requested=1" : "execution_state='Succeeded'";
        Scalar("CREATE TRIGGER end_copy AFTER INSERT ON sys_record_versions WHEN new.rrn=2 AND EXISTS(SELECT 1 FROM sys_record_slots WHERE id=new.member_id AND name='COPY') BEGIN UPDATE sys_jobs SET " + change + " WHERE number=" + job.Key.Number + "; END");
        using (OperationIdentity.Enter("QSECOFR",job.Key))
        {
            if (cancel) Assert.Throws<OperationCanceledException>(() => _files.CopyRecords(new("QGPL","DATA"),"DATA",new("QGPL","COPY"),"DATA",createFile:true));
            else Assert.Equal("CPF1241",Assert.Throws<CpfException>(() => _files.CopyRecords(new("QGPL","DATA"),"DATA",new("QGPL","COPY"),"DATA",createFile:true)).MessageId);
        }
        NoCopy();
    }

    [Fact]
    public void Compiled_CL_uses_the_shared_creation_handler_and_native_defaults()
    {
        _files.CreateSourceFile("QGPL","SOURCE"); _files.SaveSourceMember("QGPL","SOURCE","COPYCL", "PGM\nCPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) CRTFILE(*YES)\nENDPGM",null);
        Ok("CRTCLPGM PGM(QGPL/COPYCL) SRCFILE(QGPL/SOURCE)"); Ok("CALL QGPL/COPYCL");
        Assert.Equal(2,_files.RowCount("QGPL","COPY","DATA")); Assert.Null(_files.GetDefinition("QGPL","COPY")!.SqlTable);
    }

    [Theory]
    [InlineData("*NONE")]
    [InlineData("*REPLACE")]
    public void Empty_sources_still_create_the_requested_file_and_member(string option)
    {
        Run("DELETE FROM QGPL/DATA");
        Ok("CPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) TOMBR(EMPTY) MBROPT(" + option + ") CRTFILE(*YES)");
        Assert.Equal(new[] { "EMPTY" },_files.ListMembers("QGPL","COPY"));
        Assert.Equal(0,_files.RowCount("QGPL","COPY","EMPTY"));
        Assert.Equal(0L,Scalar("SELECT high_water FROM sys_record_slots WHERE name='COPY'"));
    }

    [Fact]
    public void New_file_omits_source_journal_bindings_and_does_not_replay_source_triggers()
    {
        _system.Journals.CreateReceiver(new("QGPL","RCV")); _system.Journals.Create(new("QGPL","JRN"),new("QGPL","RCV"));
        _system.Journals.StartFile(new("QGPL","DATA"),new("QGPL","JRN"),"*BOTH");
        Ok("CPYF FROMFILE(QGPL/DATA) TOFILE(QGPL/COPY) CRTFILE(*YES)");
        Run("INSERT INTO QGPL/COPY VALUES(1,-1)");
        Assert.Empty(_system.Journals.Read(new("QGPL","JRN")));
        Run("INSERT INTO QGPL/DATA VALUES(3,30)");
        Assert.Single(_system.Journals.Read(new("QGPL","JRN")));
    }

    [Fact]
    public void Created_native_layout_records_and_capacity_reopen_after_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(),"ipc-copy-create-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,"data.db");
        try
        {
            using (var system = IpcSystem.Create(path))
            {
                system.Start(); var files = new SqliteFileStore(system.Connections,system.Objects);
                files.ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER PRIMARY KEY)",Options);
                files.ExecuteSql("INSERT INTO QGPL/DATA VALUES(1),(2)",Options);
                using (var c = system.Connections.Open()) using (var q = c.CreateCommand())
                {
                    q.CommandText = "UPDATE sys_file_defs SET def=json_set(def,'$.memberSize',json('{\"initialRecords\":1,\"incrementRecords\":0,\"maximumIncrements\":0,\"unlimited\":false}')) WHERE name='DATA'";
                    q.ExecuteNonQuery();
                }
                Assert.Equal(2,files.CopyRecords(new("QGPL","DATA"),"*FIRST",new("QGPL","COPY"),"*FIRST",createFile:true));
            }
            using (var system = IpcSystem.Create(path))
            {
                system.Start(); var files = new SqliteFileStore(system.Connections,system.Objects);
                Assert.Equal(2,files.RowCount("QGPL","COPY","DATA")); Assert.Null(files.GetDefinition("QGPL","COPY")!.SqlTable);
                Assert.Equal(1,files.GetDefinition("QGPL","COPY")!.MemberSize!.InitialRecords);
                Assert.Equal("CPF5030",Assert.Throws<CpfException>(() => files.ExecuteSql("INSERT INTO QGPL/COPY VALUES(1)",Options)).MessageId);
                Assert.Equal(2,files.RowCount("QGPL","COPY","DATA"));
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory,true); }
    }

    [Fact]
    public void A_live_job_can_create_and_read_its_private_QTEMP_file_without_schema_lock_reentry()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"),"CopyQTempPublisherPassword73");
        var job = _system.Jobs.CreateInteractive("QSECOFR");
        using (OperationIdentity.Enter("QSECOFR",job.Key))
        {
            Ok("CPYF FROMFILE(QGPL/DATA) TOFILE(QTEMP/COPY) CRTFILE(*YES)");
            Assert.Equal(2,_files.RowCount("QTEMP","COPY","DATA"));
            Assert.Null(_files.GetDefinition("QTEMP","COPY")!.SqlTable);
        }
        Assert.False(_files.FileExists("QGPL","COPY"));
    }

    public void Dispose() => _system.Dispose();
}
