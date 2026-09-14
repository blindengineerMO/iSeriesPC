using System.Collections.Concurrent;
using Ipc.Core.Messages;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Logging;

public sealed class HostLog
{
    private readonly string? _logDirectory;
    private readonly SqliteConnectionFactory _sqliteFactory;
    private readonly object _writeGate = new();

    public HostLog(SqliteConnectionFactory sqliteFactory, string? logDirectory = null)
    {
        _sqliteFactory = sqliteFactory;
        _logDirectory = logDirectory;
    }

    public void Info(string message) => Write("INFO", null, null, message);

    public void Error(string message) => Write("ERROR", null, null, message);

    public void Error(Exception ex, string message) =>
        Write("ERROR", null, null, $"{message}: {ex}");

    public void History(IpcMessage message) =>
        Write("HST", message.MessageId, message.From, message.Text);

    public void JobMessage(string job, string messageId, string message) =>
        Write("JOB", messageId, job, message);

    private void Write(string kind, string? messageId, string? job, string message)
    {
        var at = DateTimeOffset.UtcNow.ToString("O");
        lock (_writeGate)
        {
            if (_logDirectory is not null)
            {
                Directory.CreateDirectory(_logDirectory);
                var today = DateTime.UtcNow.ToString("yyyyMMdd");
                var file = Path.Combine(_logDirectory, $"ipcsys-{today}.log");
                File.AppendAllText(file, $"{at} [{kind}] {job} {messageId} {message}\n");
            }

            try
            {
                using var connection = _sqliteFactory.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO sys_hst (at, job, message_id, message)
                    VALUES ($at, $job, $message_id, $message)
                    """;
                cmd.Parameters.AddWithValue("$at", at);
                cmd.Parameters.AddWithValue("$job", (object?)job ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$message_id", (object?)messageId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$message", message);
                cmd.ExecuteNonQuery();
            }
            catch (InvalidOperationException)
            {
            }
            catch (SqliteException)
            {
            }
        }
    }

    public IReadOnlyList<(DateTimeOffset At, string? Job, string? MessageId, string Message)> Recent(int limit)
    {
        using var connection = _sqliteFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT at, job, message_id, message FROM sys_hst ORDER BY seq DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<(DateTimeOffset, string?, string?, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((
                DateTimeOffset.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3)));
        }

        list.Reverse();
        return list;
    }
}