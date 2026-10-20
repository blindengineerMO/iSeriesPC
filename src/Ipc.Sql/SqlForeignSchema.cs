using System.Security.Cryptography;
using System.Text;
using Ipc.Db.Definitions;

namespace Ipc.Db.Sql;

internal static class SqlForeignSchema
{
    internal static bool Enforced(SqlForeignConstraint foreign) => !foreign.Defined && !foreign.Disabled && !foreign.CheckPending;
    internal static string Quote(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    internal static string Collation(FieldSpec field) => field.Type switch {
        FieldType.Alpha => "ipc_text_v1_" + field.Ccsid,
        FieldType.Packed or FieldType.Zoned => "ipc_decimal_v1", _ => "BINARY" };
    internal static bool SameType(FieldSpec left, FieldSpec right) => left.Type == right.Type && left.Length == right.Length &&
        left.Decimals == right.Decimals && left.VariableLength == right.VariableLength && (left.Type != FieldType.Alpha || left.Ccsid == right.Ccsid);
    internal static string IndexName(string table, IReadOnlyList<string> columns) => "ipc_sql_reference_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(table + "\0" + string.Join(',', columns))));
    internal static string ForeignIndexName(string table, IReadOnlyList<string> columns) => IndexName(table,columns).Replace("ipc_sql_reference_","ipc_sql_foreign_",StringComparison.Ordinal);
    internal static string ParentTable(SqlForeignConstraint foreign) => foreign.Parent.Schema + "." + foreign.Parent.Name + "." + foreign.ParentMember;
    internal static bool PartialNull(RecordFormat format, SqlForeignConstraint foreign) => Enforced(foreign) && foreign.DeleteRule == SqlForeignDelete.SetNull &&
        foreign.Columns.Any(column => !format.Find(column)!.NullCapable);
    internal static string Clause(RecordFormat format, SqlForeignConstraint foreign) => "CONSTRAINT " + Quote(foreign.Name!) + " FOREIGN KEY(" +
        string.Join(',', foreign.Columns.Select(Quote)) + ") REFERENCES " + Quote(ParentTable(foreign)) + "(" +
        string.Join(',', foreign.ParentColumns!.Select(Quote)) + ") ON DELETE " + (PartialNull(format, foreign) ? "NO ACTION" : foreign.DeleteRule switch {
            SqlForeignDelete.Restrict => "RESTRICT", SqlForeignDelete.Cascade => "CASCADE", SqlForeignDelete.SetNull => "SET NULL",
            SqlForeignDelete.SetDefault => "SET DEFAULT", _ => "NO ACTION" }) + " ON UPDATE " + (foreign.UpdateRule == SqlForeignUpdate.Restrict ? "RESTRICT" : "NO ACTION") + " DEFERRABLE INITIALLY DEFERRED";
    internal static string NullTriggerName(string child, SqlForeignConstraint foreign) => "ipc_sql_fk_null_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(child + "\0" + foreign.Name)));
    internal static string NullTrigger(string child, RecordFormat format, SqlForeignConstraint foreign)
    {
        var match = foreign.Columns.Select((column, ordinal) => Quote(column) + "=OLD." + Quote(foreign.ParentColumns![ordinal]) + " COLLATE " + Collation(format.Find(column)!));
        return "CREATE TRIGGER " + Quote(NullTriggerName(child, foreign)) + " BEFORE DELETE ON " + Quote(ParentTable(foreign)) +
            " BEGIN UPDATE " + Quote(child) + " SET " + string.Join(',', foreign.Columns.Where(column => format.Find(column)!.NullCapable).Select(column => Quote(column) + "=NULL")) +
            " WHERE " + string.Join(" AND ", match) + "; END";
    }
}
