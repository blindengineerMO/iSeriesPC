using Ipc.Core.System;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

public sealed class SystemValueStore
{
    private readonly SqliteConnectionFactory _factory;

    public SystemValueStore(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public void SeedFromRegistry(SystemValueRegistry registry)
    {
        using var connection = _factory.Open();
        foreach (var value in registry.All)
        {
            using var upsert = connection.CreateCommand();
            upsert.CommandText =
                "INSERT INTO sys_sysvals (name, value) VALUES ($name, $value) " +
                "ON CONFLICT(name) DO UPDATE SET value = sys_sysvals.value";
            upsert.Parameters.AddWithValue("$name", value.Name);
            upsert.Parameters.AddWithValue("$value", value.Value);
            upsert.ExecuteNonQuery();
        }
    }

    public void Set(string name, string value)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO sys_sysvals (name, value) VALUES ($name, $value) " +
            "ON CONFLICT(name) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    public void LoadInto(SystemValueRegistry registry)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, value FROM sys_sysvals";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (registry.TryGet(reader.GetString(0), out var value) && value is not null)
            {
                value.Value = reader.GetString(1);
            }
        }
    }
}