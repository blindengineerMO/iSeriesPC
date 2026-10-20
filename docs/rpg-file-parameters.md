# RPG DISK file parameters

Native PR/PI parameters accept `LIKEFILE(parent)` for a previously declared
DISK file. Local procedures and separately saved RPG programs borrow the
caller's file directly. The parameter has no scalar, array or byte-buffer
storage.

```rpgle
**free
dcl-f MODEL qualified template block(*no) extdesc('QGPL/ORDERS');
dcl-f INPUT likefile(MODEL) extfile('QGPL/ORDERS') usropn;
dcl-s ID int(20);
eval ID=NEXTROW(INPUT);
seton LR;

dcl-proc NEXTROW;
  dcl-pi *n int(20);
    F likefile(MODEL);
  end-pi;
  dcl-ds BUFFER likerec(F.ORDERROW);
  if not %open(F);
    open F;
  endif;
  read F BUFFER;
  return BUFFER.ID;
end-proc;
```

This example assumes an ORDERS file with an ORDERROW format and ID field.
The file parameter's record name is qualified by its own name. LIKEREC can
also use that name in a return declaration before the parameter declaration,
including RTNPARM returns. Supported traditional D-spec PR/PI forms use blank
parameter type/length columns and LIKEFILE in the keyword area; both supported
column origins work.

## Declaration and call rules

The parent must appear earlier as a file declaration and have a known blocking
property. Explicit `BLOCK(*NO)` handles an input parent; supported input-add,
update and output declarations already determine unblocked operation. A file
parameter cannot itself supply the parent definition. A parameter also cannot
shadow its own parent name; retain a distinct parent definition.

Only `OPTIONS(*NOPASS)` and `OPTIONS(*OMIT)` can accompany LIKEFILE. Existing
trailing optional-parameter rules, `%PARMS`, `%PARMNUM`, `%OMITTED`, short-circuit
guards and RTNPARM position adjustments apply. Accessing a missing or omitted
file fails. CONST, VALUE, DIM, CCSID and other data-parameter options reject.
Files cannot be returned or used as data, addresses or layout inquiries.

At a prototyped call site, the actual must be a runtime file from the same
parent family. Templates, literals and unrelated file definitions reject.
PR/PI declarations must agree on family and parameter modes. At a separate
saved program's entry, the runtime checks usage, keyed access, prefix/rename
settings, external record identity, fields and physical layout before its body
runs. External filenames belong to the actual file and may differ from the
parent's defaults.

File arguments are supported only between RPG programs and procedures.
Calls to CL, built-in APIs or external processes reject before the target
executes. File references expose neither native argument memory nor a buffer;
borrowed cells expire when the call ends. Semantic host arguments carry the same
expiration guard. Retained programs rebind their entry
file parameters on every invocation. Local data shadows preserve global file
parameter bindings for file operands. Forwarding preserves the original owner
and call-lifetime checks.

## Shared file behavior

OPEN and CLOSE through a parameter affect the actual caller file. Reads and
positioning use its cursor; updates, deletes and UNLOCK use its current record
and locks. An update through the parameter consumes the caller's update right.
`%EOF(F)`, `%EQUAL(F)`, `%FOUND(F)`, `%OPEN(F)` and `%STATUS(F)` report that same
file state, including changes made by another alias.

The caller's actual open settings, authority/override checks and INFDS remain
in effect. Unhandled file errors invoke the actual owner's INFSR with its
storage and execution context. RETURN or GOTO from that handler reaches the
owner's frame, including across saved RPG calls. An E extender or matching
MONITOR in the called procedure handles the error before INFSR dispatch.
Returning from a callee or setting its LR closes its own files; borrowed caller
handles remain owned by the caller.

## Saved bindings and evidence

Programs with main PI file parameters save version-5 DISK bindings. Other
programs retain the existing version selection, with versions 1–4 accepted.
Restore validates parameter flags, family graphs and layouts; recompilation
checks every saved binding. Local procedure interfaces are reconstructed from
the saved source snapshot. Original source and a separate compile-time
file description can be removed before execution against a compatible actual.
The existing 16-visible-DISK-file bound includes allocated file parameters.

Tests cover cursor/open-state sharing, locks and feedback, optional arguments,
forwarding, local shadows, native fixed forms, forward return metadata,
retained entry rebinding, saved-binding corruption, source/description removal,
call lifetimes, incompatible entry layouts, isolated metadata caches for equal
layouts from unrelated families, error-handler control transfers and
rejection before CL/API/process execution. Saved cases cover CCSIDs 37 and
1208. The RPGFPRM terminal fixture compiles and calls a procedure that opens a
borrowed file and advances its cursor across two calls.

These rules follow the [IBM i 7.6 ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 497–498. The supported file-definition subset is described in
[DISK file definitions](rpg-file-definitions.md). [Procedure-local DISK files](rpg-local-files.md) now support automatic and STATIC
instances. [WORKSTN templates/copies](rpg-workstation-definitions.md) and
[result structures](rpg-workstation-records.md) add display buffers. Blocked I/O, broader external/member options and cross-unit ILE linking remain open.
Independent native-system golden and scale acceptance remains open; C09 stays
partial and the checklist remains 36 complete / 70 open.

Verification (2026-10-18): all 5264 solution tests pass with zero failures/skips
in 8m21s, including 56 new cases: 54 parameter/metadata/integration cases and
two external-process cases. All 1355 broad and 158 final focused checks, locked
restore, a Release build with zero warnings/errors, expanded RPGFPRM terminal
acceptance, generated inventory, local links and whitespace checks pass.
No performance target is claimed.

[WORKSTN file parameters](rpg-workstation-parameters.md) now borrow caller-owned
display handles, subfile state, RRN/indicator bindings and error handling.
