using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;

namespace Ipc.Api;

public sealed partial class JobApi(SqliteConnectionFactory factory, Func<string, IEnumerable<string>> searchLibraries,
    int ccsid = 37, JobKey? job = null, CancellationToken cancellationToken = default, Func<DateTimeOffset>? clock = null)
{
    private readonly ApiArguments _arguments = new(ccsid);
    private NativeJobStore Jobs => new(factory);
    public void Invoke(string name, IReadOnlyList<object?> p)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (name == "QUSRJOBI" ? p.Count is not (5 or 6 or 7) : name != "QUSLJOB" || p.Count is not (4 or 5 or 8 or 9))
            throw new CpfException("CPF3C36", "Invalid job API parameter count or incomplete group.");
        var errorIndex = name == "QUSRJOBI" ? p.Count >= 6 ? 5 : -1 : p.Count >= 5 ? 4 : -1;
        var error = errorIndex < 0 ? null : new ApiArguments.ErrorReceiver(_arguments, p[errorIndex]);
        if (error?.Target is { } target && p.Where((_, index) => index != errorIndex).Any(value => Ipc.Core.Work.ProgramArgument.Overlaps(value, target)))
            throw ApiArguments.Bad("API error receiver cannot alias another parameter.");
        try
        {
            if (name == "QUSRJOBI") Retrieve(p, error); else List(p, error);
        }
        catch (CpfException failure) when (error?.Report(failure) == true) { }
    }

    private void Retrieve(IReadOnlyList<object?> p, ApiArguments.ErrorReceiver? error)
    {
        var receiver = _arguments.CharacterOutput(p[0]); var length = _arguments.Binary32(p[1]);
        if (length < 8 || length > receiver.Buffer.Length) throw new CpfException("CPF3C24", "Invalid job information receiver length.");
        var format = _arguments.Text(p[2], 8);
        if (format is not ("JOBI0100" or "JOBI0700")) throw new CpfException("CPF3C21", "Unsupported job information format.");
        if (p.Count == 7 && _arguments.Text(p[6], 1) != "0") throw new CpfException("CPF3C3B", "Performance reset is not valid for this format.");
        var name = JobName(p[3]); var identifier = _arguments.Character(p[4], 16).ToArray().AsSpan(0, 16).ToArray();
        var blank = identifier.AsSpan().SequenceEqual(_arguments.Encode(new string(' ', 16)));
        NativeJobRecord found;
        if (name.Name == "*INT")
        {
            if (name.User.Length != 0 || name.Number.Length != 0) throw BadJobName();
            if (blank || identifier.All(b => b == 0)) throw new CpfException("CPF3C51", "Invalid internal job identifier.");
            found = Jobs.Get(identifier: identifier, includeLibraries: format == "JOBI0700");
        }
        else
        {
            if (!blank) throw new CpfException("CPF3C59", "Internal identifier must be blank unless job name is *INT.");
            var key = name.Name == "*" ? name.User.Length == 0 && name.Number.Length == 0 ? CurrentJob() : throw BadJobName()
                : Specific(name);
            found = Jobs.Get(key, includeLibraries: format == "JOBI0700");
        }
        var bytes = format == "JOBI0700" ? Libraries(found) : Basic(found);
        var returned = Math.Min(length, bytes.Length); Put(bytes, 0, returned); Put(bytes, 4, bytes.Length);
        var output = _arguments.Prefix(receiver, bytes, returned); var success = error?.Success();
        cancellationToken.ThrowIfCancellationRequested(); Apply(output, success);
    }
    private sealed record QualifiedJob(string Name, string User, string Number);
    private QualifiedJob JobName(object? value)
    {
        var bytes = _arguments.Character(value, 26).ToArray();
        string Part(int at, int size) => new ProgramBuffer(bytes.AsSpan(at, size), ccsid).ToText().TrimEnd(' ');
        return new(Part(0, 10), Part(10, 10), Part(20, 6));
    }
    private static JobKey Specific(QualifiedJob name)
    {
        if (!ObjectName.IsValid(name.Name) || !ObjectName.IsValid(name.User) || name.Number.Length != 6 || name.Number.Any(c => !char.IsAsciiDigit(c)) ||
            !int.TryParse(name.Number, out var number) || number == 0) throw BadJobName();
        return new(number, name.Name, name.User);
    }
    private JobKey CurrentJob() => job ?? OperationIdentity.Current?.Job ?? throw new CpfException("CPF3C53", "No executing job is available.");
    private static CpfException BadJobName() => new("CPF3C58", "Invalid qualified job name.");
    private byte[] Basic(NativeJobRecord row)
    {
        var bytes = new byte[86]; Header(bytes, 8, row); Text(bytes, 62, 2, "");
        var active = NativeJobStore.Status(row.Job) == "*ACTIVE";
        Put(bytes, 64, active ? row.Job.RunPriority : 0); Put(bytes, 68, active ? row.Job.TimeSliceMilliseconds : 0);
        Put(bytes, 72, 0); // Automatic allocations fail immediately; explicit waits are operation parameters.
        Text(bytes, 76, 10, active ? "*NO" : ""); // Interpreter storage is not purged at time-slice boundaries.
        return bytes;
    }
    private byte[] Libraries(NativeJobRecord row)
    {
        var existing = row.ExistingLibraries!;
        string[] Present(IEnumerable<string> names) => names.Where(existing.Contains).Distinct(StringComparer.Ordinal).ToArray();
        var system = Present(row.SystemLibraries!);
        var current = row.Job.CurrentLibrary is { Length: > 0 } name && ObjectName.IsValid(name) ? Present([name]) : [];
        var users = Present((row.Job.LibraryList is { } list && list != "*LIBL" ? list : "QGPL QUSRSYS").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries));
        var values = system.Concat(current).Concat(users).ToArray(); var bytes = new byte[80 + values.Length * 11];
        Header(bytes, 8, row); Text(bytes, 62, 2, ""); Put(bytes, 64, system.Length); Put(bytes, 68, 0); Put(bytes, 72, current.Length); Put(bytes, 76, users.Length);
        for (var n = 0; n < values.Length; n++) Text(bytes, 80 + n * 11, 11, values[n]);
        return bytes;
    }
    private void Header(byte[] bytes, int offset, NativeJobRecord row)
    {
        var key = row.Job.Key; var (type, subtype) = NativeJobStore.Type(row.Job.Type);
        Text(bytes, offset, 10, key.Name); Text(bytes, offset + 10, 10, key.User); Text(bytes, offset + 20, 6, key.Number.ToString("D6", CultureInfo.InvariantCulture));
        row.Identifier.CopyTo(bytes, offset + 26); Text(bytes, offset + 42, 10, NativeJobStore.Status(row.Job));
        Text(bytes, offset + 52, 1, type); Text(bytes, offset + 53, 1, subtype);
    }
    private void Text(byte[] bytes, int at, int width, string text)
    {
        var encoded = _arguments.Encode(text);
        if (encoded.Length > width) throw new CpfException("IPC0136", "Job data exceeds its native field width.");
        bytes.AsSpan(at, width).Fill(_arguments.Encode(" ")[0]); encoded.CopyTo(bytes, at);
    }
    private static void Put(byte[] bytes, int at, int value) => BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at), value);
    private static int Align(int value) => (value + 3) & ~3;
    private static void Apply(ApiArguments.Assignment? output, ApiArguments.Assignment? error) => ApiArguments.Apply(new[] { output, error }.OfType<ApiArguments.Assignment>());
}
