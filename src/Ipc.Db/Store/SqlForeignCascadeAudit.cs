using Ipc.Core.Messages;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed record CascadeAudit(Func<IReadOnlyDictionary<string,HashSet<long>>> Read, Action Remove);

    private static CascadeAudit BeginCascadeAudit(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyDictionary<string,CascadeMember> members, CancellationToken token)
    {
        var name = "ipc_cascade_" + Guid.NewGuid().ToString("N");
        var triggers = new List<string>();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "CREATE TEMP TABLE " + Quote(name) + "(member TEXT,rrn INTEGER,PRIMARY KEY(member,rrn)) WITHOUT ROWID";
        command.ExecuteNonQuery();
        foreach (var (table,member) in members)
        {
            foreach (var operation in new[] { "DELETE", "UPDATE" })
            {
                var trigger = name + "_" + triggers.Count; triggers.Add(trigger);
                var memberValue = "'" + table.Replace("'","''") + "'";
                var number = "OLD." + Quote(RowIdentitySql(member.Definition.PrimaryFormat));
                // An outer UPDATE's conflict policy overrides OR IGNORE inside
                // a trigger. Avoid the duplicate insert explicitly instead.
                command.CommandText = "CREATE TEMP TRIGGER " + Quote(trigger) + " BEFORE " + operation + " ON main." + Quote(table) +
                    " BEGIN INSERT INTO " + Quote(name) + " SELECT " + memberValue + "," + number +
                    " WHERE NOT EXISTS(SELECT 1 FROM " + Quote(name) + " WHERE member=" + memberValue + " AND rrn=" + number + "); END";
                command.ExecuteNonQuery();
            }
        }
        // Both creation and cleanup belong to the caller's transaction. Failed
        // statements roll back the temporary objects along with their changes.
        return new(() => {
            var result = new Dictionary<string,HashSet<long>>();
            using var read = connection.CreateCommand(); read.Transaction = transaction;
            read.CommandText = "SELECT member,rrn FROM " + Quote(name);
            using var reader = read.ExecuteReader();
            long count = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (++count > 1000000) throw new CpfException("SQL0904","Referential actions exceed one million affected records.");
                var table = reader.GetString(0);
                if (!result.TryGetValue(table,out var rows)) result.Add(table,rows=[]);
                rows.Add(reader.GetInt64(1));
            }
            return result;
        },() => {
            using var cleanup = connection.CreateCommand(); cleanup.Transaction = transaction;
            foreach (var trigger in triggers)
            {
                cleanup.CommandText = "DROP TRIGGER " + Quote(trigger); cleanup.ExecuteNonQuery();
            }
            cleanup.CommandText = "DROP TABLE " + Quote(name); cleanup.ExecuteNonQuery();
        });
    }
}
