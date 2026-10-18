# Native LIKE declarations and type ancestry

[Forward LIKEDS templates](rpg-forward-structures.md) support later layouts,
inherited initialization, structure parameters and returns.

Free and traditional native declarations accept LIKE for declared native
scalar types. It works on standalone fields, data-structure subfields, PR/PI
parameters and procedure returns. C09 remains partial.

The contract follows IBM's [LIKE reference](https://www.ibm.com/docs/en/i/7.4.0?topic=keywords-likename-length-adjustment)
and [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
pp. 491–492, plus OPTIONS(*EXACT), pp. 509–514. Independent IBM-system acceptance
remains open.

```rpgle
**free
dcl-s PRICE packed(7:2);
dcl-s UNIT_PRICE like(PRICE) inz(12.34);
dcl-s TOTAL like(PRICE:+2);
eval TOTAL=GET(UNIT_PRICE);
return;
dcl-proc GET;
  dcl-pi *n like(PRICE);
    P like(PRICE) const options(*exact);
  end-pi;
  return P;
end-proc;
```

TOTAL is packed(9:2), with value 12.34. UNIT_PRICE shares PRICE's declared type
ancestry; an independently declared packed(7:2) field would fail this exact
parameter check even if its current value and native bytes matched.

## Inherited attributes

LIKE inherits native kind, length, decimal positions and character opaque/text
mode. Supported kinds are CHAR, PACKED, ZONED, INT, FLOAT, IND and the existing
default ISO DATE/TIME/TIMESTAMP forms. It does not inherit initialization,
CONST, VALUE, STATIC, DIM, table/CTDATA status or ASCEND/DESCEND. New storage is
independent; assignment, initialization and CLEAR use the existing native codec.
An array requires an explicit DIM, which can use the supported declaration
constant `%elem(source)`.

Targets can be declared standalone fields and subfields (including a
qualified member or a member in the current DS), PI parameters, plain
structures, or native prototypes/procedures with return metadata. LIKE of an array
uses its element type. LIKE of a returning prototype uses its return element
type; return dimensions and RTNPARM are not inherited. LIKE of a structure
defines a character byte image of one structure, without cloning its subfields.
Use LIKEDS for a structure interface.

Opaque character types retain CCSID(*HEX)/65535 through LIKE chains. An explicit
CCSID(*DFT) selects this runtime's default job text CCSID; explicit *HEX/65535
selects opaque bytes. This does not add broader CCSID conversion or control-spec
CCSID defaults. Saved/API checks cover job CCSID 37 and 1208.

## Length adjustments and fixed columns

Free LIKE accepts a second argument with an explicit plus or minus sign, such
as `like(PRICE:+2)` or `like(NAME:-3)`. A supported nonnegative named declaration
constant can supply the magnitude. Character adjustments change byte width;
packed/zoned/integer adjustments change digits while preserving decimal places.
The result must satisfy the existing type bounds and integer digit sizes
3/5/10/20. Float, indicator and date/time types cannot be adjusted, including
an explicitly specified zero adjustment.

Traditional standalone, subfield, prototype-return and parameter declarations
use LIKE(name) in the keyword area and a right-justified signed adjustment in
the Length column. Type and decimal columns must be blank. Both supported column
origins work. Subfields use blank From columns; native OVERLAY, including the
existing structure-relative *NEXT mapping, retains shared positions. Fixed
positioned From/To LIKE declarations remain unsupported.

Declarations retain the existing character/numeric/dimension bounds, 1 MiB per
native declaration, 16 MiB variable storage, invocation/call limits and compiler
cancellation checks. Invalid or unsupported attributes reject before allocation.

## Exact ancestry and call behavior

Each source definition has an identity. LIKE chains share it; equal layouts from
independent definitions do not. Local shadowed names remain distinct. Subfield
identities survive LIKEDS clones and aggregate parameter views. Indexed array
actuals and declared procedure return metadata preserve their source identities.

With OPTIONS(*EXACT), a LIKE formal requires related declared data or compatible
return metadata, followed by the existing type/length/precision/dimension/order
checks. Literals and untyped expressions cannot establish that relationship.
Known incompatible call declarations reject during compilation; remaining
argument errors report status 105 through MONITOR before target dispatch.
Without *EXACT, ordinary layout compatibility and conversion rules still apply.

Resolved character LIKE parameters can combine CONST/VALUE with *TRIM/*RIGHTADJ,
and compatible reference types can use *VARSIZE. These options are checked after
resolving the inherited type. The normal readonly, lifetime, aliasing and atomic
response-validation rules apply across saved RPG, native API and process calls.
Type ancestry is a compiler/caller contract; process protocol 2 still transports
native values, rather than a general cross-language type system.

Tests exercise inheritance, adjusted layouts, raw bytes, independent defaults,
qualified/current subfields, overlays, fixed columns, array and return metadata,
shadowing, exact related/unrelated calls and invalid definitions. Saved-program
checks remove source and cover retained rebinding, related subfields, RTNPARM,
real queue input and failed object creation at CCSID 37/1208. Process tests cover
transformed readonly inputs, complete response validation and rejection before
launch. RPGLIKE supplies terminal compile/call acceptance.

[Forward type references and implicit returns](rpg-forward-declarations.md)
are supported with dependency/cycle checks. LIKE(*EXT), LIKE(object), enumerations, varying/graphic/UCS-2
and pointer types, nondefault date/time formats, LIKEREC lineage, foreign ABI and
module/service-program binding remain open. See
[declaration constants](rpg-declaration-constants.md),
[parameter options](rpg-parameter-options.md),
[aggregate parameters](rpg-aggregate-parameters.md) and
[return values](rpg-return-values.md).

Verification (2026-10-18): all 4977 solution tests pass with zero failures/skips,
including 64 new cases: 53 unit, eight saved-program/integration and three
external-process cases. All 1115 focused checks, locked restore, a Release build
with zero warnings/errors, RPGLIKE terminal acceptance, generated inventory,
local documentation links and whitespace checks pass. Full-suite wall time is
7m51s; no performance target is claimed. RPGLIKE runs at 24x80; general display
acceptance also covers 27x132. C09 and the overall checklist remain open.
