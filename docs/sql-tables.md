# SQL tables and RUNSQL

`RUNSQL` now executes `CREATE TABLE`, `DROP TABLE`, and literal/default multirow
`INSERT … VALUES` through the shared physical-file store.
[Searched UPDATE and DELETE](sql-mutations.md) now add typed expressions and
atomic selected-record changes through the same path. SQL-created files are
ordinary catalog `*FILE` objects with a record format, first member, durable RRN
slots, authorities and journal hooks. CL DCLF/RCVF and RPG DISK use the same rows.
An independent SQLite-only table is never substituted for a native file object.
The shared [typed SELECT service](sql-queries.md) queries these same files and
logical projections. [STRSQL](sql-interactive.md) provides interactive entry;
[saved queries](query-management.md) and [typed output files](sql-query-output.md)
use the same engine. [SQL indexes](sql-indexes.md) add maintained keyed logical
objects and cascading SQL table deletion.
[SQL views](sql-views.md) now add stored typed query definitions and nonkeyed
record input, plus single-source view INSERT/UPDATE/DELETE and LOCAL/CASCADED
CHECK OPTION. [ALTER TABLE](sql-alter-tables.md) now changes columns, defaults,
nullability and primary/unique constraints with atomic member/dependency updates.
[General CHECK constraints](sql-check-constraints.md) now enforce shared typed
predicates through every record connection.

```cl
RUNSQL SQL('CREATE TABLE QGPL/ACCOUNTS (ID INTEGER NOT NULL PRIMARY KEY, NAME VARCHAR(20), AMOUNT DECIMAL(12,2) DEFAULT 0) RCDFMT ACCOUNT') COMMIT(*NONE)
RUNSQL SQL('INSERT INTO QGPL/ACCOUNTS(ID,NAME) VALUES(7,''Ada''),(8,''Grace'')') COMMIT(*NONE)
```

The native command's default `COMMIT(*CHG)` remains unavailable until native
commitment control is implemented. The supported modes are explicit `*NONE` and
its synonym `*NC`; they reject already-enlisted record resources. No implicit
commit or rollback of a job's existing private record unit occurs. The statement
limit is 5,000 encoded bytes in the executing job's CCSID, including comments.
Both EBCDIC 37 and UTF-8 1208 are tested at the exact boundary.
[RUNSQLSTM](sql-scripts.md) now processes source scripts through this same engine. Prompt/command
metadata exposes that limit without increasing unrelated custom-command limits.
See IBM's [RUNSQL reference](https://www.ibm.com/docs/api/v1/content/ssw_ibm_i_74/cl/runsql.html?lang=en&parsebody=true).

`NAMING(*SYS)` uses library/file qualification. Unqualified creation uses the
current library; unqualified input/output searches the job library list.
`NAMING(*SQL)` uses schema.table qualification and the profile's schema as its
default. `DFTRDBCOL` supplies an explicit default collection. QTEMP resolves to the
executing job's private catalog namespace. Quoted uppercase system identifiers
are accepted; longer SQL names and mixed-case delimited names require the still
unfinished system-name mapping and reject explicitly.

SQL naming sets public authority to *EXCLUDE. A matching schema user profile
owns the new table; a different creator needs *ADD authority to that profile.
Otherwise the executing profile owns it. System naming retains the executing
profile as owner and uses the library's CRTAUT level or authorization list. These
choices and list attachment commit with the table, without a public-access
window. See IBM's [table ownership and authority rules](https://www.ibm.com/docs/en/i/7.5.0?topic=statements-create-table).
Profile GRPOWN/GRPAUT options remain outside the implemented profile subset.

The table-definition parser supports SMALLINT/INTEGER/BIGINT, DECIMAL/DEC,
NUMERIC, CHAR/CHARACTER, VARCHAR/CHARACTER VARYING, REAL/DOUBLE PRECISION,
DATE/TIME and TIMESTAMP(6). DECIMAL uses packed external storage and NUMERIC uses
zoned storage. Decimal precision is bounded to 29 digits and scale to 28, with
values additionally constrained to the exact existing decimal codec range.
Character widths are bounded to 32,740 bytes; the external field layout to
32,766 bytes. Larger native types, LOBs, graphic/binary types and other timestamp
precisions remain open. Character fields use the job CCSID unless overridden by
a supported column CCSID. SQL tables default to REUSEDLT(*YES), SIZE(*NOMAX) and
one member. See IBM's [database implementation discussion](https://www.redbooks.ibm.com/redbooks/pdfs/sg244249.pdf)
for the SQL-versus-CRTPF allocation defaults.

Columns support nullable/NOT NULL metadata, literal/NULL defaults, current date,
time and timestamp defaults, and bare WITH DEFAULT. A bare default on a nullable
column is NULL; nonnullable columns use type defaults. Omitted nonnullable SQL
columns without a usable default fail. Current datetime values use the existing UTC record-default clock and are fixed
once per statement, including across an operator-capacity retry. Native SQL
job-time-zone and DATFMT/TIMFMT options remain open.

Primary and unique constraints may be inline or table-level, including compound
keys and optional names. They use maintained exact decimal/encoded-text indexes;
SQL primary keys do not turn the file into a keyed RPG file or alias the hidden
RRN. The primary-key null check preserves declared external null metadata.
Unique constraints allow repeated null values and compare character keys with
the existing encoded padding rules. These follow IBM's
[constraint definitions](https://www.ibm.com/docs/en/i/7.4.0?topic=statements-alter-table).
Named constraints are unique within a schema. Copying reconciles conflicting
names with locally generated ten-character names; moving into a conflicting
schema rejects atomically. Exact IBM generated-name and relocation-message parity
remains unverified. ALTER/DROP CONSTRAINT remains open; SQL-created index objects
are described in [SQL indexes](sql-indexes.md).

Creation publishes descriptor, definition, member, constraints and RRN identity
in one transaction. Concurrent creators cannot publish partial duplicate objects.
DROP uses shared object authority, allocations, dependencies and journal checks.
Authenticated HTTP command requests create, insert and drop through distinct
server jobs with the same ownership and diagnostics. Copy/move preserve SQL
metadata and actual constraints. Restart retains definitions,
defaults, rows and indexes. The optional SQL definition metadata does not change
the catalog schema or existing file/RPG/record metadata versions.

INSERT binds column lists and values before publication. It validates types,
CCSIDs, precision, widths, explicit NULLs and defaults. The entire VALUES list is
atomic, including extents, incarnation tokens and journal capture. Existing
member-full policy can unwind the attempt, wait for CPA5305 without holding the
writer, grant capacity and retry using the same bound values. No prefix rows or
journal entries escape a failed attempt. Staging is bounded to one million
normalized cells and 64 MiB. Cancellation and shutdown roll back publication;
writer acquisition is cancellable. Live job/authority and member/format identity
are rechecked across waits. SQL errors report the logical target and omit private
backing names.

The parser never forwards input SQL to SQLite. It recognizes SQL quoting,
comments and a single optional final semicolon; unsupported syntax and additional
statements fail before effects. Integer/decimal literals retain text until typed
validation, and floating literals support exponent notation. SQL0104/0204/0206,
SQL0117, SQL0404/0406/0407, SQL0551, SQL0601, SQL0803 and SQL0545 cover the bounded
implemented diagnostics; full SQLSTATE/SQLCA, native replacement data and shipped
message-catalog fidelity remain open.

Wider CREATE VIEW/INDEX and ALTER TABLE options, writes through DDS LF/source-file
views, ALTER VIEW, foreign keys, wider CHECK actions, identities,
CTAS/INSERT SELECT, MERGE, wider expressions/functions, wider query/result-set
interfaces, native commitment/isolation, wider RUNSQLSTM/STRSQL/QMQRY options, WRKQRY and
independent IBM golden/scale acceptance remain open. Unsupported options are
reported, rather than silently accepted. C07/C08 and the broader SQL requirements
remain partial. The existing trusted record-unit SQLite API is separate from this
new SQL parser and does not establish DB2 compatibility.

Verification (2026-10-19): all 6898 solution tests pass with zero failures/skips
in 11m17s, including 66 new SQL cases. All 250 focused cases and 4354 broad
RPG/binding/work cases pass. Locked restore, a Release build with zero warnings/errors,
24x80/27x132 terminal acceptance, frozen-source checks, generated inventory, local
links and whitespace checks pass.
