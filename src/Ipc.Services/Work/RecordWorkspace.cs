using Ipc.Core.Work;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed partial class JobRecordTransactions
{
    internal sealed partial class RecordUnit
    {
        private SqliteConnection? _source;
        private SqliteTransaction? _snapshot;
        private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
        private readonly HashSet<string> _needed = new(StringComparer.Ordinal);
        private bool _preparing;
        private sealed record SchemaItem(string Type, string Name, string Table, string Sql);
        private IReadOnlyList<string> RefreshRecords(SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            _source = _factory.Open(); _snapshot = _source.BeginTransaction(deferred: true);
            var source = _source; var snapshot = _snapshot;
            _loaded.Clear(); _needed.Clear();
            return SqliteCancellation.Run(source, cancellationToken, () => {
                _members = ReadMembers(source, snapshot);
                var schema = new List<SchemaItem>();
                using (var command = Command(source, snapshot, """
                    SELECT s.type,s.name,s.tbl_name,s.sql FROM sqlite_schema s
                    LEFT JOIN pragma_table_list l ON l.schema='main' AND l.name=s.name
                    WHERE s.sql IS NOT NULL AND s.name NOT GLOB 'sqlite_*'
                      AND (s.type='table' AND l.type='table' OR s.type IN ('view','index','trigger')) ORDER BY s.rowid
                    """))
                using (var reader = command.ExecuteReader()) while (reader.Read())
                    schema.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                ClearWorkspace(transaction, cancellationToken);
                // Keep empty table definitions so SQLite still compiles FK actions and
                // the authorizer rejects unjournaled dependents before any changes.
                foreach (var item in schema.Where(item => item.Type == "table"))
                { cancellationToken.ThrowIfCancellationRequested(); Run(Data, transaction, item.Sql); }
                foreach (var item in schema.Where(item => item.Type == "view" || item.Type == "index" && _members.ContainsKey(item.Table)))
                { cancellationToken.ThrowIfCancellationRequested(); Run(Data, transaction, item.Sql); }
                var triggers = schema.Where(item => item.Type == "trigger" && _members.ContainsKey(item.Table) &&
                    !JournalHooks.OwnsTrigger(source, snapshot, item.Name)).Select(item => item.Sql).ToArray();
                return (IReadOnlyList<string>)triggers;
            });
        }
        private void CloseSnapshot()
        {
            try { _snapshot?.Dispose(); }
            finally { _snapshot = null; _source?.Dispose(); _source = null; }
        }
        internal void PrepareRecords(SqliteCommand command, SqliteTransaction transaction)
        {
            // Prepare without stepping: SQLite visits joins, trigger programs and FK
            // actions while the normal authority checks collect their base members.
            _needed.Clear(); _preparing = true;
            try { command.Prepare(); }
            finally { _preparing = false; }
            LoadMembers(transaction, _needed.ToArray());
        }
        private void LoadMembers(SqliteTransaction transaction, IEnumerable<string> tables)
        {
            var discovered = tables.Where(table => !_loaded.Contains(table)).ToHashSet(StringComparer.Ordinal);
            if (discovered.Count == 0) return;
            // Import committed rows and replay intents without repeating user effects.
            // Execution is authorized again after restoring both enforcement switches.
            var lastRowid = SQLitePCL.raw.sqlite3_last_insert_rowid(Data.Handle!);
            SetAuthorizer(false);
            try
            {
                ConfigureWorkspace(SQLitePCL.raw.SQLITE_DBCONFIG_ENABLE_TRIGGER, 0);
                ConfigureWorkspace(SQLitePCL.raw.SQLITE_DBCONFIG_ENABLE_FKEY, 0);
                SqliteCancellation.Run(_source!, CancellationToken, () => {
                    foreach (var table in discovered) CopyRecords(_source!, _snapshot!, transaction, _members[table], CancellationToken);
                    foreach (var change in Changes())
                    {
                        CancellationToken.ThrowIfCancellationRequested();
                        if (discovered.Contains(change.Table)) Apply(Data, transaction, _members[change.Table], change.Rrn, change.Before, change.After);
                    }
                    return 0;
                });
                _loaded.UnionWith(discovered);
            }
            finally
            {
                try
                {
                    // A temporary insert restores connection state without native ABI
                    // extensions. Deleting it leaves last_insert_rowid() unchanged.
                    using var restore = Command(Data, transaction, "INSERT INTO temp.ipc_snapshot_rowid(rowid) VALUES($rowid); DELETE FROM temp.ipc_snapshot_rowid");
                    restore.Parameters.AddWithValue("$rowid", lastRowid); restore.ExecuteNonQuery();
                }
                finally
                {
                    try { ConfigureWorkspace(SQLitePCL.raw.SQLITE_DBCONFIG_ENABLE_FKEY, 1); }
                    finally { try { ConfigureWorkspace(SQLitePCL.raw.SQLITE_DBCONFIG_ENABLE_TRIGGER, 1); } finally { SetAuthorizer(true); } }
                }
            }
        }
        private void ConfigureWorkspace(int option, int value)
        {
            var status = SQLitePCL.raw.sqlite3_db_config(Data.Handle!, option, value, out var actual);
            SqliteException.ThrowExceptionForRC(status, Data.Handle);
            if (actual != value) throw Invalid("Unable to configure record snapshot loading.");
        }
        private void ClearWorkspace(SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            var existing = new List<(string Type, string Name)>();
            using (var command = Command(Data, transaction, "SELECT type,name FROM sqlite_schema WHERE type IN ('trigger','view','table') AND name NOT GLOB 'sqlite_*' ORDER BY CASE type WHEN 'trigger' THEN 0 WHEN 'view' THEN 1 ELSE 2 END"))
            using (var reader = command.ExecuteReader()) while (reader.Read()) existing.Add((reader.GetString(0), reader.GetString(1)));
            foreach (var item in existing)
            { cancellationToken.ThrowIfCancellationRequested(); Run(Data, transaction, "DROP " + item.Type.ToUpperInvariant() + " " + Quote(item.Name)); }
            using var sequence = Command(Data, transaction, "SELECT count(*) FROM sqlite_schema WHERE name='sqlite_sequence'");
            if (Convert.ToInt64(sequence.ExecuteScalar()) != 0) Run(Data, transaction, "DELETE FROM sqlite_sequence");
        }
        private void CopyRecords(SqliteConnection source, SqliteTransaction snapshot, SqliteTransaction transaction, MemberSnapshot member, CancellationToken cancellationToken)
        {
            var writable = member.Columns.Where(column => column.Hidden == 0).ToArray();
            var names = new[] { member.Rowid }.Concat(writable.Select(column => column.Name)).Select(Quote).ToArray();
            using var read = Command(source, snapshot, "SELECT " + string.Join(',', names.Select(name => "r." + name)) + "," + JournalHooks.Image("r", member.Columns) + " FROM " + Quote(member.Table) + " r ORDER BY r." + Quote(member.Rowid));
            using var insert = Command(Data, transaction, "INSERT INTO " + Quote(member.Table) + "(" + string.Join(',', names) + ") VALUES(" + string.Join(',', names.Select((_, index) => "$v" + index)) + ")");
            for (var index = 0; index < names.Length; index++) insert.Parameters.AddWithValue("$v" + index, DBNull.Value);
            using var check = Command(Data, transaction, "SELECT " + JournalHooks.Image("r", member.Columns) + " FROM " + Quote(member.Table) + " r WHERE r." + Quote(member.Rowid) + "=$rrn");
            check.Parameters.AddWithValue("$rrn", 0L);
            using (var rows = read.ExecuteReader()) while (rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint();
                for (var index = 0; index < names.Length; index++) insert.Parameters[index].Value = rows.GetValue(index);
                insert.ExecuteNonQuery(); check.Parameters["$rrn"].Value = rows.GetInt64(0);
                if (!string.Equals(rows.GetString(names.Length), check.ExecuteScalar() as string, StringComparison.Ordinal))
                    throw Invalid("Snapshot storage or generated values differ from the committed record.");
            }
            using var sequence = Command(source, snapshot, "SELECT count(*) FROM sqlite_schema WHERE name='sqlite_sequence'");
            if (Convert.ToInt64(sequence.ExecuteScalar()) == 0) return;
            sequence.CommandText = "SELECT seq FROM sqlite_sequence WHERE name=$table"; sequence.Parameters.AddWithValue("$table", member.Table);
            if (sequence.ExecuteScalar() is not long high) return;
            using var restore = Command(Data, transaction, "DELETE FROM sqlite_sequence WHERE name=$table; INSERT INTO sqlite_sequence(name,seq) VALUES($table,$seq)");
            restore.Parameters.AddWithValue("$table", member.Table); restore.Parameters.AddWithValue("$seq", high); restore.ExecuteNonQuery();
        }
    }
}
