using Ipc.Core.Objects;

namespace Ipc.Services.Work;

public sealed record JournalInfo(string Id, QualifiedName Name, QualifiedName Receiver, long Sequence, int AttachedFiles);
public sealed record JournalReceiverInfo(string Id, QualifiedName Name, string? JournalId, string? OriginJournal,
    DateTimeOffset? AttachedAt, DateTimeOffset? DetachedAt, long? FirstSequence, long? LastSequence, long Bytes, long? ThresholdKilobytes, bool Saved, bool Attached);
public sealed record JournalCell(string Name, string StorageType, string? Value);
public sealed record JournalColumn(string Name, string Type, int Hidden);
public sealed record JournalEntry(string JournalId, long Sequence, QualifiedName Receiver, int Version, string Code, string Type,
    DateTimeOffset Timestamp, string? Principal, string? Job, string? ObjectLibrary, string? ObjectName, string? Member,
    long? RecordNumber, string? Schema, IReadOnlyList<JournalCell>? Before, IReadOnlyList<JournalCell>? After, byte[]? Data, string? BindingId = null, string? CommitCycle = null);
