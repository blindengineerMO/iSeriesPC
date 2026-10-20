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

Sources can be physical/source files or simple logical files. Targets require a physical/source file with one format, of the corresponding source/data
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

Copy addition uses the target physical member's [durable extent and REUSEDLT
policy](record-slots.md). Replacement resets the extent in the same transaction;
a failed copy restores both records and allocation metadata. [SIZE capacity](member-initialization.md)
also applies to target additions; a late capacity failure restores the original
target records and extent.

Tests cover add/replace/self-copy, late trigger failure and side-effect rollback,
LF filtering/order, null preservation, mapping/drop validation, malformed values,
authority, record locks, cancellation, member defaults and command validation.
Real terminal acceptance copies LF records into a PF member and displays them.
Remaining CPYF work includes SQL-view/query sources, automatic member addition
in existing targets, multiple formats/members, UPDADD, record/key ranges, include
predicates, NOCHK/CVTSRC and the other native copy/conversion options. Native CPYF
targets are physical files; logical sources produce independent physical output. CPYF retains a partial compatibility status.

CPYF now preserves its staged snapshot across [member-capacity inquiry/retry](operator-capacity.md), validates member identities and releases TEMP staging before disk connections return to their pool.


CRTFILE(*YES) now creates missing targets in the shared service. The first six
parameters may be positional; omitted member names use *FIRST, MBROPT uses *NONE
and CRTFILE uses *NO. A newly created file ignores any valid MBROPT choice and
loads the staged rows. An existing target retains its definition and still needs
ADD or REPLACE; UPDADD on existing targets remains unimplemented. A missing target
must specify a library name or *CURLIB. See IBM's
[CPYF command](https://www.ibm.com/docs/en/i/7.5.0?topic=c-copy-file) and
[file-creation rules](https://www.ibm.com/docs/en/i/7.6.0?topic=files-creating-file-crtfile-parameter).

```text
CPYF FROMFILE(APP/ORDERS) TOFILE(TEST/ORDCOPY) CRTFILE(*YES)
CPYF APP/ORDERS TEST/ORDCOPY *FIRST ARCHIVE *NONE *YES
```

For a new file, TOMBR(*FIRST) names its member after the source **file**; it does
not select the source member name or the destination file name. TOMBR(*FROMMBR)
uses the resolved source member, and an explicit TOMBR uses that name. The copy
creates just the selected output member. This follows IBM's
[member-addition rules](https://www.ibm.com/docs/en/i/7.6.0?topic=copy-how-function-adds-members-file).
Member text/SEU-type, multi-member and existing-target member-addition acceptance
remain open.

Physical/source and simple logical sources retain their layouts, source widths,
selection and keyed ordering. The new file has independent records and access
paths, compact record positions and fresh member/row identities. Native DDS UNIQUE
attributes remain. SQL table/index sources create native physical output without
SQL table metadata, primary/unique/CHECK/foreign constraints or SQL defaults.
Triggers, source journal bindings and dependent logical files are not cloned.
The native source's typed field attributes remain, including explicit null
capabilities. SQL-view/query source projection and unsupported SQL field types
remain part of the unfinished utility work.

Source USE, destination-library use/add and CRTPF operation authority are required.
Public/private/authorization-list entries and public-list mode are copied. The
running user owns the output and receives ALL if a private entry for that user
was copied. Existing REPLACE also requires target OBJMGT along with operation,
add and delete rights. See IBM's
[to-file authorities and capabilities](https://www.ibm.com/docs/en/i/7.4.0?topic=parameter-authorities-user-profiles-file-capabilities-file)
and [copy authority rules](https://www.ibm.com/docs/en/i/7.4.0?topic=commands-general-rules-object-authorities).
Group-owner and the remaining file-capability/identifier contracts need independent
native acceptance.

The existing bounded snapshot precedes output DDL. Creation and loading share one
transaction, including member metadata, slots, access paths, copied authorities
and automatic capacity grants. New-file SIZE extends to accommodate the staged
rows without an operator inquiry, with the extension retained across restart.
This automatic creation extension differs from ordinary existing-target
[capacity recovery](operator-capacity.md). Failed loads or late publication checks
remove the entire created image; that atomic rollback is iSeriesPC's documented
extension to the broader native error-level/partial-copy contracts.

The writer checks source/logical/base/target identities, exact definitions, live
caller/job and permissions after hooks and before commit. A late cancellation,
job end, permission revocation, schema/ownership change or row-hook failure rolls
back the operation. An exclusive file allocation acquired before DDL covers the
new records through commit/rollback and avoids per-row lock holders or additional
main connections under SQLite's schema lock. QTEMP publication reuses its bound
namespace; ordinary record access resumes after publication.

Development verification (2026-10-20): 37 new creation cases, all 515 focused
checks, a final 37-case targeted run and all 7817 solution tests pass with zero
failures/skips (12:12.50, four workers). Locked restore, a zero-warning/error build,
1342 unchanged full-suite inputs, a separately frozen PTY script, inventory/links/whitespace and full 24x80/27x132 PTY
with CPYF-CRTFILE pass. The command and C07/C08/C09 remain partial.
