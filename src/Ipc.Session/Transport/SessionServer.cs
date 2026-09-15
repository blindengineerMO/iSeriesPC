using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using Ipc.Console.Session;
using Ipc.Services;
using Ipc.Core.Work;

namespace Ipc.Session.Transport;

/// <summary>One catalog owner serving independent authenticated terminal sessions.</summary>
public sealed class SessionServer
{
    private readonly string _dataDirectory;
    private readonly string _databaseFile;
    private readonly string _socketPath;
    private readonly ConcurrentDictionary<long, Task> _sessions = new();
    private readonly SemaphoreSlim _capacity;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextSession;

    public SessionServer(string dataDirectory, string? socketPath = null, string databaseFile = "system.db", int maxSessions = 64)
    {
        if (maxSessions < 1) throw new ArgumentOutOfRangeException(nameof(maxSessions));
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _databaseFile = databaseFile;
        _socketPath = Path.GetFullPath(socketPath ?? Path.Combine(_dataDirectory, "run", "as400.sock"));
        _capacity = new SemaphoreSlim(maxSessions, maxSessions);
    }

    public string SocketPath => _socketPath;
    public Task Ready => _ready.Task;
    public int SessionCount => _sessions.Count;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linkedStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var catalogPath = Path.GetFullPath(Path.Combine(_dataDirectory, _databaseFile));
            Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
            using var owner = new FileStream(catalogPath + ".host.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var socketDirectory = Path.GetDirectoryName(_socketPath)!;
            if (!Directory.Exists(socketDirectory))
            {
                Directory.CreateDirectory(socketDirectory);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(socketDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(socketDirectory) &
                (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new IOException("Session socket directory must not be writable by other accounts.");
            using var endpointOwner = new FileStream(_socketPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // Stale sockets may be removed only after both ownership locks are held.
            if (File.Exists(_socketPath)) File.Delete(_socketPath);
            using var system = IpcSystem.Create(_dataDirectory, _databaseFile);
            system.Start();
            var groupAccess = system.Config.Authentication.AllowGroupSocketAccess;
            if (groupAccess && !OperatingSystem.IsWindows())
            {
                var publicBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(_dataDirectory) & publicBits) != 0)
                    throw new IOException("Group socket access requires a private (0700) catalog directory and a separate group-accessible socket directory.");
                if (_socketPath.StartsWith(_dataDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new IOException("The shared socket must be outside the private catalog directory.");
            }
            system.Jobs.RecoverInterruptedInteractiveJobs();
            new Ipc.Services.Work.BatchQueue(system.Connections, system.Jobs).RecoverInterrupted();
            new Ipc.Services.Work.JobLockStore(system.Connections).RecoverEndedJobs();
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    (groupAccess ? UnixFileMode.GroupRead | UnixFileMode.GroupWrite : 0));
            listener.Listen(64);
            var dispatcher = new BatchDispatcher(system).RunAsync(linkedStop.Token);
            if (dispatcher.IsCompleted) await dispatcher;
            _ = dispatcher.ContinueWith(failed => linkedStop.Cancel(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _ready.TrySetResult();
            try
            {
                while (!linkedStop.IsCancellationRequested)
                {
                    await _capacity.WaitAsync(linkedStop.Token);
                    Socket socket;
                    try { socket = await listener.AcceptAsync(linkedStop.Token); }
                    catch { _capacity.Release(); throw; }
                    var id = Interlocked.Increment(ref _nextSession);
                    var session = ServeAsync(system, socket, linkedStop.Token);
                    _sessions[id] = session;
                    _ = session.ContinueWith(completed =>
                    {
                        _sessions.TryRemove(id, out _);
                        _capacity.Release();
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
            catch (OperationCanceledException) when (linkedStop.IsCancellationRequested) { }
            finally
            {
                linkedStop.Cancel();
                listener.Close();
                await Task.WhenAll(_sessions.Values);
                await dispatcher;
                File.Delete(_socketPath);
            }
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            throw;
        }
    }

    private static async Task ServeAsync(IpcSystem system, Socket socket, CancellationToken cancellationToken)
    {
        using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = connectionStop.Token;
        using var stream = new NetworkStream(socket, ownsSocket: true);
        var disconnected = MonitorDisconnectAsync(socket, connectionStop);
        ISessionController? controller = null;
        var signedOff = false;
        CancellationTokenRegistration jobCancellation = default;
        try
        {
            controller = new SignOnController(system, cancellationToken: cancellationToken, unixAccount: UnixPeerIdentity.Account(socket));
            await SessionWire.WriteAsync(stream, TerminalFrame.Capture(controller.Buffer), cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var input = await SessionWire.ReadAsync<TerminalInput>(stream, cancellationToken);
                if (input is null) break;
                if (input.Version != 1 || !Enum.IsDefined(input.Key.Aid) ||
                    (input.Key.Edit is { } edit && !Enum.IsDefined(edit)))
                    throw new InvalidDataException("Unsupported session input.");
                if (input.CommandSession is { } request)
                {
                    if (controller is not SignOnController { State: SignOnState.SignOn })
                        throw new InvalidDataException("Cannot change session protocol after sign-on.");
                    await ServeCommandsAsync(system, stream, request, cancellationToken);
                    break;
                }
                var result = controller.Handle(input.Key);
                if (result.Next is { } next)
                {
                    controller = next;
                    if (next is MenuController activeMenu) jobCancellation = activeMenu.CancellationToken.Register(() => connectionStop.Cancel());
                }
                await SessionWire.WriteAsync(stream,
                    TerminalFrame.Capture(controller.Buffer, result.EndSession, result.State), cancellationToken);
                if (result.EndSession) { signedOff = result.State != "Failed"; break; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException)
        {
            system.Log.Error($"Terminal connection ended: {ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            // A failed session must not terminate the server or expose data in exception text.
            system.Log.Error($"Terminal session failed: {ex.GetType().Name}");
        }
        finally
        {
            jobCancellation.Dispose();
            connectionStop.Cancel();
            await disconnected;
            if (controller is MenuController menu)
                menu.EndSession(signedOff ? JobCompletion.Normal : JobCompletion.Abnormal,
                    signedOff ? "Interactive session signed off." : "Interactive session disconnected or was interrupted.");
            else if (controller is IDisposable disposable) disposable.Dispose();
        }
    }

    private static async Task MonitorDisconnectAsync(Socket socket, CancellationTokenSource stop)
    {
        try
        {
            var probe = new byte[1];
            while (!stop.IsCancellationRequested)
            {
                // Poll+Available races with the protocol reader consuming the last byte.
                // A zero-byte peek is an actual orderly disconnect, not an empty buffer.
                if (await socket.ReceiveAsync(probe.AsMemory(), SocketFlags.Peek, stop.Token) == 0)
                {
                    stop.Cancel();
                    return;
                }
                await Task.Delay(50, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { stop.Cancel(); }
    }

    private static async Task ServeCommandsAsync(IpcSystem system, Stream stream, CommandRequest request,
        CancellationToken cancellationToken)
    {
        if (request.User?.Length > 10 || request.Password?.Length > 128 || request.VerificationCode?.Length > 32 ||
            request.Token?.Length > 43 || request.EnrollmentToken?.Length > 43 || request.Command is not null)
        {
            await SessionWire.WriteAsync(stream, new CommandReply(false, "IPC0100: Authentication required."), cancellationToken);
            return;
        }
        if (request.Operation is "IssueToken" or "RevokeToken" or "BeginMfa" or "ConfirmMfa" or "DisableMfa")
        {
            CommandReply reply;
            try
            {
                reply = request.Operation switch
                {
                    "IssueToken" => TokenReply(system.Security.OpenSession(request.User ?? "", request.Password ?? "", request.VerificationCode)),
                    "BeginMfa" => new(true, Enrollment: system.Security.BeginMfaEnrollment(request.User ?? "", request.Password ?? "", request.VerificationCode)),
                    "ConfirmMfa" => new(true, Confirmation: system.Security.ConfirmMfaEnrollment(request.EnrollmentToken ?? "", request.VerificationCode ?? "")),
                    "DisableMfa" => DisableMfa(),
                    "RevokeToken" => Revoke(),
                    _ => new(false),
                };
            }
            catch (Exception ex) when (ex is Ipc.Core.Messages.CpfException or System.Security.Cryptography.CryptographicException)
            { reply = new(false, "IPC0102: Authentication or verification failed."); }
            await SessionWire.WriteAsync(stream, reply, cancellationToken);
            return;

            CommandReply DisableMfa()
            {
                system.Security.DisableMfa(request.User ?? "", request.Password ?? "", request.VerificationCode ?? "");
                return new(true);
            }
            CommandReply Revoke() { system.Security.RevokeSession(request.Token ?? ""); return new(true); }
        }
        var authentication = request.Operation switch
        {
            "Authenticate" => system.Security.OpenSession(request.User ?? "", request.Password ?? "", request.VerificationCode),
            "AuthenticateToken" => system.Security.ResumeSession(request.Token ?? ""),
            _ => new Ipc.Services.Security.SignOnResult(false, null, "Authentication required."),
        };
        // Do not return provider details or distinguish absent profiles from bad credentials.
        if (!authentication.Success || authentication.MustChangePassword || authentication.Session is null)
        {
            await SessionWire.WriteAsync(stream, new CommandReply(false, "IPC0100: Sign-on failed or password change required.",
                MustVerifyMfa: authentication.MustVerifyMfa), cancellationToken);
            return;
        }
        try
        {
        var connectionCancellation = cancellationToken;
        using var execution = new ExecutionSession(system, authentication.Profile!, cancellationToken, authentication.Session.Id, communication: true);
        cancellationToken = execution.CancellationToken;
        await SessionWire.WriteAsync(stream, new CommandReply(true, Job: execution.Job.Key), cancellationToken);
        while (await SessionWire.ReadAsync<CommandRequest>(stream, cancellationToken) is { } next)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (next.User is not null || next.Password is not null || next.Token is not null || next.VerificationCode is not null || next.EnrollmentToken is not null)
                throw new InvalidDataException("Session identity cannot be replaced.");
            if (next.Operation is "Signoff" or "Logout")
            {
                if (next.Operation == "Logout") system.Security.RevokeSession(authentication.Session.Token);
                execution.End(JobCompletion.Normal, "Execution session signed off.");
                // Ending the job disposes its token source and can race the runtime
                // cancellation sampler. The connection still owns this final reply.
                await SessionWire.WriteAsync(stream, new CommandReply(true, Job: execution.Job.Key), connectionCancellation);
                return;
            }
            if (next.Operation != "Execute" || string.IsNullOrWhiteSpace(next.Command) || next.Command.Length > 32768)
            {
                await SessionWire.WriteAsync(stream, new CommandReply(false, "IPC0005: Invalid execution request."), cancellationToken);
                continue;
            }
            var result = execution.Execute(next.Command);
            await SessionWire.WriteAsync(stream, new CommandReply(!result.IsError, Job: execution.Job.Key, Result: result), cancellationToken);
            if (result.Outcome == Ipc.Cl.Commands.CommandOutcome.SignOff)
            {
                system.Security.RevokeSession(authentication.Session.Token);
                execution.End(JobCompletion.Normal, "Execution session signed off.");
                return;
            }
        }
        }
        finally
        {
            // Password-authenticated command connections receive a transient token;
            // bearer callers own the reusable token and revoke it through logout.
            if (request.Operation == "Authenticate") system.Security.RevokeSession(authentication.Session.Token);
        }
    }

    private static CommandReply TokenReply(Ipc.Services.Security.SignOnResult result) =>
        result.Success && result.Session is not null ? new(true, Session: result.Session) :
            new(false, "IPC0100: Sign-on failed or password change required.", MustVerifyMfa: result.MustVerifyMfa);
}
