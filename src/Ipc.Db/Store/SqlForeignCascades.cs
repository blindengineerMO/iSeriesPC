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
    private sealed record CascadeMember(QualifiedName File, string Member, FileDefinition Definition, HashSet<long> Rows);
    private sealed class ForeignCascadeLocks(List<IDisposable> leases, Action validate) : IDisposable
    {
        internal void Validate() => validate();
        public void Dispose() { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); }
    }
    private ForeignCascadeLocks? LockForeignCascades(SqliteConnection connection, SqliteTransaction transaction, QualifiedName root,
        string member, IReadOnlySet<long> rows, CancellationToken token, Action? validateRoot = null)
    {
        if (rows.Count == 0 || OperationIdentity.Current?.Job is not { } job) return null;
        var files = SqlForeignCatalog.Files(connection,transaction).ToDictionary(file => file.Key,file => file.Definition);
        var relationships = files.SelectMany(file => (file.Value.SqlTable?.ForeignKeys ?? []).Where(SqlForeignSchema.Enforced).Select(foreign => (File:file.Key,Definition:file.Value,Foreign:foreign))).GroupBy(item => SqlForeignSchema.ParentTable(item.Foreign)).ToDictionary(group => group.Key,group => group.ToArray());
        var rootTable = MemberTable(root.Library,root.Name.Value,member); if (!relationships.ContainsKey(rootTable)) return null;
        var deleted = new Dictionary<string,HashSet<long>> { [rootTable] = rows.ToHashSet() };
        var affected = new Dictionary<string,CascadeMember>(); var members = new Dictionary<QualifiedName,string>();
        var expected = new Dictionary<QualifiedName,string>(); var queue = new Queue<(string Table,HashSet<long> Rows)>(); queue.Enqueue((rootTable,rows.ToHashSet()));
        long count = rows.Count; var leases = new List<IDisposable>(); var validations = new List<ForeignCascadeLocks>(); var referenced = new HashSet<QualifiedName>();
        try
        {
            // Walk the relationship graph even when a member currently has no
            // matching rows. A publication hook may add a dependent later.
            var graph = new Queue<(string Table, bool Delete)>();
            var visitedGraph = new HashSet<(string Table, bool Delete)>();
            graph.Enqueue((rootTable,true));
            while (graph.TryDequeue(out var node))
            {
                token.ThrowIfCancellationRequested();
                if (!visitedGraph.Add(node) || !relationships.TryGetValue(node.Table,out var children)) continue;
                foreach (var child in children)
                {
                    var parent = new QualifiedName(child.Foreign.Parent.Schema!,child.Foreign.Parent.Name);
                    expected.TryAdd(parent,files[parent].ToJson()); expected.TryAdd(child.File,child.Definition.ToJson());
                    if (referenced.Add(child.File)) leases.Add(new JobLockStore(_factory).AcquireReferentialFile(job,child.File,token,() => ValidateIndexPublisher(connection,transaction,token),transaction));
                    if (!node.Delete || child.Foreign.DeleteRule is SqlForeignDelete.NoAction or SqlForeignDelete.Restrict) continue;
                    var target = CascadeTarget(child.File,child.Definition);
                    graph.Enqueue((target.Table,child.Foreign.DeleteRule == SqlForeignDelete.Cascade));
                }
            }
        while (queue.TryDequeue(out var pending))
        {
            token.ThrowIfCancellationRequested();
            if (!relationships.TryGetValue(pending.Table,out var children)) continue;
            foreach (var child in children)
            {
                var parent = new QualifiedName(child.Foreign.Parent.Schema!,child.Foreign.Parent.Name);
                expected.TryAdd(parent,files[parent].ToJson()); expected.TryAdd(child.File,child.Definition.ToJson());
                if (referenced.Add(child.File)) leases.Add(new JobLockStore(_factory).AcquireReferentialFile(job,child.File,token,() => ValidateIndexPublisher(connection,transaction,token),transaction));
                if (child.Foreign.DeleteRule is SqlForeignDelete.NoAction or SqlForeignDelete.Restrict) continue;
                var (table,changes) = CascadeTarget(child.File,child.Definition);
                var matches = child.Foreign.Columns.Select((column,index) => "p." + Quote(child.Foreign.ParentColumns![index]) + "=c." + Quote(column));
                var discovered = new HashSet<long>();
                foreach (var chunk in pending.Rows.Chunk(500))
                {
                    token.ThrowIfCancellationRequested(); using var read = connection.CreateCommand(); read.Transaction = transaction;
                    read.CommandText = "SELECT c." + Quote(RowIdentitySql(child.Definition.PrimaryFormat)) + " FROM " + Quote(table) + " c JOIN " + Quote(pending.Table) + " p ON " + string.Join(" AND ",matches) +
                        " WHERE p." + Quote(RowIdentitySql(files[parent].PrimaryFormat)) + " IN(" + string.Join(',',chunk.Select((_,index) => "$r" + index)) + ")";
                    for (var index=0;index<chunk.Length;index++) read.Parameters.AddWithValue("$r" + index,chunk[index]);
                    SqliteCancellation.Run(connection,token,() => {
                        using var reader = read.ExecuteReader(); while (reader.Read())
                        {
                            token.ThrowIfCancellationRequested(); var number = reader.GetInt64(0); discovered.Add(number);
                            if (changes.Rows.Add(number) && (table != rootTable || !rows.Contains(number)) && ++count > 1000000) throw new CpfException("SQL0904","Referential actions exceed one million affected records.");
                        }
                        return 0;
                    });
                }
                if (child.Foreign.DeleteRule != SqlForeignDelete.Cascade) continue;
                if (!deleted.TryGetValue(table,out var visited)) deleted.Add(table,visited=[]);
                discovered.ExceptWith(visited); if (discovered.Count == 0) continue;
                visited.UnionWith(discovered); queue.Enqueue((table,discovered));
            }
        }
            foreach (var item in deleted.OrderBy(pair=>pair.Key,StringComparer.Ordinal))
            {
                var parentFile=item.Key==rootTable ? root : affected[item.Key].File;
                var parentMember=item.Key==rootTable ? member : affected[item.Key].Member;
                var validation=LockForeignParentChecks(connection,transaction,parentFile,parentMember,item.Value,new HashSet<string>(),token,
                    () => ValidateIndexPublisher(connection,transaction,token),deleting:true);
                if (validation is not null) { validations.Add(validation); leases.Add(validation); }
            }
            foreach (var item in affected.OrderBy(pair => pair.Key,StringComparer.Ordinal))
            {
                var changes = item.Value;
                if (item.Key == rootTable) changes.Rows.ExceptWith(rows);
                if (changes.Rows.Count == 0) continue;
                AcquireRows(changes,changes.Rows);
            }
            var audit = BeginCascadeAudit(connection,transaction,affected,token);
            return new(leases,() => {
                foreach (var validation in validations) validation.Validate();
                foreach (var (table,actual) in audit.Read())
                {
                    var changes = affected[table];
                    actual.ExceptWith(changes.Rows);
                    if (table == rootTable) actual.ExceptWith(rows);
                    if (actual.Count == 0) continue;
                    count += actual.Count;
                    if (count > 1000000) throw new CpfException("SQL0904","Referential actions exceed one million affected records.");
                    AcquireRows(changes,actual);
                }
                audit.Remove();
                foreach (var (key,definition) in expected)
                    if (ReadDefinition(connection,transaction,key.Library,key.Name.Value)?.ToJson() != definition)
                        throw new CpfException("SQL0104","Referential definitions changed during cascading publication.");
                if (validateRoot is not null) validateRoot();
                else new ServiceAuthorization(_factory).RequireBoundObject(connection,transaction,root.Library,root.Name.Value,AuthorityBit.ObjectOperate | AuthorityBit.Delete);
                ValidateIndexPublisher(connection,transaction,token);
            });
        }
        catch (CpfException failure) { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); DatabaseRecordErrors.MarkRecordLockFailure(failure); throw; }
        catch { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); throw; }

        (string Table,CascadeMember Changes) CascadeTarget(QualifiedName file,FileDefinition definition)
        {
            if (!members.TryGetValue(file,out var childMember))
            {
                using var read = connection.CreateCommand(); read.Transaction = transaction;
                read.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY created,mbr LIMIT 1";
                read.Parameters.AddWithValue("$lib",file.Library); read.Parameters.AddWithValue("$name",file.Name.Value);
                childMember = read.ExecuteScalar() as string ?? throw new CpfException("SQL0204","Cascade member is unavailable.");
                members.Add(file,childMember);
            }
            var table = MemberTable(file.Library,file.Name.Value,childMember);
            if (!affected.TryGetValue(table,out var changes)) affected.Add(table,changes = new(file,childMember,definition,[]));
            return (table,changes);
        }
        void AcquireRows(CascadeMember changes,IReadOnlySet<long> numbers)
        {
            RequireUnenlisted(changes.File.Library,changes.File.Name.Value,"Cascading SQL COMMIT(*NONE)");
            try
            {
                var lease = new JobLockStore(_factory).AcquireStatementRows(job,new(changes.File.Library,changes.File.Name.Value,FileType,changes.Member),numbers,
                    AuthorityBit.None,token,validateAuthority:() => ValidateIndexPublisher(connection,transaction,token),publisher:transaction);
                if (lease is not null) leases.Add(lease);
            }
            catch (CpfException failure) { DatabaseRecordErrors.MarkRecordLockFailure(failure); throw; }
        }
    }
}
