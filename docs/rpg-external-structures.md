# Externally described native data structures

`EXT` and `EXTNAME` obtain data-structure fields from a saved database or display
file description. They do not declare or open a runtime file. `EXT` defaults to
the structure's name; `EXTNAME` accepts a file, optional external format name,
and optional extract type. Without a format name, the first format is selected.
Free-form file/format arguments are character literals or earlier named constants.
Traditional fixed definitions also accept unquoted external names.

```rpgle
**free
dcl-ds CUSTOMER extname('QGPL/CUSTOMERS') qualified inz(*extdft);
  DISPLAY_NAME extfld('NAME');
end-ds;
sndpgmmsg CUSTOMER.DISPLAY_NAME;
return;
```

Structures can be qualified or unqualified, templates, arrays, multiple-occurrence
structures, or local automatic/STATIC variables. Qualified structures can contain
external inline children. LIKEDS copies, parameters, returns and nested children
retain external layouts and names. A definition without explicit subfield
statements can put `END-DS` on the header, including `EXTNAME` definitions.
Traditional fixed headers use `E` in column 22, or EXTNAME; both column origins
remain supported. External fixed subfields leave type/position/length columns
blank and use `E` or EXTFLD.

## Fields, renames and defaults

`*INPUT` is the default extract. `*OUTPUT` and `*ALL` select their corresponding
fields. `*KEY` selects database keys in key order with compact storage. The
supported native representations are fixed character, signed integer, packed,
zoned, float, indicator and the existing ISO date/time/timestamp layouts.

PREFIX applies to extracted field names, with an optional number of original
characters to replace. EXTFLD overrides can rename a field, set INZ, or divide
its storage with DIM. Explicit EXTFLD names bypass PREFIX. Additional typed
subfields follow the external storage; ordinary POS/OVERLAY rules remain active.
Duplicate, unavailable and incompatible definitions reject with source locations.

Ordinary INZ initializes native defaults. INZ(*EXTDFT) uses description defaults;
an EXTFLD can override its initialization. Original field storage is initialized
before DIM interprets it as an array. A divided numeric field can therefore
contain invalid numeric array elements, as on IBM i. CLEAR uses native defaults
for the selected variables rather than restoring external defaults.

Current date/time defaults evaluate in the running module. The existing host
clock supplies zero-argument %DATE, %TIME and %TIMESTAMP; basic TIME uses seconds
and default TIMESTAMP uses microseconds. Explicit date/time defaults retain
native representation. LIKEDS INZ(*LIKEDS) preserves inherited defaults.

Unqualified external subfields can share compatible imported file variables,
including divided arrays. Implicit DISK and WORKSTN I/O transfers each complete
original field while ordinary expressions retain the array's element type and
dimension. Input conversions are prepared before field publication. File PREFIX
and data-structure PREFIX remain independent; explicit result buffers map
EXTFLD names to the original external fields.

## Record buffers and interfaces

EXTNAME result structures must specify an explicit compatible extract. Input
requires *INPUT or *ALL; EXFMT requires *ALL; WRITE requires *OUTPUT or *ALL.
UPDATE follows the existing DISK and subfile rules. The default extract supplies
types and variable storage, and cannot serve as an explicit result operand.

Record operations check the external format, field layout, native widths and
extract mode. Compatible logical access paths can use the same record buffer
without inheriting its key order. Added storage that changes the record byte
width rejects as a result buffer. Nested children and selected array elements
work with DISK and WORKSTN I/O and %KDS. Receiver staging, missing-record
preservation, locks and status/error handling keep their existing contracts.

Forward LIKE/LIKEDS, templates, PR/PI interfaces, EXACT ancestry, detached returns
and RTNPARM use the description snapshot. Equivalent file/format aliases share
type identity; conflicting snapshots of one canonical format reject. Protocol-2
processes receive native buffers with checked atomic parameter writeback.

## Saved descriptions and bounds

`ipc.rpg.externalstructures` version 1 (nonnullable) or version 2 (nullable) saves source-bound external descriptions
independently of runtime file bindings. Restore rejects stale source, duplicate
properties, unknown members, invalid layouts and unused bindings. Runtime loads
reconstruct without consulting live description files. Source and descriptions
can be removed before calling a program; actual runtime files still undergo
normal authority and format checks. Activation fingerprints include the new
attribute. Nullable DISK bindings use version 7; WORKSTN versions remain unchanged. Programs
without the attribute retain their previous behavior.

Requests are cached across main/procedure/forward lookups, with at most 256
external description requests per module. Snapshot writing enforces its 1 MiB
bound while escaping output. Existing source, field, dimension, nesting and
native storage limits remain active. Descriptions do not consume runtime file
slots or retain open file handles.

The rules follow IBM's [EXTNAME reference](https://www.ibm.com/docs/en/i/7.4.0?topic=keywords-extnamefile-nameformat-nameall-inputoutputkeynull)
and [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 238, 249–250, 442, 476–479 and 629–630. Native nullable fields now use
[null indicators](rpg-null-values.md). Formal/aggregate null maps, varying/opaque database
fields, opaque display message keys, additional external aliases/CCSID formats,
multi-format file I/O, blocked I/O, ILE linking and independent native-system
golden/scale acceptance remain open. C09 remains partial; checklist 36 complete /
70 open. No performance target is claimed.

Verification (2026-10-18): all 5602 solution tests pass with zero failures/skips
in 8m51s, including 63 new accepted cases: 40 structure/metadata/default/ABI
cases, 19 record/integration cases and four external-process cases. One obsolete
external-E rejection case is replaced by positive saved-program acceptance. All
2025 broad and 324 final focused checks, locked restore, a Release build with zero
warnings/errors, expanded RPGEXTDS terminal acceptance, generated inventory,
local links and whitespace checks pass.

Named variables and external indicator structures now use
[named null maps](rpg-null-maps.md). Parameter maps remain open.
