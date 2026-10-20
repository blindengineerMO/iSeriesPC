using System.Text;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed record SavedSqlSession(string Revision, string Settings, IReadOnlyList<string> Statements, string Draft);

/// <summary>Private saved interactive SQL state. Revisions prevent another session's changes being overwritten.</summary>
public sealed class SqlSessionHistoryStore(SqliteConnectionFactory factory)
{
    public const int MaximumStatements = 1000;
    public const int MaximumHistoryBytes = 8388608;
    public SavedSqlSession? Load()
    {
        using var connection = factory.Open(); var principal = Require(connection, null);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT revision,settings,statements,draft FROM sys_sql_sessions WHERE owner=$owner";
        command.Parameters.AddWithValue("$owner", principal); using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        try
        {
            var state = new SavedSqlSession(reader.GetString(0), reader.GetString(1), JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? throw new JsonException(), reader.GetString(3));
            Validate(state.Settings, state.Statements, state.Draft); return state;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { throw new CpfException("SQL0901", "Saved interactive SQL state is invalid."); }
    }
    public string Save(string settings, IReadOnlyList<string> statements, string draft, string? expectedRevision)
    {
        Validate(settings, statements, draft); var revision = Guid.NewGuid().ToString("N");
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false); var owner = Require(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sys_sql_sessions(owner,revision,settings,statements,draft) VALUES($owner,$revision,$settings,$statements,$draft)
            ON CONFLICT(owner) DO UPDATE SET revision=$revision,settings=$settings,statements=$statements,draft=$draft
            WHERE sys_sql_sessions.revision=$expected;
            """;
        command.Parameters.AddWithValue("$owner", owner); command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$settings", settings); command.Parameters.AddWithValue("$statements", JsonSerializer.Serialize(statements)); command.Parameters.AddWithValue("$draft", draft);
        command.Parameters.AddWithValue("$expected", (object?)expectedRevision ?? DBNull.Value);
        if (expectedRevision is not null)
        {
            using var check = connection.CreateCommand(); check.Transaction = transaction; check.CommandText = "SELECT revision FROM sys_sql_sessions WHERE owner=$owner";
            check.Parameters.AddWithValue("$owner", owner);
            if (check.ExecuteScalar() as string != expectedRevision) throw Conflict();
        }
        if (command.ExecuteNonQuery() != 1) throw Conflict();
        Audit(command, owner, "sql.session.saved", statements.Count); transaction.Commit(); return revision;
    }
    public void Delete(string? expectedRevision)
    {
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false); var owner = Require(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM sys_sql_sessions WHERE owner=$owner"; command.Parameters.AddWithValue("$owner", owner);
        var current = command.ExecuteScalar() as string;
        if (current != expectedRevision) throw Conflict();
        if (current is not null)
        {
            command.CommandText = "DELETE FROM sys_sql_sessions WHERE owner=$owner"; command.ExecuteNonQuery(); Audit(command, owner, "sql.session.deleted", 0);
        }
        transaction.Commit();
    }
    private string Require(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var identity = OperationIdentity.Current ?? throw new CpfException("CPF9802", "Interactive SQL history requires an executing profile.");
        new ServiceAuthorization(factory).RequireCaller(connection, transaction, identity.Principal);
        if (identity.Job is { } job)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM sys_jobs WHERE number=$number AND name=$name AND user=$user AND user_profile=$owner AND execution_state='Running' AND cancel_requested=0";
            JobRecordTransactions.BindJob(command, job); command.Parameters.AddWithValue("$owner", identity.Principal);
            if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new CpfException("CPF1241", "Interactive SQL job is no longer active.");
        }
        return identity.Principal;
    }
    private static void Validate(string settings, IReadOnlyList<string> statements, string draft)
    {
        if (settings.Length > 16384 || statements.Count > MaximumStatements || Encoding.UTF8.GetByteCount(draft) > 32768 || statements.Any(s => s is null || Encoding.UTF8.GetByteCount(s) > 32768) || statements.Sum(s => (long)Encoding.UTF8.GetByteCount(s)) > MaximumHistoryBytes)
            throw new CpfException("SQL0904", "Interactive SQL saved-state limit exceeded.");
        try { using var json = JsonDocument.Parse(settings); if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException(); }
        catch (JsonException) { throw new CpfException("SQL0901", "Invalid interactive SQL settings."); }
    }
    private static CpfException Conflict() => new("IPC0130", "Saved SQL session changed; resume or exit without replacing the other session.");
    private static void Audit(SqliteCommand command, string owner, string kind, int count)
    {
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO sys_events(kind,at,principal,job,payload) VALUES($kind,strftime('%Y-%m-%dT%H:%M:%fZ','now'),$owner,ipc_job(),$payload)";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { owner, statements = count })); command.ExecuteNonQuery();
    }
}
