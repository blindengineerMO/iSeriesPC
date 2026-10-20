using Ipc.Cl.Commands;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class QueryManagementTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private readonly ExecutionSession _session;
    private readonly QueryManagementStore _queries;
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    public QueryManagementTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "QueryManagementFixture827");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "QQMQRYSRC", sourceWidth: 240);
        _files.ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER PRIMARY KEY,NAME VARCHAR(40))", Options);
        _queries = new(_system.Connections);
        _session = new(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
    }
    public void Dispose() { _session.Dispose(); _system.Dispose(); }
    private void Source(string sql) => _files.SaveSourceMember("QGPL", "QQMQRYSRC", "QUERY", sql, _files.ReadSourceMember("QGPL", "QQMQRYSRC", "QUERY").Revision);
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private void Create(string source, string options = "") { Source(source); Ok(_session.Execute("CRTQMQRY QUERY " + options)); }
    private object? Scalar(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    [Fact]
    public void Default_source_member_and_library_create_an_independent_object_and_run_typed_fragments()
    {
        Create("INSERT INTO &LIB/DATA VALUES(&ID,&NAME)", "TEXT('Saved insertion')");
        Source("DELETE FROM QGPL/DATA");
        Ok(_session.Execute("STRQMQRY QUERY SETVAR((LIB QGPL) (ID 7) (NAME '''O''''Malley'''))"));
        var row = Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA")); Assert.Equal(7L, row["ID"]); Assert.Equal("O'Malley", row["NAME"]);
        var descriptor = _system.Objects.GetRequired("QGPL", "QUERY", ObjectType.QueryManagementQuery);
        Assert.Equal("SQL", descriptor.Attribute); Assert.Equal("Saved insertion", descriptor.Description); Assert.Equal("QSECOFR", descriptor.Owner);
        Assert.Contains("&LIB", _queries.Read("QGPL", "QUERY"));
    }
    [Fact]
    public void Retrieval_creates_or_atomically_replaces_a_member_and_can_recreate_the_query()
    {
        Create("-- saved comment\nINSERT INTO QGPL/DATA VALUES(8,'retrieved')");
        Ok(_session.Execute("RTVQMQRY QUERY QGPL/QQMQRYSRC BACKUP"));
        var snapshot = _files.ReadSourceMember("QGPL", "QQMQRYSRC", "BACKUP"); Assert.Contains("-- saved comment", snapshot.Source);
        _files.SaveSourceMember("QGPL", "QQMQRYSRC", "BACKUP", "DELETE FROM QGPL/DATA", snapshot.Revision);
        Ok(_session.Execute("RTVQMQRY QMQRY(QUERY) SRCFILE(QGPL/QQMQRYSRC) SRCMBR(BACKUP)"));
        Ok(_session.Execute("DLTQMQRY QUERY"));
        Ok(_session.Execute("CRTQMQRY QUERY QGPL/QQMQRYSRC BACKUP"));
        Ok(_session.Execute("STRQMQRY QUERY")); Assert.Equal(8L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
    }
    [Fact]
    public void Replace_defaults_to_yes_preserves_existing_authority_and_no_leaves_the_old_payload()
    {
        Create("INSERT INTO QGPL/DATA VALUES(1,'old')", "AUT(*EXCLUDE)");
        var original = _system.Objects.GetRequired("QGPL", "QUERY", ObjectType.QueryManagementQuery);
        _system.Security.Authority.Grant("QGPL", "QUERY", ObjectType.QueryManagementQuery, "QUSER", Authorities.UseBits);
        Source("INSERT INTO QGPL/DATA VALUES(2,'new')"); Assert.Equal("QWM2706", _session.Execute("CRTQMQRY QUERY REPLACE(*NO)").MessageId);
        Ok(_session.Execute("CRTQMQRY QUERY AUT(*ALL)")); var changed = _system.Objects.GetRequired("QGPL", "QUERY", ObjectType.QueryManagementQuery);
        Assert.Equal(original.Created, changed.Created); Assert.Equal(original.Owner, changed.Owner); Assert.Equal(AuthorityBit.None, changed.PublicAuthority);
        Assert.Equal((long)(int)Authorities.UseBits, Scalar("SELECT bits FROM sys_authorities WHERE name='QUERY' AND holder='QUSER'"));
        Ok(_session.Execute("STRQMQRY QUERY")); Assert.Equal(2L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
    }
    [Fact]
    public void SQL_naming_requires_an_explicit_query_library_and_accepts_SAA()
    {
        Create("INSERT INTO QGPL.DATA VALUES(1,'sql naming')");
        Assert.Equal("QWM2707", _session.Execute("STRQMQRY QUERY NAMING(*SQL)").MessageId);
        Ok(_session.Execute("STRQMQRY QGPL/QUERY NAMING(*SAA)")); Assert.Equal(1, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Theory]
    [InlineData("SETVAR((X 1) (X 2))")]
    [InlineData("SETVAR((DSQUSER 1))")]
    [InlineData("SETVAR((X '12345678901234567890123456789012345678901234567890123456'))")]
    [InlineData("SETVAR(X 1)")]
    [InlineData("SETVAR((X))")]
    public void Malformed_duplicate_reserved_and_oversized_SET_VAR_values_cannot_run(string parameters)
    {
        Create("INSERT INTO QGPL/DATA VALUES(1,'unrun')"); Assert.True(_session.Execute("STRQMQRY QUERY " + parameters).IsError);
        Assert.Equal(0, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Theory]
    [InlineData("OUTPUT(*PRINT)")]
    [InlineData("RDB(REMOTE)")]
    [InlineData("USER(OTHER)")]
    [InlineData("PASSWORD(secret)")]
    [InlineData("QMFORM(QGPL/FORM)")]
    [InlineData("ALWQRYDFN(*YES)")]
    public void Unfinished_query_modes_reject_before_any_mutation(string parameters)
    {
        Create("INSERT INTO QGPL/DATA VALUES(1,'unrun')"); Assert.Equal("QWM2701", _session.Execute("STRQMQRY QUERY " + parameters).MessageId);
        Assert.Equal(0, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Fact]
    public void Multiple_statements_catalog_injection_and_missing_variables_leave_records_unchanged()
    {
        Create("DELETE FROM QGPL/DATA; INSERT INTO QGPL/DATA VALUES(1,'bad')"); Assert.Equal("QWM2701", _session.Execute("STRQMQRY QUERY").MessageId);
        Create("INSERT INTO QGPL/DATA VALUES(&X,'value')");
        Assert.Equal("QWM2701", _session.Execute("STRQMQRY QUERY").MessageId);
        Assert.Equal("QWM2701", _session.Execute("STRQMQRY QUERY SETVAR((X '1); DELETE FROM QGPL/DATA;--'))").MessageId);
        Create("DELETE FROM sys_objects"); Assert.Equal("QWM2701", _session.Execute("STRQMQRY QUERY").MessageId);
        Assert.Contains(_system.Jobs.GetLog(_session.Job.Key, 1000), entry => entry.MessageId == "SQL0104");
        Assert.Equal(0, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Fact]
    public void Fixed_source_import_uses_only_79_bytes_and_defers_SQL_validation_until_execution()
    {
        Source("INSERT INTO QGPL/DATA VALUES(3,'native margin')".PadRight(79) + "INVALID"); Ok(_session.Execute("CRTQMQRY QUERY"));
        Ok(_session.Execute("STRQMQRY QUERY")); Assert.Equal(1, _files.RowCount("QGPL", "DATA", "DATA"));
        Create("not valid SQL"); Assert.Equal("QWM2701", _session.Execute("STRQMQRY QUERY").MessageId);
    }
    [Fact]
    public void Query_objects_use_generic_copy_move_rename_and_delete_without_a_side_catalog()
    {
        Create("INSERT INTO QGPL/DATA VALUES(4,'copy')"); _system.Libraries.CreateLibrary("QRYLIB");
        Ok(_session.Execute("CRTDUPOBJ OBJ(QUERY) FROMLIB(QGPL) OBJTYPE(*QMQRY) TOLIB(QRYLIB) NEWOBJ(COPY)"));
        Ok(_session.Execute("RNMOBJ OBJ(QRYLIB/COPY) OBJTYPE(*QMQRY) NEWOBJ(RENAMED)"));
        Ok(_session.Execute("MOVOBJ OBJ(QRYLIB/RENAMED) OBJTYPE(*QMQRY) TOLIB(QGPL)"));
        Ok(_session.Execute("DLTQMQRY QUERY")); Ok(_session.Execute("STRQMQRY RENAMED"));
        Assert.Equal(4L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
    }
    [Fact]
    public void Public_authority_and_target_file_authority_are_independent_and_replacement_is_protected()
    {
        Create("INSERT INTO QGPL/DATA VALUES(5,'authorized')", "AUT(*EXCLUDE)");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "QueryManagerReader910");
        using var user = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        Assert.Equal("CPF9802", user.Execute("STRQMQRY QUERY").MessageId);
        _system.Security.Authority.Grant("QGPL", "QUERY", ObjectType.QueryManagementQuery, "QUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", AuthorityBit.None);
        Assert.Equal("QWM2701", user.Execute("STRQMQRY QUERY").MessageId);
        Assert.Contains(_system.Jobs.GetLog(user.Job.Key, 1000), entry => entry.MessageId == "SQL0551");
        Assert.Equal("CPF9802", user.Execute("CRTQMQRY QUERY SRCFILE(QGPL/QQMQRYSRC)").MessageId);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", Authorities.ChangeBits);
        Ok(user.Execute("STRQMQRY QUERY")); Assert.Equal(1, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Fact]
    public void Create_and_replace_roll_back_when_the_audit_cannot_be_written()
    {
        Create("INSERT INTO QGPL/DATA VALUES(6,'original')"); var original = _queries.Read("QGPL", "QUERY");
        Scalar("CREATE TRIGGER fail_query_event BEFORE INSERT ON sys_events WHEN NEW.kind='query.created' BEGIN SELECT RAISE(ABORT,'audit unavailable'); END");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _queries.Create("QGPL", "OTHER", "DELETE FROM DATA"));
        Assert.False(_system.Objects.Exists("QGPL", "OTHER", ObjectType.QueryManagementQuery));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _queries.Create("QGPL", "QUERY", "DELETE FROM DATA"));
        Assert.Equal(original, _queries.Read("QGPL", "QUERY"));
        Assert.DoesNotContain("original", (string)Scalar("SELECT payload FROM sys_events WHERE kind='query.created' LIMIT 1")!);
    }
    [Fact]
    public void Ended_jobs_and_cancellation_cannot_publish_a_query_object()
    {
        var job = _system.Jobs.CreateInteractive("QSECOFR"); _system.Jobs.Complete(job.Key);
        using (OperationIdentity.Enter("QSECOFR", job.Key))
            Assert.Equal("CPF1241", Assert.Throws<CpfException>(() => _queries.Create("QGPL", "ENDED", "DELETE FROM DATA")).MessageId);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _queries.Create("QGPL", "STOPPED", "DELETE FROM DATA", cancellationToken: stop.Token));
        Assert.False(_system.Objects.Exists("QGPL", "ENDED", ObjectType.QueryManagementQuery));
        Assert.False(_system.Objects.Exists("QGPL", "STOPPED", ObjectType.QueryManagementQuery));
    }
    [Fact]
    public void SETVAR_accepts_50_native_pairs_beyond_the_old_generic_metadata_length()
    {
        Create("INSERT INTO QGPL/DATA VALUES(&X00,'many variables')");
        var entries = Enumerable.Range(0, 50).Select(index => "(" + (index == 0 ? "X00" : "X" + index.ToString("D29")) + " '" + (index == 0 ? "7" : new string('\'', 110)) + "')");
        var metadata = new Ipc.Console.Session.CommandService(_system, _session.Job).CommandMetadata("STRQMQRY");
        Assert.Equal(8192, metadata.Parameters.Single(parameter => parameter.Keyword == "SETVAR").MaximumLength);
        Ok(_session.Execute("STRQMQRY QUERY SETVAR(" + string.Join(' ', entries) + ")"));
        Assert.Equal(7L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
    }
    [Fact]
    public void CL_typed_variables_reach_substitution_and_SQL_escapes_remain_monitorable()
    {
        Create("INSERT INTO QGPL/DATA VALUES(&ID,&NAME)"); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "QRYCL", """
            PGM
            DCL VAR(&ID) TYPE(*DEC) LEN(5 0) VALUE(12)
            DCL VAR(&NAME) TYPE(*CHAR) LEN(20) VALUE('''from CL''')
            STRQMQRY QUERY SETVAR((ID &ID) (NAME &NAME))
            STRQMQRY QUERY SETVAR((ID &ID) (NAME &NAME))
            MONMSG MSGID(QWM2701) EXEC(RUNSQL SQL('UPDATE QGPL/DATA SET NAME=''monitored'' WHERE ID=12') COMMIT(*NONE))
            ENDPGM
            """, null);
        Ok(_session.Execute("CRTCLPGM PGM(QGPL/QRYCL) SRCFILE(QGPL/SOURCE)")); Ok(_session.Execute("CALL QGPL/QRYCL"));
        var row = Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA")); Assert.Equal(12L, row["ID"]); Assert.Equal("monitored", row["NAME"]);
        Assert.Contains(_system.Jobs.GetLog(_session.Job.Key, 1000), entry => entry.MessageId == "SQL0803");
    }
    [Fact]
    public void Execution_checks_the_signed_source_snapshot_and_replacement_does_not_remove_signature_policy()
    {
        Create("INSERT INTO QGPL/DATA VALUES(1,'signed')");
        var ca = _system.Certificates.CreateAuthority("QRYCA", "Query signing CA");
        _system.Certificates.SetTrust(ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning, true);
        var signer = _system.Certificates.Issue("QRYSIGN", "Query signer", ca.Id, Ipc.Services.Security.CertificatePurpose.ObjectSigning);
        _system.ObjectSigning.Sign("QGPL", "QUERY", ObjectType.QueryManagementQuery, signer.Id);
        Ok(_session.Execute("STRQMQRY QUERY"));
        var descriptor = _system.Objects.GetRequired("QGPL", "QUERY", ObjectType.QueryManagementQuery);
        descriptor.Source = QueryManagementSql.SourceRecords("DELETE FROM QGPL/DATA", 37); _system.Objects.Update(descriptor);
        Assert.True(_session.Execute("STRQMQRY QUERY").IsError); Assert.Equal(1, _files.RowCount("QGPL", "DATA", "DATA"));
        Ok(_session.Execute("CRTQMQRY QUERY")); Assert.True(_session.Execute("STRQMQRY QUERY").IsError);
        Assert.Equal("NOSIG", _system.ObjectSigning.Check("QGPL", "QUERY", ObjectType.QueryManagementQuery).Result);
        _system.ObjectSigning.Sign("QGPL", "QUERY", ObjectType.QueryManagementQuery, signer.Id);
        Assert.Equal("QWM2701", _session.Execute("STRQMQRY QUERY").MessageId);
        Assert.Contains(_system.Jobs.GetLog(_session.Job.Key, 1000), entry => entry.MessageId == "SQL0803");
    }
    [Fact]
    public void QTEMP_query_objects_are_private_to_each_job_and_removed_at_job_end()
    {
        Source("INSERT INTO QGPL/DATA VALUES(9,'private')");
        Ok(_session.Execute("CRTQMQRY QTEMP/PRIVATE SRCFILE(QGPL/QQMQRYSRC) SRCMBR(QUERY)"));
        using (var other = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None))
            Assert.Equal("CPF9801", other.Execute("STRQMQRY QTEMP/PRIVATE").MessageId);
        Ok(_session.Execute("STRQMQRY QTEMP/PRIVATE"));
        var backing = (string)Scalar("SELECT lib FROM sys_objects WHERE type='*QMQRY' AND name='PRIVATE'")!;
        _session.Dispose(); Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_objects WHERE lib='" + backing + "' AND name='PRIVATE'"));
    }
    [Fact]
    public void Retrieval_encoding_or_width_failure_keeps_the_existing_member_and_its_revision()
    {
        Create("UPDATE QGPL/DATA SET NAME='" + new string('x', 40) + "' WHERE ID=1");
        _files.CreateSourceFile("QGPL", "NARROW", sourceWidth: 44);
        var before = _files.SaveSourceMember("QGPL", "NARROW", "TARGET", "old content", null);
        Assert.Equal("CPF2817", _session.Execute("RTVQMQRY QUERY QGPL/NARROW TARGET").MessageId);
        var after = _files.ReadSourceMember("QGPL", "NARROW", "TARGET"); Assert.Equal(before.Revision, after.Revision); Assert.Equal(before.Source, after.Source);
    }
}
