using System.Text;
using System.Text.RegularExpressions;
using Ipc.Core.Messages;
using Ipc.Core.Security;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Messages;

public sealed record SystemReplyEntry(int Sequence,string MessageId="*ANY",byte[]? Comparison=null,int Start=1,int Ccsid=37,string Reply="*DFT");

public sealed class SystemReplyList(SqliteConnectionFactory factory)
{
    private void RequireChange()
    {
        if(OperationIdentity.Current?.Principal=="QPGMR")
            new ServiceAuthorization(factory).RequireRunAs("QPGMR");
        else new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.AllObject,allowAdopted:false);
    }
    public static void Validate(SystemReplyEntry entry)
    {
        if(entry.Sequence is <1 or >9999 || entry.Start is <1 or >999 || entry.Comparison?.Length is <1 or >28 ||
            entry.Ccsid!=65535&&!CodePage.IsSupported(entry.Ccsid) || entry.Reply is null || entry.Reply.Length>32 || entry.Reply.Any(char.IsControl) ||
            entry.MessageId is null || entry.MessageId!="*ANY"&&!Regex.IsMatch(entry.MessageId,@"\A[A-Z][A-Z0-9]{2}[0-9A-F]{4}\z"))
            throw new CpfException("IPC0003","Invalid system reply list entry.");
    }
    public IReadOnlyList<SystemReplyEntry> List()
    {if(OperationIdentity.Current is { } caller)new ServiceAuthorization(factory).RequireRunAs(caller.Principal);using var connection=factory.Open();using var transaction=connection.BeginTransaction(deferred:true);return Read(connection,transaction);}
    internal static IReadOnlyList<SystemReplyEntry> Read(SqliteConnection connection,SqliteTransaction transaction)
    {
        using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="SELECT sequence,message_id,comparison,start,ccsid,reply FROM sys_reply_list ORDER BY sequence";
        using var reader=command.ExecuteReader();var rows=new List<SystemReplyEntry>();
        while(reader.Read()){var entry=new SystemReplyEntry(reader.GetInt32(0),reader.GetString(1),reader.IsDBNull(2)?null:(byte[])reader[2],reader.GetInt32(3),reader.GetInt32(4),reader.GetString(5));Validate(entry);rows.Add(entry);}return rows;
    }
    public void Put(SystemReplyEntry entry,bool replace=false)=>Write(entry,replace,null);
    public void Change(int sequence,Func<SystemReplyEntry,SystemReplyEntry> change)
    {ArgumentNullException.ThrowIfNull(change);Write(new(sequence),true,change);}
    private void Write(SystemReplyEntry entry,bool replace,Func<SystemReplyEntry,SystemReplyEntry>? change)
    {
        Validate(entry);RequireChange();using var connection=factory.Open();using var transaction=connection.BeginTransaction(deferred:false);RequireChange();
        if(change is not null)
        {
            var previous=Read(connection,transaction).SingleOrDefault(e=>e.Sequence==entry.Sequence)??throw new CpfException("IPC0003","Reply list entry not found.");
            entry=change(previous);Validate(entry);
            if(entry.Sequence!=previous.Sequence)throw new CpfException("IPC0003","CHGRPYLE cannot change the sequence number.");
        }
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText=replace?"UPDATE sys_reply_list SET message_id=$id,comparison=$data,start=$start,ccsid=$ccsid,reply=$reply WHERE sequence=$sequence":"INSERT INTO sys_reply_list(sequence,message_id,comparison,start,ccsid,reply) VALUES($sequence,$id,$data,$start,$ccsid,$reply)";
        command.Parameters.AddWithValue("$sequence",entry.Sequence);command.Parameters.AddWithValue("$id",entry.MessageId);command.Parameters.AddWithValue("$data",(object?)entry.Comparison??DBNull.Value);
        command.Parameters.AddWithValue("$start",entry.Start);command.Parameters.AddWithValue("$ccsid",entry.Ccsid);command.Parameters.AddWithValue("$reply",entry.Reply);
        try{if(command.ExecuteNonQuery()!=1)throw new CpfException("IPC0003","Reply list entry not found.");}
        catch(SqliteException error)when(error.SqliteErrorCode==19){throw new CpfException("IPC0003","Reply list sequence already exists.");}transaction.Commit();
    }
    public void Remove(int sequence)
    {
        if(sequence is <1 or >9999)throw new CpfException("IPC0003","SEQNBR requires 1–9999.");RequireChange();
        using var connection=factory.Open();using var transaction=connection.BeginTransaction(deferred:false);RequireChange();
        using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="DELETE FROM sys_reply_list WHERE sequence=$sequence";command.Parameters.AddWithValue("$sequence",sequence);
        if(command.ExecuteNonQuery()!=1)throw new CpfException("IPC0003","Reply list entry not found.");transaction.Commit();
    }
    internal static SystemReplyEntry? Match(SqliteConnection connection,SqliteTransaction transaction,string id,ProgramBuffer replacement)
    {
        foreach(var entry in Read(connection,transaction))
        {
            var length=entry.MessageId=="*ANY"?0:entry.MessageId.EndsWith("0000",StringComparison.Ordinal)?3:entry.MessageId.EndsWith("00",StringComparison.Ordinal)?5:7;
            if(length>0&&(id.Length!=7||!id.AsSpan(0,length).SequenceEqual(entry.MessageId.AsSpan(0,length))))continue;
            if(entry.Comparison is { } bytes)
            {
                try
                {
                    if(entry.Ccsid!=65535 && entry.Ccsid!=replacement.Ccsid)
                    {
                        if(!CodePage.IsSupported(replacement.Ccsid))continue;
                        var decoder=(Encoding)CodePage.FromCcsid(entry.Ccsid).Clone();decoder.DecoderFallback=DecoderFallback.ExceptionFallback;
                        var encoder=(Encoding)CodePage.FromCcsid(replacement.Ccsid).Clone();encoder.EncoderFallback=EncoderFallback.ExceptionFallback;
                        bytes=encoder.GetBytes(decoder.GetString(bytes));
                    }
                }
                catch(ArgumentException){continue;}
                var data=replacement.ToArray();if(entry.Start-1>data.Length||bytes.Length>data.Length-entry.Start+1||!data.AsSpan(entry.Start-1,bytes.Length).SequenceEqual(bytes))continue;
            }
            return entry;
        }
        return null;
    }
}
