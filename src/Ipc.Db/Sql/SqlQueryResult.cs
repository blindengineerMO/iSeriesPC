using System.Globalization;
using Ipc.Core.Messages;

namespace Ipc.Db.Sql;

public enum SqlQueryKind { Null, SmallInt, Integer, BigInt, Decimal, Real, Double, Character, VarChar, Date, Time, Timestamp, Boolean, Numeric }
public sealed record SqlQueryType(SqlQueryKind Kind, int Precision = 0, int Scale = 0, int Length = 0, int Ccsid = 37, bool Nullable = true);
public sealed record SqlQueryColumn(string Name, SqlQueryType Type)
{
    public bool Calculated { get; init; }
}
public sealed record SqlQueryParameter(object? Value, SqlQueryType? Type = null);
public sealed record SqlQueryLimits(int MaximumRows = 1000000, long MaximumCells = 1000000, long MaximumBytes = 67108864, long MaximumEvaluations = 16000000)
{
    internal void Validate()
    {
        if (MaximumRows is < 0 or > 1000000 || MaximumCells is < 0 or > 1000000 || MaximumBytes is < 0 or > 67108864 || MaximumEvaluations is < 1 or > 16000000)
            throw new CpfException("SQL0904", "Invalid query resource limits.");
    }
}
// SQL decimal results can have 63 digits. Preserve their scale without converting
// them through System.Decimal or a binary floating-point value.
public sealed record SqlDecimalValue(string Text, int Precision, int Scale)
{
    public override string ToString() => Text;
    public decimal ToDecimal()
    {
        var parsed = decimal.Parse(Text, CultureInfo.InvariantCulture);
        if (SqlExactNumber.Parse(parsed.ToString(CultureInfo.InvariantCulture)).CompareTo(SqlExactNumber.Parse(Text)) != 0)
            throw new OverflowException("SQL decimal is not exactly representable as System.Decimal.");
        return parsed;
    }
}

/// <summary>A forward-only query with one current result row and a read transaction.
/// Dispose to release its transaction, functions and object allocations.</summary>
public sealed class SqlQueryCursor : IDisposable
{
    private readonly Func<IReadOnlyList<object?>?> _read;
    private readonly Action _dispose;
    private readonly Func<IReadOnlyList<SqlQueryDiagnostic>>? _diagnostics;
    private bool _closed;
    private readonly object _gate;
    private readonly Action? _checkCancellation;
    private readonly Action? _validateAccess;
    internal SqlQueryCursor(IReadOnlyList<SqlQueryColumn> columns, Func<IReadOnlyList<object?>?> read, Action dispose, Func<IReadOnlyList<SqlQueryDiagnostic>>? diagnostics = null, object? gate = null, Action? checkCancellation = null, Action? validateAccess = null)
    { Columns = columns; _read = read; _dispose = dispose; _diagnostics = diagnostics; _gate = gate ?? new(); _checkCancellation = checkCancellation; _validateAccess = validateAccess; }
    public IReadOnlyList<SqlQueryColumn> Columns { get; }
    public IReadOnlyList<SqlQueryDiagnostic> Diagnostics { get { lock (_gate) return _diagnostics?.Invoke().ToArray() ?? []; } }
    public IReadOnlyList<object?>? Current { get; private set; }
    // Also validates already-fetched rows retained by a result viewer after EOF.
    public void CheckAccess()
    {
        lock (_gate)
        {
            try { _checkCancellation?.Invoke(); _validateAccess?.Invoke(); }
            catch { Dispose(); throw; }
        }
    }
    public bool Read()
    {
        lock (_gate)
        {
            try
            {
                _checkCancellation?.Invoke();
                if (_closed) return false;
                Current = _read(); if (Current is null) { Dispose(); return false; } return true;
            }
            catch { Dispose(); throw; }
        }
    }
    public void Dispose() { lock (_gate) { if (_closed) return; _closed = true; Current = null; _dispose(); } }
}
