using System.Globalization;
using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Services.Work;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    /// <summary>Stages a bounded source snapshot, then copies it in one transaction.</summary>
    public int CopyRecords(QualifiedName source, string sourceMember, QualifiedName target, string targetMember,
        bool replace = false, bool map = false, bool drop = false, CancellationToken cancellationToken = default)
    {
        sourceMember = sourceMember.ToUpperInvariant(); targetMember = targetMember.ToUpperInvariant();
        Authorize(source.Library, source.Name.Value, AuthorityBit.Read);
        Authorize(target.Library, target.Name.Value, AuthorityBit.Add | (replace ? AuthorityBit.Delete : AuthorityBit.None));
        var locks = new JobLockStore(_factory);
        if (replace) locks.RequireObjectMutation(target.Library, target.Name.Value, FileType);
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        EnsureMember(source.Library, source.Name.Value, sourceMember); EnsureMember(target.Library, target.Name.Value, targetMember);
        var from = GetDefinition(source.Library, source.Name.Value)!; var to = GetDefinition(target.Library, target.Name.Value)!;
        if (to.Logical is not null || to.Formats.Count != 1 || from.Formats.Count != 1)
            throw new CpfException("IPC0139", "CPYF currently requires one source format and a physical target.");
        if ((from.Attribute == FileAttribute.Source) != (to.Attribute == FileAttribute.Source))
            throw new CpfException("IPC0139", "Source/data conversion requires the remaining FMTOPT(*CVTSRC) implementation.");
        var input = from.PrimaryFormat; var output = to.PrimaryFormat;
        EnsureExactDecimalStorage(connection, transaction, MemberTable(target.Library, target.Name.Value, targetMember), output);
        bool Same(FieldSpec a, FieldSpec b) => a.Name == b.Name && a.Type == b.Type && a.Length == b.Length && a.Decimals == b.Decimals && a.Ccsid == b.Ccsid && a.NullCapable == b.NullCapable && a.VariableLength == b.VariableLength;
        if (!map && !drop && (input.Fields.Count != output.Fields.Count || input.Fields.Where((f, i) => !Same(f, output.Fields[i]) || f.Position != output.Fields[i].Position).Any()))
            throw new CpfException("IPC0139", "FMTOPT(*NONE) requires matching field layouts.");
        if (!drop && input.Fields.Any(f => output.Find(f.Name) is null)) throw new CpfException("IPC0139", "Source fields absent from the target require FMTOPT(*DROP).");
        if (!map && output.Fields.Any(f => input.Find(f.Name) is not { } original || !Same(original, f))) throw new CpfException("IPC0139", "Target field conversion or defaults require FMTOPT(*MAP).");
        using var read = connection.CreateCommand(); read.Transaction = transaction;
        var basedOn = from.Logical;
        if (basedOn is not null) ValidateLogical(basedOn);
        var members = basedOn is null ? new[] { sourceMember } : basedOn.Members[sourceMember];
        var baseLibrary = basedOn?.SourceLibrary ?? source.Library; var baseFile = basedOn?.SourceFile ?? source.Name.Value;
        var physical = basedOn is null ? from : GetDefinition(baseLibrary, baseFile) ?? throw LogicalError("Missing physical definition.");
        if (basedOn is not null && (physical.PrimaryFormat.Name != basedOn.SourceFormat || input.Fields.Any(f => physical.PrimaryFormat.Find(f.Name) is not { } original || !Same(f, original))))
            throw LogicalError("Physical format changed; recompile the logical file.");
        foreach (var member in members) EnsureMember(baseLibrary, baseFile, member);
        var predicate = basedOn is null ? "1" : LogicalPredicate(read, basedOn, physical.PrimaryFormat);
        var columns = string.Join(',', input.Fields.Select(f => Quote(f.Name)));
        var keys = input.Fields.Where(f => f.Sequence > 0).OrderBy(f => f.Sequence).ToArray();
        if (members.Count != 0)
        {
            read.CommandText = "SELECT * FROM (" + string.Join(" UNION ALL ", members.Select((m, i) =>
                $"SELECT {columns},{RowIdentitySql(physical.PrimaryFormat)} AS __ipc_record_number__,{i} AS __ipc_member_order__ FROM {Quote(MemberTable(baseLibrary, baseFile, m))} WHERE {predicate}")) +
                ") ORDER BY " + (keys.Length == 0 ? "" : string.Join(',', keys.Select(OrderKey)) + ",") + "__ipc_member_order__,__ipc_record_number__ LIMIT 1000001";
        }
        // A TEMP table keeps record payloads outside managed memory and makes
        // self-copy and target triggers operate on a stable source snapshot.
        using var stage = connection.CreateCommand(); stage.Transaction = transaction;
        stage.CommandText = "CREATE TEMP TABLE ipc_copy_stage (" + string.Join(',', output.Fields.Select(f => Quote(f.Name))) + ")"; stage.ExecuteNonQuery();
        stage.CommandText = "INSERT INTO ipc_copy_stage VALUES (" + string.Join(',', output.Fields.Select((_, i) => "$v" + i)) + ")";
        for (var i = 0; i < output.Fields.Count; i++) stage.Parameters.AddWithValue("$v" + i, DBNull.Value);
        var count = 0; long bytes = 0;
        if (members.Count != 0)
        {
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > 1000000) throw new CpfException("IPC0139", "Copy exceeds one million records.");
                locks.RequireRecordAccess(baseLibrary, baseFile, members[reader.GetInt32(input.Fields.Count + 1)], reader.GetInt64(input.Fields.Count), write: false);
                for (var i = 0; i < output.Fields.Count; i++)
                {
                    var field = output.Fields[i]; var ordinal = input.Fields.FindIndex(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
                    object? raw = ordinal < 0 ? DefaultFor(field) : reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
                    if (ordinal >= 0 && reader.IsDBNull(ordinal) && !field.NullCapable) throw new CpfException("IPC0139", "Null source value cannot be copied to " + field.Name + ".");
                    var value = RecordSqlValue(field, raw);
                    bytes += value is string text ? Encoding.UTF8.GetByteCount(text) : 16;
                    if (bytes > 67108864) throw new CpfException("IPC0139", "Copy exceeds 64 MiB of staged values.");
                    stage.Parameters[i].Value = value ?? DBNull.Value;
                }
                stage.ExecuteNonQuery();
            }
        }
        var targetTable = Quote(MemberTable(target.Library, target.Name.Value, targetMember));
        using var write = connection.CreateCommand(); write.Transaction = transaction;
        if (replace)
        {
            write.CommandText = "SELECT " + RowIdentitySql(output) + " FROM " + targetTable;
            using (var reader = write.ExecuteReader()) while (reader.Read())
                locks.RequireRecordAccess(target.Library, target.Name.Value, targetMember, reader.GetInt64(0), write: true);
            cancellationToken.ThrowIfCancellationRequested();
            write.CommandText = "DELETE FROM " + targetTable; write.ExecuteNonQuery();
        }
        write.CommandText = "INSERT INTO " + targetTable + " (" + string.Join(',', output.Fields.Select(f => Quote(f.Name))) + ") SELECT * FROM ipc_copy_stage RETURNING " + RowIdentitySql(output);
        using (var reader = write.ExecuteReader()) while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            locks.RequireRecordAccess(target.Library, target.Name.Value, targetMember, reader.GetInt64(0), write: true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        write.CommandText = "DROP TABLE ipc_copy_stage"; write.ExecuteNonQuery();
        transaction.Commit(); return count;
    }
}
