using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private ForeignCascadeLocks? LockForeignParentChecks(SqliteConnection connection, SqliteTransaction transaction, QualifiedName root,
        string member, IReadOnlySet<long> rows, IReadOnlySet<string> columns, CancellationToken token, Action? validateRoot = null, bool deleting = false)
    {
        if (rows.Count == 0 || OperationIdentity.Current?.Job is not { } job) return null;
        var table = MemberTable(root.Library,root.Name.Value,member);
        var relations = SqlForeignCatalog.Files(connection,transaction).SelectMany(file => (file.Definition.SqlTable?.ForeignKeys ?? [])
            .Where(foreign => SqlForeignSchema.Enforced(foreign) && SqlForeignSchema.ParentTable(foreign)==table && (deleting ? foreign.DeleteRule is SqlForeignDelete.NoAction or SqlForeignDelete.Restrict : foreign.ParentColumns!.Any(columns.Contains)))
            .Select(foreign => (file.Key,file.Definition,Foreign:foreign))).ToArray();
        if (relations.Length == 0) return null;
        var rootDefinition = ReadDefinition(connection,transaction,root.Library,root.Name.Value)!;
        var expected = new Dictionary<QualifiedName,string> { [root]=rootDefinition.ToJson() };
        var members = new Dictionary<QualifiedName,(string Member,string? Identity)> { [root]=(member,RecordSlots.Identity(connection,transaction,root.Library,root.Name.Value,member)) };
        var dependents = new Dictionary<string,CascadeMember>();
        var leases = new List<IDisposable>(); var files = new HashSet<QualifiedName>(); long count=0;
        try
        {
            foreach (var relation in relations)
            {
                token.ThrowIfCancellationRequested(); expected.TryAdd(relation.Key,relation.Definition.ToJson());
                if (files.Add(relation.Key)) leases.Add(new JobLockStore(_factory).AcquireReferentialFile(job,relation.Key,token,() => ValidateIndexPublisher(connection,transaction,token),transaction));
                if (!members.TryGetValue(relation.Key,out var child))
                {
                    using var read = connection.CreateCommand(); read.Transaction=transaction;
                    read.CommandText="SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY created,mbr LIMIT 1";
                    read.Parameters.AddWithValue("$lib",relation.Key.Library); read.Parameters.AddWithValue("$name",relation.Key.Name.Value);
                    var childMember=read.ExecuteScalar() as string ?? throw new CpfException("SQL0204","Referential dependent member is unavailable.");
                    child=(childMember,RecordSlots.Identity(connection,transaction,relation.Key.Library,relation.Key.Name.Value,childMember)); members.Add(relation.Key,child);
                }
                var childTable=MemberTable(relation.Key.Library,relation.Key.Name.Value,child.Member);
                if (!dependents.TryGetValue(childTable,out var target)) dependents.Add(childTable,target=new(relation.Key,child.Member,relation.Definition,[]));
                var matches=relation.Foreign.Columns.Select((column,index) => "p."+Quote(relation.Foreign.ParentColumns![index])+"=c."+Quote(column));
                foreach (var chunk in rows.Chunk(500))
                {
                    using var read=connection.CreateCommand(); read.Transaction=transaction;
                    var parentRow="p."+Quote(RowIdentitySql(rootDefinition.PrimaryFormat));
                    read.CommandText="SELECT min(c."+Quote(RowIdentitySql(relation.Definition.PrimaryFormat))+") FROM "+Quote(table)+" p JOIN "+Quote(childTable)+" c ON "+string.Join(" AND ",matches)+
                        " WHERE "+parentRow+" IN("+string.Join(',',chunk.Select((_,index) => "$r"+index))+") GROUP BY "+parentRow;
                    for (var index=0;index<chunk.Length;index++) read.Parameters.AddWithValue("$r"+index,chunk[index]);
                    SqliteCancellation.Run(connection,token,() => {
                        using var reader=read.ExecuteReader();
                        while (reader.Read())
                        {
                            token.ThrowIfCancellationRequested();
                            if (target.Rows.Add(reader.GetInt64(0)) && ++count>1000000) throw new CpfException("SQL0904","Referential validation exceeds one million record locks.");
                        }
                        return 0;
                    });
                }
            }
            foreach (var dependent in dependents.OrderBy(item=>item.Key,StringComparer.Ordinal))
            {
                var target=dependent.Value;
                if (target.Rows.Count==0) continue;
                // Constraint validation needs a stable read: ordinary input can
                // coexist, but another job's retained update read cannot.
                var lease=new JobLockStore(_factory).AcquireStatementRows(job,new(target.File.Library,target.File.Name.Value,FileType,target.Member),target.Rows,
                    AuthorityBit.None,token,validateAuthority:() => ValidateIndexPublisher(connection,transaction,token),publisher:transaction,mode:JobLockMode.SharedNoUpdate);
                if (lease is not null) leases.Add(lease);
            }
            return new(leases,() => {
                foreach (var (key,definition) in expected)
                    if (ReadDefinition(connection,transaction,key.Library,key.Name.Value)?.ToJson()!=definition)
                        throw new CpfException("SQL0104","Referential definitions changed during referential publication.");
                foreach (var (key,binding) in members)
                {
                    using var check=connection.CreateCommand(); check.Transaction=transaction;
                    check.CommandText="SELECT count(*) FROM sys_file_members WHERE lib=$lib AND name=$name AND mbr=$member AND type='*FILE'";
                    check.Parameters.AddWithValue("$lib",key.Library); check.Parameters.AddWithValue("$name",key.Name.Value); check.Parameters.AddWithValue("$member",binding.Member);
                    if (Convert.ToInt64(check.ExecuteScalar())!=1 || RecordSlots.Identity(connection,transaction,key.Library,key.Name.Value,binding.Member)!=binding.Identity)
                        throw new CpfException("SQL0104","Referential member changed during referential publication.");
                }
                if (validateRoot is not null) validateRoot();
                else new ServiceAuthorization(_factory).RequireBoundObject(connection,transaction,root.Library,root.Name.Value,AuthorityBit.ObjectOperate|(deleting ? AuthorityBit.Delete : AuthorityBit.Update));
                ValidateIndexPublisher(connection,transaction,token);
            });
        }
        catch (CpfException failure) { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); DatabaseRecordErrors.MarkRecordLockFailure(failure); throw; }
        catch { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); throw; }
    }
}
