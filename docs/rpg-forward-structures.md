# Forward LIKEDS templates and interfaces

Native/free LIKEDS declarations, parameters and returns can use later named
native structure templates. Both traditional column origins support these forms.
Structure layouts and initialization remain independent of source declaration
order. C09 remains partial.

```rpgle
**free
dcl-ds COPY likeds(ROOT) inz(*likeds);
dcl-ds ROOT qualified inz;
  NUMBER packed(5:2) inz(12.34);
  TEXT char(4) inz('AB');
end-ds;
return;
```

COPY starts with NUMBER=12.34 and TEXT containing AB followed by two blanks.
Its bytes are independent of ROOT. Runtime changes to either structure do not
change the other's storage or declared initialization.

The contract follows IBM's [LIKEDS reference](https://www.ibm.com/docs/en/i/7.4.0?topic=keywords-likedsdata-structure-name)
and [INZ reference](https://www.ibm.com/docs/en/i/7.4.0?topic=keywords-inzinitial-value),
and the [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
pp. 495–496. Independent IBM-system acceptance remains open.

## Initialization and layout

Plain LIKEDS copies the layout without inheriting INZ. Argument-free INZ gives
the copy native defaults. INZ(*LIKEDS) inherits the template's declared subfield
initialization and its treatment of otherwise uninitialized storage. It does not
copy the template's current runtime values. CLEAR keeps its existing behavior of
applying type defaults, rather than restoring custom INZ values.

Metadata-only requests still avoid evaluating template initializers. When
INZ(*LIKEDS) needs a later template's initialization, the compiler resolves a
separate definition image with constants visible at that template's declaration.
No procedure is executed to obtain an initializer. Unsupported runtime-value
initializers still fail compilation. Template chains preserve inherited defaults,
including local constants that shadow globals.

Root DIM and OCCURS remain explicit on the copy. Subfield dimensions, formats,
positions, overlays, raw/text mode and unnamed filler storage are copied. A copy
is automatically qualified. PI structures can supply layouts without exposing
runtime argument values as initialization. [Data templates](rpg-templates.md) now suppress runtime storage. [Nested structures](rpg-nested-structures.md) add inline and referenced subfields.

## Interfaces, ancestry and feedback

PR/PI parameters and array/structure returns can use later templates, including
local return layouts and RTNPARM. Each declaration retains a stable identity
across metadata preview, initialization resolution and final binding. LIKEDS
chains share their structure ancestry; equal byte layouts from independent
roots remain unrelated for OPTIONS(*EXACT). Subfield identities also survive
copying. The ordinary CONST/VALUE, aliasing, prefix, optional/omitted and
variable-size rules remain in force.

Later local names shadow globals during template lookup. Global copied layouts
remain bound to their module's templates even when a procedure has a local
template with the same name. Feedback lengths propagate through forward chains
without depending on declaration order. Interfaces derived from those layouts
use the final supported INFDS extent. Plain interfaces still reject status and
indicator structures, and explicit undersized feedback definitions still reject.

## Errors, limits and evidence

Missing/non-structure templates, cycles, excessive active dependency depth and
incompatible interface modes fail compilation with source/include locations.
Existing type, declaration, array and 1 MiB/16 MiB storage bounds still apply.
Compiler callbacks detach on success and failure. Type and initializer caches
do not replace runtime buffers or retain borrowed argument cells.

Unit checks cover initialization modes, blank/default behavior, independent
storage, chains, local shadowing, exact related/unrelated roots, root/subfield
array attributes, fillers/overlays, PI templates, returns, fixed columns,
invalid templates and limits. Saved programs remove source and exercise retained
PI rebinding, real queue bytes, inherited return defaults, feedback extents and
cyclic includes at CCSID 37/1208. Process tests cover valid/malformed response
atomicity and rejection before launch. RPGFWDDS provides terminal compile/call
acceptance.

Wider external record structures, broader
forward value expressions, varying/graphic/UCS-2/pointer types, nondefault
date/time forms, foreign ABI and module/service-program binding remain open.
See [forward type references](rpg-forward-declarations.md),
[native structures](rpg-native-structures.md),
[aggregate parameters](rpg-aggregate-parameters.md) and
[return values](rpg-return-values.md).

Verification (2026-10-18): all 5071 solution tests pass with zero failures/skips,
including 45 new cases: 31 unit, 11 saved-program integration and three external
process cases. All 1265 final focused checks, locked restore, a Release build
with zero warnings/errors, RPGFWDDS terminal acceptance, generated inventory,
local links and whitespace checks pass. Full-suite wall time is 7m56s; no
performance target is claimed. RPGFWDDS runs at 24x80; general display acceptance
also covers 27x132.

An earlier run exposed a short-lived CPU fixture that could exit before live
process accounting sampled it. The fixture now waits for observed CPU/thread
data and releases/drains its child with bounded cancellation. Production
accounting behavior is unchanged; the final focused and full runs are clean.
C09 remains partial, with checklist 36 complete / 70 open.

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
