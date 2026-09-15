using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed record SourceMemberSnapshot(string Source, string? Revision, int Width);

public sealed partial class SqliteFileStore
{
    public SourceMemberSnapshot ReadSourceMember(string library, string name, string member)
    {
        library = library.ToUpperInvariant(); name = name.ToUpperInvariant(); member = member.ToUpperInvariant();
        if (!ObjectName.IsValid(member)) throw new ArgumentException("Invalid source member.");
        Authorize(library, name, AuthorityBit.Read);
        new JobLockStore(_factory).RequireStableRead(library, name, FileType);
        var definition = GetDefinition(library, name) ?? throw new CpfException("CPF9801", "Source file does not exist.");
        if (_objects.GetRequired(library, name, FileType).Attribute != SourceAttribute) throw new CpfException("CPF2817", "A source physical file is required.");
        var width = definition.PrimaryFormat.Fields.Single(f => f.Name == "SRCDTA").Length;
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = ReadSourceCore(connection, transaction, library, name, member, width); transaction.Commit(); return snapshot;
    }
    public SourceMemberSnapshot SaveSourceMember(string library, string name, string member, string source, string? expectedRevision)
    {
        library = library.ToUpperInvariant(); name = name.ToUpperInvariant(); member = member.ToUpperInvariant();
        if (!ObjectName.IsValid(member)) throw new ArgumentException("Invalid source member.");
        Authorize(library, name, AuthorityBit.Read | AuthorityBit.Add | AuthorityBit.Update | AuthorityBit.Delete);
        new JobLockStore(_factory).RequireObjectMutation(library, name, FileType);
        var definition = GetDefinition(library, name) ?? throw new CpfException("CPF9801", "Source file does not exist.");
        if (_objects.GetRequired(library, name, FileType).Attribute != SourceAttribute) throw new CpfException("CPF2817", "A source physical file is required.");
        var width = definition.PrimaryFormat.Fields.Single(f => f.Name == "SRCDTA").Length;
        if (source.Length > 1024 * 1024) throw new CpfException("CPF2817", "Source exceeds 1 MiB.");
        var normalized = NormalizeSource(source); var lines = normalized.Length == 0 ? Array.Empty<string>() : normalized[..^1].Split('\n');
        if (lines.Any(l => l.Length > width)) throw new CpfException("CPF2817", $"Source line exceeds the member's {width}-character data width.");
        if (normalized.Length > 1024 * 1024 || lines.Length > 10000) throw new CpfException("CPF2817", "Source exceeds 1 MiB or 10000 lines.");
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        definition = GetDefinition(library, name) ?? throw new CpfException("CPF9801", "Source file does not exist.");
        var previous = ReadSourceCore(connection, transaction, library, name, member, width);
        if (previous.Revision != expectedRevision) throw new CpfException("IPC0130", "Source changed since it was opened; reload before saving.");
        if (previous.Revision is null)
        {
            Authorize(library, name, AuthorityBit.ObjectManagement);
            CheckNewPhysicalMember(connection, transaction, library, name, member, definition);
            using var create = connection.CreateCommand(); create.Transaction = transaction;
            create.CommandText = "INSERT INTO sys_file_members(lib,name,type,mbr,created) VALUES($lib,$name,'*FILE',$member,$at)";
            create.Parameters.AddWithValue("$lib", library); create.Parameters.AddWithValue("$name", name); create.Parameters.AddWithValue("$member", member);
            create.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); create.ExecuteNonQuery();
            create.CommandText = CreateMemberTableSql(definition.PrimaryFormat, MemberTable(library, name, member)).Replace("IF NOT EXISTS ", "", StringComparison.Ordinal); create.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM " + Quote(MemberTable(library, name, member)); command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO " + Quote(MemberTable(library, name, member)) + " (SRCSEQ,SRCDAT,SRCDTA) VALUES($seq,$date,$text)";
        command.Parameters.Add("$seq", Microsoft.Data.Sqlite.SqliteType.Integer); command.Parameters.AddWithValue("$date", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); command.Parameters.Add("$text", Microsoft.Data.Sqlite.SqliteType.Text);
        for (var i = 0; i < lines.Length; i++) { command.Parameters["$seq"].Value = i + 1; command.Parameters["$text"].Value = lines[i]; command.ExecuteNonQuery(); }
        command.Parameters.Clear(); command.CommandText = "UPDATE sys_objects SET changed=$at WHERE lib=$lib AND name=$name AND type='*FILE'";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name); command.ExecuteNonQuery();
        command.Parameters.Clear(); command.CommandText = "INSERT INTO sys_events(at,kind,principal,job,payload) VALUES($at,'source.saved',$principal,$job,$payload)";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$principal", OperationIdentity.Current?.Principal ?? "*SYSTEM");
        command.Parameters.AddWithValue("$job", (object?)OperationIdentity.Current?.Job?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { library, name, member, lines = lines.Length, revision = Revision(normalized) })); command.ExecuteNonQuery();
        transaction.Commit(); return new(normalized, Revision(normalized), width);
    }
    private static SourceMemberSnapshot ReadSourceCore(SqliteConnection connection, SqliteTransaction transaction, string library, string name, string member, int width)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sys_file_members WHERE lib=$lib AND name=$name AND mbr=$member AND type='*FILE'";
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$member", member);
        if (Convert.ToInt64(command.ExecuteScalar()) == 0) return new("", null, width);
        command.CommandText = "SELECT SRCDTA FROM " + Quote(MemberTable(library, name, member)) + " ORDER BY _rowid_";
        var source = new StringBuilder(); var lines = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            source.AppendLine(reader.IsDBNull(0) ? "" : reader.GetString(0).TrimEnd(' '));
            if (++lines > 10000 || source.Length > 1024 * 1024) throw new CpfException("CPF2817", "Source exceeds 1 MiB or 10000 lines.");
        }
        var text = NormalizeSource(source.ToString()); return new(text, Revision(text), width);
    }
    private static string NormalizeSource(string source)
    {
        if (source.Length == 0) return "";
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (source.EndsWith('\n')) source = source[..^1];
        return string.Join('\n', source.Split('\n').Select(l => l.TrimEnd(' '))) + "\n";
    }
    private static string Revision(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
}
