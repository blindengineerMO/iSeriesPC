# Job record transaction foundation

This is an internal service foundation for C08/C11. Native commitment control
remains unfinished. `STRCMTCTL`, `ENDCMTCTL`, `COMMIT` and `ROLLBACK` retain their
planned command status. The service does not claim IBM `*CHG`, `*CS` or `*ALL`
isolation, activation-group scope, DB2 SQL syntax or RPG commitment opcodes.

## Explicit service boundary

`IpcSystem.RecordTransactions` creates one definition for a running job with
`Begin(job)`. `Enlist(job, file)` requires a journaled persistent physical/source
file and current use authority. `Open(job)` creates a synchronous statement
scope with parameterized SQLite `Execute` and bounded `Query`. Call `Complete`
before disposal to keep that statement's changes pending. Disposal without
completion backs out the statement. An SQL execution/query failure poisons the
scope: it must be disposed, and cannot be completed or reused. This also backs
out tracking entries from earlier rows of a failed multirow statement. Do not
await inside this scope: its gate is
owned by the opening thread.

`Commit(job)` publishes all pending changes together. `Rollback(job)` discards
them. Both retain the definition and file allocations for the next cycle.
`End(job)` requires no pending changes, no active statement and no attached open
commitment cursors. Job completion,
owner-factory disposal and interrupted-job recovery discard pending work and
release allocations. Another executing job cannot address the definition.
Trusted service calls without an operation identity follow the existing trusted
service convention; external hosts must supply the authenticated job identity.

Once a file is explicitly enlisted, the existing file store routes physical
insert/update/delete, materialized reads, row counts, logical projections and
mutations, and sequential cursor reads through the same unit. RPG's SQLite file
adapter and CL's shared file-store/cursor consumers consequently see its pending
records. SQL through `RecordOperation` joins the same unit and can enlist another
journaled file when it accesses it. Ordinary files remain ordinary file-store
operations; merely creating a definition does not enlist every file in the job.

`CLRPFM`, `CPYF` (including logical sources backed by an enlisted PF) and
source-editor operations reject enlisted resources until their
own transactional semantics are implemented. DDL and source creation are not
record transactions. SQL through the unit cannot modify catalog/security tables,
run DDL, attach databases, commit its private SQLite connection or directly call
the tracking callback. Read/write authority is checked for each SQL preparation
and changed record, and write authority is checked again at publication.

## Storage, visibility and atomic publication

Holding the catalog's SQLite writer across commands would also block independent
job logs, events and message operations. Each unit instead owns a private SQLite
record workspace and an ordered change log under `.record-units/<opaque-id>`.
The directory is created with owner-only access on Linux. Each statement first
copies schema metadata and anchors one committed read snapshot. It loads record
rows only for needed journaled physical/source members. SQL preparation authorizes
and discovers the base members used by joins, trigger programs and FK actions
before any user statement executes. Physical, logical and sequential file paths
supply their bound member names and prepare their actual queries/mutations through
the same service. Merely enlisting a file does not copy its rows or other members.

A member loads once per scope, from that scope's original snapshot, followed by
its pending intents in sequence. Import/replay temporarily disables capture,
user triggers and FK actions; the checked SQLite configuration is restored before
execution. User effects consequently run once, during actual statements.
`last_insert_rowid()` survives imports. Untouched pending members remain unloaded
and still publish from the ordered intent log. A preparation/execution failure
poisons the scope, including a failure while loading a newly accessed member.

Catalog, security, job, message, event and journal-history rows and unrelated
payloads remain excluded. Empty regular-table definitions preserve unjournaled FK
dependency checks; member indexes, generated columns, user triggers, sparse RRNs
and autoincrement high-water marks are retained. The SQL authorizer denies catalog
and unjournaled access and rejects an unexpected unmaterialized member during
execution; it cannot silently return an empty placeholder table.

The source read transaction lives until scope disposal, including completion,
SQL failure and cancellation. Persistent catalogs use WAL, so an independent
writer can commit while later loads still observe the original snapshot. Disposing
the scope releases its read position for WAL truncation. Shared-cache in-memory
catalogs retain SQLite's table/schema locking during this read transaction. Keep
scopes short; neither the source snapshot nor the record workspace is retained
as one open transaction across commands.

This remains full-member loading when a member is used. Aggregate growth limits,
row-level overlays and large-file performance acceptance are still required.
Private database files may retain reusable pages from an earlier larger scope
until the definition is disposed.

The exclusion test seeds a 16 MiB unrelated payload and nonempty security/event
catalogs, verifies their workspace tables contain no rows and measures less than
4 MiB of workspace database/WAL files. Additional cases preserve exact integer/BLOB
storage and generated values, reject unique conflicts and unjournaled FK cascades,
and observe one committed version across files under a concurrent writer. Additional
lazy-loading cases leave a 16 MiB unrelated journaled member, or another member
of the same PF, unloaded and keep the workspace below 4 MiB. They cover batches,
joins, nested trigger effects, file-store FK checks/cascades, pending replay,
authority denial and releasing snapshots after failure/cancellation.

Each statement observes a fresh committed snapshot plus its own changes. Writes
retain exclusive logical record locks until commit/rollback, and file/parent
leases survive until the definition ends. Newly inserted pending rows can be
read by the owning cursor before they exist in the live catalog. Two jobs can
stage and commit different existing rows independently. Changed-record conflicts
fail immediately; this is not the native isolation/lock-wait contract. The
existing per-job lock budget is 8192 holders, including file and parent
leases; repeated updates can produce more log entries without adding row locks.
Automatic record-number allocation can collide between concurrent staged inserts, which
then require retry. There are no repeatable-read, current-row or range/key locks.

Private statement transactions and their change logs share one statement
completion decision. The live catalog is changed only at publication, in one
SQLite transaction covering every changed file and its journal entries. Before
images, binding identity, table layout and file definition must still match.
Outside modifications or replaced bindings fail without overwriting live data.
The pending unit remains available for rollback or a valid retry. A successful
publication is recorded in memory before private cleanup so a cleanup failure
cannot cause the same live changes to be published twice in that process.

User triggers and foreign-key actions run in the workspace. Their changes to
other journaled physical resources join the ordered log and lock set. Publication
temporarily suspends user triggers and FK actions to avoid repeating their
effects, checks final referential integrity, restores the trigger definitions,
then commits. File authority and job ownership are checked under the live writer
before trigger changes. Credentials, including session/password expiry and directory
freshness, and active/noncancelled job state are checked again immediately before
commit on the same connection. This also supports shared-cache in-memory catalogs
without opening a connection blocked by the publisher's schema lock. Failed final
checks roll back records, journal entries and trigger changes and retain pending
intents for retry. Trigger changes to unjournaled data/catalog resources are rejected.
This mechanism does not implement the native `ADDPFTRG` interface.

Schema 31 adds nullable `commit_cycle` identity to the existing journal entries.
A publication writes local `C/SC`, its `R` records and `C/CM`, all with the same
opaque cycle ID, atomically. Update capture forces `UB`/`UP` before/after images
even for `IMAGES(*AFTER)`. Marker data uses local `IPCCMT1` JSON with definition
and cycle IDs; it is not an IBM receiver format. Record entries retain the
original change's principal, job and timestamp. Ordinary capture has no cycle ID.

Pending writes and rollbacks do not create live `R` or `C/RB` journal records.
Native journals can expose record entries before commit; implementing that
behavior is still required. APY/RMV now validates committed `IPCCMT1` cycles,
rejects a starting record inside a cycle and stops before an incomplete ending
cycle with a diagnostic. `CMTBDY(*NO)` permits explicit individual endpoints.
Native rollback/pending/interleaved cycles and partial save state remain open;
see [cycle recovery](journals.md#committed-record-cycle-recovery). Recovery cannot
run in an authenticated job while its commitment definition is active.

SQL query results are bounded to 10000 rows and 64 MiB, and individual changed
images use the existing 16 MiB journal-entry bound. Journal receiver capacity
checks remain part of the atomic publication. Aggregate workspace growth and
whole-member loading still need release limits and acceptance targets. SQLite's
`changes()`/`total_changes()` diagnostic functions count private import operations;
use `Execute`'s returned affected-row count for the user mutation.
Writer waits poll cancellation and retain the existing 30-second maximum wait;
waiting for an active statement gate is also cancellable. A cancelled statement
does not carry its cancellation token into later file enlistment. The gate and statements
are synchronous APIs.

An ownership row in the separate `.locks` database prevents another process from
silently opening the same pending definition. Ordinary catalog opens in the same
process share the owning unit. A dead process's pending data is never adopted as
committed work. Interrupted-job recovery removes the ownership row, private
directory and record/file locks. A live foreign owner must be routed through its
owning session. There is no prepared/distributed transaction protocol or durable
resume of an uncommitted unit.

## Cursor positions at commitment boundaries

Bounded `DatabaseRecordCursor` instances reading enlisted physical resources now
join the definition's cursor registry. This includes logical cursors over bound
physical members and the cursor used by compiled CL `DCLF`/`RCVF`. A cursor opened
before enlistment joins on its first subsequent read, with its current position
as its initial local checkpoint. An ordinary cursor whose source remains
unenlisted does not join or reposition.

A successful `Commit(job)` retains each cursor's current position and saves it
for rollback, including commits with no record changes. `Rollback(job)` discards
pending records, releases their locks and restores every attached cursor's last
successful commit checkpoint, or its initial join/open position if none exists.
Saved state includes the original sort tuple, physical member/RRN tie breakers
and EOF. It does not save a row-list offset or a copy of the file. Failure or
cancellation before publication does not replace the previous checkpoint. After
live publication succeeds, checkpoints advance before private cleanup so a
cleanup retry cannot capture a later position as the committed boundary.

Cursor reads, close, commit and rollback share the definition's synchronous gate.
A read completes its candidate validation and position update under that gate;
concurrent reads do not repeat or skip records. Gate waits honor the cursor's
cancellation token. Closing a cursor detaches it; rollback does not reopen it.
Job/owner-factory cleanup invalidates further reads but permits cursor disposal.
Same-process catalog reopens share the registry. Positions are volatile and do
not survive process loss. There is a limit of 256 attached cursors per definition,
matching the existing per-frame open-path budget; closing one frees its slot.

The two-reader concurrency fixture checks that all 40 records appear exactly
once. Its one-minute cooperative cancellation budget accommodates parallel suite
load and cancels the actual cursor operations, rather than abandoning workers
after a short wait. This is a correctness/deadlock guard, not a throughput target.

This is a local keyset checkpoint contract. It retains the sort tuple recorded
at the boundary; it does not track another job moving that record's key. Native
access-path movement still needs independent golden cases. SQLite
`RpgFileCursor` now uses this bounded protocol, including reverse/keyed navigation
and commitment positions; see [cursor navigation](cursor-navigation.md). Custom
non-SQLite providers retain a materialized fallback.
SQLite current-record UPDATE/DELETE also share the unit gate and intent path.
They retain the retrieved physical member/RRN and full typed before image, so a
duplicate-key sibling is not changed and an unkeyed record can be targeted.
They can change pending inserts, preserve unprojected physical columns, reject
intervening changes and consume the mutation target after success. Their trigger
effects stage once; publication does not repeat them. Temporary statement leases
coexist with unit-retained record locks. These optimistic checks do not establish
retained native update-read locks or native isolation.
Compiled expression-list, KLIST and scalar `%KDS` searches now use the same
cursor. Keyed DELETE holds the definition gate across selection, mutation and
position update; its one physical target is preserved through logical sources
and atomic journal publication. See [RPG file keys](rpg-file-keys.md).
Explicit relative-record input, bounds and deletion share those gates and
checkpoints. Sparse slot numbers and RRNs above signed 32-bit range survive
workspace import, pending changes and journal publication. Compiled RPG can use
explicit arrival access with keyed or unkeyed physical definitions. See
[relative record access](relative-records.md).
Native `COMMIT`/`ROLLBACK` commands, RPG `COMMIT`/`ROLBK`, file commitment-open flags,
lock levels and activation-group selection remain planned. Exposing those
operations requires coordinating every participating cursor and resource.

IBM's [rollback processing](https://www.ibm.com/docs/en/i/7.6.0?topic=work-rollback-operation)
requires returning open files to their previous transaction position, or their
open position before their first commit, and does not reopen closed files.
The RPG [COMMIT operation](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-commit-commit)
retains file position; [ROLBK](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-rolbk-roll-back)
restores it. These requirements explain why publishing language operations alone
would not complete native commitment support.

## Evidence and remaining acceptance

`RecordTransactionTests` (including `RecordWorkspaceTests.cs` and
`RecordCursorBoundaryTests.cs`, `RpgCursorBoundaryTests.cs` and
`RpgCurrentRecordMutationTests.cs`) covers compiled CL/RPG callers at CCSIDs 37/1208,
multiple files, SQL/file/RPG adapter/cursor and
logical access, trigger/FK-dependent updates, denial of unjournaled cascades,
exact large-decimal text/null/float storage, independent jobs, conflict detection,
live authority, cancellation while waiting for a writer or statement, failing
final commit markers, rollback, cleanup/reopen ownership, a killed staged owner,
a process killed during uncommitted WAL spill, and an attempted completion after
a multirow statement fails on another job's record lock. Migration tests preserve old
journal bytes/accounting and history through successful and failed schema-31
upgrades. Recovery tests validate complete committed frames, partial endpoints,
receiver authority and corruption; source cycles can be replayed after the
producing unit ends.

Remaining work includes native locking/isolation and open flags, command and
compiled RPG commitment operations, activation groups, SQL translation/defaults,
global RRN and key coordination, pending/rollback journal visibility, native
interleaved/rollback cycle recovery and partial save state, savepoints, notification, private resources, whole-member storage budgets
and release performance targets. C08 and C11 remain open and must be accepted
together.

IBM's reference contracts explain why these differences matter:

- [STRCMTCTL parameters](https://www.ibm.com/docs/en/i/7.4.0?topic=ssw_ibm_i_74%2Fcl%2Fstrcmtctl.html)
  define lock levels and commitment scope.
- [Commitment-control locking](https://www.ibm.com/docs/en/i/7.5.0?topic=files-using-commitment-control)
  distinguishes changed, current and all-record locks.
- [Nonerror conditions](https://www.ibm.com/docs/en/i/7.4.0?topic=errors-nonerror-conditions)
  distinguish starting commitment control from opening files under it.
- [Journal entry sequence under commitment control](https://www.ibm.com/docs/en/i/7.6.0?topic=objects-sequence-journal-entries-under-commitment-control)
  describes native record and commit/rollback entry timing.

The loader uses SQLite's [prepare-time authorizer](https://sqlite.org/c3ref/set_authorizer.html)
to discover access before stepping a statement, without changing the invoking
connection inside the callback. Import/replay uses the checked
[trigger and FK configuration switches](https://sqlite.org/c3ref/c_dbconfig_defensive.html),
then restores enforcement and authorizes execution again. Temporary triggers are
not part of the private workspace contract. These engine mechanisms implement the
local workspace; they do not establish IBM isolation compatibility.
