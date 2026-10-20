# RPG WORKSTN file parameters

Native PR/PI interfaces accept LIKEFILE of an earlier global WORKSTN definition.
The parameter borrows the caller's display file. It shares the actual handle,
pending writes, subfile state and open status across local procedures and saved
RPG program calls.

```rpgle
**free
dcl-f MODEL workstn template extdesc('QGPL/SCREEN');
dcl-f COPY likefile(MODEL) extfile(*extdesc);
callp SHOW(COPY);
seton LR;

dcl-proc SHOW;
  dcl-pi *n;
    F likefile(MODEL);
  end-pi;
  dcl-ds BUFFER likerec(F.ENTRY:*all) inz;
  exfmt F.ENTRY BUFFER;
end-proc;
```

The example assumes SCREEN has an ENTRY format. Parameter records are qualified,
and data I/O requires matching [WORKSTN result buffers](rpg-workstation-records.md).
The parameter's parent must be an earlier file definition; another parameter
cannot be its parent. At call sites, actuals must be runtime files from the same
LIKEFILE family. Templates and unrelated files reject. WORKSTN blocking is known
without BLOCK; the existing DISK parent-blocking rules remain separate.

## Caller ownership

OPEN and CLOSE act on the actual file and use its owner's runtime settings.
A saved callee uses the caller's terminal connection and display session. A
callee's LR and return do not close a borrowed caller handle. Repeated calls
rebind retained callees to each new actual, and forwarding keeps the original
owner and lifetime checks.

The actual file's INDDS or shared indicators, SFILE RRN variables, INFDS and INFSR
remain active. These variables stay in the caller's scope. Subfile READC/CHAIN
and WRITE use the actual RRN binding; a parameter declares no replacement RRN.
Input stages buffer, RRN and indicator conversions before acknowledgement.
Named `%EOF`, `%FOUND`, `%OPEN` and `%STATUS` follow the actual file. WORKSTN
`%EQUAL` remains unsupported because WORKSTN has no SETLL operation.

E extenders and matching MONITOR handlers take precedence over INFSR. Unhandled
errors reach the actual owner's INFSR with caller feedback. RETURN and GOTO
can unwind to that owner's frame, including through saved RPG calls. Record
feedback retains the external format name when formal and actual qualifications
differ.

## Interfaces, storage and persistence

Only OPTIONS(*NOPASS) and OPTIONS(*OMIT) are accepted. Presence inquiries,
parameter positions and short-circuit guards retain their existing contracts.
File parameters expose no data layout, byte buffer or native memory; CONST,
VALUE, DIM and data-oriented parameter options reject. Borrowed cells and
semantic host references expire at call boundaries. Non-RPG targets reject file
arguments before execution, including external-process protocols 1 and 2.

LIKEREC supports parameter records in native buffers, exact interfaces and forward
return types. Related copies retain their nominal ancestry. At separate program
entry, the actual must have compatible usage, prefixes, renames, field layouts,
subfile/control formats, capacity and indicator metadata. Runtime targets and
associated caller variables retain the actual owner's bindings.

Local parameters may shadow global file names of either device. A local data
variable may shadow a global file parameter while file operands retain the
global binding. A parameter cannot shadow its own parent definition. LIKEREC uses the active
parameter qualifier, including in forward returns, and rejects records hidden
by a parameter of another device. Later local file definitions still shadow
imported parameters.

Main PI parameters use version-4 WORKSTN bindings. Restore verifies parameter
flags, families and every reconstructed binding; ordinary versions 1–3 remain
supported. Local parameter layouts derive from saved global descriptions.
Source and separate description files can be removed before calls against a
compatible actual file. The existing 16-visible-WORKSTN-file and bounded metadata
limits include parameters.

These rules follow IBM's [passing file parameters](https://www.ibm.com/docs/en/i/7.1.0?topic=parameters-passing-file)
and [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 497–498. [Procedure-local WORKSTN files](rpg-local-workstations.md)
add local display instances and cross-device name shadows. Zero-field extracts, multi-format/null layouts,
broader external/member/CCSID options, blocked I/O, ILE linking and independent
native golden/scale acceptance remain open. C09 stays partial; checklist 36
complete / 70 open.

Verification (2026-10-18): all 5438 solution tests pass with zero failures/skips
in 8m39s, including 39 new cases: 36 WORKSTN parameter/metadata/integration
cases, one DISK owner-return regression and two external-process cases. All
1642 broad and 219 final scoped checks, locked restore, a Release build with
zero warnings/errors, expanded RPGWFPRM terminal acceptance, generated inventory,
local links and whitespace checks pass. No performance target is claimed.

[Nested native structures](rpg-nested-structures.md) now support inline and
LIKEDS/LIKEREC children, shared storage views, forward types and nested record
buffers. See that contract for supported operations and bounds.
