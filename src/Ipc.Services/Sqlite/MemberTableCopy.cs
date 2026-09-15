using Ipc.Core.Messages;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

/// <summary>Copies a set of member tables inside the owning catalog transaction.</summary>
internal sealed class MemberTableCopy(SqliteConnection connection, SqliteTransaction transaction)
{
    public void Copy(IReadOnlyList<(string Source, string Target)> tables)
    {
        if (tables.Count == 0) return;
        var schemas = new List<(string Type, string Name, string Table, string Sql)>();
        // Let SQLite's parser rewrite identifier references, including self/cross-member
        // foreign keys and trigger bodies. Rolling back the savepoint restores *all*
        // source schema, including references from objects outside the copy set.
        Run("SAVEPOINT ipc_copy_schema");
        try
        {
            foreach (var table in tables)
                Run($"ALTER TABLE {Quote(table.Source)} RENAME TO {Quote(table.Target)}");
            foreach (var table in tables)
            {
                using var command = Command("SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE tbl_name=$table AND sql IS NOT NULL ORDER BY type,name");
                command.Parameters.AddWithValue("$table", table.Target);
                using var reader = command.ExecuteReader();
                while (reader.Read()) schemas.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }
        finally
        {
            Run("ROLLBACK TO ipc_copy_schema");
            Run("RELEASE ipc_copy_schema");
        }

        Run("PRAGMA defer_foreign_keys=ON");
        foreach (var schema in schemas.Where(s => s.Type == "table")) Run(schema.Sql);
        // Explicit indexes can provide FK parent uniqueness, so create them before loading.
        foreach (var schema in schemas.Where(s => s.Type == "index" && !IsLogicalConstraint(s.Name)))
            Run(RenameSchemaObject(schema.Sql, "INDEX", $"{schema.Table}.index.{schema.Name}"));
        foreach (var table in tables)
        {
            var columns = Columns(table.Source);
            using var kind = Command("SELECT wr FROM pragma_table_list WHERE schema='main' AND name=$table");
            kind.Parameters.AddWithValue("$table", table.Source);
            var withoutRowId = Convert.ToInt64(kind.ExecuteScalar()) != 0;
            var writable = columns.Where(c => c.Hidden == 0).Select(c => c.Name).ToList();
            if (!withoutRowId)
            {
                var alias = new[] { "rowid", "_rowid_", "oid" }.FirstOrDefault(name =>
                    columns.All(c => !c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
                if (alias is null)
                    throw new CpfException("IPC0003", "Cannot preserve member record numbers when every rowid alias is shadowed.");
                writable.Insert(0, alias);
            }
            var names = string.Join(",", writable.Select(Quote));
            Run($"INSERT INTO {Quote(table.Target)} ({names}) SELECT {names} FROM {Quote(table.Source)}");
            CopySequence(table.Source, table.Target);
        }
        // Loading a duplicate must not emit application trigger side effects.
        foreach (var schema in schemas.Where(s => s.Type == "trigger"))
            Run(RenameSchemaObject(schema.Sql, "TRIGGER", $"{schema.Table}.trigger.{schema.Name}"));
    }

    private bool IsLogicalConstraint(string name)
    {
        using var command = Command("SELECT count(*) FROM sys_file_defs d,json_each(d.def,'$.logical.uniquePaths') p WHERE p.value=$name");
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private void CopySequence(string source, string target)
    {
        using var exists = Command("SELECT count(*) FROM sqlite_schema WHERE name='sqlite_sequence'");
        if (Convert.ToInt64(exists.ExecuteScalar()) == 0) return;
        using var command = Command("""
            DELETE FROM sqlite_sequence WHERE name=$target;
            INSERT INTO sqlite_sequence(name,seq) SELECT $target,seq FROM sqlite_sequence WHERE name=$source;
            """);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$target", target);
        command.ExecuteNonQuery();
    }

    private List<(string Name, int Hidden)> Columns(string table)
    {
        using var command = Command("SELECT name,hidden FROM pragma_table_xinfo($table) ORDER BY cid");
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var columns = new List<(string, int)>();
        while (reader.Read()) columns.Add((reader.GetString(0), reader.GetInt32(1)));
        return columns;
    }

    // Change only the declared schema object's name. Expressions, strings, comments,
    // columns, and trigger statements remain exactly as SQLite produced them.
    private static string RenameSchemaObject(string sql, string kind, string name)
    {
        var offset = 0;
        var token = NextToken(sql, ref offset);
        if (!token.Text.Equals("CREATE", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid schema declaration.");
        do { token = NextToken(sql, ref offset); }
        while (token.Text.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
               token.Text.Equals("TEMP", StringComparison.OrdinalIgnoreCase) ||
               token.Text.Equals("TEMPORARY", StringComparison.OrdinalIgnoreCase));
        if (!token.Text.Equals(kind, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected schema declaration.");
        token = NextToken(sql, ref offset);
        if (token.Text.Equals("IF", StringComparison.OrdinalIgnoreCase))
        {
            if (!NextToken(sql, ref offset).Text.Equals("NOT", StringComparison.OrdinalIgnoreCase) ||
                !NextToken(sql, ref offset).Text.Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid schema declaration.");
            token = NextToken(sql, ref offset);
        }
        return sql[..token.Start] + Quote(name) + sql[offset..];
    }

    private static (int Start, string Text) NextToken(string sql, ref int offset)
    {
        while (offset < sql.Length)
        {
            if (char.IsWhiteSpace(sql[offset])) { offset++; continue; }
            if (sql.AsSpan(offset).StartsWith("--"))
            {
                while (offset < sql.Length && sql[offset] != '\n') offset++;
                continue;
            }
            if (sql.AsSpan(offset).StartsWith("/*"))
            {
                var end = sql.IndexOf("*/", offset + 2, StringComparison.Ordinal);
                if (end < 0) throw new InvalidDataException("Unclosed schema comment.");
                offset = end + 2;
                continue;
            }
            break;
        }
        var start = offset;
        if (offset == sql.Length) throw new InvalidDataException("Incomplete schema declaration.");
        var quote = sql[offset];
        if (quote is '\"' or '\'' or '`' or '[')
        {
            var close = quote == '[' ? ']' : quote;
            offset++;
            while (offset < sql.Length)
            {
                if (sql[offset++] != close) continue;
                if (close != ']' && offset < sql.Length && sql[offset] == close) { offset++; continue; }
                return (start, sql[start..offset]);
            }
            throw new InvalidDataException("Unclosed schema identifier.");
        }
        while (offset < sql.Length && (char.IsLetterOrDigit(sql[offset]) || sql[offset] is '_' or '$' || sql[offset] >= 128)) offset++;
        if (offset == start) throw new InvalidDataException("Invalid schema identifier.");
        return (start, sql[start..offset]);
    }

    private SqliteCommand Command(string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
    private void Run(string sql) { using var command = Command(sql); command.ExecuteNonQuery(); }
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
