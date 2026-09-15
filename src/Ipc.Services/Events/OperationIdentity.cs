using Ipc.Core.Work;
using Ipc.Core.Objects;

namespace Ipc.Services.Events;

/// <summary>Server-established identity flowing through synchronous/async service calls.</summary>
public sealed record OperationIdentity(string Principal, JobKey? Job)
{
    public IReadOnlyList<ProgramAuthorityFrame> CallStack { get; init; } = Array.Empty<ProgramAuthorityFrame>();
    public string? AuthSessionId { get; init; }
    private static readonly AsyncLocal<OperationIdentity?> Slot = new();
    public static OperationIdentity? Current => Slot.Value;
    public static IDisposable Enter(string principal, JobKey? job = null, string? authSessionId = null)
    {
        var previous = Slot.Value;
        Slot.Value = new(principal, job) { AuthSessionId = authSessionId };
        return new Scope(previous);
    }
    public static IDisposable EnterProgram(ObjectDescriptor program)
    {
        var previous = Slot.Value;
        if (previous?.CallStack.Count >= 64) throw new Ipc.Core.Messages.CpfException("IPC0124", "Program call stack exceeds 64 frames.");
        if (previous is not null)
            Slot.Value = previous with { CallStack = Array.AsReadOnly(previous.CallStack.Append(new ProgramAuthorityFrame(
                program.Key, program.Owner, program.AdoptsOwnerAuthority, program.UsesAdoptedAuthority)).ToArray()) };
        return new Scope(previous);
    }

    public IEnumerable<string> AdoptedOwners()
    {
        foreach (var frame in CallStack.Reverse())
        {
            if (frame.AdoptsOwner) yield return frame.Owner;
            if (!frame.UsesAdopted) yield break;
        }
    }

    private sealed class Scope(OperationIdentity? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Slot.Value = previous;
        }
    }
}

public sealed record ProgramAuthorityFrame(QualifiedName Program, string Owner, bool AdoptsOwner, bool UsesAdopted);
