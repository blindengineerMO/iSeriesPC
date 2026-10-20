# Deferred RPG service activation

CRTPGM, CRTBNDRPG and CRTSRVPGM accept BNDSRVPGM entries with *IMMED (the default)
or *DEFER activation. Binding-directory service entries preserve the same modes.
The service must exist, be authorized and provide a compatible native interface
at binding time in either mode.

```cl
CRTPGM PGM(QGPL/APP) MODULE(QGPL/MAIN) BNDSRVPGM((QGPL/SERVICE *DEFER))
ADDBNDDIRE BNDDIR(QGPL/PROVIDERS) OBJ((QGPL/SERVICE *SRVPGM *DEFER))
UPDPGM PGM(QGPL/APP) MODULE(*NONE) BNDSRVPGM((QGPL/SERVICE *SAME))
```

The flat BNDSRVPGM(SERVICE *DEFER) form also accepts one tuple. Multiple entries
use parentheses around each tuple; explicit resolved duplicates reject.

The caller preflights its immediate dependency closure before running main.
Deferred edges do not locate their providers or check their authority, signing,
signatures or native interfaces until a bound procedure is called. Therefore,
an unavailable deferred provider does not prevent a caller from executing a path
that does not use it. Earlier caller effects survive a later activation failure.
At first use, the provider and its immediate dependency closure must pass all
checks before its procedure body runs. Further deferred edges wait for their own
calls. Every immediate edge validates even when it reaches an already visited
activation. Cycles terminate through activation identity; the existing 64-level,
256-activation preflight and native invocation budgets still apply.

Dependencies activate within their parent's activation group. A *CALLER child of
a named parent owns storage in that parent's group. Deferred and immediate paths
share the same activation when they reach the same service in the same group.
Module globals initialize on first procedure use; successful service calls retain
globals and file cursors until their owning group is reclaimed. Caller reclaim
does not discard a service in another group. Calls recheck live authority,
signing and retained-snapshot trust under the caller's effective identity.

The version available when a deferred service is first called supplies its
activation. Later updates preserve an existing activation until reclaim. Program
updates also preserve a caller's prior binding policy while it stays activated.
UPDPGM and UPDSRVPGM retain existing modes by default and accept *SAME, *IMMED or
*DEFER per provider. A newly selected provider with *SAME uses *IMMED. Rebinding
still requires current service objects and validates their interfaces.

RPGBOUND/RPGSERVICE version 3 records explicit deferred references; version 4 adds
[runtime *LIBL lookup](rpg-update-policies.md). Explicit immediate-only images
retain version 1/2 and omit the activation property; older images restore with
*IMMED defaults. Strict validation rejects unknown/null activation modes,
case variants, inconsistent versions and malformed references. Directory payloads
retain version 1. Object schema 31 and record metadata formats are unchanged.

This procedure-import contract follows IBM's [service activation rules](https://www.ibm.com/docs/ssw_ibm_i_74/ilec/ilesrvh.htm),
[creation controls](https://www.ibm.com/docs/en/i/7.6.0?topic=c-create-program)
and [update controls](https://www.ibm.com/docs/en/i/7.5.0?topic=u-update-program).
[Data imports](rpg-data-binding.md) require service storage and force activation
checks before main even with *DEFER. Procedure-pointer activation remains open. [Activation/library update policies](rpg-update-policies.md)
add named group changes and runtime library-list resolution. Broader ABI/listing
options and independent native golden/scale acceptance remain open; C09 stays partial.

Verification (2026-10-18): all 6297 solution tests pass with zero failures/skips
in 10m21s, including 44 new cases (40 activation/binding/update/security/version cases
and four real nullable-record/cursor cases). All 4041 broad and 210 focused
checks, locked restore and a zero-warning/error Release build pass. Expanded
terminal acceptance covers unused missing deferred providers, first-call failure,
provider recovery and update *SAME policy preservation. Native/file cases
cover CCSIDs 37/1208; general display acceptance covers 24x80 and 27x132. Generated
inventory, local links and whitespace checks pass.
