# Native RPG procedure pointers

`POINTER(*PROC)`, `%PADDR` and pointer-based `EXTPROC` connect native RPG
procedures through an opaque address. This extends [local procedures](rpg-local-procedures.md),
[static module binding](rpg-module-binding.md) and [service programs](rpg-service-definitions.md).
The legacy shorthand `PROCPTR` program-name field retains its earlier behavior.

```rpgle
**free
 dcl-s handler pointer(*proc) inz(%paddr(doubleIt));
 dcl-pr invoke int(10) extproc(handler);
   value int(10) const;
 end-pr;
 dcl-s result int(10);
 result = invoke(6);
 return;
 dcl-proc doubleIt;
   dcl-pi *n int(10);
     value int(10) const;
   end-pi;
   return value * 2;
 end-proc;
```

## Definitions and binding

Pointers occupy 16 bytes and default to `*NULL`. Standalone definitions, fixed
arrays, qualified/native structures, nested structures, `LIKE`, `LIKEDS`, native
parameters and return values use the same representation. Traditional D specs
use data type `*` with `PROCPTR`, an omitted or 16-byte length, and no decimal
positions. Native PR/PI pointer parameters and returns use that form too.

Pointer subfields and nested structures containing pointers align to 16 bytes;
the containing structure's length rounds up to a multiple of 16. Explicit
positions/overlays and explicit lengths must satisfy those requirements.
Arrays and multiple occurrences inherit the aligned element/structure layout.

`%PADDR` accepts a bound procedure prototype, a character/hex literal or a named
character/hex constant containing its case-sensitive entry symbol. It rejects
EXTPGM prototypes, variable character names and unsupported calling conventions.
`INZ` accepts `*NULL` or `%PADDR` of a statically named procedure. `%PADDR` of a
pointer-based prototype reads that prototype's current target at runtime and
cannot initialize a definition.

Address references participate in module/service and binding-directory discovery,
even without a callable prototype. Private procedures can supply addresses within
their own module; other modules require exports. The captured source images
rebuild those links on restore. Procedure-address imports force their service to
activate before caller effects, including when the binding mode is `*DEFER`.

## Calls, storage and lifetime

An `EXTPROC(pointer)` prototype may select a scalar pointer, an indexed pointer
array element or a qualified pointer subfield. Calls resolve the target and check
its PI against the prototype before evaluating arguments. Null, expired, foreign
and unknown addresses fail with status 202; incompatible target interfaces also
fail before argument effects. Existing native parameter rules govern reference,
CONST, VALUE, arrays, structures and return storage.

Pointers support assignment, clear/reset and equality/inequality against another
pointer or `*NULL`. Character/numeric assignments, arithmetic and ordinary scalar
conversions reject. The bytes are registry tokens, not native machine addresses;
ordering of different addresses has no portable meaning. Copying a containing
native buffer preserves its pointer fields without converting them to text.

A job shares one registry across its program and service activations. Targets
retain their original code and module storage, including private service
procedures returned by a factory. A callback re-enters its owning activation
group, including a service originally created in a caller's group. Nested calls
therefore use that original group. Procedure calls do not introduce program-owner
adopted authority. Live origin access/signing and retained snapshot trust are
checked again; the original native CCSID must remain compatible.

LR and abnormal program returns reset cycle storage while addresses keep the
activation alive. Later callbacks use its current cycle storage and retained
procedure STATIC storage. UPDPGM/UPDSRVPGM leave retained callback code unchanged.
Reclaiming the owning group, clearing the program activation or ending the job
invalidates its targets. An expired pointer fails; it does not reconnect to a
replacement activation. A fresh address can connect to the current code.

There are at most 4096 live target entries per execution registry, deduplicated
by activation and procedure. Existing native call-depth, local-storage, argument
and activation-group bounds still apply. Direct interpreter hosts can explicitly
share `RpgProcedurePointerRegistry`; otherwise each root context has its own
registry. Hosts release targets through `ReleaseProcedureAddresses` when their
activation ends; the session and service runtimes do this automatically.

## Scope and references

[Data/basing pointers](rpg-data-pointers.md), `%ADDR` and bounded heap management
are supported. Operating-system addresses,
foreign-language procedure ABIs, weak/activation-group export rules, full ILE
cycle/exception semantics and independent native golden/scale acceptance remain
open. C09 stays partial. The command catalog remains 315; source-backed module
version 1, RPGBOUND/RPGSERVICE versions 1–5, schema 31 and DISK 7/external
2/WORKSTN 5 metadata are unchanged.

The declaration/address rules follow IBM's [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 330 (procedure pointer type), 620 (comparisons) and 735 (`%PADDR`).
IBM's [service-program activation documentation](https://www.ibm.com/docs/ssw_ibm_i_74/ilec/ilesrvh.htm)
describes activation required by procedure pointer imports even with `*DEFER`.
The opaque registry and bounds above describe this emulator's implementation.

Verification (2026-10-19): all 6487 solution tests pass with zero failures/skips
in 11m06s, including 65 new cases (51 language/compiler/native-storage cases
and 14 program/service activation, security, update, LR/error and reclaim cases).
All 4231 broad and 314 focused checks, locked restore and a zero-warning/error
Release build pass. Expanded terminal acceptance covers private callback factories,
retained pointers/state and expired addresses after service reclaim. Native cases
cover CCSIDs 37/1208; general display acceptance covers 24x80 and 27x132.
Generated inventory, local file links and whitespace checks pass.
