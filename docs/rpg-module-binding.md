# Static RPG module binding

`CRTRPGMOD` creates source-bound `*MODULE` objects. `CRTPGM` copies an explicit
module list into a callable `*PGM` and resolves exported native procedures.
`DSPMOD` displays entry eligibility and saved import/export names. These extend
[local procedures](rpg-local-procedures.md), [native parameter signatures](rpg-program-signatures.md)
and [null-map parameters](rpg-null-parameters.md). C09 remains partial.

## Create and bind

Save a main source member MMAIN:

```rpgle
**free
ctl-opt alwnull(*usrctl);
dcl-pr TARGET extproc('FILL');
  P char(8) options(*nullind);
end-pr;
dcl-s TEXT char(8) nullind;
callp TARGET(TEXT);
if TEXT='   BOUND' and %nullind(TEXT);
  sndpgmmsg 'RPG MODULE BIND OK';
endif;
return;
```

Save an export source member MHELP:

```rpgle
**free
ctl-opt nomain alwnull(*usrctl);
dcl-proc FILL export;
dcl-pi *n;
  P char(8) options(*nullind);
end-pi;
evalr P='BOUND';
eval %nullind(P)=*on;
end-proc;
```

Compile, inspect and bind them:

```cl
CRTRPGMOD MODULE(QGPL/MMAIN) SRCFILE(QGPL/SOURCE)
CRTRPGMOD MODULE(QGPL/MHELP) SRCFILE(QGPL/SOURCE)
DSPMOD MODULE(QGPL/MHELP)
CRTPGM PGM(QGPL/RPGBIND) MODULE(QGPL/MMAIN QGPL/MHELP) ACTGRP(MODPTY)
CALL QGPL/RPGBIND
```

Unqualified creation uses the job's current library; module lookup uses the job
library list. QTEMP module/program objects remain private to their job. CRTRPGMOD
accepts SRCFILE/SRCMBR or SRCSTMF and the existing authority-aware preprocessing
and saved source-map rules. SRCMBR defaults to the module name. Modules cannot be
called directly.

NOMAIN permits global declarations and exported subprocedures, with no main
calculations or main PI. NOMAIN and ALWNULL may be combined or specified separately
before declarations; each keyword can appear only once. NOMAIN is module-only.
Ordinary CRTBNDRPG source still requires local procedure resolution.

## Binding contract

CRTPGM accepts 1–16 explicit module names. ENTMOD(*FIRST), the default, selects the
first supplied module that has a main procedure. ENTMOD(qualified-module) selects
an explicitly supplied main module. The program receives the existing ACTGRP
policy; RETURN, LR, named groups, *NEW and activation reclaim retain their normal
meaning.

Only EXPORT procedures satisfy imports from another module. Names resolve
case-sensitively; private procedures remain local. Duplicate exports, unresolved
imports, duplicate modules and incompatible return/parameter layouts or modes
reject the program before creation. The emulator compares the saved native
interface metadata at bind time. It does not implement a native object-file ABI.

Prototyped CALLP, the supported CALLB form and native function expressions use the
same borrowed argument scopes as local procedures. Module CALLB requires a native
procedure prototype; it cannot fall through legacy semantic dispatch. References,
CONST/VALUE, structures, file parameters and null maps retain their existing
contracts. Recursive calls and callbacks use the owning module's global context.

The bound program contains captured module source, preprocessing maps and DISK,
WORKSTN and EXTNAME descriptions. Removing or changing original source members,
modules or compile-time-only descriptions does not change captured code. Runtime
file dependencies remain live. A newly bound program captures new module versions.

## State, authority and cleanup

Global data initializes when the module's first procedure is invoked. Each module
has separate globals, indicators and procedure STATIC resources within the bound
program's activation. Programs bound from the same source modules have separate
activation resources. Entry LR restarts its cycle storage while retaining the
other modules' activation state. Activation reclaim, job end or failed program
execution closes owned module files and static resources.

Module-owned global file cursors retain their positions across calls. Borrowed
LIKEFILE parameters share the caller's cursor. Live file authority is rechecked
on subsequent calls; a failed activation is discarded and a later successful call
starts fresh. Source and compile-time external descriptions may be removed when
they are not runtime dependencies.

Creation requires source access and destination-library authority. Binding and
DSPMOD require module use authority and enforce existing module signing policy.
Calls require program authority and verify the exact captured program image.
Bound procedures execute under the program's current identity; module owners do
not introduce separate adopted authority. Runtime file authority remains live.

The version 1 bound image rejects unknown/duplicate/case-variant properties,
noncanonical identities, stale source maps and inconsistent saved descriptions.
It bounds the module array before deserialization. Combined source is limited to
4 MiB UTF-8, saved metadata to 16 MiB, encoded bound images to 32 MiB and declared
module globals to 64 MiB. Existing per-call/frame and per-module STATIC limits
also apply. These are emulator limits, not native capacity or performance claims.

## Remaining ILE scope

[Service programs](rpg-service-definitions.md) now support creation/inspection,
binder source/signature levels and dynamic BNDSRVPGM calls with shared service
activation state. [Directory discovery and service dependencies](rpg-binding-directories.md)
are also implemented within their documented contract. [Source references and
updates](rpg-program-updates.md) add embedded BNDDIR controls and checked
UPDPGM/UPDSRVPGM replacement/rebinding. [Activation policies](rpg-update-policies.md),
[deferred activation](rpg-deferred-activation.md) and [data binding](rpg-data-binding.md)
add checked service connections and shared storage.
[Native procedure pointers](rpg-procedure-pointers.md) add address imports and callbacks.
[Data pointers](rpg-data-pointers.md) expose activation-owned native storage. Wider calling conventions/
languages, wider pointer ABIs, full native cycle/activation/exception semantics
and independent native golden/scale acceptance remain open.

Schema 31 and DISK 7/external description 2/WORKSTN 5 bindings are unchanged.
Module metadata uses `ipc.rpg.moduleVersion=1`; bound programs use the RPGBOUND
attribute and a separate version 1 captured-module envelope. The curated catalog
now contains 307 names: CRTRPGMOD/CRTPGM gain implemented partial contracts and
DSPMOD is added.

The workflow follows IBM's [CRTRPGMOD documentation](https://www.ibm.com/docs/en/i/7.5.0?topic=object-using-crtrpgmod-command),
[CRTPGM documentation](https://www.ibm.com/docs/en/i/7.6.0?topic=program-using-crtpgm-command)
and [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed page 128 for NOMAIN and module initialization. The source-bound format,
interface checks and limits above are the emulator's supported contract.

Verification (2026-10-18): all 5962 solution tests pass with zero failures/skips
in 9m31s, including 61 new cases. All 3706 broad and 61 final focused checks,
locked restore, a zero-warning/error Release build and expanded terminal module
compile/inspect/bind/call acceptance pass. Native/file cases cover CCSIDs 37/1208;
general display acceptance covers 24x80 and 27x132. Generated inventory, local
links and whitespace checks pass.
