# SQL query output files

`STRQMQRY OUTPUT(*OUTFILE)` saves a typed SELECT result as a physical data file.
It works in interactive, batch and authenticated command-only/HTTP jobs without
a terminal. Saved SQL, SETVAR and errors retain their
[query-management contract](query-management.md).

```cl
STRQMQRY QGPL/REPORT OUTPUT(*OUTFILE) OUTFILE(QTEMP/RESULT) SETVAR((ID 7))
STRQMQRY QGPL/REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/RESULT) OUTMBR(*FIRST *ADD)
```

The native command/default reference is IBM's
[STRQMQRY](https://www.ibm.com/docs/api/v1/content/ssw_ibm_i_74/cl/strqmqry.html?lang=en&parsebody=true).
The source for layout compatibility, naming, CCSID conversion, authority and
failure boundaries is IBM's
[Query Management Programming](https://public.dhe.ibm.com/systems/power/docs/systemi/v6r1/en_US/sc415703.pdf),
particularly SAVE DATA AS and its column-heading, CCSID and override sections.
Independent current-release native golden acceptance remains open.

## Saving a result

The shared `SqliteFileStore.WriteSqlQueryFile` service first spools the completed
typed query snapshot to a private disk file. Only one row is held in managed
memory. Query quotas remain one million rows/cells, 64 MiB of values and the
configured evaluation budget. The spool also has a 128 MiB SQLite page limit
and a 2 MiB page cache; its directory is private on Linux. Evaluation or quota
failure happens before changing the output. Spooling makes self-replace and
self-append finite and preserves the query snapshot while saving.

An absent output file is created with the executing profile as owner and
`*PUBLIC *EXCLUDE`, regardless of library CRTAUT. Its member, SQL/physical field
definitions, record slots and indexes are one catalog publication. The first
member has the file's name. It is a SQL-compatible PF with unlimited local
member size and reused deleted records, and does not inherit source keys,
constraints, defaults, triggers or journal attachment. Empty results still create
the typed empty file.

Source column types, null capability and character CCSIDs define the new fields.
Implemented external types are binary integers, decimal/numeric through the
current 29-digit physical limit, real/double, character/varying, date/time and
six-digit timestamps. Local boolean and untyped-NULL results map to logic and
nullable CHAR(1) fields. Source text is saved directly at its column CCSID rather
than converted through terminal presentation. Wider numeric results require an
explicit supported cast; unsupported layouts fail before creation.

Calculated columns without aliases use SEL. Duplicate names receive a number
from one shared counter; if the suffix would exceed ten characters, COL is used.
This follows the independent IBM example yielding SEL, SALARY, SALARY1, SEL2,
MAXBENEFIT, COL3. Explicit aliases, including quoted numeric aliases, remain
distinguishable from calculations through the query metadata's Calculated flag.
Long/special SQL names still need the broader system-name mapping work.

Existing targets retain their field names and definition. Columns map by ordinal;
count, compatible type/width/precision/scale and null capability are checked
before clearing. Supported numeric widening and character CCSID conversion use
the shared record converters. Incompatible metadata leaves existing records
unchanged. Individual value conversions can fail later while inserting.

## Authority, replacement and partial results

Creation requires ADD/operation authority to the library. Saving to an existing
file requires CHANGE and OBJALTER. Replacement also checks the signed CLRPFM
command adapter's authority. Source/query authority, signatures and job credentials
remain independent of output authority. A new file is private to its owner;
normal grants can make it readable by other profiles.

The default is `OUTMBR(*FIRST *REPLACE)`. Replacement commits the shared CLRPFM
step before writing answer rows, following the native SAVE boundary. Each row
then publishes in query order through the existing record, RRN/incarnation,
constraint, journal and capacity paths. A later error retains the clear and
successful prefix. `*ADD` retains old records and any successful prefix as well.
Errors report whether the file was created/cleared/changed and the number written;
SQL diagnostics reach the job log and the command returns QWM2701. Cancellation
after a publication can also leave that prefix. This is not an all-or-nothing save.
Exact native multi-row INSERT failure prefixes still require golden acceptance.

Automatic row allocations end with each publication, so output beyond the
8192-holder budget does not retain one holder per row. Member-full recovery
retries only the current row, keeping previously written rows. Each publication
rechecks source/query/target access and member/format identity after writer waits.
Final credential and job checks use the publisher's own connection, including
after transactional creation DDL or insert/delete hooks. A failed creation rolls
back all catalog/member/slot metadata. Query warnings survive the spool and are
written to the command job log.

Current private record-unit source data can be exported; rolling back that unit
does not undo the no-commit output. Enlisted output targets reject before writing.
Job/factory shutdown interrupts active spooling and joins cleanup. Connection
setup releases the factory lifecycle lock while waiting on SQLite, and owned
session end cancels query resources before catalog access; see
[query lifecycle](sql-queries.md). Normal exit,
failure and cancellation close the spool and delete its directory. Abrupt-process
orphan recovery, full native commitment/isolation and native scale targets remain
open.

## Names, members and remaining work

Unqualified output names use the current library. System naming permits *LIBL
lookup, falling back to the current library for creation; SQL naming rejects
*LIBL with QWM2707. OUTMBR accepts the native *FIRST with *REPLACE or *ADD.
Existing job/call-level OVRDBF output overrides can redirect the file/member.
The typed service also accepts an explicit member. Other members remain unchanged.
An existing file without a first member currently rejects; automatic member
creation and the remaining override/open policies need native acceptance.

A non-SELECT query runs without creating an outfile. Invalid output parameter
syntax rejects before execution. Print output, QMFORM/report formatting, updates
through LF/view targets, remote RDB output, externalized formats, wider native
types and names, exact messages/replacement/constraint/clear semantics, source
overrides, native SQL-schema ownership rules and current-release golden/performance fixtures remain open. Native
RRN, lock wait and ODP/hold/isolation details retain the wider C08 limitations.

Main schema stays 35, coordination format stays 2, and the release catalog stays
324 entries / 181 defined adapters. STRQMQRY and C07/C08 remain partial; the plan
checklist stays 36 complete / 70 open.

Verification (2026-10-19): all 7270 solution tests and 930 focused checks
pass, zero failures/skips, with locked restore, a warning-free Release build,
1000 frozen source/build/cache files, full terminal acceptance and outfile/CL/RPG
checks at both geometries, inventory, local links and whitespace. The full suite
uses four xUnit workers and completes in 9:57.45, including the 8203-row case.
