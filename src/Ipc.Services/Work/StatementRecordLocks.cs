using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed partial class JobLockStore
{
    internal IDisposable AcquireReferentialFile(JobKey job, QualifiedName file, CancellationToken token, Action validatePublisher, SqliteTransaction? publisher = null, JobLockMode mode = JobLockMode.SharedRead)
    {
        if (mode is not (JobLockMode.SharedRead or JobLockMode.Exclusive)) throw new ArgumentOutOfRangeException(nameof(mode));
        void ValidateOwner() { if (publisher is null) RequireOwner(job); else RequirePublisherOwner(job,publisher); }
        ValidateOwner(); var resource = Resolve(new(file.Library,file.Name.Value,ObjectType.File));
        validatePublisher(); RequireStatementObject(resource,publisher); var id = Guid.NewGuid().ToString("N");
        try
        {
            AcquireCore(job,new("QSYS",file.Library,ObjectType.Library),JobLockMode.SharedRead,TimeSpan.Zero,"Parent","Parent:" + id,token,validateLive: publisher is null ? null : ValidateOwner);
            AcquireCore(job,resource,mode,TimeSpan.Zero,"Referential","Referential:" + id,token,id,validateLive: publisher is null ? null : ValidateOwner);
            return new HeldScope(this,job,id);
        }
        catch { ReleaseScope(job,"Parent:" + id); throw; }
    }
    // One holder protects a bounded set of selected records until statement
    // publication/rollback, without exhausting the per-job holder budget.
    internal IDisposable? AcquireStatementRows(JobKey job, JobLockResource resource, IReadOnlySet<long> rows,
        AuthorityBit authority, CancellationToken token, Action? validateAuthority = null, SqliteTransaction? publisher = null, JobLockMode mode = JobLockMode.Exclusive)
    {
        if (rows.Count == 0) return null;
        if (mode is not (JobLockMode.Exclusive or JobLockMode.SharedRead or JobLockMode.SharedNoUpdate)) throw new ArgumentOutOfRangeException(nameof(mode));
        ValidateOwner(); resource = Resolve(resource);
        (resource with { RowNumber = rows.First() }).Validate();
        if (rows.Any(row => row is < 1 or > RecordSlots.MaximumNumber)) throw new CpfException("IPC0125", "Invalid statement record number.");
        if (validateAuthority is null) new ServiceAuthorization(_factory).RequireObject(resource.Library, resource.Name, resource.Type, authority | AuthorityBit.ObjectOperate, acquireLocks: false);
        else validateAuthority();
        RequireStatementObject(resource with { Member = "", RowNumber = 0 },publisher);
        var id = Guid.NewGuid().ToString("N");
        try
        {
            AcquireCore(job, new("QSYS", resource.Library, ObjectType.Library), JobLockMode.SharedRead, TimeSpan.Zero, "Parent", "Parent:" + id, token,
                validateLive: publisher is null ? null : ValidateOwner);
            using var connection = _factory.Locks.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, token);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM waiters WHERE expires_at<$now"; command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); command.ExecuteNonQuery();
            command.CommandText = "SELECT token,job_number,member,row_number,mode,lifetime FROM holders WHERE library=$lib AND name=$name AND type=$type";
            Bind(command, job, resource, mode, id);
            var held = new List<(string Token, int Job, string Member, long Row, JobLockMode Mode, string Lifetime)>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) held.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3), (JobLockMode)reader.GetInt32(4), reader.GetString(5)));
            var covered = new HashSet<long>(); var whole = false;
            foreach (var item in held.Where(item => item.Job == job.Number))
            {
                if (item.Member.Length == 0 && (item.Mode == JobLockMode.Exclusive || item.Mode == mode)) whole = true;
                if (item.Member != resource.Member || item.Mode != JobLockMode.Exclusive && item.Mode != mode) continue;
                if (item.Lifetime == "StatementRows") foreach (var row in RecordSet(item.Token)) covered.Add(row);
                else covered.Add(item.Row);
            }
            foreach (var item in held.Where(item => item.Job != job.Number || mode == JobLockMode.Exclusive && item.Lifetime == "UpdateRead"))
                if (Blocks(item.Member, item.Row, item.Mode, item.Token, item.Lifetime == "StatementRows", false))
                    throw new CpfException("CPF1002", "A selected SQL record is allocated to another operation.");
            command.CommandText = "SELECT member,row_number,mode FROM waiters WHERE library=$lib AND name=$name AND type=$type AND job_number<>$job";
            var waits = new List<(string Member, long Row, JobLockMode Mode)>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) waits.Add((reader.GetString(0), reader.GetInt64(1), (JobLockMode)reader.GetInt32(2)));
            foreach (var item in waits) if (!whole && Blocks(item.Member, item.Row, item.Mode, "", false, true))
                throw new CpfException("CPF1002", "An earlier request is waiting for a selected SQL record.");
            command.CommandText = "SELECT count(*) FROM holders WHERE job_number=$job";
            if (Convert.ToInt64(command.ExecuteScalar()) >= 8192) throw new CpfException("IPC0125", "Job lock holder limit reached.");
            command.CommandText = "INSERT INTO holders(token,job_number,job_name,job_user,library,name,type,member,row_number,mode,lifetime,scope) VALUES($token,$job,$jname,$juser,$lib,$name,$type,$member,0,$mode,'StatementRows',$token)";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO holder_records(token,rrn) VALUES($token,$rrn)"; command.Parameters.AddWithValue("$rrn", 0L);
            foreach (var row in rows) { token.ThrowIfCancellationRequested(); command.Parameters["$rrn"].Value = row; command.ExecuteNonQuery(); }
            token.ThrowIfCancellationRequested(); ValidateOwner(); transaction.Commit(); return new HeldScope(this, job, id);

            IEnumerable<long> RecordSet(string holder)
            {
                using var read = connection.CreateCommand(); read.Transaction = transaction;
                read.CommandText = "SELECT rrn FROM holder_records WHERE token=$token"; read.Parameters.AddWithValue("$token", holder);
                using var reader = read.ExecuteReader(); while (reader.Read()) yield return reader.GetInt64(0);
            }
            bool Blocks(string member, long row, JobLockMode heldMode, string holder, bool set, bool fairness)
            {
                if (member.Length == 0) return !Compatible(Intent(mode), heldMode) && (!fairness || rows.Any(number => !covered.Contains(number)));
                if (member != resource.Member || Compatible(mode, heldMode)) return false;
                return set ? RecordSet(holder).Any(number => rows.Contains(number) && (!fairness || !covered.Contains(number))) : rows.Contains(row) && (!fairness || !covered.Contains(row));
            }
        }
        catch { ReleaseScope(job, "Parent:" + id); throw; }

        void ValidateOwner() { if (publisher is null) RequireOwner(job); else RequirePublisherOwner(job,publisher); }
    }

    private void RequireStatementObject(JobLockResource resource, SqliteTransaction? publisher)
    {
        if (publisher is null) { EnsureResourceExists(resource); return; }
        using var check = publisher.Connection!.CreateCommand(); check.Transaction = publisher;
        check.CommandText = "SELECT count(*) FROM sys_objects WHERE lib=$lib AND name=$name AND type=$type";
        check.Parameters.AddWithValue("$lib",resource.Library); check.Parameters.AddWithValue("$name",resource.Name); check.Parameters.AddWithValue("$type",resource.Type);
        if (Convert.ToInt64(check.ExecuteScalar()) != 1) throw new CpfException("CPF9801","Lock object no longer exists.");
    }

    private void RequirePublisherOwner(JobKey job, SqliteTransaction publisher)
    {
        // A publisher's hooks may own catalog table/schema locks. Reuse that
        // transaction for live credentials and job state instead of competing.
        if (OperationIdentity.Current is not { } identity || identity.Job != job)
            throw new CpfException("CPF9802","Locks must belong to the executing job.");
        if (publisher.Connection is not { } owner ||
            new SqliteConnectionStringBuilder(owner.ConnectionString).ToString() != new SqliteConnectionStringBuilder(_factory.ConnectionString).ToString())
            throw new ArgumentException("Publisher must belong to the job's catalog.",nameof(publisher));
        new ServiceAuthorization(_factory).RequireCaller(owner,publisher,identity.Principal);
        using var read = owner.CreateCommand(); read.Transaction = publisher;
        read.CommandText = "SELECT cancel_requested FROM sys_jobs WHERE number=$job AND name=$name AND user=$user AND coalesce(user_profile,user)=$owner AND execution_state='Running'";
        read.Parameters.AddWithValue("$job",job.Number); read.Parameters.AddWithValue("$name",job.Name); read.Parameters.AddWithValue("$user",job.User); read.Parameters.AddWithValue("$owner",identity.Principal);
        var state = read.ExecuteScalar();
        if (state is null) throw new CpfException("CPF1241","Lock owner is not an active job.");
        if (Convert.ToInt32(state) != 0) throw new OperationCanceledException("Job cancellation was requested.");
    }
}
