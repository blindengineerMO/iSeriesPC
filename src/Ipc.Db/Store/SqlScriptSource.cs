using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Db.Definitions;
using Ipc.Db.Records;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    public string ReadSqlSource(string library, string name, string member, int margin = 80, int ccsid = 37, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _factory.ShutdownToken); var token = lifetime.Token;
        token.ThrowIfCancellationRequested();
        if (margin is < 1 or > 32754 || !CodePage.IsSupported(ccsid)) throw new CpfException("IPC0003", "Invalid SQL source margin/CCSID.");
        library = ResolveLibrary(library).ToUpperInvariant(); name = name.ToUpperInvariant(); member = member.ToUpperInvariant();
        if (!ObjectName.IsValid(name) || !ObjectName.IsValid(member)) throw new CpfException("IPC0003", "Invalid SQL source name.");
        Authorize(library, name, AuthorityBit.Read); RequireUnenlisted(library, name, "RUNSQLSTM source");
        new JobLockStore(_factory).RequireStableRead(library, name, FileType);
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        var definition = ReadDefinition(connection, transaction, library, name) ?? throw new CpfException("CPF9801", "SQL source file not found.");
        if (definition.Logical is not null || definition.Formats.Count != 1) throw new CpfException("CPF2817", "SQL source requires a single-format physical file.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sys_file_members WHERE lib=$lib AND name=$name AND mbr=$member AND type='*FILE'";
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$member", member);
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new CpfException("CPF2817", "SQL source member not found.");
        var format = definition.PrimaryFormat;
        var sourceField = definition.Attribute == Definitions.FileAttribute.Source ? format.Find("SRCDTA") : null;
        if (sourceField is not null) ccsid = sourceField.Ccsid;
        else
        {
            var codes = format.Fields.Where(field => field.Type == FieldType.Alpha).Select(field => field.Ccsid).Distinct().ToArray();
            if (codes.Length > 1) throw new CpfException("SQL0332", "SQL source record has mixed character CCSIDs.");
            if (codes.Length == 1) ccsid = codes[0];
        }
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback; encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
        command.Parameters.Clear(); command.CommandText = "SELECT " + string.Join(',', format.Fields.Select(field => Quote(field.Name))) +
            " FROM " + Quote(MemberTable(library, name, member)) + " ORDER BY " + Quote(RowIdentitySql(format));
        var source = new StringBuilder(); var lines = 0; var codec = new RecordCodec(ccsid);
        return SqliteCancellation.Run(connection, token, () => {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var row = ReadRow(reader, format); byte[] bytes;
                try
                {
                    bytes = sourceField is null ? codec.Encode(format, row) : encoding.GetBytes((string?)row[sourceField.Name] ?? "");
                    var count = Math.Min(margin, sourceField?.Length ?? bytes.Length);
                    if (bytes.Length < count)
                    {
                        var padded = Enumerable.Repeat(encoding.GetBytes(" ")[0], count).ToArray(); bytes.CopyTo(padded, 0); bytes = padded;
                    }
                    source.Append(encoding.GetString(bytes, 0, count)).Append('\n');
                }
                catch (Exception error) when (error is EncoderFallbackException or DecoderFallbackException or ArgumentException)
                { throw new CpfException("SQL0332", "SQL source or margin is not valid in its CCSID."); }
                if (++lines > 10000 || source.Length > 1048576) throw new CpfException("SQL0904", "SQL source exceeds one MiB or 10000 records.");
            }
            return source.ToString();
        });
    }
}
