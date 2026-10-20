# RPG RECNO and relative output

Externally described DISK files accept `RECNO(field)` in free declarations and
fixed F-specs. The associated field must be a writable numeric scalar with zero
decimals; qualified data-structure subfields are accepted. RECNO implies unblocked
processing. It is not inherited through LIKEFILE, which may supply a separate
association. Saved DISK metadata uses version 8 when an association is present;
older versions remain readable and cannot carry a RECNO association.

Input open sets feedback to zero. Successful CHAIN, forward/reverse input and
SETLL/SETGT positioning supply the physical relative record number in the actual
access path, including keyed input. A miss or EOF preserves the last value.
Record structures receive feedback independently of their result operand.
Feedback conversion is checked before importing record data, so an undersized
field reports numeric status 103 without changing the record fields. Output-only
open preserves the requested number. CLOSE preserves feedback; a new input OPEN
resets it. Retained open files keep their feedback across calls.

For a non-keyed file with output capability, WRITE uses its RECNO field to fill
one allocated deleted position. Active records and positions beyond the allocated
extent are rejected. Use [INZPFM](member-initialization.md) to allocate deleted
positions first. Relative WRITE preserves the extent, existing input position and
update ownership; it does not establish an update target. Ordinary keyed WRITE
continues through the existing deleted-record reuse/allocation policy.

```rpgle
**free
// Initialize deleted positions with INZPFM before calling this program.
dcl-f ORDERS usage(*input:*output) recno(NEXT_RRN);
dcl-s NEXT_RRN packed(10:0);
eval NEXT_RRN=5;
// Supply the externally described record fields here.
write ORDERREC;
chain 5 ORDERS;
seton LR;
```

The runtime resolves the association in the actual file owner's context, including
borrowed file parameters, global fields shadowed in a procedure, qualified fields,
and automatic/STATIC local files. Saved program execution works after source
removal. Existing physical/logical output validation checks selection, defaults,
uniqueness, owner, authority, active job, live schema, cancellation and commitment
enlistment. Validation runs again after the writer wait. Trigger failures roll
back record and slot bookkeeping. Ordinary journal capture records the requested
position and executing job. Declared relative output rejects commitment workspaces.

The IBM [RECNO keyword](https://www.ibm.com/docs/en/i/7.5.0?topic=keywords-recnofieldname),
[relative record processing](https://www.ibm.com/docs/en/i/7.5.0?topic=files-relative-record-number-processing)
and [output handling](https://www.ibm.com/docs/en/i/7.5.0?topic=handler-handling-output-data)
describe the numeric association and allocated deleted-record requirement.
The ILE RPG Reference, “Record address type” and LIKEFILE table, distinguishes
non-keyed relative output and the non-inherited RECNO keyword.

This is bounded local acceptance. Eligibility failures currently use IPC0138 and
RPG file status 1299; complete native status mapping and independent IBM golden
acceptance remain open. Indexed RECNO targets, program-described/multi-format
files, multiple-member relative mapping, native COMMIT/open/isolation allocation,
operator SIZE extension and scale budgets remain open. See the
[record-slot foundation](record-slots.md) and [RPG DISK scope](rpg-disk-files.md).
C08/C09/C11 remain incomplete.

Verification (2026-10-19): all 6732 solution tests pass with zero failures/skips
in 10m34s, including 44 new cases. All 65 focused cases and 4348 broad
RPG/binding/work cases in the full suite pass. Locked restore, a Release build
with zero warnings/errors, 24x80/27x132 display PTY acceptance, generated inventory,
local links and whitespace checks pass.
