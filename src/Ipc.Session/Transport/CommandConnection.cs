using System.Net.Sockets;
using Ipc.Core.Work;

namespace Ipc.Session.Transport;

/// <summary>Headless client for web/API/bridge callers; it never opens the catalog.</summary>
public sealed class CommandConnection : IAsyncDisposable
{
    private readonly NetworkStream _stream;
    public JobKey Job { get; }
    private CommandConnection(NetworkStream stream, JobKey job) { _stream = stream; Job = job; }

    public static async Task<CommandConnection> ConnectAsync(string socketPath, string user, string password,
        CancellationToken cancellationToken = default, string? verificationCode = null) =>
        await ConnectCoreAsync(socketPath, new CommandRequest("Authenticate", user, password, VerificationCode: verificationCode), cancellationToken);

    public static Task<CommandConnection> ConnectWithTokenAsync(string socketPath, string token, CancellationToken cancellationToken = default) =>
        ConnectCoreAsync(socketPath, new CommandRequest("AuthenticateToken", Token: token), cancellationToken);

    public static async Task<CommandReply> AuthenticationRequestAsync(string socketPath, CommandRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Operation is not ("IssueToken" or "RevokeToken" or "BeginMfa" or "ConfirmMfa" or "DisableMfa"))
            throw new ArgumentException("Not an authentication management request.", nameof(request));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(Path.GetFullPath(socketPath)), cancellationToken);
        using var stream = new NetworkStream(socket);
        _ = await SessionWire.ReadAsync<TerminalFrame>(stream, cancellationToken) ?? throw new IOException("Server ended before greeting.");
        await SessionWire.WriteAsync(stream, new TerminalInput(1, default, request), cancellationToken);
        return await SessionWire.ReadAsync<CommandReply>(stream, cancellationToken) ?? throw new IOException("Server ended before authentication reply.");
    }

    private static async Task<CommandConnection> ConnectCoreAsync(string socketPath, CommandRequest request, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(Path.GetFullPath(socketPath)), cancellationToken);
            var stream = new NetworkStream(socket, ownsSocket: true);
            _ = await SessionWire.ReadAsync<TerminalFrame>(stream, cancellationToken)
                ?? throw new IOException("Server ended before greeting.");
            await SessionWire.WriteAsync(stream, new TerminalInput(1, default,
                request), cancellationToken);
            var reply = await SessionWire.ReadAsync<CommandReply>(stream, cancellationToken)
                ?? throw new IOException("Server ended before authentication.");
            if (!reply.Success || reply.Job is null) throw new UnauthorizedAccessException(reply.Error);
            return new CommandConnection(stream, reply.Job.Value);
        }
        catch { socket.Dispose(); throw; }
    }

    // Requests on a connection are serialized by its caller; separate clients use separate jobs.
    public async Task<CommandReply> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        await SessionWire.WriteAsync(_stream, new CommandRequest("Execute", Command: command), cancellationToken);
        return await SessionWire.ReadAsync<CommandReply>(_stream, cancellationToken)
            ?? throw new IOException("Execution server disconnected.");
    }

    public async Task SignoffAsync(CancellationToken cancellationToken = default)
    {
        await SessionWire.WriteAsync(_stream, new CommandRequest("Signoff"), cancellationToken);
        _ = await SessionWire.ReadAsync<CommandReply>(_stream, cancellationToken)
            ?? throw new IOException("Server ended before signoff acknowledgement.");
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
