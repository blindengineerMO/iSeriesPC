# Native job APIs

QSYS/QUSRJOBI and QSYS/QUSLJOB read the existing shared work catalog. They are
versioned IPCAPI programs, subject to ordinary lookup, current program authority,
manifest and signature checks. `WRKAPILS API(QUS*)` exposes their exact contracts.
This checkpoint implements JOBI0100/0700 and JOBL0100/0200 with the fields below.
Other job formats, performance resets and unsupported field keys fail explicitly.

## Common storage and job selection

Integers are big-endian binary. CHAR uses the caller's job CCSID. A qualified job
is CHAR(26): name(10), user(10), number(6). `*` with blank remaining fields selects
the executing job. Retrieval also accepts a specific qualified name or `*INT`
with blank remaining fields and a raw 16-byte internal identifier. Otherwise the
identifier argument must contain spaces. IDs are opaque bytes, including zeros
and high-bit bytes; never decode them as text.

Schema 28 backfills unique IDs and assigns them transactionally to new jobs.
Ordinary catalog opens preserve them. The exclusive session-server owner starts
a new epoch before readiness, atomically refreshing IDs and the list-cursor secret.
Old IDs and foreign-catalog IDs cannot select another job. Terminal history that
has no pending output is excluded: completed/ended jobs do not become `*OUTQ`.
Explicit OutputQueue status is supported, but this API does not create spool output.

Native types map Interactive to I, Batch to B, System to S, Autostart to A and
SubsystemMonitor to M. Prestart is B/J. iSeriesPC communication executions use
the immediate-batch representation B/D.

The standard optional error structure has BIN(4) bytes provided/available and a
seven-character exception ID. Zero bytes provided raises the failure. Nonzero
storage must hold at least eight bytes. Receiver/error writes are prepared before
publication; rejected writable storage leaves other output untouched.

## QUSRJOBI

Arguments are writable receiver, BIN(4) length, CHAR(8) format, CHAR(26) qualified
job, CHAR(16) ID, optional error, optional CHAR(1) reset. Total counts are 5, 6
or 7. Reset must be `0` for these formats. Receiver length is at least eight;
short receivers receive a prefix, with returned/available counts at offsets 0/4.
Bytes after the returned prefix remain unchanged. Offsets here are zero-based.

| JOBI0100 offset | Field |
|---|---|
| 0, 4 | BIN(4) bytes returned, available (86) |
| 8, 18, 28 | name(10), user(10), number(6) |
| 34 | raw ID(16) |
| 50, 60, 61 | status(10), type(1), subtype(1) |
| 62 | reserved(2) |
| 64, 68, 72 | BIN(4) run priority, time slice milliseconds, default wait seconds |
| 76 | purge(10) |

Basic identity/status is public metadata. Active priority/time slice come from
admission attributes. Default wait is zero because implicit allocations fail
immediately; explicit operation waits retain their own controls. Active purge is
`*NO` because interpreter storage survives time-slice boundaries. Queued/output
jobs return zero/blank active attributes.

JOBI0700 requires the caller's own job, the same job user, or job-control authority,
and an active target. Its identity prefix matches JOBI0100 through offset 63.
BIN(4) counts at 64/68/72/76 describe system/product/current/user libraries.
Entries begin at 80 as successive CHAR(11) names in that order. Only existing
libraries are returned; product count is zero because no product list is backed.
The private namespace is exposed as QTEMP; physical backing names remain hidden.

Invalid format/receiver/name produce CPF3C21/24/58. Invalid, stale or absent
internal IDs produce CPF3C51/52; missing specific jobs CPF3C53; ended jobs without
output CPF3C55. Unavailable/protected libraries produce CPF3C54/57. A nonblank
ID without `*INT` produces CPF3C59.

## QUSLJOB

Required arguments are CHAR(20) existing user-space name (object then library),
CHAR(8) format, CHAR(26) job selection and CHAR(10) status. Optional group one is
error storage. Group two adds CHAR(1) type, BIN(4) key count and a BIN(4) key array.
Group three adds raw CHAR(48) continuation handle. Total counts are 4, 5, 8 or 9.
JOBL0100 requires zero keys and binary-zero dummy key storage when group two is
present. JOBL0200 supports 0–64 keys, including repeated keys in requested order.

Name/user selectors accept exact names, trailing-star prefixes, `*ALL` or
`*CURRENT`. Number is six positive digits or `*ALL`. Status is `*ALL`, `*ACTIVE`,
`*JOBQ` or `*OUTQ`; type is `*` or A/B/I/M/R/S/W/X. A literal underscore in a name
is preserved. Basic rows are public. A keyed row for an unauthorized foreign job
has information status A and zero returned fields, while retaining basic identity.

The target requires *CHANGE authority, library execution and an *EXCLRD allocation.
Generation preserves caller bytes 0–63 and storage after the newly used prefix.
It grows an existing space up to its native maximum even when auto-extension is
disabled, and never shrinks it. User-space bytes, size and changed/signature state
publish atomically. Database failure, cancellation or failed final authority
validation rolls back the whole publication. It does not create the target space.

| Generic list header offset | Field |
|---|---|
| 0 | caller-owned area(64), preserved |
| 64, 68 | BIN(4) header size 128 (excludes caller area), release `0100` |
| 72, 80, 90 | format(8), API(10), local CYYMMDDHHMMSS(13) |
| 103, 104 | information status C/P, BIN(4) used bytes |
| 108, 112 | BIN(4) input offset/size |
| 116, 120 | BIN(4) API header offset/size |
| 124, 128 | BIN(4) list offset/size |
| 132, 136, 140 | BIN(4) entry count, fixed entry size, CCSID |
| 144, 146, 149 | country(2), language(3), subset(1); blank/blank/0 |

Input starts at 192 and uses 120 + 4 × key count bytes. It contains the original
job selection, status, space, format, type, count, keys and input handle. The API
header follows, containing resolved name/user/number and output handle(48), total
74 bytes. List entries start at the next four-byte boundary. With no keys, the
API header starts at 312 and list at 388. Consumers must read the offsets.

JOBL0100 entries occupy 56 bytes: name(10) at 0, user(10) at 10, number(6) at 20,
ID(16) at 26, status(10) at 42, type/subtype at 52/53, reserved at 54.
JOBL0200 adds information status(1) at 56, reserved(3), field count BIN(4) at 60
and field records at 64. Each record contains BIN(4) record length/key at 0/4,
type C/B at 8, reserved(3), BIN(4) data length at 12, then data, padded to four
bytes. All rows use the same selected entry size, including inaccessible rows.

| Key | Field | Type/bytes |
|---|---|---|
| 302 | job CCSID | B/4 |
| 304 | CPU milliseconds; -1 at or above signed 32-bit maximum | B/4 |
| 305 | current user profile | C/10 |
| 312 | unsigned CPU milliseconds | B/8 |
| 401, 402, 418 | start, submission, completion local CYYMMDDHHMMSS | C/13 |
| 409 | default wait seconds (zero) | B/4 |
| 412 | assigned default CCSID | B/4 |
| 1003, 1004 | job description, job queue (object then library) | C/20 |
| 1012 | job user identity | C/10 |
| 1604 | purge (`*NO` when active) | C/10 |
| 1802 | run priority | B/4 |
| 1803 | routing data | C/80 |
| 1906 | subsystem (object then library) | C/20 |
| 2002 | time slice milliseconds | B/4 |

CPU combines measured managed-thread and native-process accounting. Unavailable
dates/profile/subsystem are blank; inactive CPU/priority/time-slice values are
zero. I/O counters, pool statistics and other unsupported keys fail explicitly.

At the real maximum space size, status P returns an opaque authenticated output
handle. Pass it in argument nine with the same resolved selection/format/keys to
continue; an initial handle is 48 spaces. Status C returns a blank handle.
Handles are bound to the catalog, host epoch and query; tampering, foreign or
stale handles fail without changing the target. Rows are ordered by job number;
each call reads a committed view. Jobs can change between pages, so continuation
does not promise one snapshot spanning multiple calls.

## Calling from CL and RPG

This CL fragment creates a private list, obtains its ID and retrieves the job.
CL character literals requiring exact native widths use explicit type/length.

```cl
DCL &LIST *CHAR LEN(512)
DCL &INFO *CHAR LEN(128)
DCL &ID *CHAR LEN(16)
CALL QSYS/QUSCRTUS PARM('JOBS      QTEMP     ' '' (512 (*INT 4)) X'FE' '*CHANGE' ('Native job list' (*CHAR 50)))
CALL QSYS/QUSLJOB PARM('JOBS      QTEMP     ' 'JOBL0100' '*' '*ACTIVE' X'00000000')
CALL QSYS/QUSRTVUS PARM('JOBS      QTEMP     ' (1 (*INT 4)) (512 (*INT 4)) &LIST X'00000000')
CHGVAR &ID %SST(&LIST 415 16)
CALL QSYS/QUSRJOBI PARM(&INFO (86 (*INT 4)) 'JOBI0100' '*INT' &ID X'00000000')
```

Direct RPG uses `dcl-s LIST char(512) ccsid(*hex)`, raw CHAR receivers/IDs and
signed `int(10)` length fields. `eval ID = %subst(LIST:415:16)` copies all ID bytes
without decoding; CALLP can invoke both APIs without a CL wrapper. These fixed
positions apply only to the no-key layout above.

## Evidence and references

JobApiTests and JobApiEdgeTests cover independent layouts, short receivers,
authority, CPU overflow, native maximum-space pagination, foreign/stale handles,
atomic failures and cancellation. NativeJobIdentityTests covers schema-27 upgrades,
rollback, insert/delete, reopen and exclusive-host epoch refresh. CL/direct RPG
run at CCSIDs 37/1208; concurrent socket jobs each use their own QTEMP space.
The full display PTY runs compiled CL/RPG calls and WRKAPILS details.

Layouts and authority rules were checked against IBM's
[QUSRJOBI reference](https://www.ibm.com/docs/en/i/7.5.0?topic=ssw_ibm_i_75%2Fapis%2Fqusrjobi.html),
[QUSLJOB reference](https://www.ibm.com/docs/en/i/7.4.0?topic=ssw_ibm_i_74/apis/qusljob.html),
[common user-space structures](https://www.ibm.com/docs/en/i/7.5.0?topic=spaces-common-data-structure-formats)
and [Work Management API publication](https://public.dhe.ibm.com/systems/power/docs/systemi/v5r4/en_US/wm.pdf).
Broader C09/C14 ABI, job formats, date/journal/security/cryptography APIs and spool
integration remain open.
