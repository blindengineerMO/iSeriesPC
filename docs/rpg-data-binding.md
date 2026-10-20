# RPG module and service data binding

Global standalone fields and named native data structures can use IMPORT and
EXPORT. Free declarations and the supported traditional D-specification origins
share the same binding model. For example:

```rpgle
// Exporting module
ctl-opt nomain;
dcl-s TOTAL packed(9:2) inz(10.25) export('SharedTotal');

// Importing module
dcl-s LOCAL packed(9:2) import('SharedTotal');
eval LOCAL=LOCAL+1;
```

An omitted external name uses the uppercase declaration name. Character literals
and constants preserve case; free definitions also support *DCLCASE. External
symbols use the existing bounded ASCII identifier grammar. Duplicate external
data names and data/procedure symbol collisions reject. Definitions must be
global, named storage; procedure locals, parameters, templates and STATIC local
definitions cannot declare data bindings. An imported item cannot have INZ or
compile-time data, including initialization on nested members. Imported/exported
items cannot be entry parameters. Ordinary compilation rejects imports that have
no module binding phase. Module compilation can retain unresolved imports until
CRTPGM, CRTBNDRPG or CRTSRVPGM binds them.

Binding resolves static module data exports first, followed by typed service
exports in provider order. Binding directories discover both kinds. Missing
symbols, duplicate providers and incompatible storage layouts reject before
publication. Compatibility checks cover scalar representation, byte widths,
scales, dimensions, opaque bytes, structure offsets, nested layouts, occurrence
counts and null capability. Local names and structure qualifications can differ.
Captured source and descriptions reconstruct the links without the original
module or directory objects.

Imported fields read and write the exporting module's live bytes. Fixed arrays,
nested structures and structure arrays keep their normal native views and
argument aliases. Multiple-occurrence structures share all occurrence bytes while
each module retains its own occurrence selection. Automatic and named null maps
follow exporter storage and remain live through record I/O and procedure calls.
These aliases can also be borrowed by ordinary CL targets through the existing
typed call contract. Storage retains its original CCSID; mismatches reject.

Exported module storage initializes when its activation starts, before main
effects. Exported entry-module values and null indicators survive LR and abnormal
returns, while ordinary cycle globals restart. Service data belongs to the
service's activation group and shares storage with its procedures. Caller LR,
failure or reclaim does not discard data in another group. Reclaiming the owning
service group creates fresh storage on the next connection. Borrowed memory and
null maps reject access after their service activation is disposed.

A data import activates and initializes the required service storage before
caller main effects, including when BNDSRVPGM requested *DEFER. Authority, signing,
signature, export kind and layout checks remain mandatory. Procedure-only deferred
imports still wait for a procedure call, even if their provider also exports data.
Dependency traversal preserves parent-group ownership and graph budgets. Native
file opening and procedure execution keep their existing timing.

CRTSRVPGM EXPORT(*ALL) includes public procedure and data symbols. Binder source
can select or hide either kind; dependency exports are not automatically
republished. CURRENT/PRV levels preserve ordered positions. UPDSRVPGM
EXPORT(*CURRENT) preserves the data/procedure kind of each public symbol; ordinary
module replacement and library relocation retain existing activations until
reclaim. An older caller can use a compatible PRV data interface. DSPSRVPGM lists
public data exports separately.

RPGBOUND/RPGSERVICE version 5 records ordered data-export subsets alongside the
existing symbol tables. Validation rejects missing, forged, duplicate, oversized
or inconsistent kind metadata. Interfaces without data retain earlier image
formats and omit the data-export property. Static module data links reconstruct
from captured source, retaining module version 1. Database schema 31, DISK 7,
external description 2, WORKSTN 5 and catalog count 315 are unchanged. Existing
module, source, storage and activation budgets apply.

The contract follows IBM's [RPG reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf)
(EXPORT, printed page 475; IMPORT, page 487) and [service activation rules](https://www.ibm.com/docs/ssw_ibm_i_74/ilec/ilesrvh.htm).
The emulator checks typed layouts rather than permitting unchecked memory
reinterpretation. Procedure/data pointers, weak and activation-group exports,
broader binder/language/ABI controls and independent native golden/scale
acceptance remain open. C09 remains partial.

Verification (2026-10-19): all 6422 solution tests pass with zero failures/skips
in 10m57s, including 75 new cases (36 compiler/static/lifetime cases, 37 service binding/
interface/security/lifetime cases and two real nullable-record round trips). All 4166 broad and 502 focused checks,
locked restore and a zero-warning/error Release build pass. Expanded terminal
acceptance covers shared data/procedure globals, retained values through LR,
caller reclaim and service reclaim. Native/file cases cover
CCSIDs 37/1208; general display acceptance covers 24x80 and 27x132. Generated
inventory, local links and whitespace checks pass.
