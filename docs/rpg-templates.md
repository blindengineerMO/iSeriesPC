# RPG data templates

Standalone fields and native data structures accept argument-free `TEMPLATE`
in free declarations and both supported traditional D-spec column origins.
Templates retain type, layout, initialization and declaration identity metadata
without allocating runtime scalar, array, structure or occurrence storage.

```rpgle
**free
dcl-ds COPY likeds(ROOT) inz(*likeds);
dcl-ds ROOT qualified template inz;
  N packed(5:2) inz(12.34);
  TEXT char(4) inz('AB');
end-ds;
```

The copy receives ordinary runtime storage and inherited defaults. `LIKE`
inherits a standalone template's supported type, without inheriting `TEMPLATE`, DIM, order
or initialization. `LIKEDS` preserves member layouts and exact-type ancestry;
root DIM/OCCURS remain explicit on the copy. Templates can themselves derive
from other declarations. Forward lookup, local shadowing, initializer scope,
PR/PI interfaces and returns follow the existing
[forward structure rules](rpg-forward-structures.md).

## Permitted uses

A template or its subfields can supply `LIKE`, `LIKEDS`, `%SIZE` and `%ELEM`.
Declaration-only `%LEN` and `%DECPOS` also accept template layouts. `%DECPOS`
requires non-floating numeric data. Runtime `%SIZE`/`%ELEM` inquiries use layout
metadata, including `*ALL`, qualified members and constant array subscripts;
they do not read template buffers. Inquiry results can participate in ordinary
expressions such as `%char(%size(ROOT))`.

Data reads, assignments, CLEAR, calls, key fields, parameter lists and
storage-dependent INFDS/INDDS/SFILE bindings reject templates. The compiler
rejects template data operands even in unreachable calculations, before saved
object creation or external process launch. Runtime reference and built-in
guards also reject direct attempts to access template storage with status 105.
Literals, labels and another structure's member names are separate namespaces.

The supported inquiry subset requires a declared layout with constant
subscripts. Runtime indices and expressions that hide runtime `%LEN`/`%DECPOS`
inside a layout inquiry reject during compilation. TEMPLATE on subfields or
parameters rejects; it applies to standalone fields and structures. Explicit
CTDATA/PERRCD/EXTFMT/ALT combinations remain unsupported. File
`TEMPLATE`/`LIKEFILE` is separate work.

## Limits and evidence

Existing component bounds still apply: character fields through 32767 bytes,
DIM through 32767, native structure records through 1 MiB, and the supported
numeric formats. At most 256 visible data templates are supported, together
with existing declaration/member/dependency limits. Templates do not consume
runtime 1 MiB per-declaration, 16 MiB program storage or procedure invocation
budgets. Ordinary copies and formal parameters retain those storage limits.
For example, a 32767 by 32767 character array template describes 1,073,676,289
bytes, and a 1 MiB structure template with DIM(32767) describes 34,358,689,792
bytes, without allocating those buffers.

Unit tests cover layout/default inheritance, exact ancestry, local shadowing,
fixed source, metadata inquiries, count limits, storage suppression and denied
runtime access. Saved programs remove source and exercise real queue buffers,
local returns, large metadata and include diagnostics at CCSIDs 37/1208.
Process tests check atomic valid/malformed writeback and rejection before
launch. RPGTMPL adds real terminal compile/call acceptance.

The implementation follows IBM's
[TEMPLATE definition rules](https://www.ibm.com/docs/en/i/7.4.0?topic=template-rules-keyword-definition-specifications)
and the [IBM i 7.6 RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
TEMPLATE on printed page 536 and `%DECPOS` on page 690. Independent native-system
golden acceptance remains open. Wider external record layouts, complete compile-time-data template behavior, broader types and ILE linking
remain open. C09 remains partial; checklist 36 complete / 70 open.

Verification (2026-10-18): all 5122 solution tests pass with zero
failures/skips, including 51 new cases: 39 unit, nine saved-program integration
and three external-process cases. All 779 final focused checks, locked restore,
a Release build with zero warnings/errors, final RPGTMPL terminal acceptance,
generated inventory, local links and whitespace checks pass. Full-suite wall
time is 7m39s; no performance target is claimed.

[DISK LIKEREC record structures](rpg-record-structures.md) now supply native
layouts, result operands and parameter/return types.

[DISK file templates and LIKEFILE definitions](rpg-file-definitions.md) now
provide qualified module-level file copies without opening their templates.
File parameters and WORKSTN variants remain open.

[WORKSTN record buffers](rpg-workstation-records.md) and
[WORKSTN file definitions](rpg-workstation-definitions.md) now provide display
LIKEREC layouts, templates, qualified formats and LIKEFILE copies.

[WORKSTN file parameters](rpg-workstation-parameters.md) extend borrowed file
interfaces to caller-owned display state and feedback.

[Nested native structures](rpg-nested-structures.md) now support inline and
LIKEDS/LIKEREC children, shared storage views, forward types and nested record
buffers. See that contract for supported operations and bounds.

[Externally described structures](rpg-external-structures.md) add EXT/EXTNAME,
EXTFLD/PREFIX, external defaults, compatible file-variable sharing and checked
saved descriptions without opening runtime files.
