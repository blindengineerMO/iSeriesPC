using System.Text;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Session.Sql;

internal sealed record InteractiveSqlPage(IReadOnlyList<string[]> Rows, long First, bool More, IReadOnlyList<SqlQueryColumn> Columns, IReadOnlyList<SqlQueryDiagnostic> Diagnostics);

// A disk-backed scroll cache. It never keeps all fetched rows in managed memory.
internal sealed class InteractiveSqlResults : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop;
    private readonly CancellationToken _token;
    private readonly string _directory = Path.Combine(Path.GetTempPath(),"ipc-sql-results-"+Guid.NewGuid().ToString("N"));
    private SqlQueryCursor? _cursor;
    private SqliteConnection? _cache;
    private IDisposable? _registration;
    private long _count, _bytes, _shown;
    private readonly Func<SqlQueryCursor> _reopen;
    private readonly bool _forwardLive;
    private bool _end, _disposed;
    private readonly InteractiveSqlFormatter _formatter;
    internal InteractiveSqlResults(SqliteConnectionFactory factory, SqliteFileStore files, string sql, SqlExecutionOptions options,
        InteractiveSqlFormatter formatter, CancellationToken cancellationToken, bool refreshForward=false)
    {
        var parsed=SqlStatementParser.Parse(sql,options) as SqlSelect;
        _formatter=formatter; _stop=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,factory.ShutdownToken);_token=_stop.Token;
        _reopen=()=>files.OpenSqlQuery(sql,options,cancellationToken:_token);
        _forwardLive=refreshForward && parsed is { Distinct:false,From:SqlTableSource } && parsed.OrderBy.Count==0 && parsed.GroupBy.Count==0;
        lock(_gate) try
        {
            var job=OperationIdentity.Current?.Job ?? throw new CpfException("SQL6141","Interactive SQL requires a terminal job.");
            _registration=factory.RegisterJobRuntimeResource(job,this); _token.ThrowIfCancellationRequested();
            if(OperatingSystem.IsLinux()) Directory.CreateDirectory(_directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute); else Directory.CreateDirectory(_directory);
            _cache=new(new SqliteConnectionStringBuilder { DataSource=Path.Combine(_directory,"rows.db"),Pooling=false }.ToString()); _cache.Open();
            using var command=_cache.CreateCommand(); command.CommandText="CREATE TABLE rows(id INTEGER PRIMARY KEY,data TEXT NOT NULL)"; command.ExecuteNonQuery();
            _cursor=_reopen();
        }
        catch { Dispose(); throw; }
    }
    internal string DirectoryPath=>_directory;
    internal void CheckAccess() { lock(_gate) { _token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed,this); _cursor!.CheckAccess(); } }
    internal InteractiveSqlPage Read(long first,int maximum)
    {
        lock(_gate)
        {
            CheckAccess();
            if(first is < 0 or >= 1000000 || maximum is < 1 or > 100) throw new CpfException("SQL0904","Invalid result position.");
            if(_forwardLive && !_end && _shown>0 && first+maximum>_shown)
            {
                using var trim=_cache!.CreateCommand();trim.CommandText="DELETE FROM rows WHERE id>$shown";trim.Parameters.AddWithValue("$shown",_shown);trim.ExecuteNonQuery();_count=_shown;
                _cursor!.Dispose();_cursor=_reopen();
                for(var skipped=0L;skipped<_count;skipped++)if(!_cursor.Read()) { _end=true;break; }
            }
            var target=first+maximum+1;
            using(var transaction=_cache!.BeginTransaction())
            {
                using var append=_cache.CreateCommand(); append.Transaction=transaction; append.CommandText="INSERT INTO rows(id,data) VALUES($id,$data)";
                var id=append.Parameters.Add("$id",SqliteType.Integer); var data=append.Parameters.Add("$data",SqliteType.Text);
                while(!_end && _count<target)
                {
                    _token.ThrowIfCancellationRequested();
                    if(!_cursor!.Read()) { _end=true; break; }
                    var values=_cursor.Current!.Select((value,index)=>_formatter.Value(value,_cursor.Columns[index].Type)).ToArray();
                    var json=JsonSerializer.Serialize(values); _bytes+=Encoding.UTF8.GetByteCount(json);
                    if(_bytes>67108864)throw new CpfException("SQL0904","Interactive SQL scroll cache exceeds 64 MiB.");
                    id.Value=++_count;data.Value=json;append.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            if(_end && first>=_count)first=Math.Max(0,_count-maximum);
            using var read=_cache.CreateCommand(); read.CommandText="SELECT data FROM rows WHERE id>$first ORDER BY id LIMIT $maximum";read.Parameters.AddWithValue("$first",first);read.Parameters.AddWithValue("$maximum",maximum);
            using var reader=read.ExecuteReader();var rows=new List<string[]>();
            while(reader.Read())rows.Add(JsonSerializer.Deserialize<string[]>(reader.GetString(0))!);
            _shown=Math.Max(_shown,first+rows.Count);
            return new(rows,first,!_end || first+rows.Count<_count,_cursor!.Columns,_cursor.Diagnostics);
        }
    }
    public void Dispose()
    {
        try { _stop.Cancel(); } catch(ObjectDisposedException) { }
        lock(_gate)
        {
            if(_disposed)return;_disposed=true;
            try { _cursor?.Dispose(); }
            finally
            {
                try { _cache?.Dispose(); }
                finally
                {
                    try { if(Directory.Exists(_directory))Directory.Delete(_directory,true); }
                    finally { _registration?.Dispose();_stop.Dispose(); }
                }
            }
        }
    }
}
