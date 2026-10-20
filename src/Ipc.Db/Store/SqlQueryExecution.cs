using System.Text;
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
    public SqlQueryCursor OpenSqlQuery(string sql, SqlExecutionOptions options, IReadOnlyList<SqlQueryParameter>? parameters = null,
        SqlQueryLimits? limits = null, CancellationToken cancellationToken = default) => OpenSqlQueryCore(sql, options, parameters, limits, cancellationToken, false);
    public IReadOnlyList<SqlQueryColumn> PrepareSqlQuery(string sql, SqlExecutionOptions options, IReadOnlyList<SqlQueryParameter>? parameters = null, CancellationToken cancellationToken = default)
    { using var prepared = OpenSqlQueryCore(sql, options, parameters, null, cancellationToken, true); return prepared.Columns; }
    private SqlQueryCursor OpenSqlQueryCore(string sql, SqlExecutionOptions options, IReadOnlyList<SqlQueryParameter>? parameters,
        SqlQueryLimits? limits, CancellationToken cancellationToken, bool prepareOnly)
    {
        limits ??= new(); parameters = parameters?.ToArray() ?? []; SqlSelect query;
        try
        {
            limits.Validate();
            if (!Ipc.Core.Text.CodePage.IsSupported(options.Ccsid)) throw new CpfException("SQL0104", "Unsupported query CCSID.");
            query = SqlStatementParser.Parse(sql, options) as SqlSelect ?? throw new CpfException("SQL0104", "A query requires a SELECT statement.");
        }
        catch (CpfException error) { throw QueryError(error); }
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _factory.ShutdownToken); var token = lifetime.Token;
        var identity = OperationIdentity.Current; var allocations = new List<IDisposable>();
        var accesses = new List<(string Library, string Name, string? Owner)>(); var compilers = new List<SqlQueryCompiler>(); QueryPlan? plan = null;
        var diagnostics = new List<SqlQueryDiagnostic>();
        SqliteConnection? metadata = null, connection = null; SqliteTransaction? snapshot = null, transaction = null;
        RecordReadSnapshot? privateSnapshot = null; IDisposable? registration = null; SqlQueryCursor? cursor = null;
        var gate = new object();
        RecordOperation? operation = null; SqliteCommand? command = null; SqliteDataReader? reader = null; SqlQueryCompiler? compiler = null;
        var disposed = false; long rows = 0, cells = 0, bytes = 0;
        lock (gate) try
        {
            var resource = new QueryResource(() => { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }, () => { lock (gate) { if (cursor is not null) cursor.Dispose(); else Dispose(); } });
            registration = identity?.Job is { } owner ? _factory.RegisterJobRuntimeResource(owner, resource) : _factory.RegisterRuntimeResource(resource);
            ValidateCaller();
            metadata = _factory.Open(); snapshot = metadata.BeginTransaction(deferred: true);
            var physical = new HashSet<QualifiedName>();
            plan = ResolveQueryPlan(metadata, snapshot, query, options, token, Access, physical);
            snapshot.Dispose(); snapshot = null; metadata.Dispose(); metadata = null;
            operation = prepareOnly ? null : _factory.RecordTransactions.OpenEnlisted(physical.ToArray(), token, memberNames: []);
            if (operation is not null) { connection = operation.Connection; transaction = operation.Transaction; }
            else { connection = _factory.Open(); transaction = connection.BeginTransaction(deferred: true); }
            var generated = CompileQueryPlan(plan, connection, parameters, limits, token, compilers); compiler = compilers[^1];
            command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = generated;
            if (operation is not null)
            {
                operation.Prepare(command);
                command.Dispose(); command = null; DisposeCompilers(false); compiler = null;
                privateSnapshot = operation.OpenReadSnapshot(); operation = null;
                connection = privateSnapshot.Connection; transaction = privateSnapshot.Transaction;
                command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = CompileQueryPlan(plan, connection, parameters, limits, token, compilers); compiler = compilers[^1];
            }
            try { if (prepareOnly) SqliteCancellation.Run(connection, token, () => { command.Prepare(); return 0; }); else reader = SqliteCancellation.Run(connection, token, command.ExecuteReader); }
            catch (SqliteException) { RethrowFailure(); throw; }
            return cursor = new(Array.AsReadOnly(compiler.Columns.ToArray()), Read, Dispose, Diagnostics, gate, token.ThrowIfCancellationRequested, () => { try { ValidateCaller(); } catch (CpfException error) { throw QueryError(error); } });

            void Access(string library, string name, string? owner)
            {
                if (owner is null) Authorize(library, name, AuthorityBit.Read);
                else new ServiceAuthorization(_factory).RequireViewSource(owner, library, name, Authorities.UseBits);
                if (accesses.Contains((library, name, owner))) return;
                accesses.Add((library, name, owner));
                if (identity?.Job is not { } job || accesses.Any(item => item.Library == library && item.Name == name && item.Owner != owner)) return;
                allocations.Add(new JobLockStore(_factory).AcquireWithLifetime(job, new(library, name, ObjectType.File), JobLockMode.SharedRead,
                    TimeSpan.Zero, token, "Query", Guid.NewGuid().ToString("N"), viewOwner: owner));
            }
        }
        catch (CpfException error) { Dispose(); throw QueryError(error); }
        catch (SqliteException)
        {
            CpfException failure = new("SQL0901", "Query could not be prepared.");
            try { RethrowFailure(); } catch (CpfException error) { failure = error; } finally { Dispose(); }
            throw QueryError(failure);
        }
        catch { Dispose(); throw; }

        IReadOnlyList<object?>? Read()
        {
            try
            {
                ValidateCaller();
                if (!SqliteCancellation.Run(connection!, token, reader!.Read)) { return null; }
                var row = compiler!.ReadRow(reader);
                cells += row.Count; rows++;
                bytes += row.Sum(value => value switch { string text => (long)Encoding.UTF8.GetByteCount(text), SqlDecimalValue number => Encoding.UTF8.GetByteCount(number.Text), _ => 16L });
                if (rows > limits.MaximumRows || cells > limits.MaximumCells || bytes > limits.MaximumBytes) throw new CpfException("SQL0904", "Query result resource limit exceeded.");
                return row;
            }
            catch (CpfException error) { throw QueryError(error); }
            catch (SqliteException)
            {
                try { RethrowFailure(); } catch (CpfException error) { throw QueryError(error); }
                throw QueryError(new("SQL0901", "Query evaluation failed."));
            }
        }
        void ValidateCaller()
        {
            token.ThrowIfCancellationRequested(); var caller = OperationIdentity.Current;
            if (caller?.Job != identity?.Job || caller?.Principal != identity?.Principal || caller?.AuthSessionId != identity?.AuthSessionId)
                throw new CpfException("CPF9802", "Query cursor belongs to another execution context.");
            if (identity is not null)
            {
                using var check = _factory.Open(); new ServiceAuthorization(_factory).RequireCaller(check, null, identity.Principal);
                if (identity.Job is { } job) new JobDataAreaStore(_factory).RequireOwner(job);
                foreach (var access in accesses)
                {
                    if (access.Owner is null) Authorize(access.Library, access.Name, AuthorityBit.Read);
                    else new ServiceAuthorization(_factory).RequireViewSource(access.Owner, access.Library, access.Name, Authorities.UseBits);
                }
                if (plan is not null) foreach (var table in AllQueryTables(plan).Where(table => table.View is not null))
                {
                    using var current = check.CreateCommand();
                    current.CommandText = "SELECT f.def,o.owner FROM sys_file_defs f JOIN sys_objects o USING(lib,name,type) WHERE f.lib=$lib AND f.name=$name AND f.type='*FILE'";
                    current.Parameters.AddWithValue("$lib", table.Library); current.Parameters.AddWithValue("$name", table.Name);
                    using var result = current.ExecuteReader();
                    if (!result.Read() || result.GetString(0) != table.Definition.ToJson() || result.GetString(1) != table.ViewOwner)
                        throw new CpfException("SQL0104", "View definition or ownership changed while the query was open.");
                }
            }
        }
        void RethrowFailure() { foreach (var item in compilers) item.RethrowFailure(); }
        IReadOnlyList<SqlQueryDiagnostic> Diagnostics() => diagnostics.Concat(compilers.SelectMany(item => item.Diagnostics))
            .GroupBy(item => (item.SqlState, item.Message)).Take(100).Select(group => group.First() with { Occurrences = group.Sum(item => item.Occurrences) }).ToArray();
        void DisposeCompilers(bool retainDiagnostics = true)
        {
            if (retainDiagnostics) { var saved = Diagnostics(); diagnostics.Clear(); diagnostics.AddRange(saved); }
            foreach (var item in compilers.AsEnumerable().Reverse()) item.Dispose(); compilers.Clear();
        }
        void Dispose()
        {
            if (disposed) return; disposed = true;
            try { reader?.Dispose(); }
            finally
            {
                try { command?.Dispose(); DisposeCompilers(); }
                finally
                {
                    try
                    {
                        if (operation is not null) operation.Dispose();
                        else if (privateSnapshot is not null) privateSnapshot.Dispose();
                        else { transaction?.Dispose(); connection?.Dispose(); }
                    }
                    finally { try { snapshot?.Dispose(); metadata?.Dispose(); } finally { try { foreach (var allocation in allocations.AsEnumerable().Reverse()) allocation.Dispose(); } finally { registration?.Dispose(); lifetime.Dispose(); } } }
                }
            }
        }
    }
    private sealed class QueryResource(Action cancel, Action close) : IDisposable
    { public void Dispose() { cancel(); close(); } }
    private static CpfException QueryError(CpfException error) => SqlDiagnostics.Enrich(error.MessageId switch {
        "CPF9801" or "CPF9810" or "CPF2110" or "CPF2105" => new("SQL0204", "Query object not found."),
        "CPF9802" => new("SQL0551", "Query access is not authorized."), _ => error });
}
