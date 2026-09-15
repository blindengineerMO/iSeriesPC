using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Messages;

public enum MessageClear { ByKey, All, KeepUnanswered, Old, New }

public sealed partial class MessageQueueStore
{
    public int RemoveInactiveJobMessages(JobKey job, CancellationToken cancellationToken = default)
    {
        var ownership = new Ipc.Services.Work.JobDataAreaStore(factory);
        cancellationToken.ThrowIfCancellationRequested(); ownership.RequireOwner(job);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        ownership.RequireOwner(job);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT e.* FROM sys_message_entries e JOIN sys_program_message_queues q ON q.id=e.program_queue
            WHERE q.job_number=$job AND q.active=0 ORDER BY e.key LIMIT 4097
            """;
        command.Parameters.AddWithValue("$job", job.Number);
        var entries = new List<(MessageQueueAddress Queue, Stored Entry)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) entries.Add((new MessageQueueAddress(reader.GetString(reader.GetOrdinal("program_queue"))), Read(reader)));
        if (entries.Count > MaximumMessages) throw new CpfException("CPF2460", "Message removal exceeds job capacity.");
        foreach (var (queue, entry) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Message.Kind == "INQ" && !entry.Message.Replied) ReplyCore(connection, transaction, queue, entry, entry.DefaultReply, entry.DefaultReplyCode);
        }
        foreach (var (queue, entry) in entries)
        { cancellationToken.ThrowIfCancellationRequested(); DeleteMessage(connection, transaction, queue, entry.Message); }
        command.CommandText = """
            DELETE FROM sys_program_message_queues AS q WHERE q.job_number=$job AND q.active=0 AND q.external=0
            AND NOT EXISTS(SELECT 1 FROM sys_message_entries e WHERE e.program_queue=q.id)
            AND NOT EXISTS(SELECT 1 FROM sys_program_message_queues child WHERE child.parent=q.id)
            """;
        // Remove empty leaves, then their parents; call depth is bounded to 64.
        for (var depth = 0; depth < 64; depth++)
        { cancellationToken.ThrowIfCancellationRequested(); if (command.ExecuteNonQuery() == 0) break; }
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return entries.Count;
    }

    public int Remove(QualifiedName queue, MessageClear clear = MessageClear.ByKey, uint key = 0,
        CancellationToken cancellationToken = default) => RemoveFrom(new(queue), clear, key, cancellationToken);

    public int RemoveFrom(MessageQueueAddress queue, MessageClear clear = MessageClear.ByKey, uint key = 0,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(clear) || (clear == MessageClear.ByKey ? key == 0 : key != 0))
            throw Invalid("Specify a nonzero message key only with CLEAR(*BYKEY).");
        if (clear == MessageClear.KeepUnanswered && queue.Named is null)
            throw Invalid("CLEAR(*KEEPUNANS) requires a named queue.");
        var required = AuthorityBit.ObjectOperate | AuthorityBit.Delete;
        cancellationToken.ThrowIfCancellationRequested(); Authorize(queue, required);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        cancellationToken.ThrowIfCancellationRequested(); Authorize(queue, required);
        var predicate = clear switch
        {
            MessageClear.ByKey => "AND key=$key", MessageClear.Old => "AND seen=1",
            MessageClear.New => "AND seen=0", MessageClear.KeepUnanswered => "AND NOT(kind='INQ' AND replied=0)", _ => ""
        };
        // Snapshot before default replies are inserted. A reply routed to this same
        // queue must survive this removal operation and remain receivable.
        using var select = Command(connection, transaction, queue,
            "SELECT * FROM sys_message_entries WHERE queue_lib IS $lib AND queue_name IS $name AND program_queue IS $program " + predicate + " ORDER BY key LIMIT 4097");
        select.Parameters.AddWithValue("$key", (long)key);
        var entries = new List<Stored>();
        using (var reader = select.ExecuteReader()) while (reader.Read()) entries.Add(Read(reader));
        if (entries.Count > MaximumMessages) throw new CpfException("CPF2460", "Message removal exceeds queue capacity.");
        if (clear == MessageClear.ByKey && entries.Count == 0) throw new CpfException("CPF2410", "Message key was not found.");
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Message.Kind == "INQ" && !entry.Message.Replied)
                ReplyCore(connection, transaction, queue, entry, entry.DefaultReply, entry.DefaultReplyCode);
        }
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteMessage(connection, transaction, queue, entry.Message);
        }
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return entries.Count;
    }

    private static void DeleteMessage(SqliteConnection connection, SqliteTransaction transaction, MessageQueueAddress queue, QueuedMessage message)
    {
        using var delete = Command(connection, transaction, queue,
            "DELETE FROM sys_message_entries WHERE queue_lib IS $lib AND queue_name IS $name AND program_queue IS $program AND key=$key");
        delete.Parameters.AddWithValue("$key", (long)message.Key); delete.ExecuteNonQuery();
        if (message.Kind == "RPY" && message.CorrelationKey is { } copyKey)
        {
            delete.CommandText = "DELETE FROM sys_message_entries WHERE queue_lib IS $lib AND queue_name IS $name AND program_queue IS $program AND kind='COPY' AND key=$copy";
            delete.Parameters.AddWithValue("$copy", (long)copyKey); delete.ExecuteNonQuery();
        }
        else if (message.Kind == "COPY")
        {
            delete.CommandText = "DELETE FROM sys_message_entries WHERE queue_lib IS $lib AND queue_name IS $name AND program_queue IS $program AND kind='RPY' AND correlation_key=$key";
            delete.ExecuteNonQuery();
        }
    }
}
