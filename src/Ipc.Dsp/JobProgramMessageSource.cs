using System.Buffers.Binary;
using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Messages;
using Ipc.Services.Work;
using Ipc.Terminal;

namespace Ipc.Dsp;

/// <summary>Binds display output to the executing job's active call stack without receiving messages.</summary>
public sealed class JobProgramMessageSource(MessageQueueStore messages, JobCallEnvironment environment) : IProgramMessageSource
{
    private MessageQueueAddress Queue(string queue)
    {
        queue = queue.TrimEnd();
        if (queue is not ("*" or "*EXT") && !ObjectName.IsValid(queue))
            throw new CpfException("CPF2479", "Invalid display program message queue name.");
        return environment.MessageQueue(queue);
    }

    public DisplayMessage? Read(string queue, string key)
    {
        if (key.Length != 4 || key.Any(c => c > 255)) throw new CpfException("CPF2410", "Invalid four-byte message key.");
        var number = BinaryPrimitives.ReadUInt32BigEndian(Encoding.Latin1.GetBytes(key));
        if (number == 0) throw new CpfException("CPF2410", "A message reference key is required.");
        var found = messages.ListFrom(Queue(queue), after: number - 1, limit: 1).SingleOrDefault();
        return found?.Key == number ? Format(found) : null;
    }

    public IReadOnlyList<SubfileMessage> ReadAll(string queue) => messages.SnapshotProgramMessages(Queue(queue))
        .Select(message => new SubfileMessage(Encoding.Latin1.GetString(message.KeyBuffer(37).ToArray()), Format(message))).ToArray();

    private static DisplayMessage Format(QueuedMessage message)
    {
        var text = SafeText(message.Data.ToText());
        var second = message.Predefined is { } predefined
            ? SafeText(MessageDescriptionFormat.Format(predefined.Description, predefined.Replacement, message.Data.Ccsid).SecondLevel.ToText()) : "";
        return new(text, Details: new(message.MessageId.Length == 0 ? "Message details" : message.MessageId, text, second));
    }

    private static string SafeText(string text) => new(text.Select(c => TerminalGlyph.IsSingleCell(c) ? c : '?').ToArray());
}
