# Native array and structure parameters

[Forward LIKEDS templates](rpg-forward-structures.md) support later layouts,
inherited initialization, structure parameters and returns.

Native PR/PI and free DCL-PR/DCL-PI accept fixed DIM arrays and LIKEDS
structures, including arrays of structures. Program calls and same-unit local
procedures share byte-backed reference binding. C09 remains partial.

The source rules follow the [IBM ILE RPG reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf):
array interface declarations, p. 121; LIKEDS parameters and character actuals,
p. 241; fixed and variable-size reference arguments, pp. 507–508; and VALUE,
p. 539. Independent IBM-system acceptance remains open.

```rpgle
**free
dcl-s NUMBERS int(3) dim(3) inz(7);
callp CHANGE(NUMBERS:NUMBERS(2));
return;

dcl-proc CHANGE;
  dcl-pi *n;
    A int(3) dim(2);
    B int(3);
  end-pi;
  eval A(2) = 10;
  eval B = B + A(2);
  return;
end-proc;
```

NUMBERS becomes 7, 20, 7. A exposes only the declared two-element prefix;
B aliases its second element. Indexed, qualified and nested calls refresh
the caller's current bytes before merging a write, preserving other elements
and bytes beyond the formal range.

## Declarations and compatibility

Array element types use the native scalar layouts: CHAR, PACKED, ZONED, signed
INT, FLOAT, IND and default ISO DATE/TIME/TIMESTAMP. Character arrays can use
CCSID(*HEX)/65535. Mutable typed array actuals require matching element type,
length, scale and opaque-byte mode, and at least the formal's dimension. A
larger array supplies a prefix. Short arrays reject during argument preparation
before target dispatch. Noncontiguous keyed columns cannot be borrowed as one
buffer. Parameter names beginning with TAB remain arrays.

A structure formal uses `P likeds(TEMPLATE);`; a structure array adds fixed
`dim(N)`. Templates must be plain native structures. The callee receives its own
qualified subfield layout, including overlays, byte positions and array
subfields. Structure reference actuals provide enough structure or character
storage; the formal layout interprets those bytes. Fixed structure-array
elements must have the formal byte width. Different actual subfield names do
not change byte compatibility. Multiple-occurrence structures, PSDS and INDDS
are not parameter templates. Explicit PR/PI matching checks structure layouts,
dimensions and passing modes, even when total byte widths happen to match.

Traditional parameter definitions support DIM and LIKEDS with both native
column origins. LIKEDS requires blank type/length/decimal columns; passing
keywords can precede it. Prototype names remain metadata outside caller
runtime storage. Main and local PI parameters allocate native images.

## Passing modes and call boundaries

Matching CONST aggregates remain live readonly aliases. VALUE aggregates in
local procedures always use private mutable copies. CONST/VALUE numeric array
conversions apply the formal scalar codec to each selected element before
dispatch. CONST structure literals use private character/hex images. SUBARR
inputs to CONST/VALUE use private images of the selected range. Mutable
SUBARR call operands remain unsupported. Assignment to SUBARR within a bound
parameter updates the original caller range.

OPTIONS(*NOPASS:*OMIT) retains the existing parameter-count and presence rules.
Absent aggregate roots, elements and subfields have no accessible storage.
CONST result/assignment guards cover indexed and qualified writes, and native
API descriptors preserve readonly status and expire at return.

Native API aggregate descriptors carry an immutable ProgramBuffer with the
exact formal byte length, CCSID and storage range. Optional element metadata
travels with interpreter bindings; the bytes remain the API/process payload.
Byte-buffer output validation checks width, CCSID and outer receiver constraints
before mutation. Typed values are decoded when RPG reads an element. CL character
variables can supply exact-width aggregate storage; typed character constants
bind the entire aggregate width within the existing CL constant bounds. Plain
host array values initialize private entry storage.

Process protocol 2 preserves aggregate bytes without Unicode conversion. The
existing process protocol rejects overlapping array/element or structure/field
arguments before launching. Every returned value validates before writeback,
so a malformed later output leaves earlier aggregate receivers unchanged.

## Bounds, verification and remaining work

Existing bounds remain: 255 native signature parameters, 32767 fixed elements,
1 MiB per aggregate/call, 256 native declarations and 16 MiB declared storage
per compilation unit. Local invocation and retained STATIC bounds still apply.
Bulk array reads refresh one image before decoding elements; bulk assignment
normalizes elements privately, validates the complete image and publishes once.
Maximum-dimension host-backed tests guard against refetching the full buffer
for every element.
Saved-program checks cover source removal, nested aliases, CL storage and
constants, structure-array VALUE and real QSNDDTAQ/QRCVDTAQ receivers at CCSID
37/1208. Unit checks cover native widths, templates/dimensions, readonly aliases,
copies, presence guards, keyed sorting, nested subarrays and descriptor expiry.
Real external processes check atomic response rejection and overlapping ranges.
RPGAGG supplies terminal compile/call acceptance.

Verification (2026-10-18): all 4716 solution tests pass with zero failures/skips,
including 73 new cases (60 unit, ten saved-program/integration and three
external-process cases). All 602 targeted checks, locked restore, a Release
build with zero warnings/errors, RPGAGG terminal acceptance, generated inventory,
local document links and whitespace checks pass. RPGAGG runs at 24x80; general
display acceptance also covers 27x132. Full-suite wall time is 7m40s; no
performance target is claimed.

[Fixed aggregate returns and RTNPARM](rpg-return-values.md) now support detached
array/structure results. [OPTIONS(*VARSIZE)](rpg-variable-parameters.md) now
supports actual-sized reference storage. Dynamic dimensions, general table entry
ABI, procedure pointers/foreign ABI, cross-unit
module/service-program binding and independent native golden/scale acceptance
remain open. See [local procedures](rpg-local-procedures.md) for other limits.
