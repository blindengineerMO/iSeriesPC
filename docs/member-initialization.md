# Physical member initialization

`INZPFM` extends a physical data/source member with default records or allocated
deleted positions. Existing records and deleted gaps keep their positions. The
requested total includes deleted positions, so a total at or below the current
extent changes nothing. This follows IBM's [initialization contract](https://www.ibm.com/docs/en/i/7.5.0?topic=i-initialize-physical-file-member).

```text
CRTPF FILE(QGPL/DATA) SRCFILE(QGPL/DDS) SIZE(10000 1000 3)
INZPFM FILE(QGPL/DATA) TOTRCDS(12000)
INZPFM FILE(QGPL/DATA) MBR(*LAST) RECORDS(*DLT) TOTRCDS(12500)
CHGPF FILE(QGPL/DATA) REUSEDLT(*YES)
```

`FILE`, `MBR` and `RECORDS` also accept positional operands, in that order.
Members support an explicit name, `*FIRST` (the default) or `*LAST`, ordered by
creation time with a deterministic name tie break. `RECORDS(*DFT)` is the default;
fields use the existing DDS default and codec rules, including packed numbers,
nulls and CCSIDs. Default records append after the allocated extent. `*DLT`
reserves positions without materializing rows; input skips them, and ordinary
addition can reuse them when the physical file has `REUSEDLT(*YES)`.

`TOTRCDS` accepts 1–4294967288 or the default `*NXTINCR`. The latter extends to the
initial member allocation when below it, then to the next configured increment.
It rejects `SIZE(*NOMAX)` and zero increments. Beyond-limit initialization
uses the [operator capacity inquiry](initialization-capacity.md). Empty files are not
implicitly given readable records merely by specifying their creation size.

`SIZE(initial increment maximum)` is persisted by `CRTPF`, `CRTSRCPF` and `CHGPF`.
Omitted creation values use 10000/1000/3. `CHGPF` accepts `*SAME` for individual
values; an entirely unchanged list preserves the stored definition. `*NOMAX`
removes the configured capacity limit while retaining the native RRN ceiling.
Zero increment and zero maximum must occur together. Invalid lists, capacity
below any current member extent, or another invalid combined attribute leave all
metadata unchanged. See IBM's [creation](https://www.ibm.com/docs/en/i/7.5.0?topic=c-create-physical-file)
and [change](https://www.ibm.com/docs/en/i/7.5.0?topic=c-change-physical-file) references.

Ordinary physical/logical addition, declared RPG output and CPYF also enforce the
physical member's capacity. Reusing a deleted position does not extend it. Failed
copy/initialization rolls back records, extents, incarnation tokens and journal
accounting together. Beyond-limit additions use [CPA5305](operator-capacity.md);
initialization uses [CPA3138](initialization-capacity.md). Approved capacity is
committed separately and survives a later data-operation failure.
Earlier builds did not enforce the default capacity on ordinary additions;
`SIZE(*NOMAX)` permits continued growth of an existing large physical data file.
Private commitment workspaces and arbitrary raw SQL retain their existing
allocation rules; broader SQL and native commitment SIZE coordination remain open.

Initialization checks object-operation and add authority, either object-management
or object-alter authority, and delete authority for `*DLT`. Native management
alternatives also apply to CHGPF, CHGLF, ADDPFM and CLRPFM; ADDPFM additionally
checks library add authority. CHGPF/CHGLF do not require object-operation authority.
Library access, private exclusion, groups, live credentials and active-job checks
remain enforced. These alternatives follow IBM's [file-command authority table](https://www.ibm.com/docs/en/i/7.5.0?topic=commands-file).

An application insert trigger prevents initialization, including deleted-slot
initialization. Update-trigger bodies do not run. Managed journal/slot capture
triggers are recognized separately. IBM describes this [trigger restriction](https://www.ibm.com/docs/ssw_ibm_i_74/dbp/rbaforzahftrq.htm).
Single-format physical data/source members are supported; logical files, missing
members and custom WITHOUT ROWID tables reject before mutation.

The operation uses one file allocation, rechecks authority/job state after a
writer wait and rolls back on cancellation or shutdown. CLRPFM now shares
cancellable writer acquisition and rechecks its required authority after waiting.
Current locks are conservative file locks; native file/member lock granularity
and independent native multi-job acceptance remain open. Initialization
on an enlisted record resource rejects until native commitment-open semantics
are implemented.

Journaled initialization captures one `F/IZ` event in the same transaction, with
caller/job, member, resulting total, old/new extents, deleted/default flag and the
default image. It suppresses ordinary PT capture during default initialization.
Receiver limits and accounting apply; failed capture restores the member. The
payload is versioned local `IPCINIT2` JSON, with a captured column/file schema. IBM's [initialization entry](https://www.ibm.com/docs/en/i/7.5.0?topic=information-layouts-variable-length-portion-journal-entries)
defines the native total/flag/default image. [Initialization recovery](initialization-recovery.md)
now applies/removes these captured local entries. Historical IPCINIT1 entries
without schema reject. The external native binary ABI and independent acceptance
remain open. Ordinary record journaling resumes after the operation.

Default-record materialization is bounded to one million rows and 64 MiB of
normalized values per call. Deleted reservation is constant-sized metadata, even
at the native maximum. Independent IBM golden acceptance, full native statuses,
native initialization ABI/recovery acceptance and scale budgets remain open.
[RECNO field writeback and slot-directed RPG WRITE](rpg-recno.md) are implemented
within their documented subset.
C08/C09/C11 and the commands retain partial status. See [record slots](record-slots.md).

Verification (2026-10-19): all 6647 solution tests pass with zero failures/skips
in 11m17s, including 61 new cases. All 598 focused cases and 4313 broad
RPG/binding/work cases in the full suite pass. Locked restore, a Release build
with zero warnings/errors, 24x80/27x132 display PTY acceptance, generated inventory,
local links and whitespace checks pass.

[Approved operator capacity](operator-capacity.md) is available to explicit and
next-increment initialization. [INZPFM inquiry recovery](initialization-capacity.md)
now asks QSYSOPR before retrying a beyond-limit request.
