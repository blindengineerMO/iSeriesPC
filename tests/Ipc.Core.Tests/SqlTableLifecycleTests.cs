using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Services.Messages;

namespace Ipc.Core.Tests;

public sealed partial class SqlTableTests
{
    [Fact]
    public void Nullable_unique_keys_allow_multiple_NULLs_and_primary_keys_keep_their_external_null_layout()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER PRIMARY KEY, CODE CHAR(4) UNIQUE)");
        Assert.True(_files.GetDefinition("QGPL", "DATA")!.PrimaryFormat.Find("ID")!.NullCapable);
        Assert.Equal(2, Run("INSERT INTO QGPL.DATA VALUES(1,NULL),(2,NULL)"));
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL.DATA VALUES(NULL,'A')")).MessageId);
        Assert.Equal(1, Run("INSERT INTO QGPL.DATA VALUES(3,'A')"));
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL.DATA VALUES(4,'A ')")).MessageId);
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
    }

    [Fact]
    public void Bare_DEFAULT_is_NULL_for_nullable_columns_and_type_default_for_nonnullable_columns()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER WITH DEFAULT NOT NULL, VALUE DECIMAL(6,2) WITH DEFAULT, NAME CHAR(4) DEFAULT, DAY DATE DEFAULT NOT NULL)");
        Run("INSERT INTO QGPL.DATA DEFAULT VALUES");
        var row = Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"));
        Assert.Equal(0L, row["ID"]); Assert.Null(row["VALUE"]); Assert.Null(row["NAME"]); Assert.NotNull(row["DAY"]);
        Run("CREATE TABLE QGPL.NULLDEF(ID INTEGER NOT NULL DEFAULT NULL)");
        Assert.Equal("SQL0407", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL.NULLDEF DEFAULT VALUES")).MessageId);
    }

    [Fact]
    public void Named_constraints_are_unique_within_the_schema_and_failed_creation_is_atomic()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER, CONSTRAINT MYKEY UNIQUE(ID))");
        Assert.Equal("SQL0601", Assert.Throws<CpfException>(() => Run("CREATE TABLE QGPL.OTHER(ID INTEGER, CONSTRAINT MYKEY UNIQUE(ID))")).MessageId);
        Assert.False(_files.FileExists("QGPL", "OTHER"));
        Run("DROP TABLE QGPL.DATA"); Run("CREATE TABLE QGPL.OTHER(ID INTEGER, CONSTRAINT MYKEY UNIQUE(ID))");
    }

    [Fact]
    public void Copy_reconciles_named_constraints_without_weakening_either_tables_unique_index()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER, CONSTRAINT MYKEY UNIQUE(ID))"); Run("INSERT INTO QGPL.DATA VALUES(1)");
        new Ipc.Services.Sqlite.ObjectCatalogOperations(_system.Connections).Relocate(new("QGPL", "DATA"), ObjectType.File, new("QGPL", "COPIED"), copy: true);
        var original = Assert.Single(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Constraints);
        var copied = Assert.Single(_files.GetDefinition("QGPL", "COPIED")!.SqlTable!.Constraints);
        Assert.Equal("MYKEY", original.Name); Assert.NotEqual(original.Name, copied.Name); Assert.True(ObjectName.IsValid(copied.Name!));
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL.COPIED VALUES(1)")).MessageId);
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL.DATA VALUES(1)")).MessageId);
    }

    [Fact]
    public void Moving_a_table_cannot_create_duplicate_constraint_names_in_the_target_schema()
    {
        _system.Libraries.CreateLibrary("DEST");
        Run("CREATE TABLE QGPL.DATA(ID INTEGER, CONSTRAINT MYKEY UNIQUE(ID))");
        Run("CREATE TABLE DEST.OTHER(ID INTEGER, CONSTRAINT MYKEY UNIQUE(ID))");
        var operations = new Ipc.Services.Sqlite.ObjectCatalogOperations(_system.Connections);
        Assert.Equal("SQL0601", Assert.Throws<CpfException>(() => operations.Relocate(new("QGPL", "DATA"), ObjectType.File, new("DEST", "DATA"))).MessageId);
        Assert.True(_files.FileExists("QGPL", "DATA")); Assert.False(_files.FileExists("DEST", "DATA"));
    }

    [Fact]
    public void SQL_numeric_types_produce_the_published_big_endian_packed_and_zoned_external_layouts()
    {
        Run("CREATE TABLE QGPL.DATA(SM SMALLINT NOT NULL, ID INTEGER NOT NULL, BIG BIGINT NOT NULL, PACK DECIMAL(5,2) NOT NULL, ZONE NUMERIC(5,2) NOT NULL, TEXT CHAR(3) NOT NULL)");
        Run("INSERT INTO QGPL.DATA VALUES(129,-2,9223372036854775807,12.34,-12.34,'A')");
        var definition = _files.GetDefinition("QGPL", "DATA")!;
        var row = Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"));
        var bytes = new Ipc.Db.Records.RecordCodec().Encode(definition.PrimaryFormat, row);
        Assert.Equal("0081FFFFFFFE7FFFFFFFFFFFFFFF01234CF0F1F2F3D4C14040", Convert.ToHexString(bytes));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void RUNSQL_checks_5000_encoded_bytes_including_multibyte_comment_text(int ccsid)
    {
        using var session = new Ipc.Session.ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        string Statement(int bytes)
        {
            const string prefix = "CREATE TABLE QGPL/LIMITS(ID INTEGER) --";
            var remaining = bytes - Ipc.Core.Text.CodePage.ToBytes(ccsid, prefix).Length;
            var width = Ipc.Core.Text.CodePage.ToBytes(ccsid, "é").Length;
            return prefix + new string('é', remaining / width) + new string('x', remaining % width);
        }
        var accepted = session.Execute("RUNSQL SQL('" + Statement(5000) + "') COMMIT(*NONE)"); Assert.False(accepted.IsError, accepted.Message);
        var rejected = session.Execute("RUNSQL SQL('" + Statement(5001) + "') COMMIT(*NONE)"); Assert.True(rejected.IsError); Assert.Contains("IPC0003", rejected.Message);
    }

    [Theory]
    [InlineData("REAL", "1.25E+3", 1250d)]
    [InlineData("DOUBLE PRECISION", "-.25e-2", -0.0025d)]
    [InlineData("DECIMAL(6,2)", ".25", 0.25d)]
    public void Numeric_literals_preserve_fraction_and_exponent_syntax(string type, string value, double expected)
    {
        Run("CREATE TABLE QGPL.DATA(VALUE " + type + ")"); Run("INSERT INTO QGPL.DATA VALUES(" + value + ")");
        var row = Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA")); Assert.Equal(expected, Convert.ToDouble(row["VALUE"]));
    }

    [Theory]
    [InlineData("CREATE TABLE QGPL.DATA(ID INTEGER)")]
    [InlineData("DROP TABLE QGPL.DATA")]
    [InlineData("INSERT INTO QGPL.DATA VALUES(1)")]
    [InlineData("UPDATE QGPL.DATA SET ID=2")]
    [InlineData("DELETE FROM QGPL.DATA")]
    public async Task SQL_writer_waits_are_cancellable_without_publishing_metadata_or_records(string sql)
    {
        if (!sql.StartsWith("CREATE", StringComparison.Ordinal)) Run("CREATE TABLE QGPL.DATA(ID INTEGER)");
        using var connection = _system.Connections.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var cancel = new CancellationTokenSource();
        var work = Task.Run(() => Record.Exception(() => _files.ExecuteSql(sql, Options(), cancel.Token)));
        try
        {
            await Task.Delay(150); cancel.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>(await work.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { cancel.Cancel(); transaction.Rollback(); await work.WaitAsync(TimeSpan.FromSeconds(10)); }
        if (sql.StartsWith("CREATE", StringComparison.Ordinal)) Assert.False(_files.FileExists("QGPL", "DATA"));
        else { Assert.True(_files.FileExists("QGPL", "DATA")); Assert.Equal(0, _files.RowCount("QGPL", "DATA", "DATA")); }
    }

    [Fact]
    public void SQL_creation_uses_the_live_principal_as_owner_and_checks_library_add_authority()
    {
        _system.Security.Profiles.Create(new() { Name = "SQLUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("SQLUSER"), "SqlTableUserFixture43");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "SQLUSER", AuthorityBit.ObjectOperate);
        using (OperationIdentity.Enter("SQLUSER")) Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("CREATE TABLE QGPL.DATA(ID INTEGER)")).MessageId);
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "SQLUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        using (OperationIdentity.Enter("SQLUSER")) Run("CREATE TABLE QGPL.DATA(ID INTEGER)");
        Assert.Equal("SQLUSER", _system.Objects.GetRequired("QGPL", "DATA", ObjectType.File).Owner);
    }

    [Fact]
    public void SQL_insert_and_drop_cannot_bypass_file_authority()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)");
        _system.Security.Profiles.Create(new() { Name = "SQLUSER" });
        using (var connection = _system.Connections.Open()) using (var command = connection.CreateCommand())
        { command.CommandText = "UPDATE sys_objects SET public_authority=1 WHERE lib='QGPL' AND name='DATA' AND type='*FILE'"; command.ExecuteNonQuery(); }
        using (OperationIdentity.Enter("SQLUSER"))
        {
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL.DATA VALUES(1)")).MessageId);
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("DROP TABLE QGPL.DATA")).MessageId);
        }
        Assert.Equal(0, _files.RowCount("QGPL", "DATA", "DATA"));
    }

    [Fact]
    public void SQL_temporary_tables_are_job_private_and_errors_do_not_expose_the_backing_namespace()
    {
        using var owner = new Ipc.Session.ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        using var other = new Ipc.Session.ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        Assert.False(owner.Execute("RUNSQL SQL('CREATE TABLE QTEMP/DATA(ID INTEGER NOT NULL)') COMMIT(*NONE)").IsError);
        var denied = other.Execute("RUNSQL SQL('INSERT INTO QTEMP/DATA VALUES(1)') COMMIT(*NONE)"); Assert.True(denied.IsError); Assert.Contains("SQL0551", denied.Message);
        string backing;
        using (OperationIdentity.Enter("QSECOFR", owner.Job.Key)) backing = new Ipc.Services.Work.JobTemporaryLibrary(_system.Connections).ResolveLibrary("QTEMP");
        var invalid = owner.Execute("RUNSQL SQL('INSERT INTO QTEMP/DATA VALUES(NULL)') COMMIT(*NONE)");
        Assert.True(invalid.IsError); Assert.Contains("SQL0407", invalid.Message); Assert.DoesNotContain(backing, invalid.Message);
        using (var connection = _system.Connections.Open()) using (var command = connection.CreateCommand())
        {
            command.CommandText = $"CREATE TRIGGER sql_bad_null AFTER INSERT ON \"{backing}.DATA.DATA\" BEGIN UPDATE \"{backing}.DATA.DATA\" SET ID=NULL; END"; command.ExecuteNonQuery();
            var failure = owner.Execute("RUNSQL SQL('INSERT INTO QTEMP/DATA VALUES(1)') COMMIT(*NONE)");
            Assert.True(failure.IsError); Assert.Contains("SQL0407", failure.Message); Assert.Contains("QTEMP", failure.Message); Assert.DoesNotContain(backing, failure.Message);
            command.CommandText = "DROP TRIGGER sql_bad_null"; command.ExecuteNonQuery();
        }
        Assert.False(owner.Execute("RUNSQL SQL('INSERT INTO QTEMP/DATA VALUES(1)') COMMIT(*NONE)").IsError);
    }

    [Fact]
    public void A_multirow_capacity_retry_rolls_back_the_attempt_and_captures_each_successful_row_once()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)"); _files.ChangePhysicalMemberLimit("QGPL", "DATA", null, memberSize: new(1, 2, 1, false));
        Run("INSERT INTO QGPL.DATA VALUES(1),(2)");
        _system.Journals.CreateReceiver(new("QGPL", "SQLRCV")); _system.Journals.Create(new("QGPL", "SQLJRN"), new("QGPL", "SQLRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "SQLJRN"), "*BOTH");
        new SystemReplyList(_system.Connections).Put(new(1, "CPA5305", Reply: "I"));
        var job = _system.Jobs.CreateInteractive("QSECOFR"); new InquiryReplyPolicy(_system.Connections).Set(job.Key, "*SYSRPYL");
        using (OperationIdentity.Enter("QSECOFR", job.Key)) Assert.Equal(2, Run("INSERT INTO QGPL.DATA VALUES(3),(4)"));
        Assert.Equal(4, _files.RowCount("QGPL", "DATA", "DATA"));
        Assert.Equal(new long?[] { 3, 4 }, _system.Journals.Read(new("QGPL", "SQLJRN")).Select(entry => entry.RecordNumber));
        Assert.All(_system.Journals.Read(new("QGPL", "SQLJRN")), entry => Assert.Equal(job.Key.ToString(), entry.Job));
        Assert.Single(new MessageQueueStore(_system.Connections).List(new("QSYS", "QSYSOPR")));
    }

    [Fact]
    public void SQL_default_and_literal_text_cannot_execute_an_injected_catalog_statement()
    {
        var objects = _system.Objects.ObjectCount;
        Run("CREATE TABLE QGPL.DATA(TEXT VARCHAR(80) DEFAULT '); DROP TABLE sys_jobs; --')");
        Run("INSERT INTO QGPL.DATA DEFAULT VALUES"); Run("INSERT INTO QGPL.DATA VALUES('x''); DELETE FROM sys_objects; --')");
        Assert.Equal(2, _files.RowCount("QGPL", "DATA", "DATA")); Assert.Equal(objects + 1, _system.Objects.ObjectCount);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name='sys_jobs'"));
    }

    [Theory]
    [InlineData(37, false)]
    [InlineData(1208, false)]
    [InlineData(37, true)]
    [InlineData(1208, true)]
    public void SQL_created_tables_are_consumed_by_compiled_RPG_without_a_separate_database(int ccsid, bool temporary)
    {
        using var session = new Ipc.Session.ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var library = temporary ? "QTEMP" : "QGPL";
        using (OperationIdentity.Enter("QSECOFR", session.Job.Key))
        {
            Run($"CREATE TABLE {library}.SQLDATA(ID INTEGER NOT NULL, NAME CHAR(12) NOT NULL) RCDFMT SQLROW", ccsid);
            Run($"INSERT INTO {library}.SQLDATA VALUES(7,'shared')", ccsid);
            Run($"UPDATE {library}.SQLDATA SET ID=ID+1,NAME='changed' WHERE ID=7", ccsid);
        }
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "SQLRPG", $$"""
            **free
            dcl-f SQLDATA extdesc('{{library}}/SQLDATA') extfile(*extdesc) usage(*input);
            read SQLDATA;
            sndpgmmsg %char(ID)+':'+%trim(NAME);
            seton LR;
            """, null);
        var compiled = session.Execute("CRTBNDRPG PGM(QGPL/SQLRPG) SRCFILE(QGPL/SOURCE)"); Assert.False(compiled.IsError, compiled.Message);
        _files.RemoveMember("QGPL", "SOURCE", "SQLRPG");
        var called = session.Execute("CALL QGPL/SQLRPG"); Assert.False(called.IsError, called.Message); Assert.Contains("8:changed", called.Message);
        using (OperationIdentity.Enter("QSECOFR", session.Job.Key)) Assert.Equal(1, Run($"DELETE FROM {library}.SQLDATA WHERE ID=8", ccsid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_manual_SQL_capacity_wait_publishes_no_prefix_and_releases_the_writer(bool cancel)
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)"); _files.ChangePhysicalMemberLimit("QGPL", "DATA", null, memberSize: new(1, 2, 1, false));
        Run("INSERT INTO QGPL.DATA VALUES(1),(2)"); Run("CREATE TABLE QGPL.OTHER(ID INTEGER)");
        var job = _system.Jobs.CreateInteractive("QSECOFR"); using var cancellation = new CancellationTokenSource();
        var work = Task.Run(() => {
            using var identity = OperationIdentity.Enter("QSECOFR", job.Key);
            return Record.Exception(() => _files.ExecuteSql("INSERT INTO QGPL.DATA VALUES(3),(4)", Options(), cancellation.Token));
        });
        try
        {
            var messages = new MessageQueueStore(_system.Connections); var queue = new QualifiedName("QSYS", "QSYSOPR");
            var deadline = DateTime.UtcNow.AddSeconds(10); Ipc.Services.Messages.QueuedMessage? inquiry;
            while ((inquiry = messages.List(queue).FirstOrDefault(message => message.MessageId == "CPA5305" && !message.Replied)) is null)
            { Assert.True(DateTime.UtcNow < deadline, "Expected SQL capacity inquiry."); await Task.Delay(20); }
            Assert.Equal(2, _files.RowCount("QGPL", "DATA", "DATA")); Assert.Equal(1, Run("INSERT INTO QGPL.OTHER VALUES(1)"));
            if (cancel) cancellation.Cancel();
            else messages.Reply(queue, inquiry.Key, new ProgramBuffer(Ipc.Core.Text.CodePage.ToBytes(37, "I"), 37));
            var error = await work.WaitAsync(TimeSpan.FromSeconds(10));
            if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.Null(error);
            Assert.Equal(cancel ? 2 : 4, _files.RowCount("QGPL", "DATA", "DATA"));
            Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_member_size_waits"));
        }
        finally { cancellation.Cancel(); await work.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
