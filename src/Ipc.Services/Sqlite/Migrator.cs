using Ipc.Core.Catalog;

namespace Ipc.Services.Sqlite;

public sealed class Migrator
{
    private const string VersionKey = "schema_version";

    private readonly SqliteConnectionFactory _factory;

    public Migrator(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public int CurrentVersion()
    {
        using var connection = _factory.Open();
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'sys_meta'";
            if (Convert.ToInt64(probe.ExecuteScalar()) == 0)
            {
                return 0;
            }
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT value FROM sys_meta WHERE key = '{VersionKey}'";
        return (cmd.ExecuteScalar() as string) is { } v ? int.Parse(v) : 0;
    }

    public void MigrateToLatest()
    {
        using var connection = _factory.Open();
        foreach (var ddl in SystemCatalog.All)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }

        using var upsert = connection.CreateCommand();
        upsert.CommandText =
            $"INSERT INTO sys_meta (key, value) VALUES ('{VersionKey}', '{SystemCatalog.SchemaVersion}') " +
            $"ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        upsert.ExecuteNonQuery();
    }
}