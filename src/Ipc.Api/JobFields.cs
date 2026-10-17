using System.Buffers.Binary;
using System.Globalization;
using Ipc.Services.Work;

namespace Ipc.Api;

public sealed partial class JobApi
{
    private sealed record Field(string Type, int Width);
    private static readonly IReadOnlyDictionary<int, Field> Fields = new Dictionary<int, Field> {
        [302] = new("B", 4), [304] = new("B", 4), [305] = new("C", 10), [312] = new("B", 8),
        [401] = new("C", 13), [402] = new("C", 13), [409] = new("B", 4), [412] = new("B", 4), [418] = new("C", 13),
        [1003] = new("C", 20), [1004] = new("C", 20), [1012] = new("C", 10), [1604] = new("C", 10),
        [1802] = new("B", 4), [1803] = new("C", 80), [1906] = new("C", 20), [2002] = new("B", 4) };
    public static IReadOnlyList<int> SupportedKeys => Fields.Keys.Order().ToArray();
    private byte[] FieldData(int key, NativeJobRecord row)
    {
        var field = Fields[key]; var bytes = new byte[field.Width]; var value = row.Job; var active = NativeJobStore.Status(value) == "*ACTIVE";
        var cpu = Math.Max(0, row.CpuNanoseconds / 1_000_000);
        string Qualified(string? text, string defaultLibrary = "QSYS") => text is not { Length: > 0 } ? "" :
            (text.Contains('/') ? text[(text.IndexOf('/') + 1)..] : text).PadRight(10) + (text.Contains('/') ? text[..text.IndexOf('/')] : defaultLibrary).PadRight(10);
        string Date(DateTimeOffset? time) => time is { } stamp ? Timestamp(TimeZoneInfo.ConvertTime(stamp, TimeZoneInfo.Local)) : "";
        if (field.Type == "B")
        {
            if (key == 312) BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)(active ? cpu : 0));
            else Put(bytes, 0, key switch { 302 or 412 => value.Ccsid, 304 => active ? cpu < int.MaxValue ? (int)cpu : -1 : 0,
                1802 => active ? value.RunPriority : 0, 2002 => active ? value.TimeSliceMilliseconds : 0, _ => 0 });
        }
        else Text(bytes, 0, field.Width, key switch {
            305 => active ? value.UserProfile ?? value.Key.User : "", 401 => Date(value.StartedAt), 402 => Date(value.SubmittedAt), 418 => Date(value.CompletedAt),
            1003 => Qualified(value.JobDescription), 1004 => Qualified(value.JobQueue), 1012 => value.Key.User, 1604 => active ? "*NO" : "",
            1803 => value.RoutingData ?? "", 1906 => active ? Qualified(value.Subsystem) : "", _ => "" });
        return bytes;
    }
}
