# Traditional RPG calculation specifications

Native C-specs now enter the existing RPG interpreter through a separate parser.
Supported calculations use native standalone storage, control flow, parameter
borrowing and declared file services. The older project layout, with C in column
7 and its shorter operation field, remains a separate compatibility syntax.
This implements part of C09; complete fixed-form and native-system acceptance
remain open.

[Native array searches](rpg-array-search.md) now add LOOKUP, %LOOKUPNE,
%FOUND/%EQUAL state and column-limited global free declarations. LOOKUP uses
standard factors and indicators, with its own numeric/index semantics.

## Physical columns

The layout follows the calculation and extended-factor-2 sections of the
[IBM ILE RPG reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf).
Native C must be in column 6; a one-column shift of native calculations is not
accepted because it overlaps the older project syntax.

| Columns | Supported entry |
|---|---|
| 1–5 | Blank or numeric sequence text |
| 6 | C |
| 7–8 | Blank, or SR inside a subroutine |
| 9 | Blank or N to negate a conditioning indicator |
| 10–11 | One general conditioning indicator, 01–99 |
| 12–25 | Factor 1, left justified |
| 26–35 | Operation and supported extenders, left justified |
| 36–49 | Standard factor 2, left justified |
| 50–63 | Standard result, left justified |
| 64–68 | Optional right-justified result length |
| 69–70 | Optional right-justified decimal positions |
| 71–72 | HI resulting indicator |
| 73–74 | LO resulting indicator |
| 75–76 | EQ resulting indicator |
| 77–80 | Blank for standard calculations |
| 81 onward | Commentary, excluded from code and quote processing |

Extended operations use columns 36–80 as one expression and require blank
factor 1. EVAL, IF, WHEN, DOW, DOU, FOR, CALLP, ON-ERROR, RETURN and SORTA use
this form. Continuations repeat C and leave columns 7–35 blank; expression
fragments join with whitespace. Empty/comment records and expanded includes may
intervene. Literal splitting and native +/- continuation rules remain open.
Fixed C* comments and commentary apostrophes cannot change quote/directive or
CTDATA state. Include diagnostics retain the original record origin.

Inline result definitions create global CHAR fields when decimals are blank,
or PACKED fields when decimal positions are supplied. Repeated layouts must
match existing scalar declarations. Definitions precede native allocation and
file binding; indexed targets and local result definitions are unsupported.

## Calculations and control flow

ADD, SUB, MULT, DIV, Z-ADD, Z-SUB and XFOOT require numeric operands and a
declared numeric result. Blank factor 1 on the first four operations uses the
current result value. Z-ADD, Z-SUB and XFOOT require blank factor 1. XFOOT takes
a whole numeric array in factor 2. Supported whole-array arithmetic uses the
existing array rules; resulting indicators require a scalar result. An indexed
result captures its address once before publishing the converted value.

Arithmetic HI/LO/EQ test the stored result against zero, after native conversion,
and reset every supplied flag. COMP compares factor 1 with factor 2 and sets
HI/LO/EQ for greater/less/equal. Native character COMP uses unsigned job-CCSID
bytes with blank padding. Extended expressions retain the existing expression
evaluator; complete native expression typing, precision and collation remain
open. See [native scalars](rpg-native-scalars.md) and
[array operations](rpg-array-operations.md) for byte and array rules.

SETON/SETOFF take up to three indicators in columns 71–76, including LR.
General indicators are 01–99. The existing immediate LR termination behavior
applies; this is not the full native cycle/LR timing contract. CLEAR uses the
result field, optionally with *ALL in factor 2. SORTA(A/D) uses extended factor
2, including the supported array/key forms. DSPLY currently supports only a
factor-1 display value; it does not request a reply.

DO defaults its start and limit to 1 and supplies a private native counter when
the result is blank. It skips the body when the initial counter exceeds the
limit. The limit is read after counter initialization and again after every
increment, so changes and aliases in the body affect termination. ENDDO can
supply an increment, evaluated each iteration, only for DO.
ENDFOR must close FOR; ENDDO closes DO/DOW/DOU. IF/ELSE/ENDIF, SELECT/WHEN/OTHER/
ENDSL, FOR, DOW, DOU, ITER, LEAVE and RETURN use existing structured execution.
A false conditioning indicator skips a conditioned opener and its whole block.
TAG/BEGSR take factor 1; GOTO/EXSR take factor 2. SR requires a subroutine.
ENDSR supports the existing return points. MONITOR/ON-ERROR/ENDMON use numeric
status routing; native orphan ON-ERROR rejects at compile time.

## Calls and files

CALL evaluates the program name from factor 2 and optionally names a PLIST in
the result. Without a named list, immediately following PARM records supply
ordered arguments. PLIST takes its name in factor 1; PARM takes a declared field
in the result. Copy operands on PARM remain unsupported. Lists are declarative,
including inside an unexecuted subroutine, and preserve repeated argument aliases.
There are at most 256 lists and 255 arguments per list/call. *ENTRY requires
distinct scalar fields and binds before main execution, including when declared
inside a subroutine. Native CALL cannot combine a named list and inline PARMs.
Typed hosts borrow scoped native cells; semantic hosts receive snapshots and
can return ordered updates. RETURN/error detach entry references.

CALL(E) and CALLP(E) handle called-program failure as status 202 before MONITOR.
CALL can instead use its LO error indicator, reset on each call. HI and the
callee-LR EQ result remain unsupported. Error indicators cannot combine with E.
Full CALLB, prototype/entry/array ABI and native procedure specifications remain
open.

OPEN, CLOSE, UNLOCK, CHAIN, READ, READP, READC, READE, READPE, SETLL, SETGT,
WRITE, UPDATE, DELETE and EXFMT use their native factor/result positions and
the existing declared file services. Result DS operands remain unsupported.
Native SETLL sets HI when beyond the end and EQ on an equal key; SETGT uses HI
for end. CHAIN uses HI for not found; reads use EQ for end. LO handles supported
file errors; N/E validation and numeric status follow
[file status](rpg-file-status.md). These columns do not widen the existing
[DISK](rpg-disk-files.md) or [display](display-files.md) service contracts.

## Evidence and remaining work

`RpgFixedCalculationTests` covers physical operand/indicator columns, converted
arithmetic flags, CCSID COMP, continued extended expressions, native loops and
block conditions, subroutines, arrays, implicit definitions, ordered/aliased CALL
arguments, declarative lists, entry cleanup, error precedence, include origins
and explicit rejection. `RpgFixedCalculationIntegrationTests` exercises actual
CCSID 37/1208 jobs, saved-source removal, QWCRSVAL with native PLIST/PARM columns,
CL aliases on normal/error returns, retained RETURN/LR/reclamation and declared
DISK not-found/equal/EOF/E behavior. The terminal fixture compiles and calls
RPGNATCAL for DO, array arithmetic, SORTA(D), XFOOT, indicators and native DSPLY.
The generated compatibility report inventories native and older syntax
separately; recognition is not complete semantic acceptance.

Cycle/control levels, AN/OR indicator groups and special indicators, native
MOVE/MOVEL/MVR/BIT/TEST families, CALLB, unsupported arithmetic extenders,
PARM copy operands, full native file/result-DS forms, external/nested DS, local/procedure PR/PI specs,
continued names/literals, remaining types and keywords, pre-runtime arrays,
complete expression/collation/ABI behavior and independent native-system golden/
scale acceptance remain open. C09 remains partial. No performance target is
claimed.


Verification (2026-10-18): all 3969 tests pass, zero failures/skips, including
93 new cases; all 1766 expanded RPG/CL/queue checks pass. Locked restore,
zero-warning/error Release build, expanded display PTY, generated inventory and
whitespace checks pass. RPGNATCAL compiles/runs at 24x80; other display fixtures
also cover 27x132. Full-suite runtime is 7m13s, not a performance target.
Schema remains 31, command catalog 306 and checklist 36 complete / 70 open.
C09 remains partial.

[Native fixed DS](rpg-fixed-structures.md) now supplies typed arithmetic/array
operands and DO counters through the shared layout resolver. Indexed counters
write their selected cells; hidden counters retain their private storage path.
