using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

/// <summary>Maps the job's QTEMP name to a private catalog namespace. Backing names confer no access.</summary>
public sealed class JobTemporaryLibrary(SqliteConnectionFactory factory)
{
    private sealed record ReplyAccess(string Catalog, OperationIdentity Caller, QualifiedName Queue);
    private static readonly AsyncLocal<ReplyAccess?> PrivateReply = new();

    public QualifiedName Resolve(QualifiedName key, string type)
    {
        if (type == ObjectType.Library && key.Library == "QSYS")
            return new("QSYS", ResolveLibraryCore(key.Name.Value, key.Name.Value, type));
        return new(ResolveLibraryCore(key.Library, key.Name.Value, type), key.Name.Value);
    }

    public string ResolveLibrary(string library) => ResolveLibraryCore(library, null, null);

    private string ResolveLibraryCore(string library, string? name, string? type)
    {
        if (library == LibraryNames.QTemp)
        {
            var job = OperationIdentity.Current?.Job ?? throw new CpfException("CPF1241", "QTEMP requires an executing job.");
            new JobDataAreaStore(factory).RequireOwner(job);
            using var connection = factory.Open(); using var query = connection.CreateCommand();
            query.CommandText = "SELECT library FROM sys_job_temp_libraries WHERE job_number=$job";
            query.Parameters.AddWithValue("$job", job.Number);
            return query.ExecuteScalar() as string ?? throw new CpfException("CPF1241", "The job's QTEMP library is unavailable.");
        }
        RequirePrivateAccess(library, name, type);
        return library;
    }

    private void RequirePrivateAccess(string library, string? name = null, string? type = null)
    {
        if (!library.StartsWith("@T", StringComparison.Ordinal) || OperationIdentity.Current is not { } identity) return;
        using var connection = factory.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT j.number,j.name,j.user FROM sys_job_temp_libraries t JOIN sys_jobs j ON j.number=t.job_number WHERE t.library=$library";
        query.Parameters.AddWithValue("$library", library);
        JobKey? owner = null;
        using (var reader = query.ExecuteReader()) if (reader.Read()) owner = new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
        if (owner is null) return;
        if (identity.Job != owner)
        {
            // Only the exact persisted inquiry reply target and its parent allocation
            // can cross a job boundary. Untyped library lookups never gain this access.
            if (PrivateReply.Value is { } access && access.Catalog == factory.ConnectionString && ReferenceEquals(access.Caller, identity) &&
                access.Queue.Library == library && (type == ObjectType.MessageQueue && access.Queue.Name.Value == name || type == ObjectType.Library && name == library)) return;
            throw new CpfException("CPF9802", "QTEMP belongs to the executing job only.");
        }
        new JobDataAreaStore(factory).RequireOwner(owner.Value);
    }

    internal static IDisposable? EnterInquiryReply(SqliteConnectionFactory factory, SqliteConnection connection, SqliteTransaction transaction,
        QualifiedName queue, int? senderJob)
    {
        if (queue.Library == LibraryNames.QTemp) throw new CpfException("CPF2469", "The inquiry has no resolved job-private reply target.");
        if (OperationIdentity.Current is not { } caller) return null;
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT t.job_number,j.execution_state,j.cancel_requested FROM sys_job_temp_libraries t JOIN sys_jobs j ON j.number=t.job_number WHERE t.library=$library";
        query.Parameters.AddWithValue("$library", queue.Library);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetInt32(0) != senderJob || reader.GetString(1) != "Running" || reader.GetInt32(2) != 0)
            throw new CpfException("CPF2469", "The inquiry's private reply job is no longer available.");
        var previous = PrivateReply.Value; PrivateReply.Value = new(factory.ConnectionString, caller, queue);
        return new ReplyScope(previous);
    }
    private sealed class ReplyScope(ReplyAccess? previous) : IDisposable
    {
        public void Dispose() => PrivateReply.Value = previous;
    }

    public bool IsTemporary(string library)
    {
        if (library == LibraryNames.QTemp) return true;
        if (!library.StartsWith("@T", StringComparison.Ordinal)) return false;
        RequirePrivateAccess(library);
        using var connection = factory.Open(); return LogicalLibrary(connection, null, library) == LibraryNames.QTemp;
    }

    internal static string LogicalLibrary(SqliteConnection connection, SqliteTransaction? transaction, string library)
    {
        if (!library.StartsWith("@T", StringComparison.Ordinal)) return library;
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT 1 FROM sys_job_temp_libraries WHERE library=$library"; query.Parameters.AddWithValue("$library", library);
        return query.ExecuteScalar() is null ? library : LibraryNames.QTemp;
    }

    public QualifiedName Visible(QualifiedName key, string type)
    {
        var library = type == ObjectType.Library && key.Library == "QSYS" ? key.Name.Value : key.Library;
        if (!library.StartsWith("@T", StringComparison.Ordinal) || OperationIdentity.Current?.Job is not { } job) return key;
        using var connection = factory.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT 1 FROM sys_job_temp_libraries WHERE library=$library AND job_number=$job";
        query.Parameters.AddWithValue("$library", library); query.Parameters.AddWithValue("$job", job.Number);
        if (query.ExecuteScalar() is null) return key;
        return type == ObjectType.Library && key.Library == "QSYS" ? new("QSYS", LibraryNames.QTemp) : new(LibraryNames.QTemp, key.Name.Value);
    }

    internal void RequirePermanentLibrary(string library)
    {
        if (IsTemporary(library)) throw new CpfException("IPC0143", "The job's QTEMP library cannot be deleted, renamed or used as a permanent library setting.");
    }

    internal static void Initialize(SqliteConnection connection, SqliteTransaction transaction, Job job)
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT 1 FROM sys_job_temp_libraries WHERE job_number=$job"; query.Parameters.AddWithValue("$job", job.Key.Number);
        if (query.ExecuteScalar() is not null) return;
        var library = "@T" + job.Key.Number.ToString("X8", CultureInfo.InvariantCulture);
        query.CommandText = "SELECT 1 FROM sys_objects WHERE lib='QSYS' AND name=$library AND type='*LIB'";
        query.Parameters.AddWithValue("$library", library);
        for (var attempt = 0; query.ExecuteScalar() is not null; attempt++)
        {
            if (attempt == 32) throw new CpfException("IPC0143", "Could not allocate a private QTEMP namespace.");
            library = "@T" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(); query.Parameters["$library"].Value = library;
        }
        // This is runtime-owned job initialization in the job's creation transaction.
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO sys_objects(lib,name,type,owner,created,changed,description,ccsid,attribute,public_authority)
                VALUES('QSYS',$library,'*LIB',$owner,$at,$at,'Job temporary library',$ccsid,'*PROD',0);
                INSERT INTO sys_job_temp_libraries(job_number,library) VALUES($job,$library);
                """;
            insert.Parameters.AddWithValue("$library", library); insert.Parameters.AddWithValue("$job", job.Key.Number);
            insert.Parameters.AddWithValue("$owner", job.UserProfile ?? job.Key.User); insert.Parameters.AddWithValue("$ccsid", job.Ccsid);
            insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); insert.ExecuteNonQuery();
        }
    }

    internal void InitializeExistingJobs()
    {
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT number,name,user,coalesce(user_profile,user),ccsid FROM sys_jobs WHERE execution_state IN ('Queued','Running') AND NOT EXISTS(SELECT 1 FROM sys_job_temp_libraries t WHERE t.job_number=sys_jobs.number)";
        var jobs = new List<Job>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) jobs.Add(new Job { Key = new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)), UserProfile = reader.GetString(3), Ccsid = reader.GetInt32(4) });
        foreach (var job in jobs) Initialize(connection, transaction, job);
        transaction.Commit();
    }

    internal static void Cleanup(SqliteConnection connection, SqliteTransaction transaction, int jobNumber)
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT library FROM sys_job_temp_libraries WHERE job_number=$job"; query.Parameters.AddWithValue("$job", jobNumber);
        if (query.ExecuteScalar() is not string library) return;
        query.Parameters.AddWithValue("$library", library);
        query.CommandText = "SELECT count(*) FROM sys_object_dependencies WHERE target_lib=$library AND source_lib<>$library";
        if (Convert.ToInt64(query.ExecuteScalar()) != 0) throw new CpfException("IPC0201", "QTEMP has an external dependent; cleanup cannot discard a permanent dependency.");
        Ipc.Services.Messages.MessageQueueStore.MarkPrivateSnapshots(connection, transaction, library);
        query.CommandText = "SELECT name,type FROM sys_objects WHERE lib=$library ORDER BY name,type";
        var contents = new List<(QualifiedName Key, string Type)>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) contents.Add((new(library, reader.GetString(0)), reader.GetString(1)));
        query.CommandText = "DELETE FROM sys_object_dependencies WHERE source_lib=$library AND target_lib=$library; DELETE FROM sys_menu_options WHERE library=$library";
        query.ExecuteNonQuery();
        while (contents.Count != 0)
        {
            var removed = false; CpfException? blocked = null;
            for (var index = contents.Count - 1; index >= 0; index--)
            {
                var item = contents[index];
                try { ObjectCatalogOperations.DeleteCore(item.Key, item.Type, connection, transaction); contents.RemoveAt(index); removed = true; }
                catch (CpfException error) when (error.MessageId == "IPC0201") { blocked = error; }
            }
            if (!removed) throw blocked ?? new CpfException("IPC0201", "QTEMP contains an object that cannot be deleted.");
        }
        query.CommandText = "DELETE FROM sys_job_temp_libraries WHERE job_number=$job"; query.ExecuteNonQuery();
        ObjectCatalogOperations.DeleteCore(new("QSYS", library), ObjectType.Library, connection, transaction);
    }

    public int RecoverEndedJobs()
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.JobControl);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT t.job_number FROM sys_job_temp_libraries t JOIN sys_jobs j ON j.number=t.job_number WHERE j.execution_state NOT IN ('Queued','Running')";
        var jobs = new List<int>(); using (var reader = query.ExecuteReader()) while (reader.Read()) jobs.Add(reader.GetInt32(0));
        foreach (var job in jobs) Cleanup(connection, transaction, job);
        transaction.Commit(); return jobs.Count;
    }
}
