using Ipc.Core.Messages;
using Ipc.Core.Work;

namespace Ipc.Services.Sqlite;

public sealed partial class SqliteConnectionFactory
{
    private string ResourceDatabase => DatabasePath ?? ConnectionString;
    internal IDisposable RegisterJobRuntimeResource(JobKey job, IDisposable resource)
    {
        var local = RegisterRuntimeResource(resource);
        try { return new ResourcePair(local, JobRuntimeResources.Register((ResourceDatabase, job), resource)); }
        catch { local.Dispose(); throw; }
    }
    internal IDisposable CloseJobRuntimeResources(JobKey job) => JobRuntimeResources.Close((ResourceDatabase, job));
    private sealed class ResourcePair(IDisposable local, IDisposable shared) : IDisposable
    { public void Dispose() { try { shared.Dispose(); } finally { local.Dispose(); } } }
    // Coordinate readers across factories attached to the same catalog, just as
    // record units coordinate their owning process. No gate is held while a
    // resource is cancelled/disposed or while SQLite is unwinding.
    private static class JobRuntimeResources
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<(string, JobKey), HashSet<IDisposable>> Resources = [];
        private static readonly Dictionary<(string, JobKey), int> Closing = [];
        internal static IDisposable Register((string, JobKey) key, IDisposable resource)
        {
            lock (Gate)
            {
                if (Closing.ContainsKey(key)) throw new CpfException("CPF1241", "Job resource cleanup is in progress.");
                if (!Resources.TryGetValue(key, out var set)) Resources[key] = set = new(ReferenceEqualityComparer.Instance);
                if (set.Count >= 128) throw new CpfException("SQL0904", "Job query cursor limit reached.");
                set.Add(resource); return new Registration(key, resource);
            }
        }
        internal static IDisposable Close((string, JobKey) key)
        {
            IDisposable[] resources;
            lock (Gate) { Closing[key] = Closing.GetValueOrDefault(key) + 1; resources = Resources.TryGetValue(key, out var set) ? set.ToArray() : []; }
            var scope = new ClosingScope(key);
            try
            {
                List<Exception>? errors = null;
                foreach (var resource in resources) try { resource.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
                if (errors is not null) throw new AggregateException("Could not close every job query resource.", errors);
                return scope;
            }
            catch { scope.Dispose(); throw; }
        }
        private sealed class Registration((string, JobKey) key, IDisposable resource) : IDisposable
        {
            public void Dispose()
            {
                lock (Gate) if (Resources.TryGetValue(key, out var set)) { set.Remove(resource); if (set.Count == 0) Resources.Remove(key); }
            }
        }
        private sealed class ClosingScope((string, JobKey) key) : IDisposable
        {
            private bool _closed;
            public void Dispose() { lock (Gate) { if (_closed) return; _closed = true; if (--Closing[key] == 0) Closing.Remove(key); } }
        }
    }
}
