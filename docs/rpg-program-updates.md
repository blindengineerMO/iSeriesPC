# RPG source binding references and updates

RPG modules can save BNDDIR control references for later binding. UPDPGM and
UPDSRVPGM replace captured modules or rebind service references, preserving existing
activations until reclaim. These extend [module binding](rpg-module-binding.md),
[service programs](rpg-service-definitions.md) and [directory discovery](rpg-binding-directories.md).
C09 remains partial.

## Source-level directories

Free source accepts BNDDIR with quoted names separated by colons:

```rpgle
**free
ctl-opt bnddir('QGPL/PROVIDERS':'EXTRAS') alwnull(*usrctl);
```

The keyword can share a control statement with ALWNULL and module-only NOMAIN,
or appear in a separate control statement. Controls precede declarations and
cannot appear inside procedures. Repeated keywords, unquoted/invalid names and
more than 16 directory names reject. Multiline control statements and preprocessed
includes preserve original source locations. Traditional H specifications accept
the same keyword in the existing supported column origins.

CRTRPGMOD records the references without opening the directories. Unqualified
names remain unresolved until binding, using that job's library list. DSPMOD lists
the saved names. Module images reconstruct references from captured source and
source maps, retaining existing image formats.

CRTBNDRPG detects references after preprocessing and selects the module-binding
path automatically. Explicit command directories are considered before module
references; the source keyword does not replace command options. When unresolved
imports remain, module reference directories are considered in module order.
Selected modules can introduce further directory references. Resolved imports do
not open unused source directories. Duplicate resolved directories are ignored,
with the existing limit of 16 distinct examined directories per bind.

Source references are build inputs. Captured programs and services do not reopen
them when called, and still work after source/directory/module removal under the
existing saved-description contracts. Existing complete-language control options,
including full DFTACTGRP and wider H-specification behavior, remain open.

## Replace modules and rebind

Update captured code after compiling replacement module objects:

```cl
UPDPGM PGM(QGPL/APP) MODULE(QGPL/HELPER)
UPDSRVPGM SRVPGM(QGPL/SERVICE) MODULE(QGPL/HELPER) EXPORT(*CURRENT)
```

MODULE is required. It accepts 1–16 explicit specifications, generic prefixes or
*ALL. Generic specifications select matching names already present in the bound
object and use available authorized replacement modules. MODULE(*NONE) replaces
no modules and rebinds current service references:

```cl
UPDPGM PGM(QGPL/APP) MODULE(*NONE)
UPDSRVPGM SRVPGM(QGPL/SERVICE) MODULE(*NONE)
```

Replacement matches bound module names. RPLLIB(*ONLY), the default, rejects an
ambiguous name. *FIRST chooses its first occurrence; *MODULE matches the
replacement module's library; a library name selects that recorded origin.
A replacement from another library retains the original bound module library and
entry identity. Replacing one slot twice, missing required matches, incompatible
interfaces or an ineligible main module rejects the complete update.

Unchanged modules come from the saved object, so their original module/source
objects are not required. Ordinary CRTBNDRPG objects with saved source metadata
can be converted into a single-module RPGBOUND envelope during update. Native
interfaces and saved file/external descriptions are revalidated before publishing.

Previously bound services are considered before additional BNDSRVPGM providers
and BNDDIR discovery. Their current signatures and ordered exports are rebound;
unused services are removed. New imports may select additional directory modules
or services, subject to existing limits. Source-level references also participate.
Current service objects remain required when rebinding existing references.
Existing activation modes are preserved unless BNDSRVPGM explicitly selects
*IMMED or *DEFER for that resolved provider. Update tuples default to *SAME; a new
provider with *SAME defaults to *IMMED. See [deferred activation](rpg-deferred-activation.md).

## Service exports

UPDSRVPGM EXPORT defaults to *CURRENT. It preserves the ordered public export
levels and signatures; adding an exported module procedure does not expose it.
Removing a required export rejects the update. A captured synthetic binder source
with exact hexadecimal signatures is used when the original service used *ALL,
so later restoration can reproduce the preserved interface.

EXPORT(*ALL) publishes all selected module exports and uses the existing
emulator-generated signature algorithm. Changed export names/order can require
client rebinding. EXPORT(*SRCFILE) reads SRCFILE/SRCMBR or SRCSTMF with the existing
authority and source limits, allowing CURRENT/PRV levels within the documented
append-only binder contract. Source options with other EXPORT modes reject.

## Authority, atomic publication and retained activations

Updates require library *USE/*ADD, target use and the owner, an effective owner
group member or nonadopted *ALLOBJ authority. Generic object-management grants
alone do not satisfy ownership. Replacement modules, service providers and
directories retain live authority and signing checks. Existing target integrity
and signing policy are checked before preparation and publication.

RPG creation commands accept ALWUPD(*YES | *NO). The default permits updates;
*NO rejects them, including for the owner. Existing objects without the attribute
retain the default. [Activation/library update policies](rpg-update-policies.md)
support *SAME defaults, named group changes and service-library relocation gated
by ALWLIBUPD(*YES). Unsupported update options reject without mutation.

The complete new artifact is prepared and restored before publication. A write
transaction compares the inspected object's timestamp, canonical contents and
signature with the current row. Concurrent edits cause IPC0130 and require a
fresh update; they are not overwritten. Cancellation, validation or integrity
failure preserves original code, attributes, signing data and change timestamp.
Successful publication preserves owner, creation time, description, authority and
activation policy, updates the code atomically and removes the stale signature.
Required-signature execution policy still applies: re-sign the updated object
before execution when required by that policy.

Activated programs and services retain their previous code, native interface,
globals and owned cursors until their activation ends. Retained program selection
occurs before entry argument binding, so an updated entry PI cannot silently
change an active version's interface. Reclaim loads the new version; other jobs
can activate it immediately. Live object authority, current-object signing and
retained-version trust checks remain mandatory. This uses in-memory activation
snapshots rather than materializing native QRPLOBJ copies.

RPGBOUND/RPGSERVICE version 4 supports runtime *LIBL references; version 3
supports deferred references, while explicit immediate-only images retain versions 1/2. Schema 31 and DISK 7/external
description 2/WORKSTN 5 remain unchanged. The curated catalog contains 315 names after adding
UPDPGM and UPDSRVPGM as partial implementations.

[Native procedure pointers](rpg-procedure-pointers.md) retain callback code through
updates and invalidate addresses when their owning activation is reclaimed.

## Remaining work and references

[Named activation changes and service-library relocation](rpg-update-policies.md)
are supported within the documented contract. Native update/listing/event options,
wider program/module languages and ABI, wider pointer contracts, and independent
native golden/scale acceptance remain open.

The contract follows IBM's [BNDDIR keyword reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf)
(printed page 369), [UPDPGM](https://www.ibm.com/docs/en/i/7.5.0?topic=u-update-program),
[replacement selection](https://www.ibm.com/docs/en/i/7.5.0?topic=ssw_ibm_i_75%2Fcl%2Fupdpgm.html)
and [UPDSRVPGM](https://www.ibm.com/docs/en/i/7.4.0?topic=u-update-service-program).
The strict formats, publication transaction and emulator limits do not establish
complete native parity.

Verification (2026-10-18): all 6253 solution tests pass with zero failures/skips
in 10m26s, including 54 new cases (19 controls, 33 update/binding/security/
publication and two retained-cursor cases). All 3997 broad and 345 focused
checks, locked restore and a zero-warning/error Release build pass. Expanded
terminal acceptance covers source BNDDIR, replacement modules, old/new activation
versions, service EXPORT(*CURRENT), reclaim and MODULE(*NONE). Native/file cases
cover CCSIDs 37/1208; general display acceptance covers 24x80 and 27x132. Generated
inventory, local links and whitespace checks pass.
