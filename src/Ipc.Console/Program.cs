using Ipc.Console.Session;
using Ipc.Services;

namespace Ipc.Console;

public static class Program
{
    public static int Main(string[] args)
    {
        var dataDirectory = "/var/lib/ipcsys";
        string? databaseFile = null;

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
                    global::System.Console.Error.WriteLine("Usage: as400menu [--data-dir DIR] [--database FILE]");
                    return 2;
            }
        }

        try
        {
            using var system = IpcSystem.Create(dataDirectory, databaseFile);
            system.Start();
            var session = new TtySession(system, global::System.Console.In, global::System.Console.Out);
            session.Run();
            return session.ExitCode;
        }
        catch (Exception ex)
        {
            global::System.Console.Error.WriteLine($"AS400Menu: {ex.Message}");
            return 3;
        }
    }
}