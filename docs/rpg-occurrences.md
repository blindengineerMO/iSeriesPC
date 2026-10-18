# Multiple-occurrence RPG data structures

Free-form native structures accept `OCCURS(n)`. Each occurrence has the same
layout and its own byte storage. The active occurrence starts at 1 and determines
which bytes a structure expression or subfield refers to. `EVAL %OCCUR(ds)=n`
selects another occurrence; `%OCCUR(ds)` reads the current selection. These follow
IBM's [OCCURS declaration](https://www.ibm.com/docs/ko/ssw_ibm_i_74/rzasd/doccurs.htm)
and [%OCCUR rules](https://www.ibm.com/docs/en/i/7.5.0?topic=functions-occur-setget-occurrence-data-structure).

```rpgle
**free
dcl-ds BANK qualified occurs(3) inz;
  LABEL char(10);
  AMOUNT packed(9:2);
end-ds;
eval BANK.LABEL='FIRST';
eval %occur(BANK)=2;
eval BANK.LABEL='SECOND';
eval %occur(BANK)=1;
sndpgmmsg BANK.LABEL;
```

## Storage and selection

The supported occurrence count is a positive integer constant from 1 through
32767, within the existing 1 MiB per structure and 16 MiB per program limits.
These limits include all occurrences and final feedback/INDDS layout expansion.
Initialization, overlays and subfield arrays apply independently to every
occurrence. Qualified and unqualified structures are supported. OCCURS cannot
combine with DIM, PSDS or INFDS; anonymous multiple-occurrence declarations are
unsupported. LIKEDS copies the layout without inheriting OCCURS or DIM; a new
declaration may specify its own OCCURS. INZ(*LIKEDS) follows the existing
initialization contract. See IBM's
[LIKEDS rules](https://www.ibm.com/docs/en/i/7.4.0?topic=keywords-likedsdata-structure-name).

Selection requires a non-float numeric value with zero decimal places. Literal,
declared field/subfield and ordinary arithmetic scale checks reject incompatible
sources with program status 105. An occurrence below 1 or above the declaration's
count reports status **122** and preserves the previous selection. MONITOR,
PSDS and error-subroutine dispatch use that status through the ordinary error
path. See IBM's
[program status codes](https://www.ibm.com/docs/en/i/7.4.0?topic=structure-program-status-codes).

Multiple-occurrence structures are distinct from arrays: `BANK(2)` is invalid.
`%ELEM(BANK)` returns the occurrence count. `%SIZE(BANK)` returns one occurrence's byte
width; `%SIZE(BANK:*ALL)` returns the total allocation. Whole-structure value
assignment and `%SUBST` address only the selected occurrence, as do qualified or
unqualified subfields and `%KDS` searches. See IBM's
[%SIZE rules](https://www.ibm.com/docs/en/i/7.5.0?topic=functions-size-get-size-in-bytes).

`CLEAR BANK` clears only the selected occurrence, using type defaults rather than
restoring explicit initial values. `CLEAR *ALL BANK` clears every occurrence and
sets the selection to 1. CLEAR of a subfield or subfield array affects the active
occurrence. Whole-structure clearing prepares a private image before publication;
cancellation preserves both storage and selection. See IBM's
[CLEAR rules](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-clear-clear).

## Calls and lifetime

A whole multiple-occurrence structure passed to a native API contains **all**
occurrences, starting at the first byte regardless of the active selection. A
subfield parameter refers only to its selected occurrence. This distinction
follows IBM's [parameter rules](https://www.ibm.com/docs/ro/i/7.4.0?topic=codes-parm-identify-parameters).

Scoped native API borrowing and copied host arguments preserve that distinction.
Actual byte ranges are captured at call entry, so lazy borrowing and copied
writeback retain the original subfield address if the selection later changes.
Whole-structure and subfield aliases share live bytes and retain range-aware
overlap protection. Borrowed cells expire at call return. `%SIZE(ds)` remains one
occurrence even when passing `ds` supplies all storage; an API length parameter
must describe the intended receiver size.

Retained RETURN preserves every occurrence and the current selection. LR,
activation reclamation and abnormal cancellation discard that state through the
existing session lifecycle. Saved programs execute without their source members.
The general native CL/RPG entry/procedure ABI remains unfinished; this change
extends the existing native API and copied-host contracts.

## Indicator structures

An INDDS may specify OCCURS. Every occurrence contains a separate 99-byte indicator
bank. WORKSTN I/O snapshots the current bank for output conditioning and captures
that bank's address for response deltas. Changing the selection while an operation
is pending does not redirect its response. Global `*IN` remains independent.
All-occurrence CLEAR resets every bank using the existing typed field/default
rules. See [INDDS](rpg-indicator-structures.md) and IBM's
[indicator structure example](https://www.ibm.com/docs/en/i/7.4?topic=structures-data-structure-examples).

## Evidence and remaining work

`RpgOccurrenceTests` covers byte vectors at CCSIDs 37/1208, initialization,
selection/type/range errors, PSDS and MONITOR, CLEAR, cancellation, LIKEDS,
storage limits, value images and frozen API argument addresses.
`RpgOccurrenceIntegrationTests` covers saved programs, retained selection,
LR/reclaim, whole-MO QWCRSVAL, current-occurrence %KDS and separate INDDS CA/CF
responses in actual CCSID 37/1208 jobs. Indicator unit tests also cover pending
response addresses and final expanded storage bounds. The display PTY exercises
two occurrences through CRTBNDRPG/CALL RPGINDDS.

Named constants and supported pure built-ins now supply declaration counts;
see [declaration constants](rpg-declaration-constants.md). Nested structures,
complete native fixed DS/cycle forms and OCCUR opcode, pointer alignment, extended feedback,
PRINTER, full expression type inference, full entry/prototype ABI and independent
native-system reference acceptance remain open. IBM's free calculation syntax
does not permit the OCCUR opcode; use %OCCUR with EVAL. C09 remains partial.


Final verification (2026-10-17): all 3345 tests pass, zero failures/skips,
including 60 new cases and all 237 final focused checks. Locked restore,
zero-warning/error Release build, expanded display PTY, generated inventory
and whitespace checks pass. Full-suite runtime is 7m26s, not a performance
target. Schema remains 31, command catalog 306 and checklist 36 complete /
70 open. C09 remains partial.


Declaration follow-up (2026-10-17): named constants and pure declaration built-ins
now supply OCCURS and related native layout values. %ELEM correctly returns the
number of occurrences; its prior rejection test is replaced by the native
contract, including occurrence count 1 and independence from current selection.
All 3405 tests pass, including 60 new declaration cases and 292 focused checks.
See [declaration constants](rpg-declaration-constants.md) for complete verification.
