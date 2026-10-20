# Job inquiry replies and the system reply list

`CHGJOB INQMSGRPY(*RQD|*DFT|*SYSRPYL)` now controls inquiries sent by the
executing job. Required replies remain pending. Default mode sends the stored
message default, or the existing system-default representation when none exists.
System reply-list mode uses the first matching entry; an unmatched message, a
manual-reply entry or an invalid automatic reply remains pending.

`CRTJOBD` and `CHGJOBD` accept the same policy. Submission captures it from the
job description. Later description changes affect later submissions. Interactive
jobs default to `*RQD`; group jobs inherit the parent's current policy. Policy
changes affect subsequent sends and survive catalog restart. WRKJOB shows the
current value. The normal job-owner or job-control authority check applies.

`ADDRPYLE`, `CHGRPYLE`, `RMVRPYLE` and `WRKRPYLE` manage the durable list.
Sequences range from 1 to 9999 and are matched in ascending order. Identifiers
may be exact, generic `pppnn00`/`ppp0000`, or `*ANY`. Only predefined inquiries
match list entries. Comparison data tests the original replacement bytes, with
a one-based offset from 1 through 999 and up to 28 encoded bytes. Supported
comparison CCSIDs convert to the replacement-data CCSID; `*HEX` compares raw
bytes. A short or unconvertible comparison does not match.

Replies may be explicit text of at most 32 characters, `*DFT` or `*RQD`.
The existing description rules validate explicit automatic replies, including
special-value mapping. Invalid or unrepresentable replies require manual action;
they do not fall through to a later entry. Successful list replies carry native
return type 25 through RCVMSG and the shared message API receiver. Job default
replies retain return types 23/24. Immediate inquiries support default mode but
do not match reply-list entries.

```cl
ADDRPYLE SEQNBR(27) MSGID(USR1234) CMPDTA('BC' 2) RPY(Y)
CHGJOB INQMSGRPY(*SYSRPYL)
/* A predefined inquiry with matching replacement bytes receives reply Y. */
CHGJOB INQMSGRPY(*RQD)
RMVRPYLE SEQNBR(27)
```

List changes require QPGMR or non-adopted *ALLOBJ authority. Duplicate adds,
missing changes/removals and invalid attributes fail without altering entries.
Changes read and update the current entry inside one writer transaction.
Automatic reply, original inquiry and sender copy commit together; receiver
preparation failures roll them all back. Message queue authority, quotas, job
identity, reply delivery and predefined snapshot checks remain in force.

Schema 33 adds policy columns to jobs/job descriptions and the reply-list table.
Upgrade preserves existing data with `*RQD` defaults. A failed upgrade rolls back
all new schema and history. Job-description copy retains policy, and queue/reply
correlation survives the existing persisted message representation.

The native contracts are documented in IBM's [CHGJOB reference](https://www.ibm.com/docs/en/i/7.5.0?topic=ssw_ibm_i_75%2Fcl%2Fchgjob.html),
[ADDRPYLE reference](https://www.ibm.com/docs/en/i/7.5.0?topic=beginning-add-reply-list-entry),
and [QMHRCVPM return types](https://www.ibm.com/docs/en/i/7.4.0?topic=ssw_ibm_i_74%2Fapis%2FQMHRCVPM.html).

This supplies the shared policy needed for member-full recovery. CPA5305
emission, operator-granted member capacity and database retry are still open.
`DUMP(*YES)` is explicitly rejected pending native job-dump/spool delivery;
`DUMP(*NO)` is supported. Full queue break/delivery modes, external batch queue
automatic defaults, inquiry exits, native job-information formats for this
attribute and independent IBM acceptance remain open. C07/C08/C11 remain partial.

Verification (2026-10-19): all 6771 solution tests pass with zero failures/skips
in 10m54s, including 39 new cases. All 222 focused cases and 4348 broad
RPG/binding/work cases in the full suite pass. Locked restore, a Release build
with zero warnings/errors, 24x80/27x132 display PTY acceptance, generated inventory,
local links and whitespace checks pass.
