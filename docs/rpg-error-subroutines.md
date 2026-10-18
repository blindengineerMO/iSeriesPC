# RPG error subroutines

Declared DISK and WORKSTN files accept `INFSR(name)` or `INFSR(*PSSR)`.
Program errors can dispatch to `BEGSR *PSSR`. The existing numeric status and
[native feedback structures](rpg-feedback-structures.md) are updated before
the error subroutine runs.

```rpgle
**free
dcl-f DATA usropn infsr(FILEERR);
dcl-s N int(10);
read DATA;
return;
tag RECOVER;
eval N=1/0;
return;
begsr FILEERR;
  if %status(DATA)=1211;
    goto RECOVER;
  endif;
  return;
endsr;
begsr *pssr;
  sndpgmmsg %char(%status);
  return;
endsr;
```

## Dispatch and scope

Expected file errors first consider E or the operation's ER indicator, then
matching MONITOR groups, then the file's INFSR. Program errors use the local
*PSSR after matching monitors. A *PSSR receives file errors only when the file
explicitly specifies INFSR(*PSSR). Normal EOF/misses do not dispatch handlers.
These rules follow IBM's [file handler contract](https://www.ibm.com/docs/en/i/7.5.0?topic=exceptionerrors-file-exceptionerror-subroutine-infsr).

Monitors enclosing EXSR calls take precedence over error subroutines in that
same procedure, including nested EXSR calls. A callee procedure's *PSSR takes
precedence over a monitor around its CALLP/CALLB. Failure then reaches the caller
as status 202. See IBM's [MONITOR rules](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-monitor-begin-monitor-group).

Subroutines are local to their owning procedure. Each procedure may define its
own *PSSR and ordinary subroutines with the same names as other procedures.
INFSR must identify an existing main-procedure subroutine. A subprocedure or its
local subroutine cannot perform I/O on a global file with INFSR. The current
procedure syntax uses the existing P-spec boundaries; complete DCL-PROC and
local-file declarations remain open.

Initialization/open failures and end-of-program cleanup failures use default
handling rather than INFSR. Job end and cancellation bypass error subroutines.
Errors in a handler may recursively invoke it, as on IBM i. The interpreter
limits active error-subroutine nesting to 64 and propagates a bounded status
9999 failure instead of overflowing the host stack. A handler can use E,
MONITOR or a first-time switch to handle its own errors.

## Returning, recovering and cancelling

An explicit EXSR call to an error subroutine returns to the next statement
when ENDSR has no return point or a blank value. Automatic invocation with a
blank ENDSR uses default failure handling; it does not silently resume the
failed operation. A subprocedure *PSSR that falls through ENDSR fails with
RNX9001, producing caller status 202. A RETURN inside the handler returns from
its procedure normally. Main-procedure RETURN follows the existing retained
activation rules.

An error subroutine can GOTO a label in its own procedure body. Runtime transfer
unwinds intervening subroutine frames and reconciles loop/SELECT state. Labels
inside a MONITOR body or handler are rejected as recovery targets. This supports
the [program-error recovery patterns](https://www.ibm.com/docs/en/i/7.5.0?topic=subroutine-using-program-error)
without inventing an implicit statement retry.

Main-procedure error subroutines accept `ENDSR '*CANCL'` or a six-byte CHAR
field, subfield or supported indexed element. The field/element is reset to
blanks when ENDSR is reached. *CANCL raises escape RNX9001, closes retained
DISK/WORKSTN owners and discards activation state. Other unhandled failures
also discard failed activation state and release retained file locks. The next
call receives fresh storage. Normal RETURN preserves state; LR reclaims it.

Only blank and uppercase *CANCL return values are implemented. Literal cycle
return points fail compilation; unsupported variable values fail at the ENDSR
source location after resetting the field. ENDSR operands on ordinary
subroutines or subprocedure error subroutines fail compilation. Native cycle
points *GETIN/*DETC/*DETL/*TOTC/*TOTL/*OFL require the full native cycle and remain
open. See IBM's [ENDSR return contract](https://www.ibm.com/docs/en/i/7.5.0?topic=handlers-specifying-return-point-in-endsr-operation).

## Evidence and remaining work

`RpgErrorSubroutineTests`, `RpgFileErrorSubroutineTests` and
`RpgDisplayFileTests` cover dispatch priority, nested EXSR, procedure-local
handlers, explicit versus automatic ENDSR, feedback visibility, RETURN/GOTO,
six-byte return-point reset, RNX9001, recursion, cancellation/job end, retained
lock cleanup, fresh calls, saved source removal and WORKSTN errors. The terminal
PTY compiles and calls RPGERRSR, exercising INFSR GOTO and *PSSR RETURN.

Native cycle processing/return points, local file declarations, complete fixed
D/C layouts, remaining error/status families and independent IBM i golden
acceptance remain open. General ILE condition handlers, ON-EXCP and the full
procedure ABI also remain open. C09/C11 therefore stay partial.

Final verification (2026-10-17): all 3233 tests pass, zero failures/skips,
including 60 new cases and all 60 targeted checks. Locked restore, zero-warning/
error Release build, expanded display PTY, generated inventory and whitespace
checks pass. Recovery inside active FOR/SELECT and case-insensitive local labels
are included. Checklist remains 36 complete / 70 open.
