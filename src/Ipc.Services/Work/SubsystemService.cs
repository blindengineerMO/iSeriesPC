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
        };

        foreach (var (name, description, maxActive) in configured)
        {
            var routing = new RoutingTable(_factory);
            Ensure(name, description, maxActive);
            routing.EnsureEntry(name, 1, "*ANY", maxActive <= 30 ? "QSYS/QCMD" : "QSYS/QCMD");
        }
    }

    public void Ensure(string name, string? description, int maxActive)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sys_subsystems (name, description, status, max_active)
            VALUES ($name, $description, 'Stopped', $max)
            ON CONFLICT(name) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$max", maxActive);
        cmd.ExecuteNonQuery();
    }

    public void Start(string name)
    {
        var queue = new JobQueueStore(_factory);
        queue.Ensure(name, "QUSRSYS");

        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE sys_subsystems SET status = 'Active' WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        var changed = cmd.ExecuteNonQuery();
        if (changed == 0)
        {
            Ensure(name, null, 1);
        }

        _jobs.StartNext(name);
    }

    public void End(string name)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE sys_subsystems SET status = 'Stopped' WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.ExecuteNonQuery();
    }

    public bool IsActive(string name)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status FROM sys_subsystems WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() as string == "Active";
    }

    public IReadOnlyList<SubsystemStatus> StatusAll()
    {
        using var connection = _factory.Open();
        var subsystems = new List<SubsystemStatus>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name, description, status, max_active FROM sys_subsystems ORDER BY name";
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