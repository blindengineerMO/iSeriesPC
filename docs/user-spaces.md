# Durable user spaces

Schema 25 stores `*USRSPC` payloads as opaque SQLite BLOBs, bound to the catalog
object by a fresh identity. Positions are one-based and sizes are exact bytes.
Reads return detached data. Writes and resizing preserve the unaffected prefix
and tail and commit atomically with metadata. Concurrent writers serialize through
SQLite; busy writers observe cancellation between bounded lock attempts.

The storage, native API and terminal acceptance suites cover byte values 00–FF,
both native maximum sizes, first/last bytes and range overflow, resize/truncation,
copy/move/delete/library rename, replacement and authority preservation, concurrent
disjoint writers, cancellation under a real writer lock, schema-24 upgrade and
server restart. WP12-04 byte-store acceptance is complete; wider API/MI
compatibility remains tracked separately.

The latest verification and remaining checklist work are recorded in
[completion progress](completion-progress.md).

## Commands

`CRTUSRSPC` is an iSeriesPC extension:

```text
CRTUSRSPC USRSPC(QGPL/BYTES) SIZE(1024) VALUE(X'00') ATTR(APPDATA) TEXT('Application data')
DLTUSRSPC USRSPC(QGPL/BYTES)
```

Accepted create keywords are USRSPC, SIZE (default 1), VALUE (one raw hex or job
character byte, default 00), ATTR, TEXT, AUT (default *LIBCRTAUT), REPLACE (*NO),
DOMAIN (*DEFAULT), TRFSIZ (0), ALIGN (*YES), and AUTOEXT (*NO). AUT also accepts
an authorization-list name. DLTUSRSPC accepts exact names or a nonempty prefix
followed by a terminal `*` (ten characters total). Embedded stars, bare `*` and
object-name `*ALL` are rejected. Underscores in names are literal characters.
Omitted libraries use the current library for creation and job library search for
deletion. [Job-private QTEMP](temporary-libraries.md) now supports these commands
and APIs, with isolation, logical return-library names and job-end/recovery cleanup.

Deletion accepts real libraries, *CURLIB, *LIBL, *USRLIBL, *ALL and *ALLUSR. *LIBL
stops at the first library containing matches, including unauthorized matches.
*USRLIBL searches the configured current library and user portion, without the
system portion. *ALLUSR uses IBM's documented library exclusions and Q-prefixed
user-data list, including numeric ASP and prior-release library patterns. *ALL
also includes QSYS and the executing job's QTEMP. No broad selector searches other
jobs' private namespaces; outside a job *ALL searches permanent libraries only.

Generic and multiple-library deletion commits eligible matches and retains denied,
allocated or dependent objects. Partial success returns CPF2117; no matches or no
eligible matches return CPF2125. Exact missing objects return CPF2105, missing
libraries CPF2110, denied libraries/objects CPF2182/CPF2189, and conflicting object
allocations CPF2114. Protected exact deletions retain the dependency diagnostic.
Unsearchable libraries in broad searches are reported separately from matched
objects. Unexpected database failures and cancellation roll back the whole batch.
Authority checks finish before the first catalog write; per-object savepoints
isolate expected dependency failures. Partial failures are reported after commit,
including through native error receivers and CL MONMSG.

## API contracts

All six APIs are versioned QSYS `*PGM` IPCAPI objects; ordinary CALL authority,
program lookup and execution scope apply. Characters use the job CCSID. Binary
fields are signed four-byte big-endian integers, never packed decimal; compiled
CL callers can use `(8 (*INT 4))` or CHAR(4) buffers populated with `%BIN`.
Native character arguments require their full documented storage widths. In
particular, declare CHAR(50) for QUSCRTUS text instead of passing a short constant.
RPG callers can use a typed CL wrapper; general native RPG binary storage remains
open. Scalar character/packed RPG borrowing does not imply binary INT support.

| API | Accepted parameters in order |
|---|---|
| QUSCRTUS | Qualified CHAR(20), extended attribute CHAR(10), size BINARY(4), initial raw CHAR(1), AUT CHAR(10), text CHAR(50); optional replace CHAR(10) **and** error code; then domain CHAR(10); then transfer BINARY(4) **and** alignment CHAR(1); then autoextend CHAR(1) |
| QUSRTVUS | Qualified CHAR(20), starting position BINARY(4), length BINARY(4), writable CHAR receiver; optional error code |
| QUSCHGUS | Qualified CHAR(20), starting position BINARY(4), length BINARY(4), CHAR data, force CHAR(1); optional error code; optional writable return library CHAR(10) |
| QUSRUSAT | Writable CHAR receiver, receiver length BINARY(4), `SPCA0100` CHAR(8), qualified CHAR(20), error code |
| QUSCUSAT | Writable return library CHAR(10), qualified CHAR(20), attribute records, error code |
| QUSDLTUS | Qualified CHAR(20), error code |

Qualified names contain the object name in the first ten bytes and library in
the next ten, space padded. Creation accepts a real library or *CURLIB; other
operations also accept *LIBL; QUSDLTUS additionally accepts the generic deletion
selectors described above. Output validation and normalization occur before
any persistent mutation. Aliased output cells are rejected. Receiver tails remain
unchanged. The shared API error header uses big-endian bytes provided/available;
zero bytes provided raises a monitorable exception, otherwise at least eight
writable bytes are required. Error identifiers fit a 16-byte header; richer native
exception data and all platform-specific CPF distinctions remain unfinished.

`SPCA0100` is 24 bytes. Receivers may request 8 through their actual storage size;
the returned prefix is truncated when necessary.

| Offset | Type | Field |
|---:|---|---|
| 0 | BINARY(4) | Bytes returned, min(requested length, 24) |
| 4 | BINARY(4) | Bytes available, 24 |
| 8 | BINARY(4) | Current size |
| 12 | CHAR(1) | Autoextend, encoded `0` or `1` |
| 13 | CHAR(1) | Initial raw byte |
| 14 | CHAR(10) | Resolved library |

QUSCUSAT accepts 1–256 records. A BINARY(4) count precedes four-byte-aligned
records containing key BINARY(4), data length BINARY(4), then data. Keys are size
(1, BINARY(4)), initial byte (2, CHAR(1)), autoextend (3, encoded CHAR `0`/`1`),
and transfer size (4, BINARY(4)). Extra record data is ignored; short/truncated
records fail atomically. Each value is validated; the last duplicate supplies the
final value. When size and initial byte change together, growth uses the new
initial byte. Existing data is retained up to the new size.

## Limits, authority and replacement

Optimum alignment permits 1–16,773,120 bytes; unaligned spaces permit
1–16,776,704. Transfer size is a 0–32 request retained as metadata. The emulator
does not expose MI or host pointers, physical page alignment, or transfer scheduling.
Autoextend is retained as an attribute; read/change APIs enforce explicit bounds.
Implicit pointer-driven extension and list-API producers remain open. Force `2`
uses SQLite FULL synchronous commit; `0`/`1` use normal SQLite durability. Native
asynchronous auxiliary-storage scheduling is outside the current implementation.

Read and attributes require *USE; writes require *CHANGE; attribute changes also
require *OBJMGT. Creation checks library read/add authority; deletion checks
*OBJEXIST without an extra *USE requirement. Existing explicit job allocations
protect reads and mutations. Byte writes acquire native *EXCLRD, allowing another
job's *SHRRD allocation while rejecting competing write modes. Attributes,
replacement and deletion require *EXCL. Copies get independent payload identities;
invalid object/payload bindings reject read, change, copy and move.

QALWUSRDMN defaults to *ALL. CHGSYSVAL requires both *ALLOBJ and *SECADM and accepts
*ALL alone, *DIR, or existing unique libraries optionally containing *DIR (at most
50 entries). Individual library lists must include QTEMP; *DIR alone also permits
user-domain QTEMP objects. Physical backing names cannot become permanent policy
entries. Creation reads
the persisted value live: *DEFAULT chooses *USER when allowed, otherwise *SYSTEM;
explicit *USER in a disallowed library fails with native CPF3C49. Explicit *SYSTEM is preserved even
in QTEMP. Copying or moving a user-domain space to another library checks the live
policy inside the catalog transaction, including library rename. Failure rolls
back the entire relocation. System-domain spaces remain unrestricted. Renaming
an existing space within its library preserves its domain after policy changes;
creating a copy still requires the library to permit user-domain objects.

REPLACE(*YES) creates a new identity and preserves the original owner, public,
private and attached authorization-list authorities. AUT is ignored on replacement.
The original moves to a generated QRPLOBJ name with its original name as text.
If it was *USER and QRPLOBJ disallows user-domain objects, it is deleted in the
same transaction after authorities are copied. Registered dependencies are
preserved on archival; any protected deletion failure rolls the entire replacement
back. The exclusive session-server owner removes marked replacement archives at
startup, after job/lock recovery and before accepting sessions. Ordinary catalog
opens preserve them. Unrelated manually created QRPLOBJ objects are retained.
Protected dependents prevent recovery deletion and fail startup visibly.

Remaining work includes additional QTEMP domain-store integration, richer native
error data/distinctions, native MI behavior, and additional list
APIs. [QUSLJOB](job-apis.md) now produces JOBL0100/0200 atomically in existing
spaces, with preserved caller/tail bytes, implicit growth and continuation. Broader C11/C14 checklist items remain open.

## Byte-store acceptance (WP12-04)

The finite WP12-04 requirement is CRTUSRSPC plus QUSCRTUS/QUSRUSAT as a
byte-addressable store. Its acceptance evidence is:

| Required behavior | Evidence |
|---|---|
| Opaque bytes, independent storage, bounded read/write/resize | UserSpaceTests: all 256 bytes, exact maximum sizes, boundary arithmetic, prefix/tail retention, resize and copy independence |
| Commands and native API subset | UserSpaceApiTests: independent SPCA0100 vectors, optional groups, receiver tails, binary/character types, compiled CL calls and CCSIDs 37/1208 |
| Object and job lifecycle | UserSpaceTests, UserSpacePersistenceTests, JobTemporaryLibraryTests: replacement, authority preservation, copy/move/rename/delete, recovery, own-job QTEMP and foreign backing-name denial |
| Authority, allocations and domain policy | UserSpaceTests, UserSpaceDeletionTests, JobLockTests: live revocation, OBJEXIST-only deletion, 25 native lock combinations, EXCLRD writes, privileged policy updates and relocation rollback |
| Generic deletion and observable failures | UserSpaceDeletionTests, UserSpaceApiTests: all selectors, literal prefixes, partial commits, dependencies, allocations, native error receivers, CL MONMSG and invalid-output-before-mutation |
| Persistence, concurrency and cancellation | UserSpacePersistenceTests, JobTemporaryLibraryPersistenceTests: migration/reopen, independent process writers, contention cancellation, database-fault rollback and durable partial deletion |
| Terminal operation | tools/pty-display-smoke.py: create, native binary read/write/attributes/delete, EXCLRD allocation/release and generic two-object QTEMP deletion |

The broader C11 combined storage/API checklist remains open for its remaining
data-object/API work. Native MI/pointers, additional list producers, rich exception data and
other private domain stores retain their separate open requirements.

## References

The byte layouts and limits were checked against IBM documentation:
[QUSCRTUS](https://www.ibm.com/docs/fr/i/7.6.0?topic=ssw_ibm_i_76%2Fapis%2Fquscrtus.html),
[QUSRUSAT](https://www.ibm.com/docs/en/i/7.5.0?topic=q-retrieve-user-space-attributes-qusrusat),
[QUSCHGUS](https://www.ibm.com/docs/en/i/7.5.0?topic=q-change-user-space-quschgus),
[QUSRTVUS](https://www.ibm.com/docs/en/i/7.6.0?topic=q-retrieve-user-space-qusrtvus),
[QUSCUSAT](https://www.ibm.com/docs/en/i/7.4.0?topic=q-change-user-space-attributes-quscusat),
[QUSDLTUS](https://www.ibm.com/docs/en/i/7.6.0?topic=q-delete-user-space-qusdltus),
[DLTUSRSPC](https://www.ibm.com/docs/en/i/7.5.0?topic=d-delete-user-space),
[QALWUSRDMN](https://www.ibm.com/docs/en/i/7.4.0?topic=values-allow-user-domain-objects-qalwusrdmn),
[security-value authority](https://www.ibm.com/docs/en/i/7.4.0?topic=overview-security-system-values-allow-these-objects-in),
[MOVOBJ](https://www.ibm.com/docs/sr/ssw_ibm_i_74/cl/movobj.htm),
and [CRTDUPOBJ](https://www.ibm.com/docs/en/i/7.5.0?topic=c-create-duplicate-object).
