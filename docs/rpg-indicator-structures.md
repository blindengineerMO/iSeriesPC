# RPG indicator data structures

WORKSTN files support `INDDS(name)` with a named native data structure, optionally
declared with OCCURS.
The structure contains 99 bytes corresponding to DDS conditioning and response
indicators 01–99. It is independent of the program's global `*IN` array and LR.
Several files may explicitly share one structure; otherwise their areas remain
independent. These rules follow IBM's
[INDDS contract](https://www.ibm.com/docs/en/i/7.5.0?topic=keywords-inddsdata-structure-name).

```rpgle
**free
dcl-f SCREEN workstn indds(KEYS);
dcl-ds KEYS qualified;
  EXIT ind pos(3);
  ACCEPT ind pos(6);
  SHOW ind pos(21);
end-ds;
eval KEYS.SHOW=*on;
exfmt ENTRY;
if KEYS.EXIT;
  seton LR;
endif;
```

The referenced display DDS must declare unconditional file-level INDARA:

```dds
     A                                      INDARA CA03(03) CF06(06)
     A          R ENTRY
     A  21                              2  2'Local indicator enabled'
```

## Native storage and binding

An implicit structure length grows to 99. An explicit LEN must equal 99.
Only IND and fixed CHAR subfields are allowed, including native subfield arrays,
POS, OVERLAY, qualifiers and anonymous filler subfields. LIKEDS receives the
final length after binding, with the existing per-structure/program storage
bounds. Ordinary copies retain ordinary initialization and do not become
indicator banks. See IBM's
[indicator structure rules](https://www.ibm.com/docs/es/i/7.5.0?topic=structures-indicator-data-structure).

Default storage is encoded character '0' in every position. Explicit field INZ
values retain declaration/overlay order. As in other native structures, explicit
INZ/CLEAR uses the subfields' typed defaults; CHAR defaults can therefore produce
blanks. Transfer requires encoded '0' or '1' in each indicator position and
reports program status 105 before host I/O for other bytes. Character groups
should be assigned their indicator values before transfer. Character/indicator
bytes use the activation's captured CCSID, including the UTF-8 host extension.
See [native structure initialization](rpg-native-structures.md) and IBM's
[initialization rules](https://www.ibm.com/docs/en/i/7.6.0?topic=keywords-inzinitial-value).

Fields, character overlays, byte substrings and borrowed API arguments share
the same live native bytes. Contiguous indicator subfield arrays retain their
native dimension/indexing. Borrowed views preserve alias ranges and expire at
call return under the existing API argument contract.

## I/O and lifetime

WRITE, READ, EXFMT and supported subfile operations take conditioning flags
from the associated INDDS. Input publishes response flags only to that same
area. An untouched position retains its prior value; explicit false response
values reset the corresponding byte. Files without INDDS use global *IN as
before. Operation error/result indicators, EOF/FOUND and numeric file status
remain separate from DDS indicator storage.

The host receives a copied flag image. Input fields and response indicator
indexes are validated before provider commit; the prepared delta is published
after program field assignment. Invalid responses cannot overwrite global or
local indicator storage. CA responses preserve program input fields, while CF
responses publish accepted fields and local response indicators.

Opening/closing a display owner does not reinitialize the program's structure.
Retained RETURN keeps the bank; LR/reclaim or abnormal termination discards the
activation. A fresh call receives the initial indicator values. Sharing one bank
between files is explicit and does not imply shared file cursors or display state.

## Saved binding compatibility

New production WORKSTN bindings use version two and include the DDS INDARA flag
in their format signature, even when no INDDS is declared. Removing or changing
INDARA requires recompilation and is detected before terminal transfer. Tampered
flags, missing version-two metadata, duplicate properties and version downgrades
are rejected. Program-local INDDS names are source-hash bound and do not change
external record field identities.

Version-one snapshots remain readable and continue their original record-only
signature checks against live files. They have no INDDS declarations. Custom
layout providers may still supply legacy layouts for ordinary files; INDDS
requires explicit IndicatorArea=true. A saved version-two file set requires
indicator-area metadata on every file.

## Evidence and remaining work

`RpgIndicatorStructureTests` covers initialization, known bytes at CCSIDs
37/1208, sharing/isolation, aliases, native API receivers, retained calls,
pre-commit response validation, limits, invalid declarations and saved metadata.
`RpgIndicatorIntegrationTests` covers real CA/CF exchanges, local output
conditioning, saved source removal, retained RETURN/LR, actual CCSID 1208 jobs,
live INDARA changes and legacy snapshots. Existing saved native-API and feedback
tests now explicitly create and assert their requested job CCSID. The display
PTY compiles/calls RPGINDDS with local conditioning and two CA/CF exchanges.

[Multiple-occurrence structures](rpg-occurrences.md) now provide separate banks
selected by EVAL %OCCUR. Output and response addresses are captured at I/O entry;
each occurrence defaults to 99 character-zero bytes. Native fixed D-spec storage, PRINTER files,
program-described WORKSTN, and independent native-system reference acceptance
remain open. General arrays, full procedure ABI and extended feedback/cycle
semantics also keep the broader C09 requirements partial.


Final verification (2026-10-17): all 3285 tests pass, zero failures/skips,
including 52 new cases and all 156 final focused checks. Locked restore,
zero-warning/error Release build, expanded display PTY, generated inventory
and whitespace checks pass. The full-run queue timing fixture was corrected
with dedicated-worker synchronization and unconditional cleanup; queue
production behavior is unchanged. Checklist remains 36 complete / 70 open.


Multiple-occurrence follow-up verification (2026-10-17): all 3345 tests pass,
zero failures/skips, including 60 new occurrence cases and 237 final focused
checks. Saved INDDS CA/CF and the display PTY now exercise separate occurrences;
pending responses retain their original bank address. See
[the occurrence contract](rpg-occurrences.md) for complete verification and limits.
