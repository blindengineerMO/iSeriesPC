using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Security;
using Ipc.Services.Work;

namespace Ipc.Api;

public sealed partial class JobApi
{
    private void List(IReadOnlyList<object?> p, ApiArguments.ErrorReceiver? error)
    {
        var qualified = _arguments.Character(p[0], 20).ToArray();
        string Part(int at) => new Ipc.Core.Work.ProgramBuffer(qualified.AsSpan(at, 10), ccsid).ToText().TrimEnd(' ');
        var spaceName = Part(0); var spaceLibrary = Part(10);
        if (!ObjectName.IsValid(spaceName) || !ObjectName.IsValid(spaceLibrary) && spaceLibrary is not ("*LIBL" or "*CURLIB")) throw ApiArguments.Bad("Invalid user-space name.");
        var space = ResolveSpace(spaceName, spaceLibrary);
        var format = _arguments.Text(p[1], 8);
        if (format is not ("JOBL0100" or "JOBL0200")) throw new CpfException("CPF3C21", "Unsupported job list format.");
        var specified = JobName(p[2]); var selected = Selection(specified);
        var status = _arguments.Text(p[3], 10);
        if (status is not ("*ALL" or "*ACTIVE" or "*JOBQ" or "*OUTQ")) throw new CpfException("CPF3CB1", "Invalid job status.");
        var type = p.Count >= 8 ? _arguments.Text(p[5], 1) : "*";
        if (type is not ("*" or "A" or "B" or "I" or "M" or "R" or "S" or "W" or "X")) throw new CpfException("CPF1865", "Invalid job type.");
        var count = p.Count >= 8 ? _arguments.Binary32(p[6]) : 0;
        if (count is < 0 or > 64 || format == "JOBL0100" && count != 0) throw new CpfException("CPF1866", "Invalid number of job fields; this runtime supports at most 64.");
        var keys = new int[count];
        if (count > 0)
        {
            var bytes = _arguments.Character(p[7], count * 4).ToArray();
            for (var n = 0; n < count; n++)
            {
                keys[n] = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(n * 4));
                if (!Fields.ContainsKey(keys[n])) throw new CpfException("CPF1867", "Unsupported job field key.");
            }
        }
        else if (p.Count >= 8 && format == "JOBL0100" && _arguments.Binary32(p[7]) != 0)
            throw new CpfException("CPF1867", "JOBL0100 key parameter must be binary zero.");
        var inputHandle = p.Count == 9 ? _arguments.Character(p[8], 48).ToArray().AsSpan(0, 48).ToArray() : _arguments.Encode(new string(' ', 48));
        var queryHash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', selected.Name, selected.User, selected.Number, status, type, format, string.Join(',', keys))));
        var success = error?.Success();
        new UserSpaceStore(factory).ReplaceList(space, target =>
        {
            var secret = Jobs.CursorSecret(); var after = ReadCursor(inputHandle, secret, queryHash);
            var entrySize = format == "JOBL0100" ? 56 : 64 + keys.Sum(key => Align(16 + Fields[key].Width));
            var inputSize = 120 + count * 4; var headerOffset = 192 + inputSize; var listOffset = Align(headerOffset + 74);
            using var entries = new MemoryStream(); var total = 0; var last = after; var partial = false;
            foreach (var row in Jobs.List(selected.Name, selected.User, selected.Number is "*ALL" ? null : int.Parse(selected.Number, System.Globalization.CultureInfo.InvariantCulture),
                         status, type, after, cancellationToken))
            {
                if (listOffset + entries.Length + entrySize > target.MaximumSize) { partial = true; break; }
                var entry = new byte[entrySize]; Header(entry, 0, row); Text(entry, 54, 2, "");
                if (format == "JOBL0200")
                {
                    var accessible = true;
                    try { new ServiceAuthorization(factory).RequireJob(row.Job.Key); }
                    catch (CpfException failure) when (failure.MessageId == "CPF9802") { accessible = false; }
                    Text(entry, 56, 1, accessible ? "" : "A"); Text(entry, 57, 3, ""); Put(entry, 60, accessible ? count : 0);
                    if (accessible)
                    {
                        var offset = 64;
                        foreach (var key in keys)
                        {
                            var field = Fields[key]; var size = Align(16 + field.Width); Put(entry, offset, size); Put(entry, offset + 4, key);
                            Text(entry, offset + 8, 1, field.Type); Text(entry, offset + 9, 3, ""); Put(entry, offset + 12, field.Width);
                            FieldData(key, row).CopyTo(entry, offset + 16); offset += size;
                        }
                    }
                }
                entries.Write(entry); total++; last = row.Job.Key.Number;
            }
            if (partial && total == 0) throw new CpfException("CPF3C14", "User space cannot hold a job list entry.");
            var outputHandle = partial ? Cursor(last, secret, queryHash) : _arguments.Encode(new string(' ', 48));
            var result = new byte[listOffset + entries.Length]; target.UserArea.CopyTo(result, 0);
            Put(result, 64, 128); Text(result, 68, 4, "0100"); Text(result, 72, 8, format); Text(result, 80, 10, "QUSLJOB");
            Text(result, 90, 13, Timestamp(clock?.Invoke() ?? DateTimeOffset.Now)); Text(result, 103, 1, partial ? "P" : "C");
            Put(result, 104, result.Length); Put(result, 108, 192); Put(result, 112, inputSize); Put(result, 116, headerOffset); Put(result, 120, 74);
            Put(result, 124, listOffset); Put(result, 128, (int)entries.Length); Put(result, 132, total); Put(result, 136, entrySize); Put(result, 140, ccsid);
            Text(result, 144, 2, ""); Text(result, 146, 3, ""); Text(result, 149, 1, "0"); Text(result, 150, 42, "");
            Text(result, 192, 10, specified.Name); Text(result, 202, 10, specified.User); Text(result, 212, 6, specified.Number); Text(result, 218, 10, status);
            Text(result, 228, 10, spaceName); Text(result, 238, 10, spaceLibrary); Text(result, 248, 8, format); Text(result, 256, 1, type); Text(result, 257, 3, ""); Put(result, 260, count);
            for (var n = 0; n < count; n++) Put(result, 264 + n * 4, keys[n]); inputHandle.CopyTo(result, 264 + count * 4);
            Text(result, headerOffset, 10, selected.Name); Text(result, headerOffset + 10, 10, selected.User); Text(result, headerOffset + 20, 6, selected.Number);
            outputHandle.CopyTo(result, headerOffset + 26); entries.ToArray().CopyTo(result, listOffset);
            cancellationToken.ThrowIfCancellationRequested(); return result;
        }, cancellationToken);
        Apply(null, success);
    }
    private QualifiedName ResolveSpace(string name, string library)
    {
        var objects = new Ipc.Services.Sqlite.SqliteObjectStore(factory);
        foreach (var candidate in library is "*LIBL" or "*CURLIB" ? searchLibraries(library) : [library])
            if (objects.Get(candidate, name, ObjectType.UserSpace) is { }) return new(candidate, name);
        throw new CpfException("CPF9801", "User space was not found.");
    }
    private QualifiedJob Selection(QualifiedJob input)
    {
        bool Pattern(string value) => value == "*ALL" || ObjectName.IsValid(value.EndsWith('*') ? value[..^1] : value);
        if (input.Name == "*")
        {
            if (input.User.Length != 0 || input.Number.Length != 0) throw new CpfException("CPF3CB2", "Current-job selector requires a blank user and number.");
            var current = CurrentJob(); return new(current.Name, current.User, current.Number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
        }
        var name = input.Name == "*CURRENT" ? CurrentJob().Name : input.Name;
        var user = input.User == "*CURRENT" ? CurrentJob().User : input.User;
        if (!Pattern(name) || !Pattern(user) || input.Number != "*ALL" && (input.Number.Length != 6 || input.Number.Any(c => !char.IsAsciiDigit(c)) || input.Number == "000000"))
            throw new CpfException("CPF3CB2", "Invalid qualified job list selection.");
        return new(name, user, input.Number);
    }
    private int ReadCursor(byte[] handle, byte[] secret, byte[] query)
    {
        if (handle.AsSpan().SequenceEqual(_arguments.Encode(new string(' ', 48)))) return 0;
        var after = BinaryPrimitives.ReadInt32BigEndian(handle.AsSpan(4));
        if (!handle.AsSpan(0, 4).SequenceEqual("IPJ1"u8) || after < 1 || !CryptographicOperations.FixedTimeEquals(handle.AsSpan(8, 8), query.AsSpan(0, 8)) ||
            !CryptographicOperations.FixedTimeEquals(handle.AsSpan(16), Signature(handle.AsSpan(0, 16), secret, query)))
            throw new CpfException("CPF3CB2", "Invalid continuation handle or mismatched job selection.");
        return after;
    }
    private static byte[] Cursor(int after, byte[] secret, byte[] query)
    {
        var handle = new byte[48]; "IPJ1"u8.CopyTo(handle); Put(handle, 4, after); query.AsSpan(0, 8).CopyTo(handle.AsSpan(8));
        Signature(handle.AsSpan(0, 16), secret, query).CopyTo(handle, 16); return handle;
    }
    private static byte[] Signature(ReadOnlySpan<byte> header, byte[] secret, byte[] query)
    {
        var data = new byte[48]; header.CopyTo(data); query.CopyTo(data, 16); return HMACSHA256.HashData(secret, data);
    }
    private static string Timestamp(DateTimeOffset time) => (time.Year / 100 - 19).ToString(System.Globalization.CultureInfo.InvariantCulture) + time.ToString("yyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
}
