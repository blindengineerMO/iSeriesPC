using System.Buffers.Binary;
using System.Text.Json;
using Ipc.Terminal;

namespace Ipc.Session.Transport;

public sealed record TerminalFrame(int Version, int Rows, int Columns, int CursorRow, int CursorColumn,
    string Text, int[] Attributes, int[] Colors, bool Ended = false, string State = "SignOn")
{
    public static TerminalFrame Capture(DisplayBuffer buffer, bool ended = false, string state = "SignOn")
    {
        var positions = buffer.Positions().ToArray();
        return new(1, buffer.Rows, buffer.Columns, buffer.Cursor.Row, buffer.Cursor.Column,
            new string(positions.Select(p =>
                (buffer[p.Row, p.Column].Attributes & DisplayAttribute.NonDisplay) != 0
                    ? ' ' : buffer[p.Row, p.Column].Value).ToArray()),
            positions.Select(p => (int)buffer[p.Row, p.Column].Attributes).ToArray(),
            positions.Select(p => buffer[p.Row, p.Column].Foreground).ToArray(), ended, state);
    }

    public DisplayBuffer ToBuffer()
    {
        if (Text is null || Attributes is null || Colors is null ||
            Version != 1 || Rows is < 1 or > 100 || Columns is < 1 or > 200 ||
            Text.Length != Rows * Columns || Attributes.Length != Text.Length || Colors.Length != Text.Length ||
            CursorRow < 1 || CursorRow > Rows || CursorColumn < 1 || CursorColumn > Columns + 1)
            throw new InvalidDataException("Invalid terminal frame.");
        var buffer = new DisplayBuffer(Rows, Columns);
        for (var i = 0; i < Text.Length; i++)
            buffer.Set(i / Columns + 1, i % Columns + 1, Text[i], (DisplayAttribute)Attributes[i], Colors[i]);
        buffer.MoveCursor(CursorRow, Math.Min(CursorColumn, Columns));
        return buffer;
    }
}

public static class SessionWire
{
    public const int MaximumFrameBytes = 256 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("Session frame is too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken) == 0) return default;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("Invalid session frame length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty session frame.");
    }
}

public sealed record TerminalInput(int Version, KeyPress Key, CommandRequest? CommandSession = null);

public sealed record CommandRequest(string Operation, string? User = null, string? Password = null, string? Command = null,
    string? VerificationCode = null, string? Token = null, string? EnrollmentToken = null);

public sealed record CommandReply(bool Success, string? Error = null,
    Ipc.Core.Work.JobKey? Job = null, Ipc.Cl.Commands.CommandResult? Result = null,
    Ipc.Services.Security.SessionCredential? Session = null, bool MustVerifyMfa = false,
    Ipc.Services.Security.MfaEnrollment? Enrollment = null, Ipc.Services.Security.MfaConfirmation? Confirmation = null);
