using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;

namespace Ipc.Api;

/// <summary>Checked local STD data-queue program interfaces. Receivers are borrowed for one synchronous call.</summary>
public sealed class DataQueueApi(SqliteConnectionFactory factory, Func<string, IEnumerable<string>> searchLibraries,
    int ccsid, CancellationToken cancellationToken = default)
{
    private readonly ApiArguments _arguments = new(ccsid);
    private readonly DataQueueStore _store = new(factory);

    public void Invoke(string program, IReadOnlyList<object?> parameters)
    {
        var valid = program switch {
            "QSNDDTAQ" => parameters.Count is 4 or 6 or 7 or 8,
            "QRCVDTAQ" => parameters.Count is 5 or 10 or 13 or 14,
            "QCLRDTAQ" => parameters.Count is 2 or 6,
            _ => false };
        if (!valid) throw new CpfException("CPF3C36", "Unsupported data-queue API parameter count or incomplete optional group.");
        var errorIndex = program == "QRCVDTAQ" && parameters.Count >= 13 ? 12 : program == "QCLRDTAQ" && parameters.Count == 6 ? 5 : -1;
        var errors = errorIndex < 0 ? null : new ApiArguments.ErrorReceiver(_arguments, parameters[errorIndex]);
        // An error receiver must not overwrite another argument when reporting a failure.
        if (errors?.Target is { } errorTarget && parameters.Where((_, index) => index != errorIndex).Any(value => ReferenceEquals(value, errorTarget)))
            throw ApiArguments.Bad("API error receiver cannot alias another parameter.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queue = Resolve(parameters[0], parameters[1]);
            switch (program)
            {
                case "QSNDDTAQ": Send(queue, parameters); break;
                case "QRCVDTAQ": Receive(queue, parameters, errors); break;
                case "QCLRDTAQ": Clear(queue, parameters); break;
            }
            if (errors?.Success() is { } success) ApiArguments.Apply([success]);
        }
        catch (CpfException error) when (errors?.Report(error) == true) { }
    }

    private QualifiedName Resolve(object? nameValue, object? libraryValue)
    {
        var name = _arguments.Text(nameValue, 10); var library = _arguments.Text(libraryValue, 10);
        if (!ObjectName.IsValid(name) || name != name.ToUpperInvariant() ||
            library is not ("*LIBL" or "*CURLIB") && (!ObjectName.IsValid(library) || library != library.ToUpperInvariant()))
            throw ApiArguments.Bad("API requires uppercase queue and library names.");
        using (var connection = factory.Open())
        {
            foreach (var candidate in library is "*LIBL" or "*CURLIB" ? searchLibraries(library) : [library])
            {
                var resolved = new JobTemporaryLibrary(factory).ResolveLibrary(candidate);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1 FROM sys_objects WHERE lib=$lib AND name=$name AND type='*DTAQ'";
                command.Parameters.AddWithValue("$lib", resolved); command.Parameters.AddWithValue("$name", name);
                if (command.ExecuteScalar() is not null) return new(candidate, name);
            }
            throw new CpfException("CPF9801", "Data queue not found.");
        }
    }

    private ProgramBuffer Prefix(object? value, int length)
    {
        if (length < 1) throw ApiArguments.Bad("Invalid API data or key length.");
        var buffer = _arguments.Character(value, length);
        return new(buffer.ToArray().AsSpan(0, length), ccsid);
    }
    private static DataQueueKeyOrder Order(string value) => value switch {
        "EQ" => DataQueueKeyOrder.Equal, "NE" => DataQueueKeyOrder.NotEqual,
        "GT" => DataQueueKeyOrder.Greater, "GE" => DataQueueKeyOrder.GreaterOrEqual,
        "LT" => DataQueueKeyOrder.Less, "LE" => DataQueueKeyOrder.LessOrEqual,
        _ => throw ApiArguments.Bad("Unsupported API key order.") };
    private void Send(QualifiedName queue, IReadOnlyList<object?> values)
    {
        var length = _arguments.Packed(values[2], 5);
        if (length is < 1 or > DataQueueStore.MaximumEntryLength) throw ApiArguments.Bad("Invalid API data length.");
        var data = Prefix(values[3], length);
        var keyLength = values.Count >= 6 ? _arguments.Packed(values[4], 3) : 0;
        if (keyLength is < 0 or > 256) throw ApiArguments.Bad("Invalid API key length.");
        var key = keyLength == 0 ? null : Prefix(values[5], keyLength);
        if (values.Count >= 7 && _arguments.Text(values[6], 10) != "*NO")
            throw ApiArguments.Bad("Asynchronous DDM data-queue requests are unsupported.");
        if (values.Count == 8 && _arguments.Text(values[7], 10) != "*NO")
            throw ApiArguments.Bad("Journal-entry data-queue sends are unsupported.");
        _store.Send(queue, data, key, cancellationToken);
    }
    private void Clear(QualifiedName queue, IReadOnlyList<object?> values)
    {
        var length = values.Count == 6 ? _arguments.Packed(values[3], 3) : 0;
        if (length is < 0 or > 256) throw ApiArguments.Bad("Invalid API key length.");
        var key = length == 0 ? null : Prefix(values[4], length);
        var order = length == 0 ? (DataQueueKeyOrder?)null : Order(_arguments.Text(values[2], 2));
        _store.Clear(queue, key, order, cancellationToken);
    }
    private void Receive(QualifiedName queue, IReadOnlyList<object?> values, ApiArguments.ErrorReceiver? errors)
    {
        var length = _arguments.PackedOutput(values[2], 5);
        var data = _arguments.CharacterOutput(values[3]);
        var wait = _arguments.Packed(values[4], 5);
        var keyLength = values.Count >= 10 ? _arguments.Packed(values[6], 3) : 0;
        if (keyLength is < 0 or > 256) throw ApiArguments.Bad("Invalid API key length.");
        var key = keyLength == 0 ? null : Prefix(values[7], keyLength);
        var keyOutput = keyLength == 0 ? null : _arguments.CharacterOutput(values[7]);
        var order = keyLength == 0 ? (DataQueueKeyOrder?)null : Order(_arguments.Text(values[5], 2));
        var senderLength = values.Count >= 10 ? _arguments.Packed(values[8], 3) : 0;
        if (senderLength != 0 && senderLength is < 8 or > 999) throw ApiArguments.Bad("Invalid API sender receiver length.");
        var sender = senderLength == 0 ? null : _arguments.CharacterOutput(values[9]);
        if (sender is not null && sender.Buffer.Length < senderLength) throw ApiArguments.Bad("API sender receiver is too small.");
        var remove = values.Count >= 13 ? _arguments.Text(values[10], 10) switch {
            "*YES" => true, "*NO" => false, _ => throw ApiArguments.Bad("Invalid API remove option.") } : true;
        var size = values.Count >= 13 ? _arguments.Packed(values[11], 5) : data.Buffer.Length;
        if (size < 0 || size > data.Buffer.Length)
            throw ApiArguments.Bad("Invalid API data receiver size.");
        var units = values.Count == 14 ? _arguments.Text(values[13], 10) : "*SEC";
        if (units is not ("*SEC" or "*MSEC")) throw ApiArguments.Bad("Invalid API wait units.");
        var outputs = new List<ProgramArgument> { length, data.Target };
        if (keyOutput is not null) outputs.Add(keyOutput.Target);
        if (sender is not null) outputs.Add(sender.Target);
        if (errors?.Target is { } errorTarget) outputs.Add(errorTarget);
        ApiArguments.DistinctOutputs(outputs);
        var writes = new List<ApiArguments.Assignment> { _arguments.PackedAssignment(length, 5, 0) };
        _store.Receive(queue, key, order,
            wait < 0 ? Timeout.InfiniteTimeSpan : units == "*MSEC" ? TimeSpan.FromMilliseconds(wait) : TimeSpan.FromSeconds(wait),
            remove, cancellationToken, values.Count >= 13 ? DataQueueStore.MaximumEntryLength : Math.Min(size, DataQueueStore.MaximumEntryLength),
            keyOutput is null ? 256 : Math.Min(keyOutput.Buffer.Length, 256), entry => {
                writes = [_arguments.PackedAssignment(length, 5, entry.Data.Length),
                    _arguments.Prefix(data, entry.Data.ToArray(), Math.Min(size, entry.Data.Length))];
                if (keyOutput is not null) writes.Add(_arguments.Prefix(keyOutput, entry.Key.ToArray(), keyLength));
                if (sender is not null)
                {
                    var bytes = Sender(entry.Sender, senderLength);
                    writes.Add(_arguments.Prefix(sender, bytes, Math.Min(senderLength, bytes.Length)));
                }
            });
        ApiArguments.Apply(writes);
    }
    private byte[] Sender(DataQueueSender? sender, int requested)
    {
        var available = sender is null ? 8 : 44;
        var bytes = Enumerable.Repeat(_arguments.Encode(" ")[0], available).ToArray();
        _arguments.PackedBuffer(7, Math.Min(available, requested)).ToArray().CopyTo(bytes, 0);
        _arguments.PackedBuffer(7, available).ToArray().CopyTo(bytes, 4);
        if (sender is not null)
        {
            void Field(string text, int offset, int width)
            {
                var encoded = _arguments.Encode(text);
                if (encoded.Length > width) throw ApiArguments.Bad("Sender identity exceeds its native API field width.");
                encoded.CopyTo(bytes, offset);
            }
            if (sender.Job is { } job)
            {
                if (job.Number is < 0 or > 999999) throw ApiArguments.Bad("Sender job number exceeds the native API field width.");
                Field(job.Name, 8, 10); Field(job.User, 18, 10);
                Field(job.Number.ToString("D6", CultureInfo.InvariantCulture), 28, 6);
            }
            Field(sender.User, 34, 10);
        }
        return bytes;
    }
}
