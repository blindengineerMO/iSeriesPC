# Screen Design Aid

`STRSDA SRCFILE(QGPL/QDDSSRC) SRCMBR(ENTRY)` opens a DDS source member in the
interactive terminal job. A missing or empty member starts with a MAIN record.
Create a source file first, for example:

```cl
CRTSRCPF FILE(QGPL/QDDSSRC) SRCDTALEN(240)
STRSDA SRCFILE(QGPL/QDDSSRC) SRCMBR(ENTRY)
```

`SRCDTALEN` is an iSeriesPC extension specifying the source data column width
(default 100, range 44–5000); it does not include IBM source sequence/date headers.
SDA writes fixed-column DDS. Lines that exceed the source file width fail saving
without truncation. Source is limited to 1 MiB and 10,000 lines.

Select a record and press Enter for its fields. F6 adds, F7 edits, and F11 removes
the selected record, field or menu option from the draft. F8 adds/changes/removes
a window; F9 adds a subfile and control with editable rows and hidden keys.
F10 creates a menu layout and opens its options. Page keys navigate long lists.
F12 returns to the record list. F1 opens the shared help viewer.

Field forms support character, numeric, date, time and timestamp types, input,
output, both and hidden usage, coordinates, lengths, decimal positions, literals,
attributes and indicator conditions. Existing supported DDS keywords survive
edits. Every edit runs through the same DSPF compiler used by CRTDSPF; invalid
edits leave the draft intact. See [display DDS](display-files.md) for exact
supported keywords and diagnostics. This is a finite DDS design editor, not
full IBM SDA source-format compatibility.

F4 previews the selected record in the actual display runtime. Subfile preview
supplies up to 20 sample rows. F3/F12 return from preview. F2 saves source.
F5 saves and compiles a DSPF or MENU, prompting for target and explicit replacement.
F3 exits; a dirty draft offers F2 save-and-exit, F12 discard, or F3 continue editing.
Reopen STRSDA to edit saved DDS, compile again with replacement YES, then execute
`RUNPNL FILE(QGPL/ENTRY)` to exercise the compiled panel.

Saving compares the opening revision with the current source in one SQLite
transaction. Concurrent changes fail with IPC0130 and keep the draft open.
Reconcile changes before discarding and reopening; there is no automatic merge
or force-save. Saves require live source change authority, acquire an exclusive
file allocation, and atomically update rows and an audit event. Source text is
excluded from that event. Stable reads conflict with record writers. Sequence
numbers and dates are regenerated on save; trailing spaces are canonicalized.

Menu designs persist option metadata in versioned `IPC.SDA.MENU` DDS comments;
these are an iSeriesPC extension. Up to 16 options use canonical numbers 1–999
and Command, SubMenu, Prompt, Exit or SignOff actions. Opening verifies that the
metadata agrees with the generated DDS layout. Menu fields are edited through
the option editor. `CRTMNU MENU(QGPL/APP) SRCFILE(QGPL/QDDSSRC) SRCMBR(APP)`
compiles the persisted design into an executable menu. Targets resolve when used;
authority checks remain with the owning command/menu service. F5 calls the same
CRTDSPF/CRTMNU command handlers, preserving runtime logging and authorization.

`REPLACE(*YES)` replaces only a DSPF for CRTDSPF; it cannot overwrite a physical
file. Replacing compiled content removes its old signature and preserves its
owner. Signing policy may require signing the replacement before execution.

Acceptance includes compiler round trips for fields/indicators, windows and
windowed subfiles; menu action execution; atomic save rollback and concurrent
revision rejection; access and record-lock denial; keyboard workflows; and a
real PTY save → compile → reopen → edit → replace → RUNPNL sequence. The latter
runs in `tools/pty-display-smoke.py` in CI.
