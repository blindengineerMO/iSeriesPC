using Ipc.Core.Objects;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

public sealed record JobQueueEntry(string Queue, string Subsystem, int Sequence, int MaximumActive);

public sealed class JobQueueStore
{
    private readonly SqliteConnectionFactory _factory;

    public JobQueueStore(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public void Ensure(string name, string library = "QGPL", string? description = null)
    {
        var key = QualifiedName.Parse(name.ToUpperInvariant(), library.ToUpperInvariant());
        var objects = new SqliteObjectStore(_factory);
        var owner = Ipc.Services.Events.OperationIdentity.Current?.Principal ?? "QSYS";
        var authorization = new Ipc.Services.Security.ServiceAuthorization(_factory);
        if (objects.GetForAuthorization(key.Library, key.Name.Value, ObjectType.JobQueue) is null)
            authorization.RequireCreate(new ObjectDescriptor { Key = key, ObjectType = ObjectType.JobQueue, Owner = owner });
        else authorization.RequireObject(key.Library, key.Name.Value, ObjectType.JobQueue, Authorities.UseBits);
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sys_jobqs (name, library, description)
            VALUES ($name, $library, $description)
            ON CONFLICT(library,name) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$name", key.Name.Value);
        cmd.Parameters.AddWithValue("$library", key.Library);
        cmd.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
        if (cmd.ExecuteNonQuery() == 1)
        {
            cmd.CommandText = "UPDATE sys_objects SET owner=$owner WHERE lib=$library AND name=$name AND type='*JOBQ'";
            cmd.Parameters.AddWithValue("$owner", owner);
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public bool Exists(string name)
    {
        var objects = new SqliteObjectStore(_factory);
        var keys = name.Contains('/') ? new[] { QualifiedName.Parse(name.ToUpperInvariant()) }
            : new[] { new QualifiedName("QGPL", name.ToUpperInvariant()), new QualifiedName("QUSRSYS", name.ToUpperInvariant()) };
        foreach (var key in keys)
        {
            if (objects.Get(key.Library, key.Name.Value, ObjectType.JobQueue) is null) continue;
            using var connection = _factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sys_jobqs WHERE library=$library AND name=$name";
            command.Parameters.AddWithValue("$library", key.Library);
            command.Parameters.AddWithValue("$name", key.Name.Value);
            if (Convert.ToInt64(command.ExecuteScalar()) != 0) return true;
        }
        return false;
    }

    public IReadOnlyList<string> List()
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT library||'/'||name FROM sys_jobqs ORDER BY library,name";
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            var key = QualifiedName.Parse(reader.GetString(0));
            try
            {
                new Ipc.Services.Security.ServiceAuthorization(_factory).RequireObject(key.Library, key.Name.Value, ObjectType.JobQueue, Authorities.UseBits);
                list.Add(key.ToString());
            }
            catch (Ipc.Core.Messages.CpfException ex) when (ex.MessageId == "CPF9802") { }
        }

        return list;
    }

    public int JobCount(string jobq)
    {
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT count(*) FROM sys_jobs WHERE jobq = $jobq " +
            "AND status IN ('JobQueue', 'Held') AND started_at IS NULL";
        cmd.Parameters.AddWithValue("$jobq", QualifiedName.Parse(jobq.ToUpperInvariant(), "QUSRSYS").ToString());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<JobQueueEntry> Entries(string? subsystem = null)
    {
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT queue_lib||'/'||queue_name,subsystem_lib||'/'||subsystem_name,sequence,max_active FROM sys_jobq_entries ORDER BY subsystem_lib,subsystem_name,sequence";
        using var reader = command.ExecuteReader(); var entries = new List<JobQueueEntry>();
        var filter = subsystem is null ? null : QualifiedName.Parse(subsystem.ToUpperInvariant(), "QSYS").ToString();
        while (reader.Read()) if (filter is null || filter == reader.GetString(1))
            entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        return entries;
    }

    public void Attach(string queue, string subsystem, int sequence = 9999, int maximumActive = 32000, bool replace = false)
    {
        if (sequence is < 1 or > 9999 || maximumActive is < 0 or > 32000)
            throw new Ipc.Core.Messages.CpfException("IPC0120", "Queue sequence must be 1–9999 and MAXACT 0–32000.");
        var q = QualifiedName.Parse(queue.ToUpperInvariant(), "QUSRSYS");
        var s = QualifiedName.Parse(subsystem.ToUpperInvariant(), "QSYS");
        var authorization = new Ipc.Services.Security.ServiceAuthorization(_factory);
        authorization.RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        authorization.RequireObject(s.Library, s.Name.Value, ObjectType.SubsystemDescription, AuthorityBit.ObjectManagement);
        authorization.RequireObject(q.Library, q.Name.Value, ObjectType.JobQueue, Authorities.UseBits);
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT subsystem_lib||'/'||subsystem_name FROM sys_jobq_entries WHERE queue_lib=$qlib AND queue_name=$qname";
        command.Parameters.AddWithValue("$qlib", q.Library); command.Parameters.AddWithValue("$qname", q.Name.Value);
        var owner = command.ExecuteScalar() as string;
        if (owner is null && replace) throw new Ipc.Core.Messages.CpfException("IPC0120", "Queue entry does not exist.");
        if (owner is not null && (owner != s.ToString() || !replace))
            throw new Ipc.Core.Messages.CpfException("IPC0120", "Queue already belongs to a subsystem; detach it before transferring ownership.");
        command.CommandText = """
            INSERT INTO sys_jobq_entries(queue_lib,queue_name,subsystem_lib,subsystem_name,sequence,max_active)
            VALUES($qlib,$qname,$slib,$sname,$sequence,$max)
            ON CONFLICT(queue_lib,queue_name) DO UPDATE SET sequence=excluded.sequence,max_active=excluded.max_active
            """;
        command.Parameters.AddWithValue("$slib", s.Library); command.Parameters.AddWithValue("$sname", s.Name.Value);
        command.Parameters.AddWithValue("$sequence", sequence); command.Parameters.AddWithValue("$max", maximumActive);
        try { command.ExecuteNonQuery(); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        { throw new Ipc.Core.Messages.CpfException("IPC0120", "Queue/subsystem is missing or that subsystem sequence is already assigned."); }
        Ipc.Services.Security.MfaStore.Audit(connection, transaction, "work.queue.attached", Ipc.Services.Events.OperationIdentity.Current?.Principal ?? "*SYSTEM", true, DateTimeOffset.UtcNow);
        transaction.Commit();
    }

    public void Detach(string queue, string subsystem)
    {
        var q = QualifiedName.Parse(queue.ToUpperInvariant(), "QUSRSYS");
        var s = QualifiedName.Parse(subsystem.ToUpperInvariant(), "QSYS");
        var authorization = new Ipc.Services.Security.ServiceAuthorization(_factory);
        authorization.RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        authorization.RequireObject(s.Library, s.Name.Value, ObjectType.SubsystemDescription, AuthorityBit.ObjectManagement);
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sys_jobs WHERE jobq=$queue AND started_at IS NOT NULL AND status IN ('Active','MessageWait','Held')";
        command.Parameters.AddWithValue("$queue", q.ToString());
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new Ipc.Core.Messages.CpfException("IPC0120", "Queue still has executing jobs.");
        command.CommandText = "DELETE FROM sys_jobq_entries WHERE queue_lib=$qlib AND queue_name=$qname AND subsystem_lib=$slib AND subsystem_name=$sname";
        command.Parameters.AddWithValue("$qlib", q.Library); command.Parameters.AddWithValue("$qname", q.Name.Value);
        command.Parameters.AddWithValue("$slib", s.Library); command.Parameters.AddWithValue("$sname", s.Name.Value);
        if (command.ExecuteNonQuery() != 1) throw new Ipc.Core.Messages.CpfException("IPC0120", "Queue is not attached to that subsystem.");
        Ipc.Services.Security.MfaStore.Audit(connection, transaction, "work.queue.detached", Ipc.Services.Events.OperationIdentity.Current?.Principal ?? "*SYSTEM", true, DateTimeOffset.UtcNow);
        transaction.Commit();
    }

    public void SetHeld(string queue, bool held)
    {
        var q = QualifiedName.Parse(queue.ToUpperInvariant(), "QUSRSYS");
        var authorization = new Ipc.Services.Security.ServiceAuthorization(_factory);
        authorization.RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        authorization.RequireObject(q.Library, q.Name.Value, ObjectType.JobQueue, AuthorityBit.ObjectManagement);
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sys_jobqs SET held=$held WHERE library=$lib AND name=$name";
        command.Parameters.AddWithValue("$held", held ? 1 : 0); command.Parameters.AddWithValue("$lib", q.Library); command.Parameters.AddWithValue("$name", q.Name.Value);
        if (command.ExecuteNonQuery() != 1) throw new Ipc.Core.Messages.CpfException("CPF9801", "Job queue not found.");
    }
}
