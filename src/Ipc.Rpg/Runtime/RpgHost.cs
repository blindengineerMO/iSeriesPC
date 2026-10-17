using Ipc.Db.Definitions;

namespace Ipc.Rpg.Runtime;

public sealed class RpgExternalCallResult
{
    public bool Success { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<object?> UpdatedParameters { get; init; } = Array.Empty<object?>();
}

public delegate RpgExternalCallResult? RpgCallHandler(string library, string name, IReadOnlyList<object?> parameters);

/// <summary>Hosts borrow typed scalar arguments only for targets that support them.</summary>
public delegate RpgExternalCallResult? RpgTypedCallHandler(string library, string name,
    IReadOnlyList<object?> parameters, Func<IReadOnlyList<object?>>? borrowArguments);

public delegate void RpgMessageHandler(string message);

public delegate string? RpgReceiveHandler();

public delegate void RpgDisplayHandler(string text);

public interface IRpgFileAccess
{
    FileDefinition? GetDefinition(string library, string name);

    bool MemberExists(string library, string name, string member);

    IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadAll(string library, string name, string member);

    IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadKeyed(string library, string name, string member);

    IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadKeyPrefix(
        string library, string name, string member, IReadOnlyDictionary<string, object?> prefix);

    void Insert(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> values);

    void Update(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> values);

    void Delete(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> keyValues);

    long RowCount(string library, string name, string member);
}

public sealed class RpgHost
{
    public Func<int> Ccsid { get; init; } = () => 37;

    public CancellationToken CancellationToken { get; init; }

    public IRpgFileAccess? Files { get; init; }

    public Func<string, RpgFileHandle>? OpenFile { get; init; }
    public Func<Ipc.Rpg.Model.RpgWorkstationBinding, IRpgWorkstation>? OpenWorkstation { get; init; }
    public Action<IRpgWorkstation>? RebindWorkstation { get; init; }
    public bool RetainWorkstations { get; init; }

    public Func<string, string, string?>? LibraryResolver { get; init; }

    public RpgCallHandler? ProgramCaller { get; init; }

    public RpgTypedCallHandler? TypedProgramCaller { get; init; }

    public RpgMessageHandler? SendMessage { get; init; }

    public RpgReceiveHandler? ReceiveMessage { get; init; }

    public RpgDisplayHandler? Display { get; init; }

    public DateTimeOffset? Now { get; init; }
}

public sealed class RpgFileHandle(Func<RpgFileCursor> cursor, Action close, Action? explicitClose = null) : IDisposable
{
    private bool _closed;
    public RpgFileCursor Cursor { get { ObjectDisposedException.ThrowIf(_closed, this); return cursor(); } }
    public void Close() { if (_closed) return; (explicitClose ?? close)(); _closed = true; }
    public void Dispose() { if (_closed) return; close(); _closed = true; }
}
