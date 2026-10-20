![Header](assets/iSeriesPC_Banner.png)
# iSeriesPC

Emulation of the IBM iSeries/AS-400 experience on x64 Intel Linux, implemented in C# (.NET 8)
over SQLite, with host-level integrations for SSH/SFTP, LDAP, DNS, SMTP, UFW, Samba, CUPS,
Node.js and a Vite admin console.

The SSH login shell is replaced by an iSeries-style menu interface (`AS400Menu`) with a full
5250/ANSI full-screen experience: sign-on, menus, CL command line, prompting (F4), help (F1),
function keys, subfiles and paging.

## Status

Under construction. See [PLAN.md](PLAN.md) for the master task list and the tracked
[compatibility matrix](docs/compatibility-matrix.md) for requirement-level status.
The verified original baseline is 223 passing tests; package completion requires
the additional acceptance criteria in the plan.

Latest [completion checkpoint](docs/completion-progress.md): 36/106 checklist items
complete, 7659 tests passing, and real sign-on/display PTY acceptance passing.
RPG [RECNO feedback and relative WRITE](docs/rpg-recno.md) now support numeric
record positions and directed output into allocated deleted slots.
Job [inquiry policies and the system reply list](docs/inquiry-replies.md) now
provide durable automatic default/list replies through the shared message queues.
[Member-full operator recovery](docs/operator-capacity.md) now grants durable
capacity and retries ordinary file/RPG writes and atomic copies.
[SQL tables and RUNSQL](docs/sql-tables.md) now share the catalog, typed constraints,
record storage and CL/RPG file I/O. [ALTER TABLE](docs/sql-alter-tables.md) adds
atomic column/key changes and dependent view/index rebinding.
[CHECK constraints](docs/sql-check-constraints.md) now share typed predicates across
SQL, native records, RPG, copies and private record workspaces.
[Searched SQL mutations](docs/sql-mutations.md) add atomic UPDATE/DELETE, exact
arithmetic and selected-record locks.
[Referential integrity](docs/sql-foreign-keys-progress.md) is in progress.
[RUNSQLSTM scripts](docs/sql-scripts.md) now process members and UTF-8 streams,
with mixed CL, syntax checking and located job-log diagnostics.
The shared [typed SELECT service](docs/sql-queries.md) adds joins, grouping,
exact aggregates, parameters and bounded query cursors with job cleanup.
[STRSQL](docs/sql-interactive.md) now provides statement entry, paged results,
saved sessions and interactive CL return through the same services.
[Query-management objects](docs/query-management.md) now save SQL source, run
native SETVAR fragments, prompt for missing values and display paged results.
[Query output files](docs/sql-query-output.md) now support typed PF creation,
replacement, append and CL/RPG reads through the shared record services.
[SQL indexes](docs/sql-indexes.md) now create maintained keyed logical files,
with unique/null rules, external record layouts and shared object lifecycle.
[SQL views](docs/sql-views.md) now store bound typed queries, including nested
joins/aggregates, owner-checked access, atomic replacement, CL/RPG input and
single-source SQL writes with LOCAL/CASCADED CHECK OPTION.
The C11 [journal foundation](docs/journals.md) now supplies durable receivers,
rotation, entry display, atomic PF/source capture, and forward/reverse recovery
with native partial-error policies. Audit hooks, save/restore and
commitment-control acceptance remain open. The internal
[record transaction foundation](docs/record-transactions.md) now shares staged
file/RPG/SQL changes and publishes records and journal cycles atomically. Statement
workspaces load only needed journaled members from a consistent snapshot while
excluding catalog/security/history rows. Joins, trigger/FK dependencies and bound
physical/logical/cursor members share that path; authenticated in-memory
publication is also verified. Bounded cursors retain successful commit checkpoints
and restore their positions on service rollback. SQLite RPG file access now uses
[bounded forward/reverse/key navigation](docs/cursor-navigation.md) with the same
checkpoints and no managed record-list cache. Current-record UPDATE/DELETE target
one physical member/RRN, preserving duplicate-key siblings and checking exact
before images through ordinary and enlisted writes. Compiled
[compound file keys](docs/rpg-file-keys.md) support expression lists, KLIST and
scalar `%KDS`; keyed DELETE selects one record under the same transaction gate.
[Relative record access](docs/relative-records.md) adds sparse RRN lookup, bounds
and deletion, explicit host arrival mode and the same commitment checkpoints.
[Declared DISK files](docs/rpg-disk-files.md) add external field bindings,
explicit access modes, USROPN, overrides and retained activation positions.
Declared *OUTPUT and fixed A/O modes now add records through mapped record-format
WRITE, including output-only users with Add authority. RECNO and native slot
allocation/reuse remain open.
The [retained update-read foundation](docs/update-read-locks.md) adds scoped
input locks, inquiry access, bounded waits and lifecycle cleanup. Declared
noncommit update/delete files now use them through N input, UNLOCK and mapped
record-format UPDATE.
[WAITRCD file attributes and overrides](docs/record-waits.md) now configure timed,
immediate and cancellable indefinite record waits for declared update files.
[Numeric file statuses and MONITOR](docs/rpg-file-status.md) now handle E/ER
precedence, record contention, preserved input fields and nested numeric handlers.
Full status/INFDS/PSDS and ILE exception coverage remain open.
[Native null values](docs/rpg-null-values.md) add ALWNULL modes, separate NULLIND
state, nullable database input/output and key searches with saved descriptions.
[Named indicators and external maps](docs/rpg-null-maps.md) now share live IND
storage across native scalars, structures, arrays and record I/O.
[Null-map parameters](docs/rpg-null-parameters.md) now share live indicators
across native procedures and stored RPG calls.
[Process protocol 3](docs/rpg-process-null-maps.md) carries validated null-map
sidebands alongside data; process aliases, aggregate indicators and broader
native ABI acceptance remain open.
[Right-adjusted assignment](docs/rpg-right-assignment.md) now supports native
fields, structures, array expressions and bounded borrowed substrings while
preserving null indicators.
[Corresponding-field assignment](docs/rpg-corresponding-assignment.md) now moves
compatible fields and null flags through nested structures, arrays and record
buffers, with source-order overlays and numeric half adjustment.
[Native free-form data structures](docs/rpg-native-structures.md) now provide byte
layouts, qualified fields/arrays, shared overlays and borrowed API receivers.
[Native INFDS/PSDS feedback](docs/rpg-feedback-structures.md) now supplies common
file feedback, program errors, last-file snapshots and initial job identity.
Extended feedback areas, nested structures and full entry/prototype ABI remain open.
[RPG error subroutines](docs/rpg-error-subroutines.md) now dispatch INFSR and
procedure-local *PSSR with monitor precedence, RETURN/GOTO recovery and *CANCL
cleanup. Native cycle restart points and general ILE exception handling remain open.
[Indicator data structures](docs/rpg-indicator-structures.md) now give WORKSTN
files local or explicitly shared 99-byte indicator banks. Saved bindings track
DDS INDARA while retaining version-one compatibility.
[Native fixed DS definitions](docs/rpg-fixed-structures.md) now support From/To
byte layouts, PACKEVEN, overlays, arrays, occurrences and feedback. Native
arithmetic resolves numeric DS views, and DO loops write indexed counter cells.
[Native program signatures](docs/rpg-program-signatures.md) now support PR/main PI
and free DCL-PR/DCL-PI with live scalar references. Free CALLP(E) handles final
call errors while preserving argument-processing statuses. External process
responses validate all returned cells before writeback.
[CONST parameters and character prefixes](docs/rpg-const-parameters.md) add live
read-only aliases, converted temporary values and formal-width prefix writes
that preserve caller tails through nested calls.
[Optional native parameters](docs/rpg-optional-parameters.md) add OPTIONS(*NOPASS),
%PASSED, real %PARMNUM positions and short-circuit guards across stored RPG, CL,
system API and external-process calls.
[Omitted native arguments](docs/rpg-omitted-parameters.md) add OPTIONS(*OMIT),
%OMITTED, distinct presence states and checked null slots in external requests.
[Native local procedures](docs/rpg-local-procedures.md) add P/DCL-PROC, linked
scalar interfaces and returns, VALUE copies, recursion, automatic/STATIC locals
and shared module storage and file positions.
[Static module binding](docs/rpg-module-binding.md) adds CRTRPGMOD/CRTPGM/DSPMOD,
captured exports, isolated module globals and activation-owned file resources.
[Service programs](docs/rpg-service-definitions.md) add CRTSRVPGM/DSPSRVPGM,
binder export/signature levels and dynamic BNDSRVPGM references from CRTPGM and
CRTBNDRPG. Services share activation-owned globals/files across programs, with
signature/interface checks and live authority/signing. [Binding directories and service dependencies](docs/rpg-binding-directories.md)
add ordered provider discovery, atomic entry updates and transitive activation
checks. [Source binding references and updates](docs/rpg-program-updates.md)
add BNDDIR controls, module replacement and service rebinding with atomic
publication and retained activation versions. [Deferred activation](docs/rpg-deferred-activation.md)
adds per-provider *IMMED/*DEFER modes with first-use validation. [Update policies](docs/rpg-update-policies.md)
add named group changes and fixed or runtime *LIBL service relocation.
[Data imports/exports](docs/rpg-data-binding.md) share bytes and null maps with
exporter-owned initialization and activation lifetimes. Wider binder/ABI contracts and independent native acceptance remain open.
[Native aggregate parameters](docs/rpg-aggregate-parameters.md) add fixed arrays
and LIKEDS structure interfaces, live element/subfield aliases, CONST inputs,
private VALUE copies and formal prefixes that preserve caller tails.
[Native array and structure returns](docs/rpg-return-values.md) add detached
results, default array tails and RTNPARM parameter positions/counts.
[DISK file definitions](docs/rpg-file-definitions.md) add metadata-only templates,
LIKEFILE copies, qualified records and checked saved file families.
[DISK file parameters](docs/rpg-file-parameters.md) share caller file state across
local procedures and saved RPG programs, including caller-owned error handling.
[Procedure-local DISK files](docs/rpg-local-files.md) add automatic and STATIC
file lifetimes, local feedback, error handlers and saved scope metadata.
[WORKSTN file definitions](docs/rpg-workstation-definitions.md) add templates,
LIKEFILE copies, qualified formats and checked saved display families.
[WORKSTN file parameters](docs/rpg-workstation-parameters.md) share caller display
state, RRN/indicator bindings and error handling across RPG calls.
[Procedure-local WORKSTN files](docs/rpg-local-workstations.md) add automatic and
STATIC display lifetimes, local buffers, feedback and scoped saved metadata.
[WORKSTN record structures](docs/rpg-workstation-records.md) add LIKEREC extracts,
EXFMT/read/write/subfile buffers and checked zoned display numerics.
[Record structures](docs/rpg-record-structures.md) add DISK LIKEREC layouts,
checked result buffers, key structures and native parameter/return interfaces.
[Externally described structures](docs/rpg-external-structures.md) add EXT/EXTNAME,
EXTFLD/PREFIX mappings, external defaults and source-bound description snapshots.
[Nested native structures](docs/rpg-nested-structures.md) add inline and LIKEDS/
LIKEREC children, shared storage views, nested record buffers and forward types.
[Data templates](docs/rpg-templates.md) retain layouts and defaults without
allocating runtime storage, with checked metadata-only uses.
[Forward LIKEDS templates](docs/rpg-forward-structures.md) add later structure
layouts and inherited initialization for copies, interfaces and returns.
[Forward native types](docs/rpg-forward-declarations.md) resolve later declarations
and implicit returns, with scoped constants, cycle checks and exact ancestry.
[Native LIKE declarations](docs/rpg-like-declarations.md) inherit supported
types and signed length adjustments, with type ancestry for exact calls.
[Exact and adjusted parameters](docs/rpg-parameter-options.md) add native
OPTIONS(*EXACT:*TRIM:*RIGHTADJ), checked conversions and related LIKEDS types.

[Variable-size references](docs/rpg-variable-parameters.md) add OPTIONS(*VARSIZE),
actual character-array strides and checked byte ranges across calls and APIs.
[Native RPG tables](docs/rpg-tables.md) now support CTDATA/ALT loading, paired
LOOKUP, six %TLOOKUP variants and current-element scalar/API access. Unprototyped
stored CL/RPG calls preserve live parameter aliases and writes on callee errors.
Pre-runtime table loading and complete native entry/procedure ABI remain open.
APY/RMV
validates committed cycles and reports incomplete ending ranges.
Native commitment commands, isolation and pending/rollback/interleaved cycle
recovery remain unfinished.
The C11 [job data-area storage lifecycle](docs/job-environment.md#c11-storage-completion-evidence)
is verified across submission, routing, group sharing, process concurrency, restart and cleanup.
[QWCRDTAA](docs/data-areas.md#qwcrdtaa-retrieval-api) now retrieves typed named/job
areas through checked binary receivers, compiled CL and borrowed RPG calls.
Durable [user spaces](docs/user-spaces.md) now support binary storage, resizing,
object lifecycle, commands and six native API adapters, including
[job-private QTEMP](docs/temporary-libraries.md), generic deletion with partial-failure
reporting, native EXCLRD writes and live domain policy on copy/move. Further private domain stores and
native compatibility work remain open.
QTEMP source/PF/simple LF storage now supports private members, CL/RPG compilation,
record I/O, shared paths, copy/export and recovery cleanup.
[Private message queues and files](docs/message-queues.md#job-private-queues-and-message-files)
now support saved descriptions, inquiry replies across jobs, isolated storage and atomic cleanup.
[Message subfiles](docs/display-files.md#message-subfiles) now bind to live job/frame
queues, capture text and second-level help at output, and load queues with SFLINZ.
Compiled CL DCLF/SNDF/RCVF/SNDRCVF now exchange typed fields and indicators with
local/socket terminals, including message-subfile help, nested calls, initial
programs and suspended-job transfers. Compiled RPG
[WORKSTN/EXFMT](docs/rpg-spec.md#12-workstn-display-files-and-subfiles) now exchanges
typed forms and ordinary/message subfiles, with READC/CHAIN/UPDATE RRNs and
activation-group file lifetimes. Wider native language, messaging and device work remains open.
Six [native message APIs](docs/api.md#native-message-program-apis) now send,
receive, reply and retrieve through shared queues and saved descriptions, with
binary receivers and cancellable allocated waits. RPG can call them directly using
raw CHAR and signed INT fields. `WRKAPILS` discovers all 27 runtime adapters and
shows their current availability and supported contract.
[QWCRSVAL](docs/system-values.md) retrieves 27 backed system values through native
aligned receivers, including binary policy values and consistent local date/time.
Reads observe committed changes from other hosts.
[QUSRJOBI and QUSLJOB](docs/job-apis.md) now retrieve basic/library information
and produce native user-space job lists with 17 backed fields, raw job IDs and
maximum-space continuation. Compiled CL/direct RPG and independent socket jobs
share the same work catalog while retaining private QTEMP storage.
[QWCCVTDT](docs/date-time-apis.md) converts native calendar/Julian dates and
binary timestamps, with millisecond/microsecond precision and UTC/host-zone
conversion. Job attributes and named time-zone metadata remain open.
[Software cryptography](docs/cryptography-apis.md) now provides native hash/HMAC,
AES encryption/decryption and encrypted job-owned key contexts. Compiled CL and
direct RPG exercise all six QC3 adapters; key ownership, rotation and end-of-job
cleanup use the shared catalog and authorization services.
RPG supports [compilation diagnostics](docs/rpg-spec.md#11-compilation-diagnostics-and-source-coverage),
[bounded preprocessing](docs/source-preprocessing.md#rpg-directives) and
[scalar prototype API calls](docs/api.md#scalar-prototype-calls).

| WP | Component | Status |
|----|-----------|--------|
| WP0 | Foundation | build baseline verified |
| WP1 | Ipc.Core domain model | partial — see compatibility matrix |
| WP2 | Ipc.Services host (SQLite, config, logging, events, IpcSystem) | partial — see compatibility matrix |
| WP3 | Security engine | partial — see compatibility matrix |
| WP4 | Job/work-management engine | partial — see compatibility matrix |
| WP5 | Terminal + display engine (ANSI 5250, fields) | partial — see compatibility matrix |
| WP6 | Session host (as400menu) + sign-on | partial — see compatibility matrix |
| WP7 | Menus system (MAIN/MAJOR, GO, *MENU objects) | partial — see compatibility matrix |
| WP8 | CL command system + interpreter (*CMD catalog, CRTCLPGM, CALL, command line) | partial — see compatibility matrix |
| WP9 | DDS compiler + SQLite file store (*FILE objects, members, record codec, CRTSRCPF/ADDSRCPFM/CRTPF/DSPPFM/ADDPFM/CPYF/DLTF) | partial — see compatibility matrix |
| WP10 | RPG compiler + interpreter (fixed + free form, `/free` directives, data structures, expressions + `%` built-ins, file I/O, subroutines, subprocedures P/D + CALLP, prototypes PR/PARMs + `PROCPTR`, `CALL`/`CALLP` with PLIST + updated-parameter writeback, `ON-ERROR`/`*INLR`, messaging) | partial — see compatibility matrix |
| WP12 | Messaging and data objects (durable MSGQ, typed DTAARA, FIFO/LIFO/keyed DTAQ, byte-addressable USRSPC) | partial — wider messaging/API integration and journals remain open |
| WP15 | System API host (QCMDEXC, checked local data-queue and user-space programs) | partial — broader API registry, formats and crypto remain open |

## Layout

This is the target layout; the compatibility matrix distinguishes implemented and planned components.

- `src/Ipc.Core` — domain model: objects, libraries, authorities, system values, CCSID, dates, *MENU objects
- `src/Ipc.Services` — job engine, spool engine, security, menu store, IFS, save/restore, messaging, journals
- `src/Ipc.Db` — SQLite catalog, DDS compiler, SQL bridge, SQL tools
- `src/Ipc.Cl` — command catalog, parser, prompt, CL program interpreter
- `src/Ipc.Rpg` — RPG interpreter (fixed + free form)
- `src/Ipc.Dsp` — DSPF compiler + panel/subfile runtime
- `src/Ipc.Terminal` — ANSI 5250 renderer + input editor
- `src/Ipc.Console` — `AS400Menu` SSH login-shell host + sign-on
- `src/Ipc.Api` — checked data-queue and user-space program buffers; broader APIs planned
- `src/Ipc.Session` — shared controllers, command orchestration, and terminal session transport
- `src/Ipc.Server` — `as400server` catalog/session owner
- `src/Ipc.Web` — ASP.NET Core REST + IWS-style program services + 5250-over-websocket
- `src/Ipc.WebUi` — Vite SPA admin console
- `src/Ipc.Bridge` — interop/migration CLI
- `src/Ipc.Installer` — provisioning + first-run wizard + systemd

See `docs/*` for architecture, object model, and command/RPG/DDS specifications.

## Build & test

```
dotnet restore iSeriesPC.sln --locked-mode
dotnet build iSeriesPC.sln --no-restore -c Release
dotnet test iSeriesPC.sln --no-build -c Release
```

Build with SDK 8.0.425 (pinned in `global.json`). See [build/runtime policy](docs/build-baseline.md).

## Run the shared terminal host

```sh
dotnet run --project src/Ipc.Server -c Release -- --data-dir /path/to/instance
# In another terminal, under the same OS account:
dotnet run --project src/Ipc.Console -c Release -- --data-dir /path/to/instance
```

`as400menu` connects to the shared server by default. `--server SOCKET` selects an
explicit Unix socket; `--standalone` keeps the embedded development mode. Use
`--migrate-only` for [catalog migration and recovery](docs/catalog-migrations.md).
New catalogs generate a private initial-password file; see [authentication and recovery](docs/authentication.md).
The socket defaults to owner-only, with explicit group access configuration. Real SSH/PAM
integration is tested; multi-account host provisioning and full web/batch feature delivery remain open;
this is not yet a production deployment. See [architecture](docs/architecture.md).
