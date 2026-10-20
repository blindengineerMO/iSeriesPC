# Native local procedures

Native P-spec B/E definitions and free DCL-PROC/END-PROC now define procedures
within one RPG compilation unit. Scalar PR/PI interfaces support live references,
CONST, VALUE, *NOPASS, *OMIT and scalar returns.
[Fixed array and LIKEDS parameters](rpg-aggregate-parameters.md) also support
aggregate references, CONST and VALUE copies. Calls can use CALLP, native
C-spec CALLP, implicit procedure statements or expressions. C09 remains partial.

[Aggregate returns and RTNPARM](rpg-return-values.md) extend return layouts to
fixed arrays and structures while preserving copies and parameter positions.
[Exact and adjusted parameters](rpg-parameter-options.md) add caller-side type
checks and private character transformations for CONST/VALUE arguments.

[Variable-size references](rpg-variable-parameters.md) add bounded actual
storage, partial accesses and capacity-preserving forwarding.

The source rules follow the [IBM ILE RPG reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
procedure interfaces, p. 121; subprocedure storage/error rules, pp. 142–144;
free interfaces, p. 448; VALUE, p. 539; procedure statements, p. 585; and RETURN,
p. 949. Independent native-system reference acceptance remains open.

```rpgle
**free
dcl-s RESULT int(20);
eval RESULT = FACT(7);
return;

dcl-proc FACT;
  dcl-pi *n int(20);
    N int(3) value;
  end-pi;
  if N <= 1;
    return 1;
  endif;
  return N * FACT(N - 1);
end-proc;
```

The compiler supplies a prototype when a local definition has no explicit PR.
An explicit procedure PR may specify its scalar return type and EXTPROC with a
quoted local symbol, or default to the prototype name. Quoted EXTPROC names are
case sensitive; ordinary RPG identifiers normalize to uppercase. Each target must
exist in the same compilation unit. Missing targets and incompatible interfaces
reject before execution. EXPORT is recorded on procedure definitions; it does
not establish cross-module service-program binding.

Native P specs support both existing column origins, names in columns 7–21,
B/E in column 24 and optional EXPORT in the keyword area. Native returning
PR/PI headers use the ordinary type/length/decimal columns. A free PI names its
procedure or *N. An absent PI describes an empty void interface. Free headers
and endings can span declaration lines and include comments. Original source
locations remain attached to declarations, calls and runtime statements.

## Storage and parameter modes

Each invocation has its own automatic local variables, arrays and structures.
Local names can shadow module variables. Local scalar/array STATIC storage is
initialized once per module activation and survives calls and recursion; activation
reclaim or a fresh module activation discards it. Local file declarations and
local PSDS definitions reject. Module-global variables, native buffers, indicators,
file positions, table selections and structure occurrences retain shared storage.
Callees resolve global names in their module, rather than in their caller's locals.

Reference parameters retain captured addresses and alias identity. An update
through one alias is visible through another. CONST retains existing read-only
reference/conversion behavior. VALUE always creates a private native image before
entry, even for an exact-layout actual; the callee can change it without writing
back to the actual. Scalar conversions use the native codec. VALUE cannot combine
with CONST or *OMIT and cannot appear on an EXTPGM parameter. VALUE may use
*NOPASS. Explicit omissions and absent trailing arguments retain their distinct
%PARMS/%PASSED/%OMITTED states within the current local PI.

Borrowed receiver scopes expire when the procedure returns or fails. The compiler
checks statement and expression calls against linked interfaces. Indexed actuals
keep their selected address if later arguments call functions that change indexes.
Existing project prototype/P-spec conventions remain separate.

## Returns, errors and integration

A scalar function returns through RETURN with a value. The declared native codec
normalizes its type, precision, byte width and CCSID. Void procedures cannot be
used as expressions. Return operands must agree with the PI; reaching a returning
procedure's end without a value fails at execution. A CALLP statement can discard
a scalar result. Functions can nest inside expressions and arguments.

Argument-processing failures preserve their existing status and monitor behavior.
Final callee failures use the existing status-202 call boundary; CALLP(E) can
handle them after valid reference writes. Cancellation and ENDJOB propagate and
release borrowed cells. Local *PSSR can return a declared result or recover through
its own labels. Falling through it abnormally ends the procedure with RNX9001.
Its ENDSR cannot use main-cycle return operands, and native subprocedures cannot
perform I/O against files with INFSR. Wider native exception distinctions remain
open.

%PARMS is local to the invocation while PSDS *PARMS retains the main call count.
Local error handlers can read shared module feedback. Functions can call system
APIs with private receivers, dispatch stored/external programs and access global
files without closing those files on procedure return. External response validation
and reference lifetime rules remain in force. Saved-program tests remove source
before executing these paths at CCSIDs 37/1208.

## Bounds and evidence

Compilation permits at most 256 native procedure definitions/prototypes under the
existing metadata limits. Calls retain the 255-position and 1 MiB actual-argument
bounds. Active native calls admit at most 128 frames and 64 MiB of declared local
images. Retained procedure STATIC images are bounded to 16 MiB per activation.
These are emulator resource limits, not native capacity or performance claims.

`RpgProcedureTests` covers native/free declarations, aliases, recursive VALUE
calls, automatic and STATIC storage, scalar codecs, local name shadowing,
parameter presence, source diagnostics, cancellation/error recovery, shared table
and occurrence state and resource bounds. Saved-program tests cover real queue
APIs, global disk positions, source removal, retained STATIC values and activation
group reclaim. External fixture processes validate private receiver writeback,
including errors and malformed responses. RPGPROC provides terminal compilation
and factorial/reference/STATIC acceptance.

Verification (2026-10-18): all 4643 solution tests pass with zero failures/skips,
including 70 new checks (58 unit, nine saved-program/integration and three
external-process cases). All 377 targeted checks, locked restore, zero-warning/
error Release build, final terminal acceptance, generated inventory, document
links and whitespace checks pass. RPGPROC runs at 24x80; general display fixtures
also cover 27x132. Full-suite wall time is 7m30s, not a performance target.

[Modules](rpg-module-binding.md), [services](rpg-service-definitions.md) and
[native procedure pointers](rpg-procedure-pointers.md) extend local calls.
Broader ILE binding, wider pointer ABIs/foreign calling
conventions, additional return layouts and parameter types, further storage and CCSID modes,
local files, complete cycle/exception/activation semantics and independent native
golden/scale acceptance remain open. C09 is not closed.

[Static module binding](rpg-module-binding.md) now resolves exported native
procedures across captured source-bound modules while preserving separate globals
and existing borrowed argument contracts.
