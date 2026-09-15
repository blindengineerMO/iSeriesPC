using System.Runtime.InteropServices;
using Ipc.Session.Transport;

namespace Ipc.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var directory = "/var/lib/ipcsys";
        string? socket = null;
        var database = "system.db";
        var resetAdministrator = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-dir" when i + 1 < args.Length: directory = args[++i]; break;
                case "--socket" when i + 1 < args.Length: socket = args[++i]; break;
                case "--database" when i + 1 < args.Length: database = args[++i]; break;
                case "--reset-admin": resetAdministrator = true; break;
                case "--help":
                    global::System.Console.WriteLine("Usage: as400server [--data-dir DIR] [--database FILE] [--socket PATH] [--reset-admin]");
                    return 0;
                default:
                    global::System.Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
                    return 2;
            }
        }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        global::System.Console.CancelKeyPress += cancel;
        using var terminate = OperatingSystem.IsLinux()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); })
            : null;
        try
        {
            if (resetAdministrator)
            {
                var catalog = Path.GetFullPath(Path.Combine(directory, database));
                Directory.CreateDirectory(Path.GetDirectoryName(catalog)!);
                using var ownership = new FileStream(catalog + ".host.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                using var system = Ipc.Services.IpcSystem.Create(directory, database);
                system.Start();
                var path = system.Security.Profiles.ResetAdministratorCredential();
                global::System.Console.WriteLine($"QSECOFR recovery credential written to {path}; password change is required at sign-on.");
                return 0;
            }
            var server = new SessionServer(directory, socket, database);
            var run = server.RunAsync(stop.Token);
            await server.Ready;
            global::System.Console.WriteLine($"as400server ready: {server.SocketPath}");
            await run;
            return 0;
        }
        catch (Exception ex)
        {
            global::System.Console.Error.WriteLine($"as400server: {ex.Message}");
            return 3;
        }
        finally { global::System.Console.CancelKeyPress -= cancel; }
    }
}
