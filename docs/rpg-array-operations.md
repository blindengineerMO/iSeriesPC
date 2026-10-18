# Native RPG array operations

Free-form SORTA, ASCEND/DESCEND, %LOOKUP/%LOOKUPLT/%LOOKUPLE/%LOOKUPGT/%LOOKUPGE,
%XFOOT and %SUBARR operate on the native standalone and DS subfield arrays.
[%LOOKUPNE and traditional LOOKUP](rpg-array-search.md) now add sequential
not-equal searches and native factor/indicator/index semantics.
Supported [traditional C-specs](rpg-fixed-calculations.md) also expose SORTA
through extended factor 2 and XFOOT through the standard factor/result fields.
These extend the [standalone array contract](rpg-standalone-arrays.md) and
[native storage contract](rpg-native-structures.md); C09 remains partial.

```rpgle
**free
dcl-s NUM int(5) dim(5) ascend;
eval NUM(1)=9;
eval NUM(2)=3;
eval NUM(3)=7;
eval NUM(4)=1;
eval NUM(5)=5;
sorta NUM;
// %lookup(7:NUM) = 4; %lookuple(6:NUM) = 3; %xfoot(NUM) = 25
eval %subarr(NUM:2:2)=2;
// NUM now contains 1, 2, 2, 7, 9
return;
```

## Sorting and sequence

An array declaration accepts one argument-free ASCEND or DESCEND keyword.
SORTA uses that sequence, defaulting to ascending when neither is declared.
SORTA(A)/SORTA(D) select direction for an unsequenced array; an extender that
conflicts with a declared sequence is rejected with a source location.
Sequence metadata also survives LIKEDS and saved-program serialization.
See IBM's [SORTA rules](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-sorta-sort-array).

Character comparisons use unsigned native bytes in the activation's captured
CCSID. CCSID 37 and UTF-8 therefore sort `a`, `A`, `0` differently. Opaque CHAR
CCSID(*HEX) values retain arbitrary bytes without decoding. Numeric fields use
their numeric values. The existing indicator and default ISO date/time types
use their typed comparison rules.

Sorting an array overlay moves complete base elements, including bytes outside
the key. Chained overlays resolve to the base array. Selected DS array elements
and current occurrences retain their own storage; sorting a %SUBARR changes
only the selected elements. Keys and the reordered byte image are prepared
before publication, with a final cancellation check. Invalid numeric data leaves
the entire source unchanged and retains the native numeric status.

## Search and totals

%LOOKUP searches an unsequenced array sequentially. Declared ASCEND/DESCEND
arrays use a binary search; relative variants require that declared sequence.
Optional start/count arguments restrict the searched range. A miss returns zero;
the operations leave %FOUND and %EQUAL unchanged.

For exact matches, %LOOKUP/%LOOKUPLE/%LOOKUPGE return the first equal element
inside the requested range. Without an exact match, relative searches choose
the nearest qualifying value. Among duplicate nearest values, a less-than
search selects the last ascending/first descending element; a greater-than
search selects the first ascending/last descending element. See IBM's
[lookup result table](https://www.ibm.com/docs/en/i/7.4.0?topic=functions-lookupxx-look-up-array-element).

Numeric search arguments retain their own precision and scale. Character
arguments are encoded strictly in the captured CCSID and padded with native
blanks for comparison. Raw arguments require a matching CCSID. Incompatible
types report 105 and character conversion failures report 452. The caller must
supply data in the declared sequence; lookup does not sort it first.

%XFOOT accepts numeric arrays, supported array expressions and %SUBARR. Decimal
totals use exact scaled coefficients and preserve the common decimal scale;
unrepresentable results report 103 rather than silently dropping small addends.
Float totals add elements in index order and reject nonfinite results with 103.
The existing .NET decimal precision bound still applies; native RPG's full
63-digit result precision remains open. See %XFOOT in IBM's
[ILE RPG reference](https://www.ibm.com/docs/ssw_ibm_i_72/rzasd/sc092508.pdf).

## Subarrays and calls

%SUBARR(array:start{:count}) selects native elements. The default count extends
through the last element. EVAL supports it on either side, including scalar
broadcast, common-count assignment and array arithmetic. SORTA and %XFOOT accept
it, and runtime %ELEM/%SIZE/%LEN retain the selected view's metadata.

Start/count values require non-float numeric values with zero decimal positions;
incompatible values report 105. Start must be within the array and count must
be positive and fit the remaining range; violations report 121. %LOOKUP rejects
%SUBARR as its array operand: use lookup's start/count arguments instead.
See IBM's [%SUBARR restrictions](https://www.ibm.com/docs/en/i/7.4.0?topic=functions-subarr-setget-portion-array).

Subarray call expressions use detached copied values, including lazy host
borrowing, and host writeback does not mutate their source storage. They are
not exposed as writable API references. An ordinary indexed element whose index
is calculated from a subarray remains a writable element reference. Full native
array CONST/VALUE formal parameters remain open.

## Evidence and remaining work

`RpgArrayOperationTests` checks duplicate/range lookup, numeric search precision,
sort directions/dimensions, native collation and opaque bytes, overlay record
movement, LIKEDS, selected structure arrays/occurrences, subarray assignment,
totals/overflow, copied arguments and numeric/source diagnostics.
`RpgArrayOperationIntegrationTests` compiles saved programs, removes source and
runs numeric operations and character sorting in actual CCSID 37/1208 jobs.
The display PTY's RPGARRAYS program exercises SORTA, %LOOKUP, %XFOOT and writable
%SUBARR alongside native API buffers.

[Keyed DS array sorting and exact lookup](rpg-keyed-arrays.md) now support
DS(*).KEY, indexed scalar-array keys, %FIELDS and selected subarray ranges.

Nested DS key paths, ALTSEQ and other collating modes, pre-runtime table loading,
legacy semantic arrays, nested
subarrays, declaration-time slice queries, general array comparisons/conversions,
varying arrays, DS-subfield CTDATA and pre-runtime arrays, full expression typing and array entry/
procedure ABI remain open. Independent native-system acceptance, including
overlapping subarray assignments and overlay/extender combinations, remains
required. Existing EVAL reads use detached values; this checkpoint does not
establish native overlap behavior or duplicate-key sort stability guarantees.

Initial array-operation verification (2026-10-17): all 3533 tests pass, zero failures/skips,
including 75 new cases and all 574 expanded focused checks. Locked restore,
zero-warning/error Release solution build, display PTY, generated inventory
and whitespace checks pass. Full-suite runtime is 7m02s, not a performance
target. Schema remains 31, command catalog 306 and checklist 36 complete /
70 open. C09 remains partial.
