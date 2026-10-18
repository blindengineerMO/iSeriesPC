# Native program prototypes and main interfaces

[Forward native types](rpg-forward-declarations.md) resolve later LIKE targets
and explicit/implicit return types while preserving source-order value binding.

[Native LIKE declarations](rpg-like-declarations.md) add inherited scalar types,
signed length adjustments and related type identities for exact parameters.

Native D-spec PR/PI and free DCL-PR/DCL-PI now describe program calls using
fixed scalar and aggregate references. Prototypes use EXTPGM; main interfaces
bind the caller's cells to native storage. Free and native C-spec CALLP(E) support the
same final-call error path. C09 remains partial.

Source rules follow the
[IBM ILE RPG reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf),
pp. 447–450, 452–457, 615–616 and 800. EXTPGM identifies a dynamic program call;
local procedure prototypes bind within one unit under the
[local procedure contract](rpg-local-procedures.md). Cross-unit binding remains open.
Emulator tests do not replace independent native-system reference acceptance.

```rpgle
**free
dcl-pr UPDATE extpgm('QGPL/UPDATER');
  FIRST int(3);
  SECOND int(3);
end-pr;
dcl-s NUMBER int(3) inz(7);
callp(e) UPDATE(NUMBER:NUMBER);
```

The callee can declare `dcl-pi *n; A int(3); B int(3); end-pi;`. Both variables
then refer to the same one-byte caller cell. Incrementing A and subsequently
adding A to B produces 16. A later callee error leaves those writes visible.

## Declarations and layouts

Native fixed headers use PR or PI in columns 24–25, with blank external/DS,
From, return-type and reserved columns. Scalar parameter definitions follow with
blank definition type and From entry. Their lengths, types, decimal positions
and keyword continuations use the [traditional definition rules](rpg-fixed-declarations.md).
The first new definition type, non-D specification or end of source ends the
signature. Both supported native column origins retain original source maps.

Free signatures use END-PR/END-PI, optionally followed by the matching name.
Inline zero-parameter `END-PR`/`END-PI` is supported. Column-limited declarations
use columns 8–80 before calculations. Operation-code parameter names require
DCL-PARM. Program declarations and main PI behavior are described here;
[local interfaces and prototypes](rpg-local-procedures.md) have procedure scope.

EXTPGM accepts a quoted program name or LIB/PROGRAM, or defaults to the prototype
name when no argument is specified. Object components are at most ten characters.
Dynamic EXTPGM expressions remain unsupported. EXTPROC can bind a
[procedure in the same compilation unit](rpg-local-procedures.md); foreign and
cross-unit binding remain open.

Supported scalar types are CHAR, PACKED, ZONED, signed INT, FLOAT, IND and default
ISO DATE/TIME/TIMESTAMP, with optional raw-character CCSID(*HEX)/65535. Numeric
and byte widths use the existing native codec and bounds. Mutable numeric
references require exact layouts; native character actuals may be longer than
the formal. [CONST parameters](rpg-const-parameters.md) support literals,
expressions, private conversions and live read-only references.
[Fixed array and LIKEDS parameters](rpg-aggregate-parameters.md) extend these
interfaces to native aggregates. Incompatible mutable layouts reject before
executing a native prototyped call. Malformed known-native
call syntax also rejects at the original call location; a PLIST name cannot
replace a native prototype variable argument. [OPTIONS(*NOPASS)](rpg-optional-parameters.md)
permits optional trailing parameters. [OPTIONS(*OMIT)](rpg-omitted-parameters.md)
permits explicit omission slots; %PASSED/%OMITTED distinguish those states.
VALUE remains invalid for EXTPGM; local procedure VALUE parameters are
[supported separately](rpg-local-procedures.md).
[Exact and adjusted parameters](rpg-parameter-options.md) add OPTIONS(*EXACT),
*TRIM and *RIGHTADJ for supported native types.
[OPTIONS(*VARSIZE)](rpg-variable-parameters.md) supports shorter/longer reference
storage and actual character-array strides. Other OPTIONS modes remain open.

Prototype parameter names are documentation. Their metadata lives outside
runtime fields and native allocations, so repeated names cannot shadow caller
variables. Main PI names define native variables. *N declares an unnamed main
interface; a named interface must match its native program prototype by layout.
Only one main interface is allowed, and it cannot coexist with an *ENTRY PLIST.
Calls must supply every required parameter and may supply any optional trailing
prefix; missing required or excess arguments reject before the body runs.

Native program calls support at most 255 parameters and native declarations at
most 256 prototypes. Existing argument
bounds remain 1 MiB per call. Parameter metadata does not consume the 256 native
variable/DS allocations. Include maps and compilation/runtime diagnostics retain
original member locations.

## References, activation and external targets

Native prototypes pass live cells to stored CL/RPG programs and native API adapters.
Aliases share identity and storage ranges; reads observe intervening writes.
Indexed fields and current elements retain the address captured before the call,
including when a host returns updated values. Borrowed cells and factories expire
on return or error. Main PI references detach on exit and rebind on later calls;
RETURN retains other activation storage, while LR/reclamation reload it.

Supported scalar raw-character formals can address exact-width whole DS byte
receivers. This does not establish complete native DS/array/table/procedure ABI.
Type/layout and read-only validation precede native API output preparation.
Stored programs receive exact-width one-byte integer references and opaque bytes.
Character/zoned/indicator/date-time behavior is verified in CCSID 37/1208 jobs.

External processes keep the existing value protocol. Overlapping RPG references
reject before starting the process, because that protocol cannot represent live
aliases. All returned reference values are normalized before applying any of them;
malformed later values leave earlier caller cells unchanged. Valid returned values
are applied even when the process reports failure. This is response validation,
not rollback of external side effects or a claim of native foreign-language ABI.

The older project prototype syntax retains its copied ordinary-program convention
and strict API borrowing. Native signatures use the distinct reference path.
Custom hosts need a typed dispatcher for native prototypes; they cannot silently
fall back to an untyped copy interface. The legacy untyped callback retains its
null-return acknowledgment for void calls; null from typed dispatch means the
target was not resolved.

## CALLP(E) and error stages

CALLP(E) handles final target failures as status 202, sets %ERROR and takes
precedence over MONITOR for that call. A subsequent successful E operation resets
the error state. Unresolved CALLP targets report failure. Cancellation/ENDJOB
continue through the normal cleanup path.

E does not consume errors while evaluating arguments. Division by zero retains
102, an invalid indexed parameter retains 121, and unsupported (*) argument
selection retains 105; an enclosing MONITOR can handle those original statuses. Both free and native C-spec forms use this boundary.
Other CALLP extenders and richer 211/231 distinctions remain open.
[Local procedure calls](rpg-local-procedures.md) now support scalar returns,
implicit calls and expressions; program calls continue to use EXTPGM.

## Evidence and remaining work

`RpgProgramSignatureTests` covers native/free syntax, all scalar layouts, private
formal metadata, PI aliases/counts, captured addresses, raw DS receivers, includes,
limits and rejection paths. `RpgCallErrorTests` distinguishes argument preparation
from final-call errors, handler precedence, reset, unresolved targets and cancellation.
Saved-program integration removes source and exercises CL/RPG/API calls, complete
scalar byte images, PSDS counts and retained activations at CCSID 37/1208.
External-process tests cover alias rejection and complete response prevalidation.
The terminal compiles RPGSIGUP/RPGSIG and checks native PR/PI aliases and free
CALLP(E) error writeback.

VALUE, other OPTIONS modes, wider reference compatibility, arrays/DS/table entry ABI,
remaining aggregate/local storage interfaces, modules/service programs/cross-unit binding, remaining
activation/exception semantics and independent native golden/scale acceptance
remain open. No performance target is claimed.


Signature baseline verification (2026-10-18): all 4380 solution tests pass, zero failures/skips,
including 128 new cases (99 unit, 21 saved-program and eight external-process
cases). The 304 targeted checks and 2714 final expanded checks pass. Locked
restore, zero-warning/error Release build, final RPGSIG/RPGSIGUP display PTY,
generated inventory, documentation links and whitespace checks pass.
RPGSIG/RPGSIGUP run at 24x80; general display fixtures also cover 27x132.
Full-suite runtime is 7m39s.
