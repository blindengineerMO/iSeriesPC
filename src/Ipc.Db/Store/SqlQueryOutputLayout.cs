using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private static FileDefinition QueryOutputLayout(string name, IReadOnlyList<SqlQueryColumn> columns, int ccsid)
    {
        var names = new HashSet<string>(StringComparer.Ordinal); var suffix = 0;
        var fields = new List<FieldSpec>();
        foreach (var column in columns)
        {
            var basis = !column.Calculated && ObjectName.IsValid(column.Name) ? column.Name : "SEL"; var field = basis;
            while (!names.Add(field))
            {
                var number = (++suffix).ToString(CultureInfo.InvariantCulture);
                field = basis.Length + number.Length <= 10 ? basis + number : "COL" + number;
            }
            var type = column.Type;
            var (kind, length) = type.Kind switch {
                SqlQueryKind.SmallInt => (FieldType.Binary, 2), SqlQueryKind.Integer => (FieldType.Binary, 4), SqlQueryKind.BigInt => (FieldType.Binary, 8),
                SqlQueryKind.Decimal => (FieldType.Packed, type.Precision), SqlQueryKind.Numeric => (FieldType.Zoned, type.Precision),
                SqlQueryKind.Real => (FieldType.Float, 4), SqlQueryKind.Double => (FieldType.Float, 8), SqlQueryKind.Boolean => (FieldType.Logic, 1),
                SqlQueryKind.Date => (FieldType.Date, 10), SqlQueryKind.Time => (FieldType.Time, 8), SqlQueryKind.Timestamp => (FieldType.Timestamp, 26),
                SqlQueryKind.Character or SqlQueryKind.VarChar => (FieldType.Alpha, Math.Max(1, type.Length)), SqlQueryKind.Null => (FieldType.Alpha, 1),
                _ => throw new CpfException("SQL0104", "Unsupported query output type.") };
            if (kind is FieldType.Packed or FieldType.Zoned && (length > 29 || type.Scale > 28))
                throw new CpfException("SQL0406", "Output exceeds the current physical decimal precision; cast the result to an implemented field type.");
            fields.Add(new() { Name = field, Type = kind, Length = length, Decimals = type.Scale, Ccsid = type.Ccsid,
                VariableLength = type.Kind == SqlQueryKind.VarChar, NullCapable = type.Nullable || type.Kind == SqlQueryKind.Null });
        }
        var format = new RecordFormat { Name = name, Fields = fields }; format.AssignPositions();
        if (format.RecordLength > 32766) throw new CpfException("SQL0904", "Query output exceeds the physical record-length limit.");
        return new() { Name = name, Attribute = FileAttribute.Physical, Formats = [format], MaximumMembers = 1,
            MemberSize = new(Unlimited: true), ReuseDeletedRecords = true, Ccsid = columns.FirstOrDefault(column => column.Type.Kind is SqlQueryKind.Character or SqlQueryKind.VarChar)?.Type.Ccsid ?? ccsid,
            SqlTable = new([]) };
    }
    private static void RequireQueryOutputLayout(FileDefinition output, IReadOnlyList<SqlQueryColumn> columns)
    {
        if (output.Attribute != FileAttribute.Physical || output.Logical is not null || output.Formats.Count != 1)
            throw new CpfException("SQL0104", "Query output currently requires a single-format physical data file.");
        var fields = output.PrimaryFormat.Fields;
        if (fields.Count != columns.Count) throw new CpfException("SQL0117", "Query and output file column counts differ.");
        for (var index = 0; index < fields.Count; index++)
        {
            var target = fields[index]; var source = columns[index].Type;
            var compatible = source.Kind switch {
                SqlQueryKind.Null => target.NullCapable,
                SqlQueryKind.Character or SqlQueryKind.VarChar => target.Type == FieldType.Alpha && target.Length >= source.Length,
                SqlQueryKind.SmallInt or SqlQueryKind.Integer or SqlQueryKind.BigInt => target.Type == FieldType.Binary && target.Length >= (source.Kind == SqlQueryKind.SmallInt ? 2 : source.Kind == SqlQueryKind.Integer ? 4 : 8) ||
                    target.Type is FieldType.Packed or FieldType.Zoned && target.Length - target.Decimals >= source.Precision,
                SqlQueryKind.Decimal or SqlQueryKind.Numeric => target.Type is FieldType.Packed or FieldType.Zoned && target.Decimals >= source.Scale && target.Length - target.Decimals >= source.Precision - source.Scale,
                SqlQueryKind.Real or SqlQueryKind.Double => target.Type == FieldType.Float && target.Length >= (source.Kind == SqlQueryKind.Real ? 4 : 8),
                SqlQueryKind.Boolean => target.Type == FieldType.Logic, SqlQueryKind.Date => target.Type == FieldType.Date,
                SqlQueryKind.Time => target.Type == FieldType.Time, SqlQueryKind.Timestamp => target.Type == FieldType.Timestamp && target.Length >= 26,
                _ => false };
            if (!compatible || source.Nullable && !target.NullCapable) throw new CpfException("SQL0408", "Query column " + (index + 1) + " is incompatible with output field " + target.Name + ".");
        }
    }
}
