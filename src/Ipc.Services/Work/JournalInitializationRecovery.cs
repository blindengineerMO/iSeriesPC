using System.Text;
using System.Text.Json;
using Ipc.Core.Work;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed partial class JournalStore
{
    private sealed partial class RecoveryMember
    {
        internal void ApplyInitialization(SqliteConnection connection, SqliteTransaction transaction, RecoveryEntry entry,
            bool remove, bool strict, CancellationToken cancellationToken)
        {
            if (entry.Data is null || entry.Data.Length > MaximumEntryBytes || entry.Before is not null || entry.After is not null)
                throw Invalid("Missing or invalid initialization payload.");
            using var document = JsonDocument.Parse(entry.Data); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("Initialization payload must be an object.");
            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            string[] expectedNames = ["Format", "BeforeExtent", "AfterExtent", "Count", "Flag", "DefaultImage"];
            if (names.Length != expectedNames.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                !names.ToHashSet(StringComparer.Ordinal).SetEquals(expectedNames)) throw Invalid("Invalid initialization payload fields.");
            if (root.GetProperty("Format").ValueKind != JsonValueKind.String || root.GetProperty("Format").GetString() != "IPCINIT2")
                throw Invalid("Unsupported initialization payload; recovery requires IPCINIT2 with its captured schema.");
            long Number(string name)
            {
                var value = root.GetProperty(name);
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw Invalid("Initialization counts must be integers.");
            }
            var before = Number("BeforeExtent"); var after = Number("AfterExtent"); var count = Number("Count"); var flag = Number("Flag");
            if (before < 0 || after > RecordSlots.MaximumNumber || after <= before || count != after - before ||
                entry.Rrn != after || flag is not (0 or 1)) throw Invalid("Invalid initialization range or type.");
            var imageElement = root.GetProperty("DefaultImage");
            if (flag == 1 ? imageElement.ValueKind != JsonValueKind.Null : imageElement.ValueKind != JsonValueKind.String)
                throw Invalid("Initialization default image does not match its type.");
            var desired = flag == 0 ? Image(imageElement.GetString()) : null;
            if (flag == 0 && (desired is null || !columns.Select(column => column.Name).SequenceEqual(desired.Select(cell => cell.Name))))
                throw Invalid("Incomplete or mismatched initialization default image.");
            var values = desired?.Select(CellValue).ToArray();
            if (values is not null)
            {
                var bytes = values.Sum(value => value is string text ? (long)Encoding.UTF8.GetByteCount(text) : value is byte[] raw ? raw.Length : 16L);
                if (count > 1000000 || bytes > 67108864 / count) throw Invalid("Initialization recovery exceeds one million default records or 64 MiB.");
            }
            var identity = RecordSlots.Identity(connection, transaction, target.File.Library, target.File.Name.Value, member)
                ?? throw Invalid("Initialization member identity is unavailable.");
            if (remove && RecordSlots.HighWater(connection, transaction, identity) < after)
                throw Invalid("Initialization positions are not allocated at the removal point.");
            var table = JournalHooks.Quote(Table); var numberColumn = JournalHooks.Quote(rowid);
            using var command = Command(connection, transaction, "");
            command.Parameters.AddWithValue("$first", before + 1); command.Parameters.AddWithValue("$last", after);
            var range = numberColumn + " BETWEEN $first AND $last";
            command.CommandText = "SELECT count(*) FROM " + table + " WHERE " + range;
            var existing = Convert.ToInt64(SqliteCancellation.Run(connection, cancellationToken, command.ExecuteScalar));
            if (flag == 1 || !remove)
            {
                if (existing != 0) throw Invalid("Initialization positions contain records at the recovery point.");
            }
            else if (existing != count) throw Invalid("Initialized default records are missing at the removal point.");
            if (remove && desired is not null && strict)
            {
                command.CommandText = "SELECT " + JournalHooks.Image("p", columns) + " FROM " + table + " p WHERE " + range;
                SqliteCancellation.Run(connection, cancellationToken, () => {
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint();
                        if (Image(reader.GetString(0)) is not { } actual || !actual.SequenceEqual(desired))
                            throw Invalid("Initialized record differs from its captured default image.");
                    }
                    return 0;
                });
            }
            if (flag == 0 && remove)
            {
                command.CommandText = "DELETE FROM " + table + " WHERE " + range;
                if (SqliteCancellation.Run(connection, cancellationToken, command.ExecuteNonQuery) != count)
                    throw Invalid("Initialization removal did not delete the expected records.");
            }
            else if (flag == 0)
            {
                var writable = columns.Select((column, index) => (column, index)).Where(item => item.column.Hidden == 0).ToArray();
                command.Parameters.AddWithValue("$rrn", 0L);
                foreach (var item in writable) command.Parameters.AddWithValue("$v" + item.index, values![item.index]);
                command.CommandText = "INSERT INTO " + table + " (" + numberColumn + "," + string.Join(',', writable.Select(item => JournalHooks.Quote(item.column.Name))) +
                    ") VALUES ($rrn," + string.Join(',', writable.Select(item => "$v" + item.index)) + ")";
                for (var number = before + 1; number <= after; number++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint();
                    command.Parameters["$rrn"].Value = number;
                    if (SqliteCancellation.Run(connection, cancellationToken, command.ExecuteNonQuery) != 1 ||
                        ReadImage(connection, transaction, number) is not { } restored || !restored.SequenceEqual(desired!))
                        throw Invalid("Initialized storage/generated values differ from the captured default image.");
                }
            }
            // Removing initialization deletes its records; it does not clear or renumber the member.
            // Reapplication can fill those same deleted positions without shrinking a later extent.
            if (!remove) RecordSlots.Reserve(connection, transaction, identity, after);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
