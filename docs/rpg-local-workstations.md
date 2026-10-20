# Procedure-local WORKSTN files

Native procedures can declare externally described WORKSTN files using free
DCL-F or traditional F specs. Each procedure owns its file and format names;
other procedures and the main body cannot use those private names. Local files
and formats can shadow global DISK or WORKSTN definitions. Data names remain a
separate namespace.

```rpgle
**free
callp SHOW();
seton LR;
dcl-proc SHOW;
dcl-f FORM workstn extdesc('QGPL/SCREEN') extfile(*extdesc);
dcl-pi *n end-pi;
dcl-ds BUFFER likerec(ENTRY:*all) inz;
eval BUFFER.NAME='LOCAL';
exfmt ENTRY BUFFER;
end-proc;
```

Local files do not import individual external fields into procedure storage.
Data-bearing I/O requires a compatible result data structure. Existing LIKEREC
extract modes, checked zoned display numerics, exact parameter ancestry and
atomic receiver preparation apply. Qualified formats, local/global templates,
LIKEFILE copies and forward procedure-return metadata use the local description
without opening a display. A copy may use an earlier global parent before a
later local declaration shadows that parent name.

## Lifetime and associated variables

An automatic file opens on each procedure entry unless USROPN is specified,
and closes on return or error. Recursive calls have independent handles and
pending writes. STATIC files share one handle across calls and recursion for
the activation. Explicit CLOSE leaves a STATIC file closed on subsequent calls;
OPEN can reopen it. STATIC and USROPN do not inherit through LIKEFILE.

Procedure STATIC handles survive main-cycle LR and are rebound when retained
programs run again. Activation reclamation, *NEW activation completion and job
end release them, including their object allocations. Failed initialization
closes already opened automatic files; retained STATIC files follow activation
cleanup. Templates and FILE parameters do not open independent handles.

Local INFDS must have the file's scope and automatic/STATIC duration. Named
status, feedback, INDDS, INFSR and subfile RRN bindings use the actual owner's
variables. Local handles can be borrowed through native LIKEFILE parameters;
OPEN/CLOSE, pending writes, subfile updates and indicators remain shared with the
owner. Error dispatch retains the existing E/MONITOR precedence and can return
from the owning procedure across a borrowed call. Associated INDDS and RRN
variables retain their declared storage duration.

## Saved programs and external calls

Version-5 WORKSTN bindings save procedure names and STATIC flags. Restore checks
source hashes, each scoped definition, family ancestry, inherited layouts and
runtime targets. Same-named files in different procedures have distinct keys.
Ordinary binding versions 1–4 remain supported, and programs without local file
definitions retain their existing version selection. The 16-file visible-scope
limit and 1 MiB serialized-metadata bound remain active.

Saved programs can run after source and separate compile-time descriptions are
removed, against a compatible actual display. Local result buffers retain
protocol-2 process writeback atomicity at CCSIDs 37 and 1208. File arguments are
RPG-only and reject external processes before side effects in protocols 1 and 2.

The scope and duration rules follow IBM's
[ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 174, 200, 209–210 and 433. See also
[WORKSTN file definitions](rpg-workstation-definitions.md) and
[WORKSTN file parameters](rpg-workstation-parameters.md).

Zero-field extracts, multi-format/null layouts, blocked I/O, broader
external/member/CCSID options, ILE module/service-program linking and independent
native-system golden/scale acceptance remain open. C09 remains partial; the
checklist remains 36 complete / 70 open. No performance target is claimed.

Verification (2026-10-18): all 5486 solution tests pass with zero failures/skips
in 8m46s, including 48 new cases: 42 local WORKSTN/metadata/integration
cases and six external-process cases. All 48 final focused checks and 1686 other
broad regression cases pass. Locked restore, a Release build with zero warnings/
errors, expanded RPGWLOCAL terminal acceptance, generated inventory, local links
and whitespace checks pass.

[Nested native structures](rpg-nested-structures.md) now support inline and
LIKEDS/LIKEREC children, shared storage views, forward types and nested record
buffers. See that contract for supported operations and bounds.
