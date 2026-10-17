using System.Buffers.Binary;
using Ipc.Api;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class JobApiTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public JobApiTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "NativeJobs28");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "NativeJobUser28");
    }
    private NativeJobStore Jobs => new(_system.Connections);
    private UserSpaceStore Spaces => new(_system.Connections);
    private static readonly QualifiedName Space = new("QGPL", "JOBLIST");
    private static string Qualified(JobKey key) => key.Name.PadRight(10) + key.User.PadRight(10) + key.Number.ToString("D6");
    private static string All(string name = "*ALL", string user = "*ALL", string number = "*ALL") => name.PadRight(10) + user.PadRight(10) + number.PadRight(6);
    private static ProgramBuffer Zero(int ccsid = 37) => new(new byte[4], ccsid);
    private static ProgramBuffer Keys(int[] keys, int ccsid = 37)
    { var data = new byte[keys.Length * 4]; for (var n = 0; n < keys.Length; n++) BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(n * 4), keys[n]); return new(data, ccsid); }
    private JobApi Api(int ccsid = 37, JobKey? job = null, CancellationToken token = default) => new(_system.Connections,
        selector => _system.SearchLibraries(job is { } key ? _system.Jobs.GetRequired(key) : null, selector), ccsid, job, token,
        () => DateTimeOffset.Parse("2024-01-23T04:05:06Z"));
    private sealed class Cell
    {
        internal ProgramBuffer Buffer { get; private set; }
        internal ProgramArgument Argument { get; }
        internal byte[] Bytes => Buffer.ToArray();
        internal Cell(int size = 128, int ccsid = 37, Func<ProgramBuffer, bool>? accept = null)
        {
            Buffer = new(Enumerable.Repeat((byte)0xAA, size).ToArray(), ccsid);
            Argument = new(() => Buffer, value => Buffer = (ProgramBuffer)value!, value => value is ProgramBuffer buffer && buffer.Length == size && buffer.Ccsid == ccsid && (accept?.Invoke(buffer) ?? true)
                ? buffer : throw new CpfException("CPF3C3A", "Rejected receiver."), "*CHAR", size, buffer: () => Buffer);
        }
        internal int Int(int offset) => BinaryPrimitives.ReadInt32BigEndian(Bytes.AsSpan(offset));
        internal string Text(int offset, int length) => new ProgramBuffer(Bytes.AsSpan(offset, length), Buffer.Ccsid).ToText();
    }
    private Cell Get(Job target, int ccsid = 37, int length = 86, string format = "JOBI0100", object? name = null, object? identifier = null, JobKey? caller = null)
    {
        var output = new Cell(Math.Max(length, 128), ccsid);
        Api(ccsid, caller).Invoke("QUSRJOBI", [output.Argument, length, format, name ?? Qualified(target.Key), identifier ?? new string(' ', 16), Zero(ccsid)]);
        return output;
    }
    private byte[] List(string selector, string status = "*ALL", int ccsid = 37, string format = "JOBL0100", int[]? keys = null, JobKey? caller = null)
    {
        if (! _system.Objects.Exists(Space.Library, Space.Name.Value, ObjectType.UserSpace)) Spaces.Create(Space, 512, 0xAA);
        Api(ccsid, caller).Invoke("QUSLJOB", ["JOBLIST   QGPL      ", format, selector, status, Zero(ccsid), "*", keys?.Length ?? 0, keys is null || keys.Length == 0 ? Zero(ccsid) : Keys(keys, ccsid)]);
        return Spaces.Read(Space, 1, Spaces.Inspect(Space).Size);
    }
    private static int Int(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset));
    private static string Text(byte[] bytes, int at, int length, int ccsid = 37) => new ProgramBuffer(bytes.AsSpan(at, length), ccsid).ToText();

    [Theory]
    [InlineData(37, "D8C4C6E3D1D6C2404040D8E4E2C5D94040404040", "F0F0F0F0F0F1", "5CC1C3E3C9E5C5404040C940")]
    [InlineData(1208, "514446544A4F4220202051555345522020202020", "303030303031", "2A4143544956452020204920")]
    public void Basic_receiver_matches_independent_native_offsets_and_bytes(int ccsid, string names, string number, string statusAndType)
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); Assert.Equal(1, job.Key.Number); var identity = Jobs.Get(job.Key).Identifier;
        var output = Get(job, ccsid);
        Assert.Equal("0000005600000056" + names + number + Convert.ToHexString(identity) + statusAndType, Convert.ToHexString(output.Bytes.AsSpan(0, 62)));
        Assert.Equal(20, output.Int(64)); Assert.Equal(2000, output.Int(68)); Assert.Equal(0, output.Int(72)); Assert.Equal("*NO       ", output.Text(76, 10));
        Assert.All(output.Bytes[86..], b => Assert.Equal(0xAA, b));
    }
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(49)]
    [InlineData(62)]
    [InlineData(85)]
    public void Truncated_job_receivers_keep_full_availability_and_untouched_tails(int length)
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var output = Get(job, length: length);
        Assert.Equal(length, output.Int(0)); Assert.Equal(86, output.Int(4)); Assert.All(output.Bytes[length..], b => Assert.Equal(0xAA, b));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Current_explicit_and_opaque_internal_ID_selection_resolve_the_same_job(int ccsid)
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var first = Get(job, ccsid); var id = new ProgramBuffer(first.Bytes.AsSpan(34, 16), ccsid);
        Assert.Equal(first.Bytes, Get(job, ccsid, name: "*", caller: job.Key).Bytes);
        Assert.Equal(first.Bytes, Get(job, ccsid, name: "*INT", identifier: id).Bytes);
        using var connection = _system.Connections.Open(); using var update = connection.CreateCommand(); update.CommandText = "UPDATE sys_job_api_ids SET identifier=X'FF00FE0180000000FF00FE0180000000'"; update.ExecuteNonQuery();
        var raw = new ProgramBuffer(Convert.FromHexString("FF00FE0180000000FF00FE0180000000"), ccsid);
        Assert.Equal(raw.ToArray(), Get(job, ccsid, name: "*INT", identifier: raw).Bytes.AsSpan(34, 16).ToArray());
    }
    [Fact]
    public void Queued_attributes_are_blank_and_history_is_not_claimed_to_be_output_queue_work()
    {
        var job = _system.Jobs.Submit("QUEUED", "", "QBATCH", profile: "QUSER"); var output = Get(job);
        Assert.Equal("*JOBQ     ", output.Text(50, 10)); Assert.Equal(0, output.Int(64)); Assert.Equal(0, output.Int(68)); Assert.Equal(new string(' ', 10), output.Text(76, 10));
        _system.Jobs.RequestEnd(job.Key); Assert.Equal("CPF3C55", Assert.Throws<CpfException>(() => Get(job)).MessageId);
        Assert.Equal(0, Int(List(All(), "*OUTQ"), 132)); Assert.Equal(0, Int(List(All()), 132));
    }
    [Fact]
    public void Native_library_receipt_is_authorized_and_uses_logical_QTEMP_and_permanent_at_prefix_names()
    {
        _system.Libraries.CreateLibrary("@TEST"); var job = _system.Jobs.CreateInteractive("QUSER", libraryList: "QGPL QTEMP @TEST");
        using (OperationIdentity.Enter("QUSER", job.Key))
        {
            var output = Get(job, length: 1024, format: "JOBI0700");
            Assert.Equal(5, output.Int(64)); Assert.Equal(0, output.Int(68)); Assert.Equal(1, output.Int(72)); Assert.Equal(3, output.Int(76));
            Assert.Equal("QSYS       QSYS2      QUSRSYS    QHLPSYS    QSPL       QGPL       QGPL       QTEMP      @TEST      ", output.Text(80, 99));
            Assert.DoesNotContain("@T0", output.Text(80, 99));
        }
        _system.Security.Profiles.Create(new UserProfile { Name = "OTHER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("OTHER"), "OtherNativeJobs28");
        using (OperationIdentity.Enter("OTHER")) Assert.Equal("CPF3C57", Assert.Throws<CpfException>(() => Get(job, format: "JOBI0700")).MessageId);
        using (OperationIdentity.Enter("OTHER")) Assert.Equal("QUSER     ", Get(job).Text(18, 10));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Generated_list_has_a_native_generic_header_and_returns_the_same_internal_ID_as_retrieval(int ccsid)
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var bytes = List(Qualified(job.Key), ccsid: ccsid); var info = Get(job, ccsid);
        Assert.All(bytes[..64], b => Assert.Equal(0xAA, b)); Assert.Equal(128, Int(bytes, 64)); Assert.Equal("0100", Text(bytes, 68, 4, ccsid));
        Assert.Equal("JOBL0100QUSLJOB   1240123040506C", Text(bytes, 72, 32, ccsid));
        Assert.Equal(444, Int(bytes, 104)); Assert.Equal(192, Int(bytes, 108)); Assert.Equal(120, Int(bytes, 112)); Assert.Equal(312, Int(bytes, 116)); Assert.Equal(74, Int(bytes, 120));
        Assert.Equal(388, Int(bytes, 124)); Assert.Equal(56, Int(bytes, 128)); Assert.Equal(1, Int(bytes, 132)); Assert.Equal(56, Int(bytes, 136)); Assert.Equal(ccsid, Int(bytes, 140));
        Assert.Equal(info.Bytes.AsSpan(8, 54).ToArray(), bytes.AsSpan(388, 54).ToArray()); Assert.All(bytes[444..], b => Assert.Equal(0xAA, b));
    }
    [Fact]
    public void Listing_implicitly_grows_a_tiny_nonextendible_space_without_changing_its_attributes()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); Spaces.Create(Space, 1, 0xFE, automaticallyExtendible: false);
        var result = List(Qualified(job.Key)); Assert.Equal(444, result.Length); Assert.All(result[..64], b => Assert.Equal(0xFE, b));
        Assert.False(Spaces.Inspect(Space).AutomaticallyExtendible);
    }
    [Fact]
    public void Status_type_prefix_and_current_filters_do_not_treat_underscores_as_SQL_wildcards()
    {
        var first = _system.Jobs.Submit("ABC_ONE", "", "QBATCH", profile: "QUSER");
        _system.Jobs.Submit("ABCXONE", "", "QBATCH", profile: "QUSER"); var interactive = _system.Jobs.CreateInteractive("QUSER");
        var bytes = List(All("ABC_*"), "*JOBQ"); Assert.Equal(1, Int(bytes, 132)); Assert.Equal("ABC_ONE   ", Text(bytes, Int(bytes, 124), 10));
        Assert.Equal(2, Int(List(All(), "*JOBQ"), 132)); Assert.Equal(1, Int(List(All(), "*ACTIVE"), 132));
        bytes = List("*", caller: interactive.Key); Assert.Equal(1, Int(bytes, 132)); Assert.Equal(interactive.Key.Name.PadRight(10), Text(bytes, Int(bytes, 124), 10));
        Assert.Equal(1, Int(List(All("*CURRENT", "*CURRENT"), caller: first.Key), 132));
    }
    [Fact]
    public void Keyed_rows_have_checked_field_records_and_foreign_jobs_return_authority_status_without_values()
    {
        var own = _system.Jobs.CreateInteractive("QUSER"); _system.Jobs.CreateInteractive("QSECOFR");
        Spaces.Create(Space, 512, authority: "*CHANGE");
        using var identity = OperationIdentity.Enter("QUSER", own.Key);
        var bytes = List(All(), format: "JOBL0200", keys: [302, 1012, 1802]); var start = Int(bytes, 124); var size = Int(bytes, 136);
        Assert.Equal(2, Int(bytes, 132)); Assert.Equal(132, size); Assert.Equal(" ", Text(bytes, start + 56, 1)); Assert.Equal(3, Int(bytes, start + 60));
        Assert.Equal(20, Int(bytes, start + 64)); Assert.Equal(302, Int(bytes, start + 68)); Assert.Equal("B   ", Text(bytes, start + 72, 4)); Assert.Equal(4, Int(bytes, start + 76)); Assert.Equal(37, Int(bytes, start + 80));
        Assert.Equal(1012, Int(bytes, start + 88)); Assert.Equal("QUSER     ", Text(bytes, start + 100, 10));
        Assert.Equal("A", Text(bytes, start + size + 56, 1)); Assert.Equal(0, Int(bytes, start + size + 60)); Assert.All(bytes.AsSpan(start + size + 64, size - 64).ToArray(), b => Assert.Equal(0, b));
    }
    [Fact]
    public void Every_published_key_has_a_bounded_backed_field()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var bytes = List(Qualified(job.Key), format: "JOBL0200", keys: JobApi.SupportedKeys.ToArray());
        var offset = Int(bytes, 124) + 64; Assert.Equal(17, Int(bytes, Int(bytes, 124) + 60));
        foreach (var key in JobApi.SupportedKeys) { Assert.Equal(key, Int(bytes, offset + 4)); Assert.True(Int(bytes, offset + 12) > 0); offset += Int(bytes, offset); }
        Assert.Equal(Int(bytes, 104), offset);
    }
    [Theory]
    [InlineData("QUSRJOBI", 4)]
    [InlineData("QUSRJOBI", 8)]
    [InlineData("QUSLJOB", 3)]
    [InlineData("QUSLJOB", 6)]
    [InlineData("QUSLJOB", 7)]
    public void Incomplete_parameter_groups_fail_before_accessing_receivers(string name, int count)
        => Assert.Equal("CPF3C36", Assert.Throws<CpfException>(() => Api().Invoke(name, new object?[count])).MessageId);
    public void Dispose() => _system.Dispose();
}
