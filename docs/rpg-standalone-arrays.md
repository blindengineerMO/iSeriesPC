# Native standalone RPG arrays

Free-form DCL-S accepts DIM with a positive, zero-decimal numeric constant from
1 through 32767. Earlier named constants and supported declaration built-ins
may supply the dimension. Element types follow the existing CHAR, PACKED, ZONED,
INT, FLOAT, IND and default ISO date/time declarations. See IBM's
[DIM rules](https://www.ibm.com/docs/en/i/7.5.0?topic=keywords-dimautoctdatavarnumeric-constant)
and the [ILE RPG definition reference](https://www.ibm.com/docs/ssw_ibm_i_72/rzasd/sc092508.pdf).

```rpgle
**free
dcl-c COUNT 3;
dcl-s AMOUNTS packed(9:2) dim(COUNT) inz(1.25);
eval AMOUNTS=AMOUNTS+1;
eval AMOUNTS(%int(1.9))=7.50;
sndpgmmsg %char(AMOUNTS(1));
```

## Storage and operations

Every standalone array owns contiguous native bytes and uses the same typed
views as native DS subfields. An internal storage owner is not exported as an
RPG structure name. Initialization uses type defaults for every element; explicit
INZ initializes each element to the declared value. This differs from an ordinary
DS without INZ, whose numeric subfields retain blank storage. Packed/zoned,
signed integers, IEEE floats and character/indicator bytes retain their existing
native representations and the activation's captured CCSID.

EVAL broadcasts a scalar to every element. Array assignment and elementwise
arithmetic process the common element count and preserve the remaining target
elements. Reads return detached semantic arrays. CLEAR applies type defaults
rather than restoring explicit INZ. Whole-array conversion prepares a private
image before publication; errors or cancellation preserve the original array,
including cancellation during the final element's conversion. Ordinary text
assignment retains existing byte truncation behavior; complete CHARCOUNT modes
remain open. See [native storage rules](rpg-native-structures.md).

`%ELEM(array)` returns its dimension, including DIM(1). `%SIZE(array)` returns one
element's native byte width, `%SIZE(array:*ALL)` returns the entire allocation,
and `%LEN` returns the existing declared character length/numeric precision.
These values also work in later declaration constants. See IBM's
[%SIZE contract](https://www.ibm.com/docs/en/i/7.5.0?topic=functions-size-get-size-in-bytes).

Native array indexes require non-float numeric values with zero decimal
positions. Incompatible values report program status 105; out-of-range indexes
report 121 without first overflowing a narrower host integer. These checks also
apply to native DS/subfield arrays. `%INT` now truncates fractional digits rather
than rounding them, for numeric values and supported character numeric spellings.
Invalid character data reports 105 and signed integer overflow reports 103.
See IBM's [%INT rules](https://www.ibm.com/docs/en/i/7.5.0?topic=functions-int-convert-integer-format)
and [float index restriction](https://www.ibm.com/docs/de/ssw_ibm_i_74/rzasd/fltfmt9.htm).

## Calls and lifetime

Scoped native API arguments borrow a whole array as a raw CHAR buffer; an indexed
element retains its native type/precision. The whole buffer has zero decimal
positions, even when its elements are scaled numeric values. Compatible raw
CHAR CCSID(*HEX) scalar prototypes can borrow the full allocation. Repeated
element references share one cell, whole-array/element aliases share live bytes,
and range metadata retains partial-overlap protection. Actual index addresses
are frozen at call entry for lazy borrowing and copied host writeback.

Borrowed cells expire on return. Normal character API receivers validate cells
before a dequeue; opaque character arrays preserve arbitrary bytes. API argument
normalization, size budgets and captured-CCSID checks remain in force. Existing
copied-host calls receive detached semantic arrays and can return array values.
Retained RETURN keeps element storage, while LR and activation reclamation reset
it. Saved programs run after their source members have been removed.

## Bounds and remaining work

Native standalone arrays share the existing limit of 256 native storage
declarations, 1 MiB per declaration and 16 MiB per program with DS and standalone scalar allocations.
Final feedback expansion is included. Element widths, numeric precision and
integer/float initializer byte bounds remain checked before program creation.

Varying-dimension and pre-runtime arrays, FROMFILE/TOFILE,
native DS-subfield CTDATA, additional keyed/sort/search/collation operations, general
array comparisons/conversions, complete expression typing, full array
entry/procedure ABI, complete native fixed definitions, complete CHARCOUNT/CCSID modes
and independent native-system reference acceptance remain open. C09 stays partial.

`RpgStandaloneArrayTests` covers known bytes at CCSIDs 37/1208, defaults/INZ,
assignment/arithmetic, DIM(1), declaration constants, index and INT status,
cancellation, API aliases/prototypes/lifetime, copied writeback and queue
normalization before dequeue. `RpgStandaloneArrayIntegrationTests` verifies saved
QWCRSVAL calls with character input and integer-array output, retained RETURN,
LR/reclaim and rejected oversized programs in actual CCSID 37/1208 jobs.
The display PTY compiles/calls RPGARRAYS for array arithmetic, INT truncation and
native API buffers.

[Native array operations](rpg-array-operations.md) now add free SORTA,
ASCEND/DESCEND, five lookup variants, %XFOOT and %SUBARR. The linked contract
records the new subset and its remaining gaps.

[Compile-time arrays](rpg-compile-time-arrays.md) now add standalone CTDATA,
PERRCD, numeric external sign formats and inferred DIM(*CTDATA), preserving
literal data records and saved initialization values.


Standalone-storage verification (2026-10-17): all 3458 tests pass, zero failures/skips,
including 53 new cases and all 318 focused checks. Locked restore, zero-warning/
error Release build, expanded display PTY, generated inventory and whitespace
checks pass. Full-suite runtime is 7m02s, not a performance target. Schema is
31, command catalog 306 and checklist 36 complete / 70 open. C09 remains partial.

[Alternating compile-time arrays](rpg-alternating-arrays.md) now support ALT(main),
paired record loading, forward declarations and shared inferred counts. The two
arrays retain independent native storage, formats, ordering and lifecycle.

[Traditional standalone definitions](rpg-fixed-declarations.md) now map native
D-spec S fields/arrays and C constants into this storage/declaration pipeline,
including continued keywords, CTDATA/ALT and inferred counts.

[Standalone scalar declarations](rpg-native-scalars.md) now share this byte codec
and allocation budget, preserving scalar metadata and live entry references.
