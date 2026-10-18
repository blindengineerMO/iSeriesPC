# Optional native program parameters

Native scalar PR/main PI signatures now support OPTIONS(*NOPASS). Calls may
supply any prefix that includes all required parameters. Every parameter after
the first optional parameter must also specify OPTIONS(*NOPASS). The same rules
apply to traditional D-specs and free DCL-PR/DCL-PI, including CONST parameters.
C09 remains partial.

The rules follow the [IBM ILE RPG reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
OPTIONS(*NOPASS), pp. 505–506; short-circuit evaluation, p. 666; %PARMNUM, p. 739;
and %PASSED, pp. 742–743. Independent native-system acceptance remains open.

```rpgle
**free
dcl-pi *n;
  NUMBER int(10);
  TEXT char(4) const options(*nopass);
end-pi;

if %passed(TEXT) and TEXT = 'ABCD';
  eval NUMBER = NUMBER + 10;
endif;
if not %passed(TEXT) or TEXT = '';
  eval NUMBER = NUMBER + 1;
endif;
return;
```

The caller can declare the corresponding EXTPGM prototype and call it with
NUMBER alone or with NUMBER and TEXT. Calls outside that range reject at their
original source location. Main PI binding and runtime argument preparation also
validate the actual count before executing the callee or dispatching the target.
A named PI must match its PR's optional and CONST attributes as well as layout.

## Presence and numbering

%PARMS and PSDS *PARMS report the actual argument count. Absent optional formals
do not create placeholder arguments, buffers or process-protocol values. The
255-parameter and 1 MiB actual-argument bounds still apply. Prototype metadata
alone does not allocate native variable storage.

%PASSED(P) accepts the name of an OPTIONS(*NOPASS) or OPTIONS(*OMIT) parameter
in the current
native interface (main or local). It returns *ON when that position was supplied and *OFF
when it was not or when *OMIT was supplied. A required ordinary parameter,
expression, indexed operand, literal,
unknown name, *ENTRY PLIST field or parameter from a different procedure rejects.

%PARMNUM(P) returns the one-based position of a required or optional parameter
in the current native main or local interface. It also works in declaration constants
and dimensions after the PI declaration, without reading parameter storage.
[Local procedure interfaces](rpg-local-procedures.md) now provide these queries;
return-parameter ABI and cross-unit binding remain open.

AND and OR now evaluate from left to right and stop once the result is known.
A false left operand of AND skips the right operand; a true left operand of OR
skips it. This permits parameter-presence guards and bounds checks such as
`I <= %ELEM(ARR) AND I > 0 AND ARR(I) > 10`.

## Storage and call boundaries

Supplied parameters retain the existing live-reference, CONST conversion,
character-prefix, alias and scoped-lifetime behavior. Stored RPG and CL callers,
native APIs and external process targets receive only the supplied arguments.
CL-to-RPG calls can pass a shorter valid prefix using variables or typed constants.
External responses must still match the actual request count and validate every
returned cell before mutable writeback.

A retained activation recomputes presence on every invocation. Its previous
optional value cannot stand in for an absent argument. Reading, writing or
forwarding an unpassed scalar fails with emulator status 105; MONITOR can handle
it, while CALLP(E) does not consume an argument-preparation failure. API receiver
forwarding rejects before dispatch and before queue removal or other output
changes. Metadata queries such as %SIZE do not read the parameter value.

IBM documents unpassed storage access as unpredictable. The status-105 guard is
a deterministic emulator diagnostic, not a claim about the native failure code.

## Evidence and remaining work

`RpgOptionalParameterTests` covers native/free declarations, both native column
origins, CALLP and native C-spec calls, all valid argument prefixes, live writes,
CONST values, presence rebinding, numbering/constants, short-circuit guards,
source diagnostics, forged runtime counts and allocation bounds. Saved-program
integration removes source and exercises retained RPG calls, PSDS counts, CL
variables/constants and real queue APIs in CCSID 37/1208 jobs. External-process
tests inspect zero-, one- and two-argument requests. RPGOPTION/RPGOPTUP provide
terminal compilation and alternating present/absent calls.

NOPASS baseline verification (2026-10-18): all 4509 solution tests pass with zero failures/skips,
including 70 new cases (57 unit, 10 saved-program and three external-process).
All 261 targeted checks, locked restore, zero-warning/error Release build,
terminal acceptance, generated inventory, local links and whitespace checks pass.
That checkpoint replaced three obsolete native NOPASS rejection fixtures
with then-unsupported *OMIT modes. Omitted-argument support subsequently changed
those rejection fixtures to duplicate *OMIT options. RPGOPTION/RPGOPTUP run at 24x80; general display fixtures also cover
27x132. Full-suite runtime is 7m33s, not a performance target.

[OPTIONS(*OMIT) and %OMITTED](rpg-omitted-parameters.md) now distinguish explicit
omissions from absent trailing parameters. [Local procedures](rpg-local-procedures.md)
now support scalar VALUE and returns. Other OPTIONS modes, EXTPGM VALUE, native
array/DS/table entry ABI, remaining local interfaces, modules/service programs/
cross-unit binding and wider CCSID modes
and independent native golden/scale acceptance remain open. The older project
prototype option contract is separate. No performance target is claimed.
