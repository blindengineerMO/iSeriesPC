# File copy — implementation in progress

CPYF now stages source records and applies target writes in one transaction. A
failure in validation, authority, locks, cancellation, constraints or a trigger
rolls back replacement and inserted rows, including transactional trigger effects.
This atomic behavior is an iSeriesPC extension; broader native CPYF options remain
open under C07/C08.

```text
CPYF FROMFILE(QGPL/SOURCE) TOFILE(QGPL/TARGET) MBROPT(*ADD)
CPYF FROMFILE(QGPL/VIEW) TOFILE(QGPL/TARGET) TOMBR(ARCHIVE) MBROPT(*REPLACE)
CPYF FROMFILE(QGPL/SOURCE) TOFILE(QGPL/TARGET) MBROPT(*ADD) FMTOPT(*MAP *DROP)
```

Existing targets require explicit `MBROPT(*ADD)` or `MBROPT(*REPLACE)`. Omitting
MBROPT uses native `*NONE` and fails for an existing physical target. Earlier
iSeriesPC builds implicitly appended. `FMTOPT(*NONE)` requires matching field
layouts; `*MAP` permits name-based conversion and missing target-field defaults,
and `*DROP` permits unused source fields. Unsupported conversions, overflow, and
nulls destined for non-null fields fail. Compatible null values are preserved.
These contracts follow the supported portion of IBM's
[CPYF reference](https://www.ibm.com/docs/en/i/7.5.0?topic=c-copy-file).

Sources can be physical/source files or simple logical files. Targets currently
require a physical/source file with one format, of the corresponding source/data
type. Logical source selection, member order and keys apply. Self-copy reads a
stable snapshot, so append duplicates the original records once and replace
preserves the original records. `*FIRST` selects the oldest member by creation
time; `TOMBR(*FROMMBR)` uses the resolved source member name. See IBM's
[member description reference](https://www.ibm.com/docs/nl/i/7.4.0?topic=ssw_ibm_i_74%2Fcl%2Frtvmbrd.html).

The snapshot uses a temporary SQLite table rather than a managed list of records.
It is limited to one million records and 64 MiB of staged field values. Existing
job lock limits also apply; reaching any bound fails the entire copy. Temporary
state is removed before commit or by rollback. Job commitment integration and
large-copy lock escalation remain part of the open database work.

Tests cover add/replace/self-copy, late trigger failure and side-effect rollback,
LF filtering/order, null preservation, mapping/drop validation, malformed values,
authority, record locks, cancellation, member defaults and command validation.
Real terminal acceptance copies LF records into a PF member and displays them.
Remaining CPYF work includes logical targets, multiple formats/members, CRTFILE,
UPDADD, record/key ranges, include predicates, NOCHK/CVTSRC and the other native
copy/conversion options. CPYF retains a partial compatibility status.
