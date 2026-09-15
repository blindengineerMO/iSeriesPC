# Terminal lifecycle

The Linux console uses its controlling PTY (`stty -F /dev/tty`), captures `stty -g`
before raw/no-echo mode, and restores that exact state on exit. Setup, rendering,
input and controller execution are inside the cleanup boundary. Cleanup resets
ANSI attributes, disables bracketed paste, restores autowrap and terminal settings,
and disposes the current session controller. A controller transition disposes the
previous controller.

The host reads a private close-on-exec stdin duplicate with bounded `poll(2)` and
incremental UTF-8 decoding. Cancellation ends a pending read within a polling
interval, including an incomplete UTF-8 sequence; disposal joins that read before
closing the descriptor. This avoids leaving a blocked console reader after session
shutdown. EOF ends the connection with a nonzero status. See the Linux
[poll](https://man7.org/linux/man-pages/man2/poll.2.html) and
[termios](https://man7.org/linux/man-pages/man3/termios.3.html) interfaces.

HUP, INT, QUIT and TERM request connection shutdown and return 128 + signal number.
TSTP deliberately ends this login-shell connection with status 148; OS-level
suspend/resume is not offered. Signals cancel local command execution or close the
remote session socket, allowing the server to cancel its owned job. Handlers use
[PosixSignalRegistration](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.posixsignalregistration.create?view=net-8.0)
and let the session unwind and restore the PTY. SIGKILL/SIGSTOP cannot be handled;
server disconnect/crash recovery owns abandoned-job cleanup. After a kernel PTY
hangup, the terminal may no longer exist to restore; job/controller cleanup still
runs, and failure to restore is reported.

At the input prompt, Ctrl-C ends the connection with status 130 and Ctrl-D with
status 1. Inside bracketed paste/control sequences these bytes cannot sign off or
execute commands. An external signal can interrupt an executing command; literal
keyboard input is processed when the command returns. F3 and normal SIGNOFF retain
their screen-specific semantics. EOF, signal, I/O failure and controller errors
cancel/dispose a local interactive job as abnormal; successful signoff completes
normally. Shared-server connections have their own disconnect monitor.

Terminal dimensions are checked continuously, including SSH window-change
notifications. A terminal too small for the current display receives a size prompt;
ordinary input pauses until it fits. F3/Attention remain available. Restoring the
size restores the underlying display, unsent input and cursor. Supported screens
are 24x80 and 27x132; see [keyboard behavior](terminal-keyboard.md).

CI runs `tools/pty-lifecycle-smoke.py` for real PTY signals, Ctrl-C/D, paste isolation,
EOF/hangup, missing-stty setup failure, input/output/controller failures, Unicode,
partial UTF-8 cancellation, controller disposal and exact original settings.
`tools/ssh-smoke.py` logs in through real OpenSSH/PAM and verifies local and remote
PTYs, F1/F3, command execution, resize/recovery, signoff/restoration, and job cleanup
after the SSH client is killed. The authentication fixture uses temporary keys,
PAM configuration and catalog; it does not alter host accounts or system policy.
