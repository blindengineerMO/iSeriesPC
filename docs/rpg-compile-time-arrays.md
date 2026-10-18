# Native RPG compile-time arrays

Free standalone declarations now support CTDATA, PERRCD, numeric EXTFMT(S/L/R),
inferred DIM(*CTDATA), and [alternating ALT pairs](rpg-alternating-arrays.md). They use the existing
[native array storage](rpg-standalone-arrays.md), operations and API views.
C09 remains partial.

```rpgle
**free
dcl-s NAMES char(7) dim(*ctdata);
dcl-c COUNT %elem(NAMES);
dcl-s VALUES packed(5:2) dim(COUNT) ctdata perrcd(2) extfmt(L);
if %xfoot(VALUES)=2.5;
  sndpgmmsg NAMES(1);
endif;
seton LR;
**CTDATA NAMES
FIRST
SECOND
**CTDATA VALUES
+00125+00125
```

## Declaration and record rules

CTDATA requires DIM and cannot be combined with INZ. PERRCD defaults to one;
earlier supported declaration constants/functions can supply its positive,
zero-decimal count. DIM(*CTDATA) implies CTDATA, uses the physical data-record
count and requires PERRCD(1). Its named sections must precede fixed-dimension
data. The inferred dimension is available to later %ELEM/layout constants.
See IBM's [DIM rules](https://www.ibm.com/docs/en/i/7.5.0?topic=keywords-dimautoctdatavarnumeric-constant)
and [ILE RPG reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf).

Named `**CTDATA name` sections bind by name. Bare `**` sections bind in declaration
order; these techniques cannot be mixed. Entries begin in column 1 and occupy
fixed adjacent slots. Missing positions are blank-padded; absent entries retain
type defaults. Unused slots in the final partial record must be blank. Text
after all PERRCD slots is ignored as commentary. Excess records, duplicate or
unknown sections, unsupported program-data headers and invalid values reject
compilation with original source/include locations. Entry width times PERRCD is
bounded to 100 source positions. See IBM's
[source-record rules](https://www.ibm.com/docs/en/i/7.5.0?topic=array-rules-source-records).

The trailing data section bypasses directive/comment processing. `/COPY`, `//`,
apostrophes and fixed-comment-looking text remain literal data. Named markers
inside multiline code literals remain code text. Includes preserve physical
record origins; the synthetic line after a terminal newline does not add an
inferred entry. A real blank data record does. Original data remains in the
saved source snapshot and its signature.

## Values, bytes and initialization

Numeric S data uses space-padded digit strings and EBCDIC-style zoned sign characters; L/R
requires a leading/trailing explicit sign. Decimal positions are implied by the
declaration. Float entries use 14 positions for FLOAT(4), 23 for FLOAT(8), with
finite-value checks. EXTFMT is forbidden on floats. Indicators use 0/1, and
supported date/time/timestamp fields use the default ISO source formats.
Precision, native integer widths and float bounds are checked before creation.

Character entries retain their raw text and use the activation's captured CCSID
and existing CHAR byte-width/truncation behavior. Opaque CCSID(*HEX) CTDATA is
rejected until the complete source-CCSID byte contract is implemented. Numeric
and other noncharacter sequence declarations are checked during compilation,
including default tails. Character sequence checks use native CCSID bytes during
activation initialization; a failure reports 1052 with its data-record location.
Encoding failures report 452 before calculations execute.

Runtime loading uses validated initializers and native element storage. Borrowed
API buffers, aliases and layout queries retain their normal array contracts.
CLEAR applies type defaults. RETURN retains mutations, while LR/reclamation
create a fresh initialized image. Saved programs load after source removal.

## Evidence and remaining work

`RpgCompileTimeArrayTests` covers record/marker handling, signs, native byte
vectors, inferred counts/constants, scalar types, defaults, CLEAR, includes,
source errors, sequence checks and initialization encoding failures.
`RpgCompileTimeArrayIntegrationTests` covers saved programs/source removal,
actual CCSID 37/1208 jobs, RETURN/LR/reclaim and creation failures. The display
PTY compiles/calls RPGCTDATA for inferred dimensions, signed PERRCD entries,
totals and directive-looking literal data.

CTDATA in DS subfields and
overlap initialization order, FROMFILE/TOFILE pre-runtime loading/output, tables,
graphic/UCS-2/varying/unsigned formats, opaque and complete multibyte source-CCSID
column rules, extended date/time formats, additional declaration/array built-ins,
full entry/procedure ABI and independent native-system golden/scale acceptance
remain open. Existing source, declaration, dimension and native allocation
bounds continue to apply; no performance target is claimed.

Final verification (2026-10-17): all 3684 tests pass, zero failures/skips.
There are 65 new cases and one obsolete rejection retired (net +64); all
839 expanded focused checks pass. Locked restore, zero-warning/error Release
build, display PTY, generated inventory and whitespace checks pass. The existing
queue revocation fixture now observes a dedicated cancellable waiter and checks
retained data, eliminating its four-second expiry assumption. Full-suite runtime
is 7m20s, not a performance target. Schema remains 31, command catalog 306
and checklist 36 complete / 70 open. C09 remains partial.

[Traditional D-spec declarations](rpg-fixed-declarations.md) now support the
standalone CTDATA/PERRCD/EXTFMT/ALT forms, native type columns and keyword
continuations, using the same loader and saved initializers.
