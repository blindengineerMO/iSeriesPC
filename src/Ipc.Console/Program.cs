using Ipc.Console.Session;
using Ipc.Services;
using Ipc.Services.Sqlite;
using Ipc.Session.Transport;

namespace Ipc.Console;

public static class Program
{
    public static int Main(string[] args)
    {
        var dataDirectory = "/var/lib/ipcsys";
        string? databaseFile = null;
        var migrateOnly = false;
        string? serverSocket = null;
        var standalone = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-dir" when i + 1 < args.Length:
                    dataDirectory = args[++i];
                    break;
                case "--database" when i + 1 < args.Length:
                    databaseFile = args[++i];
                    break;
                case "--help":
                case "-?":
                case "/?":
                    global::System.Console.Error.WriteLine("Usage: as400menu [--data-dir DIR] [--database FILE] [--server SOCKET | --standalone | --migrate-only]");
                    return 2;
                case "--migrate-only":
                    migrateOnly = true;
                    break;
                case "--server" when i + 1 < args.Length:
                    serverSocket = args[++i];
                    break;
                case "--standalone":
                    standalone = true;
                    break;
                default:
                    global::System.Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
                    return 2;
            }
        }

        try
        {
            if ((serverSocket is not null && (migrateOnly || standalone)) || (migrateOnly && standalone))
                throw new ArgumentException("--server, --standalone, and --migrate-only are mutually exclusive.");
            if (!migrateOnly && !standalone)
            {
                using var remote = new RemoteSessionController(serverSocket ?? Path.Combine(dataDirectory, "run", "as400.sock"));
                using var input = OpenTerminalInput();
                var terminal = new TtySession(remote, input, global::System.Console.Out);
                terminal.Run();
                return terminal.ExitCode;
            }
            if (migrateOnly)
            {
                using var factory = new SqliteConnectionFactory(dataDirectory, databaseFile ?? "system.db");
                var migrator = new Migrator(factory);
                migrator.MigrateToLatest();
                global::System.Console.WriteLine($"Catalog schema {migrator.CurrentVersion()} is ready.");
                if (migrator.LastBackupPath is { } backup)
                    global::System.Console.WriteLine($"Pre-upgrade backup: {backup}");
                return 0;
            }
            using var system = IpcSystem.Create(dataDirectory, databaseFile);
            var catalogPath = Path.GetFullPath(Path.Combine(dataDirectory, databaseFile ?? "system.db"));
            Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
            using var standaloneOwner = new FileStream(catalogPath + ".host.lock",
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            system.Start();
            using var localInput = OpenTerminalInput();
            var session = new TtySession(system, localInput, global::System.Console.Out);
            session.Run();
            return session.ExitCode;
        }
        catch (Exception ex)
        {
            global::System.Console.Error.WriteLine($"AS400Menu: {ex.Message}");
            return 3;
        }
    }

    // Console.In's interactive reader can impose managed line editing even after stty
    // enables raw mode. Read the actual byte stream so AID sequences arrive immediately.
    private static TextReader OpenTerminalInput() => OperatingSystem.IsLinux()
        ? new TerminalInputReader()
        : new StreamReader(global::System.Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
}
