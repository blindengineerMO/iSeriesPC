using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Db.Definitions;

namespace Ipc.Db.Sql;

public sealed partial class SqlStatementParser
{
    private SqlAlterTable AlterTable()
    {
        var table = ObjectNameValue(); var changes = new List<SqlTableChange>();
        do
        {
            if (changes.Count >= 1000) throw Invalid("ALTER TABLE exceeds its action limit.");
            if (Take("ADD"))
            {
                Take("COLUMN"); var constraintName = Take("CONSTRAINT") ? Identifier() : null;
                if (Is("PRIMARY") || Is("UNIQUE"))
                    changes.Add(new SqlAddKey(AlterKey(constraintName)));
                else if (Is("CHECK")) changes.Add(new SqlAddCheck(CheckDefinition(constraintName)));
                else if (Is("FOREIGN")) changes.Add(new SqlAddForeign(ForeignDefinition(constraintName)));
                else
                {
                    if (constraintName is not null) throw Invalid("Unsupported ALTER TABLE constraint.");
                    var start = _position; var constraints = new List<SqlUniqueConstraint>(); var checks = new List<SqlCheckConstraint>(); var foreign = new List<SqlForeignConstraint>(); var field = ColumnDefinition(constraints, true, checks, foreign);
                    var historical = field.CurrentDatetimeDefault && !_tokens.Skip(start).Take(_position-start).Any(token => token.Kind == 'W' && token.Text is "CURRENT" or "CURRENT_DATE" or "CURRENT_TIME" or "CURRENT_TIMESTAMP");
                    changes.Add(new SqlAddColumn(field, Take("BEFORE") ? Identifier() : null, historical));
                    changes.AddRange(constraints.Select(constraint => new SqlAddKey(constraint)));
                    changes.AddRange(checks.Select(check => new SqlAddCheck(check)));
                    changes.AddRange(foreign.Select(item => new SqlAddForeign(item)));
                }
            }
            else if (Take("DROP"))
            {
                if (Take("FOREIGN")) { Need("KEY"); changes.Add(new SqlDropForeign(Identifier())); }
                else if (Take("PRIMARY")) { Need("KEY"); changes.Add(new SqlDropKey(null, true, AlterCascade())); }
                else if (Is("CONSTRAINT") || Is("UNIQUE") || Is("CHECK"))
                { var unique = Take("UNIQUE"); var check = !unique && Take("CHECK"); if (!unique && !check) Need("CONSTRAINT"); var name = Identifier(); changes.Add(new SqlDropKey(name, false, AlterCascade(), unique, check)); }
                else { Take("COLUMN"); var name = Identifier(); changes.Add(new SqlDropColumn(name, AlterCascade())); }
            }
            else if (Take("ALTER"))
            {
                Take("COLUMN"); var name = Identifier(); FieldSpec? type = null; SqlValue? value = null; bool? nullable = null; var dropDefault = false;
                if (Take("SET"))
                {
                    if (Take("DATA"))
                    {
                        Need("TYPE"); var data = DataType(); var ccsid = 0;
                        if (Take("CCSID")) { ccsid = Integer(1, 65535); if (data.Type != FieldType.Alpha || !CodePage.IsSupported(ccsid)) throw Invalid("Invalid character CCSID."); }
                        type = new() { Name = name, Type = data.Type, Length = data.Length, Decimals = data.Scale, VariableLength = data.Varying, Ccsid = ccsid };
                        if (Take("DEFAULT")) value = AlterDefault();
                    }
                    else if (Take("NOT")) { Need("NULL"); nullable = false; }
                    else { Need("DEFAULT"); value = AlterDefault(); }
                }
                else
                {
                    Need("DROP"); if (Take("DEFAULT")) dropDefault = true; else { Need("NOT"); Need("NULL"); nullable = true; }
                }
                changes.Add(new SqlAlterColumn(name, type, value, dropDefault, nullable));
            }
            else throw Invalid("Expected ADD, ALTER or DROP TABLE action.");
        } while (Is("ADD") || Is("ALTER") || Is("DROP"));
        return new(table, changes);
    }
    private bool AlterCascade() { if (Take("RESTRICT")) return false; Take("CASCADE"); return true; }
    private SqlValue AlterDefault() => Current.Kind == 'E' || Is(";") || Is("ADD") || Is("ALTER") || Is("DROP") ? new(SqlValueKind.Default) : Value(false);
    private SqlUniqueConstraint AlterKey(string? name)
    {
        var primary = Take("PRIMARY"); if (primary) Need("KEY"); else Need("UNIQUE");
        IReadOnlyList<string> columns;
        if (Take("(")) { columns = Identifiers(); Need(")"); } else columns = [Identifier()];
        if (columns.Count > 120 || columns.Distinct().Count() != columns.Count) throw new CpfException("SQL0612", "Invalid repeated or excessive key columns.");
        return new(name, primary, columns);
    }
}
