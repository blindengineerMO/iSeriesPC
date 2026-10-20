# RPG corresponding-field assignment

`EVAL-CORR target = source` assigns compatible subfields with the same local
program name and carries their null indicators. It is available in free source
and native fixed calculations using extended factor 2. Free source also supports
`EVAL-CORR(H)` for numeric half adjustment. This extends the
[native structure](rpg-native-structures.md), [nested layout](rpg-nested-structures.md)
and [null-map parameter](rpg-null-parameters.md) contracts. C09 remains partial.

```rpgle
**free
ctl-opt alwnull(*usrctl);
dcl-ds INPUTDATA qualified inz;
  AMOUNT packed(7:2) nullind;
  TEXT char(3);
end-ds;
dcl-ds OUTPUTDATA qualified inz;
  TEXT char(5);
  AMOUNT int(10) nullind;
  EXTRA char(2) inz('ZZ');
end-ds;
eval INPUTDATA.AMOUNT=12.75;
eval %nullind(INPUTDATA.AMOUNT)=*on;
eval INPUTDATA.TEXT='ABC';
eval-corr OUTPUTDATA=INPUTDATA;
// OUTPUTDATA.AMOUNT is 12 and its null indicator is on.
// OUTPUTDATA.TEXT is 'ABC  '; EXTRA remains 'ZZ'.
```

## Field selection and order

Operands must be native data structure variables, selected nested structures,
structure array elements or `%SUBARR` slices of structure arrays. Selected
subscripts are evaluated once, source before target. A source array requires a
target array; a scalar source can broadcast over a target array. Unequal arrays
assign their common prefix and preserve remaining target elements. Corresponding
subfields must both be arrays or both be scalars. An unused source-array tail is
not decoded. Multiple-occurrence operands use their respective current occurrence.

Fields match their internal names after PREFIX/EXTFLD renaming, independently of
external database identities and byte positions. Anonymous padding and unmatched
or incompatible fields are skipped. Numeric types correspond with each other;
character, indicator and matching date/time kinds follow native EVAL conversions.
Character lengths pad or truncate. Nested structures recurse by matching local
names; a nested structure does not correspond to a scalar field.

Assignments follow source declaration order, including destination overlays.
A later field conversion failure preserves completed earlier field assignments;
it leaves the failing field and its null flag unchanged. Conversion failures use
native monitor statuses, including 00103 for numeric overflow and 00907 for
invalid packed/zoned data. Null data is still decoded when its field is selected,
matching ordinary native movement rules.

Related identical layouts use a complete native image move, including padding
and overlays. The source image is captured before writing, preserving overlapping
array slices. The optimization checks layout, local names, ranks, offsets,
strides, types and null capabilities; sharing an external file family alone does
not qualify. Different extracts and prefixes retain field correspondence.

## Null indicators and borrowing

For each selected scalar/array leaf:

- A null-capable target copies the source flag when the source is null-capable.
- A null-capable target receives an off flag from a nonnullable source.
- A nonnullable target ignores source null attributes.

Unselected fields and extra target array elements retain their flags. Native
queries, named IND associations and borrowed `OPTIONS(*NULLIND)` maps all observe
the same changes. Explicit indicator fields also move as ordinary data when they
correspond, in source declaration order. Null indicators attached to an aggregate
itself are outside the current supported aggregate-indicator subset.

CONST targets reject at compilation. Runtime argument bounds and readonly named
or borrowed maps are checked before their associated data movement. Related
whole-image moves preflight their complete target map before publishing data.
Saved source-bound programs reconstruct correspondence from their saved layouts;
external description files and source members are not needed at call time when
the program has no runtime dependency on them. Nullable record output uses the
resulting target attributes normally.

## Remaining scope

The free H extender rounds selected integer/packed/zoned assignments away from
zero at a midpoint. Native fixed extenders, M/R precision modes and procedure
expressions returning structures are explicitly unsupported. Unsupported operands
or extenders fail compilation with original source locations.

Whole-array/aggregate `%NULLIND`, null-map INZ(*EXTDFT), divided nullable EXTFLD
arrays, broader varying/opaque and CCSID settings, multi-format/blocked I/O, ILE
linking and independent native golden/scale acceptance remain open. Schema 31,
catalog 306 and DISK 7/external description 2/WORKSTN 5 bindings are unchanged.

The implementation follows IBM's [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 852–858, including corresponding-field order, array rules, related
layout optimization and null-indicator propagation. The emulator's supported
operand and precision subset is described above.

Verification (2026-10-18): all 5844 solution tests pass with zero failures/skips
in 9m27s, including 53 new cases (39 native/compiler/ABI and 14 saved-program/
record cases). All 3588 broad and 53 final focused checks, locked restore, a
zero-warning/error Release build and expanded RPGCORR terminal acceptance pass.
Native and record cases cover CCSIDs 37/1208; general display acceptance covers
24x80 and 27x132. Generated inventory, local links and whitespace checks pass.
