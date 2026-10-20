using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

/// <summary>Saved SQL source lives in the ordinary *QMQRY payload: no private side tables or hidden authority.</summary>
public sealed class QueryManagementStore(SqliteConnectionFactory factory)
{
    private readonly SqliteObjectStore _objects = new(factory);
    private readonly ServiceAuthorization _authorization = new(factory);
    public ObjectDescriptor? Find(string library, string name, AuthorityBit? required = null)
    {
        var key = new JobTemporaryLibrary(factory).Resolve(new(library, name), ObjectType.QueryManagementQuery);
        _authorization.RequireObject("QSYS", key.Library, ObjectType.Library, AuthorityBit.ObjectOperate, checkLibrary: false);
        var descriptor = _objects.GetForAuthorization(library, name, ObjectType.QueryManagementQuery);
        if (descriptor is null) return null;
        _authorization.RequireObject(library, name, ObjectType.QueryManagementQuery, required ?? Authorities.UseBits);
        using var connection = factory.Open(); RequireCaller(connection, null);
        return descriptor.Snapshot(new(library, name));
    }
    public string Read(string library, string name, bool forExecution = false)
    {
        var descriptor = Find(library, name) ?? throw new CpfException("CPF9801", "Query management query not found.");
        if (descriptor.Attribute != "SQL" || descriptor.Format != "IPCQMQRY1" || descriptor.Source is null)
            throw new CpfException("QWM2701", "Query management object has no supported SQL source image.");
        if (descriptor.ExtendedAttributes?.GetValueOrDefault("ipc.query.sortSequence") is not ("*HEX" or "*JOB" or "*JOBRUN") ||
            descriptor.ExtendedAttributes?.GetValueOrDefault("ipc.query.language") is not ("*JOB" or "*JOBRUN"))
            throw new CpfException("QWM2701", "Query sort/language metadata is unsupported or invalid.");
        if (forExecution)
        {
            using var certificates = new CertificateService(factory);
            new ObjectSigningService(factory, new ContentTrustService(factory, certificates)).RequireExecutable(descriptor);
        }
        return QueryManagementSql.SourceRecords(descriptor.Source, descriptor.Ccsid);
    }
    public void Create(string library, string name, string source, int ccsid = 37, string text = "", string authority = "*LIBCRTAUT", bool replace = true,
        string sortSequence = "*JOBRUN", string language = "*JOBRUN", CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, factory.ShutdownToken);
        cancellationToken = lifetime.Token;
        cancellationToken.ThrowIfCancellationRequested(); source = QueryManagementSql.SourceRecords(source, ccsid);
        if (text.Length > 50 || text.Any(char.IsControl)) throw new CpfException("IPC0003", "Query description requires at most 50 printable characters.");
        if (sortSequence is not ("*HEX" or "*JOB" or "*JOBRUN") || language is not ("*JOB" or "*JOBRUN"))
            throw new CpfException("QWM2701", "Non-hex query sort sequences and language collations remain unfinished.");
        var key = new JobTemporaryLibrary(factory).Resolve(new(library, name), ObjectType.QueryManagementQuery);
        var container = _objects.GetRequired("QSYS", key.Library, ObjectType.Library);
        if (authority == "*LIBCRTAUT") authority = container.ExtendedAttributes?.GetValueOrDefault("ipc.library.createAuthority") ?? "*CHANGE";
        var list = ObjectName.IsValid(authority); AuthorityBit bits;
        if (list) _authorization.RequireObject("QSYS", authority, ObjectType.AuthorizationList, AuthorityBit.ObjectReference);
        try { bits = list ? AuthorityBit.None : Authorities.FromLevel(authority); }
        catch (ArgumentException) { throw new CpfException("IPC0003", "Invalid query public authority."); }
        var descriptor = new ObjectDescriptor { Key = key, ObjectType = ObjectType.QueryManagementQuery, Attribute = "SQL", Format = "IPCQMQRY1", Source = source,
            Ccsid = ccsid, Description = text, Owner = OperationIdentity.Current?.Principal ?? "QSECOFR", PublicAuthority = bits, UseAuthorizationListPublicAuthority = list,
            ExtendedAttributes = new Dictionary<string, string> { ["ipc.query.sortSequence"] = sortSequence, ["ipc.query.language"] = language } };
        using var allocation = OperationIdentity.Current?.Job is { } job ? new JobLockStore(factory).EnterCommand(job, cancellationToken) : null;
        new JobLockStore(factory).RequireObjectMutation(library, name, ObjectType.QueryManagementQuery);
        using var connection = factory.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, cancellationToken);
        RequireCaller(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sys_objects WHERE lib=$lib AND name=$name AND type='*QMQRY'";
        command.Parameters.AddWithValue("$lib", key.Library); command.Parameters.AddWithValue("$name", key.Name.Value);
        var exists = Convert.ToInt64(command.ExecuteScalar()) != 0;
        if (exists)
        {
            if (!replace) throw new CpfException("QWM2706", "Query already exists; specify REPLACE(*YES).");
            _authorization.RequireObject(library, name, ObjectType.QueryManagementQuery, AuthorityBit.ObjectManagement | AuthorityBit.ObjectExist);
            command.CommandText = "UPDATE sys_objects SET source=$source,ccsid=$ccsid,description=$text,attribute='SQL',format='IPCQMQRY1',changed=$at,attrs=json_set(json_remove(coalesce(attrs,'{}'),'$.\"ipc.signature\"'),'$.\"ipc.query.sortSequence\"',$sort,'$.\"ipc.query.language\"',$language) WHERE lib=$lib AND name=$name AND type='*QMQRY'";
            command.Parameters.AddWithValue("$source", source); command.Parameters.AddWithValue("$ccsid", ccsid); command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$sort", sortSequence); command.Parameters.AddWithValue("$language", language); command.ExecuteNonQuery();
        }
        else
        {
            _objects.Create(descriptor, connection, transaction);
            if (list)
            {
                command.CommandText = "INSERT INTO sys_authorities(lib,name,type,holder,is_authl,bits) VALUES($lib,$name,'*QMQRY',$list,1,$bits)";
                command.Parameters.AddWithValue("$list", authority); command.Parameters.AddWithValue("$bits", (int)Authorities.AllBits); command.ExecuteNonQuery();
            }
        }
        RequireCaller(connection, transaction); cancellationToken.ThrowIfCancellationRequested();
        command.Parameters.Clear(); command.CommandText = "INSERT INTO sys_events(kind,at,principal,job,payload) VALUES('query.created',strftime('%Y-%m-%dT%H:%M:%fZ','now'),$owner,ipc_job(),$payload)";
        command.Parameters.AddWithValue("$owner", OperationIdentity.Current?.Principal ?? "*SYSTEM");
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { library, name, replaced = exists })); command.ExecuteNonQuery(); transaction.Commit();
    }
    private void RequireCaller(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (OperationIdentity.Current is not { } identity) return;
        _authorization.RequireCaller(connection, transaction, identity.Principal);
        if (identity.Job is not { } job) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sys_jobs WHERE number=$number AND name=$name AND user=$user AND coalesce(user_profile,user)=$owner AND execution_state='Running' AND cancel_requested=0";
        JobRecordTransactions.BindJob(command, job); command.Parameters.AddWithValue("$owner", identity.Principal);
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new CpfException("CPF1241", "Query-management job is no longer active.");
    }
}
