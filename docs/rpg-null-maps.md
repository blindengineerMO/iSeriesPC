# Named RPG null indicators and external maps

`NULLIND(name)` now associates native variables with live indicator storage.
`LIKEREC` and `EXTNAME` accept `*NULL` extracts, providing indicator structures
for external database and display records. These extend the [ALWNULL foundation](rpg-null-values.md);
C09 remains partial; [parameter maps](rpg-null-parameters.md) now support native
procedures/stored RPG, while process sidebands and aggregate expressions stay open.

## Named variables and subfields

```rpgle
**free
ctl-opt alwnull(*usrctl);
dcl-s AMOUNT packed(9:2) nullind(AMOUNT_NULL);
dcl-s AMOUNT_NULL ind;
eval AMOUNT_NULL=*on;
if %nullind(AMOUNT);
  eval AMOUNT=12.50;
  eval %nullind(AMOUNT)=*off;
endif;
```

The named indicator and `%NULLIND` address the same storage in both directions.
Indicators can be declared after the data. A nullable array requires an indicator
array with matching dimensions. Captured array and structure indices are reused;
looking up a flag does not evaluate an index expression again.

A program-described subfield must use an indicator in the same structure, named
without qualification in the declaration. LIKEDS copies inherit these internal
relationships, including nested arrays. Scalar LIKE does not inherit null
capability or the named association. Standalone variables can also name visible
unqualified indicator subfields.

The compiler checks scope, automatic/STATIC lifetime, dimensions, indicator type,
nonnullable targets and unique ownership. Template relationships are checked too.
CONST and argument availability protections apply to the actual indicator storage.
Global associations use global storage when accessed from local procedures.
STATIC indicator bytes persist with other STATIC storage across calls and LR
restart in retained activations.

Named indicator subfields are ordinary data. Copying or borrowing a containing
structure includes their bytes, so changing them through an aggregate parameter
changes the caller's indicator. Automatic null flags retain the independent
formal state described by the earlier foundation. Copying a structure does not
implicitly copy a separately associated outer null map; LIKEDS also does not
inherit that outer map name.

## External indicator structures

```rpgle
**free
ctl-opt alwnull(*usrctl);
dcl-f ORDERS keyed qualified usage(*update);
dcl-ds BUFFER likerec(ORDERS.ORDERROW:*all) nullind(FLAGS) inz;
dcl-ds FLAGS likerec(ORDERS.ORDERROW:*all:*null) inz;
chain 100 ORDERS BUFFER;
if %found(ORDERS) and FLAGS.COMMENT;
  eval BUFFER.COMMENT='Reviewed';
  eval FLAGS.COMMENT=*off;
  update ORDERS.ORDERROW BUFFER;
endif;
seton LR;
```

`*NULL` can accompany INPUT, OUTPUT, ALL or KEY, in either order. With only
`*NULL`, INPUT is selected. Each selected external field becomes a one-byte IND,
including fields whose data is nonnullable. Database record maps follow original
field order; KEY maps follow key order. Display maps follow selected field order.
PREFIX and EXTFLD renames retain the original external identities, allowing a
map and its data buffer to use different prefixes.

A named structure association requires matching external family, extract, array
shape, occurrences and storage lifetime. Corresponding null-map fields must be
nonnullable indicators. A `*NULL` structure cannot be a record I/O buffer, even
when an all-indicator data record has the same byte length. OPTIONS(*EXACT) also
distinguishes the data and map roles.

Successful input updates only the selected data buffer and its map. Nonnullable
input clears the corresponding map bits; WORKSTN input clears only received
fields. Not-found and staged conversion failures preserve the receiver and map.
WRITE/UPDATE continue validating data before applying nullable flags. Named KEY
maps supply `%KDS` null attributes through the same live indicator storage.
Nested arrays and multiple-occurrence buffers select the corresponding map
position; this does not change the map's current occurrence selection.

Null maps can be used as ordinary native templates, aggregate parameters and
return values. This passes their IND bytes as data, without enabling automatic
null-map parameter passing.

## Saved programs and remaining work

Source-bound DISK version 7 and external-description version 2 metadata retain
the physical descriptions. Recompiling saved source reconstructs the data/map
roles and named relationships without consulting removed description files.
Existing strict JSON, source hashes, family checks and metadata/storage bounds
remain active. WORKSTN metadata versions, catalog 306 and schema 31 are unchanged.

Native formal associations and OPTIONS(*NULLIND) now use
[null-map parameters](rpg-null-parameters.md). Process sidebands,
aggregate/whole-array `%NULLIND`, INZ(*EXTDFT) for null maps,
divided nullable EXTFLD arrays and broader EVAL-CORR precision/procedure expressions remain open. Broader external/CCSID
settings, varying/opaque database layouts, multi-format I/O, blocked I/O, ILE
linking and independent native-system golden/scale acceptance remain open.

The contracts follow IBM's [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 478–479 and 499–500 (external extracts), 501–503 (NULLIND),
517 (parameter maps), and 331–337 (record null handling).

Verification (2026-10-18): all 5715 solution tests pass with zero failures/skips
in 9m16s, including 48 new cases (28 native/control/metadata/ABI and 20 record/
integration cases). All 3459 broad and 160 final focused checks, locked restore,
a zero-warning/error Release build, expanded RPGNMAP terminal acceptance, generated
inventory, local links and whitespace checks pass. Record cases cover CCSIDs
37/1208; terminal display acceptance covers 24x80 and 27x132.

[Corresponding-field assignment](rpg-corresponding-assignment.md) now transfers
selected native fields and null indicators, including nested/array and borrowed
maps. Its broader precision/procedure extensions remain open.
