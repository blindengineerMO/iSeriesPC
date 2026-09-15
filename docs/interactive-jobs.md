# Sign-on and interactive jobs

Successful sign-on creates one interactive QINTER job under the authenticated
profile and session. Its CCSID and initial current library come from the profile;
null or `*CRTDFT` current library resolves to QGPL. Invalid configured libraries
fail startup. QUSRLIBL supplies the initial user library list: an unconfigured empty
value uses QGPL QUSRSYS, while `*NONE` explicitly selects an empty list. The system
library list, current library and user list participate in subsequent resolution.

The profile's initial program runs through the shared CALL runtime under that job's
identity, authority checks, cancellation, accounting and logging. Null, `*NONE`
and QCMD mean no initial application program. Program changes to the job environment
apply before resolving the initial menu. A missing/denied/failed initial program
or menu fails sign-on, completes the job abnormally and revokes the new session.
`INLMNU(*SIGNOFF)` semantics are supported by the profile's InitialMenu property:
return from the initial program completes the job and signs off. Other profiles
enter their configured menu, default MAIN. Profile administration commands are
covered by the later command/admin workflow items.

## Group jobs

```cl
CHGGRPA GRPJOB(HOME) TEXT('Home work')
TFRGRPJOB GRPJOB(REPORTS) INLGRPPGM(QCMD)
TFRGRPJOB GRPJOB(HOME)
TFRGRPJOB GRPJOB(*PRV)
TFRGRPJOB GRPJOB(*SELECT)
ENDGRPJOB GRPJOB(REPORTS)
```

CHGGRPA names the current group job and creates its 512-byte GDA. TFRGRPJOB creates
or resumes a named job in the current group; creation permits a qualified initial
program or QCMD. Returning to an existing job retains its menu, pending input,
DSPF/subfile/window/help/editor state, current library, library list, private LDA,
locks and open paths. Group members share their GDA. Newly created members have a
blank LDA and inherit the parent's current library list and CCSID. Names are unique
within the group. The selector accepts a listed number/name; F6 starts a named job,
F11 ends it and F3/F12 return. Name the first job before starting additional members.

Only the selected job executes terminal commands. Suspended jobs are persisted as
Held with execution state Running; they retain resources until resumed or ended.
There are at most 16 jobs per group. ENDGRPJOB supports a name or `*CURRENT`, returns
to another available job when ending the current one, and signs off if none remain.
SIGNOFF ends every job on the terminal. Transfers and group attribute changes are
audited. Suspension/activation and the transfer event commit in one transaction.
A failed initial group program returns to the previous job and completes the failed
child. Failed creation does not overwrite an existing job.

## System Request

Ctrl-G opens System Request over the current screen. Option 1 creates or switches
to an alternate interactive job; option 6 opens the group selector; option 3
runs DSPJOB; option 90 signs off. F3/F12 restore the calling screen unchanged.
The alternate job uses the same authenticated profile and has a separate GDA/group
of up to 16 jobs, for at most 32 jobs on the terminal. TFRSECJOB is an iSeriesPC
command exposing option 1. A system request does not discard unsent display input.

The QSYS/SYSREQ menu is the iSeriesPC authority/signature control object. Revoking
its use denies System Request and hides an already-open request screen. Menu options
are resolved through the same guarded menu/command paths. Transfers cannot reach a
different terminal family, even for the same profile. Session revocation or a
connection failure closes the family; job-owned resources and GDA/member metadata
are reclaimed. Completed jobs retain their logs. Restart recovery ends abandoned
interactive jobs rather than attempting to recreate in-memory screens.

This is the finite, input-boundary group/session split in WP6. Transfer commands
are supported on the interactive command line; embedded CL transfers, SETATNPGM,
CHGGRPA *NONE, cross-profile alternate sign-on, and interruption/suspension in the
middle of an executing program are not implemented. An executing program can still
be cancelled by disconnect/signals/ENDJOB. IBM's broader group-job and System Request
semantics are described in [group job concepts](https://www.ibm.com/docs/en/i/7.6.0?topic=reference-group-jobs)
and [using a group job](https://www.ibm.com/docs/en/i/7.6.0?topic=sessions-using-group-job).

Acceptance covers startup and failure cleanup, same-terminal ownership, the 16-job
limit, failed initial program rollback, separate LDAs/shared GDAs, alternate-group
isolation, live SysReq authority revocation, audited transfer rollback and restart
recovery. A real PTY switches from a dirty DSPF to an alternate job and another group
job, returns with the exact unsent value/cursor, then verifies normal cleanup of all
three jobs. See `tools/pty-display-smoke.py` and [terminal lifecycle](terminal-lifecycle.md).
