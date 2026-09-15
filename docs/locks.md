# Job-owned locks

The shared runtime coordinates typed object and member-record allocations in
`<catalog>.locks`, a private SQLite WAL sidecar. Independent processes using the
same catalog see the same holders and waiters. Its schema is version 1; it is
runtime state, excluded from application backups and never restored as user data.
The current catalog schema is 16; lock coordination has its own version.

Locks belong to the persisted job number/name/user, not a thread, connection,
activation group, browser token or Unix PID. A session cannot allocate or release
another job's locks, including another job running under the same profile. A
trusted host with no caller identity can administer runtime services; native code
running as the server's Unix account is inside that trust boundary.

## Compatibility

For different jobs allocating the same typed object, `yes` means compatible:

| Held / requested | `*EXCL` | `*SHRUPD` | `*SHRRD` | `*SHRNUP` |
| --- | --- | --- | --- | --- |
| `*EXCL` | no | no | no | no |
| `*SHRUPD` | no | yes | yes | no |
| `*SHRRD` | no | yes | yes | yes |
| `*SHRNUP` | no | no | yes | yes |

This four-state subset follows IBM's
[lock enforcement matrix](https://www.ibm.com/support/pages/lock-enforcement-rules).
`*EXCLRD` and other IBM allocation options are outside this command subset.
Library/name/type are all part of the identity: `QGPL/DATA *FILE` is distinct
from `QGPL/DATA *PGM`. Record identity adds member and the member table's positive
SQLite row number. Different rows do not conflict with each other.

An exclusive/update record allocation contributes shared-update intent against
object allocations; a read record contributes shared-read intent. Therefore an
exclusive object allocation blocks all record access, and `*SHRNUP` prevents
record updates. Each explicit child allocation also holds shared-read access to
its library. Library locks protect the library identity, not every row inside it.

The same job may reacquire compatible or stronger access; other jobs' holders
still participate in the check. Existing access can be reused without waiting
behind a later exclusive waiter. An upgrade that conflicts with another holder
can wait and participate in deadlock detection.

## Commands and waiting

```
ALCOBJ OBJ((QGPL/DATA *FILE *EXCL)) WAIT(10)
WRKOBJLCK OBJ(QGPL/DATA) OBJTYPE(*FILE)
DLCOBJ OBJ((QGPL/DATA *FILE *EXCL))
```

Object resolution uses the executing job's library list/current library.
`WRKOBJLCK` lists job identity, state, mode, lifetime, and member/row where
applicable. Inspection checks authority without taking a competing read lock.
There is no cross-job force-unlock command; use normal job termination.

`ALCOBJ` accepts 1–64 tuples and a total wait budget of 0–300 seconds, default 0.
Failure releases allocations acquired by that invocation. Repeated calls create
separate allocation tokens; `DLCOBJ` releases all of this job's explicit tokens
matching each requested object/mode. Multi-object deallocation proceeds in tuple
order; a failure leaves earlier deallocations applied.

Conflicting older waiters have priority. The coordinator polls every 20 ms and
records a wait-for graph. A cycle rejects its newest request with `CPF1003`;
timeout/fail-fast conflict returns `CPF1002`. Cancellation removes the waiter and
its partial parent allocation. Host scheduling and SQLite contention can add
latency beyond the requested wait interval; this is not a real-time scheduler.
A live job has at most 8,192 allocation tokens, including parent and automatic
allocations. Nonexistent explicit objects, members and records are rejected.

## Enforcement and release

| Kind | Acquisition | Release |
| --- | --- | --- |
| Explicit object/record | `JobLockStore.Acquire`; `ALCOBJ` for objects | allocation handle disposal, matching `DLCOBJ`, or durable job end |
| Automatic object | authorized shared command/menu access: read/use = `*SHRRD`, data mutation = `*SHRUPD`, structural mutation = `*EXCL` | outer command/menu-load scope, including exception/cancellation |
| Automatic record | `SqliteFileStore` returned rows = shared-read; inserted/updated/deleted rows = exclusive | outer command scope |
| Commitment | `JobLockTransaction.Acquire` | successful actual SQLite commit, rollback, or disposal after rollback |
| Open path | RPG file open | explicit close, owning call-frame return, or job end; see [job-environment.md](job-environment.md) |
| Parent library | explicit child allocation | child allocation release or job end |

File insert/update/delete acquire row allocations before committing changes.
Clear-member requires exclusive object access. Failed record checks roll back the
mutation. Structural delete/rename/move rejects any persistent allocation, even
one owned by the requesting job: release those allocations first. This avoids
attempting to atomically rename runtime locks across two SQLite databases.

Automatic and commitment acquisitions fail immediately on conflict. They can run
inside a data transaction, where waiting could deadlock SQLite's writer against
a different job's allocation. Explicit `ALCOBJ` waits occur outside data writes.
The commitment owner must perform commit/rollback itself; completing its underlying
transaction separately is unsupported. Failed commit retains allocations until
rollback/disposal. C08 will connect this transaction boundary to the language-level
commitment commands and shared CL/RPG/SQL file unit of work. Current automatic
file operations commit individually.

Enforcement applies to the existing shared command service's authorized object
boundaries and file store. Future SQL, ODP, messaging and other domain operations
must use the same service boundary; raw host SQLite/file access does not acquire
application locks. Locks do not implement IBM's full database isolation model.

A cancellation request does not release a running job's locks. They are released
after execution stops and its terminal outcome commits. Process death alone also
does not release persistent allocations: the owning host first records interrupted
jobs, then removes allocations for ended/missing jobs before admitting work.
There is no lease expiry that could silently unlock a still-running job. Recovery
can safely repeat after a crash between catalog completion and sidecar cleanup.

## Evidence

`JobLockTests` covers all 16 compatibility combinations, typed identities,
cross-job ownership, real file reads/updates/deletes, command inspection,
multi-allocation failure cleanup, timeout, cancellation, deadlock resolution,
reentrant access, job end, and real SQLite commit/rollback/disposal.
`JobLockProcessTests` uses separate .NET processes for contention/wakeup and kills
an actual holder, proving allocations survive until recovery and a replacement
process can acquire afterward. MFA entry-point tests also verify that revoked
sessions still receive their explicit authentication failure before lock setup.
