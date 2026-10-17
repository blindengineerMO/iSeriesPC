using System.Buffers.Binary;
using Ipc.Cl.Definitions;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;

namespace Ipc.Api;

/// <summary>QWCRDTAA's fixed header and typed value, with no host pointers.</summary>
public sealed class DataAreaApi(SqliteConnectionFactory factory, Func<string, IEnumerable<string>> searchLibraries,
    int ccsid = 37, JobKey? job = null, CancellationToken cancellationToken = default)
{
    private readonly ApiArguments _arguments = new(ccsid);

    public void Retrieve(IReadOnlyList<object?> parameters)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parameters.Count != 6) throw new CpfException("CPF3C36", "QWCRDTAA requires six parameters.");
        var error = new ApiArguments.ErrorReceiver(_arguments, parameters[5]);
        try
        {
            var receiver = _arguments.CharacterOutput(parameters[0]);
            ApiArguments.DistinctOutputs(error.Target is { } target ? [receiver.Target, target] : [receiver.Target]);
            var receiverLength = _arguments.Binary32(parameters[1]);
            if (receiverLength < 8 || receiverLength > receiver.Buffer.Length) throw new CpfException("CPF3C24", "Invalid data-area receiver length.");
            var qualified = _arguments.Character(parameters[2], 20).ToArray();
            var name = new ProgramBuffer(qualified.AsSpan(0, 10), ccsid).ToText().TrimEnd(' ').ToUpperInvariant();
            var library = new ProgramBuffer(qualified.AsSpan(10, 10), ccsid).ToText().TrimEnd(' ').ToUpperInvariant();
            var start = _arguments.Binary32(parameters[3]); var length = _arguments.Binary32(parameters[4]);
            if (start != -1 && start is < 1 or > 2000) throw new CpfException("CPF1088", "Starting position is outside the data area.");
            if (length < 1) throw new CpfException("CPF1863", "Data-area length must be positive.");
            var (area, returnedLibrary) = Read(name, library);
            if (start != -1 && start > area.Length) throw new CpfException("CPF1088", "Starting position is outside the data area.");
            if (start != -1 && (area.Type != "*CHAR" || length > area.Length - start + 1))
                throw new CpfException("CPF1089", "Invalid data-area substring.");
            byte[] data; int valueLength;
            if (area.Value is ProgramBuffer buffer)
            {
                var selected = start == -1 ? buffer : new ProgramBuffer(buffer.ToArray().AsSpan(start - 1, length), buffer.Ccsid);
                // Same-CCSID data is opaque. Cross-CCSID text conversion is strict,
                // including when a byte selection splits a multibyte sequence.
                try { data = selected.Ccsid == ccsid ? selected.ToArray() : _arguments.Encode(selected.ToText()); }
                catch (CpfException) { throw new CpfException("CPF3C19", "Data-area text cannot be represented in the receiver CCSID."); }
                valueLength = data.Length;
            }
            else if (area.Type == "*DEC")
            {
                data = CommandArgumentCodec.Encode(new ParameterDefinition { Keyword = "VALUE", Type = "*DEC", Length = area.Length, Decimals = area.Decimals }, area.Value, ccsid);
                valueLength = area.Length; // Decimal precision, not packed storage width.
            }
            else { data = _arguments.Encode((bool)area.Value ? "1" : "0"); valueLength = 1; }
            var available = 36 + data.Length; var returned = Math.Min(receiverLength, available);
            var bytes = receiver.Buffer.ToArray(); var header = new byte[available];
            // QWCRDTAA uses available/returned order, unlike SPCA0100.
            BinaryPrimitives.WriteInt32BigEndian(header, available); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), returned);
            _arguments.Encode(area.Type.PadRight(10)).CopyTo(header, 8);
            _arguments.Encode(returnedLibrary.PadRight(10)).CopyTo(header, 18);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(28), valueLength);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(32), area.Decimals); data.CopyTo(header, 36);
            header.AsSpan(0, returned).CopyTo(bytes);
            if (start == -1 && length > data.Length && receiverLength > available)
                bytes.AsSpan(available, Math.Min(receiverLength - available, length - data.Length)).Fill(_arguments.Encode(" ")[0]);
            var received = new ApiArguments.Assignment(receiver.Target, receiver.Target.Normalize(new ProgramBuffer(bytes, ccsid)));
            var success = error.Success(); cancellationToken.ThrowIfCancellationRequested();
            ApiArguments.Apply(success is null ? [received] : [received, success]);
        }
        catch (CpfException failure) when (error.Report(failure)) { }
    }

    private (DataAreaValue Area, string Library) Read(string name, string library)
    {
        if (name is "*LDA" or "*GDA" or "*PDA")
        {
            if (library.Length != 0) throw ApiArguments.Bad("Job data-area library must be blank.");
            var owner = job ?? OperationIdentity.Current?.Job ?? throw new CpfException("CPF1241", "A job data area requires an executing job.");
            var data = new JobDataAreaStore(factory).Read(owner, name switch { "*LDA" => JobDataArea.Local, "*GDA" => JobDataArea.Group, _ => JobDataArea.Initialization });
            using var connection = factory.Open(); using var query = connection.CreateCommand();
            query.CommandText = "SELECT ccsid FROM sys_jobs WHERE number=$job"; query.Parameters.AddWithValue("$job", owner.Number);
            var areaCcsid = Convert.ToInt32(query.ExecuteScalar());
            return (new("*CHAR", data.Length, 0, areaCcsid, new ProgramBuffer(data, areaCcsid)), "");
        }
        if (!ObjectName.IsValid(name) || !ObjectName.IsValid(library) && library is not ("*LIBL" or "*CURLIB"))
            throw ApiArguments.Bad("Invalid qualified data-area name.");
        var temporary = new JobTemporaryLibrary(factory);
        foreach (var candidate in library is "*LIBL" or "*CURLIB" ? searchLibraries(library) : [library])
        {
            cancellationToken.ThrowIfCancellationRequested(); var resolved = temporary.ResolveLibrary(candidate);
            using var connection = factory.Open(); using var query = connection.CreateCommand();
            query.CommandText = "SELECT 1 FROM sys_objects WHERE lib='QSYS' AND name=$library AND type='*LIB'";
            query.Parameters.AddWithValue("$library", resolved);
            if (query.ExecuteScalar() is null) throw new CpfException("CPF1021", "Data-area library not found.");
            try { new ServiceAuthorization(factory).RequireObject("QSYS", resolved, ObjectType.Library, AuthorityBit.ObjectOperate, checkLibrary: false); }
            catch (CpfException failure) when (failure.MessageId is "CPF9802" or "CPF1002")
            { throw new CpfException(failure.MessageId == "CPF9802" ? "CPF1022" : "CPF1067", "Data-area library is unavailable."); }
            query.CommandText = "SELECT 1 FROM sys_objects WHERE lib=$library AND name=$name AND type='*DTAARA'";
            query.Parameters.AddWithValue("$name", name); if (query.ExecuteScalar() is null) continue;
            try
            {
                var area = new DataAreaStore(factory).Read(resolved, name);
                return (area, temporary.Visible(new(resolved, name), ObjectType.DataArea).Library);
            }
            catch (CpfException failure) when (failure.MessageId is "CPF9801" or "CPF9802" or "CPF1002")
            { throw new CpfException(failure.MessageId == "CPF9802" ? "CPF1016" : failure.MessageId == "CPF1002" ? "CPF1063" : "CPF1015", "Data area is unavailable."); }
        }
        throw new CpfException("CPF1015", "Data area not found.");
    }
}
