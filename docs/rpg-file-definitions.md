# DISK file templates, copies and qualified records

Module-level DISK declarations accept `TEMPLATE`, `QUALIFIED`, `LIKEFILE(parent)`
and `BLOCK(*NO)` in free and supported native fixed F specifications. A template
retains its compile-time description without importing scalar fields, allocating
a runtime file slot, or opening a file. A template's file name is usable for
LIKEFILE; its record names supply LIKEREC layouts.

```rpgle
**free
dcl-f MODEL template keyed block(*no) extdesc('QGPL/DATA');
dcl-f COPY likefile(MODEL) extfile('QGPL/DATA');
dcl-ds BUFFER likerec(ROW);
chain 10 COPY.ROW BUFFER;
if %found(COPY);
  sndpgmmsg %char(BUFFER.ID);
endif;
seton LR;
```

## Inheritance and names

LIKEFILE requires an earlier DISK definition. Copies inherit usage, addition and
keyed access, description, field prefixes and record renames. Their records are
qualified automatically: `COPY.ROW`, or `COPY.ITEM` if ROW was renamed to ITEM.
Explicit QUALIFIED and overrides of inherited properties reject. Templates and
USROPN are not inherited. Copies may themselves be templates or parents of
further copies; cycles and forward/missing parents reject.

Only explicitly specified constant EXTFILE settings are inherited. Without one,
a copy's runtime file defaults to its own name. A copy can override EXTFILE,
including the supported `*EXTDESC` form. Its compile-time description still
comes from the parent snapshot. USROPN, INFDS and INFSR may be specified on a
runtime copy; templates reject those non-inherited runtime settings.

For input-only parents, BLOCK(*NO) makes the blocking property explicit before
LIKEFILE is used. Input-add, update and output definitions already determine
that they are unblocked. BLOCK(*YES), runtime-variable external file names,
member overrides and other unsupported file options reject explicitly.

QUALIFIED also applies to ordinary DISK files. Qualified files and LIKEFILE
copies generate no scalar fields or implicit input/output specifications. Data
operations require result structures; positioning, locking, open/close and
keyed deletion retain their usual operand forms. Each runtime copy has its own
cursor and open state. Automatic opens preserve reverse declaration order and
normal cleanup behavior.

Native fixed LIKEFILE definitions use blank file attributes and a LIKEFILE
keyword, including continuation lines. Standard native C-spec result fields
supply record buffers. Both supported native F/D column origins are covered.

Simple file prefixes accept an RPG name (`PREFIX(P_)`) or an uppercase character
literal (`PREFIX('P_')`) for DISK and WORKSTN declarations. Names normalize to
uppercase; lowercase or invalid characters inside a literal report the file
declaration location. Saved programs retain the imported field names after source
removal. IBM documents both forms in its
[PREFIX reference](https://www.ibm.com/docs/en/i/7.4.0?topic=keywords-prefixprefixnbr-char-replaced).
Qualified prefixes containing a period and replacement counts remain open.

## Record buffers and persistence

LIKEREC buffers derived from any member of a file family can serve related
copies, subject to existing extract, size, readonly and index checks. Prefixes
and member layouts retain their identities for OPTIONS(*EXACT). Result-buffer
input preserves imported scalar fields from other declarations; template and
qualified files add no scalar slots. See
[record structures](rpg-record-structures.md) for transfers, atomicity and limits.

Compiled bindings use version 4 when template, qualified or LIKEFILE attributes
are present. Existing ordinary file bindings keep version 3, and versions 1–3
remain accepted under their existing restrictions. Restore validates family
ancestry, inherited shapes, qualified names, hashes, counts and duplicate JSON
properties. Recompilation verifies every saved binding, including derived
bindings that reuse a parent descriptor. Changed targets or family links reject
before execution.

Descriptions are cached once per root request; copies derive their layouts
without re-reading a live description. Source removal and deletion of a
separate compile-time description file do not prevent execution against a
compatible runtime file. Template-only programs also run without opening their
deleted descriptions. Runtime opens retain authority, format and override
checks. Existing 16-DISK-file and serialized-metadata limits include templates.

## Evidence and remaining work

Checks cover independent cursors, inheritance and overrides, default and
constant runtime targets, source/description removal, qualified fields,
parent ordering and blocking rules, template use restrictions, fixed origins,
related exact types, copied input/update/output, saved-binding corruption and
process writeback atomicity. Saved cases run at CCSIDs 37/1208. RPGFILES adds
real terminal compile/call acceptance.

The behavior follows IBM's
[LIKEFILE rules](https://www.ibm.com/docs/en/i/7.6.0?topic=filename-rules-likefile-keyword)
and [IBM i 7.6 RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 421–426 and 434. [LIKEFILE file parameters](rpg-file-parameters.md)
now share caller state across RPG calls. [Procedure-local DISK files](rpg-local-files.md)
add automatic and STATIC instances. [WORKSTN result structures](rpg-workstation-records.md) support global display buffers.
[WORKSTN file definitions](rpg-workstation-definitions.md) add templates/copies
and qualified display records. Local WORKSTN files, blocked I/O, broader
external/member options and ILE binding remain open. Independent native-system
golden acceptance remains open. C09 remains partial; checklist 36 complete /
70 open.

Verification (2026-10-18): all 5208 solution tests pass with zero
failures/skips, including 41 new cases: 38 file-definition/metadata/integration
cases and three external-process cases. All 1031 final focused checks, locked
restore, a Release build with zero warnings/errors, final RPGFILES terminal
acceptance, generated inventory, local links and whitespace checks pass.
Full-suite wall time is 8m24s; no performance target is claimed.

[WORKSTN file parameters](rpg-workstation-parameters.md) share caller-owned
display state across local procedures and saved RPG programs.

[Nested native structures](rpg-nested-structures.md) now support inline and
LIKEDS/LIKEREC children, shared storage views, forward types and nested record
buffers. See that contract for supported operations and bounds.

Nullable DISK fields, native null indicators and nullable key searches now use
[ALWNULL modes](rpg-null-values.md). Formal/aggregate null maps remain open.

Named variables and external indicator structures now use
[named null maps](rpg-null-maps.md). Parameter maps remain open.
