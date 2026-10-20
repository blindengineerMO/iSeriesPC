using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Definitions;
using Ipc.Services.Sqlite;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    /// <summary>Extend one physical member with default records or allocated deleted positions.</summary>
    public long InitializeMember(string library, string name, string member = "*FIRST", long? totalRecords = null,
        bool deleted = false, CancellationToken cancellationToken = default)
    {
        string? pinnedMember=null;long? pinnedTotal=null;string? identity=null;string? format=null;
        return WithCapacityRecovery(token=>InitializeMemberCore(library,name,pinnedMember??member,pinnedTotal??totalRecords,deleted,token,identity,format),cancellationToken,
            request=>{
                pinnedMember??=request.Member;pinnedTotal??=request.Requested;identity??=request.Identity;format??=request.Format;
                AuthorizeFileManagement(request.Library,request.File,AuthorityBit.ObjectOperate|AuthorityBit.Add|(deleted?AuthorityBit.Delete:AuthorityBit.None));
                RequireUnenlisted(request.Library,request.File,"INZPFM");
            });
    }
    private long InitializeMemberCore(string library,string name,string member,long? totalRecords,bool deleted,CancellationToken cancellationToken,string? expectedIdentity,string? expectedFormat)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _factory.ShutdownToken);
        cancellationToken = lifetime.Token;
        if (totalRecords is < 1 or > RecordSlots.MaximumNumber) throw LogicalError("TOTRCDS requires 1–4294967288 or *NXTINCR.");
        library = ResolveLibrary(library).ToUpperInvariant(); name = name.ToUpperInvariant(); member = member.ToUpperInvariant();
        cancellationToken.ThrowIfCancellationRequested();
        var locks = new JobLockStore(_factory);
        using var gate = locks.EnterRecordWrite(cancellationToken);
        var permission = AuthorityBit.ObjectOperate | AuthorityBit.Add | (deleted ? AuthorityBit.Delete : AuthorityBit.None);
        AuthorizeFileManagement(library, name, permission); RequireUnenlisted(library, name, "INZPFM");
        using var connection = _factory.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, cancellationToken);
        // Check live identity, credentials and attributes after a writer wait.
        if (OperationIdentity.Current?.Job is { } job) new JobDataAreaStore(_factory).RequireOwner(job);
        AuthorizeFileManagement(library, name, permission); RequireUnenlisted(library, name, "INZPFM");
        var definition = ReadDefinition(connection, transaction, library, name) ?? throw new CpfException("CPF9801", "Physical file not found.");
        if (definition.Logical is not null || definition.Attribute is not (FileAttribute.Physical or FileAttribute.Source) || definition.Formats.Count != 1)
            throw LogicalError("INZPFM requires a single-format physical data/source file.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name);
        if (member is "*FIRST" or "*LAST")
        {
            command.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY created " +
                (member == "*LAST" ? "DESC,mbr DESC" : "ASC,mbr ASC") + " LIMIT 1";
            member = command.ExecuteScalar() as string ?? throw new CpfException("CPF2817", "File has no members.");
        }
        if (!ObjectName.IsValid(member)) throw LogicalError("MBR requires a member name, *FIRST or *LAST.");
        var id = RecordSlots.Identity(connection, transaction, library, name, member) ?? throw new CpfException("CPF2817", "Member not found.");
        if(expectedIdentity is not null&&(id!=expectedIdentity||CursorFingerprint(definition)!=expectedFormat))throw LogicalError("Initialization member or format changed during recovery.");
        locks.RequireObjectMutation(library, name, FileType);
        var before = RecordSlots.HighWater(connection, transaction, id);
        var table = MemberTable(library, name, member);
        var physicalColumns = JournalHooks.Columns(connection, transaction, table);
        command.CommandText = "SELECT wr FROM pragma_table_list WHERE schema='main' AND name=$table";
        command.Parameters.AddWithValue("$table", table);
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw LogicalError("INZPFM requires a physical member with relative record positions.");
        RejectInitializationInsertTriggers(connection, transaction, table);
        var size = definition.MemberSize ?? new(); size.Validate();
        var effectiveCapacity=EffectiveMemberCapacity(connection,transaction,library,name,member,definition);
        if(totalRecords is null&&before>=size.InitialRecords&&size.IncrementRecords==0)throw new CpfException("CPF3143","Increments are not allowed for this member.");
        var total = totalRecords ?? size.NextExtent(before,RecordSlots.MaximumNumber);
        if (total <= before) { transaction.Commit(); return 0; }
        var format = definition.PrimaryFormat; var rowid = CursorRowid(physicalColumns);
        object?[]? values=null;
        if (!deleted)
        {
            values = format.Fields.Select(field => RecordSqlValue(field, DefaultFor(field))).ToArray();
            var bytes = values.Sum(value => value is string text ? (long)Encoding.UTF8.GetByteCount(text) : value is byte[] raw ? raw.Length : 16L);
            if (total - before > 1000000 || bytes > 67108864 / (total - before)) throw new CpfException("IPC0139", "Default initialization exceeds one million records or 64 MiB.");
        }
        if(total>effectiveCapacity)
        {
            if(size.IncrementRecords==0)throw new CpfException("CPF3143","Increments are not allowed for this member.");
            throw new MemberFullRequest(library,name,member,id,CapacitySignature(size),CursorFingerprint(definition),total,effectiveCapacity,initialization:true);
        }
        string? image = null;
        if(!deleted)
        {
            EnsureExactDecimalStorage(connection, transaction, table, format);
            // INZPFM is captured as one F/IZ event, rather than ordinary PT entries.
            connection.CreateFunction("ipc_journal_replay", () => 1);
            command.CommandText = "INSERT INTO " + Quote(table) + " (" + Quote(rowid) + "," + string.Join(',', format.Fields.Select(field => Quote(field.Name))) +
                ") VALUES ($rrn," + string.Join(',', values!.Select((_, index) => "$v" + index)) + ")";
            command.Parameters.AddWithValue("$rrn", 0L);
            for (var index = 0; index < values!.Length; index++) command.Parameters.AddWithValue("$v" + index, values[index] ?? DBNull.Value);
            for (var number = before + 1; number <= total; number++)
            {
                cancellationToken.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint();
                command.Parameters["$rrn"].Value = number;
                SqliteCancellation.Run(connection, cancellationToken, command.ExecuteNonQuery);
            }
            command.CommandText = "SELECT " + JournalHooks.Image("p", physicalColumns) + " FROM " + Quote(table) + " p WHERE p." + Quote(rowid) + "=$rrn";
            image = command.ExecuteScalar() as string;
        }
        RecordSlots.Reserve(connection, transaction, id, total);
        JournalInitialization.Capture(connection, transaction, library, name, member, before, total, deleted, image);
        command.CommandText = "UPDATE sys_objects SET changed=$now WHERE lib=$lib AND name=$name AND type='*FILE'";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return total - before;
    }

    private static void RejectInitializationInsertTriggers(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT name,sql FROM sqlite_schema WHERE type='trigger' AND tbl_name=$table"; command.Parameters.AddWithValue("$table", table);
        var triggers = new List<(string Name, string Sql)>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1)));
        foreach (var trigger in triggers)
        {
            if (JournalHooks.OwnsTrigger(connection, transaction, trigger.Name) || RecordSlots.OwnsTrigger(connection, transaction, trigger.Name)) continue;
            var offset = 0; var token = "";
            while (!token.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase)) token = MemberTableCopy.NextToken(trigger.Sql, ref offset).Text;
            token = MemberTableCopy.NextToken(trigger.Sql, ref offset).Text;
            if (token.Equals("IF", StringComparison.OrdinalIgnoreCase))
                for (var index = 0; index < 3; index++) token = MemberTableCopy.NextToken(trigger.Sql, ref offset).Text;
            token = MemberTableCopy.NextToken(trigger.Sql, ref offset).Text;
            if (token.Equals("BEFORE", StringComparison.OrdinalIgnoreCase) || token.Equals("AFTER", StringComparison.OrdinalIgnoreCase)) token = MemberTableCopy.NextToken(trigger.Sql, ref offset).Text;
            if (token.Equals("INSERT", StringComparison.OrdinalIgnoreCase)) throw LogicalError("INZPFM cannot initialize a file with an insert trigger.");
        }
    }
}
