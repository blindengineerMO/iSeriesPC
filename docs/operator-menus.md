# Operator menus, prompting, and work lists

`GO MAIN` uses the canonical 1–11/90 task groups, including Information Assistant
at 10 and Client Access at 11. `GO MAJOR` opens command groups; `VERB`, `SUBJECT`,
`SYSTEM`, and `WRKSYS` provide further navigation. MAIN/MAJOR numbering follows
IBM's [iSeries handbook](https://www.redbooks.ibm.com/redbooks/pdfs/sg246814.pdf)
and [IBM i technical overview](https://www.redbooks.ibm.com/redbooks/pdfs/sg248001.pdf).
Task screens expose implemented services. Later checklist packages supply spool
contents, message delivery, save/restore, and communications protocols; current
queue screens explicitly display queue definitions. They do not imply those
later packages are delivered. Every shipped target resolves to a registered
command or stored menu, verified by `WorkWithTests`.

Startup adds missing system menus. Exact, unsigned QSYS-owned legacy MAIN/MAJOR
defaults are upgraded; customized menus and signed objects remain intact.

Enter an option or CL command on the menu command line (up to 32 KiB, with
a horizontal viewport). F4 opens the command's
registered parameter metadata. Blank F4 opens a paged command selector; option 1
prompts a command. `SLTCMD CMD(CRT*)` selects by prefix; `*JOB*` selects by subject.
Only registered commands appear. F1 opens the shared command help. F3/F12 cancels
without executing or losing the original menu command/cursor. Parameters page
without losing values; text is quoted, nested CL values are preserved, secrets
are hidden, and malformed/injected parameter boundaries are rejected. Enter
runs through the current job's shared execution and authorization service.
Validation errors retain the form. Typed command objects and richer parameter
constraints remain C07; prompting currently reflects the finite built-in contract.

Work-with screens display 15 rows per page. Enter one row option and press Enter;
F5 refreshes, F9 selects the command line, F4 prompts, and F3/F12 returns. Row
options perform actual display, change, hold/release, end, or delete commands.
Destructive row actions require F6 on a confirmation screen; F12 cancels.
Actions use live service authority, so visible options do not grant permission.
Readonly command output wraps into 74-character rows. It is a snapshot and F5
does not repeat the originating command. Work screens have a maximum nesting
of 16, refreshable providers require filters above 4000 rows, and readonly output
shows an explicit limit notice above 9999 wrapped rows. Long object descriptions
are available through Display; compact work rows fit the terminal width.

`WRKLIB`, `WRKOBJ`, `WRKACTJOB`, `WRKSBMJOB`, `WRKSBS`, `WRKJOBQ`, `WRKOUTQ`,
`WRKMSGQ`, `WRKSYSVAL`, and `WRKUSRPRF` provide refreshable screens. `WRKSYSSTS`
reports runtime job/subsystem counts. `DSPOBJD`, `DSPFD`, `DSPFFD`, `DSPPGM`,
`DSPUSRPRF`, and `DSPJOBLOG` provide paged detail snapshots.

Profile creation/change/deletion requires *SECADM. `CRTUSRPRF` and `CHGUSRPRF`
support the cataloged class, special authorities, enabled status, group, startup
program/menu/current library, CCSID, and description. Settings-only edits retain
current credentials even if a password changed after the profile was loaded.
Host-file password provisioning additionally requires non-adopted *SERVICE.
Use `PWDFILE('/private/password.txt')` for password provisioning: a bounded,
private file is read, policy-validated, and hashed in the same profile update.
Inline passwords are rejected so CL source and submitted commands need not
contain credentials. `PASSWORD(*NONE)` removes local password authentication;
`*SAME` preserves it. Profile details never include password hashes. Built-in
profiles cannot be deleted. Administrative settings and password updates are
transactional; invalid settings leave the stored profile unchanged.

Validation: six `WorkWithTests`, three `CommandPromptTests`, existing menu,
profile, help, and display regressions; `tools/pty-display-smoke.py` drives real
F4 text entry, F1/back, creation, work-row confirmation/deletion/refresh, command
paging and signoff with exact terminal restoration. The full suite has 587 tests.
