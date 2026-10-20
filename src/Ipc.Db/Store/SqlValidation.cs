using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
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
    /// <summary>Resolve and type-check the implemented statement without executing it.
    /// Runtime data/constraint and trigger outcomes are still checked only on execution.</summary>
    public void ValidateSql(string sql,SqlExecutionOptions options,CancellationToken cancellationToken=default)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_factory.ShutdownToken);var token=lifetime.Token;
        token.ThrowIfCancellationRequested();
        try
        {
            var statement=SqlStatementParser.Parse(sql,options);
            if(statement is SqlSelect) { _=PrepareSqlQuery(sql,options,cancellationToken:token);return; }
            if(OperationIdentity.Current?.Job is { } job)new JobDataAreaStore(_factory).RequireOwner(job);
            if(statement is SqlCreateIndex index)
            { using var metadata=_factory.Open();using var read=metadata.BeginTransaction(deferred:true);_=BindSqlIndex(metadata,read,index,options);return; }
            if(statement is SqlDropIndex drop)
            { using var metadata=_factory.Open();using var read=metadata.BeginTransaction(deferred:true);_=RequireSqlIndex(metadata,read,drop.Name,options);return; }
            if(statement is SqlCreateView view)
            { using var metadata=_factory.Open();using var read=metadata.BeginTransaction(deferred:true);_=BindSqlView(metadata,read,view,options,token);return; }
            if(statement is SqlAlterTable alter)
            {
                using var metadata=_factory.Open();using var read=metadata.BeginTransaction(deferred:true);var bound=BindTableAlteration(metadata,read,alter,options);
                var foreignChanges=PlanIncomingForeignChanges(metadata,read,bound.Key,bound.Original,bound.Definition,alter.Changes,bound.Dropped);
                SqlConstraintCatalog.ValidateLimits(metadata,read,foreignChanges.Select(change=>(change.Key,change.Definition)).Append((bound.Key,bound.Definition)).ToArray());
                _=PlanAlterDependents(metadata,read,bound,SqlTableLogicalDependents(metadata,read,bound.Key.Library,bound.Key.Name.Value),token);return;
            }
            if(statement is SqlDropView dropView)
            {
                using var metadata=_factory.Open();using var read=metadata.BeginTransaction(deferred:true);var key=RequireSqlView(metadata,read,dropView.Name,options);
                foreach(var dependent in SqlTableLogicalDependents(metadata,read,key.Library,key.Name.Value))
                    new ServiceAuthorization(_factory).RequireObject(dependent.Library,dependent.Name.Value,FileType,AuthorityBit.ObjectOperate|AuthorityBit.ObjectExist);
                return;
            }
            var target=ResolveSqlObject(statement.Table,options,statement is SqlCreateTable);
            using var connection=_factory.Open();using var transaction=connection.BeginTransaction(deferred:true);
            if(statement is SqlCreateTable create)
            {
                if(ReadDefinition(connection,transaction,target.Library,target.Name) is not null)throw new CpfException("SQL0601","Table already exists.");
                foreach(var field in create.Definition.PrimaryFormat.Fields)if(field.DefaultValue is { Value:not null } value)_=SqlFieldValue(field,value.Value);
                BindSqlForeignKeys(connection,transaction,new(target.Library,target.Name),create.Definition,options);
                SqlConstraintCatalog.ValidateLimits(connection,transaction,(new(target.Library,target.Name),create.Definition));
                ValidateSqlConstraintNames(connection,transaction,target.Library,create.Definition);
                CreateSqlTableDescriptor(connection,transaction,new ObjectDescriptor { Key=new(target.Library,target.Name),ObjectType=ObjectType.File,Owner=_owner,Attribute=PhysicalAttribute },options.Naming,validateOnly:true);return;
            }
            var definition=ReadDefinition(connection,transaction,target.Library,target.Name) ?? throw new CpfException("SQL0204","Table not found.");
            if(definition.SqlView is not null && statement is SqlInsert or SqlUpdate or SqlDelete)
            { ValidateViewDml(connection,transaction,new(target.Library,target.Name),definition,statement,options,token);return; }
            if(definition.Logical is not null || definition.Formats.Count!=1 || definition.Attribute!=Definitions.FileAttribute.Physical)throw new CpfException("SQL0104","This statement requires a single-format physical table.");
            if(statement is SqlDropTable)
            {
                Authorize(target.Library,target.Name,AuthorityBit.ObjectExist);
                foreach(var dependent in SqlTableLogicalDependents(connection,transaction,target.Library,target.Name))
                    new ServiceAuthorization(_factory).RequireObject(dependent.Library,dependent.Name.Value,FileType,AuthorityBit.ObjectOperate|AuthorityBit.ObjectExist);
                return;
            }
            RequireUnenlisted(target.Library,target.Name,"SQL COMMIT(*NONE)");
            if(statement is SqlInsert insert) { Authorize(target.Library,target.Name,AuthorityBit.Add);_=BindSqlRows(insert,definition,token,options.Conversion); }
            else
            {
                var (predicate,assignments)=BindSqlMutation(definition,statement,options,target.Name);
                var permission=statement is SqlUpdate ? AuthorityBit.Update : AuthorityBit.Delete;
                if(predicate?.Reads==true || assignments.Any(item=>item.Value.Reads))permission|=AuthorityBit.Read;
                Authorize(target.Library,target.Name,permission);
            }
            using var member=connection.CreateCommand();member.Transaction=transaction;
            member.CommandText="SELECT count(*) FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE'";
            member.Parameters.AddWithValue("$lib",target.Library);member.Parameters.AddWithValue("$name",target.Name);
            if(Convert.ToInt64(member.ExecuteScalar())==0)throw new CpfException("SQL0204","Table has no member.");
            token.ThrowIfCancellationRequested();
        }
        catch(CpfException error) { throw QueryError(error); }
        catch(SqliteException) { throw SqlDiagnostics.Enrich(new("SQL0901","SQL statement could not be validated.")); }
    }
}
