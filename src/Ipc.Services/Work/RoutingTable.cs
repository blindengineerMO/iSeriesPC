using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

public sealed class RoutingTable
{
    private readonly SqliteConnectionFactory _factory;

    public RoutingTable(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public void EnsureEntry(string subsystem, int sequence, string compareValue, string program, string userClass = "*USER")
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sys_routing (subsystem, seq, compare_value, program, user_class)
            VALUES ($subsystem, $seq, $compare, $program, $class)
            ON CONFLICT(subsystem, seq) DO UPDATE SET
                compare_value = excluded.compare_value,
                program = excluded.program,
                user_class = excluded.user_class;
            """;
        cmd.Parameters.AddWithValue("$subsystem", subsystem);
        cmd.Parameters.AddWithValue("$seq", sequence);
        cmd.Parameters.AddWithValue("$compare", compareValue);
        cmd.Parameters.AddWithValue("$program", program);
        cmd.Parameters.AddWithValue("$class", userClass);
        cmd.ExecuteNonQuery();
    }

    public string? Route(string subsystem, string? compareData, string? userProfile = null)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT compare_value, program, user_class FROM sys_routing WHERE subsystem = $subsystem ORDER BY seq";
        cmd.Parameters.AddWithValue("$subsystem", subsystem);
        using var reader = cmd.ExecuteReader();

        string? fallback = null;
        while (reader.Read())
        {
            var compare = reader.GetString(0);
            var program = reader.GetString(1);
            var userClass = reader.GetString(2);
            if (compare == "*ANY" || compare == compareData)
            {
                return program;
            }

            fallback ??= program;
        }

        return fallback;
    }
}