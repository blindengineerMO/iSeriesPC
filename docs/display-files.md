# Display DDS and panels

`Ipc.Dsp` compiles fixed-column display DDS to versioned `PanelDefinition` JSON.
`CRTDSPF` stores the original source and compiled definition together as a typed
`*FILE` with attribute `DSPF`. Loading checks the definition against recompilation
of the stored source and its bound message constants. Rename/copy preserves the
compiled record and field names; the containing object's name may change.

```
CRTDSPF FILE(QGPL/CUSTOMERS) SRCFILE(QGPL/QDDSSRC) SRCMBR(CUSTOMERS)
RUNPNL FILE(QGPL/CUSTOMERS) RCDFMT(ENTRY)
```

`RUNPNL` is an iSeriesPC terminal preview command. It displays the named record
through the shared panel runtime and returns to the menu on a successful input/AID.
Batch and communication jobs reject this interactive request. It does not resume
an RPG `EXFMT`; the language binding is a separate C09 item.

## Source contract

The compiler uses IBM's display DDS positions, including
[length in columns 30–34](https://www.ibm.com/docs/en/i/7.6.0?topic=44-length-display-files-positions-30-through-34):

| Columns | Meaning |
| --- | --- |
| 6 | `A` specification |
| 7 | `*` comment; otherwise blank |
| 8–16 | up to three ANDed ` 01`/`N01` indicator conditions |
| 17 | `R` for a record declaration |
| 19–28 | record/field name; blank for a positioned constant |
| 29 | blank; external field references are not yet supported |
| 30–34 | field length |
| 35 | `A`/blank character, `S`/`Y` numeric, `L` date, `T` time, `Z` timestamp |
| 36–37 | decimal positions; numeric blank shift is recognized when supplied |
| 38 | `O` output, `I` input, `B` both, `H` hidden |
| 39–41, 42–44 | one-based row and column |
| 45 onward | keywords and quoted constants |

A field fits on one display row. Position 1,1 is reserved on a full screen;
window fields use coordinates relative to the usable window interior. Numeric
screen width includes sign/decimal/grouping/edit-word space beyond digit precision.
Limits: 1 MiB source, 10,000 lines, 5,000 columns per source line, 256 records,
1,024 fields per record and decimal precision up to 29 digits. Unsupported syntax
returns `IPC0006` with source/member, line, column and token. Unknown keywords and
unsupported option values are errors, including unsupported continuation/reference
forms; they are never silently accepted.

## Keyword subset

| Keyword | Accepted behavior |
| --- | --- |
| `DSPSIZ` | one 24×80 (`*DS3`) or 27×132 (`*DS4`) definition |
| `INDARA` | separate 1–99 indicator array in the runtime interface |
| `DSPATR` | `HI RI UL BL CS ND PR`, conditional attributes and protection |
| `COLOR` | `GRN WHT RED TRQ YLW PNK BLU` |
| `DFT` / quoted literal | default/constant text |
| `DFTVAL` | first-output default for named O/B fields, then program values |
| `ERRMSG` | text plus optional response indicator; first selected field message wins |
| `ERRMSGID` | message ID/file plus optional response indicator; replacement-data fields remain unsupported |
| `MSGCON` | length, message ID, file; binds text when the file is compiled |
| `COMP` | `EQ NE GT GE LT LE`, exact numeric or ordinal padded character comparison |
| `CHECK` | mandatory entry `ME`, mandatory fill `MF`, lowercase `LC`, allow blank `AB` |
| `CHKMSGID` | message ID/file for input validation errors |
| `DATE`, `TIME` | clock-supplied output constants |
| `DATFMT` | `*ISO *USA *EUR *JIS *YMD *DMY *MDY`; two-digit years use 1950–2049 |
| `TIMFMT` | `*ISO *HMS`, `HH:mm:ss` |
| `EDTCDE` | numeric-only Y/blank shift: `1 2 3 4 Z`; exact decimal formatting with blank-QDECFMT punctuation/zero suppression |
| `EDTWRD` | output-only Y/blank numeric fields; digit blanks, one zero-suppression stop, `. , /`, ampersand blanks and trailing minus; no conditional edit words |
| `CF01`–`CF24` | validation and input transfer; optional response indicator/text |
| `CA01`–`CA24` | response without field validation or edited-data transfer |
| `PAGEDOWN`, `PAGEUP` | page AID and response indicator; `ROLLUP`/`ROLLDOWN` aliases |
| `ALTPAGEDWN`, `ALTPAGEUP` | file-level alternate CF key, defaults F8/F7, requiring a pageable record |
| `ALIAS` | named field binding, up to 30 characters |
| `CSRLOC` | cursor row/column from declared hidden numeric fields |
| `RTNCSRLOC` | record, field and optional field-offset return into declared hidden fields; implicit `*RECNAME` form |
| `HELP`, `HLPID` | help request event with record/field context for the shared help service |
| `OVERLAY` | retain the previous buffer while outputting this record |
| `TEXT` | persisted descriptive metadata |

The plan spelling `PAGEDWN` is accepted as a compatibility alias for `PAGEDOWN`.
Alternative key behavior follows IBM's
[ALTPAGEDWN/ALTPAGEUP contract](https://www.ibm.com/docs/en/i/7.6.0?topic=beginning-altpagedwn).
Default and error transitions follow
[DFTVAL](https://www.ibm.com/docs/en/i/7.6.0?topic=d-dftval) and
[ERRMSG/ERRMSGID](https://www.ibm.com/docs/en/i/7.5.0?topic=e-errmsg).
The numeric subset follows the documented
[edit-word digit positions](https://www.ibm.com/docs/en/i/7.6.0?topic=80-edtwrd-edit-word-keyword-display-files)
and [blank decimal format](https://www.ibm.com/docs/en/i/7.4.0?topic=values-decimal-format-qdecfmt-system-value).
Asterisk protection, floating currency, CR status, user-defined edit codes and
other decimal-format locales remain outside this compiler subset.

## Input/output and messages

`PanelSession.Write` copies program values and indicators into a display operation.
`Handle` returns acceptance, normalized AID, a new value dictionary, indicator
snapshot and cursor state. Character, exact-decimal, date, time and timestamp
inputs are validated before any edited fields are returned. Failed validation
keeps the panel active. CA keys return the original program values. Hidden H
fields remain in the record without screen cells; ND renders blank cells. Program
values and DDS literals reject terminal control characters.

A failed output leaves the session unavailable for input until a successful write.
`HELP` opens the shared [UIM help viewer](shared-help.md) through HLPPNLGRP/HLPARA
bindings; legacy HLPID-only records still expose their context. Editor modes, key sequences, renderer snapshots and real keyboard/resize acceptance
are documented in [terminal-keyboard.md](terminal-keyboard.md).

The typed `*MSGF` description catalog supports the display dependency:

```
CRTMSGF MSGF(QGPL/TEXTS)
ADDMSGD MSGID(DSP0001) MSGF(QGPL/TEXTS) MSG('Quantity must be positive.')
```

New IDs have a three-character letter/alphanumeric prefix and four hexadecimal
suffix positions; first-level templates allow 1–132 printable characters, up to
4,096 descriptions per file. Legacy text remains readable. Adds are transactional
and duplicate IDs fail. `MSGCON` copies text into the compiled panel, so its source message file
is not needed afterward. `ERRMSGID` and `CHKMSGID` resolve the authorized message
file through the current job's library list at display/validation time; deleting or
renaming that late-bound file can make later displays fail. [Message descriptions](message-descriptions.md) now include substitution formats,
second-level text, severity and CL retrieval. Predefined MSGQ delivery and the
remaining message formats still belong to C07/C11.

## Subfiles and windows

`DisplayFileSession` owns one open file. `Subfile("ROWS").Write(rrn, values,
indicators)` loads a new relative record; `Update` replaces an existing one.
`ReadNextChanged(after)` returns and consumes the next changed record in ascending
RRN order. Program updates can select conditional `SFLNXTCHG` to make the row
available again. Snapshots copy both scalar values and indicators; hidden fields
stay in storage and never enter the composed display. Opens do not share state.
The behavior follows IBM's [SFLNXTCHG contract](https://www.ibm.com/docs/en/i/7.6.0?topic=80-sflnxtchg-subfile-next-changed-keyword-display-files).

A control record requires `SFLCTL(record) SFLSIZ(n) SFLPAG(n)`; RRN values are
1–SFLSIZ, at most 9999. Each SFL has one control. `SFLDSP` and `SFLDSPCTL` select
rows and control fields; `SFLCLR` removes all stored rows. The compiler checks
page dimensions, control-field overlap, keyword placement and duplicate controls.
`SFLEND`/`SFLEND(*MORE)` paints More/Bottom in a reserved seven-column area at the
right of the last subfile line. Unsupported options fail compilation.

Page keys validate and transfer all edited rows atomically, then page locally.
Only an exhausted page boundary returns the configured PAGEUP/PAGEDOWN response
indicator to the caller. Internal paging clears that response indicator. `SFLFOLD`
starts with full multi-line records; `SFLDROP` starts with their first lines only.
Both accept a CF/CA function key for toggling; CF validates/transfers edits, CA
preserves stored data. Truncation increases page capacity without losing detail
fields, hidden keys, or per-record indicators. Changed status comes from actual
editing, so retyping the original value still yields a changed row and untouched
numeric conversions do not. A successful input consumes the editor's modification
flags; reading a changed row separately consumes its stored changed status.

`WINDOW(row column height width [*MSGLIN])` supports constant geometry with
relative field locations, the reserved message line and ASCII borders. A subfile
control can carry WINDOW to display its rows inside the window. `WDWSFL` is the
SDA window-subfile record type, represented by SFL/SFLCTL/WINDOW DDS; it is not an
additional accepted DDS keyword. Coordinates follow IBM's
[WINDOW contract](https://www.ibm.com/docs/en/i/7.5.0?topic=80-window-window-keyword-display-files).
Dynamic/reference/default-position windows, border customization, *NOMSGLIN and
other window options produce unsupported-syntax diagnostics.

Writing a window suspends the existing editors, up to 12 nested windows. Rewriting
an existing window replaces its content and removes windows above it. `CloseWindow`
restores underlying cells, unsent input, cursor, and subfile page/fold state. Writing
a full-screen record dismisses windows. Input reaches only the top layer. RUNPNL
uses this same file runtime and defaults to the first non-SFL record; it previews
an empty subfile until a host program loads records. RPG display binding is C09.

## Message subfiles

The plan's `SFLMSG` label maps to the canonical DDS `SFL SFLMSGRCD(line)` with
exactly two predefined hidden character fields: `SFLMSGKEY` (four bytes), followed
by `SFLPGMQ` (ten characters). Their lengths/hidden usage can be omitted in DDS.
Message controls use the same size, page, display and clear behavior. Messages are
one high-intensity line, clipped to the available 76/128-column message area.
They do not accept or return edited message fields. SFLNXTCHG is invalid here.
See IBM's [SFLMSGRCD definition](https://www.ibm.com/docs/en/i/7.4.0?topic=80-sflmsgrcd-subfile-message-record-keyword-display-files).

The file-open program-message resolver receives the program queue and four opaque
key bytes represented losslessly as four Latin-1 characters. It returns printable
text and optional help context. Missing messages fail output; keys and queue names
are never rendered. Paging retains the cursor, and F1 selects the message beneath
it. This is the display-side message lookup contract: durable program MSGQs, delivery
and automatic queue initialization belong to C11; shared help panels use the [UIM help viewer](shared-help.md). The callback must apply its owning queue's authority policy.

## Verification

`DisplayPanelTests` compiles actual fixed-column DDS, checks unsupported-source
locations, JSON persistence and typed rename, compiles from a real source member
through `CRTDSPF`, drives terminal menu preview, checks CF/CA/indicator transitions,
validates exact numeric/date values, returns cursor/help context, confirms hidden
values are absent from ANSI output, and checks bound message constants after the
original `*MSGF` is removed.

`SubfileTests` checks sparse/bounded RRN storage, forced and consumed changed rows,
atomic page validation, CF/CA behavior, numeric edit tracking, paging/folding,
per-row indicators, hidden keys, message lookup/paging/help, independent file opens,
control clear/rewrite, nested-window limits, invalid layouts, and restoration of
unsent text and cursor after closing a window.
