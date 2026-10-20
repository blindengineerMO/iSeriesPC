# Declared RPG database files

RPG supports externally described DISK input, noncommit update/delete and record-addition declarations.
Free `DCL-F DATA;` defaults to DISK, external description and input usage.
`DISK`, `DISK(*EXT)` and `USAGE(*INPUT)` can make those defaults explicit.
`KEYED` selects the declared database key order; its absence selects arrival
order even if the PF has keys. This follows IBM's
[free file definition rules](https://www.ibm.com/docs/en/i/7.4.0?topic=specifications-free-form-file-definition-statement).
USAGE(*UPDATE) enables locked input and UPDATE. USAGE(*DELETE) also enables DELETE;
*INPUT can be written redundantly with either. *OUTPUT enables record addition,
alone or combined with input/update/delete. IBM defines these implications in the
[ILE RPG reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf).

```rpg
**free
 dcl-f LOOKUP disk(*ext) usage(*input) keyed
     extdesc('QGPL/CUSTOMER') extfile(*extdesc)
     rename(CUSTREC:CUSTOMER_ROW) prefix(C_) usropn;
 if not %open(LOOKUP);
   open LOOKUP;
 endif;
 chain 42 CUSTOMER_ROW;
 if %found(LOOKUP);
   sndpgmmsg %trim(C_NAME);
 endif;
 close LOOKUP;
 seton LR;
```

Fixed input F-specs accept native `F` in column 6, name in 7–16,
`I`, `U` or `O` in 17, `F` in 18 (blank also accepted for O),
optional `A` in 20, `E` in 22, optional `K` in 34, and `DISK` in 36–42.
Keywords begin in 44; blank-name F-spec continuations append keywords. The
historical layout shifted right one column also remains accepted. Primary,
secondary and program-described fixed files are rejected. A fixed U file enables
update and delete; A enables addition for I/U, while O always permits addition.
Fixed types determine usage, and a redundant USAGE keyword is rejected. These
flags follow IBM's [file addition definition](https://www.ibm.com/docs/en/i/7.5.0?topic=statement-position-20-file-addition).
Free global declarations precede calculations and can span source lines.

## Binding and file selection

Compilation imports scalar external fields and one record format through a
compile-time database resolver. `EXTDESC` selects the description file;
`EXTFILE` independently selects the runtime file. `EXTFILE(*EXTDESC)` uses the
requested EXTDESC filename, including fresh runtime overrides. Without EXTFILE,
the runtime filename defaults to the RPG file name. Long RPG aliases require
both EXTDESC and EXTFILE. Literal filenames must be uppercase and can use an
explicit library or `*LIBL`; `*CURLIB` is rejected. Named constants and variable
EXTFILE expressions are not implemented. These distinctions follow IBM's
[file name rules](https://www.ibm.com/docs/en/i/7.5.0?topic=considerations-rules-file-names),
[EXTDESC](https://www.ibm.com/docs/en/i/7.4.0?topic=keywords-extdescexternal-filename)
and [EXTFILE](https://www.ibm.com/docs/en/i/7.4.0?topic=keywords-extfilefilename-extdesc) contracts.

Compile and runtime overrides apply to the external filename, rather than an
unrelated RPG alias. Runtime overrides can select a compatible file/member.
`RENAME` changes the program record name; `PREFIX` adds an identifier prefix to
imported fields. External record names, field types, lengths, scale, CCSIDs,
positions and key sequence/direction form the saved layout signature. Physical
file identity can change through an override, but the expected layout cannot.

CRTBNDRPG saves source-bound `ipc.rpg.diskfiles` metadata alongside the program.
Loading uses that snapshot, including after reopening the catalog; it does not
recompile against whichever live file happens to exist. Restore checks version,
source hash, duplicate/unknown JSON properties, request identity, layout signature,
bounds and unused bindings. Version 3 records input/output and update/delete
access. Version 1 input-only and version 2 input/update snapshots remain readable
and cannot carry output privileges. Source/include diagnostics retain their original
locations. Open and subsequent transfers validate live authority and format;
changed formats require recompilation.

The initial field subset supports fixed character, packed/zoned decimal,
signed binary integers, floats, indicators and date/time/timestamp scalars.
Null-capable, varying and opaque database fields fail explicitly. Conflicting
scalar/DS declarations and ambiguous record names are rejected; RENAME/PREFIX
can distinguish files with the same external record. At most 16 disk files,
1024 fields per format and 1 MiB of binding metadata are accepted. General DS
memory layouts, native null attributes, qualified files and multi-format I/O
remain open.

## Input, open state and lifetime

READ/READP, keyed CHAIN/SETLL/SETGT/READE/READPE and arrival-mode RRN operations
use the shared [bounded cursor](cursor-navigation.md). Logical projection and
selection apply. [RRN searches](relative-records.md) identify actual sparse
physical slots. Record-format operands resolve to their owning declaration and
report status under that file name. Missing input preserves the program buffer;
character data such as the literal `*BLANKS` is copied as data. Input declarations
reject UPDATE/DELETE/WRITE; output-only declarations reject input and positioning.
FORCE and undeclared file operands are rejected during compilation.

Automatic files open in reverse declaration order, including mixed display/disk
files. USROPN files wait for OPEN. Explicitly closed files stay closed until OPEN;
I/O never silently reopens them. Duplicate OPEN, CLOSE of a closed file and I/O
on a closed file fail. `%OPEN(file)` reports the declared disk/workstation state.
The hosted activation context retains disk position/open state after RETURN.
LR, a new activation-group exit, group reclaim and job end close the handles.
Partial initialization closes files already opened.

OVRDBF SHARE can share compatible declared paths. Keyed/arrival and input/update/
delete/output access modes use separate identities, preventing one mode from inheriting the other's sort
position. Legacy implicit paths remain separate. Program-owned native handles
retain their path references across call frames and release them on close or
activation cleanup; override scope still determines which override is visible
when a file is opened. Broader native ODP compatibility and OPNQRYF sharing
remain open. Retained cursors rebind the executing call's cancellation token
without transferring ownership. Ordinary input retains service commitment checkpoints;
update and output declarations reject enlisted workspaces until native commitment
open policy is implemented.

## Locked input, N and UNLOCK

An update declaration opens a live cursor with a retained physical record lease.
Opening requires an executing job, read/update authority and, for delete-capable
files, delete authority. Unsupported providers cannot return a materialized or
ordinary input cursor as an update cursor. Live open/transfer validation includes
the access mode, so sharing cannot downgrade or upgrade a declaration.

By default CHAIN/READ/READP/READE/READPE lock their input for update. Attached
`(N)` retrieves inquiry input and releases any preceding input lease. Inquiry
reads can retrieve records held for update by another program. `UNLOCK file`
releases the retained input lease while preserving fields and position. A later
UPDATE/current DELETE requires fresh locked input; successful writes consume that
target. These operations use the [retained cursor contract](update-read-locks.md).
IBM specifies N input and unchanged fields on missing input under
[file operations](https://www.ibm.com/docs/en/i/7.5.0?topic=operations-file), and
separates file and data-area UNLOCK in its
[UNLOCK reference](https://www.ibm.com/docs/ssw_ibm_i_72/rzasd/sc092508.pdf).

UPDATE requires the externally described record-format name and maps imported
or prefixed program fields back to their physical column names. Logical
projections preserve hidden source columns. Key changes are allowed, and only
the selected duplicate is changed. DELETE requires *DELETE usage (or fixed U),
supports current input or a key/RRN search, and deletes one selected physical
record. See [IBM UPDATE](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-update-modify-existing-record)
and [DELETE](https://www.ibm.com/docs/en/i/7.4.0?topic=codes-delete-delete-record).

RETURN retains the input lease and target alongside the activation's file
state. Independent programs in the same job contend; compatible SHARE paths use
one cursor/lease. N input cannot release another path's lock. LR, explicit close,
activation reclaim and job end release the handles. Authority is checked again
when a retained call resumes and when the current record is written.

## Output and record addition

`USAGE(*OUTPUT)` opens a writer without fetching input or decoding existing rows.
Combine *OUTPUT with *INPUT, *UPDATE or *DELETE for mixed operations. Externally
described `WRITE` requires the record-format name and constructs values from all
imported program fields, honoring RENAME/PREFIX. Output does not create or clear
an existing file. The operation follows IBM's
[WRITE operand and addition rules](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-write-create-new-records).
Data-structure operands and E handling are still unsupported.

Output open/transfer requires object-operation and Add authority. Output-only
users need no Read, Update or Delete data rights. Input/output files also require
Read, and update/delete combinations retain their additional requirements.
Library-list resolution and persistent allocations respect this distinction;
ordinary public object-lock commands retain their existing authority contract.
Open allocates the file and a logical file's source for shared update. Transfers
recheck owner, active job, authority, member, format, cancellation and enlistment.
These checks run again after acquiring SQLite's writer, before inserting data,
so changes committed by a preceding writer are honored.
Retained handles rebind call cancellation and release on close, LR, activation
cleanup, job end or factory disposal. Factory shutdown cancels a pending SQLite
writer wait before closing output handles. A concurrent open/transfer can also
fail with a disposed-factory error if shutdown wins before a connection opens;
both paths leave records unchanged and release operation locks.

WRITE uses the store's atomic insertion and journal capture. Failed unique,
selection or trigger checks roll back the insertion. Logical output requires
exactly one bound physical member; omitted source columns use the physical
field defaults, and selection rules must accept the new record. A successful
addition does not provide an UPDATE target, move the input cursor or release an
existing input lease. Mixed input/output can read later additions through the
same bounded input path. SHARE separates input-only, output-only and combined
modes.

Sequential addition now uses [durable member extents and REUSEDLT](record-slots.md),
including incarnation checks for ordinary current-record writes. RECNO-directed
output remains open. [INZPFM/SIZE](member-initialization.md) now provide default/
deleted initialization and ordinary capacity enforcement. Enlisted output is rejected
at open and again after late enlistment, pending native commitment flags.

## Evidence and unfinished native behavior

`RpgDiskFileTests`, including the partial update and contention files, cover
free/fixed declarations, field mapping, keyed/arrival/relative input, N/UNLOCK,
update/delete access distinctions, duplicate and logical record identity,
missing input, retained calls, ODP compatibility, authority and saved modes.
`RpgOutputFileTests`, `RpgOutputFileLifetimeTests` and
`RpgOutputFileTransactionTests` cover output modes, authority, fixed addition,
field mapping, logical selection/uniqueness, cancellation, ownership, retention,
overrides, saved compatibility, journal attribution and explicit enlistment rejection.
`RpgUpdateFileTransactionTests` verifies explicit rejection of enlisted update
files and ordinary noncommit journal capture with job attribution. The existing
input-only transaction tests continue verifying service rollback checkpoints.
The real terminal RPGDISK fixture verifies input modes/USROPN; RPGUPD compiles
and calls a delete-capable program with N input, UNLOCK, prefixed UPDATE and
keyed DELETE. RPGOUTPUT adds and retrieves a renamed/prefixed record through an
explicitly opened input/output file, then verifies close status.

RECNO writeback/slot-directed output, native initialization ABI/acceptance and operator extension,
global pending RRN reservation,
commitment open flags and isolation,
Complete native file statuses, H/M/R extenders and general error handling, full native
fixed calculation layouts, DS/null/multi-format handling, wider ODP negotiation,
arbitrary SQL/trigger/FK and trusted jobless writer lock coordination, and
independent native golden/performance acceptance remain open. Unhandled service errors preserve CPF1002/CPF1003 identities. Declared RPG
operations classify actual record contention as status 1218. Declared update files
use [file/override WAITRCD](record-waits.md), including timed, immediate and
cancellable indefinite override waits. E/%ERROR/%STATUS, ER precedence and structured MONITOR are now covered by the
[bounded status contract](rpg-file-status.md). Full native status mapping remains
open. C08/C09/C11
remain unchecked.

[Record structures](rpg-record-structures.md) now support LIKEREC and checked
result operands for declared DISK input, update and output.

[DISK file templates and LIKEFILE definitions](rpg-file-definitions.md) now
provide qualified module-level file copies without opening their templates.
File parameters and WORKSTN variants remain open.

Nullable DISK fields, native null indicators and nullable key searches now use
[ALWNULL modes](rpg-null-values.md). Formal/aggregate null maps remain open.

Named variables and external indicator structures now use
[named null maps](rpg-null-maps.md). Parameter maps remain open.

[RECNO feedback and relative WRITE](rpg-recno.md) now associate native numeric
fields with physical positions and fill allocated deleted slots. Native status,
commitment, multi-member and independent golden acceptance remain open.
