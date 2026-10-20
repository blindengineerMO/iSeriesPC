using Ipc.Db.Definitions;

namespace Ipc.Db.Sql;

public sealed partial class SqlStatementParser
{
    private SqlForeignConstraint ForeignDefinition(string? name, string? column = null)
    {
        IReadOnlyList<string> columns;
        if (column is null) { Need("FOREIGN"); Need("KEY"); Need("("); columns = Identifiers(); Need(")"); }
        else columns = [column];
        Need("REFERENCES"); var parent = ObjectNameValue(); IReadOnlyList<string>? parentColumns = null;
        if (Take("(")) { parentColumns = Identifiers(); Need(")"); }
        var delete = SqlForeignDelete.NoAction; var update = SqlForeignUpdate.NoAction; var seen = new HashSet<string>();
        while (Take("ON"))
        {
            var kind = Take("DELETE") ? "DELETE" : "UPDATE"; if (kind == "UPDATE") Need("UPDATE");
            if (!seen.Add(kind)) throw Invalid("Repeated referential action.");
            if (kind == "UPDATE")
            {
                if (Take("RESTRICT")) update = SqlForeignUpdate.Restrict;
                else { Need("NO"); Need("ACTION"); }
            }
            else if (Take("RESTRICT")) delete = SqlForeignDelete.Restrict;
            else if (Take("CASCADE")) delete = SqlForeignDelete.Cascade;
            else if (Take("SET")) { if (Take("NULL")) delete = SqlForeignDelete.SetNull; else { Need("DEFAULT"); delete = SqlForeignDelete.SetDefault; } }
            else { Need("NO"); Need("ACTION"); }
        }
        if (columns.Count is < 1 or > 120 || columns.Distinct().Count() != columns.Count ||
            parentColumns is not null && (parentColumns.Count != columns.Count || parentColumns.Distinct().Count() != parentColumns.Count))
            throw new Ipc.Core.Messages.CpfException("SQL0538", "Invalid foreign or parent key columns.");
        return new(name, columns, parent, parentColumns, delete, update);
    }
}
