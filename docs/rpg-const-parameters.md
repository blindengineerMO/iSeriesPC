# Native CONST parameters and character references

Native program PR/PI signatures now accept argument-free CONST on scalar
parameters. Exact native layouts retain live read-only references; expressions,
literals and different numeric formats use private converted temporaries.
Native character actuals may exceed the formal width, with the formal addressing
the corresponding prefix. C09 remains partial.

[Exact and adjusted parameters](rpg-parameter-options.md) add stricter type
checks and private *TRIM/*RIGHTADJ character images, including arrays.
[Aggregate parameters](rpg-aggregate-parameters.md) and
[variable-size references](rpg-variable-parameters.md) extend the layouts below.

These rules follow [IBM's read-only reference description](https://www.ibm.com/docs/en/i/7.6.0?topic=styles-passing-by-read-only-reference)
and CONST/OPTIONS(*VARSIZE) in the
[ILE RPG reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf),
pp. 464–465 and 507. The default fixed character reference requires at least the
formal width. Shorter writable actuals still reject; other OPTIONS modes remain open.
[OPTIONS(*NOPASS)](rpg-optional-parameters.md) and
[OPTIONS(*OMIT)](rpg-omitted-parameters.md) also work with CONST parameters.

```rpgle
**free
dcl-pr SEND extpgm('QSNDDTAQ');
  QUEUE char(10) const;
  LIBRARY char(10) const;
  LENGTH packed(5:0) const;
  DATA char(8) const;
end-pr;
callp SEND('MYQUEUE':'QGPL':5:'HELLO');
```

Each literal becomes a formal-width native value. Character data is encoded and
padded in the job CCSID; packed input uses native decimal bytes. The same CONST
keyword works in traditional D-spec signatures and main interfaces. A named PI
and its PR must agree on CONST as well as type, width, scale and raw/text layout.

## Live references and private values

An exact-layout native variable remains shared. Passing it through both CONST
and mutable formals preserves one storage range: a write through the mutable
alias is immediately visible through the CONST reference. CONST prevents writes
through that parameter; it does not freeze the underlying caller variable.
Repeated CONST aliases share a scoped reference and captured index/address.

A different numeric layout or a literal/expression produces a private native
image before target dispatch. Conversion follows the existing assignment codec,
including integer/scale truncation, signed bounds, finite float checks and default
ISO date/time storage. Numeric text requires explicit conversion. Character/raw
values retain byte-width and CCSID checks. Converted temporaries have independent
storage identities and never write their actuals back.

Arguments are prepared before the final call. Overflow, invalid type and expression
errors retain their original statuses for MONITOR; CALLP(E) does not consume them.
Aggregate converted bytes retain the 1 MiB call bound, and invalid runtime formal
metadata rejects before temporary allocation. Cells and factories expire when
the call ends.

## Read-only enforcement

PI parameters marked CONST cannot be direct assignment, CLEAR, arithmetic/loop
result, substring or native LOOKUP-index destinations. Such known writes reject
at the statement's original source location. Runtime native storage also protects
CONST PI values, including byte substrings and indirect writes.

Borrowed read-only metadata survives nested RPG/CL calls and prefixes. Native API
receivers reject read-only cells before output preparation can consume or mutate
queue/API state. The shared argument type prevents direct Value assignment and
Normalize calls on read-only arguments. Read access remains live and permits
native buffers, including opaque bytes.

External processes can echo a read-only input unchanged. Response validation uses
the parameter's native codec, compares the resulting bytes and applies only
mutable outputs. Changing a later CONST response rejects the entire response
writeback, including earlier mutable values. Nested CL-to-process calls preserve
this metadata. External side effects are not rolled back, and overlapping process
references still require an alias-capable protocol.

## Character prefixes

For native character references, the visible buffer and storage range have the
formal byte width. Reads and writes address that prefix while retaining the
caller's remaining bytes. Updates merge with current parent storage, so intervening
alias changes to the tail survive. Prefixes can pass through main PI fields and
nested calls; captured indexed elements remain selected after index changes.
UTF-8 complete-character prefixes use byte counts and preserve parent tails.

Text/raw layouts must still agree. Shorter mutable actuals reject. A shorter
CONST character value uses a padded private image. Character prefixes extend
the native byte engine; the older project scalar representation still uses its
existing exact-layout borrowing guards. Wider CCSID/collation and partial UTF-8
acceptance need independent native-system fixtures.

## Evidence and remaining work

`RpgConstParameterTests` covers live aliases, converted values, byte vectors,
native/free declarations, type/overflow/error stages, readonly PI writes, scopes,
prefix ranges/tails and resource bounds. Saved-program integration removes source
and exercises real queue input, readonly receiver rejection, native PI aliases,
numeric copies and nested prefixes in CCSID 37/1208 jobs. External-process tests
check unchanged/altered CONST responses through both direct and nested CL calls.
RPGCONST/RPGCSTUP provide terminal compilation and converted CONST/prefix checks.

Verification (2026-10-18): locked restore, a Release solution build with zero
warnings/errors, all 241 targeted checks, terminal acceptance and the full
4439-test suite pass with zero failures/skips. The 59 new cases comprise 43 unit,
12 saved-program and four external-process cases. The expanded run initially
passed 3165 of 3169 checks; four waiting/process tests failed, then all eight
cases in their focused rerun passed. The final full run is clean. Its 7m25s
runtime is verification evidence, not a performance target.

EXTPGM VALUE, remaining OPTIONS modes and general table entry ABI remain open.
[Local procedures](rpg-local-procedures.md) now support scalar VALUE and returns.
Modules/service programs/cross-unit binding, broader compatibility/CCSID modes and independent
native golden/scale acceptance remain open. The legacy project prototype option
contract remains separate. No performance target is claimed.
