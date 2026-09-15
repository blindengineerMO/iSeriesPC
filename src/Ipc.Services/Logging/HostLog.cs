using System.Text;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Logging;

public sealed record LogRetention(long MaximumFileBytes = 10 * 1024 * 1024, int MaximumFiles = 10,
    int HistoryDays = 30, int AuditDays = 90);

public sealed class HostLog
{
    private readonly string? _logDirectory;
    private readonly SqliteConnectionFactory _sqliteFactory;
    private readonly object _writeGate = new();
    private readonly LogRetention _retention;
    public string? LastMirrorError { get; private set; }

    public HostLog(SqliteConnectionFactory sqliteFactory, string? logDirectory = null, LogRetention? retention = null)
    {
        _sqliteFactory = sqliteFactory;
        _logDirectory = logDirectory;
        _retention = retention ?? new();
        if (_retention.MaximumFileBytes < 1024 || _retention.MaximumFiles < 1 || _retention.HistoryDays < 1 ||
            _retention.AuditDays < _retention.HistoryDays) throw new ArgumentOutOfRangeException(nameof(retention));
    }

    public void Info(string message) => Write("INFO", null, null, message);
    public void Error(string message) => Write("ERROR", null, null, message);
    public void Error(Exception ex, string message) => Write("ERROR", null, null, $"{message}: {ex}");
    public void History(IpcMessage message) => Write("HST", message.MessageId, OperationIdentity.Current?.Job?.ToString(), message.Text);
    public void JobMessage(string job, string messageId, string message) => Write("JOB", messageId, job, message);

    private void Write(string kind, string? messageId, string? job, string message)
    {
        var at = DateTimeOffset.UtcNow.ToString("O");
        var principal = OperationIdentity.Current?.Principal ?? "*SYSTEM";
        job ??= OperationIdentity.Current?.Job?.ToString();
        lock (_writeGate)
        {
            // SQLite is authoritative. Failure is visible to the caller, never silently lost.
            using (var connection = _sqliteFactory.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO sys_hst(at,job,message_id,message,kind,principal) VALUES($at,$job,$id,$message,$kind,$principal)";
                command.Parameters.AddWithValue("$at", at);
                command.Parameters.AddWithValue("$job", (object?)job ?? DBNull.Value);
                command.Parameters.AddWithValue("$id", (object?)messageId ?? DBNull.Value);
                command.Parameters.AddWithValue("$message", message);
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$principal", principal);
                command.ExecuteNonQuery();
            }
            if (_logDirectory is null) return;
            try
            {
                EnsureDirectory();
                var mirrored = message;
                byte[] line;
                do
                {
                    line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { at, kind, principal, job, messageId, message = mirrored }) + "\n");
                    if (line.Length <= _retention.MaximumFileBytes) break;
                    mirrored = mirrored[..(mirrored.Length / 2)];
                    if (mirrored.Length == 0) throw new IOException("Log metadata exceeds the mirror file size limit.");
                } while (true);
                var current = Path.Combine(_logDirectory, "ipcsys.log");
                if (File.Exists(current) && new FileInfo(current).Length + line.Length > _retention.MaximumFileBytes)
                    File.Move(current, Path.Combine(_logDirectory, $"ipcsys-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.log"));
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(current, options)) { stream.Write(line); stream.Flush(flushToDisk: true); }
                TrimMirrors(DateTimeOffset.UtcNow);
                LastMirrorError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The history row is durable; expose degraded mirroring without duplicating it on retry.
                LastMirrorError = ex.Message;
            }
        }
    }

    public void Maintain(DateTimeOffset now)
    {
        lock (_writeGate)
        {
            new DurableEventStore(_sqliteFactory).Retain(now, _retention.HistoryDays, _retention.AuditDays);
            if (_logDirectory is null || !Directory.Exists(_logDirectory)) return;
            try { TrimMirrors(now); LastMirrorError = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastMirrorError = ex.Message; }
        }
    }

    private void EnsureDirectory()
    {
        if (Directory.Exists(_logDirectory)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_logDirectory!);
        else Directory.CreateDirectory(_logDirectory!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void TrimMirrors(DateTimeOffset now)
    {
        var files = new DirectoryInfo(_logDirectory!).GetFiles("ipcsys*.log")
            .OrderByDescending(f => f.Name == "ipcsys.log").ThenByDescending(f => f.LastWriteTimeUtc).ToArray();
        for (var index = 0; index < files.Length; index++)
            if (index >= _retention.MaximumFiles || new DateTimeOffset(files[index].LastWriteTimeUtc) < now.AddDays(-_retention.HistoryDays))
                files[index].Delete();
    }

    public IReadOnlyList<(DateTimeOffset At, string? Job, string? MessageId, string Message)> Recent(int limit)
    {
        new Ipc.Services.Security.ServiceAuthorization(_sqliteFactory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.Audit);
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = _sqliteFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT at,job,message_id,message FROM sys_hst ORDER BY seq DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<(DateTimeOffset, string?, string?, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add((DateTimeOffset.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
        list.Reverse();
        return list;
    }
}
