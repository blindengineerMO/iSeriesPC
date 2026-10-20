# Typed SQL queries

`SqliteFileStore.OpenSqlQuery` provides a bounded, forward-only SELECT service for
local SQL consumers and the planned reporting gateway. It resolves ordinary
catalog files and uses the same physical members, logical-file definitions,
CCSIDs, authorities and pending record data as file I/O. The caller supplies an
execution identity, naming options, typed parameters and cancellation token.

```csharp
var options = new SqlExecutionOptions(
    SqlNaming.Sql, "QGPL", "QSECOFR", ["QGPL"]);
using var cursor = files.OpenSqlQuery(
    "SELECT ID,NAME FROM QGPL.DATA WHERE ID=? ORDER BY ID",
    options, [new SqlQueryParameter(7)]);
while (cursor.Read())
{
    var values = cursor.Current!;
    // Columns describes the result; values contains one current row.
}
```

The [STRSQL interactive front end](sql-interactive.md) now uses this service.
[Saved QMQRY objects](query-management.md) also use its typed query cursors,
with native SETVAR substitution before AST binding.
[Typed output files](sql-query-output.md) spool these cursors before saving to
physical members, including compatible replace/append and private pending source data.
QMQRY/WRKQRY, embedded SQL, IWS SQL deployment and the PostgreSQL-protocol
ODBC/JDBC gateway still need their entry points and acceptance. RUNSQL and RUNSQLSTM execute statements without
result sets; both reject SELECT. RUNSQLSTM also rejects it in syntax-only mode.
The required [SQL/reporting contract](compatibility-contracts.md#sql-and-reporting)
remains partial.

The supported SELECT grammar includes:

- Column/expression projections, aliases, `*` and qualified wildcards; ALL and
  DISTINCT; mandatory FROM; first-member file resolution using SQL/system naming.
- Comma/CROSS, INNER, LEFT, RIGHT and FULL joins, including parenthesized joins.
  ON cannot refer to a later table. Ambiguous fields and duplicate qualifiers fail.
- Comparisons, arithmetic, concatenation, LIKE/NOT LIKE with ESCAPE, IS NULL,
  IN/BETWEEN, NOT/AND/OR and three-valued NULL predicates.
- GROUP BY expressions, HAVING, COUNT/COUNT_BIG, SUM/AVG/MIN/MAX, ALL/DISTINCT
  aggregate arguments and scalar expressions around aggregates. Ungrouped columns,
  nested aggregates and aggregates in WHERE/ON/GROUP BY reject before reading.
- ORDER BY expressions, result aliases or ordinals, ASC/DESC and NULLS FIRST/LAST;
  FETCH FIRST with an integer or parameter. Default NULL order is high: last in
  ascending order, first in descending order.

Physical and source-file reads use the first member. Single-format logical files
expand their existing projection, physical-member bindings and select/omit rules;
the DDS expansion creates no separate answer table. Stored [SQL views](sql-views.md)
now bind nested queries separately in the catalog. Logical queries require
source-file read authority. QTEMP uses the executing job's private library and
closes its query snapshots before job cleanup.

The typed expression binder is shared with [searched mutations](sql-mutations.md).
It implements COALESCE/NULLIF, ABS/ROUND, LENGTH/OCTET_LENGTH,
CHAR_LENGTH/CHARACTER_LENGTH, SUBSTR/SUBSTRING, TRIM/LTRIM/RTRIM,
UPPER/LOWER and aliases, CAST, DATE/TIME/TIMESTAMP, current datetime values,
EXTRACT and YEAR/MONTH/DAY/HOUR/MINUTE/SECOND/QUARTER. Datetime arithmetic supports
compatible labeled year/month/day/hour/minute/second durations.

SUBSTR counts bytes and pads an explicit length; SUBSTRING counts Unicode
characters and does not pad. The current text profile requires positive start
positions, rejects incomplete multibyte SUBSTR output and bounds dynamic SUBSTR
lengths by the declared result width. Fixed SUBSTR result metadata distinguishes
constant lengths and fixed source/start combinations. Additional native
substring positions, binary/graphic strings and conversions remain open.
LENGTH counts encoded bytes, including fixed-field padding, or the supported
native numeric/datetime storage width. Character length counts Unicode scalar
values. Character comparisons/grouping/sorting use encoded, space-padded CCSID
keys; mixed column CCSIDs reject explicitly.

Exact numeric operations use scaled arbitrary-precision integers. Decimal query
expressions and results can have 63 digits; `SqlDecimalValue` preserves precision,
scale and text without passing through SQLite REAL or System.Decimal.
`ToDecimal()` checks exact representability. Stored file decimals retain the
existing codec limits. COUNT is 32-bit, COUNT_BIG is 64-bit, and integer SUM/AVG
use the documented promoted/result widths. Decimal aggregate precision/scale and
intermediate overflow checks follow the default IBM rules; integer averages
discard fractions. ROUND uses halfway-away-from-zero rounding.

SECOND(timestamp) returns whole seconds. Its optional constant precision is
currently 0–6. EXTRACT(SECOND) retains timestamp precision and returns six
fractional digits for TIME. Parameters support timestamps at precision 0 or 6;
stored second-only timestamps can also be extracted and used in labeled arithmetic.
Host UTC supplies current datetime values; job time-zone semantics remain open.

These rules were checked against IBM's [IBM i 7.4 SQL reference](https://www.ibm.com/docs/en/ssw_ibm_i_74/pdf/rbafzpdf.pdf),
especially AVG/COUNT/SUM, LENGTH, EXTRACT/SECOND, SUBSTR/SUBSTRING and ROUND.
They have local regression evidence; independent IBM execution fixtures remain
required for compatibility acceptance.

`SqlQueryType` describes result kinds, precision/scale, byte width, CCSID and
nullability, including outer-join columns. Positional markers bind values without
SQL substitution. Explicit descriptors support typed NULLs and validate ranges,
widths, encodings and precision even when a predicate produces no rows. Decimal
parameter text and large integers are bounded before expensive conversion.
Caller parameter arrays are copied and values bind once per compiler.

Query exceptions expose SQLSTATE and SQLCODE alongside the existing message ID.
Unsupported grammar is 42601; missing/ambiguous objects and columns, bad markers,
authority, overflow, division by zero and resource failures have reason-specific
states. Character CAST truncation produces SQL0445/01004 warnings, retained in
`cursor.Diagnostics` after close, with bounded distinct entries and occurrence
counts. The mapping uses IBM's [SQL messages and codes](https://www.ibm.com/docs/en/ssw_ibm_i_74/pdf/rzalapdf.pdf).
Full native message replacement data, SQLCA/diagnostic areas and warning/conversion
parity remain open. The SQLSTATE facade currently applies to this query service;
legacy command/mutation errors still need complete propagation.

A cursor owns one SQLite read transaction, per-query typed functions and its
object allocations. It retains only the current output row. Fetch checks the
original principal/job/session, live credentials, job state and file/source read
authority. Cross-thread sequential fetch/dispose is supported with the same
execution identity. Another principal or job cannot fetch the cursor.
EOF, evaluation failure and disposal release resources. Cancellation is checked
during SQLite work and on fetch; an idle caller should still dispose its cursor.
Job completion/requested end and factory shutdown cancel and close readers before
QTEMP cleanup or private record-workspace deletion. Factories attached to the same
catalog coordinate job resources. Query functions are removed before connections
return to the pool.

Connection opening and first memory-database initialization perform SQLite setup
outside the factory lifecycle lock. Shutdown can therefore signal cancellation
while another open waits for a schema writer; disposal joins in-flight opens
before closing the keepalive connection and pool. An owned execution session
cancels its captured job's query resources before catalog authorization/cleanup
can wait behind those queries. Catalog changes still perform their normal
authorization checks. Schema-writer regression cases cover fresh and initialized
factories and owned-session cleanup.

An enlisted query imports only its referenced members and pending intents through
the existing record-unit preparation path. It then anchors an independent,
read-only WAL snapshot of that private workspace before releasing the synchronous
unit gate. Fetches can move between threads while later record statements run;
they retain the original values. No query publishes or rolls back the job's
pending changes. Commit, rollback and definition end require these query cursors
closed. This explicit local policy does not implement native hold cursors,
commitment-open flags or isolation levels. A referenced member is still loaded
in full; the existing workspace scale limitations remain.

Default limits are one million output rows, one million cells, 64 MiB of output
values and sixteen million typed evaluation steps. Group/distinct managed state
and parameters also have cell/byte bounds. Callers can lower limits. Queries allow
32 sources, 1000 output columns, 128 group/order terms, 128 expression levels,
8192 expression nodes and a bounded set of generated functions. A job can hold
128 query cursors; disposal makes capacity reusable. These are implementation
bounds, not independently measured native memory/latency targets. SQLite sorting,
WAL retention, metadata growth and large-file acceptance still need scale budgets.

Input SQL is parsed and bound before trusted SQL is generated. Internal catalog
tables, PRAGMA, ATTACH, extensions, additional statements and unsupported syntax
cannot be forwarded to SQLite. Subqueries/CTEs/set operations, CASE/windows,
SYSIBM virtual catalogs, long/mixed-case SQL-name mappings, wider functions and
conversions, wider SQL view/index options and trigger/FK DDL, INSERT SELECT/MERGE, remaining query
interfaces, native commitment/isolation and independent golden/performance
acceptance remain unfinished. Main schema 35 and coordination format 2 are
unchanged; C07/C08 and the wider release requirements remain partial.

Verification (2026-10-19): all 7116 solution tests pass with zero failures/skips
in 8m19s, including 115 query cases and two new script SELECT checks. All
514 focused checks and 4354 broad RPG/binding/work cases pass. Locked restore,
a zero-warning/error Release build, existing HTTP and compiled CL/RPG terminal
acceptance at both screen sizes, 969 frozen-source checks, generated inventory,
local links and whitespace checks pass. Full acceptance uses four xUnit workers.
