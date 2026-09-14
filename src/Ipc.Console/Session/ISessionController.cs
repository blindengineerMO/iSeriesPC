using Ipc.Core.Security;
using Ipc.Terminal;

namespace Ipc.Console.Session;

public interface ISessionController
{
    DisplayBuffer Buffer { get; }
    SessionEvent Handle(KeyPress keyPress);
}

public sealed record SessionEvent(
    string State,
    UserProfile? Profile = null,
    string? Message = null,
    ISessionController? Next = null,
    bool EndSession = false);