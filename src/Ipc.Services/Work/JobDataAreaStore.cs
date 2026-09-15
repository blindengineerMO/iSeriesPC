using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public enum JobDataArea { Local, Group, Initialization }

public sealed class JobDataAreaStore(SqliteConnectionFactory factory)
{
    public byte[] Read(JobKey job, JobDataArea area)
    {
        RequireOwner(job);
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        Bind(command, job); command.CommandText = ReadSql(area);
        return command.ExecuteScalar() is byte[] data ? data : throw new CpfException("CPF1015", "Job data area is unavailable.");
    }
    public void Write(JobKey job, JobDataArea area, int offset, ReadOnlySpan<byte> value)
    {
        RequireOwner(job);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction; Bind(command, job);
        command.CommandText = ReadSql(area);
        var data = command.ExecuteScalar() as byte[] ?? throw new CpfException("CPF1015", "Job data area is unavailable.");
        if (offset < 0 || offset > data.Length || value.Length > data.Length - offset) throw new CpfException("IPC0126", "Data-area range is out of bounds.");
        value.CopyTo(data.AsSpan(offset)); command.Parameters.AddWithValue("$data", data);
        command.CommandText = area == JobDataArea.Group
            ? "UPDATE sys_job_groups SET gda=$data WHERE id=(SELECT group_id FROM sys_job_environment WHERE job_number=$job)"
            : "UPDATE sys_job_environment SET " + (area == JobDataArea.Local ? "lda" : "pda") + "=$data WHERE job_number=$job";
        command.ExecuteNonQuery(); transaction.Commit();
    }
    public string? Group(JobKey job)
    {
        RequireOwner(job); using var connection = factory.Open(); using var command = connection.CreateCommand(); Bind(command, job);
        command.CommandText = "SELECT group_id FROM sys_job_environment WHERE job_number=$job"; return command.ExecuteScalar() as string;
    }
    internal void RequireOwner(JobKey job)
    {
        if (OperationIdentity.Current is { } identity && identity.Job != job)
            throw new CpfException("CPF9802", "The job environment belongs to the executing job only.");
        new ServiceAuthorization(factory).RequireJob(job);
        using var connection = factory.Open(); using var command = connection.CreateCommand(); Bind(command, job);
        command.CommandText = "SELECT cancel_requested FROM sys_jobs WHERE number=$job AND name=$name AND user=$user AND execution_state='Running'";
        var state = command.ExecuteScalar();
        if (state is not null && Convert.ToInt32(state) != 0) throw new OperationCanceledException("Job cancellation was requested.");
        if (state is null) throw new CpfException("CPF1241", "Job environment is not active.");
    }
    internal static void Initialize(SqliteConnection connection, SqliteTransaction transaction, int target, JobKey? parent, byte[]? pip, bool groupChild)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$target", target);
        if (parent is { } source)
        {
            Bind(command, source);
            command.CommandText = "SELECT count(*) FROM sys_jobs WHERE number=$job AND name=$name AND user=$user AND execution_state='Running' AND cancel_requested=0";
            if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new CpfException("CPF1241", "Submitting job ended before environment inheritance.");
            if (groupChild)
            {
                command.CommandText = "SELECT type||':'||coalesce(user_profile,user) FROM sys_jobs WHERE number=$job";
                if (command.ExecuteScalar() as string != "Interactive:" + source.User) throw new CpfException("IPC0126", "Group jobs require an interactive parent.");
                command.CommandText = "SELECT group_id FROM sys_job_environment WHERE job_number=$job";
                var group = command.ExecuteScalar() as string;
                if (group is null)
                {
                    group = Guid.NewGuid().ToString("N"); command.Parameters.AddWithValue("$group", group);
                    command.CommandText = "INSERT INTO sys_job_groups(id,owner,gda) VALUES($group,$user,$blank); UPDATE sys_job_environment SET group_id=$group WHERE job_number=$job";
                    command.Parameters.AddWithValue("$blank", Enumerable.Repeat(Blank(connection, transaction, source.Number), 512).ToArray()); command.ExecuteNonQuery();
                }
                else command.Parameters.AddWithValue("$group", group);
                command.CommandText = "UPDATE sys_job_environment SET group_id=$group WHERE job_number=$target"; command.ExecuteNonQuery();
            }
            else
            {
                command.CommandText = "UPDATE sys_job_environment SET lda=(SELECT lda FROM sys_job_environment WHERE job_number=$job) WHERE job_number=$target";
                command.ExecuteNonQuery();
            }
        }
        command.CommandText = "UPDATE sys_job_environment SET pip=$pip WHERE job_number=$target";
        command.Parameters.AddWithValue("$pip", (object?)pip ?? DBNull.Value); command.ExecuteNonQuery();
    }
    internal static void StartRouting(SqliteConnection connection, SqliteTransaction transaction, int job)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT pip FROM sys_job_environment WHERE job_number=$job"; command.Parameters.AddWithValue("$job", job);
        var pip = command.ExecuteScalar() as byte[]; var pda = Enumerable.Repeat(Blank(connection, transaction, job), 2000).ToArray();
        pip?.CopyTo(pda, 0); command.Parameters.AddWithValue("$data", pda);
        command.CommandText = "UPDATE sys_job_environment SET pda=$data,pip=NULL WHERE job_number=$job"; command.ExecuteNonQuery();
    }
    private static byte Blank(SqliteConnection connection, SqliteTransaction transaction, int job)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT ccsid FROM sys_jobs WHERE number=$job"; command.Parameters.AddWithValue("$job", job);
        return Convert.ToInt32(command.ExecuteScalar()) is 37 or 500 or 1047 ? (byte)0x40 : (byte)0x20;
    }
    private static string ReadSql(JobDataArea area) => area switch {
        JobDataArea.Local => "SELECT lda FROM sys_job_environment WHERE job_number=$job",
        JobDataArea.Group => "SELECT gda FROM sys_job_groups WHERE id=(SELECT group_id FROM sys_job_environment WHERE job_number=$job)",
        JobDataArea.Initialization => "SELECT pda FROM sys_job_environment WHERE job_number=$job",
        _ => throw new CpfException("IPC0126", "Unknown job data area.") };
    private static void Bind(SqliteCommand command, JobKey job)
    { command.Parameters.AddWithValue("$job", job.Number); command.Parameters.AddWithValue("$name", job.Name); command.Parameters.AddWithValue("$user", job.User); }
}
