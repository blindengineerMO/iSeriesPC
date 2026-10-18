# Relative record input and deletion

The database cursor now supports explicit relative-record-number (RRN) lookup,
lower/upper positioning and deletion. An RRN identifies a physical member slot;
it is independent of a record's ID/key field, row count or current list index.
Deleted gaps retain their numbers. Numbers from 1 through 4294967288 are accepted
without signed 32-bit conversion. IBM describes
[RRN processing](https://www.ibm.com/docs/en/i/7.5.0?topic=files-relative-record-number-processing)
and uses this range for [physical record locks](https://www.ibm.com/docs/en/i/7.6.0?topic=ssw_ibm_i_76%2Fcl%2Fdsprcdlck.html).

## Cursor operations

| API | Behavior |
|---|---|
| `ChainRecord(number)` | Retrieve that existing, selected physical slot. A miss invalidates the input target and requires repositioning. |
| `SetRecordLowerBound(number)` | Position before the first selected slot at or above the RRN. Return whether the slot is an exact match. |
| `SetRecordUpperBound(number)` | Position before the first selected slot strictly above the RRN. |
| `DeleteRecord(number)` | Delete one existing, selected slot; report false on a miss and preserve the previous position and target. |
| `CurrentRecordNumber` | Physical RRN of the currently retrieved input target, or null after positioning, failed input, a consumed write target, rollback or close. |

RRN predicates apply to the actual unshadowed SQLite rowid alias in the physical
member branch. A lookup does not enumerate preceding records or decode other
record payloads. Bounds project positioning information without decoding the
record buffer. Native-range sparse test slots do not allocate intervening rows.
The existing one-record lease, candidate revalidation, schema/before-image checks,
live authority, cancellation and job ownership rules also apply to these paths.
Delete authority is required even for a missing slot.

The cursor's configured sequential access path remains in effect. An RRN lookup
or bound chooses a physical slot and saves that slot's canonical position. With
`OpenSequentialCursor(..., keyed: false)`, later READ/READP uses physical RRN order.
With a keyed cursor, later reads use that record's key position. Thus the next
record after an RRN lookup need not have the next physical number in a keyed
path. Native combined-access and moved-key golden acceptance remain open.

A logical view applies its projection and select/omit predicate to the physical
slot. The opened logical member must bind exactly one physical member. Multiple
bound source members can each contain the same RRN; these calls fail explicitly
until native mapping is established. A caller can open an appropriate single
member binding instead. This restriction leaves broader native member acceptance
open; it does not change ordinary keyed multi-member traversal.

## RPG access mode and operands

`RpgFileCursor` exposes the same RRN operations and an optional `keyed` constructor
argument. A host can select arrival access on a PF that also has DDS keys:

```csharp
new RpgFileCursor(new RpgSqliteFileAccess(files),
    "QGPL", "DATA", "DATA", keyed: false);
```

The cursor protocol's explicit-mode overload is optional for providers. Existing
providers keep their original entry point; providers that do not implement the
new overload reject an explicit mode. The SQLite provider passes it to the shared
database cursor. Explicit keyed mode requires actual keys. Materialized custom
providers cannot infer RRNs from row indices and reject RRN operations.

Declared DISK input files select their mode explicitly; see [disk declarations](rpg-disk-files.md).
The existing implicit RPG opening infers its mode from the file's key metadata
when no host override is supplied. In arrival mode, CHAIN, SETLL, SETGT and DELETE
use an RRN. READ/READP continue in physical order and skip deleted gaps. READE and
READPE require keyed mode. The WORKSTN/SFILE RRN path remains separate.

RPG RRN operands must be integer literals or numeric fields with zero declared
decimal positions. Numeric array elements require a valid index; scalar fields
cannot be indexed. Character values, fractional declarations, out-of-range values,
compound lists and KDS/KLIST arguments are rejected rather than coerced into an
RRN. Bounds preserve program input fields. Successful CHAIN replaces them and
updates FOUND/EOF state; a miss preserves fields and reports not found. Successful
RRN input can supply the current target for UPDATE. IBM specifies the operand
rules in [CHAIN](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-chain-random-retrieval-from-file),
[SETLL](https://www.ibm.com/docs/en/i/7.4.0?topic=codes-setll-set-lower-limit),
[SETGT](https://www.ibm.com/docs/en/i/7.4.0?topic=codes-setgt-set-greater-than) and
[DELETE](https://www.ibm.com/docs/en/i/7.4.0?topic=codes-delete-delete-record).

## Transactions, evidence and remaining work

RRN positions join the same commitment checkpoints as keyed positions. Commit
saves the position; rollback restores it and clears the cached input target.
Before-target limits retain their inclusive next-read behavior. Pending inserts
can be retrieved, updated or deleted by their actual workspace RRN. Publication
preserves that number in record/journal changes. RRN deletion holds the unit gate
across selection, mutation and positioning, and retains transaction record locks
through the boundary.

`RelativeRecordTests.cs` verifies sparse/deleted slots, native maximum values,
configured order, limits/EOF, logical selection/members, payload laziness,
allocations, authority, ownership, cancellation, input validation and explicit
arrival-mode RPG access. `RelativeRecordTransactionTests.cs` covers checkpoint
restoration, pending inserts, journal RRNs above signed 32-bit range and compiled
RPG against keyed and unkeyed physical definitions. The terminal RPGRRN program
performs CHAIN, a deleted-slot miss, SETLL, UPDATE and one-record DELETE/read
continuation.

Native DISK input declarations now bind F-spec/DCL-F layouts and access modes.
Declared update/delete/output modes and retained input now also use
[file/override WAITRCD policies](record-waits.md). Native commitment-open flags,
RECNO field writeback, initialized/deleted-slot output and INZPFM/REUSEDLT allocator
semantics, global RRN reservation/generations, complete native statuses/isolation,
multi-member relative mapping and independent IBM golden acceptance remain open.
The current record workspace still loads a used member in full; large-file,
aggregate and WAL budgets remain required. These are additions to the internal
[cursor](cursor-navigation.md) and [transaction](record-transactions.md) foundation;
C08/C09/C11 remain incomplete.
