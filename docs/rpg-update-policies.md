# RPG activation and service-library update policies

UPDPGM and UPDSRVPGM support ACTGRP(*SAME) and changes between named activation
groups. A program or service created with *CALLER, or a program created with *NEW,
must retain that policy. Named groups cannot be changed to either special value.
Invalid changes reject the complete update before publication.

```cl
CRTPGM PGM(QGPL/APP) MODULE(QGPL/MAIN) BNDSRVPGM(FIRSTLIB/SERVICE) ACTGRP(APPGROUP) ALWLIBUPD(*YES)
UPDPGM PGM(QGPL/APP) MODULE(*NONE) ACTGRP(NEWGROUP) SRVPGMLIB(SECONDLIB)
UPDPGM PGM(QGPL/APP) MODULE(*NONE) SRVPGMLIB(*LIBL)
```

Changing a program's named policy directs subsequent dynamic calls to that
group. Previously created groups retain their captured code, interfaces, globals
and file cursors until reclaim. Returning to a group that retains the program
uses that activation. Existing service connections keep their captured service
group and version; new connections use the service's updated policy. Reclaiming
an old service group refreshes those connections into the new group.

CRTPGM, CRTBNDRPG and CRTSRVPGM accept ALWLIBUPD(*YES | *NO). The default is *NO,
including for older objects without the attribute. A non-*SAME SRVPGMLIB update
requires *YES even when no services are currently bound; ALWUPD(*NO) continues to
forbid the entire update. Flags, authority and object integrity remain enforced
for owners and privileged users.

SRVPGMLIB(*SAME), the default, preserves each existing reference's lookup policy.
A named library first looks for each already bound service in that library. A
found provider replaces the reference's library and is rebound by symbol with
its current signature and checked native interface. A missing preferred service
retains the original provider and lookup policy. Authorization, signing, malformed
artifacts and incompatible interfaces are failures and do not trigger fallback.
Additional BNDSRVPGM/directory providers keep their normal binding precedence.

SRVPGMLIB(*LIBL) searches the updating job's library list. A found provider records
*LIBL for runtime lookup, together with its resolved binding origin. A missing
preferred provider uses the same fallback rule. Later *SAME updates retain dynamic
lookup and rebind using the current list, falling back to the saved origin for
binding when necessary. Named relocation can return the reference to a fixed
library. Service dependency images use the same policies.

At runtime each caller image resolves a dynamic reference using its own job's
current list when activation is required. [Deferred references](rpg-deferred-activation.md)
wait until the procedure is called. A successful connection retains its actual
provider while the caller remains activated, despite subsequent library-list
changes. Another caller can resolve a different library, including in the same
group. References that reach the same qualified provider and activation group
share service storage. Live authority/signing checks apply to the connected
provider, plus the retained snapshot's trust; another same-named object cannot
substitute for those checks. Reclaim or a fresh caller activation permits lookup
again. Runtime lookup has no fallback to an origin outside the current list.

RPGBOUND/RPGSERVICE version 4 records *LIBL lookup metadata and a canonical binding
origin. Explicit-only references retain the prior version 1/2/3 formats. Strict
validation rejects invalid lookup controls or inconsistent versions. DSPSRVPGM
shows *LIBL for dynamic dependencies. Database schema 31, record metadata formats
and catalog count 315 are unchanged.

The new artifact and requested group policy publish in one transaction after
complete restoration and comparison with the inspected object. Cancellation,
concurrent changes, failed binding or denied policy preserves the original
source, group, flags, signatures and timestamp. Success preserves other metadata
and removes stale signing as in the existing [update contract](rpg-program-updates.md).

These policies follow IBM's [UPDPGM](https://www.ibm.com/docs/en/i/7.5.0?topic=ssw_ibm_i_75%2Fcl%2Fupdpgm.html),
[UPDSRVPGM](https://www.ibm.com/docs/en/i/7.4.0?topic=u-update-service-program)
and [ALWLIBUPD creation controls](https://www.ibm.com/docs/en/i/7.4.0?topic=concepts-create-program-create-service-program-commands).
[Data imports/exports](rpg-data-binding.md) participate in binding and retain
their typed metadata through relocation. Implicit system service providers, wider pointer contracts,
broader update/listing/ABI controls and independent native golden/scale acceptance
remain open. C09 remains partial.

Verification (2026-10-18): all 6347 solution tests pass with zero failures/skips
in 10m32s, including 50 new cases (46 policy/binding/security/publication/image
cases and four real cursor cases). All 4091 broad and 260 focused checks,
locked restore and a zero-warning/error Release build pass. Expanded terminal
acceptance covers named group changes, explicit relocation, runtime lookup,
actual-provider sharing, pinned connections and reclaim. Native/file cases cover
CCSIDs 37/1208; general display acceptance covers 24x80 and 27x132. Generated
inventory, local links and whitespace checks pass.
