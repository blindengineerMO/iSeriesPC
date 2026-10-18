using System.Globalization;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

/// <summary>Durable local journals. Receiver and record changes use the same SQLite transaction.</summary>
public sealed partial class JournalStore(SqliteConnectionFactory factory)
{
    public const long MaximumReceiverBytes = 1073741824;
    public const int MaximumEntryBytes = 16777216;
    public void CreateReceiver(QualifiedName name, long? thresholdKilobytes = 1500000, string text = "", string authority = "*LIBCRTAUT", CancellationToken cancellationToken = default)
    {
        Permanent(name); if (thresholdKilobytes is < 1 or > 1000000000) throw Invalid("THRESHOLD must be *NONE or 1–1000000000 KB.");
        thresholdKilobytes = thresholdKilobytes is { } size ? Math.Max(100000, size) : null;
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken);
        CreateDescriptor(connection, transaction, name, ObjectType.JournalReceiver, text, authority);
        using var command = Command(connection, transaction, "INSERT INTO sys_journal_receivers(id,library,name,threshold_kb) VALUES($id,$library,$name,$threshold)");
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); Bind(command, name); command.Parameters.AddWithValue("$threshold", (object?)thresholdKilobytes ?? DBNull.Value);
        command.ExecuteNonQuery(); cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }
    public void Create(QualifiedName name, QualifiedName receiver, string text = "", string authority = "*LIBCRTAUT", CancellationToken cancellationToken = default)
    {
        Permanent(name); Permanent(receiver); Require(receiver, ObjectType.JournalReceiver, AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement);
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken);
        var target = ReceiverCore(connection, transaction, receiver); if (target.JournalId is not null || target.AttachedAt is not null) throw Invalid("A journal receiver can be attached only once.");
        CreateDescriptor(connection, transaction, name, ObjectType.Journal, text, authority);
        var id = Guid.NewGuid().ToString("N");
        using var command = Command(connection, transaction, "INSERT INTO sys_journals(id,library,name,current_receiver) VALUES($id,$library,$name,$receiver)");
        Bind(command, name); command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$receiver", target.Id); command.ExecuteNonQuery();
        AttachCore(connection, transaction, id, name, target.Id); cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }
    public JournalInfo Inspect(QualifiedName name)
    {
        Require(name, ObjectType.Journal, Authorities.UseBits); using var connection = factory.Open(); return InspectCore(connection, null, name);
    }
    public JournalReceiverInfo InspectReceiver(QualifiedName name)
    {
        Require(name, ObjectType.JournalReceiver, Authorities.UseBits); using var connection = factory.Open(); return ReceiverCore(connection, null, name);
    }
    public QualifiedName Rotate(QualifiedName name, QualifiedName? next = null, long? generatedThreshold = null, CancellationToken cancellationToken = default)
    {
        Require(name, ObjectType.Journal, AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement);
        if (next is { } specified) { Permanent(specified); Require(specified, ObjectType.JournalReceiver, AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement); }
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken); var journal = InspectCore(connection, transaction, name);
        var current = ReceiverCore(connection, transaction, journal.Receiver); QualifiedName targetName;
        if (next is null)
        {
            targetName = NextName(current.Name);
            var threshold = generatedThreshold ?? current.ThresholdKilobytes;
            if (threshold is < 1 or > 1000000000) throw Invalid("Invalid generated receiver threshold.");
            CreateDescriptor(connection, transaction, targetName, ObjectType.JournalReceiver, "Generated journal receiver", "*LIBCRTAUT");
            using var create = Command(connection, transaction, "INSERT INTO sys_journal_receivers(id,library,name,threshold_kb) VALUES($id,$library,$name,$threshold)");
            Bind(create, targetName); create.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); create.Parameters.AddWithValue("$threshold", (object?)(threshold is { } value ? Math.Max(100000, value) : null) ?? DBNull.Value); create.ExecuteNonQuery();
        }
        else targetName = next.Value;
        var target = ReceiverCore(connection, transaction, targetName); if (target.AttachedAt is not null || target.JournalId is not null) throw Invalid("Receiver has already been attached.");
        using var update = Command(connection, transaction, "UPDATE sys_journal_receivers SET detached_at=$now WHERE id=$old; UPDATE sys_journals SET current_receiver=$next WHERE id=$journal");
        update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); update.Parameters.AddWithValue("$old", current.Id); update.Parameters.AddWithValue("$next", target.Id); update.Parameters.AddWithValue("$journal", journal.Id); update.ExecuteNonQuery();
        AttachCore(connection, transaction, journal.Id, name, target.Id); cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return targetName;
    }
    public long Send(QualifiedName journal, string type, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        Require(journal, ObjectType.Journal, AuthorityBit.ObjectOperate | AuthorityBit.Add);
        if (type.Length != 2 || type.Any(c => !char.IsAsciiLetterOrDigit(c)) || data.Length > 3000) throw Invalid("User entries require a two-character alphanumeric type and at most 3000 bytes.");
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken); var info = InspectCore(connection, transaction, journal);
        using var command = Command(connection, transaction, """
            UPDATE sys_journals SET sequence=sequence+1 WHERE id=$id;
            INSERT INTO sys_journal_entries(journal_id,sequence,receiver_id,code,entry_type,captured_at,principal,job,user_data)
            SELECT id,sequence,current_receiver,'U',$type,$now,ipc_actor(),ipc_job(),$data FROM sys_journals WHERE id=$id;
            SELECT sequence FROM sys_journals WHERE id=$id;
            """);
        command.Parameters.AddWithValue("$id", info.Id); command.Parameters.AddWithValue("$type", type.ToUpperInvariant()); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$data", data.ToArray());
        var sequence = Convert.ToInt64(command.ExecuteScalar()); cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return sequence;
    }
    private void Require(QualifiedName name, string type, AuthorityBit authority)
    {
        Permanent(name); new ServiceAuthorization(factory).RequireObject(name.Library, name.Name.Value, type, authority);
    }
    private void Permanent(QualifiedName name)
    {
        new ObjectDescriptor { Key = name, ObjectType = ObjectType.Journal }.ValidateIdentity(); new JobTemporaryLibrary(factory).RequirePermanentLibrary(name.Library);
    }
    private void CreateDescriptor(SqliteConnection connection, SqliteTransaction transaction, QualifiedName name, string type, string text, string authority)
    {
        using (var exists = Command(connection, transaction, "SELECT count(*) FROM sys_objects WHERE lib=$library AND name=$name AND type=$type"))
        {
            Bind(exists, name); exists.Parameters.AddWithValue("$type", type);
            if (Convert.ToInt64(exists.ExecuteScalar()) != 0) throw new CpfException("CPF2112", "Object already exists: " + name + ".");
        }
        if (text == "*BLANK") text = ""; if (text.Length > 50 || text.Any(char.IsControl)) throw Invalid("Journal text requires at most 50 printable characters.");
        var objects = new SqliteObjectStore(factory); var authorization = new ServiceAuthorization(factory);
        var library = objects.GetRequired("QSYS", name.Library, ObjectType.Library);
        if (authority == "*LIBCRTAUT") authority = library.ExtendedAttributes?.GetValueOrDefault("ipc.library.createAuthority") ?? "*CHANGE";
        var list = ObjectName.IsValid(authority); AuthorityBit bits;
        if (list) { _ = objects.GetRequired("QSYS", authority, ObjectType.AuthorizationList); authorization.RequireObject("QSYS", authority, ObjectType.AuthorizationList, AuthorityBit.ObjectReference); bits = AuthorityBit.None; }
        else { try { bits = Authorities.FromLevel(authority); } catch (ArgumentException) { throw Invalid("Invalid journal public authority."); } }
        objects.Create(new ObjectDescriptor { Key = name, ObjectType = type, Owner = OperationIdentity.Current?.Principal ?? "QSECOFR", PublicAuthority = bits,
            Attribute = "LOCAL", Format = "IPCJRN1", Description = text, UseAuthorizationListPublicAuthority = list }, connection, transaction);
        if (list)
        {
            using var attach = Command(connection, transaction, "INSERT INTO sys_authorities(lib,name,type,holder,is_authl,bits) VALUES($library,$name,$type,$holder,1,$bits)");
            Bind(attach, name); attach.Parameters.AddWithValue("$type", type); attach.Parameters.AddWithValue("$holder", authority); attach.Parameters.AddWithValue("$bits", (int)Authorities.AllBits); attach.ExecuteNonQuery();
        }
    }
    private static void AttachCore(SqliteConnection connection, SqliteTransaction transaction, string journalId, QualifiedName journal, string receiverId)
    {
        using var command = Command(connection, transaction, """
            UPDATE sys_journals SET receiver_chain=receiver_chain+1 WHERE id=$journal;
            UPDATE sys_journal_receivers SET journal_id=$journal,origin_id=$journal,origin_journal=$origin,
                chain=(SELECT receiver_chain FROM sys_journals WHERE id=$journal),attached_at=$now WHERE id=$receiver;
            """);
        command.Parameters.AddWithValue("$journal", journalId); command.Parameters.AddWithValue("$origin", journal.ToString()); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$receiver", receiverId); command.ExecuteNonQuery();
    }
    private static QualifiedName NextName(QualifiedName current)
    {
        var text = current.Name.Value; var end = text.Length; while (end > 0 && char.IsAsciiDigit(text[end - 1])) end--;
        var suffix = text[end..]; if (suffix.Length == 0) { if (text.Length > 6) throw Invalid("Generated receiver names need a numeric suffix or a prefix of at most six characters."); return new(current.Library, text + "0001"); }
        var next = long.Parse(suffix, CultureInfo.InvariantCulture) + 1; var digits = next.ToString("D" + suffix.Length, CultureInfo.InvariantCulture);
        if (end + digits.Length > 10) throw Invalid("Generated receiver name would exceed ten characters."); return new(current.Library, text[..end] + digits);
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
    private static void Bind(SqliteCommand command, QualifiedName name) { command.Parameters.AddWithValue("$library", name.Library); command.Parameters.AddWithValue("$name", name.Name.Value); }
    private static SqliteTransaction BeginWrite(SqliteConnection connection, CancellationToken cancellationToken) =>
        SqliteCancellation.BeginWrite(connection, cancellationToken);
    private static CpfException Invalid(string text) => new("IPC0210", text);
}
