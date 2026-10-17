# System API checkpoint

Executable catalog APIs are QSYS/QCMDEXC, QSNDDTAQ, QRCVDTAQ and QCLRDTAQ.
The data-queue marshalling lives in `Ipc.Api`; QCMDEXC still shares the session
command dispatcher. The broader C14 registry, message/user-space/work/security
APIs and format contracts remain open.
Each adapter is a versioned `*PGM` object with attribute IPCAPI. Normal library lookup,
object use authority, signature validation and program scope apply. Startup seeds
each only when absent; it never overwrites an existing or signed program. Relocated
or changed adapter manifests fail validation. Programs with the same API names
in other libraries follow ordinary program lookup.

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
fail before dispatch. RPG scalar borrowing checks CHAR storage and DEC(15,5)
declarations on PLIST, unprototyped and supported scalar prototype calls;
DEC(15,0) is rejected even when its value is an integer. These interpreted
layouts do not certify a native compiled RPG ABI.
The typed host bridge also
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

## Local data-queue program APIs

These adapters operate on the durable STD queues in [data queues](data-queues.md).
They use the current job's library list, principal, adopted authority, object locks,
budget and cancellation. Both explicit names and *LIBL/*CURLIB resolve without
requiring read authority just to send. Queue access still uses the shared service:
send requires operate/add, destructive receive operate/read/delete, peek
operate/read, and clear operate/delete. These live checks intentionally differ
from IBM's cached authorization and native receive/clear authority requirements.

Names occupy CHAR(10); key order occupies CHAR(2); other controls occupy CHAR(10).
Only those fixed prefixes are decoded, in the executing job CCSID. Longer input
storage is permitted. Queue data and keys remain opaque bytes without conversion.
Packed DEC(5,0), DEC(3,0) and DEC(7,0) occupy 3, 2 and 4 bytes respectively.
Typed declarations must match the required precision and scale; raw CHAR/hex
storage must have exactly the packed field width and valid digits/signs. Direct
CL numeric constants default to DEC(15,5), so specify the API layout explicitly.

| API | Required arguments, in order | Accepted total counts |
|---|---|---|
| QSNDDTAQ | queue, library, DEC(5,0) data length, data | 4, 6, 7, 8 |
| QRCVDTAQ | queue, library, writable DEC(5,0) length, writable data, DEC(5,0) wait | 5, 10, 13, 14 |
| QCLRDTAQ | queue, library | 2, 6 |

QSNDDTAQ's optional pair is DEC(3,0) key length and key input. Parameters 7 and 8
are asynchronous-request and journal-entry controls; this checkpoint accepts only
*NO. Data length selects a positive input prefix up to 64512 bytes. Key length
must be zero for unkeyed queues, or match the keyed queue's declared length.
See IBM's [Send Data Queue API](https://www.ibm.com/docs/en/i/7.4.0?topic=q-send-data-queue-qsnddtaq).

QRCVDTAQ's first optional group contains key order, DEC(3,0) key length, writable
key, DEC(3,0) sender receiver length, and writable sender receiver. A zero key
length ignores key/order; a zero sender length ignores its receiver. The second
group contains *YES/*NO removal, DEC(5,0) data receiver size, and an error receiver.
The final argument selects *SEC or *MSEC. Wait zero polls once, any negative value
waits until receipt/cancellation, and positive values wait for the specified units.
All optional groups must be complete.

Without explicit data receiver size, an entry larger than actual storage raises
CPF9506 and stays queued. With explicit size, receipt copies that many bytes at
most; zero copies no data. Returned length always reports the full entry size.
Only received prefixes change; trailing bytes remain intact. Timeout changes only
the returned length to zero. Matching keyed receives return the selected entry's
actual key. All outputs are normalized before removal, including sender metadata
that cannot fit the native field widths. Checked failures and observed cancellation before commit preserve the
entry and output cells. Output aliases are rejected before receipt.

Sender receivers accept zero or 8–999 bytes. Headers at offsets 0 and 4 are packed
DEC(7,0) bytes returned/available. Identity fields start at offsets 8 (job name,
10 bytes), 18 (job user, 10), 28 (six-digit job number), and 34 (current sender,
10). Recorded sender data has 44 available bytes. Without retained sender metadata,
this implementation returns only the eight header bytes and preserves the tail.
Names use the receiving job CCSID; unrepresentable or oversized identities fail
before dequeue. Requested partial prefixes are supported.
See IBM's [Receive Data Queue API](https://www.ibm.com/docs/en/i/7.4?topic=ssw_ibm_i_74%2Fapis%2Fqrcvdtaq.htm).

QCLRDTAQ's optional group contains key order, DEC(3,0) key length, key input and
error receiver. Zero key length clears the entire queue, including keyed queues.
Positive length requires the declared queue key width and selects the comparison
range. See IBM's [Clear Data Queue API](https://www.ibm.com/docs/en/i/7.4.0?topic=q-clear-data-queue-qclrdtaq).

Error receivers begin with signed big-endian 32-bit bytes provided and bytes
available, then seven job-CCSID message-ID bytes and one reserved byte. Provided
zero propagates the exception (a four-byte literal zero is allowed). Otherwise,
provided must be 8 through actual writable storage length. Failure returns 16
available bytes, the message-ID prefix that fits, and reserved zero when it fits;
success sets available to zero. Bytes outside the supplied receiver remain intact.
Exception substitution data and extended error structures are not implemented.
An error receiver aliasing another argument is rejected without writing it.

```cl
PGM
DCL &DATA *CHAR LEN(80)
DCL &LENGTH *DEC LEN(5 0)
CALL QSYS/QSNDDTAQ PARM('WORK' 'QGPL' (5 (*DEC 5 0)) 'READY')
CALL QSYS/QRCVDTAQ PARM('WORK' 'QGPL' &LENGTH &DATA (0 (*DEC 5 0)))
CALL QSYS/QCLRDTAQ PARM('WORK' 'QGPL')
ENDPGM
```

Compiled CL, typed host ProgramArgument cells and the supported RPG scalar calls
provide writable receivers; immutable constants fail with CPF3C90. The RPG call
contract below includes direct receives without a CL wrapper. Invalid groups use
CPF3C36; checked layout/control
failures use CPF3C3A; queue failures preserve their shared service IDs. Exact IBM
error substitution data and all native error-ID distinctions remain open.

DataQueueApiTests includes independent packed/sender/error bytes, CCSIDs 37/1208,
truncation/peek/timeout, maximum entry lengths, unchanged tails, output atomicity,
MONMSG, authority/signing revocation and cancellation. Terminal acceptance compiles
CLDQAPI and verifies all three adapters with sender and error receivers. DDM,
journal replay, priority scheduling and address-cache behavior remain unimplemented.

## RPG scalar API calls

Fixed CALL/CALLB with PLIST, CALLP with a named PLIST, and unprototyped inline
CALLP/CALLB calls can borrow fixed CHAR and packed/decimal fields. Procedure-pointer
targets use the same inline scalar path. Program lookup, authority and signing run
before borrowing; only built-in IPCAPI targets request typed cells. Ordinary CL,
RPG and external process targets keep their existing semantic argument/writeback
contract. RpgHost.ProgramCaller remains available for hosts using that contract;
TypedProgramCaller adds a lazy borrowing callback for hosts implementing typed APIs.

Repeated fields, including two RPG entry names referring to the same outer CL
cell, share one borrowed identity. The queue APIs can therefore reject overlapping
output receivers before dequeuing. Reads/writes are live during a synchronous call;
both cells and the lazy factory expire on return, failure or cancellation. Receivers
update storage directly; they are not copied through UpdatedParameters afterward.
An outer receiver's normalizer is checked during output preparation. At return,
outer character buffers detach into semantic text for retained RPG storage.

The adapter preserves the declared precision/scale and rejects fractional or
overflowing output values. Packed fields support up to .NET decimal's 28 digits.
CHAR requires both its character count and strictly encoded byte count to equal
the declared width. The job CCSID is read for each call. EBCDIC 37 permits binary
bytes that round-trip through the interpreter's text storage; UTF-8 receives must
also fit that storage without replacement or extra padding. Invalid UTF-8 and
multibyte output that would change the declared width fail before removal. Use CL
or typed byte-buffer callers for arbitrary binary UTF-8 entries. Raw API text such
as `*BLANK` is stored literally, without RPG assignment-keyword interpretation.

Named constants and evaluated expressions are immutable inputs. Expression values
are evaluated once. Arrays, DS/overlays, varying characters, zoned fields, binary
integers, floats and date/time fields cannot be borrowed in this checkpoint.
Calls are bounded to 256 arguments and 1 MiB of aggregate storage. Scalar
prototype calls can also borrow receivers under the contract below. Full native
RPG storage, broader prototype modes and the C09/C14 contracts remain open.

RpgApiArgumentTests covers direct queue calls at CCSIDs 37/1208, real key/sender/
error outputs, peek/truncation/timeout, aliases and expired lifetimes, scalar shape
and quota failures, cancellation, outer receiver rejection, raw assignment-keyword
bytes, authority and CL MONMSG propagation. QCMDEXC's RPG fixture now declares
the required DEC(15,5); mismatched-scale coverage proves rejection before dispatch.
The terminal fixture's RPGDQAPI sends, receives and clears a keyed queue directly.

### Scalar prototype calls

CALLP/CALLB prototypes targeting built-in IPCAPI programs now pass the actual
caller's scalar fields to the same scoped adapter. Both formal and actual fields
must have identical CHAR byte widths or packed precision/scale, with exactly one
actual argument per formal. Missing/ambiguous declarations and duplicate prototype
bindings fail before dispatch. Repeated actual fields retain one identity, so
native receiver overlap checks still apply. Prototype temporaries remain unchanged
while the API updates actual fields; no extra writeback follows a borrowed call.

In the documented emulator D-spec layout, PR selects EXTPGM/EXTPROC and explicit
A/P parameter rows use the PARM keyword. For example, this prototype accepts a
CHAR(10) queue/library, DEC(5,0) receiver length/wait, and CHAR(8) data receiver:

```text
      DGET           PR                           EXTPGM('QRCVDTAQ')
      DQUEUE         A                10          PARM
      DLIBRARY       A                10          PARM
      DLENGTH        P                5 0         PARM
      DTEXT          A                8           PARM
      DWAIT          P                5 0         PARM
```

These are the emulator's column conventions described in [RPG source columns](rpg-spec.md#3-source-columns-fixed-form).
Named formals receive private generated storage names and cannot shadow caller
variables with the same name. Multiple unnamed prototypes get independent storage;
the first legacy unnamed prototype retains P1/P2 names where available. This
checkpoint accepts fixed reference parameters with no options beyond PARM.
CONST, VALUE, OPTIONS and other parameter keywords are retained and rejected for
typed API borrowing. Arrays, DS/overlays, varying fields and unsupported numeric
types also fail. These limits apply when a built-in target requests borrowing.

Literals and expression results become immutable ProgramConstant inputs with the
formal layout. Character values pad to that width without truncation; packed
values reject overflow or scale loss and use the shared record codec. The API
then checks the native signature as usual, including constants: a DEC(3,0)
prototype literal cannot satisfy a DEC(5,0) API parameter. Constants used as output
receivers fail with CPF3C90. Argument splitting respects nested expressions,
quoted colons and doubled apostrophes; expressions are evaluated once.

The host resolves program authority/signing before borrowing. Ordinary targets
keep their earlier copied formal values and UpdatedParameters behavior. Their
formal coercion is prepared lazily only when the host reads semantic arguments;
unused argument factories expire at call return. Native scalar receivers use
actual storage directly. Full ordinary-program reference binding, modules/service
programs, optional/value/const modes and complete native RPG ABI parity remain open.

RpgPrototypeApiTests covers QCMDEXC and the three queue adapters, independent
constant byte vectors, formal/actual mismatches, real key/sender/error receivers,
peek/truncation, immutability, aliases and expiration, ordinary fallback, signed
API trust revocation, authority/MONMSG and malformed expressions. Terminal
acceptance runs RPGPRAPI through prototyped keyed send/receive/clear.

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
