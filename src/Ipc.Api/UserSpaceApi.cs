using System.Buffers.Binary;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;

namespace Ipc.Api;

/// <summary>Native byte layouts for the implemented user-space APIs. No host pointer is exposed.</summary>
public sealed class UserSpaceApi(SqliteConnectionFactory factory, Func<string, IEnumerable<string>> searchLibraries,
    int ccsid = 37, CancellationToken cancellationToken = default)
{
    private readonly ApiArguments _arguments = new(ccsid);
    private readonly UserSpaceStore _spaces = new(factory);

    public void Invoke(string api, IReadOnlyList<object?> parameters)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errorIndex = api switch {
            "QUSCRTUS" when parameters.Count is 6 or 8 or 9 or 11 or 12 => parameters.Count >= 8 ? 7 : -1,
            "QUSRTVUS" when parameters.Count is 4 or 5 => parameters.Count == 5 ? 4 : -1,
            "QUSCHGUS" when parameters.Count is 5 or 6 or 7 => parameters.Count >= 6 ? 5 : -1,
            "QUSRUSAT" when parameters.Count == 5 => 4,
            "QUSCUSAT" when parameters.Count == 4 => 3,
            "QUSDLTUS" when parameters.Count == 2 => 1,
            _ => throw new CpfException("CPF3C36", "Invalid user-space API or parameter count.")
        };
        var error = errorIndex < 0 ? null : new ApiArguments.ErrorReceiver(_arguments, parameters[errorIndex]);
        try
        {
            var outputs = new List<ProgramArgument>(); if (error?.Target is { } errorTarget) outputs.Add(errorTarget);
            var success = error?.Success();
            switch (api)
            {
                case "QUSCRTUS":
                {
                    var space = Resolve(parameters[0], true);
                    var attribute = _arguments.Text(parameters[1], 10); var size = _arguments.Binary32(parameters[2]);
                    var initial = _arguments.Character(parameters[3], 1).ToArray()[0]; var authority = _arguments.Text(parameters[4], 10);
                    var text = _arguments.Text(parameters[5], 50);
                    var replace = parameters.Count >= 8 ? _arguments.Text(parameters[6], 10) switch { "*YES" => true, "*NO" => false, _ => throw ApiArguments.Bad("Invalid replace option.") } : false;
                    var domain = parameters.Count >= 9 ? _arguments.Text(parameters[8], 10) : "*DEFAULT";
                    var transfer = parameters.Count >= 11 ? _arguments.Binary32(parameters[9]) : 0;
                    var alignment = parameters.Count >= 11 ? Flag(parameters[10]) : true;
                    var extend = parameters.Count >= 12 && Flag(parameters[11]);
                    ApiArguments.DistinctOutputs(outputs);
                    _spaces.Create(space, size, initial, attribute, text, authority, replace, domain, transfer, alignment, extend, ccsid, cancellationToken);
                    break;
                }
                case "QUSRTVUS":
                {
                    var space = Resolve(parameters[0]); var start = _arguments.Binary32(parameters[1]); var length = _arguments.Binary32(parameters[2]);
                    var receiver = _arguments.CharacterOutput(parameters[3]); outputs.Add(receiver.Target); ApiArguments.DistinctOutputs(outputs);
                    if (length < 1) throw new CpfException("CPF3C12", "Length must be positive.");
                    if (length > receiver.Buffer.Length) throw new CpfException("CPF3C19", "User-space receiver is too small.");
                    var data = _spaces.Read(space, start, length, cancellationToken);
                    var received = _arguments.Prefix(receiver, data, data.Length);
                    ApiArguments.Apply(new[] { received }); break;
                }
                case "QUSCHGUS":
                {
                    var space = Resolve(parameters[0]); var start = _arguments.Binary32(parameters[1]); var length = _arguments.Binary32(parameters[2]);
                    var data = _arguments.Character(parameters[3]); var force = _arguments.Text(parameters[4], 1);
                    if (force is not ("0" or "1" or "2")) throw new CpfException("CPF3C13", "Invalid force option.");
                    if (length < 1) throw new CpfException("CPF3C12", "Length must be positive.");
                    if (length > data.Length) throw new CpfException("CPF3C15", "Input data is shorter than the requested length.");
                    ApiArguments.Assignment? library = null;
                    if (parameters.Count == 7)
                    {
                        var receiver = _arguments.CharacterOutput(parameters[6]); outputs.Add(receiver.Target);
                        library = _arguments.Prefix(receiver, PaddedLibrary(space.Library), 10);
                    }
                    ApiArguments.DistinctOutputs(outputs);
                    _spaces.Write(space, start, data.ToArray().AsSpan(0, length), cancellationToken, force == "2");
                    if (library is not null) ApiArguments.Apply(new[] { library }); break;
                }
                case "QUSRUSAT":
                {
                    var receiver = _arguments.CharacterOutput(parameters[0]); outputs.Add(receiver.Target); ApiArguments.DistinctOutputs(outputs);
                    var length = _arguments.Binary32(parameters[1]);
                    if (length < 8 || length > receiver.Buffer.Length) throw new CpfException("CPF3C24", "Invalid attribute receiver length.");
                    if (_arguments.Text(parameters[2], 8) != "SPCA0100") throw new CpfException("CPF3C21", "Unsupported user-space attribute format.");
                    var space = Resolve(parameters[3]); var attributes = _spaces.Inspect(space, cancellationToken);
                    var bytes = new byte[24]; BinaryPrimitives.WriteInt32BigEndian(bytes, Math.Min(length, bytes.Length));
                    BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), bytes.Length); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), attributes.Size);
                    bytes[12] = _arguments.Encode(attributes.AutomaticallyExtendible ? "1" : "0")[0]; bytes[13] = attributes.InitialValue;
                    PaddedLibrary(space.Library).CopyTo(bytes, 14);
                    var received = _arguments.Prefix(receiver, bytes, Math.Min(length, bytes.Length)); ApiArguments.Apply(new[] { received }); break;
                }
                case "QUSCUSAT":
                {
                    var receiver = _arguments.CharacterOutput(parameters[0]); outputs.Add(receiver.Target); ApiArguments.DistinctOutputs(outputs);
                    var space = Resolve(parameters[1]); var library = _arguments.Prefix(receiver, PaddedLibrary(space.Library), 10);
                    var bytes = _arguments.Character(parameters[2], 4).ToArray(); var count = BinaryPrimitives.ReadInt32BigEndian(bytes);
                    if (count is < 1 or > 256) throw new CpfException("CPF3C4B", "Invalid attribute record count.");
                    int? size = null, transfer = null; byte? initial = null; bool? extend = null; var offset = 4;
                    for (var index = 0; index < count; index++)
                    {
                        if (offset > bytes.Length - 8) throw new CpfException("CPF3C4D", "Truncated attribute record.");
                        var key = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset)); var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 4));
                        var width = key is 1 or 4 ? 4 : key is 2 or 3 ? 1 : throw new CpfException("CPF3C4B", "Unsupported user-space attribute key.");
                        if (length < width || length > bytes.Length - offset - 8) throw new CpfException("CPF3C4D", "Invalid attribute data length.");
                        var data = bytes.AsSpan(offset + 8, length);
                        switch (key)
                        {
                            case 1:
                                size = BinaryPrimitives.ReadInt32BigEndian(data);
                                if (size is < 1 or > UserSpaceStore.MaximumLength) throw new CpfException("CPF3C4B", "Invalid space size.");
                                break;
                            case 2: initial = data[0]; break;
                            case 3: extend = Flag(new ProgramBuffer(data[..1], ccsid)); break;
                            case 4:
                                transfer = BinaryPrimitives.ReadInt32BigEndian(data);
                                if (transfer is < 0 or > 32) throw new CpfException("CPF3C4B", "Invalid transfer size.");
                                break;
                        }
                        var next = (long)offset + 8 + length;
                        offset = checked((int)((next + 3) & ~3L));
                    }
                    _spaces.ChangeAttributes(space, size, initial, extend, transfer, cancellationToken); ApiArguments.Apply(new[] { library }); break;
                }
                case "QUSDLTUS":
                {
                    var bytes = _arguments.Character(parameters[0], 20).ToArray();
                    var name = new ProgramBuffer(bytes.AsSpan(0, 10), ccsid).ToText().TrimEnd(' ');
                    var library = new ProgramBuffer(bytes.AsSpan(10, 10), ccsid).ToText().TrimEnd(' ');
                    ApiArguments.DistinctOutputs(outputs);
                    _spaces.Delete(name, library, searchLibraries, cancellationToken); break;
                }
            }
            if (success is not null) ApiArguments.Apply(new[] { success });
        }
        catch (CpfException failure) when (error?.Report(failure) == true) { }
    }
    private bool Flag(object? value) => _arguments.Text(value, 1) switch { "0" => false, "1" => true, _ => throw ApiArguments.Bad("Expected character 0 or 1.") };
    private byte[] PaddedLibrary(string library)
    {
        var bytes = _arguments.Encode(library.PadRight(10)); if (bytes.Length != 10) throw ApiArguments.Bad("Library name does not fit native storage."); return bytes;
    }
    private QualifiedName Resolve(object? parameter, bool create = false)
    {
        var bytes = _arguments.Character(parameter, 20).ToArray();
        var name = new ProgramBuffer(bytes.AsSpan(0, 10), ccsid).ToText().TrimEnd(' ').ToUpperInvariant();
        var library = new ProgramBuffer(bytes.AsSpan(10, 10), ccsid).ToText().TrimEnd(' ').ToUpperInvariant();
        if (!ObjectName.IsValid(name) || library != "*CURLIB" && !(library == "*LIBL" && !create) && !ObjectName.IsValid(library)) throw ApiArguments.Bad("Invalid qualified user-space name.");
        if (library is "*CURLIB" or "*LIBL")
        {
            var candidates = searchLibraries(library).ToArray();
            library = create || library == "*CURLIB" ? candidates.FirstOrDefault() ?? "QGPL" : candidates.FirstOrDefault(candidate =>
                Exists(candidate, name)) ?? throw new CpfException("CPF9801", "User space not found in the job library list.");
        }
        var temporary = new JobTemporaryLibrary(factory);
        return temporary.Visible(temporary.Resolve(new(library, name), ObjectType.UserSpace), ObjectType.UserSpace);
    }
    private bool Exists(string library, string name)
    {
        library = new JobTemporaryLibrary(factory).ResolveLibrary(library);
        using var connection = factory.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT 1 FROM sys_objects WHERE lib=$lib AND name=$name AND type='*USRSPC'";
        query.Parameters.AddWithValue("$lib", library); query.Parameters.AddWithValue("$name", name);
        return query.ExecuteScalar() is not null;
    }
}
