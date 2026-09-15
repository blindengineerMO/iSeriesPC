using Ipc.Core.Catalog;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace Ipc.Core.Tests.Services;

public sealed class MigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-migrations-" + Guid.NewGuid().ToString("N"));
    private readonly List<SqliteConnectionFactory> _factories = new();

    private SqliteConnectionFactory Factory(string file = "system.db")
    {
        var factory = new SqliteConnectionFactory(_directory, file);
        _factories.Add(factory);
        return factory;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void Modern_catalog_upgrade_preserves_history_and_adds_new_features(int version)
    {
        var factory = Factory();
        new Migrator(factory, Migrator.Migrations.Take(version).ToArray()).MigrateToLatest();
        var existingHistory = Scalar(factory, $"SELECT group_concat(checksum) FROM sys_migrations WHERE version <= {version}");
        var upgrade = new Migrator(factory);
        upgrade.MigrateToLatest();
        Assert.Equal(SystemCatalog.SchemaVersion, upgrade.CurrentVersion());
        Assert.Equal(existingHistory, Scalar(factory, $"SELECT group_concat(checksum) FROM sys_migrations WHERE version <= {version}"));
        Assert.Equal(0L, Scalar(factory, "SELECT count(*) FROM sys_job_execution"));
        Assert.Equal(0L, Scalar(factory, "SELECT count(*) FROM sys_object_dependencies"));
        Assert.NotNull(upgrade.LastBackupPath);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Legacy_upgrade_preserves_data_and_creates_restorable_backup(int version)
    {
        var factory = Factory();
        SeedLegacy(factory, version);
        var migrator = new Migrator(factory);
        migrator.MigrateToLatest();

        Assert.Equal(SystemCatalog.SchemaVersion, migrator.CurrentVersion());
        Assert.Equal("customer data", Scalar(factory, "SELECT value FROM preserved_records"));
        Assert.Equal((long)version, Scalar(factory, "SELECT count(*) FROM sys_migrations WHERE baseline = 1"));
        Assert.Equal((long)SystemCatalog.SchemaVersion, Scalar(factory, "SELECT count(*) FROM sys_migrations"));
        Assert.NotNull(migrator.LastBackupPath);
        Assert.True(File.Exists(migrator.LastBackupPath));
        var restored = Factory("restored.db");
        File.Copy(migrator.LastBackupPath, restored.DatabasePath!);
        Assert.Equal(version, new Migrator(restored).CurrentVersion());
        Assert.Equal("customer data", Scalar(restored, "SELECT value FROM preserved_records"));
        Assert.Equal("ok", Scalar(restored, "PRAGMA integrity_check"));
        new Migrator(restored).MigrateToLatest();
        Assert.Equal("customer data", Scalar(restored, "SELECT value FROM preserved_records"));
    }

    [Fact]
    public void Fresh_install_and_repeated_start_record_each_migration_once()
    {
        using var factory = new SqliteConnectionFactory(":memory:");
        var migrator = new Migrator(factory);
        migrator.MigrateToLatest();
        var history = Scalar(factory, "SELECT group_concat(applied_at) FROM sys_migrations");
        migrator.MigrateToLatest();
        Assert.Equal(history, Scalar(factory, "SELECT group_concat(applied_at) FROM sys_migrations"));
        Assert.Equal(0L, Scalar(factory, "SELECT count(*) FROM sys_migrations WHERE baseline = 1"));
        Assert.Null(migrator.LastBackupPath);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("invalid")]
    [InlineData("-1")]
    [InlineData("0")]
    public void Rejects_newer_or_invalid_version_without_changing_catalog(string version)
    {
        var factory = Factory();
        SeedLegacy(factory, 2);
        using (var c = factory.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE sys_meta SET value = $version WHERE key = 'schema_version'";
            cmd.Parameters.AddWithValue("$version", version);
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new Migrator(factory).MigrateToLatest());
        Assert.Equal(version, Scalar(factory, "SELECT value FROM sys_meta WHERE key = 'schema_version'"));
        Assert.Equal(0L, Scalar(factory, "SELECT count(*) FROM sqlite_master WHERE name = 'sys_migrations'"));
        Assert.Equal("customer data", Scalar(factory, "SELECT value FROM preserved_records"));
        Assert.False(Directory.Exists(Path.Combine(_directory, "migration-backups")));
    }

    [Fact]
    public void Unversioned_existing_database_is_not_treated_as_empty()
    {
        var factory = Factory();
        Execute(factory, "CREATE TABLE unrelated(value TEXT)");
        Assert.Throws<InvalidOperationException>(() => new Migrator(factory).MigrateToLatest());
        Assert.Equal(0L, Scalar(factory, "SELECT count(*) FROM sqlite_master WHERE name = 'sys_meta'"));
    }

    [Fact]
    public void Failed_migration_rolls_back_schema_data_history_and_version()
    {
        var factory = Factory();
        new Migrator(factory).MigrateToLatest();
        Execute(factory, "CREATE TABLE preserved_records(value TEXT); INSERT INTO preserved_records VALUES ('customer data')");
        var failing = new SchemaMigration(SystemCatalog.SchemaVersion + 1, "Failure injection", new[]
        {
            "CREATE TABLE must_rollback(value TEXT)",
            "UPDATE preserved_records SET value = 'incorrect'",
            "INSERT INTO nonexistent_table VALUES (1)",
        });
        Assert.Throws<SqliteException>(() => new Migrator(factory, Migrator.Migrations.Append(failing).ToArray()).MigrateToLatest());
        Assert.Equal(SystemCatalog.SchemaVersion, new Migrator(factory).CurrentVersion());
        Assert.Equal(0L, Scalar(factory, "SELECT count(*) FROM sqlite_master WHERE name = 'must_rollback'"));
        Assert.Equal("customer data", Scalar(factory, "SELECT value FROM preserved_records"));
        Assert.Equal((long)SystemCatalog.SchemaVersion, Scalar(factory, "SELECT count(*) FROM sys_migrations"));
        // The unchanged production runner can safely start again after the failed upgrade.
        new Migrator(factory).MigrateToLatest();
    }

    [Theory]
    [InlineData("DELETE FROM sys_migrations WHERE version = 2")]
    [InlineData("UPDATE sys_migrations SET checksum = 'changed' WHERE version = 1")]
    [InlineData("DROP TABLE sys_migrations")]
    public void Rejects_inconsistent_migration_history(string damage)
    {
        var factory = Factory();
        new Migrator(factory).MigrateToLatest();
        Execute(factory, damage);
        Assert.Throws<InvalidOperationException>(() => new Migrator(factory).MigrateToLatest());
        Assert.Equal(SystemCatalog.SchemaVersion, new Migrator(factory).CurrentVersion());
    }

    [Fact]
    public async Task Independent_connections_serialize_concurrent_upgrade()
    {
        var first = Factory();
        SeedLegacy(first, 2);
        var factories = Enumerable.Range(0, 4).Select(_ => Factory()).ToArray();
        using var barrier = new Barrier(factories.Length);
        await Task.WhenAll(factories.Select(f => Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            new Migrator(f).MigrateToLatest();
        })));
        Assert.Equal(SystemCatalog.SchemaVersion, new Migrator(first).CurrentVersion());
        Assert.Equal((long)SystemCatalog.SchemaVersion, Scalar(first, "SELECT count(*) FROM sys_migrations"));
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "migration-backups"), "*.db"));
        Assert.Equal("ok", Scalar(first, "PRAGMA integrity_check"));
    }

    [Fact]
    public void Backup_failure_leaves_old_catalog_usable()
    {
        var factory = Factory();
        SeedLegacy(factory, 2);
        File.WriteAllText(Path.Combine(_directory, "migration-backups"), "blocks directory creation");
        Assert.Throws<IOException>(() => new Migrator(factory).MigrateToLatest());
        Assert.Equal(2, new Migrator(factory).CurrentVersion());
        Assert.Equal("customer data", Scalar(factory, "SELECT value FROM preserved_records"));
    }

    [Fact]
    public async Task Console_processes_upgrade_one_catalog_without_duplicate_migrations()
    {
        var factory = Factory();
        SeedLegacy(factory, 2);
        var processes = Enumerable.Range(0, 3).Select(_ =>
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(typeof(Ipc.Console.Program).Assembly.Location);
            start.ArgumentList.Add("--migrate-only");
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(_directory);
            return Process.Start(start)!;
        }).ToArray();
        try
        {
            foreach (var process in processes)
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await stderr);
                Assert.Contains($"schema {SystemCatalog.SchemaVersion} is ready", await stdout);
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
        }
        Assert.Equal((long)SystemCatalog.SchemaVersion, Scalar(factory, "SELECT count(*) FROM sys_migrations"));
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "migration-backups"), "*.db"));
        Assert.Equal("customer data", Scalar(factory, "SELECT value FROM preserved_records"));
    }

    [Fact]
    public void Connection_factory_quotes_paths_and_releases_memory_on_dispose()
    {
        var factory = Factory("semi;colon.db");
        new Migrator(factory).MigrateToLatest();
        Assert.True(File.Exists(Path.Combine(_directory, "semi;colon.db")));
        Assert.Equal(1L, Scalar(factory, "PRAGMA foreign_keys"));
        using var memory = new SqliteConnectionFactory(":memory:");
        new Migrator(memory).MigrateToLatest();
        var connectionString = memory.ConnectionString;
        memory.Dispose();
        memory.Dispose();
        Assert.Throws<ObjectDisposedException>(() => memory.Open());
        using var independent = new SqliteConnection(connectionString);
        independent.Open();
        using var cmd = independent.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'sys_meta'";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    private static void SeedLegacy(SqliteConnectionFactory factory, int version)
    {
        using var connection = factory.Open();
        foreach (var sql in Migrator.Migrations.Take(version).SelectMany(m => m.Statements))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO sys_meta VALUES ('schema_version', $version);
            CREATE TABLE preserved_records(value TEXT);
            INSERT INTO preserved_records VALUES ('customer data');
            """;
        seed.Parameters.AddWithValue("$version", version.ToString());
        seed.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnectionFactory factory, string sql)
    {
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Execute(SqliteConnectionFactory factory, string sql)
    {
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (var factory in _factories) factory.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
