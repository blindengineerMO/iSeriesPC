# RPG procedure-local DISK files

Native subprocedures accept free DCL-F declarations and traditional F specs
for externally described DISK files. A local file belongs to its procedure;
other procedures and the main section cannot use its file or record names.
Local files can shadow global DISK files. Records with the same name in
different procedures retain separate descriptions and ancestry.

```rpgle
**free
dcl-s FIRST int(20);
dcl-s SECOND int(20);
eval FIRST=NEXTROW();
eval SECOND=NEXTROW();
seton LR;

dcl-proc NEXTROW;
  dcl-f ROWS static extdesc('QGPL/ORDERS') extfile(*extdesc);
  dcl-pi *n int(20) end-pi;
  dcl-ds BUFFER likerec(ORDERROW);
  read ROWS BUFFER;
  return BUFFER.ID;
end-proc;
```

The example assumes an ORDERS file with an ORDERROW format and ID field.
With STATIC, the two calls advance the same cursor. Removing STATIC gives
each invocation a fresh file and cursor. Local record I/O requires a checked
result data structure; local files never import scalar record fields. KEYED,
USAGE, RENAME, PREFIX, QUALIFIED, USROPN, TEMPLATE and LIKEFILE retain the
supported [file-definition rules](rpg-file-definitions.md).

## Lifetime and ownership

Automatic files open at each invocation unless USROPN is specified. They close
on normal or abnormal return, releasing their current-record locks and open
resources. Recursive invocations have independent automatic files.

STATIC files initialize and implicitly open once, unless USROPN is specified.
They retain open state, cursor, current record, locks and named-file feedback
across calls and recursion. Explicit CLOSE leaves the file closed on later
calls; it does not trigger another implicit open. STATIC is not inherited by
LIKEFILE copies and cannot be specified for a global or TEMPLATE file.

LR resets cycle storage and closes global files, while procedure STATIC files
and data remain in the activation. Reclaim, activation-group end, program
replacement, job end and failed/cancelled activation cleanup close the retained
files. A *NEW activation ends when its call finishes. The interpreter's
RetainFiles host option still controls whether a host retains an activation.

A local file can be passed through a compatible
[LIKEFILE parameter](rpg-file-parameters.md). The reference keeps its actual
procedure owner and lifetime checks; forwarding shares its cursor and locks.
Global file parameters hidden by a local file do not supply that local file's
handle. Calls to CL, APIs and external processes still reject file arguments.

## Feedback and error handling

INFDS must be local to the same procedure, and its STATIC setting must match
the file's. Native data structures now accept argument-free STATIC, including
arrays and traditional D-spec forms. Their storage initializes once per module
activation, is not inherited by LIKEDS copies, and uses the existing 16 MiB
module STATIC-storage bound. Automatic invocation storage keeps its separate
64 MiB / 128-frame bounds.

File feedback buffers and named `%EOF`, `%FOUND`, `%EQUAL`, `%OPEN` and `%STATUS`
follow the selected file instance. STATIC feedback survives later calls.
INFSR can name a routine in the same procedure; RETURN and GOTO return to that
procedure's frame, including errors through borrowed file parameters. Implicit
open failures run that file's handler before calculation statements. Automatic
files already opened during a failed initialization are still closed on exit.
Existing E extenders, MONITOR precedence, cancellation and source diagnostics
apply. Access to a global file with INFSR from a subprocedure remains rejected.

## Types and saved programs

LIKEREC uses local record descriptions, including qualified records and copies
of global or local templates. Forward LIKE of a procedure return resolves local
file metadata without opening a file or executing a procedure. Related global
LIKEFILE families retain EXACT ancestry through preview and final binding.
Default runtime names follow the local copy; explicit constant EXTFILE settings
inherit under the existing rules.

Version-6 DISK bindings save procedure names and STATIC flags. Global bindings
and every procedure's own file definitions are checked against the reconstructed
source. Same-named local files in different procedures have distinct keys;
family graphs resolve parents in the local scope before the global scope.
Versions 1–5 remain accepted, and programs without local file definitions keep
their previous version selection. Existing source hashes, duplicate-property,
layout, 1 MiB serialized-metadata and 16-visible-DISK-file checks remain active.
Source and a separate compile-time description file can be removed before a
saved program opens a compatible actual file.

The rules follow IBM's [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 200, 420–424 and 433. Independent native-system golden and scale
acceptance remains open. [Procedure-local WORKSTN files](rpg-local-workstations.md) also support local
display lifetimes and cross-device name shadows. Blocked I/O, broader external/member options, multi-format/null
layouts and ILE module/service-program linking remain open. C09 stays partial;
the checklist remains 36 complete / 70 open.

Verification (2026-10-18): all 5311 solution tests pass with zero failures/skips
in 8m27s, including 47 new cases: 44 local-file/metadata/integration cases and
three external-process cases. All 1409 broad and 208 final focused checks, locked
restore, a Release build with zero warnings/errors, expanded RPGLOCAL terminal
acceptance, generated inventory, local links and whitespace checks pass.
No performance target is claimed.

[Nested native structures](rpg-nested-structures.md) now support inline and
LIKEDS/LIKEREC children, shared storage views, forward types and nested record
buffers. See that contract for supported operations and bounds.
