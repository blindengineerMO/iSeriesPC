# RPG right-adjusted assignment

`EVALR target = expression` assigns fixed-length native character storage with
left blank padding or left truncation. It supports free source and native fixed
calculations using extended factor 2. This extends the
[native structure](rpg-native-structures.md), [array](rpg-standalone-arrays.md)
and [null-map parameter](rpg-null-parameters.md) contracts. C09 remains partial.

```rpgle
**free
dcl-s TEXT char(5);
evalr TEXT='AB';
// TEXT is '   AB'.
evalr TEXT='ABCDEFG';
// TEXT is 'CDEFG'.
evalr TEXT='AB  ';
// TEXT is ' AB  ': source blanks are preserved.
```

## Native fields, arrays and structures

Targets can be native fixed-length character fields, indexed elements, qualified
subfields, nested structures, whole structures, structure arrays and `%SUBARR`
slices. Unindexed arrays and `array(*)` use elementwise assignment. A scalar
expression broadcasts over an array; two arrays assign their common prefix and
preserve extra target elements. Character concatenation expressions also support
array operands and explicit `(*)` references.

A scalar source is evaluated once before broadcasting. Native source bytes are
snapshotted before target publication, including overlapping array slices. Source
and target index procedures are evaluated once. Shared native index type checks
now use static type metadata so inspecting an indexed position does not rerun its
subscript procedure. Multiple-occurrence structures use the selected occurrence.

Whole structures and opaque `CCSID(*HEX)` fields move their raw bytes. The right
edge of the source aligns with the target's declared byte width. Blanks are
encoded in the current native CCSID; raw buffers must use the same CCSID. Character
source text that cannot be represented raises status 00452 before writeback.
The current byte-layout contract covers CCSIDs 37 and 1208; broader CHARCOUNT,
graphic/UCS-2 and conversion settings remain open.

## Substrings and borrowed memory

A writable `%SUBST(field:start:length)` target aligns within the selected region
and preserves its neighbors. Omitting length selects the remaining declared
width. A zero-length substring at the end is a bounded no-op. Starts and lengths
require non-float numeric values with zero decimal positions; fractional,
character and scaled numeric positions raise status 00105. Invalid declared
ranges raise 00100.

Selected substring bounds are also checked against actual borrowed memory.
VARSIZE formals can address a valid substring without reading or rewriting the
rest of a larger formal declaration. Access past actual caller storage raises
00105 before mutation. Source and target selectors are captured once, including
an indexed field used as a substring position.

All right-aligned array images are prepared before native writeback. Native
conversion and host validation failures preserve the receiver; the complete
prepared image is validated before an array write. CONST targets reject at
compilation. Borrowed memory retains its existing readonly, bounds and synchronous
lifetime rules. Character values retain leading and trailing blanks exactly.

## Null indicators and saved programs

`EVALR` moves data without changing null indicators. Automatic flags, named IND
associations and `OPTIONS(*NULLIND)` maps retain their values. Nullable implicit
and result-buffer record output uses those attributes normally, including when
new data bytes remain hidden by an on null flag. Saved programs retain the
operation after source removal; retained program calls rebind fresh borrowed data
and maps while preserving ordinary activation state.

The new IR opcode is appended, preserving existing opcode values. Schema 31,
catalog 306 and binding versions DISK 7/external descriptions 2/WORKSTN 5 are
unchanged.

## Remaining scope

EVALR precision extenders M/R, varying-length targets, graphic/UCS-2 and broader
CHARCOUNT/CCSID modes remain open. Known invalid target types, readonly targets,
array-to-scalar direct sources and invalid `(*)` references fail compilation with
source locations; runtime expression values must be character data. Explicit
`%CHAR` remains available for numeric or logical conversion.

Null-map INZ(*EXTDFT), divided nullable EXTFLD arrays, aggregate/whole-array
`%NULLIND`, EVAL-CORR precision/procedure extensions, multi-format/blocked I/O,
ILE linking, native foreign-language ABI, process aliases and independent native
golden/scale acceptance remain open.

The assignment rules follow IBM's [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 851–852, and its [EVALR documentation](https://www.ibm.com/docs/en/i/7.4.0?topic=codes-evalr-evaluate-expression-right-adjust).
The native byte-storage and precision subset is described above.

Verification (2026-10-18): all 5901 solution tests pass with zero failures/skips
in 8m54s, including 57 new cases (47 native/compiler/host and ten saved-program/
record cases). All 3645 broad and 57 final focused checks, locked restore, a
zero-warning/error Release build and expanded RPGRIGHT terminal acceptance pass.
Native and record cases cover CCSIDs 37/1208; general display acceptance covers
24x80 and 27x132. Generated inventory, local links and whitespace checks pass.
