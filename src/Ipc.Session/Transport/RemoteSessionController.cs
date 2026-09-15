using System.Net.Sockets;
using Ipc.Console.Session;
using Ipc.Terminal;

namespace Ipc.Session.Transport;

public sealed class RemoteSessionController : ISessionController, IDisposable
{
    private readonly NetworkStream _stream;
    public DisplayBuffer Buffer { get; private set; }

    public RemoteSessionController(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(Path.GetFullPath(socketPath)));
            _stream = new NetworkStream(socket, ownsSocket: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var frame = SessionWire.ReadAsync<TerminalFrame>(_stream, timeout.Token).GetAwaiter().GetResult()
                ?? throw new IOException("Session server closed before sign-on.");
            Buffer = frame.ToBuffer();
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public SessionEvent Handle(KeyPress keyPress)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        SessionWire.WriteAsync(_stream, new TerminalInput(1, keyPress), timeout.Token).GetAwaiter().GetResult();
        var frame = SessionWire.ReadAsync<TerminalFrame>(_stream, timeout.Token).GetAwaiter().GetResult()
            ?? throw new IOException("Session server disconnected.");
        Buffer = frame.ToBuffer();
        return new SessionEvent(frame.State, EndSession: frame.Ended);
    }

    public void RequestDisconnect() => _stream.Dispose();

    public void Dispose() => _stream.Dispose();
}
