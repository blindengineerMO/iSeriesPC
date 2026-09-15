using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

public sealed class SubsystemService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly JobService _jobs;

    public SubsystemService(SqliteConnectionFactory factory, JobService jobs)
    {
        _factory = factory;
        _jobs = jobs;
    }

    public void SeedDefaults()
    {
        var configured = new[]
        {
            (JobKeys.InteractiveSubsystem, "Interactive subsystem", 30),
            (JobKeys.BatchSubsystem, "Batch subsystem", 10),
            (JobKeys.CommunicationSubsystem, "Communication subsystem", 64),
            (JobKeys.SystemSubsystem, "System work subsystem", 32),
        };

        foreach (var (name, description, maxActive) in configured)
        {
            Ensure(name, description, maxActive);
        }
    }

    public void Ensure(string name, string? description, int maxActive)
    {
        if (maxActive is < 1 or > 32000) throw new Ipc.Core.Messages.CpfException("IPC0120", "Subsystem maximum must be 1–32000.");
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        var key = QualifiedName.Parse(name.ToUpperInvariant(), "QSYS");
        var objects = new SqliteObjectStore(_factory);
        var owner = Ipc.Services.Events.OperationIdentity.Current?.Principal ?? "QSYS";
        var authorization = new Ipc.Services.Security.ServiceAuthorization(_factory);
        if (objects.GetForAuthorization(key.Library, key.Name.Value, ObjectType.SubsystemDescription) is null)
            authorization.RequireCreate(new ObjectDescriptor { Key = key, ObjectType = ObjectType.SubsystemDescription, Owner = owner });
        else authorization.RequireObject(key.Library, key.Name.Value, ObjectType.SubsystemDescription, Authorities.UseBits);
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sys_subsystems (library, name, description, status, max_active)
            VALUES ($library, $name, $description, 'Stopped', $max)
            ON CONFLICT(library,name) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$name", key.Name.Value);
        cmd.Parameters.AddWithValue("$library", key.Library);
        cmd.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$max", maxActive);
        var created = cmd.ExecuteNonQuery() == 1;
        if (created)
        {
            cmd.CommandText = "UPDATE sys_objects SET owner=$owner WHERE lib=$library AND name=$name AND type='*SBSD'";
            cmd.Parameters.AddWithValue("$owner", owner);
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
        if (created) new RoutingTable(_factory).EnsureEntry(name, 9999, "*ANY", "QSYS/QCMD");
    }

    public void Start(string name)
    {
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        Ensure(name, null, 1);
        var queue = new JobQueueStore(_factory);
        var key = QualifiedName.Parse(name.ToUpperInvariant(), "QSYS");
        if (queue.Entries(name).Count == 0)
        {
            var queueKey = new QualifiedName(key.Library == "QSYS" ? "QUSRSYS" : key.Library, key.Name.Value);
            queue.Ensure(queueKey.ToString());
            queue.Attach(queueKey.ToString(), key.ToString());
        }
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE sys_subsystems SET status = 'Active' WHERE name = $name AND library = $library";
        cmd.Parameters.AddWithValue("$name", key.Name.Value);
        cmd.Parameters.AddWithValue("$library", key.Library);
        var changed = cmd.ExecuteNonQuery();
        if (changed == 0)
        {
            Ensure(name, null, 1);
        }

        // Only the server dispatcher may claim executable requests. Starting a subsystem
        // must not mark jobs active without actually executing them.
    }

    public void End(string name)
    {
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        if (!Exists(name)) throw new Ipc.Core.Messages.CpfException("CPF9801", "Subsystem not found.");
        var key = QualifiedName.Parse(name.ToUpperInvariant(), "QSYS");
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE sys_subsystems SET status='Stopped' WHERE name=$name AND library=$library;
            UPDATE sys_jobs SET cancel_requested=1,cancel_reason='Subsystem ended.'
            WHERE subsystem=CASE WHEN $library='QSYS' THEN $name ELSE $library||'/'||$name END AND execution_state='Running';
            """;
        cmd.Parameters.AddWithValue("$name", key.Name.Value);
        cmd.Parameters.AddWithValue("$library", key.Library);
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    public bool Exists(string name)
    {
        var key = QualifiedName.Parse(name.ToUpperInvariant(), "QSYS");
        if (new SqliteObjectStore(_factory).Get(key.Library, key.Name.Value, ObjectType.SubsystemDescription) is null) return false;
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sys_subsystems WHERE library=$library AND name=$name";
        command.Parameters.AddWithValue("$library", key.Library);
        command.Parameters.AddWithValue("$name", key.Name.Value);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    public bool IsActive(string name)
    {
        if (!Exists(name)) return false;
        var key = QualifiedName.Parse(name.ToUpperInvariant(), "QSYS");
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status FROM sys_subsystems WHERE name = $name AND library = $library";
        cmd.Parameters.AddWithValue("$name", key.Name.Value);
        cmd.Parameters.AddWithValue("$library", key.Library);
        return cmd.ExecuteScalar() as string == "Active";
    }

    public IReadOnlyList<SubsystemStatus> StatusAll()
    {
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        using var connection = _factory.Open();
        var subsystems = new List<SubsystemStatus>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT CASE WHEN library='QSYS' THEN name ELSE library||'/'||name END, description, status, max_active FROM sys_subsystems ORDER BY library,name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                subsystems.Add(new SubsystemStatus
                {
                    Name = reader.GetString(0),
                    Description = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Active = reader.GetString(2) == "Active",
                    MaxActiveJobs = reader.GetInt32(3),
                });
            }
        }

        foreach (var subsystem in subsystems)
        {
            subsystem.ActiveJobs = _jobs.CountWithStatus(subsystem.Name, null);
            subsystem.QueuedJobs = _jobs.CountInQueueForSubsystem(subsystem.Name);
        }

        return subsystems;
    }
}
