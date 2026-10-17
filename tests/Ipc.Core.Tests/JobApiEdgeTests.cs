using System.Buffers.Binary;
using System.Security.Cryptography;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class JobApiTests
{
    [Theory]
    [InlineData(7, "JOBI0100", "*", "", "CPF3C24")]
    [InlineData(129, "JOBI0100", "*", "", "CPF3C24")]
    [InlineData(86, "JOBI0200", "*", "", "CPF3C21")]
    [InlineData(86, "JOBI0100", "*INT", "", "CPF3C51")]
    [InlineData(86, "JOBI0100", "*", "not blank", "CPF3C59")]
    [InlineData(86, "JOBI0100", "INVALID", "", "CPF3C58")]
    public void Invalid_job_requests_report_errors_without_overwriting_information(int length, string format, string name, string id, string expected)
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var output = new Cell(); var error = Error();
        Api(job: job.Key).Invoke("QUSRJOBI", [output.Argument, length, format, name, id, error.Argument]);
        Assert.Equal(expected, error.Text(8, 7)); Assert.Equal(16, error.Int(4)); Assert.All(output.Bytes, b => Assert.Equal(0xAA, b));
    }
    private static Cell Error()
    {
        var cell = new Cell(20); var bytes = cell.Bytes; BinaryPrimitives.WriteInt32BigEndian(bytes, 16); cell.Argument.Value = new ProgramBuffer(bytes, 37); return cell;
    }
    [Fact]
    public void Reset_is_rejected_and_receiver_preparation_failure_preserves_every_output()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var receiver = new Cell(128, accept: value => value.ToArray()[0] != 0); var error = Error();
        Api().Invoke("QUSRJOBI", [receiver.Argument, 86, "JOBI0100", Qualified(job.Key), "", error.Argument]);
        Assert.Equal("CPF3C3A", error.Text(8, 7)); Assert.All(receiver.Bytes, b => Assert.Equal(0xAA, b));
        Api().Invoke("QUSRJOBI", [receiver.Argument, 86, "JOBI0100", Qualified(job.Key), "", error.Argument, "1"]);
        Assert.Equal("CPF3C3B", error.Text(8, 7)); Assert.All(receiver.Bytes, b => Assert.Equal(0xAA, b));
    }
    [Fact]
    public void Error_receiver_aliases_fail_without_writing_and_constants_are_not_receivers()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var output = new Cell(); var bytes = output.Bytes;
        BinaryPrimitives.WriteInt32BigEndian(bytes, 16); output.Argument.Value = new ProgramBuffer(bytes, 37);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => Api().Invoke("QUSRJOBI", [output.Argument, 86, "JOBI0100", Qualified(job.Key), "", output.Argument])).MessageId);
        Assert.Equal(bytes, output.Bytes);
        Assert.Equal("CPF3C90", Assert.Throws<CpfException>(() => Api().Invoke("QUSRJOBI", [output.Buffer, 86, "JOBI0100", Qualified(job.Key), ""])).MessageId);
    }
    [Fact]
    public void Unknown_and_ended_identifiers_do_not_resolve_to_a_different_job()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var id = Jobs.Get(job.Key).Identifier;
        Assert.Equal("CPF3C52", Assert.Throws<CpfException>(() => Get(job, name: "*INT", identifier: new ProgramBuffer(Enumerable.Repeat((byte)0xFE, 16).ToArray(), 37))).MessageId);
        _system.Jobs.Complete(job.Key); Assert.Equal("CPF3C52", Assert.Throws<CpfException>(() => Get(job, name: "*INT", identifier: new ProgramBuffer(id, 37))).MessageId);
        var later = _system.Jobs.CreateInteractive("QUSER"); Assert.NotEqual(id, Jobs.Get(later.Key).Identifier);
    }
    [Fact]
    public void Output_queue_status_returns_only_identity_attributes_and_library_lists_require_an_active_job()
    {
        var job = _system.Jobs.CreateInteractive("QUSER");
        using (var connection = _system.Connections.Open()) using (var update = connection.CreateCommand())
        { update.CommandText = "UPDATE sys_jobs SET status='OutputQueue',execution_state='Succeeded'"; update.ExecuteNonQuery(); }
        var output = Get(job); Assert.Equal("*OUTQ     ", output.Text(50, 10)); Assert.Equal(0, output.Int(64)); Assert.Equal(new string(' ', 10), output.Text(76, 10));
        Assert.Equal(1, Int(List(All(), "*OUTQ"), 132)); Assert.Equal("CPF3C54", Assert.Throws<CpfException>(() => Get(job, format: "JOBI0700")).MessageId);
    }
    [Theory]
    [InlineData(2147483646L, 2147483646)]
    [InlineData(2147483647L, -1)]
    [InlineData(2147483648L, -1)]
    public void Keyed_CPU_values_use_actual_counters_and_the_short_overflow_sentinel(long milliseconds, int shortValue)
    {
        var job = _system.Jobs.CreateInteractive("QUSER");
        using (var connection = _system.Connections.Open()) using (var update = connection.CreateCommand())
        { update.CommandText = "UPDATE sys_job_accounting SET cpu_nanoseconds=$cpu"; update.Parameters.AddWithValue("$cpu", milliseconds * 1_000_000); update.ExecuteNonQuery(); }
        var bytes = List(Qualified(job.Key), format: "JOBL0200", keys: [304, 312]); var start = Int(bytes, 124);
        Assert.Equal(shortValue, Int(bytes, start + 80)); Assert.Equal((ulong)milliseconds, BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(start + 100)));
    }
    [Theory]
    [InlineData("JOBL0999", "*ALL", "*", 0, 0, "CPF3C21")]
    [InlineData("JOBL0100", "*INVALID", "*", 0, 0, "CPF3CB1")]
    [InlineData("JOBL0100", "*ALL", "Z", 0, 0, "CPF1865")]
    [InlineData("JOBL0100", "*ALL", "*", 1, 302, "CPF1866")]
    [InlineData("JOBL0100", "*ALL", "*", 0, 1, "CPF1867")]
    [InlineData("JOBL0200", "*ALL", "*", 65, 302, "CPF1866")]
    [InlineData("JOBL0200", "*ALL", "*", 1, 9999, "CPF1867")]
    public void Invalid_list_requests_preserve_the_existing_user_space(string format, string status, string type, int count, int key, string expected)
    {
        Spaces.Create(Space, 512, 0xAA); var error = Error();
        Api().Invoke("QUSLJOB", ["JOBLIST   QGPL      ", format, All(), status, error.Argument, type, count, Keys([key])]);
        Assert.Equal(expected, error.Text(8, 7)); Assert.All(Spaces.Read(Space, 1, 512), b => Assert.Equal(0xAA, b));
    }
    [Theory]
    [InlineData("*ALL")]
    [InlineData("ABC**     *ALL      *ALL  ")]
    [InlineData("*         QUSER     000001")]
    [InlineData("*ALL      *ALL      000000")]
    public void Invalid_list_selection_never_rewrites_a_previous_list(string selection)
    {
        Spaces.Create(Space, 512, 0xAA);
        Assert.Equal("CPF3CB2", Assert.Throws<CpfException>(() => List(selection)).MessageId); Assert.All(Spaces.Read(Space, 1, 512), b => Assert.Equal(0xAA, b));
    }
    [Fact]
    public void Failed_list_update_and_cancelled_generation_roll_back_bytes_size_and_metadata()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); Spaces.Create(Space, 1, 0xFE); var before = _system.Objects.GetRequired("QGPL", "JOBLIST", ObjectType.UserSpace).Changed;
        using (var connection = _system.Connections.Open()) using (var fail = connection.CreateCommand())
        { fail.CommandText = "CREATE TRIGGER fail_native_list BEFORE UPDATE OF data ON sys_user_spaces BEGIN SELECT RAISE(ABORT,'injected native list write failure'); END"; fail.ExecuteNonQuery(); }
        Assert.Throws<SqliteException>(() => List(Qualified(job.Key))); Assert.Equal(1, Spaces.Inspect(Space).Size); Assert.Equal(new byte[] { 0xFE }, Spaces.Read(Space, 1, 1));
        Assert.Equal(before, _system.Objects.GetRequired("QGPL", "JOBLIST", ObjectType.UserSpace).Changed);
        using (var connection = _system.Connections.Open()) using (var drop = connection.CreateCommand()) { drop.CommandText = "DROP TRIGGER fail_native_list"; drop.ExecuteNonQuery(); }
        using var stop = new CancellationTokenSource();
        var api = new Ipc.Api.JobApi(_system.Connections, _ => ["QGPL"], cancellationToken: stop.Token, clock: () => { stop.Cancel(); return DateTimeOffset.Now; });
        Assert.ThrowsAny<OperationCanceledException>(() => api.Invoke("QUSLJOB", ["JOBLIST   QGPL      ", "JOBL0100", All(), "*ALL", Zero()]));
        Assert.Equal(1, Spaces.Inspect(Space).Size); Assert.Equal(new byte[] { 0xFE }, Spaces.Read(Space, 1, 1));
        Assert.Equal(before, _system.Objects.GetRequired("QGPL", "JOBLIST", ObjectType.UserSpace).Changed);
    }
    [Fact]
    public void Generic_list_producers_cannot_mutate_the_caller_area_even_by_alias()
    {
        Spaces.Create(Space, 512, 0xFE);
        Assert.Equal("IPC0136", Assert.Throws<CpfException>(() => Spaces.ReplaceList(Space, target => {
            target.UserArea[0] = 0; var result = new byte[192]; target.UserArea.CopyTo(result, 0); return result;
        })).MessageId);
        Assert.All(Spaces.Read(Space, 1, 512), b => Assert.Equal(0xFE, b));
    }
    [Fact]
    public void Real_maximum_size_partial_lists_continue_without_duplicates_and_reject_tampering()
    {
        using (var connection = _system.Connections.Open()) using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                WITH RECURSIVE numbers(n) AS (SELECT 1000 UNION ALL SELECT n+1 FROM numbers WHERE n<3999)
                INSERT INTO sys_jobs(number,name,user,type,status,user_profile,current_lib,libl,ccsid,execution_state,submitted_at,routing_data)
                SELECT n,'BULK'||printf('%06d',n),'QUSER','Batch','JobQueue','QUSER','QGPL','QGPL',37,'Queued','2024-01-23T04:05:06Z',printf('%-80s','DATA'||n) FROM numbers;
                """;
            seed.ExecuteNonQuery();
        }
        var keys = Enumerable.Repeat(1803, 64).ToArray(); var first = List(All("BULK*"), format: "JOBL0200", keys: keys);
        Assert.Equal("P", Text(first, 103, 1)); var firstCount = Int(first, 132); Assert.InRange(firstCount, 1, 2999);
        var handle = first.AsSpan(Int(first, 116) + 26, 48).ToArray(); var size = Spaces.Inspect(Space).Size;
        var before = SHA256.HashData(first); var tampered = handle.ToArray(); tampered[^1] ^= 1;
        Assert.Equal("CPF3CB2", Assert.Throws<CpfException>(() => Api().Invoke("QUSLJOB",
            ["JOBLIST   QGPL      ", "JOBL0200", All("BULK*"), "*ALL", Zero(), "*", 64, Keys(keys), new ProgramBuffer(tampered, 37)])).MessageId);
        Assert.Equal(before, SHA256.HashData(Spaces.Read(Space, 1, size)));
        using (var foreign = Ipc.Services.IpcSystem.Create(":memory:"))
        {
            foreign.Start(); var foreignSpaces = new UserSpaceStore(foreign.Connections); foreignSpaces.Create(Space, 1, 0xFE);
            var foreignApi = new Ipc.Api.JobApi(foreign.Connections, _ => ["QGPL"]);
            Assert.Equal("CPF3CB2", Assert.Throws<CpfException>(() => foreignApi.Invoke("QUSLJOB",
                ["JOBLIST   QGPL      ", "JOBL0200", All("BULK*"), "*ALL", Zero(), "*", 64, Keys(keys), new ProgramBuffer(handle, 37)])).MessageId);
            Assert.Equal(new byte[] { 0xFE }, foreignSpaces.Read(Space, 1, 1));
        }
        Assert.Equal("CPF3CB2", Assert.Throws<CpfException>(() => Api().Invoke("QUSLJOB",
            ["JOBLIST   QGPL      ", "JOBL0200", All("BULK*"), "*ACTIVE", Zero(), "*", 64, Keys(keys), new ProgramBuffer(handle, 37)])).MessageId);
        Api().Invoke("QUSLJOB", ["JOBLIST   QGPL      ", "JOBL0200", All("BULK*"), "*ALL", Zero(), "*", 64, Keys(keys), new ProgramBuffer(handle, 37)]);
        var second = Spaces.Read(Space, 1, Spaces.Inspect(Space).Size); Assert.Equal("C", Text(second, 103, 1)); Assert.Equal(3000, firstCount + Int(second, 132));
        var lastFirst = Text(first, Int(first, 124) + (firstCount - 1) * Int(first, 136) + 20, 6);
        var firstSecond = Text(second, Int(second, 124) + 20, 6); Assert.True(string.CompareOrdinal(firstSecond, lastFirst) > 0);
        Assert.Equal(new string(' ', 48), Text(second, Int(second, 116) + 26, 48)); Assert.Equal(size, second.Length);
        Jobs.BeginHostEpoch();
        Assert.Equal("CPF3CB2", Assert.Throws<CpfException>(() => Api().Invoke("QUSLJOB",
            ["JOBLIST   QGPL      ", "JOBL0200", All("BULK*"), "*ALL", Zero(), "*", 64, Keys(keys), new ProgramBuffer(handle, 37)])).MessageId);
        Assert.Equal(SHA256.HashData(second), SHA256.HashData(Spaces.Read(Space, 1, size)));
    }
}
