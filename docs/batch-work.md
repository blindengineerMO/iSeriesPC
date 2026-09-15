# Batch execution and work definitions

`as400server` owns one continuous dispatcher with up to 64 concurrent workers. Terminal
and HTTP/headless `SBMJOB` requests persist a command and a separate batch job. A worker
claims the job, resolves its routing class and program, executes it with the job's profile
and libraries, and records output and completion. CL, RPG and external `*PGM` objects
share the `CALL` path, including signature verification and adopted-authority scopes.

## Queues, routing and classes

Queue identity is library-qualified. A queue belongs to one subsystem at a time; a
subsystem may own several queues. This release uses explicit static ownership. Detach
before transferring a queue, and finish its running jobs before detaching. Jobs waiting
on a transferred queue follow its new subsystem. Queue sequence (1–9999), job priority
(0–9, lower first), submission time and job number determine admission order. Within
each queue and priority, jobs retain FIFO order. Both subsystem and queue capacity
must be available. Held queues and zero-capacity entries admit no work. Running,
message-waiting and started/held jobs consume capacity.

`CRTJOBQ`, `HLDJOBQ`, `RLSJOBQ`, `ADDJOBQE`, `CHGJOBQE`, `RMVJOBQE`, `CRTSBSD`,
`STRSBS` and `ENDSBS` manage this subset. `MAXACT(*NOMAX)` is bounded to 32000;
the host worker limit still applies. Creating a subsystem installs a final sequence
9999 `*ANY` route to `QSYS/QCMD`; starting one with no queue entries creates and
attaches a same-name queue (QUSRSYS for QSYS subsystems). Existing definitions survive
startup. Ending a subsystem stops admission; job termination is a separate lifecycle action.

Routing selects the first ascending-sequence match: full `*EQ`, a `*SECTION` starting at
the one-based comparison position, or `*ANY`. There is no unmatched-first-entry fallback.
Missing routes/classes fail the claimed job with a completion log. `QSYS/QCMD` executes
the submitted command. Other routing programs receive that complete command as their
first parameter. They decide how to handle it. `ADDRTGE`, `CHGRTGE`, `RMVRTGE` support
`SBSD`, `SEQNBR`, `CMPVAL(value [position])`, `PGM`, `CLS`, and the `CMPMODE` extension.

`CRTCLS`/`CHGCLS` persist `RUNPTY(1..99)` and `TIMESLICE(1..10000)` milliseconds;
`WRKCLS` lists definitions. Admission snapshots class values into the job. CL/RPG
interpreters yield cooperatively at instruction boundaries after each time slice,
with a 0–4 ms priority-dependent yield. These are application scheduling values;
they are not IBM storage-pool scheduling or Linux real-time thread priorities.
Native children use the host's ordinary Linux scheduler.

`CRTJOBD`/`CHGJOBD` persist `JOBQ`, `JOBPTY`, `USER`, `RTGDTA`, `INLLIBL` and the
`CURLIB` extension; `WRKJOBD` lists them. Explicit library lists are ordered, unique,
bounded to 250 and reference real library objects. `SBMJOB JOBD(...)` takes defaults
from the description and applies explicit `JOBQ`, `JOBPTY`, `USER`, `RTGDTA`, and
`INLLIBL` overrides. `*CURRENT`, `*SYSVAL`, `*JOBD` (submission only) and `*NONE`
have their documented list meanings. The seeded QGPL/QBATCH description inherits the
submitting job's libraries/profile and uses QUSRSYS/QBATCH. A created description
defaults to the system user library list. Run-as authority is checked before submission.
Jobs retain the submitter separately from their executing profile. Copy and rename
preserve JOBD/CLS payloads and update library/routing references; referenced objects
cannot be deleted.

These are bounded subsets of IBM's [JOBD parameters](https://www.ibm.com/docs/en/i/7.4.0?topic=ssw_ibm_i_74%2Fcl%2Fcrtjobd.html),
[multiple queue scheduling](https://www.ibm.com/docs/en/i/7.4.0?topic=jq-how-jobs-are-taken-from-multiple-job-queues),
and [routing entries](https://www.ibm.com/support/pages/node/642841).

## External program protocols

`CRTEXTPGM` is an iSeriesPC extension:

```text
CRTEXTPGM PGM(QGPL/REPORT) EXEC('/opt/dotnet/dotnet') ARGS('/opt/report/report.dll') DEPENDS('/opt/report/report.runtimeconfig.json' '/opt/report/report.deps.json') TIMEOUT(60)
CALL PGM(QGPL/REPORT) PARM('two words' 'a second value')
SBMJOB CMD(CALL PGM(QGPL/REPORT) PARM('nightly')) JOB(REPORT)
```

Registration requires the caller's non-adopted `*SERVICE` authority and normal object
creation authority. Direct creation/update and signed-package admission of an EXTERNAL
program enforce that special authority too. The program object contains a versioned
manifest. Executable/dependency paths are absolute and SHA-256 pinned. Existing absolute
file arguments are pinned automatically and resolved to their symlink targets. List all
additional application dependencies explicitly. Group/other-writable files are rejected;
maximum individual file size is 256 MiB. Host runtimes, their framework libraries and
OS shared libraries remain part of the trusted host installation. Register a new program
version after changing pinned content. Sign program objects for enforced package trust.

External programs are trusted native code under the daemon's Unix account, with its
filesystem/network access. They are not a profile sandbox. The child gets a private
temporary working directory, a cleared environment with `LANG=C.UTF-8` and private
`TMPDIR`, fixed registered arguments, and no shell interpolation. Job parameters never
become process flags or environment variables. The host writes one UTF-8 JSON document
plus newline to standard input and closes it:

```json
{"version":1,"parameters":["two words",12.25,true,null]}
```

The child writes exactly one JSON response to standard output, then exits:

```json
{"version":1,"success":true,"message":"report created","parameters":["two words",12.25,true,null]}
```

Parameters are strings, numbers, booleans or null; at most 256 scalars and 64 KiB input.
CL passes character values. RPG supplies scalar values and accepts returned parameters
through its external-call writeback path. Returned parameter count must match the call;
omitting `parameters` preserves input values. Response version and `success` are required;
unknown/duplicate response fields are rejected. Nonzero process exit, `success:false`,
invalid JSON, timeout or excessive output fails the call. Standard output/error are drained
concurrently, each limited to 1 MiB. Returned message and stderr are control-character
filtered and capped at 8192 characters for job output. Programs must not print secrets.

`PROTOCOL(1)` is the registration default and keeps the scalar wire format above.
Register `PROTOCOL(2)` for byte-preserving command list/qualified arguments. Version 2
uses the same request/response envelope with `"version":2`; a buffer parameter is
exactly `{"type":"buffer","ccsid":1208,"data":"AAL//gD/"}` (base64 bytes).
This example contains a two-element signed-INT2 list, -2 and 255. Buffer bytes are
never decoded through UTF-8. Unknown/duplicate buffer properties, invalid base64,
and unsupported CCSIDs fail the response. Returned buffers become immutable
`ProgramBuffer` values. Counts, input/output limits, cancellation, and scalar types
are identical to version 1. Version 1 can receive a command buffer only when it
round-trips exactly through its declared CCSID; otherwise the call fails before
process launch. Interpreted CL/RPG byte-addressed storage remains tracked in C07/C09.

Timeout is 1–3600 seconds, default 60. Cancellation/timeout kills the active process tree
and waits for its exit; temporary files are removed. Native code must keep children under
its lifetime and must not daemonize. Process-tree termination is not containment against
deliberately escaping trusted code. Already-completed external effects cannot be undone.

`tests/Ipc.NativeFixture` is an independently launched C# console executable exercising
this ABI. `ExternalProgramTests`, `SchedulerDefinitionTests`, `BatchQueueTests`, and
`SessionServerTests` cover native failures/termination, tampered dependencies, typed
parameters, CL/RPG batch side effects, custom routing/unmatched failure, FIFO/capacity,
queue ownership and JOBD/CLS defaults and object lifecycle.

## Durable execution outcomes and retry

Schema 14 records `Queued`, `Running`, `Succeeded`, `Failed`, `Cancelled` or
`Interrupted` separately from the job's work status. A held, unstarted request is still
queued. Each batch claim saves a unique claim token, host identifier, timestamp and
attempt count. Completion saves its timestamp and job-log entry in the same transaction;
the first terminal outcome wins. A late worker cannot replace an earlier completion.
`BatchQueue.Inspect` returns this metadata subject to job authority.

Job-number allocation is a single SQLite write, with a unique number constraint.
Numbers are never recycled, including numbers consumed by failed submissions. Invalid
sequence metadata or 32-bit exhaustion fails submission. Job, command and submission log
are inserted together. Claiming uses an immediate write transaction that checks both
capacities and routing before changing state. It admits only an unclaimed queued request.
The legacy state-only `StartNext` path uses the same admission transaction but never
claims durable executable requests.

At server startup, while holding catalog ownership and before admitting work, recovery
marks unfinished running work interrupted. It preserves queued work, prior terminal
outcomes, the original claim identity and attempt count. Server shutdown records native
batch cancellation and terminates its active child process. A crash after an effect but
before completion is **uncertain**, even if the effect appears to have succeeded.

There is no automatic retry of a claimed job. Operators must inspect the job log and
external systems, reconcile partial effects, and submit a new job only when safe.
The new submission receives a new job number and claim identity. Integrations requiring
retry should carry an application idempotency key and deduplicate in the destination;
neither a SQLite transaction nor a job completion record makes an external effect
exactly-once. Queued work that was never claimed can run after restart.

`ExecutionRecoveryTests` launches four independent .NET processes to allocate 80 unique
jobs and claim all 80 once. It kills a real claimant after writing an effect, reopens the
catalog, verifies interrupted recovery without replay, races completions, injects log
failures to check transaction rollback, and checks sequence corruption/exhaustion.
## Job lifecycle, controls and accounting

Schema 15 adds durable cancellation requests, accounting, activation-group inspection
and typed output/message-queue bindings. Terminal jobs are interactive in QINTER;
authenticated headless/API jobs are communication jobs in QSERVER. `SystemWork.RunAsync`
provides the shared execution lifecycle for host adapters and scheduled system work in
QSYS. System work requires the caller's non-adopted `*JOBCTL` authority; its executing
profile still needs authority to every operation. Trusted host callers default to QSYSOPR.
QSYSOPR does not receive extra object authority merely because work is a system job.
Attached jobs check subsystem state/capacity atomically and snapshot their class values.

`ENDJOB JOB(number/name/user) OPTION(*IMMED)` records an authorized cancellation request.
Unstarted jobs complete cancelled immediately. Live runtime handles poll durable controls
every 100 ms; cancellation reaches CL/RPG instruction boundaries, native processes, and
idle network reads. `ENDSBS` stops admission and requests cancellation of its running jobs
in one transaction. `HLDJOB`/`RLSJOB` currently hold/release waiting jobs. Controlled-end
exit hooks and pausing running interpreters are outside this command subset. If cancellation
commits before completion, cancellation wins even when the command finishes before the next
poll. Signoff, disconnect, command errors, subsystem end, shutdown and crash recovery all
use the shared terminal-state cleanup. A submitted job's user component identifies the
executing profile; the original submitter remains separately recorded.

`WRKACTJOB` reports visible running jobs; `WRKJOB JOB(...)` includes thread, process,
activation-group and queue details. `CHGJOB JOB(...) OUTQ(...) MSGQ(...)` changes typed
queue bindings with job and queue authority checks. Defaults are QUSRSYS/QPRINT and
QSYS/QSYSOPR. Active bindings follow queue rename and prevent deletion. Completion retains
the last queue names as historical text and releases the live references. Spool/message
delivery behavior is implemented by the owning C10/C11 services, not by these bindings.

One job executes one command at a time. Managed thread accounting uses the actual Linux
thread CPU clock, obtained with
[`pthread_getcpuclockid`](https://www.man7.org/linux/man-pages/man3/pthread_getcpuclockid.3.html),
and monotonic elapsed execution time. Idle session time is not charged as command execution;
job submission/start/completion timestamps remain available for lifetime elapsed time.
The host samples live CPU/thread status every 100 ms and takes a final command sample.
Each native invocation records PID, sampled CPU, peak observed thread count, state and exit
code. Native samples can undercount short-lived activity when the OS removes accounting
before the final read; they are labeled as samples in the work views. A crash preserves the
latest sample, marks interrupted work, and clears live thread/group state. Accounting and
control-store failures cancel active execution rather than continuing without controls.

## Activation groups

`CRTCLPGM` and `CRTBNDRPG` accept `ACTGRP(*CALLER|*NEW|name)`; the same setting is stored
as `ipc.program.activationGroup` in program descriptors and is covered by object signatures.
The default is `*CALLER`. A caller group is inherited; a top-level caller uses *DFTACTGRP.
Named groups retain RPG static storage within that job. `*NEW` creates a fresh group and
releases it on return, including error unwinding. RPG LR resets the program's retained
storage; changed source gets fresh storage. Recursive RPG activation in a retained group
is rejected; use `*NEW`. The shared program call stack is bounded to 64 frames and a job
may retain at most 256 groups.

`RCLACTGRP ACTGRP(name|*ELIGIBLE)` releases inactive named groups. It rejects an active or
default group. Job end releases every group. CL variables remain automatic call storage;
native processes retain their per-invocation lifetime. Group ownership also provides the
resource-disposal boundary for subsequent ODP/commitment/UIM integrations. This implements
the documented [named-group storage lifetime](https://www.ibm.com/docs/en/i/7.6.0?topic=group-running-program-in-named-activation)
and [caller/new group choices](https://www.ibm.com/docs/en/i/7.4.0?topic=mag-specifying-activation-group)
for the current CL/RPG runtime; it is not an IBM ILE binary loader.

`JobLifecycleTests` and `ActivationGroupTests` cover controls during real execution,
CPU/thread observations, system work, authorization, cancellation/completion ordering,
queue-reference cleanup, storage isolation/reclamation and exception unwinding. Server
tests cover idle communication-job cancellation, disconnect and signoff. Native fixtures
also prove descendant-process termination and process-accounting capture.
