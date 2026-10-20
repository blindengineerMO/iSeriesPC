# RPG null-map parameters

Native procedures and stored RPG programs now share caller null indicators through
`OPTIONS(*NULLIND)`. A parameter can instead name another indicator or external
indicator-map parameter with `NULLIND(name)`. These extend the [native null](rpg-null-values.md)
and [named map](rpg-null-maps.md) foundations; C09 remains partial.

## Borrowed maps

```rpgle
**free
ctl-opt alwnull(*usrctl);
dcl-s AMOUNT packed(9:2) nullind;
eval %nullind(AMOUNT)=*on;
callp FILL(AMOUNT);
return;
dcl-proc FILL;
dcl-pi *n;
  P packed(9:2) options(*nullind);
end-pi;
if %nullind(P);
  eval P=12.50;
  eval %nullind(P)=*off;
endif;
end-proc;
```

The parameter shares data and a live map. Queries and writes reach the caller's
automatic or named indicators, including arrays, nested structures, aliases and
forwarded parameters. CONST shares readable indicators while preventing writes.
A scalar/array formal becomes null-capable under this option. A nonnullable
scalar/array actual receives a temporary, initially-off map for that call;
its data remains a live reference.

ALWNULL(*USRCTL) is required. Only *NOPASS and *OMIT may accompany *NULLIND;
VALUE, NULLIND(name), VARSIZE, EXACT, TRIM and RIGHTADJ combinations reject.
Actuals must be exact variables even for CONST: type, length, precision,
dimensions, order and related structure definitions are checked. Structure null
capabilities must match too. Literals, expressions, conversion temporaries and
noncontiguous whole-array references are not accepted. Selected indices are
captured once. Anonymous fields and empty LEN-defined structures retain their
native data layout; an empty structure has an empty map.

Omitted/unpassed arguments preserve their existing presence checks. Missing or
malformed maps reject before callee code executes. Prototype/PI map modes and
capability layouts must agree.

## Explicit indicator parameters

```rpgle
**free
ctl-opt alwnull(*usrctl);
dcl-s AMOUNT packed(9:2);
dcl-s ISNULL ind inz(*on);
callp FILL(AMOUNT:ISNULL);
return;
dcl-proc FILL;
dcl-pi *n;
  P packed(9:2) nullind(F);
  F ind;
end-pi;
if %nullind(P);
  eval P=12.50;
  eval F=*off;
endif;
end-proc;
```

A formal NULLIND name must identify another parameter in the same signature.
The target must be nonnullable, uniquely associated and have matching dimensions;
a structure target must be the corresponding external *NULL extract. Anonymous,
missing or ambiguous prototype names reject as association targets. PR and PI
names may differ, but their association positions must agree.

Explicit parameters pass their indicator bytes as ordinary data. The data
actual's own automatic map is independent unless it is itself used to supply the
indicator argument. Input into a data parameter updates its explicitly supplied
indicator parameter. Readonly map storage is preflighted before record input,
so an error preserves the data receiver and map.

## Runtime and host boundaries

Live map entries follow declaration order and array element order. Native memory
views retain physical strides and bound selected cells to their declared width.
This permits nullable overlay subfields inside a borrowed structure without
exposing the padding or neighboring array cells as part of a scalar argument.

`ProgramArgument.NullMap` exposes bounded, capability-aware boolean reads,
validation and writes to aware synchronous hosts. Call scopes invalidate retained
map access. Data plus map entries share the existing 1 MiB call limit; entry map
shape/capabilities are checked before execution. Native frame cleanup snapshots
maps into private storage alongside detached data. Retained activations rebind
fresh maps on each call, preserving ordinary LR and STATIC rules.

Registered process protocols 1/2 do not carry null-map sidebands and now reject
such calls before starting a process. Built-in APIs without a map contract also
reject them. Ordinary calls and separately passed indicator data retain their
existing behavior. Registered [protocol 3 process calls](rpg-process-null-maps.md) carry strict
data-aligned map sidebands. Broader foreign-language ABI acceptance remains open;
maps are never silently dropped.

Source-bound saved programs reconstruct parameter options and named associations
from saved source/descriptions. Schema 31, catalog 306 and existing binding
versions remain unchanged.

Aggregate/whole-array `%NULLIND`, null-map INZ(*EXTDFT), divided nullable EXTFLD
arrays and broader EVAL-CORR precision/procedure expressions remain open. Broader external/CCSID settings, varying/opaque
database layouts, multi-format/blocked I/O, ILE linking and independent native
system golden/scale acceptance also remain open.

The contracts follow IBM's [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 501–503 (named indicator parameters) and 517–519 (OPTIONS(*NULLIND)).
The temporary map for a nonnullable scalar actual is the emulator's scoped
representation of its otherwise unobservable null state.

Verification (2026-10-18): all 5761 solution tests pass with zero failures/skips
in 9m18s, including 46 new cases (30 native/compiler, eight core/host, six
record/integration and two process-rejection cases). All 3505 broad and 174
final focused checks, locked restore, a zero-warning/error Release build, expanded
RPGNPRM terminal acceptance, generated inventory, local links and whitespace
checks pass. Native/record acceptance covers CCSIDs 37/1208; terminal display
acceptance covers 24x80 and 27x132.

[Corresponding-field assignment](rpg-corresponding-assignment.md) now transfers
selected native fields and null indicators, including nested/array and borrowed
maps. Its broader precision/procedure extensions remain open.
