# System API checkpoint

The first executable catalog API is QSYS/QCMDEXC. The broader C14 registry,
message/user-space/data-queue/work/security APIs and format contracts remain open.
QCMDEXC is a versioned `*PGM` object with attribute IPCAPI. Normal library lookup,
object use authority, signature validation and program scope apply. Startup seeds
it only when absent; it never overwrites an existing or signed program. Relocated
or changed adapter manifests fail validation. Programs named QCMDEXC in other
libraries follow ordinary program lookup.

## QCMDEXC

`CALL QSYS/QCMDEXC PARM('CHGCURLIB QGPL' 14)` executes one command using the same
dispatcher as terminal, CL and batch execution. The command sees the current job's
library list, identity, adopted authority, locks, execution budget and cancellation.
Commands and their processing programs retain live authority/signature checks.
Command failures preserve their message ID for CL MONMSG and exception receipt.
Nested dynamic calls retain the existing command/program recursion bounds.

| Parameter | Input contract |
|---|---|
| Command | CHAR storage in the executing job CCSID |
| Length | Packed DEC(15,5), an integer from 1 through 32702 within supplied storage |
| Optional IGC control | CHAR(3), uppercase `IGC` |

Direct and compiled CL calls use the same byte contract. Hex buffers can supply
independent packed lengths; malformed digits/signs and mismatched declarations
fail before dispatch. The interpreted RPG bridge supplies semantic character and
decimal inputs; it does not certify a native RPG ABI. The typed host bridge also
accepts immutable ProgramBuffer inputs. Input storage is never written back.
Only the selected command bytes are decoded, so unused trailing storage may be
binary. UTF-8 lengths count bytes; splitting an encoded character fails. Buffers
with another CCSID fail rather than being silently transcoded. The supported
CCSIDs currently require no extra DBCS state for IGC.

This checkpoint accepts a single command line, rejects NUL/newline separators,
and does not implement interactive prompting, proxy commands, command exit points
or DBCS shift-state code pages. Commands returning a screen/menu/signoff request
fail with CPF0006; terminal screens are not driven by this API. Listing-producing
commands can return a normal command result to the host. CL-only declarations and
variable-receive commands still require their compiled CL context. The HTTP command bridge below can invoke CALL QCMDEXC; full domain REST and
debugger entry points remain pending their owning services.

QcmdexcTests covers direct/compiled CL and RPG callers, independent packed bytes,
CCSIDs 37/1208, malformed layouts, byte boundaries, unchanged inputs, maximum
length, MONMSG, authority/trust revocation, bounded recursion and cancellation.
The display PTY compiles CLAPI and checks a QCMDEXC call against RTVJOBA state.
The parameter reference is IBM's [Execute Command API](https://www.ibm.com/docs/en/i/7.5?topic=ssw_ibm_i_75%2Fapis%2Fqcmdexc.html).

## HTTP command bridge

Start `as400server`, then run:

```sh
dotnet run --project src/Ipc.Web -c Release -- --server /path/to/instance/run/as400.sock
```

The default development listener is `http://127.0.0.1:5080`. Configure Kestrel HTTPS
for remote access; non-loopback plaintext command requests are rejected. Authenticate
with HTTP Basic, then POST `/api/commands` with a JSON object containing `command`.
Do not put credentials in URLs. Example body:

```json
{"command":"SBMJOB CMD(CALL PGM(QGPL/REPORT)) JOB(REPORT)"}
```

Each request creates a server-owned session/job, executes through the same command
service as the terminal, and signs off. Responses contain `success`, `error`, `job`
and `result`; `result` uses the shared CommandResult contract. There is no client-
selected job/user field. Limits: 64 KiB HTTP body, 32768 command characters, 30 seconds.
Long-lived transactions and program parameter APIs are not exposed through this route.

Status: 200 success, 400 invalid/failed command, 401 authentication failure or required
password change, 426 remote plaintext rejected, 501 unavailable command, 503 unavailable
server, 504 timeout/cancel. Complete domain-specific error mapping remains C16 work.
`GET /health/live` reports the HTTP gateway's liveness, not database/server readiness.

## Local typed connection

`CommandConnection.ConnectAsync(socketPath, user, password)` consumes the terminal
protocol greeting and authenticates in command mode. `ExecuteAsync(command)` and
`SignoffAsync()` use that server-assigned identity. Callers serialize requests on each
connection and dispose it on failure/cancellation. Multiple clients get distinct jobs.
The wire format is length-prefixed JSON bounded to 256 KiB; the initial
`TerminalInput.CommandSession` selects command mode only before terminal sign-on.
Bad credentials, password expiry, identity replacement and malformed requests fail.

The server cancels interpreter loops when the connection closes. A disconnected job
ends abnormally. Shared service authority, PAM/SSO and audit are covered by C03 and the security
contracts. The complete domain REST surface remains C16 work.

`WebExecutionTests` uses a real HTTP listener and live Unix-socket server.
`SessionServerTests` proves terminal/headless catalog sharing and independent jobs.

The shared MFA and SSO endpoints, bearer authentication and Unix management protocol
are documented in [MFA and sessions](mfa-sessions.md).
