using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Dds;
using Ipc.Db.Store;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    private static IReadOnlyDictionary<string, object?> AddedValues(long id = 104, decimal key = 15.25m) =>
        new Dictionary<string, object?> { ["ID"] = id, ["KEY"] = key, ["TEXT"] = "added" };
    private void OutputView(bool unique = false, bool selected = false)
    {
        var lines = new List<string>();
        if (unique) lines.Add(LogicalFileTests.Line(' ', "", "UNIQUE"));
        lines.AddRange([LogicalFileTests.Line('R', "VROW", "PFILE(QGPL/DATA)"), LogicalFileTests.Line(' ', "KEY"),
            LogicalFileTests.Line(' ', "TEXT"), LogicalFileTests.Line('K', "KEY")]);
        if (selected) lines.Add(LogicalFileTests.Line('S', "KEY", "COMP(GT 10)"));
        var source = string.Join('\n', lines);
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile("VIEW", source);
        _files.CreateLogicalFile("QGPL", "VIEW", definition, source);
    }
    [Fact]
    public void Logical_WRITE_uses_one_source_member_and_supplies_defaults_for_unprojected_fields()
    {
        OutputView(); Compile("**free\ndcl-f VIEW keyed usage(*output) rename(VROW:ITEM) prefix(V_);\neval V_KEY=45.50;\neval V_TEXT='logical';\nwrite ITEM;\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message);
        var row = _files.ReadAll("QGPL", "DATA", "DATA").Single(r => (decimal)r["KEY"]! == 45.50m);
        Assert.Equal(0L, row["ID"]); Assert.Equal("logical", ((string)row["TEXT"]!).TrimEnd()); Assert.Empty(Locks());
    }
    [Fact]
    public void Logical_output_rejects_rows_outside_selection_without_leaving_inserted_data()
    {
        OutputView(selected: true); Compile("**free\ndcl-f VIEW keyed usage(*output);\neval KEY=5;\nwrite VROW;\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Contains("selection", result.Message); Assert.Equal(3, Count); Assert.Empty(Locks());
    }
    [Fact]
    public void Logical_output_rejects_ambiguous_multiple_physical_members_at_open()
    {
        _files.AddMember("QGPL", "DATA", "SECOND"); OutputView();
        Compile("**free\ndcl-f VIEW keyed usage(*output);\nseton LR;");
        using var session = Session(); var result = session.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Contains("one bound physical member", result.Message); Assert.Empty(Locks());
    }
    [Fact]
    public void Logical_output_checks_Add_on_the_physical_source_before_exposing_a_writer()
    {
        OutputView(); _system.Security.Profiles.Create(new() { Name = "ADDUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("ADDUSER"), "LogicalOutputAccess42");
        _system.Security.Authority.Grant("QGPL", "VIEW", ObjectType.File, "ADDUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "ADDUSER", AuthorityBit.ObjectOperate);
        using var session = new Ipc.Session.ExecutionSession(_system, _system.Jobs.CreateInteractive("ADDUSER"), CancellationToken.None);
        using (OperationIdentity.Enter("ADDUSER", session.Job.Key))
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.OpenRecordWriter("QGPL", "VIEW", "VIEW")).MessageId);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "ADDUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        using var owner = OperationIdentity.Enter("ADDUSER", session.Job.Key);
        using var writer = _files.OpenRecordWriter("QGPL", "VIEW", "VIEW");
        writer.Write(new Dictionary<string, object?> { ["KEY"] = 45m, ["TEXT"] = "allowed" });
    }
    [Fact]
    public void Failed_unique_WRITE_rolls_back_and_the_open_writer_can_retry_with_a_new_key()
    {
        OutputView(unique: true); using var session = Session(); using var owner = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        using var writer = _files.OpenRecordWriter("QGPL", "DATA", "DATA");
        Assert.Throws<SqliteException>(() => writer.Write(AddedValues(key: 20)));
        Assert.Equal(3, Count); writer.Write(AddedValues()); Assert.Equal("added", Text(104));
        Assert.DoesNotContain(Locks(), l => l.Resource.RowNumber != 0);
    }
    [Fact]
    public void Writer_rebinds_cancellation_for_retained_calls_and_rejects_another_job()
    {
        using var session = Session(); using var second = Session(); using var owner = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        using var source = new CancellationTokenSource(); using var writer = _files.OpenRecordWriter("QGPL", "DATA", "DATA", source.Token);
        source.Cancel(); Assert.ThrowsAny<OperationCanceledException>(writer.Validate); writer.RebindCancellation(CancellationToken.None);
        using (OperationIdentity.Enter("QSECOFR", second.Job.Key))
        {
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(writer.Validate).MessageId);
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => writer.RebindCancellation(CancellationToken.None)).MessageId);
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => writer.Write(AddedValues())).MessageId);
        }
        writer.Write(AddedValues()); Assert.Equal("added", Text(104));
    }
    [Fact]
    public void Output_file_allocations_deny_another_jobs_exclusive_open_and_release_on_dispose()
    {
        using var first = Session(); using var second = Session(); var locks = new JobLockStore(_system.Connections);
        using (OperationIdentity.Enter("QSECOFR", first.Job.Key))
        {
            using var writer = _files.OpenRecordWriter("QGPL", "DATA", "DATA");
            using var other = OperationIdentity.Enter("QSECOFR", second.Job.Key);
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => locks.Acquire(second.Job.Key, new("QGPL", "DATA", ObjectType.File), JobLockMode.Exclusive, TimeSpan.Zero)).MessageId);
        }
        Assert.Empty(Locks());
    }
    [Fact]
    public void Disposed_output_handles_cannot_validate_rebind_or_write()
    {
        using var session = Session(); using var owner = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var writer = _files.OpenRecordWriter("QGPL", "DATA", "DATA"); writer.Dispose(); writer.Dispose();
        Assert.Throws<ObjectDisposedException>(writer.Validate); Assert.Throws<ObjectDisposedException>(() => writer.Write(AddedValues()));
        Assert.Throws<ObjectDisposedException>(() => writer.RebindCancellation(CancellationToken.None)); Assert.Empty(Locks());
    }
    [Fact]
    public void Input_and_output_paths_with_SHARE_keep_their_compatible_modes_separate()
    {
        Compile("**free\ndcl-f DATA keyed;\nread DATA;\nsndpgmmsg %char(ID);\nreturn;", "INPUT", "OUTFILES");
        Compile(OutputFile() + OutputValues + "seton LR;", "OUTPUT");
        using var session = Session(); Assert.False(session.Execute("OVRDBF FILE(DATA) TOFILE(QGPL/DATA) SHARE(*YES) OVRSCOPE(*JOB)").IsError);
        Ok(session.Execute("CALL QGPL/INPUT"), "102"); Assert.False(session.Execute("CALL QGPL/OUTPUT").IsError);
        Ok(session.Execute("CALL QGPL/INPUT"), "104"); Assert.False(session.Execute("RCLACTGRP ACTGRP(OUTFILES)").IsError); Assert.Empty(Locks());
    }
    [Fact]
    public void Output_overrides_select_the_runtime_member_without_changing_the_compiled_layout()
    {
        Data("OTHER"); _files.AddMember("QGPL", "OTHER", "SECOND");
        Compile(OutputFile() + OutputValues + "seton LR;");
        using var session = Session(); Assert.False(session.Execute("OVRDBF FILE(DATA) TOFILE(QGPL/OTHER) MBR(SECOND)").IsError);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Equal(3, Count);
        Assert.Equal(104L, Assert.Single(_files.ReadAll("QGPL", "OTHER", "SECOND"))["ID"]);
    }
    [Fact]
    public void Custom_hosts_cannot_supply_an_input_handle_for_an_output_binding()
    {
        var program = Parse(OutputFile() + OutputValues);
        var host = new RpgHost { OpenDisk = _ => { var cursor = new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "DATA", "DATA", keyed: true); return new(() => cursor, cursor.Dispose); } };
        Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program)); Assert.Equal(3, Count);
    }
}
