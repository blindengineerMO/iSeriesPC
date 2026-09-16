using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;

namespace Ipc.Services.Messages;

public sealed partial class MessageQueueStore
{
    public void MarkExceptionHandled(ProgramMessageReference reference)
    {
        var queue = new MessageQueueAddress(reference.Queue); AuthorizeProgram(queue);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        AuthorizeProgram(queue);
        using var command = Command(connection, transaction, queue,
            "UPDATE sys_message_entries SET exception_handled=1 WHERE program_queue=$program AND key=$key AND kind IN ('ESCAPE','NOTIFY')");
        command.Parameters.AddWithValue("$key", (long)reference.Key);
        if (command.ExecuteNonQuery() != 1) throw new CpfException("CPF2410", "Program exception was not found.");
        transaction.Commit();
    }

    public ProgramMessageReference DeliverException(MessageQueueAddress destination, ProgramBuffer data, string messageId,
        ProgramMessageReference? existing = null, CancellationToken cancellationToken = default)
    {
        if (destination.ProgramQueue is null || data.Length > 4096 ||
            !System.Text.RegularExpressions.Regex.IsMatch(messageId, "\\A[A-Z][A-Z0-9]{2}[0-9A-F]{4}\\z"))
            throw Invalid("An exception requires a program queue, message ID and at most 4096 bytes.");
        cancellationToken.ThrowIfCancellationRequested(); AuthorizeProgram(destination);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        AuthorizeProgram(destination); cancellationToken.ThrowIfCancellationRequested();
        var severity = 40;
        QueuedMessage? origin = null;
        if (existing is not null)
        {
            using var find = Command(connection, transaction, destination, """
                SELECT e.* FROM sys_message_entries e JOIN sys_program_message_queues q ON q.id=e.program_queue
                WHERE e.key=$key AND q.id=$previous AND e.kind='ESCAPE'
                AND q.job_number=(SELECT job_number FROM sys_program_message_queues WHERE id=$program)
                """);
            find.Parameters.AddWithValue("$key", (long)existing.Key); find.Parameters.AddWithValue("$previous", existing.Queue);
            Stored original;
            using (var reader = find.ExecuteReader())
            {
                if (!reader.Read()) throw new CpfException("CPF2410", "Original program exception was removed or belongs to another job.");
                original = Read(reader);
            }
            if (existing.Queue == destination.ProgramQueue) { transaction.Commit(); return existing; }
            data = original.Message.Data; messageId = original.Message.MessageId; severity = original.Message.Severity;
            origin = original.Message;
        }
        var key = Insert(connection, transaction, destination, data, "ESCAPE", messageId, severity, null,
            new ProgramBuffer(Array.Empty<byte>(), data.Ccsid), null, origin: origin);
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return new(destination.ProgramQueue, key);
    }
}
