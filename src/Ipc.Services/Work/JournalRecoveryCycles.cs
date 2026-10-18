using System.Text.Json;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed partial class JournalStore
{
    private RecoveryAllocations AllocateRecoveryFiles(IReadOnlyList<JournalFileSelection> files, CancellationToken cancellationToken)
    {
        var result = new RecoveryAllocations(); var locks = new JobLockStore(factory);
        try
        {
            foreach (var file in files.Select(file => file.File).Distinct().OrderBy(file => file.Library, StringComparer.Ordinal).ThenBy(file => file.Name.Value, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested(); var resource = new JobLockResource(file.Library, file.Name.Value, ObjectType.File);
                locks.RequireNoPersistentAllocation(resource);
                if (OperationIdentity.Current?.Job is { } job)
                    result.Leases.Add(locks.AcquireWithLifetime(job, resource, JobLockMode.Exclusive, TimeSpan.Zero, cancellationToken, "JournalRecovery", Guid.NewGuid().ToString("N")));
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    private sealed class RecoveryAllocations : IDisposable
    {
        internal List<IDisposable> Leases { get; } = [];
        public void Dispose()
        {
            List<Exception>? errors = null;
            foreach (var lease in Leases.AsEnumerable().Reverse())
                try { lease.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
            if (errors is not null) throw new AggregateException("Could not release all recovery allocations.", errors);
        }
    }

    // IPCCMT1 publications are contiguous within each journal. Validate the frame
    // without materializing record images, and never look outside authorized receivers.
    private sealed class RecoveryCycles(SqliteConnection connection, SqliteTransaction transaction, string journal,
        long firstReceiver, long lastReceiver, CancellationToken cancellationToken)
    {
        private const string Source = " FROM sys_journal_entries e JOIN sys_journal_receivers r ON r.id=e.receiver_id AND r.journal_id=e.journal_id WHERE e.journal_id=$journal AND r.chain BETWEEN $first AND $last";
        private readonly Dictionary<string, Cycle> _cache = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();
        private sealed record Cycle(string Id, long? Start, long? End, HashSet<(string, string)> Members);

        internal JournalRecoveryBoundary? CheckRange(long from, long to, bool remove)
        {
            var start = At(from);
            if (start is not null && start.Start != from && start.End != from)
                throw Invalid("Starting sequence is inside commitment cycle " + start.Id + "; choose a C/SC or C/CM boundary.");
            var end = At(to);
            if (end is null || end.Start == to || end.End == to) return null;
            var stop = remove ? end.End : end.Start;
            if (stop is null) throw Invalid("The requested ending cycle has no retained starting recovery boundary.");
            return new(end.Id, stop.Value, to);
        }
        internal HashSet<(string, string)> Members(string id) => Load(id).Members;
        private Cycle? At(long sequence)
        {
            using var command = Query("SELECT e.code,e.commit_cycle" + Source + " AND e.sequence=$sequence"); command.Parameters.AddWithValue("$sequence", sequence);
            string? id; string code;
            using (var reader = command.ExecuteReader())
            { if (!reader.Read()) return null; code = reader.GetString(0); id = reader.IsDBNull(1) ? null : reader.GetString(1); }
            if (id is not null) return Load(id);
            if (code == "C") throw Invalid("Unsupported commitment-control entry without a local cycle identity.");
            // Detect a stripped record tag at an endpoint as well as a tagged one.
            command.CommandText = "SELECT e.entry_type,e.commit_cycle" + Source + " AND e.code='C' AND e.sequence<$sequence ORDER BY e.sequence DESC LIMIT 1";
            using var previous = command.ExecuteReader();
            if (!previous.Read() || previous.GetString(0) != "SC") return null;
            if (previous.IsDBNull(1)) throw Invalid("Unsupported commitment-control start entry.");
            id = previous.GetString(1); previous.Close();
            var cycle = Load(id);
            if (cycle.End is null || cycle.End >= sequence) throw Invalid("Untagged record inside a commitment cycle.");
            return null;
        }
        internal void Validate(RecoveryEntry entry, bool honorBoundaries)
        {
            if (entry.CommitCycle is null)
            { if (entry.Code == "C") throw Invalid("Unsupported commitment-control entry without a local cycle identity."); return; }
            if (!Guid.TryParseExact(entry.CommitCycle, "N", out _) || entry.Version != 1 || entry.Code is not ("C" or "R"))
                throw Invalid("Invalid commitment cycle record.");
            if (honorBoundaries || entry.Code == "C")
            {
                var cycle = Load(entry.CommitCycle);
                if (honorBoundaries && entry.Code == "R" && (cycle.Start is null || cycle.End is null)) throw Invalid("Incomplete commitment cycle in recovery range.");
            }
        }
        private Cycle Load(string id)
        {
            if (_cache.TryGetValue(id, out var existing)) return existing;
            if (!Guid.TryParseExact(id, "N", out _)) throw Invalid("Invalid local commitment cycle identity.");
            using var command = Query("""
                SELECT e.sequence,e.version,e.code,e.entry_type,e.user_data,e.job,e.binding_id,e.member,e.record_number,
                    e.before_image IS NOT NULL,e.after_image IS NOT NULL,length(e.user_data)
                """ + Source + " AND e.commit_cycle=$cycle ORDER BY e.sequence"); command.Parameters.AddWithValue("$cycle", id);
            using var reader = command.ExecuteReader();
            long? start = null, end = null, previous = null; string? definition = null, job = null;
            (string Binding, string Member, long Rrn)? update = null;
            var members = new HashSet<(string, string)>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint();
                if (reader.GetFieldType(0) != typeof(long) || reader.GetInt32(1) != 1 || reader.GetFieldType(2) != typeof(string) || reader.GetFieldType(3) != typeof(string))
                    throw Invalid("Invalid local commitment sequence or entry type.");
                var sequence = reader.GetInt64(0); var code = reader.GetString(2); var type = reader.GetString(3);
                if (end is not null || previous is { } prior && (prior == long.MaxValue || sequence != prior + 1))
                    throw Invalid("Missing, interleaved or unsupported local commitment cycle entries.");
                previous = sequence;
                if (reader.IsDBNull(5) || reader.GetFieldType(5) != typeof(string)) throw Invalid("Commitment cycle has no valid owning job.");
                job ??= reader.GetString(5); if (reader.GetString(5) != job) throw Invalid("Commitment cycle job identity differs between entries.");
                if (code == "C")
                {
                    if (type is not ("SC" or "CM") || type == "SC" && (start is not null || members.Count != 0) || update is not null ||
                        reader.IsDBNull(11) || reader.GetInt64(11) is < 1 or > 4096 || reader.GetFieldType(4) != typeof(byte[]) ||
                        !reader.IsDBNull(6) || !reader.IsDBNull(7) || !reader.IsDBNull(8) || reader.GetBoolean(9) || reader.GetBoolean(10))
                        throw Invalid("Invalid local commitment control marker.");
                    var markerDefinition = MarkerDefinition((byte[])reader.GetValue(4), id);
                    definition ??= markerDefinition; if (definition != markerDefinition) throw Invalid("Commitment definition differs between cycle markers.");
                    if (type == "SC") start = sequence; else end = sequence;
                }
                else
                {
                    if (code != "R" || reader.IsDBNull(6) || reader.IsDBNull(7) || reader.IsDBNull(8) || reader.GetFieldType(6) != typeof(string) || reader.GetFieldType(7) != typeof(string) || reader.GetFieldType(8) != typeof(long) || reader.GetInt64(8) < 1 ||
                        !Guid.TryParseExact(reader.GetString(6), "N", out _) || !ObjectName.IsValid(reader.GetString(7)))
                        throw Invalid("Invalid local commitment record identity.");
                    var key = (reader.GetString(6), reader.GetString(7), reader.GetInt64(8));
                    var before = reader.GetBoolean(9); var after = reader.GetBoolean(10);
                    if (type switch { "PT" => before || !after, "DL" or "UB" => !before || after, "UP" => !before || !after, _ => true })
                        throw Invalid("Invalid local commitment record images.");
                    if (update is { } pending && (type != "UP" || pending != key) || type == "UP" && update is null && !(start is null && members.Count == 0))
                        throw Invalid("Missing or mismatched commitment update pair.");
                    update = type == "UB" ? key : null; members.Add((key.Item1, key.Item2));
                }
            }
            if (previous is null) throw Invalid("Commitment cycle is not retained in the selected receivers.");
            if (end is not null && update is not null) throw Invalid("Incomplete commitment update pair.");
            var result = new Cycle(id, start, end, members);
            if (_order.Count == 8) _cache.Remove(_order.Dequeue());
            _cache.Add(id, result); _order.Enqueue(id); return result;
        }
        private static string MarkerDefinition(byte[] data, string cycle)
        {
            try
            {
                using var document = JsonDocument.Parse(data); var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 ||
                    !root.TryGetProperty("Format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != "IPCCMT1" ||
                    !root.TryGetProperty("Cycle", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != cycle ||
                    !root.TryGetProperty("Definition", out var definition) || definition.ValueKind != JsonValueKind.String || !Guid.TryParseExact(definition.GetString(), "N", out _))
                    throw Invalid("Invalid local commitment marker data.");
                return definition.GetString()!;
            }
            catch (JsonException) { throw Invalid("Invalid local commitment marker JSON."); }
        }
        private SqliteCommand Query(string sql)
        {
            var command = Command(connection, transaction, sql); command.Parameters.AddWithValue("$journal", journal);
            command.Parameters.AddWithValue("$first", firstReceiver); command.Parameters.AddWithValue("$last", lastReceiver); return command;
        }
    }
    private static void RecoveryBoundaryEvent(SqliteConnection connection, SqliteTransaction transaction, QualifiedName journal,
        JournalRecoveryBoundary boundary, bool remove, long changes)
    {
        using var command = Command(connection, transaction, "INSERT INTO sys_events(at,kind,principal,job,payload) VALUES(ipc_journal_timestamp(),'journal.recovery.boundary',coalesce(ipc_actor(),'*SYSTEM'),ipc_job(),$data)");
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(new { Journal = journal.ToString(), Remove = remove, Changes = changes, Boundary = boundary })); command.ExecuteNonQuery();
    }
}
