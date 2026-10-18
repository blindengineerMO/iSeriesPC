# Native variable-size reference parameters

Native/free PR/PI accept OPTIONS(*VARSIZE) for reference character parameters,
plain LIKEDS structures and arrays of supported native types. It can combine
with CONST and OPTIONS(*NOPASS:*OMIT). VALUE is incompatible. C09 remains partial.

The source contract follows the [IBM ILE RPG reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
OPTIONS(*VARSIZE), pp. 507–509, and CONST, pp. 464–465. IBM permits shorter or
longer actual storage and requires the callee to determine how much data was
passed. Its search example uses actual character-array element widths and
explicit count/length parameters. Independent IBM-system acceptance remains open.

```rpgle
**free
dcl-s TEXT char(4) inz('ABCD');
callp CHANGE(TEXT:4);
return;

dcl-proc CHANGE;
  dcl-pi *n;
    P char(100) options(*varsize);
    N int(3) value;
  end-pi;
  eval %subst(P:2:N-2) = 'XY';
  return;
end-proc;
```

TEXT becomes AXYD. P retains its declared size of 100, while its address exposes
only the four actual caller bytes. It cannot read or write a fifth byte.

## Layout and actual storage

Character widths and array dimensions may differ from their prototypes. Native
character arrays also retain the actual element stride, so a CHAR(50) DIM(100)
interface can inspect a seven-element CHAR(1) actual using
`%subst(P(I):1:1)`. Numeric array element type, precision and scale must match for
mutable references. Scalar/array rank and opaque-character mode remain checked.
Named PR/PI declarations must agree on *VARSIZE as well as their declared layout.
Both supported traditional column origins retain these rules.

%LEN, %SIZE and %ELEM describe the declared fixed interface. Actual capacity is
separate; pass explicit lengths/counts when needed. Partial character reads and
writes use only their requested byte range. Numeric SUBARR operations and
short-array RHS assignment use the selected actual prefix and preserve other
elements. Full-value operations still require enough actual storage for the
bytes they access. Declared index violations report 121; access beyond actual
storage reports emulator status 105 before mutation. Inside a procedure,
unhandled failures retain the existing caller status-202 boundary.

Structures interpret their actual bytes using the callee's declared subfield
layout. A short character actual can supply the fields that fit. Subfields and
indexed elements retain bounded subviews; passing one scalar array element
does not expose the remainder of its containing array.

References remain live aliases. Fixed prefixes retain the original argument's
backing capacity when forwarded to a wider *VARSIZE interface. Compatible CONST
variables remain readonly aliases. Expressions and format conversions use
private formal images, including native defaults for missing converted array
elements. CONST/readonly memory cannot be written through a later API borrow.
Optional/omitted states and RTNPARM positions/counts retain their existing rules.

Byte-buffer character comparisons now blank-pad the shorter operand using its
CCSID, allowing the differently sized substrings in IBM's search example to
compare with ordinary RPG character semantics. Substring address expressions
are evaluated once.

## ABI, lifetimes and admission

ProgramArgumentMemory carries a storage identity, actual capacity, CCSID and
element metadata with checked read/validate/write callbacks. It exposes immutable
ProgramBuffer values. Slices preserve address identity, bounds, readonly status
and call lifetime. Native APIs receive exact actual-length descriptors; process
protocol 2 transfers the actual bytes. Overlapping process arguments reject
before launch, and all returned values validate before any receiver changes.

Saved RPG calls rebind actual storage on each activation call. CL variables and
explicit character/raw constants retain their actual byte lengths at variable
RPG entries. CL receiver-side variable layouts remain separate work. Plain host
character/array values create private actual-width images. At return, entry
state detaches into owned byte snapshots, retaining physical bounds and strides
without keeping expired caller cells.

The existing 1 MiB call limit now accounts for reachable backing capacities,
including capacity retained behind fixed prefixes, before dispatch. Direct native
entry admission enforces that bound too. Existing declaration, invocation,
parameter-count and fixed-dimension limits still apply. *VARSIZE does not make
the declared arrays dynamic or change the available scalar types.

[Exact and adjusted parameters](rpg-parameter-options.md) can combine *VARSIZE
with character transformations; *EXACT retains its type/dimension checks.

## Evidence and remaining work

Unit checks cover IBM's array-search pattern, partial accesses, aliases, backing
capacity forwarding, numeric conversions, actual/declared bounds, scalar-element
isolation, templates, optional/omitted/RTNPARM combinations, descriptors and
expiry. Core memory tests cover range rejection, slices, validation and readonly
lifetimes. Saved programs cover source removal, retained rebinding, CL variables
and constants, real queue receivers and readonly unprototyped API rejection at
CCSID 37/1208. Real processes cover actual-byte transfer, atomic malformed
responses and alias rejection. RPGVARSIZE supplies terminal compile/call acceptance.

Verification (2026-10-18): all 4841 solution tests pass with zero failures/skips,
including 52 new cases (41 unit/core-memory, eight saved-program/integration and
three external-process cases). All 812 focused checks, locked restore, a Release
build with zero warnings/errors, final RPGVARSIZE terminal acceptance, generated
inventory, documentation links and whitespace checks pass. RPGVARSIZE runs at
24x80; general display acceptance also covers 27x132. Full-suite wall time is
7m36s; no performance target is claimed.

VARYING/graphic/UCS-2 parameter types, dynamic arrays, other OPTIONS modes,
operational descriptors, general table entry ABI, procedure pointers/foreign ABI,
cross-unit module/service-program binding and independent native golden/scale
acceptance remain open. See [aggregate parameters](rpg-aggregate-parameters.md)
and [local procedures](rpg-local-procedures.md) for other limits.
