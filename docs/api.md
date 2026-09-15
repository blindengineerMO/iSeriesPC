# Execution interfaces

The current executable API is a command bridge to the shared server. The IBM system
API ABI, IWS deployment, RSE compatibility and full domain REST surface remain C14/C16
work. Their required names and formats are tracked in the [catalog](compatibility-matrix.md)
and [release contracts](compatibility-contracts.md).

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
ends abnormally. C03 still requires complete service-level authority, PAM/SSO and audit;
this development bridge is not a declaration of production-ready access control.

`WebExecutionTests` uses a real HTTP listener and live Unix-socket server.
`SessionServerTests` proves terminal/headless catalog sharing and independent jobs.

The shared MFA and SSO endpoints, bearer authentication and Unix management protocol
are documented in [MFA and sessions](mfa-sessions.md).
