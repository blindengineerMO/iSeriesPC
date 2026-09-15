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

Validation: all 998 tests pass; Release builds without warnings. DataAreaTests
covers independent EBCDIC bytes, raw UTF-8-tagged buffers, 24-digit signed decimals,
null/default/type boundaries, failure atomicity, concurrent substring changes,
live authorization lists, competing object allocations, command/CL paths, Unicode
expansion, copies/renames and catalog reopen. Terminal acceptance creates a named
decimal area, retrieves and changes it in CL, displays its attributes/value, and
deletes it. This does not close C11's combined DTAQ/DTAARA/USRSPC/API item. DDM data
areas and API-specific buffer formats are not implemented.

References: [CRTDTAARA](https://www.ibm.com/docs/en/i/7.6.0?topic=c-create-data-area),
[CHGDTAARA](https://www.ibm.com/docs/en/i/7.5.0?topic=c-change-data-area), and
[RTVDTAARA](https://www.ibm.com/docs/en/i/7.5.0?topic=r-retrieve-data-area).
