using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed record InteractiveJob(JobKey Job, int Partition, string? GroupName, string Description, bool Suspended);

/// <summary>Durable membership and suspension for one owned terminal connection.</summary>
public sealed class InteractiveSessionStore(SqliteConnectionFactory factory)
{
    private void Require(JobKey job) => new JobDataAreaStore(factory).RequireOwner(job);
    private void RequireActive(JobKey job)
    {
        Require(job);
        if (new JobService(factory).GetRequired(job).Status != JobStatus.Active) throw new CpfException("CPF1312", "This terminal job is suspended.");
    }
    public void Attach(JobKey job)
    {
        RequireActive(job); using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sys_terminal_jobs(job_number,family,partition) SELECT number,$family,0 FROM sys_jobs WHERE number=$job AND type='Interactive' AND status='Active' ON CONFLICT(job_number) DO NOTHING";
        command.Parameters.AddWithValue("$family", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$job", job.Number);
        command.ExecuteNonQuery();
        if (!List(job).Any()) throw new CpfException("CPF1241", "An active interactive terminal job is required.");
    }
    public IReadOnlyList<InteractiveJob> List(JobKey owner)
    {
        Require(owner); using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT j.number,j.name,j.user,t.partition,t.group_name,t.description,j.status FROM sys_terminal_jobs t JOIN sys_jobs j ON j.number=t.job_number WHERE t.family=(SELECT family FROM sys_terminal_jobs WHERE job_number=$job) ORDER BY t.partition,j.number";
        command.Parameters.AddWithValue("$job", owner.Number); using var reader = command.ExecuteReader(); var jobs = new List<InteractiveJob>();
        while (reader.Read()) jobs.Add(new(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6) == "Held"));
        return jobs;
    }
    public void Rename(JobKey owner, string name, string description)
    {
        RequireActive(owner); name = name.ToUpperInvariant();
        if (!ObjectName.IsValid(name) || description.Length > 50 || description.Any(char.IsControl)) throw new CpfException("IPC0131", "Invalid group name or description.");
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$job", owner.Number); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$description", description);
        command.CommandText = "SELECT count(*) FROM sys_terminal_jobs WHERE job_number<>$job AND group_name=$name AND (family,partition)=(SELECT family,partition FROM sys_terminal_jobs WHERE job_number=$job)";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new CpfException("CPF1310", "Group job name is already in use.");
        command.CommandText = "UPDATE sys_terminal_jobs SET group_name=$name,description=$description WHERE job_number=$job";
        if (command.ExecuteNonQuery() != 1) throw new CpfException("CPF1241", "Terminal job is not attached.");
        command.CommandText = "SELECT group_id FROM sys_job_environment WHERE job_number=$job";
        if (command.ExecuteScalar() is not string)
        {
            command.Parameters.AddWithValue("$group", Guid.NewGuid().ToString("N"));
            command.CommandText = "INSERT INTO sys_job_groups(id,owner,gda) SELECT $group,user,CAST(replace(hex(zeroblob(256)),'0',CASE WHEN ccsid IN (37,500,1047) THEN '@' ELSE ' ' END) AS BLOB) FROM sys_jobs WHERE number=$job; UPDATE sys_job_environment SET group_id=$group WHERE job_number=$job"; command.ExecuteNonQuery();
        }
        Audit(command, "terminal.group.changed", owner, null); transaction.Commit();
    }
    internal void Add(JobKey owner, JobKey child, string? groupName, bool secondary = false)
    {
        RequireActive(owner); if (groupName is not null && !ObjectName.IsValid(groupName)) throw new CpfException("IPC0131", "Invalid group job name.");
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$job", owner.Number); command.Parameters.AddWithValue("$child", child.Number);
        command.Parameters.AddWithValue("$name", (object?)groupName ?? DBNull.Value);
        command.CommandText = "SELECT count(*) FROM sys_jobs a JOIN sys_jobs b ON b.number=$child WHERE a.number=$job AND a.type='Interactive' AND b.type='Interactive' AND a.user=b.user AND a.user_profile=b.user_profile AND a.auth_session IS b.auth_session AND a.execution_state='Running' AND b.execution_state='Running' AND a.cancel_requested=0 AND b.cancel_requested=0";
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new CpfException("CPF9802", "Group jobs must belong to the same authenticated owner.");
        command.CommandText = "SELECT count(*) FROM sys_terminal_jobs WHERE family=(SELECT family FROM sys_terminal_jobs WHERE job_number=$job) AND partition=" + (secondary ? "1" : "(SELECT partition FROM sys_terminal_jobs WHERE job_number=$job)");
        var count = Convert.ToInt64(command.ExecuteScalar());
        if (secondary ? count != 0 : count >= 16) throw new CpfException("CPF1311", "Interactive group capacity reached.");
        command.CommandText = "INSERT INTO sys_terminal_jobs(job_number,family,partition,group_name) SELECT $child,family," + (secondary ? "1" : "partition") + ",$name FROM sys_terminal_jobs WHERE job_number=$job";
        try { if (command.ExecuteNonQuery() != 1) throw new CpfException("CPF1241", "Terminal job is not attached."); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) { throw new CpfException("CPF1310", "Group job name is already in use."); }
        SwitchCore(command, owner, child); transaction.Commit();
    }
    public void Transfer(JobKey owner, JobKey target)
    {
        RequireActive(owner); using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$job", owner.Number); command.Parameters.AddWithValue("$child", target.Number);
        SwitchCore(command, owner, target); transaction.Commit();
    }
    private static void SwitchCore(SqliteCommand command, JobKey owner, JobKey target)
    {
        command.CommandText = "SELECT count(*) FROM sys_terminal_jobs a JOIN sys_terminal_jobs b ON a.family=b.family JOIN sys_jobs j ON j.number=b.job_number WHERE a.job_number=$job AND b.job_number=$child AND j.execution_state='Running' AND j.cancel_requested=0";
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new CpfException("CPF9802", "Target is not a live job on this terminal.");
        command.CommandText = "UPDATE sys_jobs SET status='Held' WHERE number IN (SELECT job_number FROM sys_terminal_jobs WHERE family=(SELECT family FROM sys_terminal_jobs WHERE job_number=$job)) AND execution_state='Running' AND number<>$child; UPDATE sys_jobs SET status='Active' WHERE number=$child"; command.ExecuteNonQuery();
        Audit(command, "terminal.transferred", owner, target);
    }
    private static void Audit(SqliteCommand command, string kind, JobKey owner, JobKey? target)
    {
        command.Parameters.Clear(); command.CommandText = "INSERT INTO sys_events(at,kind,principal,job,payload) VALUES($at,$kind,$principal,$owner,json_object('target',$target))";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$principal", owner.User);
        command.Parameters.AddWithValue("$owner", owner.ToString()); command.Parameters.AddWithValue("$target", (object?)target?.ToString() ?? DBNull.Value); command.ExecuteNonQuery();
    }
}
