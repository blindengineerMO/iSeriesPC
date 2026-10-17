namespace Ipc.Cl.Interpreter;

public enum ClDisplayOperation { Send, Receive, SendReceive }

public interface IClDisplayFile : IDisposable
{
    ClFileReadResult? Transfer(ClDisplayOperation operation, string record, IReadOnlyDictionary<string, object?> values);
}
