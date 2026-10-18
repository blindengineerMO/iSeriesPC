# Forward native type references

LIKE can refer to later native fields, subfields, PI parameters and explicit or
implicit procedure return types. The compiler resolves type metadata separately
from source-order storage and initialization binding. This closes the forward
type gap in the [LIKE contract](rpg-like-declarations.md); C09 remains partial.

```rpgle
**free
dcl-s RESULT like(MAKE);
eval RESULT=MAKE();
return;
dcl-proc MAKE;
  dcl-pi *n like(TEXT) rtnparm end-pi;
  dcl-c WIDTH 6;
  dcl-s TEXT char(WIDTH);
  return 'ABCDEF';
end-proc;
```

RESULT is a six-byte character field containing ABCDEF. MAKE's implicit return
metadata uses its later local TEXT definition and the constant available at
that definition. It does not inherit TEXT's initialization or storage.

The supported base types, adjustments, CCSID modes and bounds remain those in
the [LIKE reference](https://www.ibm.com/docs/en/i/7.4.0?topic=keywords-likename-length-adjustment)
and the repository's native declaration contract. Independent IBM-system
acceptance remains open; this is implementation evidence for the finite source
forms described here.

## Resolution and scope

The compiler indexes declarations on demand and caches their resolved metadata.
Standalone and subfield chains can point forward. Later members in the current
DS are visible to LIKE, including anonymous structures. Qualified members of a
later structure or LIKEDS copy retain their element types and identities. Whole
structures provide character byte layouts. Final INFDS/INDDS widths are included,
including padding inherited through a LIKEDS template chain.

An explicit PR supplies its return type; otherwise the local procedure's PI
supplies implicit return metadata. That return can depend on later local types.
A later local definition shadows the same global name throughout type resolution.
Constant widths respect that scope as well. Source definition identities survive
preview and final binding, preserving OPTIONS(*EXACT) checks through forward
aliases, subfields, parameters, indexed actuals and returned values.

Metadata evaluation uses only declaration constants and layouts. A future type
may depend on constants available before its own definition, including supported
layout built-ins and prior PI parameter numbers. Ordinary initialization and
named-constant declarations retain their existing requirement for previously
available values; forward type lookup does not execute a procedure to discover
its value. Scalar/DS initializers are omitted from previews and handled at their
original declarations. Storage order, overlay positions, defaults and passing
flags retain their existing rules.

## Errors and bounds

Missing or void return types fail compilation. Cycles, including dependencies
through constants or implicit returns, report a declaration type diagnostic.
Active dependency resolution is bounded to 64 levels across module/procedure
scopes, with cancellation checks at each resolution step. Existing source,
declaration, type-width, dimension and storage limits still apply.

Declaration errors retain their expanded source positions and original include
locations. No program is created after a cyclic or incompatible exact type error.
Compiler lookup callbacks are removed on success and failure; runtime invocation
uses the final metadata and does not perform dependency resolution.

## Evidence and remaining work

Unit checks cover later primitive/structure/member/PI types, explicit and implicit
return layouts, constant/local shadowing, default/overlay preservation, raw bytes,
missing definitions, cycles and depth admission, and both fixed column origins.
Saved programs cover source removal, implicit RTNPARM returns, real queue bytes,
final feedback widths and include-file diagnostics at CCSID 37/1208. Process
checks verify readonly echo/atomic rejection and no launch after incompatible
exact types. RPGFORWARD provides terminal compile/call acceptance.

[Forward LIKEDS binding](rpg-forward-structures.md) also supports later structure
templates and inherited initialization.

This extends LIKE type lookup. General forward value expressions, implicit calculation-result field types,
external/object/enumeration forms, varying/graphic/
UCS-2/pointer types, nondefault date/time formats, WORKSTN/EXTNAME record layouts, foreign ABI and
cross-unit module/service-program binding remain open. See
[declaration constants](rpg-declaration-constants.md),
[program signatures](rpg-program-signatures.md) and
[parameter options](rpg-parameter-options.md).

Verification (2026-10-18): all 5026 solution tests pass with zero failures/skips,
including 49 new cases: 38 unit, eight saved-program integration and three
external-process cases. All 1154 focused checks, locked restore, a Release build
with zero warnings/errors, final RPGFORWARD terminal acceptance, generated
inventory, local links and whitespace checks pass. An earlier full run was
interrupted after source review found a case-normalization issue; the final run
includes the correction and lowercase/mixed-case regression cases. Full-suite
wall time is 7m49s; no performance target is claimed. RPGFORWARD runs at 24x80;
general display acceptance also covers 27x132. C09 remains partial.

[DISK LIKEREC record structures](rpg-record-structures.md) now supply native
layouts, result operands and parameter/return types.
