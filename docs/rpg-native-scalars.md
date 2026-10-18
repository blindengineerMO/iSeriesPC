# Native standalone RPG scalars

[Native LIKE declarations](rpg-like-declarations.md) add inherited scalar types,
signed length adjustments and related type identities for exact parameters.

Free DCL-S and supported traditional D-spec S scalars now use native byte storage,
sharing the element codec used by native DS fields and arrays. They retain scalar
identity: no DIM is added, %ELEM rejects them, and %SIZE/%LEN use scalar metadata.
C09 remains partial.

```rpgle
**free
dcl-s BYTE int(3) inz(127);
dcl-s MONEY packed(5:2);
dcl-s HANDLED ind;
eval MONEY=-12.349;
monitor;
  eval BYTE=128;
on-error 103;
  eval HANDLED=*on;
endmon;
// BYTE is still 127 and MONEY is -12.34.
seton LR;
```

## Values and bytes

Signed INT(3/5/10/20) uses 1/2/4/8 big-endian bytes. Assignment drops fractional
positions toward zero and rejects values outside the signed range with status
103. PACKED and ZONED assignment truncates to the declared scale and checks
precision. FLOAT(4) stores a four-byte IEEE value, including its precision and
overflow behavior. Indicators and default ISO date/time/timestamps use the
existing native codec. Conversion happens in a private image before publication;
conversion failure or cancellation preserves the previous bytes.

CHAR is padded/truncated by the activation's captured CCSID and byte width.
Unrepresentable input reports 452 before mutation. In the supported standard
character-size mode, truncation can leave a partial UTF-8 character. Raw %SUBST
and API buffers preserve those bytes; decoding invalid character storage reports
452. Writable %SUBST uses byte positions. Literal '*BLANKS' remains text; the
figurative constant and CLEAR apply defaults. IBM describes the byte-based
processing and truncation in
[standard-character-size processing](https://www.ibm.com/docs/en/i/7.5.0?topic=cdt-processing-string-data-by-natural-size-each-character)
and [string assignment](https://www.ibm.com/docs/en/i/7.4.0?topic=operations-string).
Full natural-character and mixed/explicit source/field CCSID modes remain open.

Standalone storage is allocated after file binding, so explicit display-field
opacity and compatible external declarations are resolved first. Scalar fields
share the existing 256 native storage declarations, 1 MiB per declaration and
16 MiB per program limits with arrays and data structures. Source/declaration
bounds and captured-codepage rules continue to apply. Older custom D-spec fields
and implicitly imported external fields retain their existing representation;
complete legacy/external-field unification remains open.

## Calls, aliases and receivers

Native API scalar views expose actual bytes and type/precision metadata, with
scoped lifetimes and opaque storage identities. Repeated arguments share cells.
Scalar prototype checks remain strict. The existing arrays/DS/procedure ABI
limitations are unchanged; see [native storage](rpg-native-structures.md).

Borrowed scalar entry parameters retain their caller's storage. Reads refresh
from the caller; calculation and API writes publish immediately. The binding
checks type, width, scale and buffer CCSID before calculations. INT entry widths
include one-byte storage. Same-cell aliases remain live, and distinct cells
with overlapping caller ranges retain their ranges for API overlap checks.
Internal storage identities do not expose caller objects or backing arrays.

Native normalization also validates an outer receiver before an API can consume
queue data or publish another receiver. The queue tests cover both outer
rejection and overlapping output aliases, preserving queued data and receivers.
Typed API output keeps the existing strict scale/byte/encoding validation.

On return or error, scalar bindings detach from the caller while retaining a
snapshot of the current bytes. Snapshot reads remain available after
cancellation; mutation keeps its cancellation checks. Later calls rebind new
entry cells. RETURN keeps
local mutations; LR and activation reclamation reload defaults/INZ. Saved
programs execute after source removal. Display and disk receiver validation
checks native conversion before acknowledging input.

## Evidence and remaining work

`RpgNativeScalarTests` covers native byte vectors at CCSID 37/1208, integer and
decimal overflow/atomicity, truncation, FLOAT(4), UTF-8 byte substrings/partial
characters, ISO defaults, CLEAR, literal text, traditional definitions, entry
layouts, live/overlapping aliases, API lifetimes, receiver rejection, real queue
retention, cancellation and storage bounds. `RpgNativeScalarIntegrationTests`
covers actual job CCSIDs, source removal, retained activation lifecycle,
QWCRSVAL, CL entry aliases on return/error and queue alias rejection.
The terminal PTY compiles/calls RPGSCALAR for monitored scalar overflow and
fractional truncation. The anonymous-filler regression checks that a real scalar
with a filler-like name stays independent of anonymous DS bytes.

Complete expression typing and native intermediate precision, arithmetic
extenders/rounding modes, additional scalar types/declaration keywords, legacy
and imported-field storage unification, complete fixed DS/procedure/local and
remaining calculation forms, pre-runtime arrays, the full entry/procedure ABI and
independent native-system golden/scale acceptance remain open. C09 remains partial. No performance target is claimed.

[Traditional calculations](rpg-fixed-calculations.md) now use native result bytes
for the documented arithmetic/indicator and call/entry subset. Remaining fixed
calculation and cycle forms retain the limits above.

Verification (2026-10-17): all 3876 tests pass with zero failures/skips,
including 55 new cases; all 1673 expanded RPG/CL/queue checks pass. Locked restore,
zero-warning/error Release build, display PTY on both screen sizes, generated
inventory and whitespace checks pass. Full-suite runtime is 8m19s, not a
performance target. Schema is 31, command catalog 306 and checklist 36 complete /
70 open. C09 remains partial.
