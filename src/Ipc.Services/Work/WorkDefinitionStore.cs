using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed record JobClassDefinition(string Library, string Name, int RunPriority = 50, int TimeSliceMilliseconds = 2000);
public sealed record JobDescriptionDefinition(string Library, string Name, string JobQueue, int Priority = 9,
    string? RunAs = null, string RoutingData = "QCMDB", string? CurrentLibrary = null, string LibraryMode = "CURRENT", IReadOnlyList<string>? Libraries = null);

public sealed class WorkDefinitionStore(SqliteConnectionFactory factory)
{
    internal void SeedClasses()
    {
        if (Class("QSYS", "QBATCH") is null) PutClass(new("QSYS", "QBATCH"), owner: "QSYS");
        if (Class("QSYS", "QINTER") is null) PutClass(new("QSYS", "QINTER", 20), owner: "QSYS");
    }
    internal void SeedJobDescriptions()
    {
        if (JobDescription("QGPL", "QBATCH") is null) PutJobDescription(new("QGPL", "QBATCH", "QUSRSYS/QBATCH"), owner: "QSYS");
    }

    public JobClassDefinition? Class(string library, string name)
    {
        if (new SqliteObjectStore(factory).Get(library, name, ObjectType.Class) is null) return null;
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_priority,time_slice_ms FROM sys_classes WHERE library=$lib AND name=$name";
        Bind(command, library, name); using var reader = command.ExecuteReader();
        return reader.Read() ? new(library, name, reader.GetInt32(0), reader.GetInt32(1)) : null;
    }

    public JobDescriptionDefinition? JobDescription(string library, string name)
    {
        if (new SqliteObjectStore(factory).Get(library, name, ObjectType.JobDescription) is null) return null;
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT queue_lib||'/'||queue_name,priority,run_as,routing_data,current_library,library_mode FROM sys_job_descriptions WHERE library=$lib AND name=$name";
        Bind(command, library, name);
        JobDescriptionDefinition description;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            description = new(library, name, reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5));
        }
        command.CommandText = "SELECT library FROM sys_jobd_libraries WHERE jobd_lib=$lib AND jobd_name=$name ORDER BY ordinal";
        var libraries = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) libraries.Add(reader.GetString(0));
        return description with { Libraries = libraries.ToArray() };
    }

    public void PutClass(JobClassDefinition value, bool replace = false, string? owner = null)
    {
        if (value.RunPriority is < 1 or > 99 || value.TimeSliceMilliseconds is < 1 or > 10000)
            throw Invalid("Class run priority must be 1–99 and time slice 1–10000 milliseconds.");
        var descriptor = Descriptor(value.Library, value.Name, ObjectType.Class, owner);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        Authorize(descriptor, replace);
        EnsureDescriptor(descriptor, connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sys_classes(library,name,run_priority,time_slice_ms) VALUES($lib,$name,$priority,$slice)
            """ + (replace ? " ON CONFLICT(library,name) DO UPDATE SET run_priority=excluded.run_priority,time_slice_ms=excluded.time_slice_ms" : "");
        Bind(command, value.Library, value.Name); command.Parameters.AddWithValue("$priority", value.RunPriority);
        command.Parameters.AddWithValue("$slice", value.TimeSliceMilliseconds); command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void PutJobDescription(JobDescriptionDefinition value, bool replace = false, string? owner = null)
    {
        var queue = QualifiedName.Parse(value.JobQueue.ToUpperInvariant(), "QUSRSYS");
        if (value.Priority is < 0 or > 9 || value.RoutingData.Length > 80 || value.LibraryMode is not ("CURRENT" or "SYSVAL" or "EXPLICIT") ||
            value.Libraries?.Count > 250 || value.Libraries?.Distinct().Count() != value.Libraries?.Count)
            throw Invalid("Invalid job-description priority, routing data or library list.");
        var descriptor = Descriptor(value.Library, value.Name, ObjectType.JobDescription, owner);
        var authorization = new ServiceAuthorization(factory);
        authorization.RequireObject(queue.Library, queue.Name.Value, ObjectType.JobQueue, Authorities.UseBits);
        if (value.RunAs is not null) authorization.RequireRunAs(value.RunAs);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        Authorize(descriptor, replace);
        EnsureDescriptor(descriptor, connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sys_job_descriptions(library,name,queue_lib,queue_name,priority,run_as,routing_data,current_library,library_mode)
            VALUES($lib,$name,$qlib,$qname,$priority,$user,$routing,$current,$mode)
            """ + (replace ? """
             ON CONFLICT(library,name) DO UPDATE SET queue_lib=excluded.queue_lib,queue_name=excluded.queue_name,priority=excluded.priority,
               run_as=excluded.run_as,routing_data=excluded.routing_data,current_library=excluded.current_library,library_mode=excluded.library_mode
            """ : "");
        Bind(command, value.Library, value.Name); command.Parameters.AddWithValue("$qlib", queue.Library); command.Parameters.AddWithValue("$qname", queue.Name.Value);
        command.Parameters.AddWithValue("$priority", value.Priority); command.Parameters.AddWithValue("$user", (object?)value.RunAs ?? DBNull.Value);
        command.Parameters.AddWithValue("$routing", value.RoutingData); command.Parameters.AddWithValue("$current", (object?)value.CurrentLibrary ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", value.LibraryMode); command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM sys_jobd_libraries WHERE jobd_lib=$lib AND jobd_name=$name"; command.ExecuteNonQuery();
        if (value.LibraryMode == "EXPLICIT")
        {
            foreach (var (library, index) in (value.Libraries ?? Array.Empty<string>()).Select((item, index) => (item, index)))
            {
                using var member = connection.CreateCommand(); member.Transaction = transaction;
                member.CommandText = "INSERT INTO sys_jobd_libraries(jobd_lib,jobd_name,ordinal,library) VALUES($lib,$name,$ordinal,$library)";
                Bind(member, value.Library, value.Name); member.Parameters.AddWithValue("$ordinal", index); member.Parameters.AddWithValue("$library", library);
                member.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    private void Authorize(ObjectDescriptor descriptor, bool replace)
    {
        descriptor.ValidateIdentity();
        var existing = new SqliteObjectStore(factory).GetForAuthorization(descriptor.Library, descriptor.Name, descriptor.ObjectType);
        if (existing is null && replace) throw Invalid("Work definition does not exist.");
        if (existing is not null && !replace) throw Invalid("Work definition already exists.");
        var authorization = new ServiceAuthorization(factory);
        if (existing is null) authorization.RequireCreate(descriptor);
        else authorization.RequireObject(descriptor.Library, descriptor.Name, descriptor.ObjectType, AuthorityBit.ObjectManagement);
    }
    private static ObjectDescriptor Descriptor(string library, string name, string type, string? owner) => new()
    { Key = new(library, name), ObjectType = type, Owner = owner ?? OperationIdentity.Current?.Principal ?? "QSYS", PublicAuthority = Authorities.UseBits };
    private void EnsureDescriptor(ObjectDescriptor descriptor, SqliteConnection connection, SqliteTransaction transaction)
    {
        using var exists = connection.CreateCommand(); exists.Transaction = transaction;
        exists.CommandText = "SELECT count(*) FROM sys_objects WHERE lib=$lib AND name=$name AND type=$type";
        Bind(exists, descriptor.Library, descriptor.Name); exists.Parameters.AddWithValue("$type", descriptor.ObjectType);
        if (Convert.ToInt64(exists.ExecuteScalar()) == 0) new SqliteObjectStore(factory).Create(descriptor, connection, transaction);
    }
    private static void Bind(SqliteCommand command, string library, string name)
    { command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name); }
    private static CpfException Invalid(string message) => new("IPC0120", message);
}
