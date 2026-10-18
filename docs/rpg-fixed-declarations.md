# Traditional RPG standalone definitions and arrays

[Native LIKE declarations](rpg-like-declarations.md) add inherited scalar types,
signed length adjustments and related type identities for exact parameters.

Traditional D-spec standalone fields, arrays and named constants now enter the
same declaration pipeline as free declarations. Arrays use the existing native
storage and support runtime DIM/INZ, CTDATA/PERRCD/EXTFMT, ALT, inferred counts,
SORTA, lookup, totals, subarrays and API views. C09 remains partial.

```rpgle
     DMAIN             S              2    DIM(2) PERRCD(2)
     D                                     CTDATA
     DOTHER            S              3P 1 DIM(2) ALT(MAIN) EXTFMT(L)
     DCOUNT            C                   %ELEM(MAIN)
/free
  sorta MAIN;
  sndpgmmsg %char(%xfoot(OTHER));
  seton LR;
/end-free
**CTDATA MAIN
BB+010AA+020
```

This sorts MAIN to AA/BB while OTHER retains 1/2. See the
[standalone storage](rpg-standalone-arrays.md),
[compile-time data](rpg-compile-time-arrays.md) and
[alternating array](rpg-alternating-arrays.md) contracts for their runtime rules.

## Physical columns

The native origin has D in column 6. The project also accepts its existing
one-column-shifted convention. All fields, keyword boundaries and commentary
shift together; this does not change the older D-spec layout used by existing
programs and prototypes. The layout follows IBM's traditional definition and
keyword-continuation sections in the
[ILE RPG reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf).

| Columns | Supported entry |
|---|---|
| 1–5 | Blank or numeric sequence text |
| 6 | D |
| 7–21 | Name, within the existing identifier grammar |
| 22–23 | Blank external/data-structure attributes |
| 24–25 | S standalone or C constant |
| 26–32 | Blank From position |
| 33–39 | Right-justified length/digits when required |
| 40 | Internal type, or inferred character/packed type |
| 41–42 | Right-justified decimal positions for non-float numeric types |
| 43 | Blank |
| 44–80 | Supported declaration keywords or constant value |
| 81 onward | Commentary, excluded from code and quote processing |

Keyword continuation lines repeat D and leave columns 7–43 blank. Empty and
comment lines and expanded includes may intervene. Fragments are joined with
whitespace, with bounded source accounting and cancellation checks. Splitting
quoted literals across fixed lines rejects explicitly until the native +/-
literal-continuation rules are implemented. A dangling continuation cannot
create an implicit field.

Column-7 comment markers, including D* comments, bypass quote/directive handling;
the shifted convention uses column 8. Commentary apostrophes and slash sequences
cannot hide CTDATA markers. Literal slashes and doubled apostrophes inside code
strings remain values. Original source snapshots and include origins are retained.

## Types and declaration behavior

A, or a blank type with blank decimals, defines CHAR. P, or a blank type with
numeric decimals, defines PACKED; S defines ZONED. I maps signed integer digit
sizes 3/5/10/20 to 1/2/4/8 native bytes. F accepts 4/8-byte floats with blank decimal
columns. N accepts indicators; D/T/Z accept the default ISO date/time/timestamp
lengths, either implied or explicitly 10/8/26. Non-float numeric definitions
require decimal positions, including zero for I. Inconsistent columns and
unsupported options reject compilation at the declaration origin.

C definitions accept a literal, CONST(value), an earlier constant or a supported
layout built-in. Their length/type/decimal columns must be blank. Values are
immutable and share the existing constant limits and pure evaluation rules.
Signed integer initializers must fit their declared native byte width, including
scalar fields. Fixed and free declarations share names and physical declaration order in the
project's existing mixed-source convention. Duplicate declarations reject.
Traditional native definitions require global scope before calculations and
cannot appear inside /FREE calculations or a subprocedure.

Runtime arrays and compile-time arrays use the same dimension, native allocation,
initializer and keyword limits as free arrays. Named/bare data sections bind
normally. Either ALT declaration can come first; matching DIM(*CTDATA) counts
are available to later C constants. Whole API buffers have native element bytes.
CLEAR, RETURN, LR and reclamation retain the existing storage contracts. Saved
programs execute after their source members are removed.

[Native scalar storage](rpg-native-scalars.md) now extends the same byte engine
to non-array S definitions. Scalars retain scalar identity and gain native
conversion/overflow, byte-based character assignment and live borrowed entry
bindings. Complete expression typing, legacy/imported-field unification and
further CCSID/ABI modes remain open.

## Evidence and remaining work

`RpgFixedDeclarationTests` checks both column origins, type byte vectors,
constants, default type inference, INZ, continuations, mixed declarations,
CTDATA/ALT, inferred dimensions, comment/quote state, includes and rejection
paths. `RpgFixedDeclarationIntegrationTests` checks actual CCSID 37/1208 jobs,
saved-source removal, independent sorting, native QWCRSVAL input/receivers,
RETURN/LR/reclaim and failed program creation. RPGFIXARR in the terminal fixture
checks native columns, a continuation, layout constants and alternating data.

[Native calculation specifications](rpg-fixed-calculations.md) now support the
documented arithmetic, control-flow, array, call and declared file subset.
[Native DS definitions](rpg-fixed-structures.md) now share the byte engine for
subfields, positions, overlays, arrays, occurrences and feedback.
[Native program PR/PI](rpg-program-signatures.md) now supports global scalar
reference signatures. Local/procedure definitions and remaining fixed calculation forms,
continued names/literals, nonnumeric sequence commentary, B/U/graphic/UCS-2/
object/pointer formats, float decimal editing, extended date/time formats,
LIKE/LEN and further declaration keywords, pre-runtime FROMFILE/TOFILE,
DS-subfield CTDATA, full expression/entry/array ABI and independent native-system
golden/scale acceptance remain open. This does not claim complete fixed-form or
fully free-form source compatibility. No performance target is claimed.

Verification (2026-10-17): all 3821 tests pass with zero failures/skips,
including 85 new cases; all 982 expanded focused checks pass. Locked restore,
zero-warning/error Release build, display PTY on both screen sizes, generated
inventory and whitespace checks pass. Full-suite runtime is 7m39s, not a
performance target. Schema is 31, command catalog 306 and checklist 36 complete /
70 open. C09 remains partial.
