# Local journals and physical-file capture

This C11 journal checkpoint supplies durable `*JRN` and
`*JRNRCV` payloads, receiver chains, bounded entry reading, and physical data/source
file capture under schema 30, plus forward apply and reverse removal. C11 remains
open: job commitment control, audit routing, private-file bindings, save/restore,
native retrieval formats, and automatic receiver management still need
implementation and acceptance. Existing host audit/event logging remains a
separate service.

## Commands

Only keyword forms listed here are accepted. Names resolve through the executing
job's library list/current library. Commands use the same `JournalStore` as trusted
host callers; compiled CL uses the ordinary command dispatch path.

| Command | Accepted parameters and behavior |
|---|---|
| `CRTJRNRCV` | `JRNRCV`, `THRESHOLD` (default 1500000 KB, or `*NONE`), `TEXT`, `AUT` |
| `CRTJRN` | `JRN`, one `JRNRCV`, explicit `MNGRCV(*USER)`, `DLTRCV(*NO)`, `TEXT`, `AUT` |
| `CHGJRN` | `JRN`, `JRNRCV(name)` or `JRNRCV(*GEN)` |
| `DLTJRN` | `JRN`; first end all attached file journaling |
| `DLTJRNRCV` | `JRNRCV`, `DLTOPT(*NONE\|*IGNINQMSG)` |
| `STRJRNPF` | One `FILE`, `JRN`, `IMAGES(*AFTER\|*BOTH)` (default `*AFTER`) |
| `ENDJRNPF` | One `FILE`; removes its binding and managed triggers |
| `SNDJRNE` | `JRN`, `TYPE` (default `00`), `ENTDTA` (default `*BLANK`) |
| `DSPJRN` | `JRN`, `FROMENT` (`*FIRST` or positive sequence), `TOENT` (`*LAST` or positive sequence), `NBRENT` (1–4000, default 1000) |
| `APYJRNCHG`, `RMVJRNCHG` | `JRN`, `FILE`, `RCVRNG`, `FROMENT`/`FROMENTLRG`, `TOENT`/`TOENTLRG`, `CMTBDY(*YES\|*NO)`, `OBJERROPT(*CONTINUE\|*END)`, `OUTPUT(*NONE)`; see recovery below |

```cl
CRTJRNRCV JRNRCV(QGPL/RCV0001) THRESHOLD(*NONE)
CRTJRN JRN(QGPL/APPJRN) JRNRCV(QGPL/RCV0001) MNGRCV(*USER)
STRJRNPF FILE(QGPL/ORDERS) JRN(QGPL/APPJRN) IMAGES(*BOTH)
SNDJRNE JRN(QGPL/APPJRN) TYPE(CK) ENTDTA('Checkpoint')
DSPJRN JRN(QGPL/APPJRN) NBRENT(100)
CHGJRN JRN(QGPL/APPJRN) JRNRCV(*GEN)
```

`DSPJRN` opens a work list with option 5 for entry details. Results are a bounded
page; resume from the last returned sequence plus one. Refresh preserves the
specified range/count. Displayed values are JSON escaped and limited to 128 fields
per image, 512 characters per value and 256 user-data bytes (hex); truncation is
labelled. The store retains full values. Outfile/print output, native converted
entry layouts, receiver ranges, filters and large-sequence command options are
not implemented. The typed reader accepts 1–10000 entries, subject to a 64 MiB
page limit. Entries commit before they become visible to another connection.

`SNDJRNE` currently accepts two ASCII alphanumeric type characters, folded to
uppercase, and at most 3000 encoded bytes. Literal text uses the job CCSID; hex
constants and compiled CL character buffers preserve bytes, including zeros and
trailing blanks. SNDJRNE borrows CHAR storage through the checked CL context
path; it does not render the buffer as a generic command string. All byte values
and trailing job-encoded spaces are verified at CCSIDs 37 and 1208. Quoted `'*BLANK'` is text; unquoted `*BLANK` deposits no data.
IBM's [SNDJRNE command](https://www.ibm.com/docs/en/i/7.5.0?topic=s-send-journal-entry)
allows up to 3000 characters and more general two-byte types; this build's byte
limit and type restriction are narrower. Object associations, force/state/remote
options and QJOSJRNE are not implemented.

## Receiver lifecycle and bounds

Creating a journal attaches a previously unused receiver atomically. An attached
or previously attached receiver cannot be reused. Rotation detaches the old
receiver, attaches an unused named receiver or creates the next generated name,
and preserves the journal's sequence counter in one transaction. A collision or
failure leaves the old receiver attached. Numeric suffixes increment with their
existing width; a prefix of at most six characters without a suffix gets `0001`.
Overflow beyond a ten-character name fails. Generated receivers inherit the
current threshold, use the library's create authority and have generated text.

Threshold values 1–1000000000 KB are accepted; values below 100000 are raised to
100000, matching [CRTJRNRCV](https://www.ibm.com/docs/en/i/7.4.0?topic=ssw_ibm_i_74%2Fcl%2Fcrtjrnrcv.htm).
The threshold is stored configuration, **not a hard capacity limit**. Notifications
and automatic switching are not implemented. IBM's
[CRTJRN default is system receiver management](https://www.ibm.com/docs/en/i/7.6.0?topic=c-create-journal);
the command therefore rejects omitted/default `*SYSTEM` instead of silently
substituting user management. Specify `MNGRCV(*USER)`.

Separate runtime limits are 1 GiB accounted payload per receiver, 16 MiB of
combined images/user data per entry, and 1 MiB per schema snapshot. Accounted bytes
include schema/images/data and a fixed 256-byte allowance; they are not filesystem
allocation measurements. Reaching a runtime bound aborts the record statement;
rotate before retrying. Sequence overflow also fails without changing records.

Deletion requires detachment and proceeds from the oldest end of each retained
chain. Unsaved receivers require explicit `DLTOPT(*IGNINQMSG)` acknowledgement;
there is no save integration yet, so attached receivers remain unsaved. Purging a
receiver deletes its entries. Deleting a journal requires no file bindings,
detaches the last receiver, and leaves receivers/history intact. Stable origin
IDs preserve retention order after journal deletion. Generic object deletion
cannot bypass attachment/order/unsaved guards. Generic duplication of journal or
receiver payloads is rejected; use of save/restore remains future work.

## Record capture and local format version 1

`STRJRNPF` accepts permanent physical data/source files and installs triggers on
all existing members in one transaction. New members, including source members
created by source saving, get triggers before loading records. Typed file writes,
logical-file writes to the underlying PF, SQL writes through factory connections,
member clearing, source replacement and CPYF into an attached target share these
triggers. No caller read authority to a journal is needed for automatic capture.
Factory connections enable recursive triggers so SQL `REPLACE` captures both its
delete and insert. Exhaustion uses an explicit abort, including when the outer
record statement specifies `OR IGNORE`.

Capture uses code `R`: `PT` for insert, `UP` for update, `DL` for delete. `*BOTH`
adds a preceding `UB` entry and retains the old image on `UP` for later local
recovery. `*AFTER` omits update before images; delete retains its deleted record.
User entries use code `U`. These are local records inspired by IBM's
[journal entry categories](https://www.ibm.com/docs/en/i/7.6.0?topic=information-journal-code-descriptions),
and [record operation types](https://www.ibm.com/docs/en/i/7.6?topic=rjo-actions-applying-removing-journaled-changes-by-journal-code),
not native IBM binary receiver records. No native retrieval API is advertised.
IBM's [QjoRetrieveJournalEntries](https://www.ibm.com/docs/ssw_ibm_i_74/apis/QJORJRNE.htm)
is an export of the `QJOURNAL` service program, with RJNE0100/RJNE0200 layouts
and optional pointer data. Implementing it needs the service-program/procedure
call contract as well as native record encoding; the documentation identifier
`QJORJRNE` is not being installed as a substitute callable program.

Each entry stores sequence/receiver IDs, UTC timestamp, current principal/job,
historical file/member names, positive physical record number, and a stable file
attachment ID. The schema snapshot contains column names/types/generated flags
and the file definition. Images contain ordered `[name, storageType, value]`
cells: integer values are decimal text, real values are 16 hex digits of IEEE 754
double bits, BLOBs are hex, text is preserved, and null is JSON null. This avoids
JSON number rounding or text conversion of binary values. Generated columns are
captured but marked in the schema for future recovery.

Record and entry creation, sequence advancement and receiver accounting use the
same SQLite transaction. Failure on either update image, a full receiver, a user
trigger failure or outer rollback leaves no record change, orphan entry, sequence
increment or partial accounting. This does not yet implement CL/RPG commitment
control: those transaction requirements remain C08.

File/library/journal/receiver renames retain payload identity. Trigger registries
follow catalog renames; entries preserve their historical names. A file duplicate
copies records and user triggers but gets no journal attachment or managed
capture triggers. Trigger ownership is checked by exact registry membership, so
a user trigger with a similar name is retained. Removing a member/file removes
the managed registry rows without deleting retained journal entries.

Capture rejects WITHOUT ROWID members, members shadowing all three SQLite rowid
aliases, nonpositive existing RRNs and changes to RRNs. Column-layout changes
fail subsequent writes until journaling is ended and restarted; data cannot
silently escape a stale schema snapshot. Direct administrator removal of managed
triggers/catalog tampering is outside the service contract.

## Authority, recovery and outstanding work

### Applying and removing changes

`JournalStore.Recover` streams a retained sequence range, preserving RRNs, column
storage types and generated values across up to 300 physical data/source
file/member selections. Repeated files merge member selections without applying
a change twice. It uses the existing attachment ID to match entries after rename and
prevents a newly attached file from consuming an old attachment's history.
Native converted receiver bytes and import of external journal files are not
implemented. This operation recovers the existing, currently journaled object;
creating/restoring objects and save-history-based ranges remain open.

The commands accept one file or element lists, for example:

```cl
RMVJRNCHG JRN(QGPL/APPJRN) FILE((QGPL/ORDERS *ALL) (QGPL/DETAIL *ALL)) +
 RCVRNG(QGPL/RCV0002 QGPL/RCV0001) FROMENT(60) TOENT(20) OBJERROPT(*END)
APYJRNCHG JRN(QGPL/APPJRN) FILE((QGPL/ORDERS *ALL) (QGPL/DETAIL *ALL)) +
 RCVRNG(QGPL/RCV0001 QGPL/RCV0002) FROMENT(20) TOENT(60)
```

Members may be named, `*ALL` (default), or `*FIRST` (earliest-created member).
Generic library/file selectors, `OBJ`, IFS/data-area/data-queue replay and object
DDL/member recreation are not implemented. For apply, specify `RCVRNG(*CURRENT)`
or two receiver names (the ending receiver may be `*CURRENT`), and explicit
sequence bounds: its native save-derived
defaults are unavailable. Removal defaults to `RCVRNG(*CURRENT)`,
`FROMENT(*LAST)` and `TOENT(*FIRST)`. Apply accepts `*FIRST`/`*LAST` bounds in the
opposite direction. Numeric ordinary bounds accept 1–9999999999; the `LRG`
parameters extend through the local signed-64-bit sequence maximum. Specify one
form of each bound. Native sequences beyond that maximum remain unsupported.

Named receiver endpoints follow processing order: oldest-to-newest for apply,
newest-to-oldest for removal. A range must be a contiguous retained chain of at
most 2045 receivers. Sequence coverage must be complete, including entries for
unselected files, and paired `UB`/`UP` entries are checked for consistency when both are selected.
Individual UP (apply) or UB (remove) entries may be endpoints. Current receivers
and default endpoints are resolved within the write transaction, before any
recovery markers are appended. See IBM's [receiver range rules](https://www.ibm.com/docs/en/i/7.4.0?topic=ssw_ibm_i_74%2Fcl%2Frmvjrnchg.html).

Forward processing inserts `PT`, updates `UP`, deletes `DL` and skips the paired
`UB`. Reverse processing removes `PT`, reinstates `DL`, restores `UB`'s before
image and skips `UP`. Removal requires the attachment to use `IMAGES(*BOTH)`. Record
presence must match before a change; the atomic service policy also checks
available expected images. Native command policies use the native record
operation and presence rules. After a change,
the complete stored image, including generated columns, must match the desired
image. Incompatible column layouts, record-format changes, missing members,
corrupt images, purged entries and prior apply/remove boundaries stop recovery.
After-only updates can be applied to an existing record without an expected
before image; they cannot be removed.

SQLite user and managed capture triggers are suspended transactionally while
their member is recovered, then restored from their original SQL in creation
order. This follows IBM's [trigger bypass rule](https://www.ibm.com/docs/ssw_ibm_i_74/dbp/rbaforzahftrq.htm).
FK actions are disabled on the recovery connection so a captured dependent
change is not applied twice; the resulting database must pass
`foreign_key_check` before it can commit. Include every dependent file, including
trigger-maintained data. This is recovery of captured changes, not re-execution
of the original application program. Custom functions used by generated
columns must still be available.

`OBJERROPT(*END)` stops all selected files on the first record failure;
`*CONTINUE` (default) stops that member and continues other members/files. Successful
changes in a consistent prefix remain committed. A failing record's savepoint
is rolled back, including first-use trigger suspension and markers. The command
returns `CPF70A7` with the failed file/member/sequence and the first/last successful
sequence; it does not report partial processing as success. No applicable
changes produce `CPF7069`. The shared service
also offers `JournalRecoveryErrorPolicy.Atomic`, which checks expected images
where available and rolls back the entire range on a record failure. Native
commands use the requested native policy.

Cancellation, invalid/missing source ranges, live authorization failure,
unresolved FK violations and failure to write final recovery metadata abort the
whole operation for every policy. Local committed record cycles are supported as
specified below. Outfile formats, save/restore defaults, alternate object
selectors and native abnormal-recovery formats remain open.

Successful recovery appends local version-1 `F/SA` and `F/AY` markers for apply,
or `F/SR` and `F/RC` for removal, per changed member. Their `IPCREC1` JSON data
records requested bounds, processed count/first/last and normal/abnormal flag.
The additional `IncompleteCommit` boolean records an incomplete ending cycle
for a changed member; it is separate from the existing failure flag.
These type names follow IBM's [apply/remove journal categories](https://www.ibm.com/docs/en/ssw_ibm_i_74/rzaki/rzakivarlength.htm);
the payload is an application format. They are barriers if encountered by a
subsequent recovery range. Replayed records do not create duplicate `R` entries.
An aborted atomic operation leaves no markers. File modification timestamps and
existing catalog audit events commit with recovery.

Recovery holds a single SQLite write transaction across selected files, marker
accounting and trigger changes. It rejects persistent file/record allocations
and authenticated jobs additionally hold exclusive file leases and parent-library read leases from before
the writer wait through completion. Cancellation and a later-file allocation
failure release every acquired lease. The executing job must have no active
record commitment definition, even one for unrelated files.
A process killed after uncommitted WAL pages were written is tested to recover
all original records, journal sequence and triggers on reopen. This is operation
atomicity; it does not implement an ambient CL/RPG/SQL commitment transaction.
The 10005-record acceptance range exceeds display/read page limits and uses
keyset seeks rather than repeatedly scanning earlier entries.

### Authority and remaining integration

Creation uses ordinary library create/owner/public authority or authorization-list
rules. This checkpoint requires journal `*OBJOPR` + `*OBJMGT` for attaching/rotating
receivers, file and journal `*OBJOPR` + `*OBJMGT` for starting/ending capture,
journal `*OBJOPR` + `*ADD` for user entries, journal and receiver `*USE` for reading,
and object `*OBJEXIST` for deletion. Live authority is checked at each operation;
entry reading rechecks all involved objects before returning. Runtime command
scopes honor object allocations; deletion also rejects persistent allocations.
Trusted host code without an operation identity retains existing host privileges.
Application validation uses `IPC0210`; unsupported command values use `IPC0003`.
Recovery additionally requires journal `*USE` + `*ADD`, file `*OBJOPR` +
`*OBJMGT` + `*READ` + `*ADD` + `*UPD` + `*DLT`, and `*USE` on every receiver
in its selected chain. These are declared service gates, not a full native
authority matrix. All gates are rechecked before committing, including a
consistent partial prefix. Authenticated socket jobs share the same recovery
service and write their actual caller/job identity into the markers.

Journals/receivers cannot be created in QTEMP. Private PF journaling is not yet
implemented here, although IBM supports journaling QTEMP physical files (see the
[ILE C guide examples](https://www.ibm.com/docs/en/ssw_ibm_i_74/pdf/sc092712.pdf)).
Moving an attached file into QTEMP is rejected until capture is ended. Permanent
to private file copies get no attachment.

Receiver entries, bindings and generated triggers survive ordinary reopen.
SQLite transaction recovery supplies crash atomicity for the current capture
path. Commitment control, private capture, receiver save
state, threshold notifications, automatic management/deletion, event/audit hooks,
remote journals and native retrieval/exit formats remain open. The journal and
commitment-control acceptance items must be completed together.

Forward [APYJRNCHG](https://www.ibm.com/docs/sr/ssw_ibm_i_74/cl/apyjrnchg.htm)
and reverse [RMVJRNCHG](https://www.ibm.com/docs/en/i/7.4.0?topic=objects-removing-journaled-changes)
now have the finite record-recovery contract above. Save/restore metadata,
object-level recovery and broader native commitment control remain requirements.
Recovery is its own operation; job-owned record transactions have the separate
[record-unit contract](record-transactions.md). Neither closes C08/C11 alone.

`JournalTests` covers durable rotation/deletion, restart, exact images, rollback
and injected failures, source/new-member capture, copy/rename, authorization,
allocation, cancellation, command/compiled CL/UI flows and independent process
writers. MigrationTests covers schema-29 upgrade history and failed-30 DDL/history
rollback followed by successful retry. The display PTY suite
exercises creation, binary send, entry details, rotation and acknowledged purge.

`JournalRecoveryTests` covers mixed records, exact storage/RRNs, paired boundaries,
current/named/reverse receiver ranges, large sequence numbers, all/first/named
members and repeated file selections, schema/image corruption, trigger/FK
recovery, atomic and native partial error policies, cancellation and authority,
10005-record streaming, crash rollback after WAL spill, compiled CL and two
independent authenticated socket jobs. The display PTY suite also removes and
reapplies a source record and displays the recovered data.


## C08 record transaction foundation

Schema 31 adds nullable cycle identity. Explicitly enlisted job record units now
publish multi-file changes and local C/SC, forced UB/UP and C/CM entries in one
transaction. Pending/rolled-back changes produce no live record entries yet, so
this is not complete native commitment journaling. APY/RMV now understands these
committed local cycles with the boundary rules below. See the
[record transaction contract](record-transactions.md) for visibility, storage,
cleanup, integration evidence and remaining joint C08/C11 acceptance.


## Committed record-cycle recovery

APY/RMV recognizes the `IPCCMT1` frames emitted by the record-unit service:
`C/SC`, zero or more `R` records and `C/CM`, carrying the same opaque cycle ID.
This is a local format, not an IBM binary receiver import or native commit-cycle
number. Each frame must be contiguous within its journal. Controls must agree
on their definition and job, and update records must form UB/UP pairs. Invalid
marker JSON/storage, mixed jobs, missing tags/pairs, foreign receivers, fractional
sequence/RRN values and unsupported control types fail without recovery effects.

With `CMTBDY(*YES)` (default), an endpoint on SC or CM identifies a boundary;
an explicit starting record inside a cycle is rejected before data changes.
An ending record inside a cycle stops recovery before that incomplete cycle,
in either direction. Earlier complete work remains eligible. The service returns
`JournalRecoveryResult.Boundaries`; commands show the boundary diagnostic without
inventing a native message identifier. An all-incomplete range changes no records
and reports the diagnostic. A transactional `journal.recovery.boundary` event
records the actual caller/job and omitted cycle, including a zero-change attempt.
An earlier `OBJERROPT(*END)` failure does not claim that the later boundary was
reached. Native END/CONTINUE record errors can still leave a prefix inside a
cycle; this is deliberately separate from the atomic service error policy.

`CMTBDY(*NO)` explicitly permits individual record endpoints without requiring
whole cycles. It retains the normal schema, image, receiver, authority, allocation
and referential-integrity checks. Before/after pairs are checked against the same
cycle identity, including an authorized partner read beyond the numeric bounds.
Lookahead never crosses the selected, authorized receiver chain. A shared cycle
ID in several journals does not merge their sequence spaces: recover each journal
with its own receiver/range selection.

Removal still requires a file explicitly journaled with `IMAGES(*BOTH)`.
Commitment control's automatic before images for an `*AFTER` file do not remove
that native restriction. This follows IBM's
[commitment-control restrictions](https://www.ibm.com/docs/en/i/7.6.0?topic=concepts-considerations-restrictions-commitment-control).
The general start/end and error rules come from
[APYJRNCHG](https://www.ibm.com/docs/sr/ssw_ibm_i_74/cl/apyjrnchg.htm)
and [RMVJRNCHG](https://www.ibm.com/docs/en/i/7.6.0?topic=r-remove-journaled-changes).
Those references distinguish an invalid starting participant, an incomplete
ending LUW, and an unexpected record error that may stop inside a transaction.
Control-marker endpoint handling here is the local-frame interpretation of those
rules; independent native receiver fixtures and the wider native formats still
require acceptance. IBM also describes
[separate recovery for multiple journals](https://www.ibm.com/docs/en/i/7.5.0?topic=eysot-recommended-recovery-procedures-after-eliminating-save-outage-time).

Cycle validation streams metadata without materializing record images and keeps
at most eight cached frames. Acceptance includes a genuine record unit with
10005 updates and 20012 cycle entries (including controls), cache eviction,
multiple journals, compiled CL at CCSIDs 37/1208, independent authenticated socket
jobs, authority revocation, exclusive allocation/cancellation, partial ranges,
corruption and END/CONTINUE/atomic failures. The terminal fixture creates a real
record-unit cycle for APY/RMV and explicit partial-entry commands.

Rollback/pending cycles, interleaved native transactions, save/restore partial
transaction state, native entry imports/layouts and object-level recovery remain
unsupported. Complete native commitment control and journal acceptance remain
open together in C08/C11.
