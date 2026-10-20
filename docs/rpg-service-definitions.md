# RPG service programs and dynamic binding

`CRTSRVPGM` creates source-bound `*SRVPGM` objects from captured RPG modules.
`DSPSRVPGM` validates and displays their modules, public exports, signatures and
activation-group policy. CRTPGM and CRTBNDRPG bind native procedure imports through
BNDSRVPGM references. Activated services share their own module globals, procedure
STATIC data and declared files within their activation group. C09 remains partial. [Directory discovery and service dependencies](rpg-binding-directories.md)
extend this contract.

## Creation and inspection

Compile the MHELP module from the [module-binding example](rpg-module-binding.md).
Export all its native EXPORT procedures:

```cl
CRTSRVPGM SRVPGM(QGPL/SERALL) MODULE(QGPL/MHELP) EXPORT(*ALL)
DSPSRVPGM SRVPGM(QGPL/SERALL)
```

Alternatively, save this binder source as QGPL/QSRVSRC(SERVICE):

```text
STRPGMEXP PGMLVL(*CURRENT) SIGNATURE('SERVICE V2')
  EXPORT SYMBOL(FILL)
ENDPGMEXP
STRPGMEXP PGMLVL(*PRV) SIGNATURE('SERVICE V1')
  EXPORT SYMBOL(FILL)
ENDPGMEXP
```

Create and inspect the definition:

```cl
CRTSRVPGM SRVPGM(QGPL/SERVICE) MODULE(QGPL/MHELP) ACTGRP(SHARED)
DSPSRVPGM SRVPGM(QGPL/SERVICE)
```

EXPORT defaults to *SRCFILE, SRCFILE to QSRVSRC, and SRCMBR to the service name.
SRCFILE/SRCMBR may be overridden, or SRCSTMF may select a strict UTF-8 regular file
under the existing source-stream authority rules. Stream and member options
cannot be combined. EXPORT(*ALL) rejects binder-source options. Unqualified
creation uses the job's current library; module/service lookup uses its library
list. QTEMP definitions remain private to their job. Existing objects are never
replaced by this command.

ACTGRP accepts *CALLER (the default) or a valid named group; *NEW is rejected.
Creation and inspection do not run main calculations, initialize globals or create
runtime activation state.

## Binder and module checks

One *CURRENT export block must precede any *PRV blocks. Each block contains
STRPGMEXP, one or more EXPORT SYMBOL entries, and ENDPGMEXP. Keywords are case
insensitive. Unquoted symbols become uppercase; quoted symbols preserve their
case and match exports exactly. The supported symbols use ASCII identifier
syntax, up to 128 characters. Native RPG procedure declarations currently export
uppercase names. Comments use /* ... */; + or - continues a record with a space.
A command may have one trailing semicolon. Nested/incomplete blocks, repeated or
unknown options, duplicate exports/signatures and unsupported operations reject.

PGMLVL defaults to *CURRENT, SIGNATURE to *GEN, and LVLCHK supports only *YES.
Literal signatures contain 1–16 EBCDIC 37 bytes and are padded on the right with
EBCDIC spaces. X'...' signatures specify exactly 16 bytes. The emulator's *GEN
uses the first 16 bytes of SHA-256 over UTF-8 export names joined by NUL, in export
order. It is deterministic within this format and does not reproduce native IBM
generated signature bytes. EXPORT(*ALL) uses module-list and procedure-source
order.

Previous export lists must be prefixes of the current list. This is an explicit
append-only subset of binder compatibility; other export mappings are rejected.
There may be at most 16 levels and 256 exports per level. Binder source is limited
to 1 MiB of UTF-8. Source errors retain the binder identity and line number.

Services accept 1–16 distinct modules and need no main entry. Internal native
procedure imports resolve to local procedures or exported procedures in the
supplied modules. Missing/private/duplicate symbols and incompatible return or
parameter layouts/modes reject before object creation. Binder source can select
and reorder public exports; hiding a procedure does not remove its availability
to internal module imports. Main calculations in supplied modules are captured
without execution. Imports from other services use the dependency-graph contract below.

## Dynamic binding and calls

Bind the main module from the [module example](rpg-module-binding.md) to the
service, or compile its source directly with service references:

```cl
CRTPGM PGM(QGPL/RPGSERV) MODULE(QGPL/MMAIN) BNDSRVPGM(QGPL/SERVICE) ACTGRP(CALLERS)
CRTBNDRPG PGM(QGPL/SRCSERV) SRCFILE(QGPL/SOURCE) SRCMBR(MMAIN) BNDSRVPGM(QGPL/SERVICE) ACTGRP(CALLERS)
CALL QGPL/RPGSERV
CALL QGPL/SRCSERV
```

BNDSRVPGM accepts up to 16 explicit service names or (service activation) tuples,
resolved through the job library list; *NONE selects no services. Activation
defaults to *IMMED and also accepts [*DEFER](rpg-deferred-activation.md). Duplicate resolved qualified names reject.
Local procedures and static module exports resolve first, followed by services in
explicit list order. Unused services are excluded from the saved program.
Missing exports and incompatible native return/parameter interfaces reject before
program creation. A service's hidden exports remain unavailable to callers.

Bound programs keep captured static modules plus each required service's absolute
library/name, signature and ordered export table. They do not copy service code.
Before main execution, the host checks immediate service references, matching
signature levels, export positions and native interfaces. Missing, unauthorized,
altered or incompatible immediate services fail before main side effects.
Deferred references are located and checked when a bound procedure is called. An append-only upgrade with
a matching PRV signature and unchanged interface can satisfy an older program.

CALLP, prototyped CALLB and native function expressions use the existing reference,
CONST/VALUE, return, structure, file and null-map contracts. A service procedure
interface is checked before argument effects. Caller arguments are evaluated and
captured in the caller's activation group; the service scope begins afterward.
Recursion and reentrant calls through other programs use the activated service's
module storage. A service's main calculations are never invoked by this path.

## Activation state and cleanup

ACTGRP(*CALLER) shares one service activation between programs in the same caller
group, with separate copies in other groups and jobs. A named service group can
share state across distinct caller groups. Services own their module contexts;
caller programs do not close them when returning, setting LR, failing or being
reclaimed. Module globals initialize on first procedure use. Procedure errors keep
completed global updates while automatic frames and borrowed references unwind.
Native module buffers retain their original CCSID.

Existing bindings retain activated code and group policy until the service
activation is reclaimed. Live object use authority, signing and trust remain
mandatory on later calls. A newly bound signature cannot use an older activation
that lacks that signature; reclaim loads the new definition. Reclaiming a named
service group invalidates cached caller bindings, which reactivate on their next
use. An active service group cannot be reclaimed while its procedure is on the
stack. Caller arguments execute before entering the service group.

Declared global file cursors are shared between service callers. LIKEFILE
parameters continue to borrow caller-owned cursors. File authority and layout
checks remain live; granting access again can resume a retained cursor. Service
group reclaim, transient *NEW caller-group exit and job end close owned service
files and STATIC resources. Named service groups survive caller-group reclaim.
Trusted calls without a job use service storage only for the outermost call,
sharing it with nested calls and disposing it on return.

## Saved images and authority

Self-contained RPGSERVICE images use a strict version 1 JSON envelope containing
module source/maps, file/external descriptions, ordered export levels and optional
binder source. Inspection recompiles captured modules, rechecks their internal
interfaces, rebuilds export levels and compares them with the saved definitions.
Unknown/duplicate/case-variant properties, invalid identities, inconsistent
exports/signatures and stale source/description metadata reject. Arrays are
bounded before typed deserialization. Captured modules retain the existing limits:
4 MiB combined UTF-8 source, 16 MiB metadata, 64 MiB declared global storage and
32 MiB encoded service image. Original modules, source members, binder files and
compile-time-only descriptions may be removed after creation.

Creation requires source access, module use and destination-library authority.
Module signing policy is checked before capture. Binding, inspection and calls
require service use and verify service signing/integrity. Original module/source
authority is not a later service dependency. Service procedure calls keep the
calling program's effective identity; service owners add no adopted authority.
Runtime file authority is checked under that identity.

[Service dependencies](rpg-binding-directories.md#service-dependency-graphs) may be
bound explicitly with CRTSRVPGM BNDSRVPGM or discovered through BNDDIR. Transitive
preflight checks the immediate dependency closure before main execution, in each
parent's group;
cycles are supported within the documented graph bounds.

Version 1 RPGBOUND programs remain supported. Programs with immediate dynamic service
references use a version 2 RPGBOUND envelope; strict validation rejects unknown,
duplicate and case-variant reference properties, invalid identities, oversized
arrays and inconsistent export tables. Self-contained services retain their version 1 RPGSERVICE envelope; services with
immediate dynamic dependencies use version 2; images with deferred references use
version 3. Version 4 adds [runtime *LIBL lookup](rpg-update-policies.md) with a
canonical binding origin. Version 5 adds [typed data exports](rpg-data-binding.md);
interfaces without data retain their earlier shapes. Schema 31 and DISK 7/external description 2/WORKSTN 5 are unchanged.

## Remaining work and references

[Source references and updates](rpg-program-updates.md) add module directory
controls and UPDPGM/UPDSRVPGM with preserved export levels and retained activations.
[Deferred procedure activation](rpg-deferred-activation.md) is supported.
[Global data imports/exports](rpg-data-binding.md) share activation-owned bytes
and null maps. [Native procedure pointers](rpg-procedure-pointers.md) add
private callback factories and activation-bound addresses. [Data pointers](rpg-data-pointers.md) expose private service storage with
retained ownership and reclaim invalidation. Broader
ABI/calling conventions and independent native acceptance remain open. CALL resolves *PGM objects;
service exports are reached through bound native procedure interfaces.

The definition workflow follows IBM's [EXPORT parameter documentation](https://www.ibm.com/docs/en/i/7.5.0?topic=access-export-parameter-crtsrvpgm-command),
[Export Symbol command](https://www.ibm.com/docs/en/i/7.6.0?topic=language-export-symbol-command),
[Start Program Export List](https://www.ibm.com/docs/en/i/7.4.0?topic=s-start-program-export-list)
and [service activation-group rules](https://www.ibm.com/docs/en/i/7.5.0?topic=management-service-programs-activation-groups).
The image format, limits, generated-signature algorithm and append-only subset
above are emulator contracts.

Service-definition checkpoint (2026-10-18): all 6056 solution tests pass with zero failures/skips
in 9m40s, including 94 new cases (47 binder/compiler and 47 command/image/security).
All 3800 broad and 148 focused checks, locked restore, a zero-warning/error
Release build and real terminal service creation/inspection acceptance pass.
General display acceptance covers 24x80 and 27x132. Generated inventory, local
links and whitespace checks pass.

Dynamic-service verification (2026-10-18): all 6114 solution tests pass with zero
failures/skips in 10m00s, including 58 new cases (48 binding/activation/security/
lifecycle and ten file/structure/null-map cases). All 3858 broad and 206 focused
checks, locked restore and a zero-warning/error Release build pass. Expanded real
terminal acceptance verifies dynamic binding, borrowed null maps, shared counters,
caller reclaim and service reclaim. Native/file cases cover CCSIDs 37/1208; general
display acceptance covers 24x80 and 27x132. Generated inventory, local links and
whitespace checks pass.
