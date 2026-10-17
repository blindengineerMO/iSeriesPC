# Data areas

Named `*DTAARA` objects hold CHAR (1–2000 bytes), DEC (1–24 digits with up to 9
fractional positions), or LGL (one logical value). CRTDTAARA, CHGDTAARA, DSPDTAARA,
RTVDTAARA and DLTDTAARA use the shared service, object authority and job locks.
Character defaults are 32 bytes, or the encoded initial value length when supplied;
DEC defaults to (15,5). Numeric literals require DEC; quote digit strings for CHAR
or LGL. CL variables retain their declared scalar type when used as values.

Each object contains a versioned, bounded payload with dimensions, CCSID and value.
CHAR retains bytes (including non-text bytes through the buffer API); DEC uses an
exact canonical decimal representation; LGL stores 0/1. Malformed, duplicated,
unknown-version or inconsistent metadata fails before reading or writing. Changes
use immediate SQLite transactions and clear obsolete content signatures. Generic
catalog copies and renames preserve values; copies have independent storage.
Creation and authorization-list attachment commit together. Catalog events retain
the caller and object identity without including data-area contents.

CHAR substring operations accept native `(start length)` syntax and the existing
flat start/length extension. Positions are one-based bytes. Writes pad the selected
range with encoded spaces and preserve the rest. Bounds and oversized data fail;
no partial change is committed. DEC/LGL do not accept substring operations. Decimal
writes reject excess precision or scale. Reading a CHAR buffer does not decode it;
text consumers must explicitly translate its CCSID.

RTVDTAARA requires a compiled CL frame. CHAR returns pad to the declared output
length, and an undersized return variable fails before any output changes. CCSID
expansion is checked before assignment. Same-CCSID CHAR retrieval and local-area
writes preserve arbitrary bytes without decoding, including substrings that split
a multibyte sequence. Cross-CCSID conversion remains strict and fails before
assignment. DSPDTAARA shows hexadecimal bytes and their CCSID when text decoding
is invalid. A null value is rejected by the change service instead of resetting
stored data. Logical returns accept LGL or CHAR; a
single character 0/1 can return to LGL. Decimal areas used through CL are limited
to 15 digits; retrieval aligns decimal positions and truncates extra fractional
positions while rejecting integer overflow. The service retains 24-digit support
for other consumers. LDA/GDA/PDA retain their existing private job/group/routing
lifetimes and use the same CL selection and return checks.

Original storage checkpoint: 998 tests passed; Release built without warnings. DataAreaTests
covers independent EBCDIC bytes, raw UTF-8-tagged buffers, 24-digit signed decimals,
null/default/type boundaries, failure atomicity, concurrent substring changes,
live authorization lists, competing object allocations, command/CL paths, Unicode
expansion, copies/renames and catalog reopen. Terminal acceptance creates a named
decimal area, retrieves and changes it in CL, displays its attributes/value, and
deletes it. This does not close C11's combined DTAQ/DTAARA/USRSPC/API item. DDM data
areas remain unimplemented. QWCRDTAA now exposes checked native receiver layouts;
the current API contract and remaining native acceptance work follow below.

## QWCRDTAA retrieval API

QSYS/QWCRDTAA is a versioned IPCAPI program. CALL resolution, program authority,
signing trust, the executing job and cancellation use the shared runtime. It
retrieves local CHAR/DEC/LGL objects and the current job's *LDA/*GDA/*PDA. Named
objects also support private QTEMP, *LIBL and *CURLIB; returned QTEMP names remain
logical. *LIBL stops at the first matching object even when that object is denied.
Special areas require a blank library and cannot select another job's storage.
*PDA retains the documented routing-PIP extension in job-environment.md; native
prestart protocols are still separate unfinished work.

The six arguments, in order, are writable CHAR storage, receiver byte count
BINARY(4), qualified name CHAR(20), starting position BINARY(4), selection length
BINARY(4), and the shared API error receiver. Integers use signed big-endian
storage. Compiled CL callers can use `(*INT 4)` constants or CHAR(4) `%BIN` fields.
At least eight receiver bytes must be requested; a larger count than actual
storage fails before assignment. Receivers may truncate at any byte boundary.
Aliased receiver/error arguments, immutable outputs and incompatible declarations
fail without changing data output. Error-buffer tails retain their original bytes.

The fixed header is 36 bytes; the value follows immediately:

| Byte offset | Representation | Meaning |
|---:|---|---|
| 0 | BINARY(4) | Full result size |
| 4 | BINARY(4) | Result prefix delivered |
| 8 | CHAR(10) | *CHAR, *DEC or *LGL |
| 18 | CHAR(10) | Resolved library, or blanks for a job area |
| 28 | BINARY(4) | Character byte count, decimal precision, or one for logical |
| 32 | BINARY(4) | Decimal scale, zero for character/logical |
| 36 | variable | Value bytes |

The first two integers have the reverse order of user-space SPCA0100. Decimal
values use signed packed storage, including the leading zero nibble needed for
even precision; precision can reach 24 with scale up to nine. Logical values
return encoded `0`/`1`. Metadata characters use the caller's CCSID. Character
selections use one-based stored byte positions. Identical CCSIDs preserve opaque
bytes; different CCSIDs translate strictly into the caller's encoding. Conversion
failure, including a split UTF-8 sequence, leaves the receiver unchanged.

A start of -1 selects the whole value regardless of the positive selection length.
Positive starts permit character substrings wholly within the stored area; typed
decimal/logical substrings are rejected. All-value requests whose selection length
exceeds the value's byte width blank-pad the unused declared receiver payload up
to that requested length. Padding does not increase the header's actual value
size and cannot touch storage beyond the declared receiver length. The exact
padding/count behavior and cross-CCSID position semantics still need independent
IBM i acceptance fixtures; they are documented emulator behavior, not a claim of
complete native parity. DDM objects, full native exception data and native RPG
integer/DS storage also remain open. This program adapter does not expose the
C-library QXXCHGDA/QXXRTVDA pointer interfaces.

Library execute and data-area *USE authority are checked live. Retrieval takes
*SHRRD: it can coexist with EXCLRD or SHRUPD allocations but fails against EXCL.
Missing library/object returns CPF1021/CPF1015; denied library/object returns
CPF1022/CPF1016; library/object allocation conflicts return CPF1067/CPF1063.
Invalid start/substring/selection length returns CPF1088/CPF1089/CPF1863. The
shared error header or CL MONMSG receives these failures; errors never partially
assign the data result. Missing GDA/PDA retain CPF1046/CPF1072.

DataAreaApiTests checks independent raw headers, selected receiver truncation boundaries,
packed signs/even precision/maximum precision, encoded logical values, ranges,
output normalization, errors, CCSIDs 37/1208, live authority and signing trust,
allocations, job ownership, QTEMP and routing data. Compiled CL and direct RPG
callers verify actual writeback. The RPG caller borrows CHAR(4) binary fields;
this does not certify native RPG integer declarations or arbitrary binary UTF-8
character storage. Terminal acceptance compiles a CL caller and verifies decimal,
LDA and direct borrowed RPG retrieval.

References: IBM's [QWCRDTAA parameter and receiver contract](https://www.ibm.com/docs/en/i/7.4.0?topic=ssw_ibm_i_74%2Fapis%2Fqwcrdtaa.html)
and [JTOpen's data-area implementation](https://github.com/IBM/JTOpen/blob/main/src/main/java/com/ibm/as400/access/DataAreaImplRemote.java).
The latter independently confirms the 36-byte header, decimal precision/scale
interpretation, packed decoding and all-value calls with start -1.

References: [CRTDTAARA](https://www.ibm.com/docs/en/i/7.6.0?topic=c-create-data-area),
[CHGDTAARA](https://www.ibm.com/docs/en/i/7.5.0?topic=c-change-data-area), and
[RTVDTAARA](https://www.ibm.com/docs/en/i/7.5.0?topic=r-retrieve-data-area).
