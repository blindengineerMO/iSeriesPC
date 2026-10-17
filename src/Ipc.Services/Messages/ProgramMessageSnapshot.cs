using Ipc.Core.Messages;

namespace Ipc.Services.Messages;

public sealed partial class MessageQueueStore
{
    /// <summary>One read snapshot for display output; leaves receive and exception state unchanged.</summary>
    public IReadOnlyList<QueuedMessage> SnapshotProgramMessages(MessageQueueAddress queue)
    {
        if (queue.ProgramQueue is null || queue.Named is not null) throw new CpfException("IPC0003", "A program message queue is required.");
        AuthorizeProgram(queue);
        using var connection = factory.Open();
        using var command = Command(connection, null, queue,
            "SELECT * FROM sys_message_entries WHERE program_queue=$program ORDER BY key LIMIT $limit");
        command.Parameters.AddWithValue("$limit", MaximumMessages + 1);
        using var reader = command.ExecuteReader(); var messages = new List<QueuedMessage>();
        while (reader.Read()) messages.Add(Visible(Read(reader).Message, connection));
        if (messages.Count > MaximumMessages) throw new CpfException("IPC0127", "Program message queue exceeds its display snapshot limit.");
        return messages;
    }
}
