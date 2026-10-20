namespace Ipc.Core.Messages;

public enum IpcMessageType
{
    Informational,
    Completion,
    Diagnostic,
    Escape,
    Inquiry,
    Status,
}

public sealed class IpcMessage
{
    public required string MessageId { get; init; }

    public required string Text { get; init; }

    public IpcMessageType Type { get; init; } = IpcMessageType.Informational;

    public int Severity { get; init; }

    public string? From { get; init; }

    public DateTimeOffset SentAt { get; init; } = DateTimeOffset.UtcNow;

    public string? SenderJob { get; init; }

    public string? Reply { get; set; }

    public override string ToString() =>
        $"{MessageId} {(int)Type} {Text}";
}

public sealed class CpfException : Exception
{
    public string MessageId { get; }

    public int Severity { get; }

    public IpcMessageType MessageType { get; }

    public string? SqlState { get; }
    public int? SqlCode => SqlState is not null && MessageId.Length == 7 && MessageId.StartsWith("SQL", StringComparison.Ordinal) && int.TryParse(MessageId.AsSpan(3), out var code) ? -code : null;

    public CpfException(
        string messageId,
        string messageText,
        IpcMessageType type = IpcMessageType.Escape,
        int severity = 30,
        string? sqlState = null)
        : base($"{messageId}: {messageText}")
    {
        MessageId = messageId;
        MessageType = type;
        Severity = severity;
        SqlState = sqlState;
    }

    public static CpfException SystemError(string what) =>
        new("CPF0001", $"Error found addressing {what} request.");
}

public sealed class StatusException : Exception
{
    public string MessageId { get; }

    public StatusException(string messageId, string messageText)
        : base($"{messageId}: {messageText}")
    {
        MessageId = messageId;
    }
}