# Durable history, audit and domain events

SQLite is the authoritative history, job-log and event store. Schema 9 adds history
kind/principal fields, job-log principal fields, `sys_events`, and persistent consumer
cursors. Catalog triggers write object mutations, job creation/status/message events,
profile changes, authority/list membership changes and system-value changes in the
same transaction as their data. Rollback removes both the mutation and its events.
Authentication results, command start/finish receipts and system startup are explicitly
appended by their owning runtime services. Authentication/profile/authority events
use the `security.` namespace and the longer audit retention policy.

`OperationIdentity` carries the server-established principal and optional job across
async calls. Session and batch execution establish this context; pooled SQLite
connections read it at statement execution, without retaining a previous caller's
identity. Event job keys use the same `number/name/user` representation as `JobKey`.
System maintenance uses `*SYSTEM`; rejected sign-on attempts use `*UNAUTHENTICATED`
and record the attempted profile separately. Job state events include the job's
profile as payload even when a system actor performs recovery.

Passwords, password hashes, raw command text, command parameters and object source
are not included in generated audit events. Command receipts use a random request ID
and outcome. A start without a finish after a crash means the outcome needs review;
it does not imply the command was rolled back. Application-supplied history/job-log
text is retained as supplied. Access control for those records belongs to C03/C16;
audit durability alone does not establish the full security boundary.

## Delivery and restart

`IpcSystem.DurableEvents` exposes `Read`, `Acknowledge` and `DeliverAsync`. Each named
consumer has an independent persisted sequence. Reading does not acknowledge; a
failed callback or crash before acknowledgement replays the batch. `DeliverAsync`
acknowledges only after every callback succeeds and cancellation is checked. Delivery
is at least once: consumers must make effects idempotent using the event sequence.
There is no exactly-once claim for external effects. An immutable batch belongs to
its originating catalog; stale concurrent acknowledgements fail rather than silently
advancing a cursor. Use one worker per consumer name for ordered application effects.

The typed `IEventBus` remains a process-local notification utility, without restart
semantics. Durable work must consume the persisted outbox. Future spool/message and
other domain implementations must append their events within their own transactions;
those domains are still tracked in their implementation checklist items.

## Rotation and retention

`system.json` accepts a `Logging` object with `MaximumFileBytes` (default 10485760),
`MaximumFiles` (10), `HistoryDays` (30) and `AuditDays` (90). Audit retention must be
at least history retention. The active mirror is `logs/ipcsys.log`; size overflow
archives it to a unique timestamped file. The file count includes the active file.
Mirrors contain one JSON record per line, escaping embedded newlines. Oversized
mirror messages are truncated to fit; the authoritative SQLite history retains the
complete text. New log files are mode 0600 and new log directories mode 0700 on Unix.

Maintenance runs at startup and hourly. It removes history older than `HistoryDays`
and logs for completed/ended jobs older than that interval, preserving active-job
logs. Events expire after their namespace's retention interval only when every
registered consumer has acknowledged them. An abandoned consumer therefore prevents
pruning; remove it explicitly with `RemoveConsumer` after deciding its pending work
is no longer required. A new consumer starts at the earliest retained event, not an
unbounded historical archive. No automatic cap discards unacknowledged events.

SQLite logging errors propagate. File mirror failures leave the authoritative history
intact and set `HostLog.LastMirrorError`; hourly maintenance failures are exposed as
`IpcSystem.MaintenanceError`. Neither is silently treated as successful mirroring.
Terminal/HTTP health presentation of these diagnostics remains C16/C22 operator UX.
Direct database maintenance that writes trigger-backed tables must use the application
connection factory, which installs the identity functions used by schema 9 triggers.

Evidence: `DurableLoggingTests`, `SessionServerTests`, `BatchQueueTests`, and
`MigrationTests` cover restart/replay, independent/stale cursors, callback failure,
transaction rollback, concurrent identity isolation, retention, bounded mirror
rotation, mirror/database failure and real authenticated interactive/batch correlation.
