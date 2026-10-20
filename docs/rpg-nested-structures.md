# Nested native data structures

Qualified native structures can contain inline DCL-DS definitions or LIKEDS and
LIKEREC subfields. Inline children are automatically qualified. Nested children
can be arrays, including arrays inside array structures. Their bytes belong to
the enclosing declaration; selecting a child or leaf creates a view of those
bytes and preserves overlapping references.

```rpgle
**free
dcl-ds FAMILY qualified inz;
  dcl-ds PEOPLE dim(2);
    NAME char(10);
    dcl-ds PETS dim(3);
      NAME char(10);
      AGE int(10);
    end-ds;
  end-ds;
end-ds;
eval FAMILY.PEOPLE(2).PETS(1).NAME='Rex';
return;
```

Free, column-limited free, and traditional fixed LIKEDS/LIKEREC subfields use
the same layouts. Traditional subfields leave type, position and length columns
blank for LIKEDS/LIKEREC. Inline nesting uses free declarations. Parents must be
qualified, names remain scoped to their parent, and named END-DS statements must
match. Nested STATIC, TEMPLATE, OCCURS and PSDS declarations reject; allocation
and lifetime belong to the enclosing structure. A top-level STATIC, TEMPLATE,
array or multiple-occurrence structure can contain ordinary nested children.

## Storage, initialization and operations

Nested arrays honor DIM, POS and OVERLAY. Overlays of array subfields inherit
the base dimension and stride, so CLEAR and individual writes preserve padding
and neighboring elements. Initialization reaches each child array element.
Ordinary INZ supplies default values recursively; INZ(*LIKEDS) retains explicit
parent defaults and uninitialized bytes. CLEAR resets the selected storage to
default values without restoring explicit INZ values.

Qualified references support assignment, whole-structure copies, CLEAR,
%SUBST, %SIZE, %ELEM, %SUBARR, keyed SORTA/%FIELDS and keyed lookup. Whole nested
structures retain their own width when their owner has multiple occurrences.
Reference parameters share storage with overlapping scalar aliases. CONST and
VALUE retain their existing protection/copy behavior. LIKEDS/LIKEREC parameters,
EXACT ancestry, detached returns and RTNPARM support nested layouts. Existing
contiguous-storage and argument-lifetime checks remain active.

Forward type lookup handles nested members, inline layouts, LIKE scalars,
LIKEDS templates, return metadata and layout inquiries without allocating
storage or running a procedure. Recursive type dependencies reject before
execution. Ordinary template use still exposes metadata without runtime bytes.

## Record buffers and persistence

Nested LIKEREC children work as DISK and WORKSTN result buffers, including selected
array elements. Record ancestry, extract modes, byte widths and writable input
checks remain active. DISK READ/CHAIN/UPDATE/WRITE and WORKSTN EXFMT preserve
sibling storage. %KDS accepts nested key structures and selected native array
elements; the structure index is captured once before reading key components.

Saved programs reconstruct nested layouts after source removal. Existing DISK
and WORKSTN binding versions remain unchanged because scoped file snapshots
retain their existing format. Protocol-2 external processes receive contiguous
nested buffers with unchanged binary layout and atomic parameter writeback.
Cases cover CCSIDs 37 and 1208.

The implementation supports up to 64 nesting levels and 1024 recursive subfield
definitions per structure. Existing dimension, per-declaration byte, module
storage, call-frame and metadata limits remain in force. Unsupported types and
keywords reject with original source locations.

The language rules follow IBM's
[ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 242–243, 446, 488 and 499–500. These rules cover qualification,
recursive initialization and nested LIKEREC use; the bounds above are emulator
limits. Multi-format/null external layouts, zero-field record extracts, broader
external/member/CCSID options, blocked I/O, ILE linking and independent native
system golden/scale acceptance remain open. C09 stays partial; checklist 36
complete / 70 open. No performance target is claimed.

Verification (2026-10-18): all 5540 solution tests pass with zero failures/skips
in 9m01s, including 54 new cases: 42 structure/metadata/ABI cases, eight
saved-record cases and four external-process cases. All 1957 broad and 311 final
focused checks, locked restore, a Release build with zero warnings/errors,
expanded RPGNESTED terminal acceptance, generated inventory, local links and
whitespace checks pass.

[Externally described structures](rpg-external-structures.md) add EXT/EXTNAME,
EXTFLD/PREFIX, external defaults, compatible file-variable sharing and checked
saved descriptions without opening runtime files.
