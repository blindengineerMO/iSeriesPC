using System.Security.Cryptography;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Security;

internal sealed class CryptoKeyContexts(SqliteConnectionFactory factory) : IDisposable
{
    private readonly SecretProtector _protector = new(factory);
    internal byte[] Create(JobKey job, int type, ReadOnlySpan<byte> key, Action<byte[]>? prepare, CancellationToken cancellationToken)
    {
        new JobDataAreaStore(factory).RequireOwner(job); CryptographicService.ValidateKey(type, key.Length);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken);
        new JobDataAreaStore(factory).RequireOwner(job);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sys_crypto_keys WHERE job_number=$job"; command.Parameters.AddWithValue("$job", job.Number);
        if (Convert.ToInt32(command.ExecuteScalar()) >= 256) throw new CpfException("IPC0126", "Job cryptographic key-context limit reached (256).");
        byte[] token;
        do
        {
            cancellationToken.ThrowIfCancellationRequested(); token = RandomNumberGenerator.GetBytes(8);
            command.CommandText = "SELECT count(*) FROM sys_crypto_keys WHERE token=$token"; command.Parameters.Clear(); command.Parameters.AddWithValue("$token", token);
        } while (token.All(b => b == 0) || Convert.ToInt32(command.ExecuteScalar()) != 0);
        var envelope = _protector.Protect(Purpose(job.Number, token, type), key);
        prepare?.Invoke(token.ToArray()); cancellationToken.ThrowIfCancellationRequested();
        command.CommandText = "INSERT INTO sys_crypto_keys(token,job_number,key_type,key_value) VALUES($token,$job,$type,$value)";
        command.Parameters.AddWithValue("$job", job.Number); command.Parameters.AddWithValue("$type", type); command.Parameters.AddWithValue("$value", envelope);
        command.ExecuteNonQuery(); new JobDataAreaStore(factory).RequireOwner(job); cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return token;
    }
    internal byte[] Use(JobKey job, byte[] token, int type, Func<byte[], byte[]> operation)
    {
        Token(token); new JobDataAreaStore(factory).RequireOwner(job);
        int actual; string envelope;
        using (var connection = factory.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key_type,key_value FROM sys_crypto_keys WHERE token=$token AND job_number=$job";
            command.Parameters.AddWithValue("$token", token); command.Parameters.AddWithValue("$job", job.Number);
            using var reader = command.ExecuteReader(); if (!reader.Read()) throw Missing(); actual = reader.GetInt32(0); envelope = reader.GetString(1);
        }
        if (actual != type) throw new CpfException("CPF9DF7", "Key context is not compatible with the requested algorithm.");
        byte[] key;
        try { key = _protector.Unprotect(Purpose(job.Number, token, actual), envelope); }
        catch (CryptographicException) { throw new CpfException("CPF9DC3", "Unable to decrypt cryptographic key context."); }
        byte[]? result = null;
        try { result = operation(key); new JobDataAreaStore(factory).RequireOwner(job); return result; }
        catch { if (result is not null) CryptographicOperations.ZeroMemory(result); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    internal void Destroy(JobKey job, byte[] token, CancellationToken cancellationToken)
    {
        Token(token); new JobDataAreaStore(factory).RequireOwner(job); cancellationToken.ThrowIfCancellationRequested();
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken);
        new JobDataAreaStore(factory).RequireOwner(job); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM sys_crypto_keys WHERE token=$token AND job_number=$job";
        command.Parameters.AddWithValue("$token", token); command.Parameters.AddWithValue("$job", job.Number);
        if (command.ExecuteNonQuery() != 1) throw Missing(); cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }
    internal void Rewrap(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT token,job_number,key_type,key_value FROM sys_crypto_keys";
        var rows = new List<(byte[] Token, int Job, int Type, string Envelope)>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) rows.Add(((byte[])reader[0], reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3)));
        foreach (var row in rows)
        {
            var purpose = Purpose(row.Job, row.Token, row.Type); var key = _protector.Unprotect(purpose, row.Envelope); string envelope;
            try { envelope = _protector.Protect(purpose, key); } finally { CryptographicOperations.ZeroMemory(key); }
            command.CommandText = "UPDATE sys_crypto_keys SET key_value=$value WHERE token=$token"; command.Parameters.Clear();
            command.Parameters.AddWithValue("$value", envelope); command.Parameters.AddWithValue("$token", row.Token); command.ExecuteNonQuery();
        }
    }
    private static string Purpose(int job, byte[] token, int type) => "crypto-key/" + job + "/" + Convert.ToHexString(token) + "/" + type;
    private static void Token(byte[] token) { if (token.Length != 8 || token.All(b => b == 0)) throw new CpfException("CPF9DF4", "Invalid key context token."); }
    private static CpfException Missing() => new("CPF9DF5", "Key context was not found for the executing job or was destroyed.");
    private static SqliteTransaction BeginWrite(SqliteConnection connection, CancellationToken cancellationToken)
    {
        connection.DefaultTimeout = 1; var began = global::System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return connection.BeginTransaction(deferred: false); }
            catch (SqliteException failure) when (failure.SqliteErrorCode is 5 or 6 && global::System.Diagnostics.Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(30))
            { cancellationToken.WaitHandle.WaitOne(25); }
        }
    }
    public void Dispose() => _protector.Dispose();
}
