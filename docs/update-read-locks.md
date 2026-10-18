# Retained update-read cursor locks

`SqliteFileStore.OpenUpdateCursor` supplies the retained input lock used by
[declared RPG update/delete files](rpg-disk-files.md). The RPG compiler now
connects noncommit update/delete usage, N input and UNLOCK to this contract.
Declared output/addition is implemented separately. This service contract does
not establish native RPG status handling or commitment isolation.
[File/override WAITRCD policies](record-waits.md) now control declared update reads.

IBM distinguishes inquiry input from update input. Update input locks the
record; inquiry input can read a record held for update. The lock is released
by a later input, positioning, update/delete or UNLOCK, while adding another
record does not release it. Independent programs in one job can contend too.
See [IBM record locking](https://www.ibm.com/docs/en/i/7.4.0?topic=gfc-record-locking).

## API and lifetime

```csharp
using var identity = OperationIdentity.Enter(user, job.Key);
using var cursor = files.OpenUpdateCursor("QGPL", "DATA", "DATA",
    keyed: true, lockWait: TimeSpan.FromSeconds(5), cancellationToken: cancellation);
if (cursor.Chain(key))
{
    // This cursor owns the selected physical record until a release boundary.
    cursor.Update(changedFields);
}
```

The API requires an active executing job, read/update authority and a compatible
file allocation. Explicit waits accept 0–32767 seconds or
`Timeout.InfiniteTimeSpan`; the service default is zero. Declared RPG opens use
the file/override WAITRCD policy. The
ordinary OpenSequentialCursor contract and signatures remain available.

An update cursor holds one physical member/RRN lease, including when accessed
through a logical projection, a duplicate key or an explicit RRN. Its file and
parent allocations survive command boundaries. The record lease uses the
existing exclusive-read coordination mode: inquiry reads remain compatible;
other update reads and cooperating record writers contend. A private cursor
scope distinguishes independent cursors in the same job. Only that cursor's
current-record mutation can bypass its own read lease. An owner can upgrade to
write while another reader waits; the queued request cannot prevent the owner
from completing the write that will release its lock.

READ/READP/READE/READPE, CHAIN, SETLL/SETGT and rewind release the prior update
input before attempting the next operation. `HasUpdateLock` reports whether the
retained coordination lease still exists. `Unlock()` preserves input values and
position but removes mutation eligibility. Read/CHAIN overloads with
`lockRecord: false` release the preceding lease and retrieve inquiry input;
UPDATE/current DELETE then require a fresh locked input. Refresh preserves an
existing locked target and cannot upgrade inquiry input into an update target.

Successful UPDATE/current DELETE consume the input target and release its lease.
Validation or trigger failure retains a locked current target for retry. Keyed
or RRN DELETE obtains its own scoped selection lease and releases it after the
operation. Adding a different record leaves the input lease intact. Bound
queries do not retain an input record lock.

Selection is repeated after acquiring a record lock. A reader waiting behind
an update therefore receives the fresh image, and a changed search candidate is
reselected. Authority, cancellation and format checks run again after the wait.
A timed-out forward/reverse sequential input saves the blocked sort position
with an inclusive retry; inserting earlier records does not redirect the retry.
A failed locked CHAIN requires explicit repositioning. IBM describes the locked
record retry rule for [READE](https://www.ibm.com/docs/en/i/7.6.0?topic=codes-reade-read-equal-key)
and [READPE](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-readpe-read-prior-equal).
Declared RPG now maps record-lock contention to [status 1218](rpg-file-status.md);
full access-path/golden acceptance remains unfinished;
this API currently reports the existing CPF1002/CPF1003 coordination errors.

Cancellation removes queued requests. Rebinding call cancellation preserves a
completed input lease and job ownership. Cursor close releases its allocations;
job end removes the persisted leases. The owning factory tracks these cursors,
cancels their pending input when closing and disposes them before shutting down
its lock catalog. Close and input share a cursor gate so shutdown cannot leave a
newly acquired lease behind. Reopened factories see the same persisted scopes.
The existing 8192-holder job lock budget also applies.

## Commitment and writer integration still required

This mode explicitly rejects an enlisted record workspace, including late
enlistment. Commitment read-lock levels, boundary release/retention and native
COMMIT-open behavior need their own policy; the noncommit release rules must not
be silently applied to commitment files. Existing ordinary cursors keep their
service commit/rollback checkpoints. See [record transactions](record-transactions.md).

Point cursor mutations and record-lock-aware file writers honor these leases.
Job-owned direct record INSERT/UPDATE/DELETE create a bounded operation scope when no
command scope exists; record leases survive through commit/rollback and are then
released. Clear, replace-copy and source-save entry points also establish that
scope before their object/record checks, preventing a direct caller from bypassing
the retained read. Existing enclosing command scopes remain in effect. Complete coordination of arbitrary SQL and trigger/FK effects remains
required. Trusted jobless writes and raw SQL can still bypass the file-lock service; this API does
not claim that all database writers are covered. Per-cursor same-job waits are
bounded, while the existing job-level deadlock graph continues detecting
cross-job cycles; it does not infer intrajob cursor cycles. Full fairness,
Complete native file statuses, further extenders, native commitment modes, WAITFILE
and independent native acceptance remain open. WAITRCD attributes/overrides now
control retained input and point mutation waits. Declared noncommit
update/delete files, N input and file UNLOCK now use these leases. Declared
[output/addition](rpg-disk-files.md#output-and-record-addition) uses separate writer
handles; WRITE preserves an existing input lease and does not create an update target.

`CursorUpdateReadTests`, `CursorUpdateReadContentionTests` and
`CursorUpdateReadTransactionTests` cover same/other-job contention, inquiry,
release boundaries, stale-target rejection, fresh images after waits, blocked
position retry, cancellation, authority, logical/RRN identity, trigger retry,
job/factory cleanup, reopen and explicit commitment rejection. C08/C09/C11 remain
unchecked.

Declared RPG E operations, numeric status and MONITOR now handle classified
record contention without changing input fields. See [status handling](rpg-file-status.md)
for precedence, lifetime, mappings and remaining exception contracts.
