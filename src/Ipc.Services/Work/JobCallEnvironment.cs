using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Messages;

namespace Ipc.Services.Work;

public enum JobEnvironmentScope { Call, Job }
public sealed record JobFileOverride(string File, string Library, string Name, string Member, bool Share, JobEnvironmentScope Scope);

/// <summary>One job's call frames, overrides and open paths. Handles never migrate to another job.</summary>
public sealed class JobCallEnvironment : IDisposable
{
    private readonly JobKey _job;
    private readonly JobDataAreaStore _areas;
    private readonly MessageQueueStore _messages;
    private readonly Frame _root = new();
    private Frame _current;
    private bool _disposed;
    internal JobCallEnvironment(SqliteConnectionFactory factory, JobKey job) { _job = job; _areas = new(factory); _messages = new(factory); _current = _root; }
    public IDisposable EnterCall(string program = "QCMD")
    {
        RequireOwner();
        var parent = _current; if (parent.Depth >= 64) throw new CpfException("IPC0126", "Call environment depth exceeds 64.");
        if (!ObjectName.IsValid(program)) throw new CpfException("IPC0126", "Invalid call-frame program name.");
        var frame = new Frame(parent, program); _current = frame; return new CallLease(this, frame, parent);
    }
    public MessageQueueAddress MessageQueue(string relationship = "*SAME")
    {
        RequireOwner();
        var frame = relationship switch { "*SAME" or "*" => _current, "*PRV" => _current.Parent ?? _root, "*EXT" => _root,
            _ => Frames().FirstOrDefault(f => f.Program == relationship) ?? throw new CpfException("CPF2479", "Program is not on the call stack.") };
        return EnsureQueue(frame);
    }
    private MessageQueueAddress EnsureQueue(Frame frame) => frame.Queue ??= _messages.OpenProgramQueue(_job,
        frame.Parent is null ? "*EXT" : frame.Program, frame.Parent is null ? null : EnsureQueue(frame.Parent));
    private void CloseFrame(Frame frame)
    {
        try { frame.Dispose(); }
        finally { if (frame.Queue is { } queue) _messages.CloseProgramQueueForOwner(_job, queue); }
    }
    public void Override(JobFileOverride value)
    {
        RequireOwner();
        if (!ObjectName.IsValid(value.File) || !ObjectName.IsValid(value.Library) || !ObjectName.IsValid(value.Name) || !ObjectName.IsValid(value.Member) || !Enum.IsDefined(value.Scope))
            throw new CpfException("IPC0126", "Invalid file override identity or scope.");
        var frame = value.Scope == JobEnvironmentScope.Job ? _root : _current;
        if (!frame.Overrides.ContainsKey(value.File) && frame.Overrides.Count >= 256) throw new CpfException("IPC0126", "Override limit reached.");
        frame.Overrides[value.File] = value;
    }
    public void DeleteOverride(string file, JobEnvironmentScope scope)
    {
        RequireOwner(); if (!Enum.IsDefined(scope)) throw new CpfException("IPC0126", "Invalid override scope."); var frame = scope == JobEnvironmentScope.Job ? _root : _current;
        if (file == "*ALL") frame.Overrides.Clear(); else frame.Overrides.Remove(file);
    }
    public JobFileOverride? Resolve(string file)
    {
        RequireOwner();
        for (var frame = _current; frame is not null; frame = frame.Parent)
            if (frame.Overrides.TryGetValue(file, out var value)) return value;
        return null;
    }
    public IReadOnlyList<JobFileOverride> Overrides()
    {
        RequireOwner(); var names = new HashSet<string>(StringComparer.Ordinal); var result = new List<JobFileOverride>();
        for (var frame = _current; frame is not null; frame = frame.Parent)
            foreach (var value in frame.Overrides.Values) if (names.Add(value.File)) result.Add(value);
        return result;
    }
    public JobOpenPath<T> OpenPath<T>(string identity, bool shared, JobEnvironmentScope scope, Func<T> create) where T : class, IDisposable
    {
        RequireOwner();
        if (identity.Length is < 1 or > 512 || !Enum.IsDefined(scope)) throw new CpfException("IPC0126", "Invalid open-path identity or scope.");
        var frame = scope == JobEnvironmentScope.Job ? _root : _current;
        if (shared)
        {
            for (var ancestor = frame; ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Paths.TryGetValue(identity, out var existing))
                {
                    if (existing.Value is not T) throw new CpfException("IPC0126", "Open path has a different record interface.");
                    existing.References++; return new(this, existing);
                }
        }
        if (frame.Paths.Count >= 256) throw new CpfException("IPC0126", "Open-path limit reached.");
        var key = shared ? identity : identity + ":" + Guid.NewGuid().ToString("N");
        var entry = new PathEntry(create(), frame, key, shared); frame.Paths.Add(key, entry);
        return new(this, entry);
    }
    public int OpenPathCount { get { RequireOwner(); return Frames().Sum(f => f.Paths.Count); } }
    private IEnumerable<Frame> Frames() { for (var frame = _current; frame is not null; frame = frame.Parent) yield return frame; }
    internal void RequireOwner()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _areas.RequireOwner(_job);
    }
    private void CheckIdentity()
    {
        if (OperationIdentity.Current is { } identity && identity.Job != _job)
            throw new CpfException("CPF9802", "Open paths belong to the executing job only.");
    }
    internal void Release(PathEntry entry, bool close = false)
    {
        CheckIdentity();
        if (entry.Disposed) return;
        entry.CloseRequested |= close;
        if (--entry.References == 0 && (!entry.Shared || entry.CloseRequested)) { entry.Dispose(); entry.Frame.Paths.Remove(entry.Key); }
    }
    public void Dispose()
    {
        CheckIdentity();
        if (_disposed) return; _disposed = true;
        foreach (var frame in Frames()) CloseFrame(frame);
    }
    internal sealed class Frame(Frame? parent = null, string program = "QCMD") : IDisposable
    {
        public Frame? Parent { get; } = parent;
        public string Program { get; } = program;
        public MessageQueueAddress? Queue { get; set; }
        public int Depth { get; } = (parent?.Depth ?? -1) + 1;
        public Dictionary<string, JobFileOverride> Overrides { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PathEntry> Paths { get; } = new(StringComparer.Ordinal);
        public void Dispose() { foreach (var path in Paths.Values) path.Dispose(); Paths.Clear(); Overrides.Clear(); }
    }
    internal sealed class PathEntry(IDisposable value, Frame frame, string key, bool shared) : IDisposable
    {
        public IDisposable Value { get; } = value; public Frame Frame { get; } = frame; public string Key { get; } = key;
        public bool Shared { get; } = shared; public bool CloseRequested { get; set; } public int References { get; set; } = 1; public bool Disposed { get; private set; }
        public void Dispose() { if (Disposed) return; Disposed = true; Value.Dispose(); }
    }
    private sealed class CallLease(JobCallEnvironment owner, Frame frame, Frame parent) : IDisposable
    {
        private bool _ended;
        public void Dispose()
        {
            if (_ended) return; _ended = true;
            if (owner._current != frame) throw new InvalidOperationException("Call environments must unwind in stack order.");
            try { owner.CloseFrame(frame); } finally { owner._current = parent; }
        }
    }
}
public sealed class JobOpenPath<T> : IDisposable where T : class, IDisposable
{
    private readonly JobCallEnvironment _owner; private readonly JobCallEnvironment.PathEntry _entry; private bool _disposed;
    internal JobOpenPath(JobCallEnvironment owner, JobCallEnvironment.PathEntry entry) { _owner = owner; _entry = entry; }
    public T Value { get { ObjectDisposedException.ThrowIf(_disposed || _entry.Disposed, this); _owner.RequireOwner(); return (T)_entry.Value; } }
    public void Close() { if (_disposed) return; _owner.Release(_entry, close: true); _disposed = true; }
    public void Dispose() { if (_disposed) return; _owner.Release(_entry); _disposed = true; }
}
