# Native fixed-form data structures

[Native LIKE declarations](rpg-like-declarations.md) add inherited scalar types,
signed length adjustments and related type identities for exact parameters.

Traditional D-spec DS definitions now use the same native byte storage as
[free-form structures](rpg-native-structures.md). Named, qualified and anonymous
structures support typed subfields, fillers, initialization, explicit positions,
overlays, arrays, LIKEDS, OCCURS, PSDS, INFDS and INDDS bindings. The older project
D-spec layout remains a separate extension. C09 remains partial.

The column and conversion rules follow the
[IBM ILE RPG reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf),
pp. 447, 452–457, 520–521 and 525. The packed-width formula is also described on
pp. 312–313. These references establish the source contract; emulator tests do
not replace independent native-system acceptance.

## Columns and implicit boundaries

| Columns | Structure header | Subfield |
|---|---|---|
| 1–5 | Blank or numeric sequence | Blank or numeric sequence |
| 6 | D | D |
| 7–21 | Name, or blank for anonymous | Name, or blank for filler |
| 22 | Blank; external descriptions remain open | Blank |
| 23 | Blank, or S for PSDS | Blank |
| 24–25 | DS | Blank |
| 26–32 | Blank | From byte position or reserved feedback word |
| 33–39 | Optional structure byte length | To byte position or element length |
| 40 | Blank | Supported internal type or default inference |
| 41–42 | Blank | Decimal positions for non-float numeric types |
| 43 | Blank | Blank |
| 44–80 | Supported structure keywords | Supported subfield keywords |
| 81 onward | Ignored commentary | Ignored commentary |

The existing one-column-shifted native convention shifts all these entries
together. Definitions require global declaration scope before calculations,
outside /FREE regions and subprocedures. Continued names and literals remain
unsupported. Keyword continuations leave columns 7–43 blank and may cross
comments, blank lines and expanded includes. Original source locations survive.

A structure ends at the next D specification with a definition type, the next
non-D specification or end of source. No END-DS is written in native source.
The compiler creates a separate declaration image with an implicit terminator;
the following real declaration or calculation retains its position and meaning.
Saved source metadata retains the original source and include maps.

```rpgle
     DDATA             DS                  QUALIFIED
     D  NUMBER                 1      3P 0 PACKEVEN INZ(1234)
     D  TEXT                          4A   INZ('ABCD')
     D  PART                          2A   OVERLAY(TEXT:2)
/free
  eval DATA.PART='xy';
  sndpgmmsg DATA.TEXT;
/end-free
```

NUMBER occupies three bytes with four digits; TEXT follows it and the program
prints `AxyD`. DS-level INZ is omitted so default overlay initialization does
not replace the explicitly initialized text.

## Lengths, placement and native bytes

With a blank From entry, To/Length specifies characters, numeric digits or float
bytes using the [traditional scalar rules](rpg-fixed-declarations.md). A blank
type with decimals defines a zoned subfield; standalone fields instead infer
packed storage. Numeric types require decimal positions, including zero for INT.
Without positioning, a subfield follows the largest extent of prior subfields.

With a numeric From entry, To is the inclusive ending byte position of an
element. CHAR/ZONED/FLOAT and default ISO datetime lengths use that byte width.
INT byte widths 1/2/4/8 map to digit lengths 3/5/10/20. For N packed bytes, the
digit length is 2N−1, or 2N−2 with PACKEVEN. PACKEVEN only applies to packed
From/To subfields. Packed precision retains the existing 29-digit/28-scale
limit; IBM's wider decimal precision remains open.

DIM repeats the element width, and subsequent subfields follow the complete
array extent. A TAB-prefixed subfield with DIM remains an array. DS DIM requires
QUALIFIED; OCCURS uses the existing current-occurrence rules. Explicit structure
length must contain every subfield and array element. Existing bounds remain
32767 array elements, 256 structures/standalone allocations, 1024 subfields,
1 MiB per allocation and 16 MiB per program.

Subfield OVERLAY uses the shared containment, inherited DIM, stride and *NEXT
rules. Native OVERLAY may also name the enclosing structure: a numeric position
selects its absolute byte position; *NEXT appends after the current extent.
From/To and OVERLAY cannot be combined. POS is free-form syntax; native source
uses From/To or OVERLAY.

INZ and subfield INZ use the common initialization order. DS-level INZ initializes
every subfield, including default values on overlays. Without INZ, untouched
bytes remain blanks; reading uninitialized numeric bytes can report 907. CLEAR
initializes the selected structure/occurrence. Conversion, overflow and native
API writes retain the shared atomic byte-image behavior.

Native arithmetic and XFOOT accept numeric scalar/array DS views, including
indexed DS-array elements. Result indicators use the stored native value. DO
counters and ENDDO steps can name numeric DS cells; indexed standalone counters
also write their actual element. Invalid character/array/scaled/float counters
and invalid qualification reject during compilation. Compiler-owned unnamed
counters keep their private storage path.

## Feedback, calls and activation

S in column 23 selects PSDS. Reserved entries in columns 26–39 bind the existing
feedback layouts; native *PARM maps to the parameter-count field. File feedback
words require INFDS. Native structures can also bind INDDS on WORKSTN with DDS
INDARA. Existing feedback lengths, field types and access rules apply.

Whole structures and selected subfields borrow native API cells with captured
addresses and storage ranges. Scalar subfields can pass live aliased arguments
to stored CL/RPG programs. RETURN retains bytes and occurrence selection;
LR/reclamation reload initialization and the first occurrence. See
[reference calls](rpg-tables.md#calls-and-captured-storage) for ABI boundaries.

## Evidence and remaining work

`RpgFixedStructureTests` covers both column origins, From/To byte vectors,
PACKEVEN, default zoning, fillers, layouts, strided overlays, arrays, LIKEDS,
OCCURS, PSDS, includes, trimmed/EOF records, commentary, bounds and diagnostics.
The integration cases remove saved source and run QWCRSVAL receivers, file/
program feedback, retained occurrences and CL aliases on return/error in actual
CCSID 37/1208 jobs. RPGFIXDS adds terminal compilation and execution of packed
precision, native integers, overlays and C-spec arithmetic.

External DS descriptions, data-area DS, nested/LIKEDS subfields, DS-subfield
CTDATA/ALT, local/procedure signatures, wider type/date-time/CCSID formats,
continued names/literals,
complete native entry/array/procedure ABI and independent native golden/scale
acceptance remain open. [Native program signatures](rpg-program-signatures.md)
now add PR/main PI and free CALLP(E), including parameter-processing error
boundaries. No performance target is claimed.


Verification (2026-10-18): all 4252 solution tests pass, zero failures/skips,
including 98 new cases (84 unit and 14 integration). The 325 final targeted
checks pass, and the full run includes 2567 RPG/CL/queue checks. Locked restore,
zero-warning/error Release build, final RPGFIXDS/display PTY, generated inventory,
documentation links and whitespace checks pass. RPGFIXDS runs at 24x80; general
display fixtures also cover 27x132. Full-suite runtime is 7m13s.
