# RPG binding directories and service dependencies

Binding directories are ordered `*BNDDIR` objects used by CRTPGM, CRTBNDRPG and
CRTSRVPGM to discover native RPG providers. CRTSRVPGM also accepts explicit
BNDSRVPGM dependencies. This extends [module binding](rpg-module-binding.md) and
[dynamic services](rpg-service-definitions.md). C09 remains partial.

## Directory commands

Create a directory, add providers and inspect its contents:

```cl
CRTBNDDIR BNDDIR(QGPL/PROVIDERS) TEXT('RPG providers') AUT(*USE)
ADDBNDDIRE BNDDIR(QGPL/PROVIDERS) OBJ((QGPL/MHELP *MODULE) (QGPL/SERVICE *SRVPGM))
DSPBNDDIR BNDDIR(QGPL/PROVIDERS)
```

Entries contain a library/name, *MODULE or *SRVPGM type and *IMMED/*DEFER
activation control. The type defaults to *SRVPGM; activation defaults to *IMMED.
Service providers retain the selected mode when bound. Modules are copied into
the caller and have no separate service activation. Entry library *LIBL is saved for resolution by the
binding job. Explicit libraries are saved literally. *CURLIB input is resolved
when adding an entry. Referenced objects need not exist when entries are added.

ADDBNDDIRE preserves input order. POSITION accepts *LAST (default), *FIRST,
*BEFORE, *AFTER and *REPLACE. Relative positions identify an existing entry by
library/name and type, for example:

```cl
ADDBNDDIRE BNDDIR(QGPL/PROVIDERS) OBJ((QGPL/OTHER *MODULE)) POSITION(*BEFORE QGPL/MHELP *MODULE)
```

Replacement accepts one new entry. Exact duplicate library/name/type identities
reject within a directory. Missing anchors, invalid entries, duplicate entries or
quota violations leave the complete payload and change timestamp unchanged.
Concurrent additions read the latest payload inside their write transaction.

RMVBNDDIRE accepts explicit names, prefix patterns and *ALL, with explicit
libraries or *ALL libraries and *MODULE/*SRVPGM/*ALL types:

```cl
RMVBNDDIRE BNDDIR(QGPL/PROVIDERS) OBJ((QGPL/M* *MODULE))
RMVBNDDIRE BNDDIR(QGPL/PROVIDERS) OBJ((*ALL/*ALL *ALL))
DLTBNDDIR BNDDIR(QGPL/PROVIDERS)
```

Each selector must match an original entry before removal begins; overlapping
selectors remove each matching entry once. Failed multi-entry changes roll back
as one operation. Display returns ordered numbered entries. Unsupported command
options reject; interactive WRKBNDDIR/WRKBNDDIRE, output-file/print options and
broader generic library selectors remain open.

Unqualified creation uses the job's current library; directory lookup uses its
library list. QTEMP directories are private to their job. Entry storage uses a
strict version 1 object payload, with at most 256 entries and 512 KiB encoded
UTF-8. Unknown/duplicate/case-variant fields, invalid identities, types or
activation controls reject. Object copy/rename and the existing object save/delete
lifecycle retain the payload without a separate directory-entry table.

Creation uses destination-library authority and AUT(*LIBCRTAUT), explicit public
authorities or an authorization list. Authorization-list attachment is in the
creation transaction. Adding entries requires directory *OBJOPR/*ADD; removal
requires *OBJOPR/*DLT. These edits do not require directory *READ or *OBJMGT.
Display and binding require directory *USE. These operations require library
*USE; referenced modules/services retain their own authority and signing checks
when examined for binding. Entry creation does not grant authority to a provider.

## Provider discovery

Use directories with all three creation commands:

```cl
CRTPGM PGM(QGPL/APP) MODULE(QGPL/MMAIN) BNDDIR(QGPL/PROVIDERS)
CRTBNDRPG PGM(QGPL/SOURCEAPP) SRCFILE(QGPL/SOURCE) SRCMBR(MMAIN) BNDDIR(QGPL/PROVIDERS)
CRTSRVPGM SRVPGM(QGPL/WRAPPER) MODULE(QGPL/WRAPMOD) BNDDIR(QGPL/PROVIDERS) EXPORT(*ALL)
```

BNDDIR accepts up to 16 explicit directories, or *NONE. Directories and entries
are considered in list order. Explicit module exports and BNDSRVPGM providers
resolve first. A candidate is selected only if it exports a currently unresolved
native import. Selected modules are copied into the new object; selected services
remain dynamic references. Unselected providers are excluded. Discovery stops
when imports are satisfied and revisits earlier candidates when selected modules
introduce new imports. Duplicate directory entries and duplicate resolved object
identities do not add a second provider.

Missing candidates produce CPD5D1D diagnostics and discovery continues. Successful
creation messages include those diagnostics. If no provider resolves a remaining
import, creation fails. Unused trailing candidates are not opened. Examined
objects require live use authority and signing/integrity checks. A legacy semantic
procedure name does not satisfy a native import or intercept native dispatch.
Native interface, duplicate static export and source/metadata checks run before
publication.

The existing limits of 16 selected modules and 16 selected services apply.
Examined directory artifacts, including saved metadata, are bounded to 64 MiB
combined UTF-8 per bind. Captured module/source/image/global-storage budgets
remain in force. These are emulator limits, not native capacity claims.

Directories are build inputs. Removing or editing them after creation does not
change a captured program/service. Captured modules and compile-time descriptions
can be removed under the existing contracts. Dynamic service objects and runtime
files remain live dependencies.

## Service dependency graphs

A service can import native procedures from other services through BNDSRVPGM or
BNDDIR. Only required dependencies are saved. Public exports still come from its
own selected modules; dependency exports remain available through procedure
wrappers. Version 2 RPGSERVICE images save immediate dependency identities,
signatures and ordered exports. Version 3 also records deferred activation modes;
version 4 adds runtime library-list lookup under [update policies](rpg-update-policies.md). Version 1 self-contained services remain readable,
including older images without a dependency field. Strict dependency validation
rejects unknown/duplicate fields, oversized arrays, inconsistent references and
unsupported versions. DSPSRVPGM lists direct service dependencies.

Before program main side effects, activation checks the immediate dependency
closure for use/signing, signature levels, export positions and native interfaces.
[Deferred edges](rpg-deferred-activation.md) are followed when a procedure is
called; its immediate dependency closure is checked before entering its body. Dependency
checks occur in the parent service's activation group. A *CALLER dependency of a
named parent therefore shares that parent's group, rather than the original
program's group. Distinct groups and jobs keep separate state as before.

Preflight tracks activation identity, so cycles terminate while each dependency
edge is still validated. It does not initialize module data. The graph is limited
to 64 dependency levels and 256 distinct activations per preflight. Cyclic calls
and reentrant procedure execution retain the existing native frame limits and
owning module state. Previous signature levels can satisfy saved dependencies
when the ordered exports and native interfaces remain compatible.

Dependency code/group policy remains pinned for its activation. Live authority,
signing and runtime-file checks continue on later calls. Reclaiming a named child
group invalidates weak cached bindings; a surviving parent reactivates the child
on its next call. Parent reclaim does not discard a separately named child's
cursor. Structure/null-map aliases and declared file state retain their existing
ownership through dependency chains, including at CCSIDs 37/1208 after original
source, directory and compile-time description deletion.

Binding directories, dependency graphs and the captured-module formats do not
change database schema 31 or DISK 7/external description 2/WORKSTN 5 bindings.
The curated catalog contains 313 names after adding CRTBNDDIR, DLTBNDDIR,
ADDBNDDIRE, RMVBNDDIRE and DSPBNDDIR as partial implementations.

## Remaining contracts and references

[Source references and updates](rpg-program-updates.md) add embedded BNDDIR controls
and UPDPGM/UPDSRVPGM replacement/rebinding. [Deferred activation](rpg-deferred-activation.md)
and [activation/library policies](rpg-update-policies.md) are supported.
[Data binding](rpg-data-binding.md) adds discovery for exported variables. Broader binder mapping
and calling conventions, richer directory work/output operations and independent
native golden/scale acceptance remain open.

The workflow follows IBM's [binding-directory concept](https://www.ibm.com/docs/en/i/7.4.0?topic=concepts-binding-directory),
[binding processing](https://www.ibm.com/docs/en/i/7.5.0?topic=directory-binding-processing),
[add-entry command](https://www.ibm.com/docs/en/i/7.4.0?topic=beginning-add-binding-directory-entry),
[remove-entry command](https://www.ibm.com/docs/en/i/7.5.0?topic=r-remove-binding-directory-entry),
[display command](https://www.ibm.com/docs/en/i/7.4.0?topic=d-display-binding-directory),
[create command](https://www.ibm.com/docs/en/i/7.5.0?topic=c-create-binding-directory)
and [nonfatal missing-provider diagnostic](https://www.ibm.com/support/pages/cpd5d1d-crtpgm-or-crtsrvpgm).
The strict payload, transaction boundaries, discovery algorithm and quotas above
are the emulator's supported contract; they do not establish full native parity.

Verification (2026-10-18): all 6199 solution tests pass with zero failures/skips
in 9m58s, including 85 new cases (40 directory lifecycle/authority/image/
concurrency, 19 discovery, 22 dependency and four record/ABI cases). All 3943
broad and 291 focused checks, locked restore and a zero-warning/error Release
build pass. Expanded terminal acceptance covers directory lifecycle, selected
static modules, service dependencies, shared leaf counters and dependency reclaim.
Native/record cases cover CCSIDs 37/1208; general display acceptance covers 24x80
and 27x132. Generated inventory, local links and whitespace checks pass.
