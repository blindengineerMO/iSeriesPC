# Runtime architecture

`as400server` owns one disk-backed catalog and accepts terminal sessions over an
owner-only Unix socket. `as400menu` is now a client by default. It forwards decoded
key events and renders server-produced display frames; it does not initialize a
second system or run a second copy of the session controller.

```text
SSH / local PTY → as400menu → Unix socket → as400server
                                              ├─ Ipc.Session: sign-on/menu/commands per session
                                              ├─ Ipc.Services: shared catalog/security/jobs
                                              └─ Ipc.Cl / Ipc.Rpg / Ipc.Db: execution + data
```

`Ipc.Session` contains the former console controllers and command orchestration.
Their `Ipc.Console.Session` namespaces remain stable for existing callers. The
console owns terminal I/O only. `Ipc.Server` supplies the background executable;
`SessionServer` hosts the shared services and isolated session controllers.

## Ownership and lifecycle

The server holds exclusive OS file handles on `<catalog>.host.lock` and
`<socket>.lock`. A competing server fails without deleting the running server's
socket. The owner may remove a stale socket after a crash. The default endpoint is
`<data-dir>/run/as400.sock`; its directory is private when created and the socket
mode is 0600. An existing endpoint directory writable by other accounts is rejected.
Do not grant multiple processes independent ownership through path aliases.

After acquiring ownership the server performs catalog migrations, initializes the
system, marks leftover active interactive jobs as abnormally completed, and then
binds/listens. The readiness line is emitted only after these steps succeed.
SIGINT/SIGTERM stops accepting sessions, cancels pending network I/O, waits for
session cleanup, removes the socket, disposes the system, and releases ownership.
Kernel ownership locks release on process death; persisted job state enables the
next owner to record interrupted sessions rather than replay their commands.

Each connection begins with its own sign-on controller. Successful authentication
creates an interactive job and menu controller. `DSPJOB` reads that controller's
job rather than selecting the newest job globally. A persisted atomic counter
allocates job numbers; deleting old job rows no longer causes number reuse.
Signoff/disconnect disposes the menu and completes its job. Hidden display fields
are redacted from outgoing frames. Authentication policy is described in the
[security model](security-model.md).

Protocol version 1 is length-prefixed UTF-8 JSON with a 256 KiB frame limit, typed
key events, and full display snapshots (text, attributes, colors, cursor, end/state).
The server validates protocol version and enum values and caps concurrent sessions
at 64. A malformed or disconnected client does not end other sessions. Socket
filesystem permissions protect the local transport; it is not a remotely exposed
plaintext authentication endpoint.

## Commands for development

```sh
dotnet run --project src/Ipc.Server -c Release -- --data-dir /path/to/instance
dotnet run --project src/Ipc.Console -c Release -- --data-dir /path/to/instance
dotnet run --project src/Ipc.Console -c Release -- --server /path/to/instance/run/as400.sock
```

Use the same OS account for these commands. `--standalone` is an explicit embedded
development mode, not the multi-session deployment path. `--migrate-only` operates
on the catalog without launching a session; see [migration recovery](catalog-migrations.md).
PTY setup reads and saves actual `/dev/tty` settings, enters raw/no-echo mode, and
restores the saved settings when the session loop exits.

## Remaining architectural work

The shared host now serves terminal, headless API, HTTP and batch execution as
described below. Object persistence, security and concurrency work remains in C02–C04.
CL/RPG loops now check server cancellation at instruction boundaries. Blocking
external/DB operations and per-job cancellation still require bounded execution.
Job-owned
locks, transaction handles, activation groups, durable event delivery, external
executor isolation, and full scheduler routing/recovery policies remain open.

Multi-account SSH socket access must be provisioned through a restricted service
group or broker with PAM/profile identity mapping. Do not expose this development
host to other accounts before completing cross-entry-point authority enforcement.
Full-system backup, authenticated remote transport, installer/systemd units, and
websocket integration remain separate completion tasks.

`SessionServerTests` exercises authenticated concurrent sessions, job isolation,
failed sign-on, oversized frames, duplicate ownership, stop/restart, killed-process
recovery, and hidden-field redaction. `tools/pty-smoke.py` checks the actual Linux
PTY, F3 without newline, and exact terminal restoration.

## Shared execution boundary (C02)

`as400web` now forwards `POST /api/commands` to `as400server` with `CommandConnection`.
It does not create an `IpcSystem`, open SQLite, or start workers. The typed command
protocol authenticates once per connection; subsequent requests cannot replace the
profile or job. HTTP calls currently create one short-lived server session per
request. Terminal and headless execution both use `ExecutionSession`, which owns
the command interpreter and rechecks disabled/expired profiles before each command.
The HTTP bridge has a 64 KiB body limit and 30-second timeout. Non-loopback requests
require HTTPS. Loopback HTTP is for local development; full SSO and operation-wide
authority remain C03 requirements. Gateway liveness is not server readiness.

`SBMJOB CMD(...) JOB(...) JOBQ(...)` persists the command and queued job atomically.
The server creates one `BatchDispatcher` against its existing `IpcSystem` after
recovery and before reporting ready. The dispatcher executes CL/RPG/commands in
`ExecutionSession` under the submitted job/profile. Queue claims use an immediate
SQLite transaction, priority/FIFO ordering, and subsystem capacity. Jobs and results
survive reconnect; job-log sequence allocation is atomic. Active jobs found during
server recovery are completed abnormally with uncertain-effect status; there is no
automatic replay. Per-queue routing, JOBD/CLS, external execution, full per-job
cancellation and lock/transaction semantics remain C04/C08 requirements.

| Resource | Owner and lifetime |
|---|---|
| Service facade, catalog, migration lock | One `as400server` per catalog; disposed after all workers/sessions |
| Scheduler and batch workers | One dispatcher created by that server; clients cannot start it |
| Authentication, profile lookup, events | The same `IpcSystem` service instances across entry points |
| Interactive/API job and interpreter | One `ExecutionSession` per authenticated connection |
| Batch job and interpreter | One `ExecutionSession` per claimed durable request |
| Job-owned locks | SQLite coordination sidecar keyed by persisted job identity; see [locks.md](locks.md) |
| Transaction/ODP handles | Server-side execution context, never a client-provided SQLite connection; implementation remains C08 |
| HTTP transport | `as400web`; forwards cancellation/identity over the protected Unix socket |

A socket disconnect cancels CL/RPG execution at instruction boundaries. Server
shutdown cancels both clients and batch workers and waits before releasing the
catalog. Tests include real HTTP-to-server command execution, terminal/headless
catalog sharing, batch CL file effects, concurrent independent queue claims,
capacity/hold/stop behavior, and non-replaying recovery. The system API ABI (C14)
will call this same dispatcher; the HTTP bridge is not an implementation of those
IBM API receiver formats.

```sh
dotnet run --project src/Ipc.Web -c Release -- --server /path/to/instance/run/as400.sock
```

Use Basic credentials on local development HTTP or configured Kestrel HTTPS.
`POST /api/commands` accepts `{ "command": "DSPJOB" }` and returns a typed result
with the actual server job key. Invalid authentication returns 401; unsupported
commands return 501; unsupported parameters return 400; unavailable server returns
503. Browser terminal transport, admin UI and IWS deployment remain C16 work.
