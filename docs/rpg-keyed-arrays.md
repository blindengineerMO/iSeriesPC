# Keyed native RPG data structure arrays

Native DS arrays now support free SORTA with a scalar subfield key,
an indexed scalar-array subfield key, or a list of scalar keys. Exact %LOOKUP
supports the keyed form too. [%LOOKUPNE](rpg-array-search.md) now supports the
same key forms with a sequential first-unequal search. These extend the
[array operations contract](rpg-array-operations.md); C09 remains partial.

```rpgle
**free
dcl-ds EMP qualified dim(3) inz;
  NAME char(6);
  PAY packed(7:2);
end-ds;
eval EMP(1).NAME='Pablo';
eval EMP(1).PAY=1200;
eval EMP(2).NAME='Bill';
eval EMP(2).PAY=1100;
eval EMP(3).NAME='Alex';
eval EMP(3).PAY=1200;
sorta EMP %fields(PAY:NAME);
// Bill, Alex, Pablo; names move together with their pay fields.
sorta(d) %subarr(EMP(*).NAME:2:2);
// Bill, Pablo, Alex; only the final two records move.
sndpgmmsg %char(%lookup('Alex':EMP(*).NAME));
return;
```

## Sort operands

`SORTA DS(*).KEY` sorts complete DS elements using scalar KEY. An array subfield
must have an explicit index, such as `DS(*).CODES(I)`. Its index is resolved
once before sorting. The key's own ASCEND/DESCEND metadata does not determine
the DS-array sort order: default/A is ascending, D is descending.

`SORTA DS %FIELDS(KEY1:KEY2)` compares keys in listed priority order and proceeds
to the next key only on a tie. The operation's direction applies to every key.
%FIELDS accepts a colon-separated list of case-insensitive, unqualified scalar
subfield names; qualified names, arrays, comma-separated lists,
indexed arrays, literals and expressions are rejected at compilation with the
original source location. This implementation bounds the list to 1024 keys.
See IBM's [SORTA rules](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-sorta-sort-array)
and [%FIELDS restrictions](https://www.ibm.com/docs/en/i/7.4?topic=fields-subfields-sorting).

Both keyed forms work with %SUBARR, including a default count through the end
of the array. Start/count retain the existing numeric type and 105/121 bounds
rules. A DS array without a key/%FIELDS is rejected at compilation. Named native
DS arrays, including DIM(1) and LIKEDS-derived arrays, retain their byte layouts.
Nested DS declarations and complex nested keyed paths remain open.

## Storage and comparisons

The sort moves the entire DS element width, including gaps, unused fields,
opaque bytes and uninitialized numeric fields outside the keys. It does not
convert nonkey data. Native character keys compare CCSID bytes; numeric,
indicator and supported ISO date/time keys compare typed values. Tests include
the different CCSID 37/1208 character orders, one-byte signed integers, zoned,
floating point, indicators and date/time/timestamps.

Sorting prepares an index order and private byte image before publication.
Conversion errors encountered in keys and cancellation leave the original array
unchanged. The final image has the existing native storage bound. Multiple-key
comparison reads secondary keys on ties instead of allocating a count-by-key
object matrix, which could grow far beyond native bytes for overlapping keys.
Duplicate-key relative stability is not an independent native acceptance claim.

## Lookup and restricted contexts

`%LOOKUP(value:DS(*).KEY{:start{:count}})` searches sequentially and returns the
first exact match inside the requested range, or zero. Indexed scalar-array
keys are supported. Numeric search precision and native character padding follow
the existing array operation rules. Key-array sequence metadata does not turn
this search into a binary search.

Relative %LOOKUP variants reject keyed DS arrays: ASCEND/DESCEND cannot be
declared on a DS array. See IBM's
[lookup reference](https://www.ibm.com/docs/en/i/7.5.0?topic=functions-lookupxx-look-up-array-element)
and [documentation correction](https://www.ibm.com/support/pages/documentation-lookupxx-71-incorrect).

The (*) key form is restricted to SORTA, exact %LOOKUP and %LOOKUPNE. It is not an ordinary
array value or writable API argument. Unsupported runtime uses report 105;
CALL argument rejection follows the existing 202 call-error boundary and does
not enter the host. %FIELDS use outside sorting retains its existing unsupported
behavior. General keyed EVAL/XFOOT/layout expressions and array ABI remain open.

## Verification and remaining work

`RpgKeyedArrayTests` covers key priority/direction, indexed-key capture, lookup
duplicates/ranges/types, subarray tails, complete record/gap/opaque-byte movement,
LIKEDS, invalid keys and source diagnostics, key-count bounds and conversion
failure before publication. `RpgKeyedArrayIntegrationTests` checks saved programs
after source removal, actual CCSID 37/1208 jobs, retained RETURN, LR/reclaim and
failed program creation. The display PTY compiles/calls RPGKEYARR for %FIELDS,
keyed lookup and keyed subarray sorting on both screen sizes.

Nested DS paths, varying arrays, DS-subfield CTDATA/pre-runtime arrays, alternate collation,
additional array built-ins, fixed native SORTA, complete expression typing,
the full entry/procedure ABI, independent native-system golden cases and scale/
overlap acceptance remain open. Existing declaration and native storage limits
continue to apply; no performance target is claimed.

Final verification (2026-10-17): all 3620 tests pass, zero failures/skips,
including 87 new cases and all 661 expanded focused checks. Locked restore,
zero-warning/error Release solution build, display PTY, generated inventory
and whitespace checks pass. Full-suite runtime is 7m17s, not a performance
target. Schema remains 31, command catalog 306 and checklist 36 complete /
70 open. C09 remains partial.
