# RUNSQLSTM source scripts

`RUNSQLSTM` now processes source members, ordinary physical-file text records and
bounded UTF-8 host stream files through the shared [SQL table](sql-tables.md) and
[mutation](sql-mutations.md) engine. Native commitment control and printed source
listings remain unfinished. Execution therefore requires explicit
`COMMIT(*NONE)` or `*NC` and `OPTION(*NOLIST)`; their native defaults (`*CHG` and
`*LIST`) reject before effects. No printing or commitment option is silently
accepted.

```cl
RUNSQLSTM SRCFILE(QGPL/QSQLSRC) SRCMBR(SETUP) COMMIT(*NONE) OPTION(*NOLIST)
RUNSQLSTM SRCSTMF('/home/admin/setup.sql') COMMIT(*NC) OPTION(*NOLIST)
RUNSQLSTM SRCFILE(QGPL/QSQLSRC) SRCMBR(SETUP) PROCESS(*SYN) OPTION(*NOLIST)
```

For example, the SETUP member can contain:

```sql
CREATE TABLE QGPL/ACCOUNTS(ID INTEGER PRIMARY KEY, NAME CHAR(12));
INSERT INTO QGPL/ACCOUNTS VALUES(1,'first'),(2,'second');
UPDATE QGPL/ACCOUNTS SET ID=ID+10 WHERE ID=1;
DELETE FROM QGPL/ACCOUNTS WHERE ID=2;
```

Each statement must end with a semicolon. Delimiters inside quoted literals or
identifiers do not end statements. Escaped quotes, line comments and nested block
comments are recognized. Member records default to right margin 80; MARGINS can
set 1–32754. Source sequence/date fields are excluded. Deleted records are skipped
in arrival order. Source and ordinary physical-file CCSIDs are honored, and an
invalid UTF-8 boundary at a margin rejects instead of substituting characters.
Multi-format/logical source files and mixed-CCSID raw record formats reject.
Native inline data files and all native source-format conversions remain open.
See IBM's [SQL statement processor](https://www.ibm.com/docs/en/i/7.5.0?topic=environments-using-sql-statement-processor)
and [RUNSQLSTM command reference](https://www.ibm.com/docs/api/v1/content/ssw_ibm_i_74/cl/runsqlstm.html?lang=en&parsebody=true).

A source member is read from a consistent snapshot before execution. Later
changes to that member, including an explicit CL CLRPFM in the script, cannot
change the statements being processed. Source is bounded to 1,048,576 decoded
characters, 10,000 records/line endings and 10,000 statements. Stream input uses
the existing compilation-source resolver: a regular strict UTF-8 file of at most
1 MiB, with an optional BOM removed, and existing unadopted *SERVICE authority.
Relative host paths use the service's working directory; job IFS mapping and
non-UTF-8 stream attributes remain open. Member margins do not truncate streams.
An individual SQL statement may exceed RUNSQL's 5,000-byte command limit, within
the script and SQL normalization bounds.

`NAMING(*SYS/*SQL)` and `DFTRDBCOL` use the executing job's library environment.
Mixed CL commands begin with `CL:` and also end with a semicolon. Noninteractive
commands use the same command binding, authority and recursion checks as direct
execution. Library changes affect subsequent statements. Interactive command
results reject. SQL and CL run as the current job; source and target permissions
are checked independently. This is not a new authority boundary or an adopted
owner profile.

Under the supported no-commit modes, completed statements remain published when
a later statement fails. Each statement releases its automatic allocations at
its boundary, so a waiting later statement does not retain the earlier rows
or exhaust the job holder budget across the entire script. Each SQL statement retains its own current storage
semantics; the earlier atomic INSERT VALUES profile is stronger than all native
COMMIT(*NONE) partial-failure behavior and remains explicitly unfinished.
Cancellation propagates through source loading, statement boundaries and SQL
waits. A cancelled capacity inquiry preserves completed statements and unwinds
the current statement, inquiry registration and automatic allocations. Existing
private record units are neither committed nor silently bypassed.

ERRLVL defaults to 10 and accepts 0–40. A diagnostic whose reported severity is
greater than ERRLVL stops execution. The remaining statements are syntax-checked.
Diagnostics within the threshold permit further statements and a normal command
result. Most current SQL failures have local severity 30; full shipped-message
severities and replacement data remain unverified. OPTION(*NOLIST) writes
statement completion and located diagnostics to the job log. A failed processor
returns SQL9010, allowing a compiled CL MONMSG to handle it while the underlying
SQL/CL diagnostics remain in the job log. Source locations identify the source,
statement line and column. Native second-level text and print/spool options
remain open and reject when requested.

PROCESS(*SYN) parses SQL and binds CL command syntax without running statements;
its commitment parameter is validated but does not start a transaction. SQL
catalog-dependent semantic preparation is still open. SELECT rejects in both
execution and syntax-only modes; result sets belong to the shared
[query service](sql-queries.md). Unsupported procedural
sources (routines, triggers and compound/control blocks, including labels) reject
the entire source before effects. Their internal semicolons must never expose
body fragments as ordinary DML, even at ERRLVL(40). Full procedural statement
segmentation belongs with the missing routine/trigger grammar.

A script regression also corrected final UPDATE publication validation: after
transactional unique-index changes, credentials and live job state are checked
on the publisher's existing connection. Opening a second connection could block
on its own schema lock in an in-memory catalog. The same-connection path retains
profile/session expiry checks and rolls back job cancellation, job-end and
profile failures with the data and indexes.

Broader SQL grammar, native commitment/isolation and activation-group binding,
printed listings, catalog preparation in syntax-only mode, SQLSTATE/SQLCA ABI,
complete source/CCSID/diagnostic parity and independent IBM golden/scale
acceptance remain open. C07/C08 remain partial.

Verification (2026-10-19): all 6999 solution tests pass with zero failures/skips
in 8m31s, including 50 added cases. All 397 focused checks and 4354 broad
RPG/binding/work cases pass. Locked restore, zero-warning/error Release build,
HTTP and compiled CL/RPG acceptance at both terminal sizes, frozen-source checks,
generated inventory, local links and whitespace checks pass.
