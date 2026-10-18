# DISK record structures and result operands

`LIKEREC(record{:extract})` defines a qualified native structure from a declared,
externally described DISK record. The supported extract types are `*INPUT`
(the default), `*OUTPUT`, `*ALL`, and `*KEY`. Input/output capability follows the
file declaration; `*KEY` requires KEYED. The record argument uses its internal
RENAME name, and PREFIX applies to member names.

```rpgle
**free
dcl-f DATA keyed usage(*update:*output);
dcl-ds BUFFER likerec(ROW:*all);
dcl-ds KEYS likerec(ROW:*key) inz;
eval KEYS.KEY=10;
chain %kds(KEYS) DATA BUFFER;
if %found(DATA);
  eval BUFFER.TEXT='changed';
  update ROW BUFFER;
endif;
seton LR;
```

`*INPUT`, `*OUTPUT` and `*ALL` preserve the record's field positions and length.
`*KEY` packs key fields consecutively in key order for `%KDS`; it cannot serve
as a record I/O buffer. Native signed integer, packed/zoned, character, floating,
indicator and default ISO date/time fields retain the supported database and
native-field limits. Null maps, varying fields and opaque database fields remain
outside the declared DISK subset.

## I/O and origin checks

CHAIN, READ, READP, READE and READPE accept a result structure after the file or
record name. WRITE and UPDATE accept a structure after the record-format name.
Native standard calculations use the result field; historical calculation
extensions retain their legacy record operands. Both supported traditional
D-spec origins accept LIKEREC definitions. A structure array requires an indexed
element; a multiple-occurrence structure uses its selected occurrence.

Input requires `*INPUT` or `*ALL`. WRITE requires `*OUTPUT` or `*ALL`, with one
DISK exception: an implicit default-input definition can be written when the
input and output layouts coincide. An explicitly specified `*INPUT` does not
qualify for that exception. UPDATE accepts input, output or all layouts. An
update file has an output buffer even without `USAGE(*OUTPUT)`; that usage
option controls record addition.

Buffers must derive from the matching record, including through LIKEDS. Equal
byte counts from another record or an unrelated plain structure do not grant
record access. Feedback extensions that change the structure's byte size cannot
be used as the original record buffer. TEMPLATE definitions supply types but
cannot receive data. CONST parameters cannot receive input.

Receiver addresses and indices are resolved once before cursor movement.
Missing records and EOF preserve receiver bytes. Input stages all field
conversions before one validated publication and leaves imported scalar fields
alone. Conversion failures preserve the entire receiver and invalidate the
current record to release update locks. Output reads one structure image and
validates its fields before file mutation. Indexed buffers preserve neighboring
array elements. Existing lock, N/E extender, status and MONITOR behavior applies.
Character conversion uses the runtime native CCSID; encoded input must fit the
fixed byte width. Complete CHARCOUNT, per-field CCSIDs and DATA(*NOCVT) remain
separate work.

## Interfaces and persistence

LIKEREC also supplies PR/PI parameter and return layouts, including fixed DIM,
CONST/VALUE, exact related types, implicit prototypes and RTNPARM. LIKEDS copies
retain the record origin and exact-type ancestry. Parameters continue to borrow
live caller cells under existing lifetime and storage limits.

Compile-time record metadata is resolved lazily and cached per file request.
Layout discovery and final binding use one descriptor snapshot. Runtime
INFDS/INFSR validation stays in its existing phase. Forward type and local
procedure previews share record metadata, while ordinary declaration constants
retain their existing source-order rules. Lookup callbacks detach on success
and failure.

Saved disk bindings remain source-bound and restore record definitions without
consulting the live description file. Programs can run after both the source
member and a separate compile-time description file are removed, using a
compatible runtime file. Runtime opens still validate format signatures and
perform normal authority checks.

## Evidence and remaining work

Tests cover record layouts, prefixes/renames, key order, fixed source, template
and forward type inheritance, all supported read/write/update forms, exact
origins, arrays, borrowed interfaces/returns, missing records, address preflight,
conversion atomicity, locks, malformed source locations and descriptor/source
removal. Saved cases exercise CCSIDs 37/1208. Process tests verify valid and
malformed response atomicity and rejection before launch. RPGREC supplies
terminal compile/call acceptance.

The implementation follows the [IBM i 7.6 RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
LIKEREC on printed pages 499–500, file result structures on pages 629–630, and
CCSID conversion on pages 304–305. It provides prerequisites for
[LIKEFILE](https://www.ibm.com/docs/en/i/7.6.0?topic=filename-rules-likefile-keyword),
whose qualified record names and result structures are required for file copies.
File parameters, WORKSTN templates/copies/result structures, EXTNAME, nested and multi-format
record layouts, null extracts, broader types/CCSIDs, full ILE linking and
independent native-system golden acceptance remain open. C09 remains partial;
checklist 36 complete / 70 open.

Verification (2026-10-18): all 5167 solution tests pass with zero
failures/skips, including 45 new cases: 42 record/metadata/integration cases and
three external-process cases. All 1202 broad focused checks and 124 final
record/display/legacy checks pass, as do locked restore, a Release build with
zero warnings/errors, final RPGREC terminal acceptance, generated inventory,
local links and whitespace checks. The final full run includes the
native-standard/historical result-operand compatibility guard and preserved
WORKSTN diagnostics. Full-suite wall time is 8m13s; no performance target is
claimed.

[DISK file templates and LIKEFILE definitions](rpg-file-definitions.md) now
provide qualified module-level file copies without opening their templates.
File parameters and WORKSTN variants remain open.
