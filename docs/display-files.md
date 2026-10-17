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
Batch and communication jobs reject this interactive request. Compiled RPG
`EXFMT` uses the [WORKSTN language binding](rpg-spec.md#12-workstn-display-files-and-subfiles).

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
by `SFLPGMQ` or `SFLPGMQ(10)` (ten characters). Their lengths/hidden usage can be omitted in DDS.
No other field keywords or indicator conditions are allowed on these predefined fields.
Message controls use the same size, page, display and clear behavior. Messages are
one high-intensity line, clipped to the available 76/128-column message area.
They do not accept or return edited message fields. SFLNXTCHG is invalid here.
See IBM's [SFLMSGRCD definition](https://www.ibm.com/docs/en/i/7.4.0?topic=80-sflmsgrcd-subfile-message-record-keyword-display-files).

`DisplayFileStore.Open(library, name, environment)` binds an open file to the
executing job's active program queues through `JobProgramMessageSource`. The
queue field selects `*` (the output caller), `*EXT`, or the nearest active frame
with the specified program name. Named object queues and CL relationship values
such as `*PRV` are not display queue names. Even *ALLOBJ cannot use another job's
retained source. The lower-level callback interface remains available to other hosts.

Writing a subfile row looks up its queue and four-byte message reference key,
represented losslessly as four Latin-1 characters. Output captures immutable
message text and second-level text using the saved predefined description and
replacement bytes. Missing messages fail that row's write/update without replacing
an existing row. Paging and later control writes use the captured text even after
the queue entry or source MSGF is removed. These reads never mark a message old,
handle an exception, consume an inquiry or deliver a default reply. Unsupported
terminal glyphs and control characters become `?`; keys and queue names are hidden.

For single-output message loading, specify `SFLINZ` and exactly one `SFLPGMQ`
field on the control record. When SFLINZ is selected, pass the queue name in that
field to `Write(control, values, indicators)`. The service reads all messages in one
SQLite snapshot, including beyond its ordinary 1,000-entry listing page. The
subfile grows beyond SFLSIZ to fit this snapshot (up to the queue's 4,096-message
limit), resets to its first page, and replaces previous rows. An inactive SFLINZ
retains previous rows/page; SFLCLR clears copied rows without touching the queue.
An initialization or control-output failure preserves the prior rows, capacity,
page, editor and buffer. With both SFLINZ and SFLCLR selected, initialization
supplies the replacement rows. Ordinary non-message SFLINZ and the ILE 276-byte
queue layout remain unsupported with compile-time diagnostics.

With HELP selected, F1 returns the captured `PanelResponse.MessageHelp` for the
message under the cursor. The terminal host presents first/second-level text in
the shared help viewer and restores the suspended display on return. Paging
retains the cursor. See IBM's [SFLPGMQ output rules](https://www.ibm.com/docs/en/i/7.6.0?topic=s-sflpgmq)
and [message-subfile example](https://www.ibm.com/docs/en/i/7.4.0?topic=type-example-message-subfile-using-dds).

Compiled CL binds DCLF/SNDF/RCVF/SNDRCVF fields and indicators to the terminal,
including control-format SFLINZ and saved message help. See the [CL display
contract](cl-runtime.md#display-files-and-terminal-exchange). Compiled RPG WORKSTN
binds EXFMT/READ/WRITE, SFILE RRNs, READC/CHAIN/UPDATE and message subfiles to the
same runtime; see [the supported contract](rpg-spec.md#12-workstn-display-files-and-subfiles).
ILE procedure selection and asynchronous/multiple-device exchange remain C07/C09/C11 work. RUNPNL still
previews empty values and cannot supply a SFLINZ queue field. The terminal fixture
tests the public live store/session interface and compiled CL/RPG exchange.

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

`MessageSubfileTests` uses real job/frame queues to verify opaque keys (including
high-bit bytes), CCSIDs 37/1208, saved private MSGF text/help, unchanged receive and
exception state, inquiry non-delivery, nearest recursive frames, foreign-job
denial, bulk loading beyond a listing page, automatic size extension, 80/132-column
clipping, refresh rollback, source removal, and strict DDS diagnostics. The display
PTY script drives a compiled live message subfile through paging and F1 after its
queue entries and private MSGF have been removed, then checks terminal restoration.
