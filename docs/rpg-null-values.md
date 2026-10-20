# RPG null values

Native RPG programs can read and write nullable database fields, inspect their
null indicators, and search nullable keys. This is a partial C08/C09 increment;
named indicators, external maps and [native parameter maps](rpg-null-parameters.md)
are supported; process sidebands and aggregate indicator expressions remain open.

## Control and native storage

Specify one global `CTL-OPT ALWNULL(...)` before declarations, or `ALWNULL(...)`
on an H specification in traditional source. The supported modes are:

| Mode | Input | Output and keys |
| --- | --- | --- |
| `*NO` (default) | A row containing NULL raises file status 1299 before changing receiver data or indicators. Non-null rows are accepted. | Ordinary non-null values remain usable. |
| `*INPUTONLY` | NULL becomes the live file's DDS default, or the system type default. | Nullable record WRITE/UPDATE and nullable key searches reject at compilation. |
| `*USRCTL` | Native field indicators reflect the record's null attributes. | Indicators control nullable WRITE/UPDATE and key searches. |

Argument-free `NULLIND` defines a null-capable scalar or array under `*USRCTL`.
Nullable imported database fields and `LIKEREC`/`EXTNAME` subfields acquire that
capability automatically. `LIKEDS` retains subfield capability; scalar `LIKE`
does not inherit it. `%NULLIND(field)` queries or sets one scalar or indexed leaf:

```rpgle
**free
ctl-opt alwnull(*usrctl);
dcl-f ORDERS keyed qualified usage(*update);
dcl-ds KEYS likerec(ORDERS.ORDERROW:*key) inz;
dcl-ds BUFFER likerec(ORDERS.ORDERROW:*all) inz;
eval %nullind(KEYS.CUSTOMER)=*on;
chain %kds(KEYS) ORDERS BUFFER;
if %found(ORDERS) and %nullind(BUFFER.COMMENT);
  eval BUFFER.COMMENT='Reviewed';
  eval %nullind(BUFFER.COMMENT)=*off;
  update ORDERS.ORDERROW BUFFER;
endif;
seton LR;
```

Indicators start off and occupy separate runtime state. Setting an indicator
leaves data unchanged. Ordinary assignment, arithmetic, comparisons, array
operations and CLEAR retain indicator settings. They neither propagate nor move
indicators with the data. Arrays, nested structures and multiple occurrences
have independent flags at each selected position. Global flags share module
scope; procedure automatic flags restart on each invocation. Procedure STATIC
flags persist with STATIC bytes, including module cycle restart in a retained
activation group.

Ordinary aggregate parameters borrow data while using independent automatic
formal null indicators. Named indicator subfields travel as ordinary data. They do not implicitly pass a null map. CONST/read-only and missing
argument protections apply to indicator operations.

## Record input, output and keys

Implicit input updates imported field data and indicators. Result-structure
input updates only the selected buffer and its subfields. A not-found operation
changes neither. Conversion is staged before publishing input; errors invalidate
the current record and release its update lock. Successful non-null input turns
off a previously set indicator, including input from a nonnullable description
sharing the same field.

The SQLite record representation keeps NULL separately from its value. For
`*USRCTL`, NULL input supplies a deterministic system-default data placeholder
and sets its indicator. For `*INPUTONLY`, defaults come from the live runtime
file, so changing a DDS default does not require recompilation. Alpha, zoned,
packed, binary, floating, logic, date, time and timestamp fields are supported
within the existing native layout limits. Character input must fit its native
encoded byte width.

WRITE/UPDATE decode and validate data before applying indicators. Invalid packed
data still fails when its null indicator is on. A nullable destination receives
NULL only when its corresponding indicator is on. Nonnullable destinations
ignore program null indicators.

Scalar and expression-list search components use native field indicators;
`%KDS` uses the selected subfields' flags. Traditional KFLD accepts a scalar
indicator in factor 2 under `*USRCTL`. Its explicit indicator controls that key
component independently of the data field's own indicator. Native key views are
captured once so indexed operands are not reevaluated to query their flags.
Singleton EXTFLD arrays route the selected element indicator through implicit
and result-buffer I/O. Existing cursor ordering, partial keys and member/RRN tie
breakers still apply.
Procedure-local and borrowed files use the executing program's control mode and
result-buffer flags.

## Saved programs and limits

`ipc.rpg.diskfiles` version 7 records nullable field attributes;
`ipc.rpg.externalstructures` version 2 does the same for EXTNAME descriptions.
Versions 1–6 and 1 respectively remain readable for nonnullable descriptions.
False attributes are omitted so existing nonnullable layout signatures remain
stable. Older envelopes reject nullable attributes. Source hashes, strict JSON,
layout/family verification and bounded metadata writing remain active. Actual
runtime formats, including nullability, are checked when files open.

Source members and separate description files may be removed after compilation.
Existing field, declaration, nesting, dimension and storage bounds still apply.

Named variables and `LIKEREC`/`EXTNAME *NULL` extracts now use
[named indicators and maps](rpg-null-maps.md). [Native formal-parameter maps](rpg-null-parameters.md) are supported; process
sidebands, aggregate/whole-array `%NULLIND`, and divided
nullable EXTFLD arrays remain open and reject explicitly. EVAL-CORR precision/procedure extensions, varying or
opaque database fields, multi-format file I/O, wider CCSID/external settings,
blocked I/O, ILE linking and independent native golden/scale acceptance remain
open. No native parity or performance target is claimed.

The contracts follow IBM's [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 331–337 (database null support), 368 (ALWNULL), 501 (NULLIND),
517 (parameter null maps), and 732 (%NULLIND).

Verification (2026-10-18): all 5667 solution tests pass with zero failures/skips
in 9m09s, including 65 new cases (15 native/control/metadata and 50 database/
integration/metadata cases). All 3409 broad and 292 final focused checks, locked
restore, a zero-warning/error Release build, RPGNULL terminal acceptance, generated
inventory, local links and whitespace checks pass. Record acceptance covers
CCSIDs 37/1208; terminal display acceptance covers 24x80 and 27x132.

[Corresponding-field assignment](rpg-corresponding-assignment.md) now transfers
selected native fields and null indicators, including nested/array and borrowed
maps. Its broader precision/procedure extensions remain open.
