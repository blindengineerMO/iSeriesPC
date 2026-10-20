using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Messages;

public sealed class InquiryReplyPolicy(SqliteConnectionFactory factory)
{
    public static bool Valid(string value) => value is "*RQD" or "*DFT" or "*SYSRPYL";
    public string Get(JobKey job)
    {
        new ServiceAuthorization(factory).RequireJob(job);
        using var connection=factory.Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT inquiry_reply FROM sys_jobs WHERE number=$job AND name=$name AND user=$user";
        command.Parameters.AddWithValue("$job",job.Number);command.Parameters.AddWithValue("$name",job.Name);command.Parameters.AddWithValue("$user",job.User);
        return command.ExecuteScalar() as string??throw new CpfException("CPF1241","Job not found.");
    }
    public void Set(JobKey job,string value)
    {
        if(value=="*SAME"){_=Get(job);return;}
        if(!Valid(value))throw new CpfException("IPC0003","INQMSGRPY requires *RQD, *DFT, *SYSRPYL or *SAME.");
        var authorization=new ServiceAuthorization(factory);authorization.RequireJob(job);
        using var connection=factory.Open();using var transaction=connection.BeginTransaction(deferred:false);
        authorization.RequireJob(job);using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="UPDATE sys_jobs SET inquiry_reply=$value WHERE number=$job AND name=$name AND user=$user AND execution_state IN ('Queued','Running') AND cancel_requested=0";
        command.Parameters.AddWithValue("$value",value);command.Parameters.AddWithValue("$job",job.Number);command.Parameters.AddWithValue("$name",job.Name);command.Parameters.AddWithValue("$user",job.User);
        if(command.ExecuteNonQuery()!=1)throw new CpfException("CPF1241","Job not found or already ended.");transaction.Commit();
    }
    internal static string Read(SqliteConnection connection,SqliteTransaction transaction,JobKey job)
    {
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT inquiry_reply FROM sys_jobs WHERE number=$job AND name=$name AND user=$user AND execution_state IN ('Queued','Running') AND cancel_requested=0";
        command.Parameters.AddWithValue("$job",job.Number);command.Parameters.AddWithValue("$name",job.Name);command.Parameters.AddWithValue("$user",job.User);
        return command.ExecuteScalar() as string??throw new CpfException("CPF1241","Sending job has ended.");
    }
}
