using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

internal sealed class JobActivationGroups(SqliteConnectionFactory factory, JobKey job) : IDisposable
{
    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private readonly AsyncLocal<Group?> _current = new();
    private int _next;
    internal string CurrentName => _current.Value?.Name ?? "*DFTACTGRP";
    internal IDisposable Enter(ObjectDescriptor program)
    {
        var requested = program.ExtendedAttributes?.GetValueOrDefault("ipc.program.activationGroup") ?? "*CALLER";
        requested = requested.ToUpperInvariant();
        if (requested is not ("*CALLER" or "*NEW") && !ObjectName.IsValid(requested))
            throw new CpfException("IPC0124", "Invalid program activation group.");
        var name = requested switch { "*CALLER" => CurrentName, "*NEW" => "NEW" + (++_next).ToString("D7"), _ => requested };
        if (requested == "*NEW") while (_groups.ContainsKey(name)) name = "NEW" + (++_next).ToString("D7");
        if (!_groups.TryGetValue(name, out var group))
        {
            if (_groups.Count >= 256) throw new CpfException("IPC0124", "A job may retain at most 256 activation groups.");
            group = new Group(name, requested == "*NEW"); _groups.Add(name, group);
        }
        var previous = _current.Value; group.Calls++; _current.Value = group;
        try { Persist(group); return new CallScope(this, group, previous); }
        catch { group.Calls--; _current.Value = previous; throw; }
    }
    internal T Resource<T>(string key, Func<T> create) where T : class
    {
        var group = _current.Value ?? throw new InvalidOperationException("No active program scope.");
        if (!group.Resources.TryGetValue(key, out var value)) group.Resources.Add(key, value = create());
        return (T)value;
    }
    internal void RemoveResource(string key)
    {
        if (_current.Value?.Resources.Remove(key, out var value) == true && value is IDisposable disposable) disposable.Dispose();
    }
    internal void Reclaim(string name)
    {
        new ServiceAuthorization(factory).RequireJob(job);
        name = name.ToUpperInvariant();
        var candidates = name == "*ELIGIBLE" ? _groups.Values.Where(g => g.Calls == 0 && g.Name != "*DFTACTGRP").ToArray() :
            _groups.TryGetValue(name, out var group) ? new[] { group } : throw new CpfException("IPC0124", "Activation group not found.");
        if (candidates.Any(g => g.Calls != 0 || g.Name == "*DFTACTGRP")) throw new CpfException("IPC0124", "An active or default activation group cannot be reclaimed.");
        foreach (var candidate in candidates) Remove(candidate);
    }
    private void Persist(Group group)
    {
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sys_activation_groups(job_number,name,created_at,active_calls)
            VALUES($job,$name,$at,$calls) ON CONFLICT(job_number,name) DO UPDATE SET active_calls=$calls
            """;
        command.Parameters.AddWithValue("$job", job.Number); command.Parameters.AddWithValue("$name", group.Name);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$calls", group.Calls);
        command.ExecuteNonQuery();
    }
    private void Remove(Group group)
    {
        foreach (var disposable in group.Resources.Values.OfType<IDisposable>()) disposable.Dispose();
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sys_activation_groups WHERE job_number=$job AND name=$name";
        command.Parameters.AddWithValue("$job", job.Number); command.Parameters.AddWithValue("$name", group.Name); command.ExecuteNonQuery();
        _groups.Remove(group.Name);
    }
    public void Dispose() { foreach (var group in _groups.Values.ToArray()) Remove(group); }
    private sealed class Group(string name, bool transient)
    {
        public string Name { get; } = name; public bool Transient { get; } = transient; public int Calls;
        public Dictionary<string, object> Resources { get; } = new(StringComparer.Ordinal);
    }
    private sealed class CallScope(JobActivationGroups owner, Group group, Group? previous) : IDisposable
    {
        public void Dispose()
        {
            owner._current.Value = previous; group.Calls--;
            if (group.Transient && group.Calls == 0) owner.Remove(group); else owner.Persist(group);
        }
    }
}
