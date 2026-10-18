# Job environments

Catalog schema 16 adds live job data areas and explicit group identity. The server's
job runtime owns call frames, file overrides and open paths. These handles remain
inside their owning job and are never serialized into a submitted job or passed
as a process-global current environment.

| State | New interactive job | Submitted batch job | Nested call / return |
| --- | --- | --- | --- |
| Current library / library list | profile/system defaults | JOBD and explicit SBMJOB choices, including `*CURRENT` | shared job attributes; changes remain after return |
| CCSID | profile | submitting job | shared job attribute |
| LDA | 1,024 blank bytes | atomic byte copy at submission | same job's storage; mutations remain |
| Group identity / GDA | no group until group creation | no inherited group | same group identity |
| Initialization area | unavailable | pending bytes materialized at routing start | same routing-step storage |
| Overrides | empty | empty | inherited lookup; call overrides removed on return/error; job overrides remain |
| Open paths | empty | empty | explicitly shared paths reuse position; call paths close on return/error |

Job current-library changes are validated at the service boundary as well as the
command parser. Direct reads/writes of private environment state require the
executing job identity, even when both jobs use the same profile or an administrator
profile. Trusted host code with no operation identity remains able to coordinate
lifecycle. No client API accepts an arbitrary environment handle or switches a
connection's job based on user-supplied text.

## Local, group and initialization storage

LDA is always created with the job and is copied in the same transaction as batch
submission, the command request and its log. Later parent/child changes are
independent. Its copy survives a queued-job restart. This matches IBM's
[local data area description](https://www.ibm.com/docs/en/i/7.6.0?topic=areas-local-data-area).

`JobService.CreateGroupJob` accepts an owned live interactive parent. A fresh,
opaque group identity is assigned when needed; the child joins that exact group
under the same profile/CCSID. It starts with its own blank LDA and empty runtime
handles. The GDA is 512 bytes and remains until the final group member ends. A
same-profile job outside the group cannot select its GDA by guessing the group ID.
The terminal's CHGGRPA creates GDA storage when the first group is named; TFRGRPJOB
preserves it across transfers. Secondary terminal partitions have independent groups.
These sizes and lifetimes follow IBM's
[group data area contract](https://www.ibm.com/docs/en/i/7.6.0?topic=areas-group-data-area).

Initialization data accepts up to 2,000 raw bytes at submission. Claiming a routing
step creates a 2,000-byte area, copies the submitted bytes, pads the remainder with
blanks and clears the pending payload in the same claim transaction. It is shared
through calls in that job and removed at job end. No implicit scalar packing or
native IBM parameter format is claimed.

IBM names the PIP data area `*PDA` and defines it for **prestart jobs** in its
[program initialization parameter documentation](https://www.ibm.com/docs/en/i/7.5.0?topic=areas-program-initialization-parameter-data-area).
The plan's batch-routing initialization requirement is implemented as an explicit
iSeriesPC extension; `*PIP` is not a command alias. Prestart pool dispatch and
language/API parameter codecs are separate from this raw storage boundary.

Private areas are bytes, not standalone `*DTAARA` objects. New blank bytes are
`0x40` for CCSID 37/500/1047 and `0x20` for the other supported job CCSIDs. There is
no CCSID conversion during LDA inheritance. The byte API uses zero-based offsets;
the CL command range uses one-based positions. Range overflow fails before writing.

```
CHGDTAARA DTAARA(*LDA 1 5) VALUE('hello')
DSPDTAARA DTAARA(*LDA 1 5)
```

The current commands support `*LDA`, `*GDA` and `*PDA`; character values use the
job CCSID with strict conversion and byte-length validation. Named data areas are
documented in [data areas](data-areas.md); compiled CL supports RTVDTAARA return
variables for private areas. A job without group membership reports CPF1046 for
`*GDA`; unavailable routing initialization reports CPF1072 for `*PDA`. These IDs
remain usable by MONMSG. Partial GDA writes
serialize through SQLite transactions, preventing lost updates in different ranges.
Job completion/cancellation/recovery deletes private areas transactionally and
removes unreferenced groups. Completed jobs do not retain these application bytes.

## Overrides and open paths

```
OVRDBF FILE(INPUT) TOFILE(QGPL/ROWS) MBR(ROWS) SHARE(*YES) OVRSCOPE(*JOB)
DSPOVR FILE(INPUT)
DLTOVR FILE(INPUT) LVL(*JOB)
```

The implemented override subset is replacement file/member, `SHARE`, `WAITRCD`, and explicit
`*CALLLVL`/`*JOB` scope. The default is `*CALLLVL`; at the command-entry root its
lifetime is the job. Nested calls search the nearest override first. An override
issued inside a called program with job scope survives that program's return.
This uses the call/job distinctions in IBM's
[override lifetime documentation](https://www.ibm.com/docs/en/i/7.4.0?topic=overrides-how-system-processes).
Activation-group scope, parameter merging and additional file/print/query options
remain in their owning C07/C08/C10 items and are rejected by this command contract.

RPG file opening resolves the owning job's override before constructing a cursor.
The registry keys a path by resolved library/file/member; declared DISK paths also
separate access modes and effective record-wait policies. Shared paths carry one
cursor position through nested calls; job-scoped shared paths can survive separate
calls. Unshared paths close when their handle is disposed. Returning from a call
closes resources owned by that call frame and restores its parent's override lookup,
including exceptional exits. A `CLOSE` request destroys a shared path after its last
active handle closes; the next open starts fresh. Removing an override does not
retroactively retarget an already open path.

Input paths hold shared-read allocations, while declared update/output paths use
shared-update allocations with `OpenPath` lifetime.
It cannot be removed with `DLCOBJ`, whose scope is explicit job allocations. Fresh
opens revalidate authority and refresh file data/automatic record allocations while
preserving the cursor position. Structural deletion/move/rename remains blocked
until the path closes. Job end disposes all paths and activation resources.

Limits are 64 call frames and 256 overrides/paths per frame. SQLite RPG paths use
bounded live cursors; legacy custom providers can still use materialized input.
[Record-wait policies](record-waits.md) are captured at open and survive retained
calls. Query ODPs, full sharing compatibility, native commitment/open policy and
independent concurrency acceptance remain C08 requirements.

## Evidence

`JobEnvironmentTests` checks atomic LDA inheritance, exact sizes and boundaries,
group membership/cleanup, initialization, same-profile cross-job denial, scoped
resource cleanup, nested CL exception unwinding, actual RPG shared reads and explicit
close. `JobEnvironmentPersistenceTests` reopens an on-disk catalog, preserves a
queued child's environment, and removes the interrupted parent's private state.

### C11 storage completion evidence

The C11 checklist item requires the storage lifecycle below, using the routing
extension already recorded under completed C04. It does not establish native
prestart-pool dispatch or parameter packing.

| Requirement | Authoritative evidence |
| --- | --- |
| Automatic CHAR LDA creation, size and encoded blanks | Schema-16 trigger and seven independent CCSID fixtures in `JobDataAreaLifecycleTests` |
| Atomic batch inheritance, independent parent/child bytes | `JobEnvironmentTests`, queued catalog-reopen and compiled CL retrieval cases |
| Shared 512-byte GDA with separate groups and private LDAs | CL group calls, independent-process disjoint writes, terminal CHGGRPA/TFRGRPJOB/SysReq acceptance |
| Routing initialization and raw payload bounds | NULL/0/1/1999/2000/2001-byte cases; claim materialization clears pending bytes atomically |
| Failed routing leaves no partial claim/initialization | Trigger-injected rollback verifies queued state, attempt count, pending bytes and a subsequent valid claim |
| Job ownership and cancellation | Same-profile and administrator job contexts cannot select another job; a cancelled group member cannot change surviving shared data |
| Cleanup and restart | Normal/abnormal final-member cleanup, pending cancellation, interrupted-job recovery and queued-child persistence cases |
| No standalone data-area object or leaked read buffers | Object-catalog and detached-byte assertions plus exact endpoint/failed-range checks |

`JobDataAreaLifecycleTests` adds 27 cases to the existing environment and CL tests.
Its process fixture performs competing writes through separate SQLite connections
in separate OS processes. The full terminal scenario checks GDA sharing and LDA
isolation while preserving an edited panel across group/secondary transfers.

## Native job API views

[QUSRJOBI/QUSLJOB](job-apis.md) read committed job admission and environment data.
Basic identity is public; JOBI0700 library lists require own/same-user job or job
control authority, and JOBL0200 suppresses protected fields for unauthorized rows.
Library output uses logical QTEMP and excludes private backing names. Native job
IDs persist across ordinary opens and refresh only at exclusive server startup.
Concurrent authenticated socket jobs exercise private list creation, raw-ID
retrieval and distinct caller identities without sharing QTEMP objects.

## Cryptographic key contexts

[Native QC3 key contexts](cryptography-apis.md) belong to the job across program
returns and activation groups. Each executing job can retain up to 256 encrypted
keys. Other jobs cannot use/destroy its tokens, including jobs of the same profile
and JOBCTL callers. Context creation prepares outputs before committing; current
profile/job liveness is checked at service boundaries. Job end/recovery deletes
contexts transactionally. Independent socket jobs verify creation in one call,
use in a later call, foreign-token denial and signoff cleanup.
