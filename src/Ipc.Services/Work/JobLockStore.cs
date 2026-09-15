using System.Diagnostics;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public enum JobLockMode { Exclusive, SharedUpdate, SharedRead, SharedNoUpdate }
public sealed record JobLockResource(string Library, string Name, string Type, string Member = "", long RowNumber = 0)
{
    public bool IsRecord => Member.Length != 0;
    internal void Validate()
    {
        new ObjectDescriptor { Key = new(Library, Name), ObjectType = Type }.ValidateIdentity();
        if (IsRecord && (Type != ObjectType.File || !ObjectName.IsValid(Member) || RowNumber < 1) || !IsRecord && RowNumber != 0)
            throw new CpfException("IPC0125", "Invalid record lock identity.");
    }
}
public sealed record JobLockInfo(string Token, JobKey Job, JobLockResource Resource, JobLockMode Mode, bool Waiting, string Lifetime, string Scope);

public sealed class JobLockStore(SqliteConnectionFactory factory)
{
    private readonly SqliteConnectionFactory _factory = factory;
    private static readonly AsyncLocal<CommandScope?> ActiveCommand = new();

    public static bool Compatible(JobLockMode left, JobLockMode right) =>
        Enum.IsDefined(left) && Enum.IsDefined(right) && left != JobLockMode.Exclusive && right != JobLockMode.Exclusive &&
        (left == JobLockMode.SharedRead || right == JobLockMode.SharedRead || left == right);

    public IDisposable EnterCommand(JobKey job, CancellationToken cancellationToken = default)
    {
        RequireOwner(job); var previous = ActiveCommand.Value;
        var scope = new CommandScope(this, job, Guid.NewGuid().ToString("N"), cancellationToken, previous);
        ActiveCommand.Value = scope; return scope;
    }

    internal void RequireAccess(string library, string name, string type, AuthorityBit permission)
    {
        var scope = ActiveCommand.Value;
        if (scope is null || OperationIdentity.Current?.Job != scope.Job || scope.Store._factory.ConnectionString != _factory.ConnectionString) return;
        var mode = (permission & (AuthorityBit.ObjectExist | AuthorityBit.ObjectManagement | AuthorityBit.ObjectAlter)) != 0 ? JobLockMode.Exclusive :
            (permission & (AuthorityBit.Add | AuthorityBit.Update | AuthorityBit.Delete)) != 0 ? JobLockMode.SharedUpdate : JobLockMode.SharedRead;
        RequireAutomatic(new(library, name, type), mode, scope);
    }
    public void RequireStableRead(string library, string name, string type)
    {
        var scope = ActiveCommand.Value;
        if (scope is null || OperationIdentity.Current?.Job != scope.Job || scope.Store._factory.ConnectionString != _factory.ConnectionString) return;
        RequireAutomatic(new(library, name, type), JobLockMode.SharedNoUpdate, scope);
    }
    public void RequireObjectMutation(string library, string name, string type) => RequireAccess(library, name, type, AuthorityBit.ObjectExist);

    public void RequireRecordAccess(string library, string name, string member, long rowNumber, bool write)
    {
        var scope = ActiveCommand.Value;
        if (scope is null || OperationIdentity.Current?.Job != scope.Job || scope.Store._factory.ConnectionString != _factory.ConnectionString) return;
        RequireAutomatic(new(library, name, ObjectType.File, member, rowNumber), write ? JobLockMode.Exclusive : JobLockMode.SharedRead, scope);
    }
    /// <summary>Read access held only until the returned lease is disposed, independent
    /// of other record locks owned by the enclosing command.</summary>
    public IDisposable? AcquireReadScope(JobLockResource resource)
    {
        var scope = ActiveCommand.Value;
        if (scope is null || OperationIdentity.Current?.Job != scope.Job || scope.Store._factory.ConnectionString != _factory.ConnectionString) return null;
        return AcquireWithLifetime(scope.Job, resource, JobLockMode.SharedRead, TimeSpan.Zero,
            scope.CancellationToken, "Read", Guid.NewGuid().ToString("N"));
    }
    private void RequireAutomatic(JobLockResource resource, JobLockMode mode, CommandScope scope)
    {
        var key = (resource, mode);
        if (!scope.Acquired.Add(key)) return;
        try { AcquireCore(scope.Job, resource, mode, TimeSpan.Zero, "Command", scope.Id, scope.CancellationToken); }
        catch { scope.Acquired.Remove(key); throw; }
    }

    public IDisposable Acquire(JobKey job, JobLockResource resource, JobLockMode mode, TimeSpan wait, CancellationToken cancellationToken = default)
        => AcquireWithLifetime(job, resource, mode, wait, cancellationToken, "Job", "*JOB");

    internal IDisposable AcquireWithLifetime(JobKey job, JobLockResource resource, JobLockMode mode, TimeSpan wait, CancellationToken cancellationToken, string lifetime, string scope)
    {
        RequireOwner(job); resource.Validate();
        new ServiceAuthorization(_factory).RequireObject(resource.Library, resource.Name, resource.Type, Authorities.UseBits, acquireLocks: false);
        EnsureResourceExists(resource);
        var token = Guid.NewGuid().ToString("N");
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (resource.Type != ObjectType.Library)
                AcquireCore(job, new("QSYS", resource.Library, ObjectType.Library), JobLockMode.SharedRead, wait, "Parent", "Parent:" + token, cancellationToken);
            var remaining = wait - Stopwatch.GetElapsedTime(started);
            AcquireCore(job, resource, mode, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, lifetime, scope, cancellationToken, token);
            try { EnsureResourceExists(resource); } catch { new HeldScope(this, job, token).Dispose(); throw; }
            return new HeldScope(this, job, token);
        }
        catch { ReleaseScope(job, "Parent:" + token); throw; }
    }

    public JobLockTransaction EnlistTransaction(JobKey job, SqliteTransaction transaction)
    {
        RequireOwner(job);
        if (transaction.Connection is not { } connection ||
            new SqliteConnectionStringBuilder(connection.ConnectionString).ToString() != new SqliteConnectionStringBuilder(_factory.ConnectionString).ToString())
            throw new ArgumentException("Transaction must belong to this job's catalog.", nameof(transaction));
        return new(this, job, transaction);
    }

    private void EnsureResourceExists(JobLockResource resource)
    {
        if (new SqliteObjectStore(_factory).GetForAuthorization(resource.Library, resource.Name, resource.Type) is null)
            throw new CpfException("CPF9801", "Lock object no longer exists.");
        if (!resource.IsRecord) return;
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sys_file_members WHERE lib=$lib AND name=$name AND mbr=$member AND type='*FILE'";
        command.Parameters.AddWithValue("$lib", resource.Library); command.Parameters.AddWithValue("$name", resource.Name); command.Parameters.AddWithValue("$member", resource.Member);
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new CpfException("CPF2817", "Lock member no longer exists.");
        var table = resource.Library + "." + resource.Name + "." + resource.Member;
        command.CommandText = "SELECT name FROM pragma_table_info($table)"; command.Parameters.AddWithValue("$table", table);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(0));
        var rowid = new[] { "_rowid_", "rowid", "oid" }.FirstOrDefault(x => !columns.Contains(x))
            ?? throw new CpfException("IPC0125", "Member does not expose a supported record identity.");
        command.CommandText = "SELECT count(*) FROM \"" + table + "\" WHERE " + rowid + "=$row";
        command.Parameters.AddWithValue("$row", resource.RowNumber);
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new CpfException("CPF5001", "Lock record no longer exists.");
    }

    // Long lived allocations must be released before changing an object's identity. Runtime
    // locks are deliberately outside catalog transactions and cannot be renamed atomically.
    internal void RequireNoPersistentAllocation(JobLockResource resource)
    {
        using var connection = _factory.Locks.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM holders WHERE library=$lib AND name=$name AND type=$type AND lifetime<>'Command'";
        command.Parameters.AddWithValue("$lib", resource.Library); command.Parameters.AddWithValue("$name", resource.Name); command.Parameters.AddWithValue("$type", resource.Type);
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new CpfException("CPF1002", "Release persistent allocations before deleting, moving or renaming this object.");
    }

    internal string AcquireCore(JobKey job, JobLockResource resource, JobLockMode mode, TimeSpan wait, string lifetime, string scope, CancellationToken cancellationToken, string? requestToken = null)
    {
        resource.Validate();
        if (!Enum.IsDefined(mode) || wait < TimeSpan.Zero || wait > TimeSpan.FromMinutes(5)) throw new CpfException("IPC0125", "Lock wait must be 0–300 seconds.");
        var token = requestToken ?? Guid.NewGuid().ToString("N"); var started = Stopwatch.GetTimestamp();
        var expires = DateTimeOffset.UtcNow.Add(wait).ToUnixTimeMilliseconds();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureLive(job);
                if (lifetime is "Job" or "Commitment")
                    new ServiceAuthorization(_factory).RequireObject(resource.Library, resource.Name, resource.Type, Authorities.UseBits, acquireLocks: false);
                using (var connection = _factory.Locks.Open())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "DELETE FROM waiters WHERE expires_at<$now";
                    command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); command.ExecuteNonQuery();
                    var blockers = Conflicts(connection, transaction, job, resource, mode, token);
                    if (blockers.Count == 0)
                    {
                        EnsureLive(job);
                        command.CommandText = "SELECT count(*) FROM holders WHERE job_number=$job";
                        command.Parameters.AddWithValue("$job", job.Number);
                        if (Convert.ToInt64(command.ExecuteScalar()) >= 8192) throw new CpfException("IPC0125", "Job lock limit of 8192 reached.");
                        command.CommandText = """
                            INSERT INTO holders(token,job_number,job_name,job_user,library,name,type,member,row_number,mode,lifetime,scope)
                            VALUES($token,$job,$jname,$juser,$lib,$name,$type,$member,$row,$mode,$life,$scope);
                            DELETE FROM waiters WHERE token=$token;
                            """;
                        Bind(command, job, resource, mode, token); command.Parameters.AddWithValue("$life", lifetime); command.Parameters.AddWithValue("$scope", scope);
                        command.ExecuteNonQuery(); transaction.Commit(); return token;
                    }
                    if (wait == TimeSpan.Zero || Stopwatch.GetElapsedTime(started) >= wait)
                        throw new CpfException("CPF1002", "Object or record is allocated to another job; lock wait expired.");
                    command.CommandText = """
                        INSERT INTO waiters(token,job_number,job_name,job_user,library,name,type,member,row_number,mode,expires_at,blockers)
                        VALUES($token,$job,$jname,$juser,$lib,$name,$type,$member,$row,$mode,$expires,$blockers)
                        ON CONFLICT(token) DO UPDATE SET blockers=$blockers;
                        """;
                    Bind(command, job, resource, mode, token); command.Parameters.AddWithValue("$expires", expires);
                    command.Parameters.AddWithValue("$blockers", JsonSerializer.Serialize(blockers)); command.ExecuteNonQuery();
                    if (Deadlock(connection, transaction, job.Number)) throw new CpfException("CPF1003", "Lock deadlock detected; the newest request was rejected.");
                    transaction.Commit();
                }
                if (cancellationToken.WaitHandle.WaitOne(20)) cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally { RemoveWaiter(token); }
    }

    private static HashSet<int> Conflicts(SqliteConnection connection, SqliteTransaction transaction, JobKey job, JobLockResource resource, JobLockMode mode, string token)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT member,row_number,mode FROM holders WHERE library=$lib AND name=$name AND type=$type AND job_number=$job";
        Bind(command, job, resource, mode, token); var covered = false;
        using (var own = command.ExecuteReader()) while (own.Read())
        {
            var ownMode = (JobLockMode)own.GetInt32(2); var ownRecord = own.GetString(0).Length != 0;
            if (!resource.IsRecord && ownRecord && mode == JobLockMode.SharedRead ||
                !ownRecord && ownMode == JobLockMode.Exclusive ||
                ownRecord == resource.IsRecord && own.GetString(0) == resource.Member && own.GetInt64(1) == resource.RowNumber &&
                (ownMode == mode || ownMode == JobLockMode.Exclusive || mode == JobLockMode.SharedRead)) covered = true;
        }
        command.Parameters.AddWithValue("$covered", covered ? 1 : 0);
        command.CommandText = """
            SELECT job_number,member,row_number,mode FROM holders WHERE library=$lib AND name=$name AND type=$type AND job_number<>$job
              AND ($member='' OR member='' OR member=$member AND row_number=$row)
            UNION ALL
            SELECT job_number,member,row_number,mode FROM waiters WHERE library=$lib AND name=$name AND type=$type AND job_number<>$job
              AND ($member='' OR member='' OR member=$member AND row_number=$row)
              AND $covered=0 AND sequence<coalesce((SELECT sequence FROM waiters WHERE token=$token),9223372036854775807)
            """;
        Bind(command, job, resource, mode, token); using var reader = command.ExecuteReader(); var blockers = new HashSet<int>();
        while (reader.Read())
        {
            var heldRecord = reader.GetString(1).Length != 0; var heldMode = (JobLockMode)reader.GetInt32(3);
            var requestedMode = mode;
            if (resource.IsRecord != heldRecord)
            {
                if (resource.IsRecord) requestedMode = Intent(mode); else heldMode = Intent(heldMode);
            }
            if (!Compatible(requestedMode, heldMode)) blockers.Add(reader.GetInt32(0));
        }
        return blockers;
    }
    private static JobLockMode Intent(JobLockMode mode) => mode is JobLockMode.Exclusive or JobLockMode.SharedUpdate ? JobLockMode.SharedUpdate : JobLockMode.SharedRead;
    private static bool Deadlock(SqliteConnection connection, SqliteTransaction transaction, int job)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT job_number,blockers FROM waiters";
        var graph = new Dictionary<int, HashSet<int>>();
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var from = reader.GetInt32(0); if (!graph.TryGetValue(from, out var edges)) graph[from] = edges = new();
            edges.UnionWith(JsonSerializer.Deserialize<int[]>(reader.GetString(1))!);
        }
        var pending = new Stack<int>(graph.GetValueOrDefault(job) ?? new()); var seen = new HashSet<int>();
        while (pending.TryPop(out var next))
        {
            if (next == job) return true;
            if (seen.Add(next) && graph.TryGetValue(next, out var edges)) foreach (var edge in edges) pending.Push(edge);
        }
        return false;
    }
    public IReadOnlyList<JobLockInfo> Inspect(JobLockResource resource)
    {
        resource.Validate(); new ServiceAuthorization(_factory).RequireObject(resource.Library, resource.Name, resource.Type, Authorities.UseBits, acquireLocks: false);
        using var connection = _factory.Locks.Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT token,job_number,job_name,job_user,member,row_number,mode,0,lifetime,scope FROM holders WHERE library=$lib AND name=$name AND type=$type
            UNION ALL SELECT token,job_number,job_name,job_user,member,row_number,mode,1,'Wait','' FROM waiters WHERE library=$lib AND name=$name AND type=$type
            """;
        command.Parameters.AddWithValue("$lib", resource.Library); command.Parameters.AddWithValue("$name", resource.Name); command.Parameters.AddWithValue("$type", resource.Type);
        using var reader = command.ExecuteReader(); var rows = new List<JobLockInfo>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), new(reader.GetInt32(1), reader.GetString(2), reader.GetString(3)),
            resource with { Member = reader.GetString(4), RowNumber = reader.GetInt64(5) }, (JobLockMode)reader.GetInt32(6), reader.GetInt32(7) != 0, reader.GetString(8), reader.GetString(9)));
        return rows;
    }
    public void Release(JobKey job, JobLockResource resource, JobLockMode mode)
    {
        RequireOwner(job); resource.Validate();
        using var connection = _factory.Locks.Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM holders WHERE scope IN (SELECT 'Parent:'||token FROM holders WHERE job_number=$job AND job_name=$jname AND job_user=$juser AND library=$lib AND name=$name AND type=$type AND member=$member AND row_number=$row AND mode=$mode AND lifetime='Job');
            DELETE FROM holders WHERE job_number=$job AND job_name=$jname AND job_user=$juser AND library=$lib AND name=$name AND type=$type AND member=$member AND row_number=$row AND mode=$mode AND lifetime='Job';
            """;
        Bind(command, job, resource, mode, ""); if (command.ExecuteNonQuery() == 0) throw new CpfException("CPF1004", "Job does not hold the requested lock.");
    }
    internal void ReleaseScope(JobKey job, string scope)
    {
        using var connection = _factory.Locks.Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM holders WHERE job_number=$job AND scope=$scope";
        command.Parameters.AddWithValue("$job", job.Number); command.Parameters.AddWithValue("$scope", scope); command.ExecuteNonQuery();
    }
    internal void ReleaseJob(JobKey job)
    {
        using var connection = _factory.Locks.Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM holders WHERE job_number=$job; DELETE FROM waiters WHERE job_number=$job";
        command.Parameters.AddWithValue("$job", job.Number); command.ExecuteNonQuery();
    }
    public void RecoverEndedJobs()
    {
        new ServiceAuthorization(_factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        using var connection = _factory.Locks.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT job_number,job_name,job_user FROM holders UNION SELECT job_number,job_name,job_user FROM waiters";
        var jobs = new List<JobKey>(); using (var reader = command.ExecuteReader()) while (reader.Read()) jobs.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        foreach (var job in jobs) if (!IsLive(job, includeCancellation: true)) ReleaseJob(job);
    }
    private void RequireOwner(JobKey job)
    {
        if (OperationIdentity.Current is { } identity && identity.Job != job) throw new CpfException("CPF9802", "Locks must belong to the executing job.");
        new ServiceAuthorization(_factory).RequireJob(job); EnsureLive(job);
    }
    private bool IsLive(JobKey job, bool includeCancellation = false)
    {
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sys_jobs WHERE number=$job AND name=$name AND user=$user AND execution_state='Running' AND ($include=1 OR cancel_requested=0)";
        command.Parameters.AddWithValue("$include", includeCancellation ? 1 : 0);
        command.Parameters.AddWithValue("$job", job.Number); command.Parameters.AddWithValue("$name", job.Name); command.Parameters.AddWithValue("$user", job.User);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }
    private void EnsureLive(JobKey job)
    {
        if (IsLive(job)) return;
        if (IsLive(job, includeCancellation: true)) throw new OperationCanceledException("Job cancellation was requested.");
        throw new CpfException("CPF1241", "Lock owner is not an active job.");
    }
    private void RemoveWaiter(string token)
    {
        using var connection = _factory.Locks.Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM waiters WHERE token=$token"; command.Parameters.AddWithValue("$token", token); command.ExecuteNonQuery();
    }
    private static void Bind(SqliteCommand command, JobKey job, JobLockResource resource, JobLockMode mode, string token)
    {
        void Add(string name, object value) { if (!command.Parameters.Contains(name)) command.Parameters.AddWithValue(name, value); }
        Add("$job", job.Number); Add("$jname", job.Name); Add("$juser", job.User); Add("$lib", resource.Library); Add("$name", resource.Name);
        Add("$type", resource.Type); Add("$member", resource.Member); Add("$row", resource.RowNumber); Add("$mode", (int)mode); Add("$token", token);
    }
    private sealed class HeldScope(JobLockStore store, JobKey job, string token) : IDisposable
    {
        public void Dispose()
        {
            using var connection = store._factory.Locks.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM holders WHERE (token=$token OR scope='Parent:'||$token) AND job_number=$job";
            command.Parameters.AddWithValue("$token", token); command.Parameters.AddWithValue("$job", job.Number); command.ExecuteNonQuery();
        }
    }
    private sealed class CommandScope(JobLockStore store, JobKey job, string id, CancellationToken cancellationToken, CommandScope? previous) : IDisposable
    {
        public JobLockStore Store { get; } = store; public JobKey Job { get; } = job; public string Id { get; } = id;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public HashSet<(JobLockResource, JobLockMode)> Acquired { get; } = new();
        public void Dispose() { try { Store.ReleaseScope(Job, Id); } finally { ActiveCommand.Value = previous; } }
    }
}
