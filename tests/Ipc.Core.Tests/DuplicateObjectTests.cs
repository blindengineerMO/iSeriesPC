using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class DuplicateObjectTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    public DuplicateObjectTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        Run("CREATE TABLE QGPL/DATA(ID INTEGER PRIMARY KEY,V INTEGER DEFAULT 7,CONSTRAINT POS CHECK(V>0))");
        Run("INSERT INTO QGPL/DATA VALUES(1,10),(2,20)");
    }
    private void Run(string sql) => _files.ExecuteSql(sql, Options);
    private object? Scalar(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private void Ok(string command)
    { var result = new CommandService(_system).Execute(command); Assert.False(result.IsError, result.Message); }

    [Fact]
    public void Native_default_duplicates_members_and_constraints_without_copying_records()
    {
        Ok("CRTDUPOBJ OBJ(DATA) FROMLIB(QGPL) OBJTYPE(*FILE) TOLIB(QGPL) NEWOBJ(COPY)");
        Assert.Equal(new[] { "DATA" }, _files.ListMembers("QGPL", "COPY")); Assert.Equal(0, _files.RowCount("QGPL", "COPY", "DATA"));
        var definition = _files.GetDefinition("QGPL", "COPY")!;
        Assert.Single(definition.SqlTable!.Constraints); Assert.Single(definition.SqlTable.Checks!);
        Assert.Equal(0L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='COPY'"));
        Run("INSERT INTO QGPL/COPY(ID) VALUES(1)");
        Assert.Equal(7L, Scalar("SELECT V FROM \"QGPL.COPY.DATA\""));
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/COPY VALUES(1,10)")).MessageId);
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/COPY VALUES(2,-1)")).MessageId);
        Assert.Equal(2, _files.RowCount("QGPL", "DATA", "DATA"));
    }

    [Fact]
    public void DATA_YES_and_CST_NO_copy_records_without_primary_unique_CHECK_or_foreign_enforcement()
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(10),(20)");
        Run("ALTER TABLE QGPL/DATA ADD CONSTRAINT REF FOREIGN KEY(V) REFERENCES QGPL/PARENT");
        Ok("CRTDUPOBJ OBJ(DATA) FROMLIB(QGPL) OBJTYPE(*FILE) TOLIB(QGPL) NEWOBJ(COPY) DATA(*YES) CST(*NO)");
        var definition = _files.GetDefinition("QGPL", "COPY")!;
        Assert.Empty(definition.SqlTable!.Constraints); Assert.Null(definition.SqlTable.Checks); Assert.Null(definition.SqlTable.ForeignKeys);
        Assert.Equal(2, _files.RowCount("QGPL", "COPY", "DATA"));
        Run("INSERT INTO QGPL/COPY VALUES(1,-1)");
        Assert.Equal(3, _files.RowCount("QGPL", "COPY", "DATA"));
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(1,10)")).MessageId);
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(3,-1)")).MessageId);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(3,99)")).MessageId);
    }

    [Fact]
    public void Positional_arguments_and_same_library_defaults_follow_the_native_command_contract()
    {
        Ok("CRTDUPOBJ DATA QGPL *FILE NEWOBJ(COPY) CST(*NO)");
        Assert.Equal(0, _files.RowCount("QGPL", "COPY", "DATA"));
        Assert.Empty(_files.GetDefinition("QGPL", "COPY")!.SqlTable!.Constraints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_and_direct_copies_require_source_management_in_addition_to_use(bool command)
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "DuplicateAuthorityPassword329");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        var source = _system.Objects.GetRequired("QGPL", "DATA", ObjectType.File); source.PublicAuthority = AuthorityBit.None; _system.Objects.Update(source);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Read);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using (OperationIdentity.Enter("QUSER", session.Job.Key))
        {
            if (command)
            { var result = session.Execute("CRTDUPOBJ OBJ(DATA) FROMLIB(QGPL) OBJTYPE(*FILE) NEWOBJ(COPY)"); Assert.True(result.IsError); Assert.Contains("CPF9802", result.Message); }
            else Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(new("QGPL", "DATA"), ObjectType.File, new("QGPL", "COPY"), copy: true)).MessageId);
        }
        Assert.False(_files.FileExists("QGPL", "COPY"));
    }

    [Fact]
    public void Source_file_default_keeps_all_members_but_not_their_source_records()
    {
        _files.CreateSourceFile("QGPL", "SOURCE"); _files.SaveSourceMember("QGPL", "SOURCE", "EXTRA", "PGM\nENDPGM", null);
        Ok("CRTDUPOBJ OBJ(SOURCE) FROMLIB(QGPL) OBJTYPE(*FILE) TOLIB(QGPL) NEWOBJ(COPY)");
        Assert.Equal(new[] { "EXTRA", "SOURCE" }, _files.ListMembers("QGPL", "COPY"));
        Assert.Equal(0, _files.RowCount("QGPL", "COPY", "EXTRA")); Assert.Equal(0, _files.RowCount("QGPL", "COPY", "SOURCE"));
        Assert.True(_files.RowCount("QGPL", "SOURCE", "EXTRA") > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DATA_controls_sparse_RRNs_deleted_extents_and_new_record_incarnations(bool data)
    {
        Scalar("UPDATE \"QGPL.DATA.DATA\" SET rowid=20 WHERE ID=2; DELETE FROM \"QGPL.DATA.DATA\" WHERE ID=2");
        var source = Scalar("SELECT id FROM sys_record_slots WHERE name='DATA'");
        Ok("CRTDUPOBJ DATA QGPL *FILE QGPL COPY DATA(" + (data ? "*YES" : "*NO") + ")");
        Assert.NotEqual(source, Scalar("SELECT id FROM sys_record_slots WHERE name='COPY'"));
        Assert.Equal(data ? 20L : 0L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='COPY'"));
        Assert.Equal(data ? 1 : 0, _files.RowCount("QGPL", "COPY", "DATA"));
        if (data)
        {
            Assert.Equal(1L, Scalar("SELECT rowid FROM \"QGPL.COPY.DATA\""));
            Assert.NotEqual(Scalar("SELECT version FROM sys_record_versions WHERE member_id=(SELECT id FROM sys_record_slots WHERE name='DATA') AND rrn=1"),
                Scalar("SELECT version FROM sys_record_versions WHERE member_id=(SELECT id FROM sys_record_slots WHERE name='COPY') AND rrn=1"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Copies_preserve_pending_CHECK_state_and_restore_enforcement_after_old_row_copy(bool data)
    {
        Assert.Null(_files.ChangeConstraints("QGPL", "DATA", ["POS"], false)[0].Error);
        Run("INSERT INTO QGPL/DATA VALUES(3,-1)");
        Assert.True(_files.ChangeConstraints("QGPL", "DATA", ["POS"], true)[0].CheckPending);
        Ok("CRTDUPOBJ DATA QGPL *FILE QGPL COPY DATA(" + (data ? "*YES" : "*NO") + ")");
        var check = Assert.Single(_files.GetDefinition("QGPL", "COPY")!.SqlTable!.Checks!);
        Assert.True(check.CheckPending); Assert.False(check.Disabled);
        Assert.Equal(data ? 3L : 0L, Scalar("SELECT count(*) FROM \"QGPL.COPY.DATA\""));
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "COPY", "DATA")).MessageId);
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/COPY VALUES(4,-2)")).MessageId);
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(4,-2)")).MessageId);
    }

    [Fact]
    public void Empty_child_copy_checks_its_empty_data_instead_of_the_sources_unmatched_rows()
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(10),(20)");
        Run("ALTER TABLE QGPL/DATA ADD CONSTRAINT REF FOREIGN KEY(V) REFERENCES QGPL/PARENT");
        Assert.Null(_files.ChangeConstraints("QGPL", "DATA", ["REF"], false)[0].Error);
        Run("INSERT INTO QGPL/DATA VALUES(3,99)"); Assert.True(_files.ChangeConstraints("QGPL", "DATA", ["REF"], true)[0].CheckPending);
        _system.Libraries.CreateLibrary("DEST");
        Run("CREATE TABLE DEST/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO DEST/PARENT VALUES(10)");
        Ok("CRTDUPOBJ DATA QGPL *FILE DEST COPY");
        var foreign = Assert.Single(_files.GetDefinition("DEST", "COPY")!.SqlTable!.ForeignKeys!);
        Assert.False(foreign.Defined); Assert.False(foreign.CheckPending);
        Run("INSERT INTO DEST/COPY VALUES(1,10)");
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/COPY VALUES(2,99)")).MessageId);
        Assert.True(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.ForeignKeys![0].CheckPending);
    }

    [Fact]
    public void CST_NO_omits_constraint_paths_and_keeps_defaults_and_NOT_NULL_column_attributes()
    {
        Run("ALTER TABLE QGPL/DATA ALTER V SET NOT NULL");
        Run("CREATE UNIQUE INDEX QGPL/VIDX ON QGPL/DATA(V)");
        Ok("CRTDUPOBJ DATA QGPL *FILE QGPL COPY DATA(*YES) CST(*NO)");
        Assert.False(_files.FileExists("QGPL", "VIDXCOPY")); Assert.True(_files.FileExists("QGPL", "VIDX"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE type='index' AND tbl_name='QGPL.COPY.DATA' AND (name LIKE '%ipc_sql_unique_%' OR name LIKE '%ipc_sql_reference_%' OR name LIKE '%ipc_sql_foreign_%')"));
        Run("INSERT INTO QGPL/COPY VALUES(1,10)");
        Assert.Equal("SQL0407", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/COPY VALUES(4,NULL)")).MessageId);
        Run("INSERT INTO QGPL/COPY(ID) VALUES(3)");
        Assert.Equal(7L, Scalar("SELECT V FROM \"QGPL.COPY.DATA\" WHERE ID=3"));
    }

    [Fact]
    public void CST_NO_preserves_the_native_DDS_unique_file_attribute()
    {
        var format = new RecordFormat { Name = "REC", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 4, Sequence = 1 }] }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "KEYED", new() { Name = "KEYED", Attribute = FileAttribute.Physical, Unique = true, Formats = [format] }, "");
        _files.Insert("QGPL", "KEYED", "KEYED", "REC", new Dictionary<string, object?> { ["ID"] = 1 });
        Ok("CRTDUPOBJ KEYED QGPL *FILE QGPL COPY DATA(*YES) CST(*NO)");
        Assert.True(_files.GetDefinition("QGPL", "COPY")!.Unique);
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/COPY VALUES(1)")).MessageId);
        Assert.Equal(1, _files.RowCount("QGPL", "COPY", "KEYED"));
    }

    [Theory]
    [InlineData("DATA(YES)")]
    [InlineData("DATA(*SAME)")]
    [InlineData("CST(1)")]
    [InlineData("CST(*ALL)")]
    public void Invalid_copy_choices_reject_before_creating_members_or_objects(string option)
    {
        var result = new CommandService(_system).Execute("CRTDUPOBJ DATA QGPL *FILE QGPL COPY " + option);
        Assert.True(result.IsError); Assert.Equal("IPC0003", result.MessageId);
        Assert.False(_files.FileExists("QGPL", "COPY"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name GLOB 'QGPL.COPY.*'"));
    }

    [Fact]
    public void Published_prompt_metadata_has_the_native_copy_defaults()
    {
        var definition = new Ipc.Services.Commands.CommandDefinitionStore(_system.Connections).Load("QSYS", "CRTDUPOBJ");
        Assert.Equal(5, definition.MaximumPositional);
        foreach (var (keyword, value) in new[] { ("TOLIB", "*FROMLIB"), ("NEWOBJ", "*OBJ"), ("DATA", "*NO"), ("CST", "*YES") })
            Assert.Equal(value, definition.Parameters.Single(parameter => parameter.Keyword == keyword).Default);
    }

    [Fact]
    public void Copying_a_copy_without_constraints_removes_renamed_checks_and_nested_constraint_indexes()
    {
        Ok("CRTDUPOBJ DATA QGPL *FILE QGPL FIRST DATA(*YES)");
        Ok("CRTDUPOBJ FIRST QGPL *FILE QGPL COPY DATA(*YES) CST(*NO)");
        Assert.Empty(_files.GetDefinition("QGPL", "COPY")!.SqlTable!.Constraints);
        Assert.Null(_files.GetDefinition("QGPL", "COPY")!.SqlTable!.Checks);
        Run("INSERT INTO QGPL/COPY VALUES(1,-1)"); Assert.Equal(3, _files.RowCount("QGPL", "COPY", "DATA"));
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/FIRST VALUES(3,-1)")).MessageId);
    }

    [Fact]
    public void Compiled_CL_uses_DATA_and_CST_choices_from_the_shared_native_command()
    {
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "DUPCL", """
            PGM
            CRTDUPOBJ DATA QGPL *FILE QGPL COPY DATA(*YES) CST(*NO)
            RUNSQL SQL('INSERT INTO QGPL/COPY VALUES(1,-1)') COMMIT(*NONE)
            SNDPGMMSG MSG('DUPLICATE CL ACCEPTED')
            ENDPGM
            """, null);
        Ok("CRTCLPGM PGM(QGPL/DUPCL) SRCFILE(QGPL/SOURCE)");
        var result = new CommandService(_system).Execute("CALL QGPL/DUPCL"); Assert.False(result.IsError, result.Message);
        Assert.Contains("DUPLICATE CL ACCEPTED", result.Message); Assert.Equal(3, _files.RowCount("QGPL", "COPY", "DATA"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_and_direct_copy_writer_waits_cancel_without_publishing_a_partial_object(bool command)
    {
        using var connection = _system.Connections.Open(); using var held = connection.BeginTransaction(deferred: false);
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.Factory.StartNew(() => {
            entered.SetResult();
            if (command) new CommandService(_system, cancellationToken: stop.Token).Execute("CRTDUPOBJ DATA QGPL *FILE QGPL COPY DATA(*YES) CST(*NO)");
            else _system.ObjectOperations.Relocate(new("QGPL", "DATA"), ObjectType.File, new("QGPL", "COPY"), copy: true, cancellationToken: stop.Token);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
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
        Assert.False(_files.FileExists("QGPL", "COPY")); Assert.Equal(2, _files.RowCount("QGPL", "DATA", "DATA"));
        Ok("CRTDUPOBJ DATA QGPL *FILE QGPL COPY DATA(*YES) CST(*NO)");
    }

    [Theory]
    [InlineData(AuthorityBit.ObjectManagement)]
    [InlineData(AuthorityBit.Read)]
    [InlineData(AuthorityBit.ObjectOperate)]
    public void Copy_rechecks_each_source_permission_after_catalog_publication_hooks(AuthorityBit revoked)
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "DuplicateRevokePassword329");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        var source = _system.Objects.GetRequired("QGPL", "DATA", ObjectType.File); source.PublicAuthority = AuthorityBit.None; _system.Objects.Update(source);
        var rights = AuthorityBit.ObjectManagement | AuthorityBit.ObjectOperate | AuthorityBit.Read;
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", rights);
        Scalar("CREATE TRIGGER revoke_copy AFTER INSERT ON sys_file_defs WHEN NEW.name='COPY' BEGIN UPDATE sys_authorities SET bits=bits & " +
            ~(long)revoked + " WHERE lib='QGPL' AND name='DATA' AND holder='QUSER'; END");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        var result = session.Execute("CRTDUPOBJ DATA QGPL *FILE QGPL COPY DATA(*YES) CST(*NO)");
        Assert.True(result.IsError); Assert.Contains("CPF9802", result.Message); Assert.False(_files.FileExists("QGPL", "COPY"));
        Assert.Equal((long)rights, Scalar("SELECT bits FROM sys_authorities WHERE lib='QGPL' AND name='DATA' AND holder='QUSER'"));
    }

    [Fact]
    public void DATA_YES_rejects_non_file_objects_without_creating_a_copy()
    {
        _system.Objects.Create(new() { Key = new("QGPL", "PROGRAM"), ObjectType = ObjectType.Program, Source = "PGM\nENDPGM" });
        var result = new CommandService(_system).Execute("CRTDUPOBJ PROGRAM QGPL *PGM QGPL COPY DATA(*YES)");
        Assert.True(result.IsError); Assert.Equal("CPF2116", result.MessageId); Assert.False(_system.Objects.Exists("QGPL", "COPY", ObjectType.Program));
    }

    [Fact]
    public void Parent_copy_without_constraints_leaves_defined_children_until_a_parent_key_is_added()
    {
        Run("CREATE TABLE QGPL/CHILD(ID INTEGER REFERENCES QGPL/DATA(ID))"); Run("INSERT INTO QGPL/CHILD VALUES(1)");
        _system.Libraries.CreateLibrary("DEST");
        Ok("CRTDUPOBJ CHILD QGPL *FILE DEST DATA(*YES)");
        Ok("CRTDUPOBJ DATA QGPL *FILE DEST DATA(*YES) CST(*NO)");
        Assert.True(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Run("INSERT INTO DEST/CHILD VALUES(99)");
        Run("ALTER TABLE DEST/DATA ADD PRIMARY KEY(ID)");
        var foreign = _files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0];
        Assert.False(foreign.Defined); Assert.True(foreign.CheckPending);
    }

    [Fact]
    public void DATA_YES_preserves_source_text_sequence_and_member_width()
    {
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        var original = _files.SaveSourceMember("QGPL", "SOURCE", "PROGRAM", "PGM\nSNDPGMMSG MSG('SOURCE')\nENDPGM", null);
        Ok("CRTDUPOBJ SOURCE QGPL *FILE QGPL COPY DATA(*YES)");
        var copied = _files.ReadSourceMember("QGPL", "COPY", "PROGRAM");
        Assert.Equal(original.Source, copied.Source); Assert.Equal(original.Width, copied.Width);
        Assert.Equal(_files.ReadAll("QGPL", "SOURCE", "PROGRAM").Select(row => row["SRCSEQ"]),
            _files.ReadAll("QGPL", "COPY", "PROGRAM").Select(row => row["SRCSEQ"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_copies_reset_dynamic_capacity_grants_while_data_copies_preserve_them(bool data)
    {
        _files.ChangePhysicalMemberLimit("QGPL", "DATA", null, memberSize: new(2, 0, 0, false));
        Scalar("INSERT INTO sys_member_capacity(member_id,definition_signature,capacity) SELECT id,'2/0/0/False',8 FROM sys_record_slots WHERE name='DATA'");
        Ok("CRTDUPOBJ DATA QGPL *FILE QGPL COPY DATA(" + (data ? "*YES" : "*NO") + ")");
        Assert.Equal(data ? 1L : 0L, Scalar("SELECT count(*) FROM sys_member_capacity WHERE member_id=(SELECT id FROM sys_record_slots WHERE name='COPY')"));
        if (data) { Run("INSERT INTO QGPL/COPY(ID) VALUES(3)"); Assert.Equal(3, _files.RowCount("QGPL", "COPY", "DATA")); }
        else
        {
            Run("INSERT INTO QGPL/COPY(ID) VALUES(3),(4)");
            Assert.Equal("CPF5030", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/COPY(ID) VALUES(5)")).MessageId);
            Assert.Equal(2, _files.RowCount("QGPL", "COPY", "DATA"));
        }
    }

    [Fact]
    public void Copies_keep_application_triggers_without_replaying_them_or_cloning_journal_bindings()
    {
        _system.Journals.CreateReceiver(new("QGPL", "RCV")); _system.Journals.Create(new("QGPL", "JRN"), new("QGPL", "RCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "JRN"), "*BOTH");
        Scalar("CREATE TABLE seen(n INTEGER); CREATE TRIGGER observe_copy AFTER INSERT ON \"QGPL.DATA.DATA\" BEGIN INSERT INTO seen VALUES(NEW.ID); END");
        Ok("CRTDUPOBJ DATA QGPL *FILE QGPL COPY DATA(*YES) CST(*NO)");
        Assert.Equal(0L, Scalar("SELECT count(*) FROM seen")); Assert.Empty(_system.Journals.Read(new("QGPL", "JRN")));
        Run("INSERT INTO QGPL/COPY VALUES(1,-1)");
        Assert.Equal(1L, Scalar("SELECT n FROM seen")); Assert.Empty(_system.Journals.Read(new("QGPL", "JRN")));
        Run("INSERT INTO QGPL/DATA VALUES(4,4)");
        Assert.Equal(2L, Scalar("SELECT count(*) FROM seen")); Assert.Single(_system.Journals.Read(new("QGPL", "JRN")));
    }

    [Fact]
    public void Native_copy_owner_comes_from_the_live_caller_even_without_an_attached_command_job()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "DuplicateOwnerPassword329");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Read | AuthorityBit.ObjectManagement);
        using (OperationIdentity.Enter("QUSER")) Ok("CRTDUPOBJ DATA QGPL *FILE QGPL COPY");
        Assert.Equal("QUSER", _system.Objects.GetRequired("QGPL", "COPY", ObjectType.File).Owner);
        Assert.Equal("QSECOFR", _system.Objects.GetRequired("QGPL", "DATA", ObjectType.File).Owner);
    }

    [Fact]
    public void Restart_upgrades_unmodified_legacy_command_metadata_to_the_native_copy_defaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-duplicate-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var system = IpcSystem.Create(directory))
            {
                system.Start(); var files = new SqliteFileStore(system.Connections, system.Objects);
                files.ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER)", Options); files.ExecuteSql("INSERT INTO QGPL/DATA VALUES(1)", Options);
                var descriptor = system.Objects.GetRequired("QSYS", "CRTDUPOBJ", ObjectType.Command);
                var definition = new Ipc.Cl.Definitions.CommandDefinition { Builtin = "CRTDUPOBJ", Title = "CRTDUPOBJ", MaximumPositional = 0,
                    Parameters = new[] { "OBJ", "FROMLIB", "OBJTYPE", "TOLIB", "NEWOBJ" }.Select(keyword => new Ipc.Cl.Definitions.ParameterDefinition { Keyword = keyword, Type = "*RAW", Length = 1024, Prompt = keyword }).ToArray() };
                var json = definition.ToJson(); descriptor.Source = json;
                descriptor.ExtendedAttributes = new Dictionary<string, string>(descriptor.ExtendedAttributes!) {
                    [Ipc.Services.Commands.CommandDefinitionStore.MetadataAttribute] = json,
                    ["ipc.command.seed"] = Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(json))) };
                system.Objects.Update(descriptor);
            }
            using (var system = IpcSystem.Create(directory))
            {
                system.Start(); var result = new CommandService(system).Execute("CRTDUPOBJ DATA QGPL *FILE QGPL COPY");
                Assert.False(result.IsError, result.Message);
                Assert.Equal(0, new SqliteFileStore(system.Connections, system.Objects).RowCount("QGPL", "COPY", "DATA"));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Copying_a_zero_member_child_keeps_file_level_constraints_defined_until_member_addition(bool data)
    {
        Run("CREATE TABLE QGPL/CHILD(ID INTEGER REFERENCES QGPL/DATA(ID))");
        _files.RemoveMember("QGPL", "CHILD", "CHILD");
        Ok("CRTDUPOBJ CHILD QGPL *FILE QGPL COPY DATA(" + (data ? "*YES" : "*NO") + ")");
        Assert.Empty(_files.ListMembers("QGPL", "COPY"));
        Assert.True(_files.GetDefinition("QGPL", "COPY")!.SqlTable!.ForeignKeys![0].Defined);
        _files.AddMember("QGPL", "COPY", "ACTUAL");
        Assert.False(_files.GetDefinition("QGPL", "COPY")!.SqlTable!.ForeignKeys![0].Defined);
        Run("INSERT INTO QGPL/COPY VALUES(1)");
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/COPY VALUES(99)")).MessageId);
    }

    public void Dispose() => _system.Dispose();
}
