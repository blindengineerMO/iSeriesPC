# WORKSTN templates, copies and qualified records

Global externally described WORKSTN files support TEMPLATE, QUALIFIED and
LIKEFILE in free declarations and both supported traditional F-spec origins.
Templates supply compile-time layouts for LIKEREC and LIKEFILE without importing
scalar fields, allocating a runtime file handle or requiring a terminal.

```rpgle
**free
dcl-f MODEL workstn template extdesc('QGPL/SCREEN');
dcl-f COPY likefile(MODEL) extfile(*extdesc);
dcl-ds BUFFER likerec(ENTRY:*all) inz;
eval BUFFER.NAME='START';
exfmt COPY.ENTRY BUFFER;
sndpgmmsg BUFFER.NAME;
seton LR;
```

The example assumes SCREEN contains an ENTRY format with a NAME field. COPY
inherits the description, usage, prefixes and renames, and automatically qualifies
its formats. Explicit QUALIFIED also qualifies ordinary WORKSTN record names.
Qualified files generate no scalar fields, so data I/O requires a matching
[result structure](rpg-workstation-records.md). File-name READ with a result
structure retains its single-format restriction. OPEN and CLOSE use the file
name. Templates reject runtime I/O, status inquiries and argument uses.

## Inheritance and subfiles

The parent must precede the copy. Copies can be parents or templates themselves.
Inherited device, usage, EXTDESC, PREFIX and RENAME settings cannot be overridden,
and LIKEFILE cannot also specify QUALIFIED. Copies may supply EXTFILE, USROPN,
INDDS, INFDS and INFSR. These runtime settings, and TEMPLATE itself, are not
inherited, except that an explicitly constant EXTFILE is inherited. Without an
explicit EXTFILE, each copy defaults to its own runtime file name. The supported
EXTFILE(*EXTDESC) form can be inherited or specified by the copy.

Each runtime copy has an independent display session, pending writes, subfile
positions, RRN binding, open state and file status. A parent's USROPN does not
prevent a copy from opening implicitly. INDDS on a parent does not redirect a
copy's response indicators. Templates reject USROPN, INDDS, INFDS and INFSR.

A subfile template specifies `SFILE(ROWS)` without an RRN variable. A runtime
copy must specify `SFILE(ROWS:RRN)` for every inherited subfile; the RRN is never
inherited. SFILE and RENAME use unqualified format names even for qualified files.
I/O uses qualified names such as COPY.ROWS and COPY.CTL. The existing result
buffer extract, conversion, hidden-field and indicator rules remain active.
Zero-field control-format buffers remain unsupported.

## Saved types and verification

Related copies share nominal record ancestry for LIKEREC, LIKEDS, native EXACT
parameters, returns and forward return types. Unrelated templates with equal
field layouts retain distinct identities. Copies derive their descriptions from
the parent's cached snapshot rather than resolving live metadata again.

Version-3 WORKSTN bindings preserve template, qualification, parent and family
metadata. Restore validates inherited shapes and parent order, rejects cycles
and downgrades, and verifies every reconstructed binding, including copies that
do not call the resolver. Ordinary definitions keep versions 1/2. Saved programs
can run after source and separate description-file removal against a compatible
actual file. Template-only programs can run without a display connection.
Metadata serialization enforces its 1 MiB bound while writing. The 16-visible-file
limit applies independently to each device after LIKEFILE inheritance, including
templates. Existing runtime authority, replacement and format checks remain.

The behavior follows IBM's [LIKEFILE rules](https://www.ibm.com/docs/en/i/7.6.0?topic=filename-rules-likefile-keyword)
and [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 421–426, 430 and 432–434. [Procedure-local WORKSTN files](rpg-local-workstations.md) add local display
lifetimes. Zero-field extracts, multi-format/null layouts, broader
external/member/CCSID options, blocked I/O, ILE linking and independent native
golden/scale acceptance remain open. C09 stays partial; checklist 36 complete /
70 open.

Verification (2026-10-18): all 5399 solution tests pass with zero failures/skips
in 8m37s, including 52 new cases: 48 definition/metadata/integration cases
and four external-process cases. All 1534 broad and 248 final focused checks,
locked restore, a Release build with zero warnings/errors, expanded RPGWCOPY
terminal acceptance, generated inventory, local links and whitespace checks
pass. No performance target is claimed.

[WORKSTN file parameters](rpg-workstation-parameters.md) now borrow caller-owned
display handles, subfile state, RRN/indicator bindings and error handling.

[Nested native structures](rpg-nested-structures.md) now support inline and
LIKEDS/LIKEREC children, shared storage views, forward types and nested record
buffers. See that contract for supported operations and bounds.
