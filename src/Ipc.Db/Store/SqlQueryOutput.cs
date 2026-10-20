using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Commands;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed record SqlQueryFileResult(long Rows, string Member, bool Created, IReadOnlyList<SqlQueryDiagnostic> Diagnostics);

public sealed partial class SqliteFileStore
{
    /// <summary>Spool a typed SELECT, then save it using no-commit create/clear/ordered record publication.
    /// The optional query validation callback must only read; it also runs after taking the publisher's writer lock.</summary>
    public SqlQueryFileResult WriteSqlQueryFile(string sql, SqlExecutionOptions options, QualifiedName target,
        string member = "*FIRST", bool replace = true, IReadOnlyList<SqlQueryParameter>? parameters = null,
        SqlQueryLimits? limits = null, CancellationToken cancellationToken = default, Action? validateQuery = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _factory.ShutdownToken); var token = stop.Token;
        var gate = new object(); IDisposable? registration = null;
        lock (gate) try
        {
            token.ThrowIfCancellationRequested();
            var resource = new QueryResource(() => { try { stop.Cancel(); } catch (ObjectDisposedException) { } }, () => { lock (gate) { } });
            registration = OperationIdentity.Current?.Job is { } owner ? _factory.RegisterJobRuntimeResource(owner, resource) : _factory.RegisterRuntimeResource(resource);
            target = new JobTemporaryLibrary(_factory).Resolve(target, FileType); member = member.ToUpperInvariant();
            if (member != "*FIRST" && !ObjectName.IsValid(member)) throw new CpfException("SQL0104", "Invalid query output member.");
            RequireUnenlisted(target.Library, target.Name.Value, "Query output COMMIT(*NONE)");
            using var query = OpenSqlQuery(sql, options, parameters, limits, token);
            using var stage = new SqlQueryOutputStage(query, token);
            token.ThrowIfCancellationRequested(); validateQuery?.Invoke(); query.CheckAccess();
            var locks = new JobLockStore(_factory);
            using var allocation = OperationIdentity.Current?.Job is { } job ? locks.EnterCommand(job, token) : null;
            var existing = _objects.GetForAuthorization(target.Library, target.Name.Value, FileType);
            var created = existing is null; var changed = false; long written = 0;
            var output = existing is null ? QueryOutputLayout(target.Name.Value, query.Columns, options.Ccsid) :
                GetDefinition(target.Library, target.Name.Value) ?? throw new CpfException("SQL0204", "Output file not found.");
            RequireQueryOutputLayout(output, query.Columns);
            if (member == "*FIRST") member = existing is null ? target.Name.Value : FirstMember(target.Library, target.Name.Value);
            if (member.Length == 0) throw new CpfException("SQL0204", "Output file has no first member.");
            string? identity = null; var format = CursorFingerprint(output);
            try
            {
                if (created)
                {
                    locks.RequireObjectMutation(target.Library, target.Name.Value, FileType);
                    using var connection = _factory.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, token);
                    ValidatePublisher(connection, transaction); validateQuery?.Invoke(); query.CheckAccess();
                    CreateFileImage(connection, transaction, target.Library, target.Name.Value, PhysicalAttribute, output, null, null, member,
                        publicAuthority: AuthorityBit.None, owner: OperationIdentity.Current?.Principal ?? _owner);
                    ValidatePublisher(connection, transaction); token.ThrowIfCancellationRequested(); transaction.Commit(); changed = true;
                }
                else Validate();
                using (var connection = _factory.Open()) identity = RecordSlots.Identity(connection, null, target.Library, target.Name.Value, member)
                    ?? throw new CpfException("SQL0204", "Output member not found.");
                if (replace && !created)
                {
                    RequireClearCommand();
                    ClearMemberCore(target.Library, target.Name.Value, member, token, Validate, ValidatePublisher); changed = true;
                }
                using var read = stage.Connection.CreateCommand(); read.CommandText = "SELECT * FROM data ORDER BY rowid";
                using var reader = SqliteCancellation.Run(stage.Connection, token, read.ExecuteReader);
                while (SqliteCancellation.Run(stage.Connection, token, reader.Read))
                {
                    Validate(); JobExecutionBudget.Checkpoint();
                    var values = output.PrimaryFormat.Fields.Select((field, index) => (field.Name, Value: reader.IsDBNull(index) ? null : SqlFieldValue(field, reader.GetValue(index))))
                        .ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
                    // End each row's allocations with its publication. Retaining one
                    // automatic holder per row would exhaust the job's lock budget.
                    using var rowScope = OperationIdentity.Current?.Job is { } live ? locks.EnterCommand(live, token) : null;
                    WithCapacityRecovery(rowToken => { InsertCore(target.Library, target.Name.Value, member, output.PrimaryFormat.Name, values,
                        rowToken, Validate, beforePublish: ValidatePublisher); return true; }, token, _ => Validate());
                    written++; changed = true;
                }
                Validate(); return new(written, member, created, query.Diagnostics);
            }
            catch (Exception error) when (error is CpfException or SqliteException)
            {
                var failure = error is CpfException cpf ? cpf : QueryOutputError((SqliteException)error);
                var visible = new JobTemporaryLibrary(_factory).Visible(target, FileType);
                var state = changed ? " Output " + visible.Library + "/" + visible.Name.Value + "(" + member + ") " +
                    (created ? "was created" : replace ? "was cleared" : "was changed") + "; " + written + " rows were written before failure." : " Output records were not changed.";
                throw new CpfException(failure.MessageId, failure.Message + state);
            }
            void RequireClearCommand()
            {
                if (new CommandDefinitionStore(_factory).Load("QSYS", "CLRPFM").Builtin != "CLRPFM")
                    throw new CpfException("CPF9802", "The native CLRPFM adapter is unavailable.");
            }
            void Validate()
            {
                token.ThrowIfCancellationRequested(); validateQuery?.Invoke(); query.CheckAccess();
                new ServiceAuthorization(_factory).RequireObject(target.Library, target.Name.Value, FileType, Authorities.ChangeBits | AuthorityBit.ObjectAlter);
                RequireUnenlisted(target.Library, target.Name.Value, "Query output COMMIT(*NONE)");
                if (GetDefinition(target.Library, target.Name.Value) is not { } current || CursorFingerprint(current) != format)
                    throw new CpfException("SQL0104", "Output file format changed during query output.");
                using var connection = _factory.Open(); var currentId = RecordSlots.Identity(connection, null, target.Library, target.Name.Value, member);
                if (currentId is null || identity is not null && identity != currentId) throw new CpfException("SQL0104", "Output member changed during query output.");
            }
            void ValidatePublisher(SqliteConnection connection, SqliteTransaction transaction)
            {
                token.ThrowIfCancellationRequested();
                if (OperationIdentity.Current is not { } caller) return;
                new ServiceAuthorization(_factory).RequireCaller(connection, transaction, caller.Principal);
                if (caller.Job is not { } live) return;
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT cancel_requested FROM sys_jobs WHERE number=$number AND name=$name AND user=$user AND coalesce(user_profile,user)=$owner AND execution_state='Running'";
                JobRecordTransactions.BindJob(command, live); command.Parameters.AddWithValue("$owner", caller.Principal);
                var canceled = command.ExecuteScalar();
                if (canceled is null) throw new CpfException("CPF1241", "Query output job is no longer active.");
                if (Convert.ToInt32(canceled) != 0) throw new OperationCanceledException("Query output job was canceled.");
            }
        }
        catch (SqliteException error) { throw QueryOutputError(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new CpfException("SQL0901", "Query output spool could not be accessed or cleaned up; inspect the output member before retrying."); }
        finally { registration?.Dispose(); }
    }
    private static CpfException QueryOutputError(SqliteException error) => new(error.SqliteExtendedErrorCode switch {
        1555 or 2067 => "SQL0803", 1299 => "SQL0407", 787 => "SQL0530", 275 => "SQL0545", 13 => "SQL0904", _ => "SQL0901" }, "Query output storage operation failed.");
}
