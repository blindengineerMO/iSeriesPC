using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed record JournalFileSelection(QualifiedName File, string Member = "*ALL");
public enum JournalRecoveryErrorPolicy { Atomic, End, Continue }
public sealed record JournalRecoveryFailure(QualifiedName File, long Sequence, string Message, string? Member = null);
public sealed record JournalRecoveryBoundary(string Cycle, long StoppedBeforeSequence, long RequestedEndSequence);
public sealed record JournalRecoveryResult(long Changes, long FirstSequence, long LastSequence, IReadOnlyList<JournalRecoveryFailure>? Failures = null,
    IReadOnlyList<JournalRecoveryBoundary>? Boundaries = null);

public sealed partial class JournalStore
{
    /// <summary>Recover a retained range; Atomic rolls back the range, End/Continue retain a consistent successful prefix.</summary>
    public JournalRecoveryResult Recover(QualifiedName journalName, IReadOnlyList<JournalFileSelection> files,
        long? fromSequence = null, long? toSequence = null, bool remove = false,
        QualifiedName? firstReceiver = null, QualifiedName? lastReceiver = null, CancellationToken cancellationToken = default,
        JournalRecoveryErrorPolicy errorPolicy = JournalRecoveryErrorPolicy.Atomic, bool honorCommitBoundaries = true)
    {
        if (factory.RecordTransactions.HasCurrentDefinition) throw Invalid("End the executing job's commitment definition before journal recovery.");
        Require(journalName, ObjectType.Journal, Authorities.UseBits | AuthorityBit.Add);
        if (files.Count is < 1 or > 300) throw Invalid("Recovery requires 1–300 physical file/member selections.");
        if (firstReceiver is null && lastReceiver is not null || remove && firstReceiver is not null && lastReceiver is null) throw Invalid("Specify both receiver endpoints; apply also accepts a named start through *CURRENT.");
        if (!Enum.IsDefined(errorPolicy)) throw Invalid("Invalid recovery error policy.");
        const AuthorityBit fileAuthority = AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement | AuthorityBit.Read | AuthorityBit.Add | AuthorityBit.Update | AuthorityBit.Delete;
        foreach (var file in files) Require(file.File, ObjectType.File, fileAuthority);
        using var allocations = AllocateRecoveryFiles(files, cancellationToken);
        using var connection = factory.Open();
        // Native apply/remove bypasses triggers. FK actions must also not replay twice:
        // recover the explicitly selected dependent records, then check all constraints.
        Pragma(connection, "PRAGMA foreign_keys=OFF");
        try
        {
            using var transaction = BeginWrite(connection, cancellationToken);
            var journal = InspectCore(connection, transaction, journalName);
            var first = ReceiverCore(connection, transaction, firstReceiver ?? journal.Receiver);
            var last = ReceiverCore(connection, transaction, lastReceiver ?? journal.Receiver);
            if (first.JournalId != journal.Id || last.JournalId != journal.Id) throw Invalid("Receivers must belong to this journal's retained chain.");
            using var query = Command(connection, transaction, "SELECT chain FROM sys_journal_receivers WHERE id=$id"); query.Parameters.AddWithValue("$id", first.Id);
            var firstChain = Convert.ToInt64(query.ExecuteScalar()); query.Parameters["$id"].Value = last.Id; var lastChain = Convert.ToInt64(query.ExecuteScalar());
            if (remove ? firstChain < lastChain : firstChain > lastChain) throw Invalid("Apply receiver ranges ascend; remove receiver ranges descend.");
            var lowChain = Math.Min(firstChain, lastChain); var highChain = Math.Max(firstChain, lastChain);
            query.Parameters.Clear(); query.Parameters.AddWithValue("$journal", journal.Id); query.Parameters.AddWithValue("$first", lowChain); query.Parameters.AddWithValue("$last", highChain);
            query.CommandText = "SELECT library,name FROM sys_journal_receivers WHERE journal_id=$journal AND chain BETWEEN $first AND $last ORDER BY chain LIMIT 2046";
            var receivers = new List<QualifiedName>(); using (var reader = query.ExecuteReader()) while (reader.Read()) receivers.Add(new(reader.GetString(0), reader.GetString(1)));
            if (receivers.Count > 2045 || receivers.Count != highChain - lowChain + 1) throw Invalid("Receiver chain is incomplete or exceeds the 2045-receiver limit.");
            foreach (var receiver in receivers) Require(receiver, ObjectType.JournalReceiver, Authorities.UseBits);
            const string range = " FROM sys_journal_entries e JOIN sys_journal_receivers r ON r.id=e.receiver_id AND r.journal_id=e.journal_id WHERE e.journal_id=$journal AND r.chain BETWEEN $first AND $last";
            query.CommandText = "SELECT min(e.sequence),max(e.sequence)" + range;
            long minimum, maximum;
            using (var reader = query.ExecuteReader())
            {
                reader.Read(); if (reader.IsDBNull(0)) throw Invalid("Receiver range contains no entries.");
                if (reader.GetFieldType(0) != typeof(long) || reader.GetFieldType(1) != typeof(long)) throw Invalid("Journal sequence numbers must be stored as integers.");
                minimum = reader.GetInt64(0); maximum = reader.GetInt64(1);
            }
            var from = fromSequence ?? (remove ? maximum : minimum); var to = toSequence ?? (remove ? minimum : maximum);
            if (from < 1 || to < 1 || (remove ? from < to : from > to)) throw Invalid("Apply ranges ascend; remove ranges descend, with positive endpoints.");
            var low = Math.Min(from, to); var high = Math.Max(from, to);
            query.Parameters.AddWithValue("$low", low); query.Parameters.AddWithValue("$high", high);
            query.CommandText = "SELECT count(*)" + range + " AND e.sequence BETWEEN $low AND $high";
            if (Convert.ToInt64(query.ExecuteScalar()) != high - low + 1) throw Invalid("Recovery range is incomplete, purged, or outside the selected receivers.");
            var targets = files.GroupBy(file => file.File).Select(group => RecoveryTarget.Load(connection, transaction, journal.Id, group.ToArray(), remove)).ToDictionary(target => target.Binding);
            var cycles = new RecoveryCycles(connection, transaction, journal.Id, lowChain, highChain, cancellationToken);
            var boundary = honorCommitBoundaries ? cycles.CheckRange(from, to, remove) : null;
            var incomplete = boundary is null ? new HashSet<(string, string)>() : cycles.Members(boundary.Cycle);
            var members = new Dictionary<(string Binding, string Member), RecoveryMember>();
            var failures = new List<JournalRecoveryFailure>(); var failedMembers = new HashSet<(string Binding, string Member)>();
            long cursor = from, changes = 0, firstChanged = 0, lastChanged = 0; var stoppedForError = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint();
                query.Parameters.AddWithValue("$cursor", cursor);
                query.CommandText = "SELECT e.sequence,e.version,e.code,e.entry_type,e.binding_id,e.member,e.record_number,e.schema_description,e.before_image,e.after_image,e.commit_cycle" + range +
                    (boundary is null ? "" : remove ? " AND e.sequence>$boundary" : " AND e.sequence<$boundary") +
                    (remove ? " AND e.sequence BETWEEN $low AND $cursor ORDER BY e.sequence DESC LIMIT 1" : " AND e.sequence BETWEEN $cursor AND $high ORDER BY e.sequence ASC LIMIT 1");
                if (boundary is not null && !query.Parameters.Contains("$boundary")) query.Parameters.AddWithValue("$boundary", boundary.StoppedBeforeSequence);
                RecoveryEntry entry;
                using (var reader = query.ExecuteReader())
                {
                    if (!reader.Read()) break;
                    if (reader.GetFieldType(0) != typeof(long) || !reader.IsDBNull(6) && reader.GetFieldType(6) != typeof(long))
                        throw Invalid("Journal sequence and record numbers must be stored as integers.");
                    if (!reader.IsDBNull(10) && reader.GetFieldType(10) != typeof(string)) throw Invalid("Commitment cycle identity requires text storage.");
                    string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
                    entry = new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), Text(4), Text(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6), Text(7), Text(8), Text(9), Text(10));
                }
                query.Parameters.RemoveAt("$cursor");
                cycles.Validate(entry, honorCommitBoundaries);
                var stop = false;
                if (entry.Binding is { } binding && targets.TryGetValue(binding, out var target) && target.Includes(entry.Member) && !failedMembers.Contains((binding, entry.Member!)))
                {
                    var key = (binding, entry.Member!); var existing = members.ContainsKey(key);
                    using var savepoint = Command(connection, transaction, "SAVEPOINT ipc_recovery_entry"); savepoint.ExecuteNonQuery();
                    try
                    {
                        if (entry.Version != 1 || entry.Code != "R" || entry.Type is not ("PT" or "UP" or "UB" or "DL")) throw Invalid("Unsupported record or prior recovery boundary.");
                        ValidateUpdatePair(connection, transaction, journal.Id, entry, low, high);
                        if (remove && entry.Type == "UP" && entry.Before is null) throw Invalid("Update has no before-image.");
                        if (remove ? entry.Type != "UP" : entry.Type != "UB")
                        {
                            if (!members.TryGetValue(key, out var member))
                            {
                                member = RecoveryMember.Load(connection, transaction, target, entry.Member!, entry.Schema);
                                members.Add(key, member); member.SuspendTriggers(connection, transaction);
                                RecoveryMarker(connection, transaction, journal.Id, member, remove ? "SR" : "SA", from, to, 0);
                            }
                            var change = entry;
                            if (remove && entry.Type == "UB" && errorPolicy == JournalRecoveryErrorPolicy.Atomic)
                            {
                                using var partner = Command(connection, transaction, "SELECT e.after_image" + range + " AND e.sequence=$pair AND e.entry_type='UP' AND e.binding_id=$binding AND e.member=$member AND e.record_number=$rrn AND e.commit_cycle IS $cycle");
                                partner.Parameters.AddWithValue("$journal", journal.Id); partner.Parameters.AddWithValue("$first", lowChain); partner.Parameters.AddWithValue("$last", highChain);
                                partner.Parameters.AddWithValue("$pair", entry.Sequence + 1); partner.Parameters.AddWithValue("$binding", binding); partner.Parameters.AddWithValue("$member", entry.Member!); partner.Parameters.AddWithValue("$rrn", entry.Rrn!.Value);
                                partner.Parameters.AddWithValue("$cycle", (object?)entry.CommitCycle ?? DBNull.Value);
                                change = entry with { After = partner.ExecuteScalar() as string };
                            }
                            member.ValidateSchema(entry.Schema); member.Apply(connection, transaction, change, remove, errorPolicy == JournalRecoveryErrorPolicy.Atomic);
                            member.Changes++; if (member.FirstSequence == 0) member.FirstSequence = entry.Sequence; member.LastSequence = entry.Sequence;
                            changes++; if (firstChanged == 0) firstChanged = entry.Sequence; lastChanged = entry.Sequence;
                        }
                        savepoint.CommandText = "RELEASE ipc_recovery_entry"; savepoint.ExecuteNonQuery();
                    }
                    catch (Exception error) when (error is JsonException or FormatException or OverflowException or InvalidOperationException or Ipc.Core.Messages.CpfException { MessageId: "IPC0210" } or SqliteException { SqliteErrorCode: 19 })
                    {
                        savepoint.CommandText = "ROLLBACK TO ipc_recovery_entry; RELEASE ipc_recovery_entry"; savepoint.ExecuteNonQuery();
                        if (!existing) members.Remove(key);
                        if (errorPolicy == JournalRecoveryErrorPolicy.Atomic) throw Invalid("Recovery failed at sequence " + entry.Sequence + ": " + error.Message);
                        failures.Add(new(target.File, entry.Sequence, error.Message, entry.Member)); failedMembers.Add(key); stop = errorPolicy == JournalRecoveryErrorPolicy.End;
                    }
                }
                if (stop) { stoppedForError = true; break; }
                if (entry.Sequence == to) break;
                cursor = remove ? entry.Sequence - 1 : entry.Sequence + 1;
            }
            var reachedBoundary = stoppedForError ? null : boundary;
            if (changes == 0 && failures.Count == 0 && reachedBoundary is null) throw new Ipc.Core.Messages.CpfException("CPF7069", "No entries applied or removed using journal " + journalName + ".");
            using (var check = Command(connection, transaction, "PRAGMA foreign_key_check"))
            using (var reader = check.ExecuteReader()) if (reader.Read()) throw Invalid("Recovered data violates a foreign key; include all dependent files or choose another recovery range.");
            foreach (var member in members.Values)
            {
                cancellationToken.ThrowIfCancellationRequested(); member.RestoreTriggers(connection, transaction);
                RecoveryMarker(connection, transaction, journal.Id, member, remove ? "RC" : "AY", from, to, member.Changes,
                    failedMembers.Contains((member.Target.Binding, member.Member)) || errorPolicy == JournalRecoveryErrorPolicy.End && failures.Count != 0,
                    reachedBoundary is not null && incomplete.Contains((member.Target.Binding, member.Member)));
                using var touch = Command(connection, transaction, "UPDATE sys_objects SET changed=ipc_journal_timestamp() WHERE lib=$library AND name=$name AND type='*FILE'"); Bind(touch, member.Target.File); touch.ExecuteNonQuery();
            }
            Require(journalName, ObjectType.Journal, Authorities.UseBits | AuthorityBit.Add);
            foreach (var file in files) Require(file.File, ObjectType.File, fileAuthority);
            foreach (var receiver in receivers) Require(receiver, ObjectType.JournalReceiver, Authorities.UseBits);
            if (reachedBoundary is not null) RecoveryBoundaryEvent(connection, transaction, journalName, reachedBoundary, remove, changes);
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return new(changes, firstChanged, lastChanged, failures.Count == 0 ? null : failures, reachedBoundary is null ? null : [reachedBoundary]);
        }
        finally { Pragma(connection, "PRAGMA foreign_keys=ON"); }
    }
    private static void Pragma(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private sealed record RecoveryEntry(long Sequence, int Version, string Code, string Type, string? Binding, string? Member, long? Rrn, string? Schema, string? Before, string? After, string? CommitCycle);
    private static void ValidateUpdatePair(SqliteConnection connection, SqliteTransaction transaction, string journal, RecoveryEntry entry, long low, long high)
    {
        if (entry.Type != "UB" && !(entry.Type == "UP" && entry.Before is not null)) return;
        if (entry.Type == "UB" && entry.Sequence == long.MaxValue) throw Invalid("Unpaired before-image.");
        var other = entry.Type == "UB" ? entry.Sequence + 1 : entry.Sequence - 1;
        // Individual native UP/UB entries may be valid recovery endpoints. Validate
        // a pair together when both entries are included, without widening the range.
        if (other < low || other > high) return;
        using var command = Command(connection, transaction, "SELECT count(*) FROM sys_journal_entries WHERE journal_id=$journal AND sequence=$sequence AND code='R' AND entry_type=$type AND binding_id=$binding AND member=$member AND record_number=$rrn AND schema_description=$schema AND before_image=$before AND commit_cycle IS $cycle");
        command.Parameters.AddWithValue("$journal", journal); command.Parameters.AddWithValue("$sequence", other); command.Parameters.AddWithValue("$type", entry.Type == "UB" ? "UP" : "UB");
        command.Parameters.AddWithValue("$binding", (object?)entry.Binding ?? DBNull.Value); command.Parameters.AddWithValue("$member", (object?)entry.Member ?? DBNull.Value);
        command.Parameters.AddWithValue("$rrn", (object?)entry.Rrn ?? DBNull.Value); command.Parameters.AddWithValue("$schema", (object?)entry.Schema ?? DBNull.Value); command.Parameters.AddWithValue("$before", (object?)entry.Before ?? DBNull.Value);
        command.Parameters.AddWithValue("$cycle", (object?)entry.CommitCycle ?? DBNull.Value);
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw Invalid("Missing or mismatched update before/after pair.");
    }
    private static void RecoveryMarker(SqliteConnection connection, SqliteTransaction transaction, string journal, RecoveryMember member, string type, long from, long to, long count, bool failed = false, bool incompleteCommit = false)
    {
        using var command = Command(connection, transaction, """
            SELECT CASE WHEN (SELECT sequence FROM sys_journals WHERE id=$journal)=9223372036854775807 THEN 1 ELSE 0 END;
            """); command.Parameters.AddWithValue("$journal", journal);
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw Invalid("Journal sequence exhausted.");
        command.CommandText = """
            UPDATE sys_journals SET sequence=sequence+1 WHERE id=$journal;
            INSERT INTO sys_journal_entries(journal_id,sequence,receiver_id,code,entry_type,captured_at,principal,job,object_library,object_name,member,binding_id,user_data)
            SELECT id,sequence,current_receiver,'F',$type,ipc_journal_timestamp(),ipc_actor(),ipc_job(),$library,$name,$member,$binding,$data FROM sys_journals WHERE id=$journal;
            """;
        command.Parameters.AddWithValue("$type", type); Bind(command, member.Target.File); command.Parameters.AddWithValue("$member", member.Member); command.Parameters.AddWithValue("$binding", member.Target.Binding);
        command.Parameters.AddWithValue("$data", JsonSerializer.SerializeToUtf8Bytes(new { Format = "IPCREC1", From = from, To = to, Changes = count, First = member.FirstSequence, Last = member.LastSequence, Flag = failed ? 1 : 0, IncompleteCommit = incompleteCommit })); command.ExecuteNonQuery();
    }
    private sealed record RecoveryTarget(QualifiedName File, string Binding, IReadOnlySet<string> Members, string Definition)
    {
        internal bool Includes(string? member) => member is not null && (Members.Contains("*ALL") || Members.Contains(member));
        internal static RecoveryTarget Load(SqliteConnection connection, SqliteTransaction transaction, string journal, IReadOnlyList<JournalFileSelection> files, bool remove)
        {
            var file = files[0];
            using var command = Command(connection, transaction, "SELECT b.id,b.images,d.def,o.attribute FROM sys_journal_bindings b JOIN sys_file_defs d ON d.lib=b.library AND d.name=b.name AND d.type='*FILE' JOIN sys_objects o ON o.lib=b.library AND o.name=b.name AND o.type='*FILE' WHERE b.journal_id=$journal AND b.library=$library AND b.name=$name");
            command.Parameters.AddWithValue("$journal", journal); Bind(command, file.File); string binding, definition;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || reader.GetString(3) is not ("*PF" or "*SRCPF")) throw Invalid("Recovery requires a physical file attached to the specified journal.");
                if (remove && reader.GetString(1) != "*BOTH") throw Invalid("Remove requires IMAGES(*BOTH).");
                binding = reader.GetString(0); definition = reader.GetString(2);
            }
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var selection in files)
            {
                var member = selection.Member;
                if (member == "*FIRST") { command.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$library AND name=$name AND type='*FILE' ORDER BY created,mbr LIMIT 1"; member = command.ExecuteScalar() as string ?? throw Invalid("File has no members."); }
                if (member != "*ALL" && !ObjectName.IsValid(member)) throw Invalid("Recovery member requires a name, *FIRST or *ALL.");
                members.Add(member);
            }
            return new(file.File, binding, members, definition);
        }
    }
    private sealed class RecoveryMember(RecoveryTarget target, string member, IReadOnlyList<JournalColumn> columns, string rowid)
    {
        internal RecoveryTarget Target => target;
        internal string Member => member;
        internal long Changes { get; set; }
        internal long FirstSequence { get; set; }
        internal long LastSequence { get; set; }
        private string Table => target.File.Library + "." + target.File.Name + "." + member;
        private readonly List<(string Name, string Sql)> _triggers = [];
        private string? _validatedSchema;
        internal static RecoveryMember Load(SqliteConnection connection, SqliteTransaction transaction, RecoveryTarget target, string member, string? schema)
        {
            if (!ObjectName.IsValid(member)) throw Invalid("Invalid recovery member.");
            using var command = Command(connection, transaction, "SELECT count(*) FROM sys_file_members WHERE lib=$library AND name=$name AND type='*FILE' AND mbr=$member"); Bind(command, target.File); command.Parameters.AddWithValue("$member", member);
            if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw Invalid("Recovery member no longer exists; member/object recreation is not implemented.");
            var table = target.File.Library + "." + target.File.Name + "." + member; var columns = JournalHooks.Columns(connection, transaction, table);
            var rowid = new[] { "rowid", "_rowid_", "oid" }.FirstOrDefault(name => columns.All(c => !c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) ?? throw Invalid("Record-number aliases are shadowed.");
            var result = new RecoveryMember(target, member, columns, rowid); result.ValidateSchema(schema); return result;
        }
        internal void ValidateSchema(string? schema)
        {
            if (schema is not null && schema == _validatedSchema) return;
            if (schema is null || Encoding.UTF8.GetByteCount(schema) > 1048576) throw Invalid("Missing or oversized recovery schema.");
            using var parsed = JsonDocument.Parse(schema); var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("Columns", out var description) || description.ValueKind != JsonValueKind.Array || !root.TryGetProperty("Definition", out var definition) || definition.ValueKind != JsonValueKind.String)
                throw Invalid("Invalid recovery schema snapshot.");
            if (!columns.SequenceEqual(JsonSerializer.Deserialize<JournalColumn[]>(description)!)) throw Invalid("Member column layout differs from the journal schema.");
            var recorded = JsonNode.Parse(definition.GetString()!) as JsonObject ?? throw Invalid("Invalid recorded file definition.");
            var current = JsonNode.Parse(target.Definition) as JsonObject ?? throw Invalid("Invalid current file definition.");
            foreach (var key in new[] { "formats", "ccsid", "attribute", "unique", "excludeNullKeys", "logical" })
                if (!JsonNode.DeepEquals(recorded[key], current[key])) throw Invalid("Record format differs from the journal schema.");
            _validatedSchema = schema;
        }
        internal void SuspendTriggers(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = Command(connection, transaction, "SELECT name,sql FROM sqlite_schema WHERE type='trigger' AND tbl_name=$table ORDER BY rowid"); command.Parameters.AddWithValue("$table", Table);
            using (var reader = command.ExecuteReader()) while (reader.Read()) _triggers.Add((reader.GetString(0), reader.GetString(1)));
            foreach (var trigger in _triggers) { command.CommandText = "DROP TRIGGER " + JournalHooks.Quote(trigger.Name); command.ExecuteNonQuery(); }
        }
        internal void RestoreTriggers(SqliteConnection connection, SqliteTransaction transaction)
        { foreach (var trigger in _triggers) { using var command = Command(connection, transaction, trigger.Sql); command.ExecuteNonQuery(); } }
        internal void Apply(SqliteConnection connection, SqliteTransaction transaction, RecoveryEntry entry, bool remove, bool strict)
        {
            if (entry.Rrn is not > 0) throw Invalid("Invalid physical record number.");
            var before = Image(entry.Before); var after = Image(entry.After);
            if (entry.Type == "PT" && (before is not null || after is null) || entry.Type == "DL" && (before is null || after is not null) || entry.Type == "UP" && after is null)
                throw Invalid("Missing or invalid record images.");
            var operation = entry.Type switch { "PT" => remove ? "DELETE" : "INSERT", "DL" => remove ? "INSERT" : "DELETE", _ => "UPDATE" };
            var expected = remove ? after : before; var desired = remove ? before : after;
            if (entry.Type == "UB" && before is null) throw Invalid("Update has no before-image.");
            var actual = ReadImage(connection, transaction, entry.Rrn.Value);
            if (operation == "INSERT" ? actual is not null : actual is null) throw Invalid("Record presence differs from the recovery point.");
            if (strict && actual is not null && expected is not null && !actual.SequenceEqual(expected)) throw Invalid("Current record differs from the expected recovery image.");
            if (operation != "DELETE" && (desired is null || !columns.Select(c => c.Name).SequenceEqual(desired.Select(c => c.Name)))) throw Invalid("Incomplete or mismatched recovery image.");
            using var command = Command(connection, transaction, ""); command.Parameters.AddWithValue("$rrn", entry.Rrn.Value);
            var writable = columns.Select((column, index) => (column, index)).Where(item => item.column.Hidden == 0).ToArray();
            if (operation != "DELETE") foreach (var item in writable) command.Parameters.AddWithValue("$v" + item.index, CellValue(desired![item.index]));
            var names = string.Join(',', writable.Select(item => JournalHooks.Quote(item.column.Name))); var values = string.Join(',', writable.Select(item => "$v" + item.index));
            var table = JournalHooks.Quote(Table); var rrn = JournalHooks.Quote(rowid);
            command.CommandText = operation switch {
                "INSERT" => $"INSERT INTO {table} ({rrn},{names}) VALUES($rrn,{values})",
                "UPDATE" => $"UPDATE {table} SET " + string.Join(',', writable.Select(item => JournalHooks.Quote(item.column.Name) + "=$v" + item.index)) + $" WHERE {rrn}=$rrn",
                _ => $"DELETE FROM {table} WHERE {rrn}=$rrn" };
            if (command.ExecuteNonQuery() != 1) throw Invalid("Recovery did not change exactly one record.");
            var restored = ReadImage(connection, transaction, entry.Rrn.Value);
            if (operation == "DELETE" ? restored is not null : restored is null || !restored.SequenceEqual(desired!)) throw Invalid("Recovered storage/generated values differ from the journal image.");
        }
        private IReadOnlyList<JournalCell>? ReadImage(SqliteConnection connection, SqliteTransaction transaction, long rrn)
        {
            using var command = Command(connection, transaction, "SELECT " + JournalHooks.Image("r", columns) + " FROM " + JournalHooks.Quote(Table) + " r WHERE r." + JournalHooks.Quote(rowid) + "=$rrn"); command.Parameters.AddWithValue("$rrn", rrn);
            return Image(command.ExecuteScalar() as string);
        }
        private static object CellValue(JournalCell cell) => cell.StorageType switch {
            "null" when cell.Value is null => DBNull.Value,
            "text" when cell.Value is not null => cell.Value,
            "integer" => long.Parse(cell.Value!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            "real" => BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(cell.Value!, NumberStyles.HexNumber, CultureInfo.InvariantCulture))),
            "blob" => Convert.FromHexString(cell.Value!), _ => throw Invalid("Invalid recovery storage type.") };
    }
}
