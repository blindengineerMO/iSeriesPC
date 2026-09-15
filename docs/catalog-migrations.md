# Catalog migrations and recovery

Schema 3 introduced the runner that replaces unconditional schema stamping with ordered, transactional
migrations. `sys_meta.schema_version` and `sys_migrations` are updated in the same
transaction as the schema changes. The history records version, name, SQL SHA-256,
timestamp, and whether a version was adopted from a legacy catalog.

Versions 1 and 2 are preserved baselines. Version 1 contains the original system,
object, security, job, and menu tables; version 2 adds file definitions and members.
Legacy catalogs did not record migration history, so the runner labels their old
versions `baseline=1`; it does not claim to have rerun them. Fresh installs execute
all versions. Schema 4 adds durable batch commands and attempt counts in
`sys_job_execution`. Schema 5 adds type-qualified object dependencies with cascading
rename and restricted target deletion. Schema 6 synchronizes profile metadata with
QSYS/*USRPRF descriptors without copying credentials. Schema 7 gives subsystem and
job-queue payloads library-qualified keys and catalog identities. Schema 8 backfills
authorization-list descriptors from existing memberships/attachments. Schema 9 adds the
transactional audit/domain outbox, consumer cursors and log identity fields; see
[logging-events.md](logging-events.md). Schema 10 adds unique enterprise identity
mappings, directory validation state and transactional identity audit events. Schema 11
adds encrypted MFA enrollment, recovery-code hashes, revocable session-token hashes,
job/session correlation and credential/directory/factor revocation triggers; see
[MFA and sessions](mfa-sessions.md). Schema 12 adds certificate versions, explicit purpose
trust and bindings, object-signature policies, integrity results, verified code artifacts
and signed service publication; see [certificates and signatures](certificates-signatures.md).
Schema 13 adds queue ownership/limits/hold state, JOBD/CLS payloads and typed library
dependencies, routing comparisons and persisted admission snapshots. Schema 14 adds
unique job numbers, durable execution outcomes and host/claim/timing metadata. Schema 15
adds cancellation controls, thread/native process accounting, activation-group inspection
and typed queue bindings that become historical names at completion; see
[batch work](batch-work.md). Job and command
submission use one transaction. Schemas 16/17 add job data areas and terminal families.
Schema 18 upgrades maintained nullable key indexes: nulls sort above non-null values,
and UNIQUE includes null duplicates by default. Its frozen data-upgrade handler runs
inside the same transaction as the version/history update; the checksum covers its
versioned SQL contract. It preserves index names, predicates and ownership, including
managed indexes copied with members. Existing conflicting null duplicates abort the
upgrade with the affected member named; reconcile records using the prior build and
retry. The pre-upgrade backup remains available. No duplicate is silently deleted.
Schema 20 adds job-owned program/call message queues and inquiry sender copies.
The message-table rebuild preserves data, routes, receive/reply state and the
allocated key high-water mark even for an empty queue. Program return/job end
closes delivery while retained frame messages remain visible in job logs.

Schema 21 adds exception-handled state and reply/default-origin codes for RCVMSG.
Existing replies retain code 21 (origin not previously recorded); nonempty stored
default data maps to message-default code 23, and empty data maps to system-default
code 24. The migration does not change message bytes or reference keys.

Schema 19 adds durable named message entries, bounded byte payloads, monotonic
four-byte keys, receive/reply state and rename-aware reply destinations. See
[message queues](message-queues.md). Existing named queues, including QSYSOPR,
become usable without rebuilding their object descriptors.
Existing migration SQL and versioned data-upgrade handlers must not be edited: add the next numbered
migration and advance `SystemCatalog.SchemaVersion` together.

An immediate SQLite transaction obtains the write reservation before reading the
version. Concurrent migrators wait and then reread the committed version. Unsupported
future versions, invalid/missing version stamps, unversioned nonempty databases,
and missing/inconsistent modern history fail without rewriting catalog metadata.
This uses [Microsoft.Data.Sqlite transactions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions).

Before upgrading an existing disk catalog, the runner creates a SQLite online
backup under `migration-backups/` beside the database. A separate WAL reader copies
the pre-migration snapshot while the migration transaction blocks writers. The
backup filename includes source filename, old version, timestamp, and a unique ID.
It is published only after copying succeeds. Backup failure aborts the upgrade.
The directory is owner-only and files are owner read/write on Unix. No passwords,
keys, or catalog rows are printed. In-memory test databases do not create backups.
The implementation uses [BackupDatabase](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup),
not a filesystem copy of a live WAL database.

## Operator workflow

Stop application writers and run with the account that owns the catalog:

```sh
as400menu --migrate-only --data-dir /var/lib/ipcsys
as400menu --migrate-only --data-dir /var/lib/ipcsys --database system.db
```

The command prints the resulting schema version and the pre-upgrade backup path
when one was created. It exits 0 on success, 2 for invalid command arguments, and
3 on migration failure. It does not start interactive sessions, seed profiles,
or launch workers. Ordinary app startup also invokes the same runner.

A failed SQL migration rolls back its DDL, data, version, and history together.
Retain the backup and failure output; correct the migration or run the previous
compatible build. On process interruption SQLite rolls back the uncommitted
transaction on reopening. The next start reads the committed version again.

For explicit restoration, stop every process using the catalog, retain the failed
catalog and its WAL/SHM files for investigation, and restore the chosen backup into
an empty data directory under the configured database filename. Run the matching
old application or `--migrate-only` with the new version and check application data.
Never replace an open database or leave stale WAL/SHM files beside a restored one.
Do not automatically delete migration backups; retention is an operator decision
until the C18 backup manager is implemented.

This backup protects the SQLite catalog and member tables only. It is not the
full-system save/restore feature: external IFS, spool, configuration, and keys will
require the coordinated C13 snapshot contract.

## Verification

`MigrationTests` covers fresh/repeated startup, both legacy versions, preserved
data, backup restoration/re-upgrade, SQL-failure rollback, newer/invalid versions,
history tampering, backup failure, competing connections, and independent console
processes upgrading the same catalog. `HostLifecycleTests` covers startup
idempotency, failure cleanup, readiness, independent memory catalogs, and disposal.

Connection factories quote database paths with the provider's connection-string
builder, enable foreign keys, and release their own keepalive/pool on disposal.
`IpcSystem` no longer writes disk logs for in-memory catalogs. Readiness here means
the embedded system initialized; the shared background server reports readiness
after ownership, migration, recovery, listener and dispatcher initialization.
