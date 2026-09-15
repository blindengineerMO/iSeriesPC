using Ipc.Core.Objects;

namespace Ipc.Services.Messages;

/// <summary>A named queue or an opaque job-owned call-frame queue, never a synthetic catalog object.</summary>
public sealed record MessageQueueAddress
{
    public QualifiedName? Named { get; }
    public string? ProgramQueue { get; }
    public MessageQueueAddress(QualifiedName named) => Named = named;
    internal MessageQueueAddress(string programQueue) => ProgramQueue = programQueue;
    public override string ToString() => Named?.ToString() ?? "*PGMQ";
}
