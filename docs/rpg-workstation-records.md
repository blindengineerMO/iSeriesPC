# RPG WORKSTN record structures

LIKEREC now describes fields from a global externally described WORKSTN file.
Free and traditional fixed declarations use the same checked native buffers.
For example, with an ENTRY format containing NAME and AMOUNT:

```rpgle
**free
dcl-f SCREEN workstn extdesc('QGPL/SCREEN') extfile(*extdesc);
dcl-ds BUFFER likerec(ENTRY:*all) inz;
eval BUFFER.NAME='START';
eval BUFFER.AMOUNT=1.25;
exfmt ENTRY BUFFER;
sndpgmmsg BUFFER.NAME;
seton LR;
```

The operation reads and writes BUFFER, leaving imported scalar NAME and AMOUNT
unchanged. Response indicators still update through the existing indicator
binding. RENAME and PREFIX apply to the format and subfield names. LIKEDS copies,
native parameters, OPTIONS(*EXACT), procedure returns and RTNPARM retain the
record's ancestry. Metadata lookup is lazy and cached, including forward return
types, and does not open the display file.

## Extracts and operations

| Operation | Permitted LIKEREC extract |
| --- | --- |
| EXFMT | `*ALL` |
| READ, CHAIN, READC | `*INPUT` or `*ALL` |
| WRITE, UPDATE | `*OUTPUT` or `*ALL` |

The default extract is `*INPUT`. Extracts select input, output or all fields in
the original format order. Subfile record buffers include stored output and
hidden fields on input as well. Display numeric fields use zoned decimal bytes,
with one byte per digit, even though imported scalar numerics use the existing
logical numeric representation. Character fields use job CCSID bytes; opaque
SFLMSGKEY fields preserve their four raw bytes. Date and time fields retain their
native types. `*KEY` is rejected for WORKSTN.

An I/O receiver must be a matching native record structure or one structure-array
element. Equal field layouts from unrelated formats do not confer compatibility.
Input rejects CONST and readonly storage before terminal transfer. WRITE can use
a readonly output parameter. An indexed receiver is captured once. Input by file
name requires a single-format file; multi-format nested overlay buffers remain
unsupported. A selected extract must contain 1 through 1048576 bytes.

## Publication and saved programs

Output snapshots and validates one buffer before transfer. Input stages all field
conversions, RRN and response-indicator updates before the workstation acknowledges
the response. Conversion failures preserve the receiver, RRN, indicators and
pending host input. CA responses, misses and EOF leave receiver bytes unchanged.
Output-only fields in an `*ALL` buffer survive input. Existing file status,
E extenders, MONITOR handling and native storage/lifetime checks still apply.

Saved display layouts reconstruct the same buffers after source removal, using
the existing checked WORKSTN binding versions. Native external-process buffer
calls use the zoned layout and retain atomic response writeback.

These rules follow IBM's [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 499–500, 629–630 and 859–860, and the DDS
[display decimal-position rules](https://www.ibm.com/docs/en/i/7.6.0?topic=44-decimal-positions-display-files-positions-36-37).
[WORKSTN templates and LIKEFILE copies](rpg-workstation-definitions.md) extend
these layouts. [Procedure-local WORKSTN files](rpg-local-workstations.md) add local
display lifetimes. Zero-field extracts, EXTNAME, multi-format/null layouts, broader CCSIDs
and independent native-system golden/scale acceptance remain open. C09 stays
partial; the checklist remains 36 complete / 70 open.

Verification (2026-10-18): all 5347 solution tests pass with zero failures/skips
in 8m25s, including 36 new cases: 32 record/metadata/integration cases and
four external-process cases. All 36 final focused checks pass. The broader run
passed 1447 other cases; its date/time fixture failure was corrected and passes
in the final focused and full runs. Locked restore, a Release build with zero
warnings/errors, expanded RPGWREC terminal acceptance, generated inventory,
local links and whitespace checks pass. No performance target is claimed.

[WORKSTN file parameters](rpg-workstation-parameters.md) now borrow caller-owned
display handles, subfile state, RRN/indicator bindings and error handling.

[Nested native structures](rpg-nested-structures.md) now support inline and
LIKEDS/LIKEREC children, shared storage views, forward types and nested record
buffers. See that contract for supported operations and bounds.
