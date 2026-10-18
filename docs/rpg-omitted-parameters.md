# Omitted native program arguments

Native scalar PR/main PI and free DCL-PR/DCL-PI signatures now support
OPTIONS(*OMIT), alone or combined with OPTIONS(*NOPASS). A call can pass the
special argument *OMIT for that position. It occupies a parameter-list slot but
provides no storage. C09 remains partial.

The rules follow the [IBM ILE RPG reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
OPTIONS(*OMIT), p. 506; %OMITTED, pp. 733–734; %PARMS, p. 737; and %PASSED,
pp. 742–743. Independent native-system reference acceptance remains open.

```rpgle
**free
dcl-pr RUN extpgm('TARGET');
  NUMBER int(10);
  TEXT char(4) const options(*omit:*nopass);
end-pr;
dcl-s NUMBER int(10) inz(7);

callp RUN(NUMBER:'TEXT');
callp RUN(NUMBER:*omit);
callp RUN(NUMBER);
```

The callee declares the matching main PI and uses %PASSED(TEXT) to check whether
TEXT has storage, or %OMITTED(TEXT) to distinguish an explicit omission:

| TEXT argument | %PARMS | %PASSED(TEXT) | %OMITTED(TEXT) |
|---|---:|---|---|
| `'TEXT'` | 2 | *ON | *OFF |
| `*OMIT` | 2 | *OFF | *ON |
| no trailing argument | 1 | *OFF | *OFF |

The required NUMBER position remains present in all three calls. OPTIONS(*OMIT)
alone still requires its position on the call; it allows either storage or *OMIT.
OPTIONS(*NOPASS) controls whether a trailing position may be absent. Any formals
after a *NOPASS formal must also specify *NOPASS, even if they allow *OMIT.
Both option orders are accepted; duplicates and other passing modes reject.

## Source and presence checks

*OMIT is a dedicated argument expression. It is valid as a complete actual for
a native prototype formal with OPTIONS(*OMIT), including CONST formals.
It cannot be used as an arithmetic operand, conversion input, declaration
constant or unprototyped argument. Quoted `'*OMIT'` remains ordinary character
data. Wrong native formal options and named PR/PI mode mismatches reject at
original source locations; runtime preparation also validates the option.

%OMITTED(P) requires the name of an OPTIONS(*OMIT) parameter in the current
native main or local PI. %PASSED(P) accepts either *OMIT or *NOPASS. These built-ins do
not read the parameter value. Required ordinary parameters, literals, indexed
operands, expressions, unknown names, *ENTRY PLIST operands and references from
a different procedure reject. %PARMNUM still returns the original position.
%PARMS and PSDS *PARMS include explicit omission slots.

A retained activation recomputes omitted and unpassed state on every call.
At runtime, reading, writing or forwarding the value of an omitted field fails with
emulator status 105, which MONITOR can handle. Metadata queries such as %SIZE
remain available. Use a presence guard before reading a field, and explicitly
pass *OMIT when forwarding an omitted argument to another eligible prototype.
IBM documents invalid parameter-storage access as unpredictable; status 105 is
a deterministic emulator diagnostic rather than a native failure-code claim.

## Borrowed descriptors, APIs and processes

Typed hosts receive a scoped ProgramArgument with IsOmitted and IsReadOnly true,
type *OMIT, no storage range and no native buffer. Its semantic value is null.
Omission does not allocate the formal-width image or count those bytes toward
the 1 MiB actual-argument bound. The 255-position native-call bound still applies.
Multiple omitted descriptors do not overlap, even when one object is reused.
Borrowed Value and ToBuffer access expire when the call ends.

Stored native RPG programs bind these descriptors without copying null into
local backing storage. A direct typed invocation can also supply null for an
OPTIONS(*OMIT) PI parameter. Present mutable references and CONST temporaries
keep their existing alias/conversion behavior.

External protocol versions 1 and 2 encode omission slots as JSON null and retain
the full supplied argument count. This protocol transfers values, not host
pointers. A null response is accepted for an omission; a non-null response rejects
all response writeback before earlier mutable outputs change. External side
effects are not rolled back. Repeated omission slots do not trigger alias denial.

API adapters still apply their own pointer/storage rules. Existing character,
numeric and output-storage contracts reject omitted storage; send/receive queue
tests verify rejection before enqueue/dequeue and other output changes.
Supporting *OMIT in a prototype does not authorize a null pointer for every API
parameter. Additional per-API nullable-pointer contracts remain open.

## Evidence and remaining work

`RpgOmittedParameterTests` covers native/free forms, both native column origins,
native C-spec CALLP, all presence states, retained rebinding, scalar metadata,
scoped lifetime, non-aliasing, conversion/option errors, maximum omission counts
and atomic response validation. Saved-program tests remove source and exercise
explicit nested forwarding, mutable writes, PSDS counts and queue rejection at
CCSIDs 37/1208. External fixture processes inspect null slots and return valid
or altered responses in both protocol versions. RPGOMIT/RPGOMITUP provide
terminal compilation and alternating present/omitted/unpassed calls.

Verification (2026-10-18): all 4573 solution tests pass with zero failures/skips,
including 64 new cases (50 unit, 10 saved-program and four external-process).
All 312 targeted checks, locked restore, zero-warning/error Release build,
terminal acceptance, generated inventory, local links and whitespace checks pass.
Obsolete native *OMIT rejection fixtures now reject duplicate *OMIT options.
RPGOMIT/RPGOMITUP run at 24x80; general display fixtures also cover 27x132.
Full-suite wall time is 7m43s, not a performance target.

[Local procedures](rpg-local-procedures.md) now support scalar VALUE and returns.
Other OPTIONS modes, EXTPGM VALUE and native aggregate entry ABI remain open.
Modules/service programs/cross-unit binding, CL receiver-side optional/omitted entry handling,
additional API pointer contracts, wider CCSID modes and independent native
golden/scale acceptance remain open. The older project prototype option contract
is separate. No performance target is claimed.
