using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ipc.Core.Catalog;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

internal sealed record SchemaMigration(int Version, string Name, IReadOnlyList<string> Statements,
    Action<SqliteConnection, SqliteTransaction>? UpgradeData = null)
{
    public string Checksum => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\n", Statements))));
}

public sealed class Migrator
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IReadOnlyList<SchemaMigration> _migrations;

    internal static IReadOnlyList<SchemaMigration> Migrations { get; } = new[]
    {
        new SchemaMigration(1, "Initial system catalog", new[]
        {
            SystemCatalog.CreateSchemaVersionTable, SystemCatalog.CreateObjectsTable,
            SystemCatalog.CreateSystemValuesTable, SystemCatalog.CreateLibrariesTable,
            SystemCatalog.CreateLibraryListTable, SystemCatalog.CreateJobLogTable,
            SystemCatalog.CreateUserProfilesTable, SystemCatalog.CreateAuthoritiesTable,
            SystemCatalog.CreateAuthLMembersTable, SystemCatalog.CreateSubsystemsTable,
            SystemCatalog.CreateJobQueuesTable, SystemCatalog.CreateJobsTable,
            SystemCatalog.CreateRoutingTable, SystemCatalog.CreateJobLogEntriesTable,
            SystemCatalog.CreateMenusTable, SystemCatalog.CreateMenuOptionsTable,
        }),
        new SchemaMigration(2, "Physical and source file catalog", new[]
        {
            SystemCatalog.CreateFileDefinitionsTable, SystemCatalog.CreateFileMembersTable,
        }),
        new SchemaMigration(3, "Transactional migration history", new[]
        {
            SystemCatalog.CreateMigrationHistoryTable,
        }),
        new SchemaMigration(4, "Durable batch execution requests", new[]
        {
            """
            CREATE TABLE sys_job_execution (
                job_number INTEGER PRIMARY KEY,
                command TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0
            );
            """,
        }),
        new SchemaMigration(5, "Type qualified object dependencies", new[]
        {
            """
            CREATE TABLE sys_object_dependencies (
                source_lib TEXT NOT NULL, source_name TEXT NOT NULL, source_type TEXT NOT NULL,
                target_lib TEXT NOT NULL, target_name TEXT NOT NULL, target_type TEXT NOT NULL,
                PRIMARY KEY(source_lib, source_name, source_type, target_lib, target_name, target_type),
                FOREIGN KEY(source_lib, source_name, source_type) REFERENCES sys_objects(lib, name, type)
                    ON UPDATE CASCADE ON DELETE CASCADE,
                FOREIGN KEY(target_lib, target_name, target_type) REFERENCES sys_objects(lib, name, type)
                    ON UPDATE CASCADE ON DELETE RESTRICT
            );
            """,
        }),
        new SchemaMigration(6, "Profile object catalog synchronization", new[] { ProfileCatalogMigration.Sql }),
        new SchemaMigration(7, "Qualified work object catalogs", new[] { WorkCatalogMigration.Sql }),
        new SchemaMigration(8, "Authorization list object catalog", new[]
        {
            """
            INSERT INTO sys_objects(lib,name,type,owner,created,changed,public_authority)
            SELECT 'QSYS',authl,'*AUTL','QSECOFR',strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'),0
            FROM (SELECT authl FROM sys_authl_members UNION SELECT holder FROM sys_authorities WHERE is_authl=1) WHERE true
            ON CONFLICT(lib,name,type) DO NOTHING;
            """,
        }),
        new SchemaMigration(9, "Durable audit and domain event outbox", new[] { EventCatalogMigration.Sql }),
        new SchemaMigration(10, "Enterprise identity mappings", new[] { EimCatalogMigration.Sql }),
        new SchemaMigration(11, "MFA and authenticated sessions", new[] { AuthenticationCatalogMigration.Sql }),
        new SchemaMigration(12, "Certificate inventory and content trust", new[] { CertificateCatalogMigration.Sql }),
        new SchemaMigration(13, "Queue ownership and execution attributes", new[] { SchedulerCatalogMigration.Sql }),
        new SchemaMigration(14, "Durable execution outcomes", new[] { ExecutionCatalogMigration.Sql }),
        new SchemaMigration(15, "Job runtime controls and accounting", new[] { JobLifecycleMigration.Sql }),
        new SchemaMigration(16, "Job environment and data areas", new[] { JobEnvironmentMigration.Sql }),
        new SchemaMigration(17, "Interactive terminal families", new[] { InteractiveSessionMigration.Sql }),
        new SchemaMigration(18, "Native null key ordering and uniqueness", new[] { NullKeyCatalogMigration.Contract }, NullKeyCatalogMigration.Apply),
        new SchemaMigration(19, "Durable named message queues", new[] { MessageQueueMigration.Sql }),
        new SchemaMigration(20, "Job call-frame message queues", new[] { ProgramMessageQueueMigration.Sql }),
        new SchemaMigration(21, "Message return types and exception receipt", new[] { MessageReturnTypeMigration.Sql }),
    };

    public Migrator(SqliteConnectionFactory factory) : this(factory, Migrations) { }

    internal Migrator(SqliteConnectionFactory factory, IReadOnlyList<SchemaMigration> migrations)
    {
        _factory = factory;
        _migrations = migrations.ToArray();
        if (_migrations.Count == 0 || !_migrations.Select(m => m.Version)
            .SequenceEqual(Enumerable.Range(1, _migrations.Count)))
            throw new ArgumentException("Migrations must be in contiguous version order starting at 1.", nameof(migrations));
    }

    public string? LastBackupPath { get; private set; }

    public int CurrentVersion()
    {
        using var connection = _factory.Open();
        return ReadVersion(connection, null);
    }

    public void MigrateToLatest()
    {
        LastBackupPath = null;
        using var connection = _factory.Open();
        // BEGIN IMMEDIATE serializes migration/version reads across independent processes.
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadVersion(connection, transaction);
        if (current > _migrations.Count)
            throw new InvalidOperationException($"Catalog schema {current} is newer than supported schema {_migrations.Count}; use a compatible server.");
        if (current == 0 && Convert.ToInt64(Scalar(connection, transaction,
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'")) != 0)
            throw new InvalidOperationException("Existing catalog has no schema version. Restore a versioned backup before upgrading.");

        var historyExists = TableExists(connection, transaction, "sys_migrations");
        if (current >= 3 && !historyExists)
            throw new InvalidOperationException("Catalog migration history is missing.");
        if (historyExists) ValidateHistory(connection, transaction, current);
        if (current == _migrations.Count)
        {
            transaction.Commit();
            return;
        }

        if (current > 0 && !_factory.UsesMemoryDatabase)
            LastBackupPath = CreateBackup(current);

        Execute(connection, transaction, SystemCatalog.CreateMigrationHistoryTable);
        if (!historyExists)
        {
            // Versions 1/2 shipped without history. Label adoption, not retroactive execution.
            foreach (var migration in _migrations.Take(current))
                RecordMigration(connection, transaction, migration, baseline: true);
        }

        foreach (var migration in _migrations.Skip(current))
        {
            foreach (var statement in migration.Statements)
                Execute(connection, transaction, statement);
            migration.UpgradeData?.Invoke(connection, transaction);
            RecordMigration(connection, transaction, migration, baseline: false);
            using var version = connection.CreateCommand();
            version.Transaction = transaction;
            version.CommandText = """
                INSERT INTO sys_meta(key, value) VALUES ('schema_version', $version)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            version.Parameters.AddWithValue("$version", migration.Version.ToString(CultureInfo.InvariantCulture));
            version.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void ValidateHistory(SqliteConnection connection, SqliteTransaction transaction, int current)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT version, checksum FROM sys_migrations ORDER BY version";
        using var reader = cmd.ExecuteReader();
        var expected = 1;
        while (reader.Read())
        {
            var version = reader.GetInt32(0);
            if (version != expected || version > current || version > _migrations.Count ||
                reader.GetString(1) != _migrations[version - 1].Checksum)
                throw new InvalidOperationException("Catalog migration history is inconsistent with this server.");
            expected++;
        }
        if (expected != current + 1)
            throw new InvalidOperationException("Catalog migration history is incomplete.");
    }

    private string CreateBackup(int version)
    {
        var directory = Path.Combine(Path.GetDirectoryName(_factory.DatabasePath!)!, "migration-backups");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory,
            $"{Path.GetFileName(_factory.DatabasePath)}.v{version}.{DateTime.UtcNow:yyyyMMddTHHmmssfffffff}.{Guid.NewGuid():N}.db");
        var partial = path + ".partial";
        try
        {
            using (File.Create(partial)) { }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // Read the pre-migration WAL snapshot while our transaction blocks writers.
            // Never copy a live .db file without its WAL.
            using var source = _factory.Open();
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = partial, Pooling = false,
            }.ToString());
            destination.Open();
            source.BackupDatabase(destination);
            destination.Close();
            File.Move(partial, path);
            return path;
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    private static int ReadVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (!TableExists(connection, transaction, "sys_meta")) return 0;
        var value = Scalar(connection, transaction, "SELECT value FROM sys_meta WHERE key = 'schema_version'");
        if (value is not string text || !int.TryParse(text, NumberStyles.None,
                CultureInfo.InvariantCulture, out var version) || version < 1)
            throw new InvalidOperationException("Catalog schema version is missing or invalid.");
        return version;
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void RecordMigration(SqliteConnection connection, SqliteTransaction transaction,
        SchemaMigration migration, bool baseline)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sys_migrations(version, name, checksum, applied_at, baseline)
            VALUES ($version, $name, $checksum, $at, $baseline)
            """;
        cmd.Parameters.AddWithValue("$version", migration.Version);
        cmd.Parameters.AddWithValue("$name", migration.Name);
        cmd.Parameters.AddWithValue("$checksum", migration.Checksum);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$baseline", baseline);
        cmd.ExecuteNonQuery();
    }
}
