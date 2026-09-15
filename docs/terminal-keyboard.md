# Terminal display and keyboard contract

The Linux terminal client uses the same compiled display-file runtime as direct
host callers. Display sizes are fixed at 24×80 and 27×132 cells. A larger terminal
anchors the display at the upper left; a smaller terminal shows its required size
and pauses ordinary input until it grows. F3/Attention can return through the
controller while undersized. Resizing preserves the editor's unsent text and cursor.
Window dimensions are read from the actual input PTY, including when idle. A PTY
reporting zero dimensions uses 24×80; dimensions are bounded before allocation.

The renderer sends the logical cursor after full output, changed cells, cursor-only
moves and display-size changes. Bottom-right output runs with terminal autowrap
disabled; the client enables it again on exit. Intensity, reverse video, underline,
blink, column separators and colors have explicit ANSI snapshots. Non-display cells
always emit spaces, regardless of their stored character. Control characters cannot
be emitted from screen cells. The cell contract accepts single-width BMP characters,
including the Latin characters in the supported job CCSIDs; combining marks,
surrogates and wide characters are rejected by the DDS/field interfaces. Other
screen sources render unsupported glyphs as `?`. Use a UTF-8 terminal configured
for single-width ambiguous characters. This is a fixed-cell interface, not a
variable-width Unicode layout engine.

## Keys

VT key sequences and modifier encoding follow the primary
[xterm control-sequence specification](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html).
The following bindings are the iSeriesPC client contract:

| Terminal key | Display action |
| --- | --- |
| Enter | Enter AID, typed validation and transfer |
| F1–F12 | PF1–PF12, SS3/CSI forms |
| Shift-F1–Shift-F12 | PF13–PF24; legacy xterm PF13–PF20 sequences also accepted |
| Page Up / Page Down | PAGEUP / PAGEDOWN (RollDown / RollUp AIDs) |
| Insert | toggle Insert/Replace; each new editor begins in Insert mode |
| Delete / Backspace | remove at cursor / before cursor |
| Home / End | start / end of active field text |
| Left / Right | move inside the field |
| Up / Down | nearest input field on the preceding/following row; cursor navigation on output-only panels |
| Tab / Shift-Tab | next / previous unprotected field; next wraps, previous stops at the first |
| Ctrl-Right / Ctrl-Left | next / previous field |
| Ctrl-K | erase from cursor to end of field |
| Ctrl-E | field exit: erase remainder and advance |
| Ctrl-A / Ctrl-B / Ctrl-F | PA1 / PA2 / PA3 |
| Ctrl-G / Ctrl-L / Ctrl-P | System Request / Clear / Print AIDs |
| Escape alone, idle for 250 ms | Attention (PA1) |

CF keys validate and transfer; CA and attention AIDs return without edited-field
transfer. Display hosts decide the operation associated with an attention event.
Print output still requires the C10 spool binding; system-request/operator workflows
remain in their C06/C18 items. Full insertion never discards the last nonblank
character silently: it reports a full field, allowing Replace or deletion. Protected
fields cannot be changed and are skipped by editor navigation.

## Escape strings and paste

The streaming parser retains at most 64 escape characters. Unsupported CSI/SS3
sequences are consumed through their final character. OSC/DCS/PM/APC strings are
ignored through their terminator, including across idle periods. A lone Escape is
resolved on idle; a partial control string never becomes plain command text merely
because input paused. Bracketed paste inserts text only, converts newline/tab to
spaces, and suppresses embedded AIDs and control sequences. Submission requires a
separate Enter after the paste ends.

The client requests bracketed paste on entry and disables it on exit. Original
termios flags are restored by the existing controlling-PTY path. Signal/EOF/error
lifecycle acceptance and SSH provisioning are completed in C06; this item does not
claim those additional recovery paths are verified.

## Evidence

`TerminalInteractionTests` checks all 24 function keys, oversized/unknown/fragmented
escape strings, paste suppression, distinct attention/edit keys, renderer snapshots,
cursor-only and dimension changes, protected fields and insertion/deletion behavior.
`DisplayPanelTests` and `SubfileTests` cover typed numeric/date/time validation,
CF/CA indicators, overlays, hidden fields and modal state.

`tools/pty-display-smoke.py` launches the test-only `Ipc.TerminalFixture` using the
actual `TtySession`, compiler and `DisplayFileSession`. An independent Python VT
screen decoder verifies both display sizes, the initial cursor, Insert/Replace/
Delete, paste without submission, all 24 function keys, attention, small/large resize
transitions, final typed values, hidden-field non-disclosure and exact termios
restoration. CI runs it alongside the sign-on and SSH/PAM PTY acceptance scripts.
The fixture is a separate test executable and exposes no production bypass option.
