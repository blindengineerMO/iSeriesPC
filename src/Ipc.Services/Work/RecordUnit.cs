using System.Globalization;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed partial class JobRecordTransactions
{
    internal sealed partial class RecordUnit : IDisposable
    {
        internal object Gate { get; } = new();
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal string Cycle { get; private set; } = Guid.NewGuid().ToString("N");
        private ImmutableHashSet<QualifiedName> _enlisted = ImmutableHashSet<QualifiedName>.Empty;
        internal ImmutableHashSet<QualifiedName> Enlisted => Volatile.Read(ref _enlisted);
        internal SqliteConnection Data => _data ?? throw Invalid("Record workspace is not open.");
        internal CancellationToken CancellationToken { get; private set; }
        private readonly SqliteConnectionFactory _factory;
        private readonly JobKey _job;
        private readonly string _directory;
        private readonly SqliteConnection _intents;
        private SqliteConnection? _data;
        private SqliteTransaction? _intentTransaction;
        private readonly List<IDisposable> _fileLocks = [];
        private readonly Dictionary<string, MemberSnapshot> _snapshots = new(StringComparer.Ordinal);
        private readonly HashSet<JobLockResource> _locked = [];
        private readonly HashSet<string> _trackingTriggers = new(StringComparer.Ordinal);
        private Dictionary<string, MemberSnapshot> _members = new(StringComparer.Ordinal);
        private bool _disposed, _operation, _published;
        private sealed record MemberSnapshot(string Table, QualifiedName File, string Member, string Binding, string Journal, string Definition, string Sql, IReadOnlyList<JournalColumn> Columns, string Rowid);
        internal RecordUnit(SqliteConnectionFactory factory, JobKey job)
        {
            _factory = factory; _job = job;
            var parent = factory.UsesMemoryDatabase ? Path.Combine(Path.GetTempPath(), "ipc-record-units") : Path.Combine(factory.DataDirectory, ".record-units");
            _directory = Path.Combine(parent, Id);
            if (OperatingSystem.IsLinux()) Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); else Directory.CreateDirectory(_directory);
            _intents = SqliteConnectionFactory.OpenPrivate(Path.Combine(_directory, "intents.db"));
            Run(_intents, null, "CREATE TABLE changes(seq INTEGER PRIMARY KEY,table_name TEXT NOT NULL,rrn INTEGER NOT NULL,before_image TEXT,after_image TEXT,principal TEXT,at TEXT NOT NULL)");
        }
        internal long PendingCount() { using var command = Command(_intents, _intentTransaction, "SELECT count(*) FROM changes"); return Convert.ToInt64(command.ExecuteScalar()); }
        internal bool OwnsRecord(JobLockResource resource) { lock (Gate) return !_disposed && _locked.Contains(resource); }
        internal void RequireIdle() { if (_operation) throw Invalid("Finish the active record statement first."); }
        internal void Enlist(QualifiedName file)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Enlisted.Contains(file)) return;
            new ServiceAuthorization(_factory).RequireObject(file.Library, file.Name.Value, ObjectType.File, Authorities.UseBits);
            using var live = _factory.Open(); using var command = Command(live, null, "SELECT count(*) FROM sys_journal_bindings WHERE library=$library AND name=$name"); BindFile(command, file);
            if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw Invalid("A commitment resource must be a journaled physical file: " + file + ".");
            _fileLocks.Add(new JobLockStore(_factory).AcquireWithLifetime(_job, new(file.Library, file.Name.Value, ObjectType.File), JobLockMode.SharedUpdate, TimeSpan.Zero, _operation ? CancellationToken : default, "CommitmentOpen", "RecordFile:" + Id));
            Volatile.Write(ref _enlisted, _enlisted.Add(file));
        }
        internal RecordOperation Open(CancellationToken cancellationToken, IReadOnlyList<QualifiedName>? files = null, IReadOnlyList<string>? memberNames = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); if (_operation) throw Invalid("A record statement is already active for this job.");
            cancellationToken.ThrowIfCancellationRequested(); CancellationToken = cancellationToken;
            if (_published) Reset();
            SqliteTransaction? transaction = null;
            try
            {
                Refresh(cancellationToken);
                _intentTransaction = _intents.BeginTransaction(deferred: false);
                transaction = Data.BeginTransaction(deferred: false); _operation = true;
                SetAuthorizer(true);
                if (files is not null)
                    LoadMembers(transaction, _members.Values.Where(member => files.Contains(member.File) &&
                        (memberNames is null || memberNames.Contains(member.Member, StringComparer.OrdinalIgnoreCase))).Select(member => member.Table));
                return new(this, transaction);
            }
            catch
            {
                SetAuthorizer(false);
                try { transaction?.Dispose(); }
                finally { try { _intentTransaction?.Dispose(); } finally { _intentTransaction = null; _operation = false; CloseSnapshot(); } }
                throw;
            }
        }
        private void SetAuthorizer(bool enabled)
        {
            if (_data is not null) SQLitePCL.raw.sqlite3_set_authorizer(Data.Handle!, enabled ?
                (SQLitePCL.strdelegate_authorizer)((_, action, p0, p1, _, origin) => AuthorizeSql(action, p0, p1, origin)) : null, null);
        }
        private int AuthorizeSql(int action, string? table, string? detail, string? origin)
        {
            try { return AuthorizeSqlCore(action, table, detail, origin); }
            catch (Exception error) when (error is Ipc.Core.Messages.CpfException or OperationCanceledException or SqliteException) { return 1; }
        }
        private int AuthorizeSqlCore(int action, string? table, string? detail, string? origin)
        {
            // SELECTs and DML may address the enlisted physical namespace, never the
            // security/job catalog. Schema/transaction changes are host-owned.
            if (action is 18 or 23 or 9 or 20 && table is not null && _members.TryGetValue(table, out var member))
            {
                Enlist(member.File);
                var permission = action switch { 18 => AuthorityBit.Add, 23 => AuthorityBit.Update, 9 => AuthorityBit.Delete, _ => AuthorityBit.Read };
                new ServiceAuthorization(_factory).RequireObject(member.File.Library, member.File.Name.Value, ObjectType.File, AuthorityBit.ObjectOperate | permission, acquireLocks: false);
                if (_preparing) { _needed.Add(table); return 0; }
                return _loaded.Contains(table) ? 0 : 1;
            }
            if (action == 20) return table is "sqlite_master" or "sqlite_schema" or "pragma_table_xinfo" or "pragma_table_info" ? 0 : 1;
            if (action == 19) return table is "table_xinfo" or "table_info" ? 0 : 1;
            if (action == 31 && detail == "ipc_record_change") return origin is not null && _trackingTriggers.Contains(origin) ? 0 : 1;
            return action is 21 or 31 or 33 ? 0 : 1;
        }
        private void Refresh(CancellationToken cancellationToken)
        {
            _data ??= SqliteConnectionFactory.OpenPrivate(Path.Combine(_directory, "records.db"));
            Run(Data, null, "CREATE TEMP TABLE IF NOT EXISTS ipc_snapshot_rowid(value INTEGER)");
            Run(Data, null, "PRAGMA foreign_keys=OFF");
            try
            {
                using var transaction = Data.BeginTransaction(deferred: false);
                var triggers = RefreshRecords(transaction, cancellationToken);
                foreach (var change in Changes())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_snapshots.TryGetValue(change.Table, out var snapshot) || !_members.TryGetValue(change.Table, out var current) || !SameSchema(snapshot, current)) throw Invalid("A pending record's file definition changed.");
                }
                foreach (var trigger in triggers) Run(Data, transaction, trigger);
                _trackingTriggers.Clear();
                foreach (var member in _members.Values)
                {
                    var oldImage = JournalHooks.Image("OLD", member.Columns); var newImage = JournalHooks.Image("NEW", member.Columns);
                    foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
                    {
                        var row = operation == "DELETE" ? "OLD" : "NEW";
                        var trigger = "ipc_stage_" + Guid.NewGuid().ToString("N"); _trackingTriggers.Add(trigger);
                        var guard = operation == "UPDATE" ? $"SELECT CASE WHEN OLD.{Quote(member.Rowid)}<>NEW.{Quote(member.Rowid)} THEN RAISE(ABORT,'IPC0211: Staged record numbers cannot change') END;" : "";
                        Run(Data, transaction, $"CREATE TRIGGER {Quote(trigger)} AFTER {operation} ON {Quote(member.Table)} BEGIN {guard} SELECT ipc_record_change('{member.File.Library}','{member.File.Name}','{member.Member}',{row}.{Quote(member.Rowid)},{(operation == "INSERT" ? "NULL" : oldImage)},{(operation == "DELETE" ? "NULL" : newImage)}); END");
                    }
                }
                transaction.Commit();
            }
            finally { Run(Data, null, "PRAGMA foreign_keys=ON"); }
            Data.CreateFunction<string, string, string, long, string?, string?, long>("ipc_record_change", Track);
        }
        private long Track(string library, string file, string member, long rrn, string? before, string? after)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (rrn < 1 || (long)Encoding.UTF8.GetByteCount(before ?? "") + Encoding.UTF8.GetByteCount(after ?? "") > JournalStore.MaximumEntryBytes) throw Invalid("Invalid or oversized staged record.");
            var table = library + "." + file + "." + member;
            var snapshot = _members[table]; Enlist(snapshot.File);
            var permission = before is null ? AuthorityBit.Add : after is null ? AuthorityBit.Delete : AuthorityBit.Update;
            new ServiceAuthorization(_factory).RequireObject(library, file, ObjectType.File, AuthorityBit.ObjectOperate | permission, acquireLocks: false);
            var resource = new JobLockResource(library, file, ObjectType.File, member, rrn);
            if (_locked.Add(resource))
                try { new JobLockStore(_factory).AcquireCore(_job, resource, JobLockMode.Exclusive, TimeSpan.Zero, "Commitment", "RecordUnit:" + Id, CancellationToken); }
                catch { _locked.Remove(resource); throw; }
            if (_snapshots.TryGetValue(table, out var original) && !SameSchema(original, snapshot)) throw Invalid("Staged record schema changed.");
            _snapshots.TryAdd(table, snapshot);
            using var command = Command(_intents, _intentTransaction, "INSERT INTO changes(table_name,rrn,before_image,after_image,principal,at) VALUES($table,$rrn,$before,$after,$actor,$at)");
            command.Parameters.AddWithValue("$table", table); command.Parameters.AddWithValue("$rrn", rrn); command.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value); command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
            command.Parameters.AddWithValue("$actor", (object?)OperationIdentity.Current?.Principal ?? DBNull.Value); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery(); return 0;
        }
        internal void FinishOperation(SqliteTransaction transaction, bool complete)
        {
            SetAuthorizer(false);
            try
            {
                if (complete) { transaction.Commit(); _intentTransaction!.Commit(); }
                else { transaction.Rollback(); _intentTransaction!.Rollback(); }
            }
            finally { try { _intentTransaction!.Dispose(); } finally { _intentTransaction = null; _operation = false; CloseSnapshot(); } }
        }
        internal void Publish(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); if (_operation) throw Invalid("Finish the current record statement before commit.");
            RequireNoQuerySnapshots(); cancellationToken.ThrowIfCancellationRequested();
            if (_published) { Reset(); return; }
            var cursorPositions = CaptureCursorPositions();
            if (PendingCount() == 0) { Reset(); AcceptCursorPositions(cursorPositions); return; }
            using var live = _factory.Open(); Run(live, null, "PRAGMA foreign_keys=OFF");
            try
            {
                using var transaction = SqliteCancellation.BeginWrite(live, cancellationToken);
                var current = ReadMembers(live, transaction);
                var changed = Changes().Select(change => change.Table).Distinct().ToArray();
                // The main writer prevents authority/job mutations until publication ends.
                // Complete all service reads before DDL takes a shared-cache schema lock.
                new JobDataAreaStore(_factory).RequireOwner(_job);
                foreach (var access in Changes().Select(change => (change.Table,
                    Permission: change.Before is null ? AuthorityBit.Add : change.After is null ? AuthorityBit.Delete : AuthorityBit.Update)).Distinct())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!current.TryGetValue(access.Table, out var member)) throw Invalid("Commit resource is no longer journaled.");
                    new ServiceAuthorization(_factory).RequireObject(member.File.Library, member.File.Name.Value, ObjectType.File, AuthorityBit.ObjectOperate | access.Permission, acquireLocks: false);
                }
                var saved = new List<string>(); var journals = new HashSet<string>();
                foreach (var table in changed)
                {
                    if (!current.TryGetValue(table, out var member) || !SameSchema(_snapshots[table], member)) throw Invalid("Commit resource was changed or is no longer journaled.");
                    journals.Add(member.Journal);
                    using var command = Command(live, transaction, "SELECT name,sql FROM sqlite_schema WHERE type='trigger' AND tbl_name=$table ORDER BY rowid"); command.Parameters.AddWithValue("$table", table);
                    var triggers = new List<(string Name, string Sql)>(); using (var reader = command.ExecuteReader()) while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1)));
                    foreach (var trigger in triggers)
                    {
                        if (JournalHooks.OwnsTrigger(live, transaction, trigger.Name) || RecordSlots.OwnsTrigger(live, transaction, trigger.Name)) continue;
                        saved.Add(trigger.Sql); Run(live, transaction, "DROP TRIGGER " + Quote(trigger.Name));
                    }
                    // Older captures did not have the commitment-before-image switch.
                    using var managed = Command(live, transaction, "SELECT name FROM sys_journal_triggers WHERE library=$lib AND file=$file AND member=$member");
                    managed.Parameters.AddWithValue("$lib", member.File.Library); managed.Parameters.AddWithValue("$file", member.File.Name.Value); managed.Parameters.AddWithValue("$member", member.Member);
                    var names = new List<string>(); using (var reader = managed.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
                    foreach (var name in names) Run(live, transaction, "DROP TRIGGER " + Quote(name));
                    managed.CommandText = "DELETE FROM sys_journal_triggers WHERE library=$lib AND file=$file AND member=$member"; managed.ExecuteNonQuery();
                    JournalHooks.InstallMember(live, transaction, member.File.Library, member.File.Name.Value, member.Member);
                }
                live.CreateFunction("ipc_commit_before", () => 1); live.CreateFunction("ipc_commit_cycle", () => Cycle);
                foreach (var journal in journals) Marker(live, transaction, journal, "SC");
                foreach (var change in Changes())
                {
                    cancellationToken.ThrowIfCancellationRequested(); var member = current[change.Table];
                    live.CreateFunction("ipc_actor", () => change.Principal); live.CreateFunction("ipc_job", () => _job.ToString()); live.CreateFunction("ipc_journal_timestamp", () => change.At);
                    Apply(live, transaction, member, change.Rrn, change.Before, change.After);
                }
                using (var check = Command(live, transaction, "PRAGMA foreign_key_check")) using (var reader = check.ExecuteReader()) if (reader.Read()) throw Invalid("Commit would violate a foreign key.");
                foreach (var sql in saved) Run(live, transaction, sql);
                live.CreateFunction("ipc_actor", () => OperationIdentity.Current?.Principal); live.CreateFunction("ipc_journal_timestamp", () => DateTimeOffset.UtcNow.ToString("O"));
                foreach (var journal in journals) Marker(live, transaction, journal, "CM");
                if (OperationIdentity.Current is { } identity) new ServiceAuthorization(_factory).RequireCaller(live, transaction, identity.Principal);
                using (var state = Command(live, transaction, "SELECT cancel_requested FROM sys_jobs WHERE number=$number AND name=$name AND user=$user AND execution_state='Running'"))
                {
                    BindJob(state, _job); var value = state.ExecuteScalar();
                    if (value is null) throw Invalid("The owning job ended before commit.");
                    if (Convert.ToInt32(value) != 0) throw new OperationCanceledException("Job cancellation was requested.");
                }
                cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); _published = true; AcceptCursorPositions(cursorPositions);
            }
            finally { Run(live, null, "PRAGMA foreign_keys=ON"); }
            Reset();
        }
        private void Marker(SqliteConnection connection, SqliteTransaction transaction, string journal, string type)
        {
            using var command = Command(connection, transaction, "UPDATE sys_journals SET sequence=sequence+1 WHERE id=$journal; INSERT INTO sys_journal_entries(journal_id,sequence,receiver_id,code,entry_type,captured_at,principal,job,user_data,commit_cycle) SELECT id,sequence,current_receiver,'C',$type,ipc_journal_timestamp(),ipc_actor(),$job,$data,$cycle FROM sys_journals WHERE id=$journal");
            command.Parameters.AddWithValue("$journal", journal); command.Parameters.AddWithValue("$type", type); command.Parameters.AddWithValue("$job", _job.ToString()); command.Parameters.AddWithValue("$cycle", Cycle);
            command.Parameters.AddWithValue("$data", JsonSerializer.SerializeToUtf8Bytes(new { Format = "IPCCMT1", Definition = Id, Cycle })); command.ExecuteNonQuery();
        }
        internal void Reset()
        {
            if (_operation) throw Invalid("Finish the record statement before rollback.");
            Run(_intents, null, "DELETE FROM changes"); _snapshots.Clear();
            new JobLockStore(_factory).ReleaseScope(_job, "RecordUnit:" + Id); _locked.Clear(); Cycle = Guid.NewGuid().ToString("N"); _published = false;
        }
        private sealed record Change(string Table, long Rrn, string? Before, string? After, string? Principal, string At);
        private IEnumerable<Change> Changes()
        {
            using var command = Command(_intents, _intentTransaction, "SELECT table_name,rrn,before_image,after_image,principal,at FROM changes ORDER BY seq"); using var reader = command.ExecuteReader();
            while (reader.Read()) yield return new(reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5));
        }
        private static Dictionary<string, MemberSnapshot> ReadMembers(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = Command(connection, transaction, "SELECT m.lib,m.name,m.mbr,b.id,b.journal_id,d.def,s.sql FROM sys_file_members m JOIN sys_objects o ON o.lib=m.lib AND o.name=m.name AND o.type='*FILE' JOIN sys_file_defs d ON d.lib=m.lib AND d.name=m.name AND d.type='*FILE' JOIN sys_journal_bindings b ON b.library=m.lib AND b.name=m.name JOIN sqlite_schema s ON s.type='table' AND s.name=m.lib||'.'||m.name||'.'||m.mbr WHERE o.attribute IN ('*PF','*SRCPF')");
            var values = new List<(string Lib, string File, string Member, string Binding, string Journal, string Def, string Sql)>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) values.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
            return values.ToDictionary(row => row.Lib + "." + row.File + "." + row.Member, row => {
                var table = row.Lib + "." + row.File + "." + row.Member; var columns = JournalHooks.Columns(connection, transaction, table);
                var rowid = new[] { "rowid", "_rowid_", "oid" }.FirstOrDefault(alias => columns.All(column => !column.Name.Equals(alias, StringComparison.OrdinalIgnoreCase))) ?? throw Invalid("Record number is unavailable.");
                return new MemberSnapshot(table, new(row.Lib, row.File), row.Member, row.Binding, row.Journal, row.Def, row.Sql, columns, rowid);
            }, StringComparer.Ordinal);
        }
        private static bool SameSchema(MemberSnapshot left, MemberSnapshot right) => left.Binding == right.Binding && left.Sql == right.Sql && left.Definition == right.Definition && left.Columns.SequenceEqual(right.Columns);
        private static void Apply(SqliteConnection connection, SqliteTransaction transaction, MemberSnapshot member, long rrn, string? before, string? after)
        {
            using var command = Command(connection, transaction, "SELECT " + JournalHooks.Image("r", member.Columns) + " FROM " + Quote(member.Table) + " r WHERE r." + Quote(member.Rowid) + "=$rrn"); command.Parameters.AddWithValue("$rrn", rrn);
            var actual = command.ExecuteScalar() as string; var expected = JournalStore.Image(before);
            if (actual is null ? before is not null : before is null || !JournalStore.Image(actual)!.SequenceEqual(expected!)) throw Invalid("Record changed outside this unit; rollback or resolve the conflict before commit.");
            var desired = JournalStore.Image(after);
            if (desired is not null && !member.Columns.Select(column => column.Name).SequenceEqual(desired.Select(cell => cell.Name))) throw Invalid("Staged record layout is invalid.");
            var writable = member.Columns.Select((column, index) => (column, index)).Where(item => item.column.Hidden == 0).ToArray();
            if (desired is not null) foreach (var item in writable) command.Parameters.AddWithValue("$v" + item.index, CellValue(desired[item.index]));
            var table = Quote(member.Table); var id = Quote(member.Rowid);
            command.CommandText = after is null ? $"DELETE FROM {table} WHERE {id}=$rrn" : before is null ?
                $"INSERT INTO {table}({id}," + string.Join(',', writable.Select(item => Quote(item.column.Name))) + ") VALUES($rrn," + string.Join(',', writable.Select(item => "$v" + item.index)) + ")" :
                $"UPDATE {table} SET " + string.Join(',', writable.Select(item => Quote(item.column.Name) + "=$v" + item.index)) + $" WHERE {id}=$rrn";
            if (command.ExecuteNonQuery() != 1) throw Invalid("The staged change did not affect one physical record.");
            command.CommandText = "SELECT " + JournalHooks.Image("r", member.Columns) + " FROM " + Quote(member.Table) + " r WHERE r." + id + "=$rrn";
            var changed = command.ExecuteScalar() as string;
            if (changed is null ? after is not null : after is null || !JournalStore.Image(changed)!.SequenceEqual(desired!)) throw Invalid("Published storage differs from the staged record.");
        }
        private static object CellValue(JournalCell cell) => cell.StorageType switch { "null" => DBNull.Value, "text" => cell.Value!, "blob" => Convert.FromHexString(cell.Value!), "integer" => long.Parse(cell.Value!, CultureInfo.InvariantCulture), "real" => BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(cell.Value!, NumberStyles.HexNumber, CultureInfo.InvariantCulture))), _ => throw Invalid("Invalid staged storage type.") };
        private static string Quote(string name) => JournalHooks.Quote(name);
        private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql) { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
        private static void Run(SqliteConnection connection, SqliteTransaction? transaction, string sql) { using var command = Command(connection, transaction, sql); command.ExecuteNonQuery(); }
        private static void BindFile(SqliteCommand command, QualifiedName file) { command.Parameters.AddWithValue("$library", file.Library); command.Parameters.AddWithValue("$name", file.Name.Value); }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; Cursors.Clear();
            try { CloseSnapshot(); _data?.Dispose(); _intents.Dispose(); new JobLockStore(_factory).ReleaseScope(_job, "RecordUnit:" + Id); }
            finally { foreach (var lease in _fileLocks) lease.Dispose(); try { Directory.Delete(_directory, true); } catch (IOException) { } }
        }
    }
}
