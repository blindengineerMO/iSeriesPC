using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

public sealed class JobQueueStore
{
    private readonly SqliteConnectionFactory _factory;

    public JobQueueStore(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public void Ensure(string name, string library = "QGPL", string? description = null)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sys_jobqs (name, library, description)
            VALUES ($name, $library, $description)
            ON CONFLICT(name) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$library", library);
        cmd.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public bool Exists(string name)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sys_jobqs WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public IReadOnlyList<string> List()
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys_jobqs ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    public int JobCount(string jobq)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT count(*) FROM sys_jobs WHERE jobq = $jobq " +
            "AND status IN ('JobQueue', 'Held')";
        cmd.Parameters.AddWithValue("$jobq", jobq);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}