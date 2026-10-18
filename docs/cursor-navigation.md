# Bounded database and RPG cursor navigation

`SqliteFileStore.OpenSequentialCursor` now provides live forward/reverse reads,
keyed `Chain`, `SetLowerBound`, `SetUpperBound` and adjacent `ReadEqual` operations.
A cursor retains one returned record, a bounded sort tuple and positioning flags.
It does not keep an open reader/SQLite transaction or a materialized record list.
The existing one-record read lease and candidate revalidation protect the row
actually returned. Positioning operations select index/key information without
decoding nonkey record fields or taking the row's read lease.

Keyed operations require declared keys and a contiguous leftmost prefix of whole
key fields. Field names are case-insensitive; duplicates, unknown fields and gaps
are rejected. Normalized decimal and CCSID-aware character key functions match
the existing physical/logical access paths. Nullable keys, descending terms and
member/RRN tie breakers use the same ordering for forward reads, reverse reads,
bounds and searches. The cursor still limits its sort position to 128 terms.
Unkeyed sequential/reverse reads use physical member/RRN order. Explicit RRN
lookup, bounds and deletion now share that cursor; see [relative record access](relative-records.md).
Compiled compound expression lists, KLIST and scalar `%KDS`
now use these same key-prefix operations; see [RPG file keys](rpg-file-keys.md).

`SetLowerBound` positions before the first record at or beyond the supplied key;
its result reports exact equality. `SetUpperBound` positions before the first
record strictly beyond the prefix. A later read obtains the target; a reverse
read obtains its predecessor. Neither operation replaces the RPG record buffer.
An unsuccessful limit positions at EOF. Reverse reads from EOF can retrieve the
last record. `Rewind` positions at either end independently of key order.

`Chain` returns the first matching record and positions subsequent forward/reverse
reads relative to it. An unsuccessful chain requires repositioning before another
read. Equal reads check the adjacent record, rather than scanning to a later
matching key. A mismatch returns no record and an EOF/BOF condition. Implicit
current-key reads need a valid positioned key; explicit forward equal reads after
EOF follow the first-record retry rule. Buffers remain unchanged when input
returns no record. The current mutation target is invalid after a failed search,
positioning or transaction rollback until a new successful input operation.

## RPG integration

`RpgSqliteFileAccess` implements `IRpgCursorFileAccess`. `RpgFileCursor` uses that
protocol for SQLite, so constructor/reload no longer call bulk `ReadKeyed`.
Reload validates live authority/schema while preserving position and the record
buffer. Real reads fetch one record. The host cancellation token reaches SQL
navigation through both interpreter-created and job open-path cursors.

Existing `READ`, `READP`, `READE`, `READPE`, `CHAIN`, `SETLL` and `SETGT` operations
use the bounded cursor. Traditional equal reads without a search argument use
the positioned key. Disk `%EOF(file)` and `%FOUND(file)` state now follows input
and positioning operations, alongside the existing indicators and workstation
status. Full native exception status, extenders, open flags, update-file locks
and independent golden acceptance remain open.
Custom non-SQLite providers that only implement `IRpgFileAccess` retain the legacy
materialized fallback; they must implement the cursor protocol for these bounds.

SQLite RPG cursors join the same job commitment checkpoint registry as bounded CL
cursors. Service commit saves the current position; service rollback restores
it, including a bound positioned before a target record. A rollback invalidates
the cached mutation target while leaving RPG program fields unchanged. A compiled
RPG caller with separate commit/rollback helper calls verifies resumed reads.
Native CL `COMMIT`/`ROLLBACK`, RPG `COMMIT`/`ROLBK` and commitment-open flags remain
planned; the internal transaction service does not establish native isolation.

## Current-record writes

SQLite cursor `Update` and `Delete`, including RPG `UPDATE` and current-record
`DELETE`, target the retrieved physical member and RRN. Duplicate-key siblings
are unaffected, and an unkeyed file can use these operations. Updates take the
current program fields, including changed keys, while the target remains the
record that was read. Logical projections change only their exposed fields;
unprojected physical fields remain intact.

Each returned record carries a private, exact typed before image of every physical
column and its table schema. A write checks that image, schema and logical
selection under its writer before executing. Changed, missing or differently
reused records fail with `CPF5026` and require a fresh read. Physical or logical
definition changes invalidate the cursor. Source and view authority are checked
again at write time, including after waiting for the writer. A job obtains an
exclusive allocation on the selected RRN for the statement; another allocated
duplicate does not prevent writing this one. Ordinary writer waits honor
cancellation. Trigger or validation failures roll back without consuming the
current target; successful writes consume it until another successful input.

Enlisted writes use the same private record workspace, ordered intents, retained
transaction locks and atomic record/journal publication as SQL and file-store
operations. Newly inserted pending records can be read and changed by the cursor.
Rollback clears its mutation target and restores its saved position. Trigger
effects stage once and are not executed again during publication.

This is an optimistic current-record service. Reads do not retain native
update-file locks. It cannot distinguish deletion and reinsertion of an identical
image at the same RRN without an intervening observed change. Global RRN
reservation/generations, commitment-open flags and native isolation remain open.
Navigation retains the original keyset anchor after a write, including DELETE;
native positioning after key changes still requires independent golden cases.
The bulk keyed file-store mutation APIs and custom provider fallback retain
their existing contracts.

IBM's [UPDATE rules](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-update-modify-existing-record)
identify the last retrieved locked record and require a new successful input
between updates. Its [DELETE rules](https://www.ibm.com/docs/en/i/7.4.0?topic=codes-delete-delete-record)
identify the last retrieved record when no search argument is supplied and
preserve sequential continuation after deletion. These rules motivate the target
identity and consumption behavior; the remaining lock/isolation work prevents
a claim of complete native acceptance.

## Evidence and limits

`DatabaseNavigationTests` covers bidirectional order, duplicate/null/descending
keys, exact adjacent large decimals, canonical character keys, lower/upper limits,
adjacent equal reads, failed chain recovery, live changes, corrupt nonkey payloads,
authority, cancellation and a later record allocated to another job. Its ordinary
SQLite RPG fixture opens and reads one record from 20000 rows with less than
4 MiB of managed allocations on the calling thread. This proves the absence of a
managed whole-file cache in that case. It is not a limit on SQLite native sort
memory, total system memory, query latency or every file layout.

`RpgCursorBoundaryTests` adds service commit/rollback, a saved lower limit,
invalidated mutation targets, a compiled RPG callback boundary and disk status
checks. Existing display, storage, cursor, compiled CL/RPG and transaction tests
remain required. `CursorRecordMutationTests` and `RpgCurrentRecordMutationTests`
cover selected duplicate writes, unkeyed files, key changes, logical projections
and multiple members, stale/unprojected images, schema changes, shadowing generated rowid aliases, deleted/reused RRNs,
authority revocation during a writer wait, foreign owners, allocations,
cancellation, statement failures,
compiled RPG, pending inserts, retained transaction locks and commit/rollback
publication. Legacy providers still reject an unkeyed delete without a record
identity.
Journaled commitment workspaces still load an accessed member
in full for each scope; row overlays, aggregate/schema/WAL budgets and large-file
performance targets remain open. Keyset positions retain the boundary's original
sort values; independently verified native behavior for another job moving the
positioned record's key is still required.

The native references motivating these rules are IBM's
[partial-key rules](https://www.ibm.com/docs/ssw_ibm_i_74/rzasc/partialkey.htm),
[SETLL](https://www.ibm.com/docs/en/i/7.6.0?topic=codes-setll-set-lower-limit),
[SETGT](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-setgt-set-greater-than),
[CHAIN](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-chain-random-retrieval-from-file),
[READE](https://www.ibm.com/docs/en/i/7.6.0?topic=codes-reade-read-equal-key) and
[READPE](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-readpe-read-prior-equal).
EOF clearing follows the [file EOF contract](https://www.ibm.com/docs/en/i/7.6?topic=functions-eof-return-end-beginning-file-condition).
These references define the desired native end state; local tests do not replace
independent IBM program fixtures for complete acceptance.

The database [retained update-read mode](update-read-locks.md) now adds scoped
input leases, inquiry overloads, unlock and bounded waits. Enlisted update reads
are rejected pending commitment lock policy. Native RPG usage/extenders and full
SQL/trigger coordination remain required.
