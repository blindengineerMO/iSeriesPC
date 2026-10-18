# Native RPG alternating compile-time arrays

Free standalone arrays support ALT(main) with compile-time data. The main and
alternate have separate native storage and may have different element types and
widths. Either declaration may come first. This extends the existing
[CTDATA loader](rpg-compile-time-arrays.md); C09 remains partial.

```rpgle
**free
dcl-s VALUES packed(3:1) dim(2) alt(NAMES) extfmt(L);
dcl-s NAMES char(2) dim(2) ctdata perrcd(2);
sorta NAMES;
// NAMES is now AA, BB; VALUES remains 1, 2.
sndpgmmsg %char(%xfoot(VALUES));
seton LR;
**CTDATA NAMES
BB+010AA+020
```

## Pair layout and declarations

ALT names the main array. Its PERRCD counts pairs, each containing a main entry
followed immediately by an alternate entry. The main controls section binding
and loading. Each member keeps its own type, source width, numeric EXTFMT and
ASCEND/DESCEND declaration. IBM describes this in
[Defining Related Arrays](https://www.ibm.com/docs/pt/ssw_ibm_i_74/rzasd/defrel.htm)
and the ALT and EXTFMT entries in the
[ILE RPG reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf).

The supported contract requires matching dimensions and one alternate per main.
If either dimension is inferred, both must use DIM(*CTDATA); the main's named
section supplies the count, including when the alternate is declared first.
Later declaration constants can use either array's inferred size.

The alternate cannot specify CTDATA, PERRCD or INZ. Its target must be a standalone
compile-time array. Missing targets, scalar/runtime targets, chains, cycles and
multiple alternates reject compilation with source locations. FROMFILE/TOFILE
remain unsupported. Bare sections follow main-array declaration order, skipping
alternates; named sections identify the main. Named and bare modes cannot mix.

The sum of both source widths times the main's PERRCD is bounded to 100 source
positions. Numeric sign positions and float source widths count toward that
bound. Short records are padded and absent entries keep type defaults. Both
members of unused final pair slots must be blank. Text after all PERRCD pairs is
commentary. Space-padded numeric digit fields are accepted; embedded spaces in
digits remain invalid. Existing type, precision, source and native allocation
bounds continue to apply.

## Storage and operations

Each member receives its own validated initializers and record origins. Numeric
sequence checks happen at compilation; character ordering and encoding use the
activation CCSID before calculations. Alternate data errors retain include
locations. Whole-array API buffers expose each member's own contiguous native
bytes, and updates to one member leave the other unchanged.

SORTA reorders only the named array, including when it is the alternate. IBM's
[SORTA definition](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-sorta-sort-array)
explicitly preserves the other array. CLEAR likewise applies only to its target.
RETURN retains mutations; LR and activation reclamation reload saved data.
Saved programs work after source removal.

## Evidence and remaining work

`RpgAlternatingArrayTests` covers both declaration orders and section modes,
inferred dimensions/constants, mixed formats, independent sorting/clearing,
sequence checks, space-padded numbers, exact 100-position records, unused final
pairs, absent data, include origins and invalid declarations. Native API byte
vectors run with CCSID 37 and 1208. `RpgAlternatingArrayIntegrationTests` covers
saved-source removal, actual job CCSIDs, RETURN/LR/reclaim and creation failures.
The display PTY compiles and calls RPGALTARR to check pair loading, independent
sorting and totals.

Unequal-dimension native acceptance, alternate-name section edge cases,
pre-runtime arrays, tables, DS-subfield compile-time arrays, complete fixed definitions,
overlap initialization, alternate collation, opaque/full multibyte source-CCSID
rules, full expression/entry/array ABI and independent native-system golden and
scale acceptance remain open. The matching-dimension limitation is this
implementation's supported contract. No performance target is claimed.

Verification (2026-10-17): all 3736 tests pass with zero failures/skips,
including 52 new cases; all 891 expanded focused checks pass. Locked restore,
zero-warning/error Release build, display PTY on both screen sizes, generated
inventory and whitespace checks pass. Full-suite runtime is 7m03s, not a
performance target. Schema is 31, command catalog 306 and checklist 36 complete /
70 open. C09 remains partial.

[Traditional D-spec standalone arrays](rpg-fixed-declarations.md) now accept ALT
and its main array's CTDATA/PERRCD, including forward definitions and continued
keywords. [Native DS definitions](rpg-fixed-structures.md) now use the same byte
engine; local/procedure definitions remain open.
