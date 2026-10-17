# Completion progress — 2026-10-17

The user requested completion of all 106 PLAN.md checklist items. **36 items are
complete and 70 remain open.** The app is under construction. PLAN.md is intentionally
Git-ignored; this tracked note preserves the implementation checkpoint and evidence.

## Completed checklist items

1. C01: client ODBC/JDBC scope decision — included; PostgreSQL-protocol reporting
   gateway with psqlODBC/pgJDBC and named reporting-client acceptance targets.
2. C01: IBM MQ scope decision — included as an adapter to an existing queue manager;
   native MSGQ/DTAQ remain separate. Research-annex mappings corrected.
3. C01: pinned SDK/build baseline — SDK 8.0.425, .sln, NuGet lock files, matching
   tracked CI, and SDK 8 validation. Original baseline: 223 passing tests.
4. C02: hosted runtime lifecycle — shared terminal server, ownership/readiness,
   independent authenticated sessions/jobs, shutdown/disposal, crash recovery,
   and cancellation of an executing CL program.
5. C02: catalog migrations — ordered transactional history, schema 1/2 upgrades,
   protected pre-upgrade backups, restore/re-upgrade, rollback, concurrency, and
   rejection of invalid/newer/inconsistent version metadata.

6. C01: requirement/command/API/DDS/RPG inventory, canonical-name corrections, aliases,
   unsupported diagnostics, generated catalog and command validation regression tests.
7. C01: finite release contracts for languages, DDS, SQL, CCSID, SAVF, SOAP and host
   services; optional inbound SOAP included, all required packages retained.

8. C02: shared owner/identity contract across terminal, typed API, HTTP and batch;
   actual server-owned execution, durable requests, queue claims and job effects tested.

9. C02: WP1 runnable object foundation — transactional file/schema and current domain
   payload lifecycle, profile/work/AUTL catalog synchronization, job-scoped menus and
   named/parsed SQL dependencies, ownership and persisted signature metadata.

10. C02: rotating history/audit/job logging and durable event delivery/retention — schema
    9 transactional outbox, consumer acknowledgement/replay, identity correlation,
    maintenance, bounded private mirrors and explicit logging failure behavior.

11. C03: service authorization and shared caller context across existing terminal/HTTP/
    batch execution; direct file/catalog/admin/job denial and revocation checks.
12. C03: authority precedence and adopted CL/RPG call stack, type isolation, supported
    QSECURITY cases, and exception unwinding; see security-model.md for boundaries.

13. C03: profile/PAM authentication, atomic lockout/credential updates, generated private
    enrollment and recovery, policy-sized fields and real OpenSSH/Unix-peer/PAM acceptance.

14. C03: Kerberos/SSSD and EIM identity bindings, LDAP TLS/state checks, guarded mapping
    commands and live revocation; real isolated MIT KDC/OpenLDAP/SSSD/PAM acceptance.

15. C03: encrypted MFA enrollment, replay prevention, one-use recovery, shared SSO tokens,
    live session expiry/revocation and terminal/browser management; see mfa-sessions.md.

16. C03: certificate/key inventory, encrypted private-key import and rotation, purpose trust,
    renewal/bindings, signed CL/RPG/menu snapshots and transactional code-package admission;
    independently verified with OpenSSL. See certificates-signatures.md.

## Implemented supporting work

- A tracked 163-requirement compatibility inventory and generated source-surface
  report, now extended by 304 normalized command/API entries and release contracts.
- `Ipc.Session` now owns the former console controllers and command orchestration;
  `Ipc.Server` supplies `as400server`. `as400menu` connects to it by default, with
  explicit `--standalone` development and `--migrate-only` maintenance modes.
- Type-qualified memory-store identity and authority lookup; same-name file/program
  isolation and denied missing-object checks.
- Atomic persisted job-number allocation, correct session-specific DSPJOB, hidden
  display-field redaction, and abnormal completion for interrupted server sessions.
- Schema 4 durable batch requests, atomic claims and job-log sequences; schema 5
  type-qualified dependency edges, transactional object relocation/copy/delete and
  file create/delete. Copies now preserve indexed/constrained/generated/trigger payloads;
  parsed SQL references guard deletion. LF implementation remains C08.
- Profile seeding now preserves passwords, disabled status and configuration on restart.
- Job library-list/current-library resolution, ownership on creation, library-list
  commands and supported system-value changes. Deleted/replaced programs cannot remain cached.
- Filesystem descriptor store with exclusive ownership, atomic snapshots and signature
  metadata; cryptographic verification and full-domain payload integration remain open.
- CL/RPG instruction-boundary cancellation and corrected CL sibling-call depth.
- Real Linux PTY raw input and exact terminal restoration, including avoiding
  .NET's interactive console input buffering.

17. C04: continuous CL/RPG/native batch execution, queue ownership and limits, routing,
    JOBD/CLS defaults and persisted admission attributes; real process ABI and side effects.
    See batch-work.md.

18. C04: atomic cross-process allocation/claiming, persisted claim identity and execution
    outcomes, crash recovery without replay, immutable atomic completion and safe retry rules.

19. C04: shared interactive/batch/communication/system lifecycle, durable cancellation,
    native process termination, activation groups, CPU/thread/process accounting and queue
    bindings. Includes real descendant termination, PTY and OpenSSH/PAM acceptance.

20. C04: job-owned object/record locks, compatibility, waiting/deadlocks, CL allocation and
    inspection, actual file access enforcement and transaction/job/crash release; independent
    process and failed-commit acceptance. See locks.md.

21. C04: schema 16 LDA/group/PDA storage, atomic batch inheritance, owned call environments,
    overrides and actual RPG shared open paths; nested calls, explicit close, restart and
    failed submission verified. See job-environment.md.

22. C05: Ipc.Dsp fixed-column compiler, persisted panel definitions, typed input/output and
    CF/CA/indicator/cursor handling; CRTDSPF/source-member compilation, message constants,
    numeric/date validation and actual terminal menu preview. See display-files.md.

23. C05: per-open subfiles, READC edit flags, page validation/fold, hidden keys and row
    indicators, program-message display lookup and modal WINDOW editor restoration;
    persisted compiler/runtime and terminal preview use the same file session.

24. C05: renderer cursor/security snapshots, Insert/Replace/edit navigation, bounded VT
    parsing, bracketed paste, F1–F24/attention and live terminal-size guards; real compiled
    DDS PTY acceptance at both screen sizes. See terminal-keyboard.md.

25. C05: persisted UIM panel groups, declarative links/index, DDS HLPARA/HLPPNLGRP,
    shared menu/command/display help, live authority/signature checks and unsent input
    restoration; actual terminal navigation verified. See shared-help.md.

26. C05: DDS design model and STRSDA records/fields/windows/subfiles/menu workflow,
    atomic revision-checked source saves, shared compiler commands and real PTY
    save/compile/reopen/edit/run acceptance. See screen-design.md.

27. C06: controlling-PTY setup/failure boundary, interruptible native input, signal/EOF
    cleanup and running-command cancellation; real OpenSSH local/remote PTYs, F1/F3,
    resize, disconnect cleanup and exact restoration. See terminal-lifecycle.md.

28. C06: authenticated initial program/environment/menu startup, failure cleanup and
    initial signoff; schema 17 terminal families, bounded group jobs, SysReq alternate
    groups, retained screens, ownership, rollback and restart cleanup. Real PTY restores
    a dirty panel after three-job navigation. See interactive-jobs.md.

## Verification

SDK 8.0.425 / runtime 8.0.31, Ubuntu 26.04:

- Locked restore succeeded; Release build succeeded with zero warnings/errors.
- **594 tests passed, 0 failed, 0 skipped** after the final code changes.
- Real PTY smoke passed: sign-on, raw/no-echo input, F3 without newline, and exact
  original settings restored.
- Compiled display PTY acceptance passed at 24x80 and 27x132, including all 24
  function keys, insert/replace/delete, paste, cursor, resizing, typed input and
  shared menu/command/DDS help with links/back/index and unsent input restoration,
  plus SDA save/compile/reopen/edit/replace/run.
- Real loopback OpenSSH/PAM smoke passed, including mismatched Unix identity rejection.
- Isolated Ubuntu 24.04 MIT Kerberos/OpenLDAP TLS/SSSD/PAM integration passed, including
  scheduled live revocation, hostname rejection, principal conflicts and provider outages.
- Compatibility report validation and `git diff --check` passed.
- Local TRX: `tests/Ipc.Core.Tests/TestResults/completion-foundation.trx`.

The verified SDK executable on this workstation is
`/home/matthewp/.local/share/iseriespc-dotnet/dotnet`; the system SDK remains 10.
Changes are in the working tree and have not been committed. Existing RPG changes
were retained. This continuation adds catalog/diagnostic contracts, shared HTTP/headless/
batch execution and object persistence improvements, without replacing the RPG work.

## Next open work

Continue with C07 release command categories and their owning service dependencies. Follow the dependency order in PLAN.md.

The ODBC/JDBC and MQ implementations are still required; their approved decisions
do not mark those integrations implemented. Web/DSPF/spool/IPC/IFS/save/restore/API/
debugger/host-adapter/installer/migration/Docker/release work remains open. Actual
SSH multi-account provisioning and Ubuntu 24.04 host/installer qualification remain
unverified. Do not infer package completion from the passing unit suite.

## Pending external acceptance information

A text question is pending about availability of a real IBM MQ test queue manager
and IBM i/SAVF fixtures. No connection details or fixtures were supplied in this
continuation. That evidence is required for C11/C13/C17/C22; it does not block current
local foundation work and does not justify checking those integrations complete.

## Immediate implementation gaps

The first open item is C07 release command categories. Current catalog schema is 17;
runtime lock sidecar schema is 1. All 622 tests pass; locked restore, Release build,
compatibility checks and PTY smoke pass. The earlier OpenSSH/PAM, isolated Ubuntu
24.04 Kerberos/LDAP/SSSD and Playwright account checks remain recorded above.
The native authentication fixture does not certify a site's directory policy or
multiple OS-account installer provisioning.
New domain functionality (LF, display, spool, journals, etc.) must use the same
transaction/dependency/outbox guarantees when implemented in its owning checklist
items. New domain payload codecs must extend the signed-content admission boundary.

The server's disconnect monitor uses a cancellable socket peek. A previous
Poll+Available implementation raced with the protocol reader and incorrectly ended
live sessions; it was replaced and the complete 303-test suite plus PTY smoke passed.

29. C06: canonical menus and registered command selectors, metadata-driven F4 and shared
    help, reusable paged work screens, confirmed row actions, refresh, bounded details,
    and atomic profile administration. Six work-with tests and three F4 tests; real
    PTY F4/help/create/delete/refresh/page/signoff acceptance. See operator-menus.md.

30. C06: strict JSON/SDA menu creation and editing, atomic replacement, copy/delete,
    signed option restrictions and live revocation, prompt reauthorization, and bounded
    navigation. Six custom-menu tests, signature checks, and real PTY JSON menu lifecycle
    acceptance pass. Menu command entry now scrolls a 32 KiB buffer. See custom-menus.md.

31. C07: persisted command definitions and CRTCMD/DSPCMD; shared compiler/binder,
    positional/keyword lists, defaults/types/choices, F4 order and UIM help, live
    authority/signatures, generic-update dependency integrity and signed packages.
    Native protocol 2 preserves binary CPP buffers in UTF-8 jobs. All 622 tests pass;
    real PTY command compilation/prompt/execution/reference and OpenSSH acceptance pass.
    See command-definitions.md for the finite compiler contract and separate runtime gaps.

C07 command categories in progress: DLTLIB with transactional contained-object deletion,
work-screen confirmation, authority/lock/dependency tests and real PTY acceptance;
CRTDTADCT with catalog identity and atomic live authorization-list attachment.
See object-command-progress.md. This does not close the category item.

The object-command checkpoint passed 629 tests and a rebuilt real PTY run. Simple
CRTLF/ADDLFM now have compiler, live physical reads/writes, ordered select/omit,
member binding, authority and physical lock tests; broader LF functionality is
in progress. See logical-files.md. No additional checklist item is closed.

The next C07 checkpoint passes 663 tests, real LF command PTY acceptance and OpenSSH.
Simple LFs now include DDS keyword continuation, exact decimal/CCSID key fixtures,
maintained shared indexes, filtered unique constraints and ownership cleanup,
MBR/DTAMBRS/MAXMBRS, numeric bounds and atomic PF relocation with dependent-lock
protection. Join/multi-format/full DDS and the remaining command categories are open.

CPYF now has transactional bounded staging, explicit add/replace and map/drop modes,
source LF filtering/order, null preservation, rollback for late trigger failures,
self-copy snapshots and member selection by creation time. Its native option set
is still partial; see file-copy.md. The checkpoint passes 671 tests and real LF-to-PF copy PTY acceptance. This continues the same open C07 category item.

C07 file-service dependencies now include buffer layout version 2, independent
packed/zoned/signed-binary/IEEE/CCSID/ISO-date fixtures, VARLEN prefixes, null
indicators and legacy metadata adaptation. PF and LF keys use exact comparisons;
large integral decimals use TEXT in new members, with a guard against rounding
writes in legacy INTEGER storage. Numeric/null validation and complete mutation
keys are enforced. The 702-test checkpoint passes, including single-precision storage bytes; the rebuilt display PTY passes. Broader DDS and database work remains open.

Native physical DDS checkpoint: 708 tests pass; Release build has no warnings.
CRTPF reads native columns with correct B/F storage widths, implicit P/A types,
ISO date/time fields, current datetime defaults, bounded native storage and source
locations. Real terminal CRTPF → CPYF → DSPPFM acceptance passes. The legacy column
layout remains a documented extension. Wider DDS keywords and formats remain open.

32. C08: independent record-buffer validation is complete. Packed/zoned signs and
    precision, binary boundaries, CCSID byte widths, ISO date/time/timestamp bytes,
    variable-length/null layouts and exact PF/LF decimal operations have independent
    fixtures. Overflow/malformed values fail explicitly. Legacy large-decimal writes
    that would round are blocked. The 716-test checkpoint also covers schema-18 nullable
    compound-key ordering, uniqueness and transactional upgrade/rollback. Broader DDS,
    schema migration, SQL and API-specific ABI work retain their own open items.

Current C07/C08 file checkpoint: 737 tests pass, Release build has zero warnings,
and real terminal CRTPF MBR(*NONE) → CHGPF MAXMBRS → ADDPFM → CPYF → DSPPFM passes.
Native DFT values are validated and persisted, including hex characters, nullable
values and ISO datetime constants; PF/LF/CPYF output uses the declared defaults.
Physical UNIQUE constraints have separate ownership from logical constraints and
survive file copies. CRTPF uses the job CCSID, retained in PF/LF catalog descriptors.
Member creation rejects duplicates, respects persisted limits under concurrency,
and rolls back if an unregistered member table already exists. CHGPF currently
supports MAXMBRS only; schema changes and its other native options remain open.
No additional broad command-category or database-semantics item is checked off.

33. C07: CL preprocessing is complete as an explicitly documented extension, separately
    scoped from RPG directives. Includes and conditions share a bounded authority-aware
    source resolver; original source snapshots and include/location maps are persisted
    with the expanded program and bound to its text hash. CALL/copy survive source edits
    and deletion. Compiler/runtime errors preserve locations; cycles, malformed scopes,
    stale/invalid maps, oversized expansion and cancellation fail explicitly. Stream
    sources use strict UTF-8 and reject Linux FIFOs without blocking. All 756 tests pass,
    with real CRTCLPGM → CALL conditional/include PTY acceptance. See source-preprocessing.md.
    The current Release build has zero warnings; C07 interpreter and broad command-category
    tasks remain open; RPG preprocessing is completed below and debugger integration
    remains in C15.

34. C09: bounded RPG source preprocessing supports `/COPY`/`/INCLUDE`, `/DEFINE`/
    `/UNDEFINE`, `/IF`/`/ELSEIF`/`/ELSE`/`/ENDIF`, and listing-only `/TITLE`/`/EJECT`/
    `/SPACE`. CRTBNDRPG shares authority-aware member/strict UTF-8 stream resolution
    and hash-bound compiled snapshots/maps with CL while retaining RPG grammar.
    QRPGLESRC defaults, library-list/current-library searches and relative streams
    are tested. Compiler/runtime diagnostics retain original locations and include
    chains; native errors retain CPF metadata. Existing programs survive source
    edits, deletion and revoked read authority; recompilation checks live access.
    54 new regression cases and terminal CRTBNDRPG → CALL acceptance pass. Broader
    RPG/ILE syntax, embedded SQL and debugger controls remain open. See
    [source preprocessing](source-preprocessing.md#rpg-directives).

35. C11: job data-area storage lifecycle is complete, reusing the audited C04
    implementation and its documented routing-PIP extension. New tests cover
    encoded blank CHAR storage (1024/512/2000 bytes), atomic submission/restart,
    initialization payload bounds and claim rollback, shared GDA with separate
    LDAs/groups, cross-job denial, process concurrency, cancellation and cleanup.
    Missing GDA/PDA now report CPF1046/CPF1072 through CL MONMSG. Expanded terminal
    acceptance verifies data across named groups and secondary partitions while
    preserving an edited panel. See the requirement-by-requirement evidence in
    [job environments](job-environment.md#c11-storage-completion-evidence).
    Native prestart protocols and wider WP12/API work remain separate and open.


C07 interpreter checkpoint: 806 tests pass and the Release build has no warnings.
Structured IF/ELSE, DO/DOWHILE/DOUNTIL/DOFOR, labelled LEAVE/ITERATE, SELECT and
RETURN execute through bounded compiled expressions with typed scalar values and
job-CCSID comparisons. Tests cover changing loop bounds, zero increments, nested
exits, conversion/error behavior and cancellation. Real CRTCLPGM → CALL loop/select
PTY acceptance passes. See cl-runtime.md. No additional checklist item is checked;
parameter ABI/writeback, MONMSG and file/procedure integration remain open.


Current C07 interpreter checkpoint: 848 tests pass, Release build has no warnings,
and real terminal acceptance covers typed loops/select, CL reference writeback and
nested escape recovery with MONMSG generic IDs and comparison data. Scalar packed,
integer, logical and character argument buffers have independent fixtures; aliased
CL parameters share changes through RETURN/error, and the RPG host receives CL
updates. MONMSG scope, recovery groups, ignore semantics, source/ID/data propagation,
bounds and cancellation are covered. Full-suite regression exposed a headless
signoff race: the final acknowledgement now uses the connection token after the
job ends. Repeated HTTP command/signoff and session tests pass. The checklist
remains 33 complete / 73 open; the native constant ABI, file I/O, broader message
services and procedure/external bindings still belong to the open C07 item.


C07 database file checkpoint: all 877 tests pass. DCLF stores validated compiled
layouts; RCVF/CLOSE use live authority, bounded sequential cursors and CL shared
open paths. Null/default/error behavior, binary ranges, date/time layouts, EOF,
reopen and cleanup have regression tests. An 8,300-record command releases record
locks between reads; native SQLite progress cancellation interrupts running SQL.
The checklist remains 33 complete / 73 open. Display/raw file storage, broader
messages, native constant ABI and procedure/external bindings remain open.


C07 cross-language checkpoint: 887 tests pass and Release builds without warnings.
CL-to-RPG scalar references preserve aliases and changes before errors, then
release borrowed cells. Real native version-2 processes return typed buffers;
invalid output fails before any assignment. Terminal CL→RPG→CL writeback passes.
Native aliases, constant storage rules, CALLPRC, display/raw file operations and
broader messages remain open; no additional checklist item is checked.


C07 environment checkpoint: 900 tests pass, warning-free Release build and terminal
CLENV acceptance pass. Library placement/current-library options preserve authority
and failure atomicity. Typed RTVJOBA and job-local RTVDTAARA return values through
the compiled CL frame, with byte-range and output-length checks. Explicit profile
CRTDFT survives signon; RPG default member selection now uses the first-created
member. Named data areas and broader messaging/procedure work remain open.


C07/C11 named data-area dependency: 921 tests pass, Release builds without warnings,
and terminal create→CL retrieve/change→display→delete acceptance passes. CHAR bytes,
24-digit DEC and LGL persist in validated versioned payloads. CL uses the supported
15-digit DEC range and aligns/truncates fractional positions on retrieval. Atomic
substring writes preserve concurrent updates; authority lists, locks, copies,
renames, catalog reopen and CCSID expansion failures are covered. DTAQs, USRSPCs,
message stacks/queues and the remaining C07 work stay open.

C07/C11 named message-queue checkpoint: all 955 tests pass, Release builds with
zero warnings, and the full display PTY passes including CRTMSGQ → CL inquiry/
receive/four-byte key/reply → SNDMSG/DSPMSG → DLTMSGQ. Schema 19 stores bounded
message entries and atomic inquiry/reply state; waits release database resources
and recheck authority/cancellation. Failure, concurrency, restart, quota and key
exhaustion tests pass. Raw CL CHAR support also preserves binary native buffers,
variable-length DCLF prefixes and large numeric packed fields; the prior full
936-test checkpoint covered those changes. The CHAR declaration limit is corrected
to 32767 bytes, with a separate 5000-character initial-value limit.
The checklist remains 33 complete / 73 open: program/job message stacks, richer
delivery, display files, CALLPRC and other C07/C11 requirements still need work.
See [message queues](message-queues.md) and [CL runtime](cl-runtime.md).


C07/C11 program-message and operator checkpoint: all 975 tests pass, locked restore
and warning-free Release build succeed, and full display PTY acceptance passes.
Schema 20 adds job-owned call/external queues, sender copies, exact reply routing,
returned-frame protection and retained job messages. RMVMSG performs atomic
key/old/new/all/keep-unanswered removal and owning-job inactive cleanup. DSPMSG
provides full details, quoted reply prompts and confirmed removal; WRKMSGQ links
to it. Failure rollback, restricted delete authority, binary keys, default reply
routing and cross-job isolation are tested. A SQLite schema-lock regression
required the bounds-checked managed provider 3.0.5; its reproducer and full suite
pass with the locked dependency graph. The checklist remains 33 complete / 73
open; full exception propagation/delivery and remaining C07/C11 work are pending.


C07 runtime-exception checkpoint: all 980 tests, warning-free Release build and
full display PTY pass. Arithmetic/command errors are queued before MONMSG recovery,
and unmonitored errors propagate once per caller with original bytes/ID/severity.
Explicit escapes reuse their delivery reference; cross-job references are denied.
A full queue produces a monitorable delivery failure without recursive reporting.
Terminal CRTCLPGM→CALL→division error→MONMSG→RCVMSG verifies MCH1211. The checklist
remains 33 complete / 73 open; ILE exception state and remaining C07/C11 work persist.


C07 message return-layout checkpoint: 994 tests pass, warning-free Release build
and full display PTY pass. Schema 21 records handled exceptions and reply/default
origins without changing stored bytes/keys. RCVMSG supports explicit sender-copy
selection, two-byte RTNTYPE and KEEPEXCP receipt; invalid output/type declarations
fail before consumption. The terminal verifies copy 06, entered reply 21 and
MONMSG-handled escape 15. Historical reply origins that were never stored cannot
be reconstructed and are documented. No additional checklist item is closed.


C07 raw data-area checkpoint: all 998 tests, warning-free Release build and full
display PTY pass. Same-CCSID RTVDTAARA/CHGDTAARA transfers preserve all byte values
through named and local areas, pad by bytes and support raw substrings. Cross-CCSID
conversion fails before assignment; invalid display text falls back to hexadecimal.
Null service changes cannot reset an existing area. The checklist remains
33 complete / 73 open.


C07 byte-function checkpoint (2026-09-16): 1023 tests, warning-free Release build
and full display PTY pass. Hex constants preserve opaque bytes and CHAR inferred
lengths count job-encoded bytes. %SST/%SUBSTRING and %BIN/%BINARY support reads
and atomic CHGVAR writes, including the job's local data area. Independent signed
fixtures, overlapping ranges, padding, invalid encodings, overflow and bounds are
covered. Terminal CRTCLPGM→CALL verifies local-area substring and signed binary
read/write. The checklist remains 33 complete / 73 open.


C07 command-argument checkpoint (2026-09-16): 1033 tests, warning-free Release
build and full display PTY pass. Qualified names, list elements, data-area
dimensions and multiple message destinations resolve variables consistently.
Variable contents cannot add command syntax, quoted literals retain their value,
and CMD/CPP bindings preserve mixed case and apostrophes. Size/nesting limits and
missing/invalid variables fail before dispatch. No additional checklist item is closed.


C07 CALL and cleanup checkpoint (2026-09-16): 1081 tests, warning-free Release
build and full display PTY pass. Direct/compiled CALLs share default CHAR/DEC and
explicit CHAR/DEC/INT/UINT/LGL/FLT temporary layouts; independent packed, binary
and IEEE fixtures and protocol-2 process captures agree at CCSIDs 37/1208.
Expressions use private temporaries, references retain aliases, invalid layouts
fail before execution and malformed temporary responses prevent reference writeback.
CL CALL limits are 255 arguments and 1 MiB. Batch routing uses the shared typed
execution path. Cleanup continues after a failing path/frame close, restores the
parent and permits safe retries/reopening. The checklist remains 33 complete /
73 open; procedure bindings, display I/O and wider command/message work remain.


C07 sender checkpoint (2026-09-16): 1101 tests, warning-free Release build and
full display PTY pass. Schema 22 retains original sender identities/timestamps
across job end and exception forwarding. RCVMSG returns independent native
SHORT/LONG layouts, preserves queue state on invalid output and returns the
sender-copy correlation key for replies. Terminal MONMSG→RCVMSG verifies the
originating program name. The checklist remains 33 complete / 73 open.


C07 QCMDEXC checkpoint (2026-09-16): 1128 tests pass, followed by expanded real
HTTP acceptance, warning-free Release build and full display PTY. A versioned
QSYS/QCMDEXC program dispatches dynamic commands with the same identity and job
state across direct CALL, compiled CL, RPG and the HTTP command bridge. Independent
packed lengths, byte limits, MONMSG, authority/signature revocation, recursion and
cancellation are covered. Existing seeded/custom programs are preserved. Native
prompting, wider APIs and debugger dispatch remain open. The checklist remains
33 complete / 73 open.


C07 message-description checkpoint (2026-09-16): 1166 tests, warning-free Release
build and full display PTY pass. Version-2 MSGF payloads retain legacy literal
text while adding native IDs, first/second-level templates, severity, default
metadata and bounded substitution fields. ADDMSGD/CHGMSGD/RMVMSGD/DSPMSGD/DLTMSGF
and atomic RTVMSG share the store; packed/binary/varying/CCHAR reference fixtures,
legacy preservation, authority and concurrent changes are covered. Predefined
queue delivery and raw exception comparison data are the next dependency. The
checklist remains 33 complete / 73 open.


C07 predefined-message delivery checkpoint (2026-09-16): all 1187 tests pass,
with no skipped tests, a warning-free Release build and full display PTY acceptance.
Schema 23 preserves existing queue state and adds bounded snapshots of descriptions,
replacement bytes and message-file identity. SNDPGMMSG uses live authority and
snapshots text/help, severity and inquiry defaults; MONMSG compares raw text/hex
replacement prefixes. RCVMSG returns help, replacement bytes, lengths and file/CCSID
metadata, with atomic conversion/layout failures. Independent tests cover CCHAR
conversion, edits/deletion/recreation, restart, migration, authority and metadata
quotas. The terminal fixture verifies packed substitution data through a nested
escape, MONMSG and RCVMSG. The old compiler test rejecting custom MSGF references
now rejects an invalid message identifier; valid custom files have delivery tests.

Work paused after that checkpoint and resumed on 2026-10-16 within C07. Display
file I/O, procedure bindings, wider command/message delivery and the remaining
completion categories remain unfinished. No broad checklist item was marked
complete for that partial checkpoint.


C07 inquiry-reply checkpoint (2026-10-16): all **1239 tests pass**, with zero
failed or skipped tests; the Release build has zero warnings/errors and the full
display PTY acceptance passes. TYPE/LEN/VALUES/SPCVAL/RANGE/REL rules persist in version-3 message descriptions and version-2 queued snapshots.
Version-1/2 descriptions and version-1 snapshots retain their earlier semantics.
Explicit replies validate inside the reply transaction; CPF2422 preserves all
inquiry/copy state, while accepted replies map special values and return type 22.
Defaults use the sent snapshot and retain codes 23/24. CHGMSGD merges omitted
fields under its write transaction and rejects incompatible changes atomically.

52 new cases cover exact decimal and independent EBCDIC comparisons, type/length
rules, compact special lists and escaped quotes, mapping, immutable metadata,
authority, CL MONMSG recovery, output codes, default removal, invalid encoding,
legacy descriptions and durable restart after file edits/deletion. The real
operator PTY flow rejects an invalid F4 reply, preserves the inquiry, accepts a
mapped retry and checks RTNTYPE 22 through compiled CL. The compatibility matrix
and message/CL documentation describe the finite contract and open limitations.
Checklist remains **33 complete / 73 open**; the broader C07/C11 items remain open.


C07/C11 data-queue checkpoint (2026-10-16): durable local FIFO/LIFO/keyed DTAQs
now expose CRTDTAQ/DLTDTAQ and the SNDDTAQ/RCVDTAQ/CLRDTAQ extension commands.
Schema 24 retains opaque entries with raw byte ordering, FIFO duplicate keys,
transactional receive/peek/clear, 4096-entry/16-MiB limits and sender snapshots.
Waits release transactions and respond to cancellation, authority revocation and
queue replacement. Job allocations, library defaults/AUTLs, and object copy/move/
delete are integrated. Copies get new identities and start empty.

46 new tests cover service and compiled CL behavior, byte fixtures and bounds,
receiver atomicity/MONMSG, independent producer/consumer processes, killed waiters,
disk reopen, schema-23 migration and contended-write cancellation. Real display
PTY acceptance creates a keyed queue, compiles/calls CL with raw data/key buffers,
peeks/receives/clears, displays bounded hex output and deletes the queue.
See [data queues](data-queues.md) for the finite contract and destructive-receive
crash semantics. Native API buffer bindings, DDM, journals and USRSPCs remain open.
Checklist remains **33 complete / 73 open**; broader C07/C11 items are unchecked.


The first full data-queue regression run exposed a startup-scheduling timeout in
SqliteCancellationTests: confirmation of SQL startup from its
thread-pool worker missed the five-second deadline, and the failure path skipped cancellation before
connection cleanup. The fixture now uses a dedicated SQL worker, a signal from the executing query and unconditional cancellation/await during cleanup. The actual running-VM
interrupt and connection-reuse assertions remain in place; the 47-case combined
queue/cancellation filter passes after the correction.


Final data-queue verification: **1285 tests pass**, zero failed/skipped; the
Release solution build has zero warnings/errors, full display PTY acceptance
passes and the generated compatibility inventory validates. The prior SQLite
cancellation fixture failure is resolved by the deterministic worker/startup and
cleanup correction described above. Broader checklist remains 33 complete / 73 open.


C11/C14 native data-queue API checkpoint (2026-10-17): created Ipc.Api and bound
QSYS/QSNDDTAQ, QRCVDTAQ and QCLRDTAQ to the existing durable queue service.
Versioned IPCAPI program objects preserve normal authority, signing and call scope;
startup leaves existing programs intact. Checked packed fields and complete optional
groups support input prefixes, keyed selection, peek/removal, seconds/milliseconds,
truncation and zero-size data receipt. Receiver assignments are prepared before
dequeue, preserving entries and other outputs when sender/layout conversion fails.
Sender headers use independent packed vectors; error receivers use big-endian
lengths, partial message IDs and unchanged tails.

57 new tests pass for CCSIDs 37/1208, raw bytes, maximum entry/receiver sizes,
immutable and aliased outputs, short/partial/error receivers, MONMSG, cancellation,
live queue/API authority and signing trust. An RPG caller exercises all three APIs
through a CL wrapper with typed receivers. Direct RPG receive borrowing, exact IBM
error distinctions, DDM, journals, USRSPCs and broader C14 APIs remain unfinished.
The plan and inventory retain **33 complete / 73 open** broad checklist items.

Final API checkpoint verification: **1342 tests pass**, zero failures/skips; the
Release solution build has zero warnings/errors, locked restore passes, full
display PTY acceptance passes with the new native API flow, and the generated
compatibility report validates. Existing NuGet versions/hashes are unchanged.
The startup fixture now includes the three new seeded programs in its object
count, checks all four IPCAPI manifests, and disposes its in-memory system.
The broad checklist remains **33 complete / 73 open**.


C09/C14 RPG scalar API checkpoint (2026-10-17): direct CALL/PLIST and
unprototyped CALLP/CALLB calls now borrow fixed CHAR and packed/decimal fields
for built-in API targets. The host resolves authority/signing before requesting
scoped cells; ordinary CL/RPG/process calls retain semantic argument/writeback.
Repeated fields and outer CL aliases keep one identity. Cells and lazy factories
expire on success, failure or cancellation. Queue output conversions and outer
receiver validation run before dequeue; outputs update live storage directly.
The job CCSID is read per call; argument counts and aggregate bytes are bounded.

Raw native text such as *BLANK bypasses RPG assignment-keyword interpretation,
and outer character buffers detach into text for retained storage. QCMDEXC now
checks typed DEC(15,5) declarations on these paths, and its earlier semantic
fixture uses that required declaration. Prototype calls retain their semantic
adapter; prototype receivers, DS/arrays and full native byte storage remain open.
42 new RPG API tests pass; the broad checklist remains **33 complete / 73 open**.

The first full RPG API regression exposed a startup timeout in the existing
RPG cancellation fixture: its thread-pool loop did not signal startup within five
seconds, and the failure path skipped cancellation. CL/RPG cancellation fixtures
now use dedicated workers, first-statement signals and unconditional cancellation/
await during cleanup. The RPG fixture also includes an actual ON-ERROR handler
and asserts cancellation bypasses it. Expanded terminal acceptance already passes
for RPGDQAPI's direct keyed send/receive/clear without a CL wrapper.

Final RPG API verification: **1384 tests pass**, zero failures or skipped tests;
42 new cases cover direct CL-free RPG calls at CCSIDs 37/1208, key/sender/error
outputs, truncation/peek/timeout, input/output conversion before queue mutation,
aliases/lifetimes, count/byte bounds, unsupported storage, cancellation, outer
receiver rejection and authority/MONMSG propagation. The corrected cancellation
filter passes all three cases. Locked restore and the Release solution build pass
with zero warnings/errors; full display PTY acceptance includes direct RPG keyed
queue calls, and generated compatibility evidence validates. Broader checklist
remains **33 complete / 73 open**; prototype receivers, full native RPG storage
and the wider C09/C14 runtime remain unfinished.


C09/C14 scalar prototype API checkpoint (2026-10-17): prototyped CALLP/CALLB
now lend actual scalar fields to built-in APIs after exact formal/actual count,
kind, length and scale checks. Missing/ambiguous metadata and unsupported formal
options fail before dispatch. Literals/expressions use immutable character/packed
temporaries with formal layouts; the API then checks its own signature. Named
formals use private storage and cannot shadow actual variables. Multiple unnamed
prototypes retain independent definitions. Ordinary targets keep semantic formal
coercion/writeback, prepared lazily when the host reads their arguments.

Native prototype calls leave formal temporaries unchanged while updating actual
receivers; aliases and expiration preserve the existing scalar scope. The call
parser now preserves colons in literals/nested expressions and doubled apostrophes.
The expanded terminal fixture includes RPGPRAPI's prototyped keyed queue lifecycle.
CONST/VALUE/OPTIONS, arrays/DS/native byte storage and general ILE binding remain
open. The broad checklist remains **33 complete / 73 open**.

Final prototype checkpoint verification: **1433 tests pass**, zero failures or
skipped tests, including **49 new prototype cases**. Reference vectors and source
programs cover QCMDEXC and the three queue APIs, literal/field layouts, exact
formal matching, immutable receivers, real key/sender/error outputs, peek/truncation,
aliases and expired lifetimes, ordinary copied fallback, ambiguous metadata,
authority/MONMSG, signing-trust revocation and nested/quoted argument parsing.
Locked restore and the Release solution build pass with zero warnings/errors.
Full display PTY acceptance passes with RPGPRAPI's prototyped keyed lifecycle;
the generated compatibility inventory validates. Broader checklist remains
**33 complete / 73 open**; general prototype modes, native storage and ILE binding
are unfinished.


C09 RPG preprocessing final verification (2026-10-17): **1487 tests pass**, zero
failures or skips, including **54 new preprocessing cases**. Locked restore and
the Release build pass with zero build warnings/errors. Full display PTY acceptance
passes, including terminal CRTBNDRPG creation and CALL of nested conditional copy
members. Generated inventory validation and whitespace checks pass. G04 is supported
for the documented CL extension and bounded RPG grammar; wider WP10 requirements
remain partial. Listing-only programs compile and call successfully with empty
executable text, and source-location reporting preserves CPF type/severity as well
as message IDs. The checklist is now **34 complete / 72 open**.


C09 syntax diagnostics checkpoint (2026-10-17): unsupported RPG operations,
specifications and D-spec type codes now fail with original source locations,
including include chains, before any program body executes. Fixed layout recognition
no longer mistakes DSPLY DATA/CALLP PRIVATE free text for specifications. Fixed
BEGSR/EXSR/GOTO/TAG names are normalized from their single supplied operand,
so separate subroutines retain separate names.

A structural validation pass rejects crossed/unclosed blocks and routine boundaries,
duplicate branches/routines/procedures/scoped labels, invalid mode markers, control
operands that were previously ignored and loop control outside loops. Control nesting
is bounded to 256 blocks. The generated opcode inventory now shows fixed and free
recognition separately and checks that fixed spellings fit the seven-column field.
85 new cases cover rejection and valid mixed source, locations, nesting limits,
scoped labels, distinct fixed routines and compile/create/call effects. Terminal
acceptance verifies the corrected free calls/display and that rejected RPGBAD source
creates no program. The wider declaration/keyword/expression/type/indicator/runtime
audit remains open; checklist progress stays **34 complete / 72 open**.

Final syntax checkpoint verification: **1572 tests pass**, zero failures or skips,
including **85 new diagnostics cases**. Locked restore, the zero-warning/error
Release solution build, full display PTY including the corrected free calls/display
and invalid compilation rejection, generated inventory and whitespace checks pass.
The broad checklist remains **34 complete / 72 open**.


C11 job data-area closure verification (2026-10-17): **1599 tests pass**, zero
failures or skips, including **27 new lifecycle/CL/process cases**. The initial
audit revalidated existing automatic LDA, GDA and routing storage, then corrected
unavailable-area error identities and completed missing acceptance evidence.
Locked restore, zero-warning/error Release build, full display PTY with shared
GDA/private LDA/secondary-group/editor checks, generated inventory and whitespace
checks pass. C11's job-area storage item is closed under its recorded C04 routing
extension; native prestart protocols and broader WP12/API work are not claimed.
The checklist is now **35 complete / 71 open**; the overall goal remains active.


C11/C14 user-space checkpoint (2026-10-17): schema-25 binary storage, explicit
byte ranges, native maximum sizes, resizing and object lifecycle now have a durable
implementation. Six QSYS user-space API adapters use signed big-endian integers,
SPCA0100, aligned attribute records and checked mutable/error receivers. The
CRTUSRSPC extension and exact-name DLTUSRSPC are executable. Replacement preserves
owner/public/private/AUTL authority and archives the old payload in QRPLOBJ, with
domain-policy deletion and exclusive-server startup cleanup. Storage/ABI acceptance
covers raw bytes, concurrency, locks, cancellation, migration, rollback and restart.
See [user spaces](user-spaces.md) for contracts and IBM references.

QTEMP job-private objects, generic deletion names, native pointer/list behavior,
full CPF/lock distinctions and domain enforcement on generic relocation remain
open. The broad C11/C14 items remain unchecked: **35 complete / 71 open**.

Final user-space verification (2026-10-17): all 1656 tests pass with zero failures/skips, including 57 new storage/ABI/restart cases. Locked restore, zero-warning/error Release solution build, full display PTY including the native binary user-space lifecycle, generated inventory and whitespace checks pass. Checklist remains 35 complete / 71 open.


QTEMP namespace checkpoint (2026-10-17): schema-26 runtime-owned private libraries initialize atomically with jobs and for existing live jobs after upgrade. Catalog, user spaces, typed data areas, STD queues, inline CL program objects, authority and allocations resolve the executing job; even privileged foreign-job backing-name access is denied. Native return names stay QTEMP. Submission/groups receive separate namespaces. Completion/cancellation and exclusive-owner crash recovery clean payloads and bindings atomically, preserving unclaimed queued jobs. Private files and other specialized stores, generic user-space deletion, MI/list behavior and full lock/error/domain relocation fidelity remain open; broad C11/C14 items remain unchecked.

See [temporary libraries](temporary-libraries.md) for the accepted stores, IBM
references and remaining integration. Checklist stays **35 complete / 71 open**.

Final QTEMP verification (2026-10-17): all 1689 tests pass, zero failures/skips, including 25 new namespace/storage/ABI/security/process/server cases and eight additional migration-version cases. Locked restore, zero-warning/error Release build, full display PTY with QTEMP user-space lifecycle, generated inventory and whitespace checks pass. Job-history deletion acceptance now completes jobs before purging their rows, preserving namespace cleanup and non-reused job numbers. Checklist remains 35 complete / 71 open.


QTEMP file checkpoint (2026-10-17): the file store resolves the owning namespace before definitions, members, source revisions, record CRUD/keyed/sequential cursors, copy and logical paths. Private source/PF/simple LF members now compile through CRTCLPGM/CRTBNDRPG/CRTPF, with CL RCVF and RPG shared ODP consumption. Permanent LFs cannot capture private PF storage; private LF unique paths on global PFs are released or retained according to other owners. Process writers, exact decimals, two CCSIDs, source revision conflicts, privileged cross-job denial, DDL/cleanup rollback, parsed dependencies, reopen/recovery and real terminal source/data lifecycle are covered. Other private domain stores and broader C08/C11/C14 contracts remain open; checklist remains 35 complete / 71 open.

The process fixture now references Ipc.Db to exercise the actual private file
service; lock-file changes record project references only, with no package version
changes. See [temporary files](temporary-libraries.md#private-source-and-database-files).

Final QTEMP file verification (2026-10-17): all 1710 tests pass, zero failures/skips, including 21 new private file/source/member/ABI/process/recovery cases. Locked restore, zero-warning/error Release solution build, full display PTY with private source copy/DDS+CL compile/read/display/delete, generated inventory and whitespace checks pass. The process fixture lock updates add project references only; package versions are unchanged. Checklist stays 35 complete / 71 open.


User-space lifecycle checkpoint (2026-10-17): DLTUSRSPC and QUSDLTUS share generic
prefix deletion, *LIBL first-match semantics, *USRLIBL current/user search and
*ALL/*ALLUSR selectors. Broad searches never enter other jobs' QTEMP. Authorized
matches commit despite denied/allocated/dependent matches, then report CPF2117;
none eligible returns CPF2125. Native receivers, CL MONMSG and two CCSIDs are
covered. Unexpected database faults and cancellation roll back the entire batch;
committed partial results survive reopen. Exact missing objects now return CPF2105.

User-space writes acquire *EXCLRD while structural changes retain *EXCL. The
coordinator covers all 25 native compatibility combinations and record intent in
both orders; commands expose allocation/inspection/deallocation. Appending the
persisted enum value preserves the sidecar schema. Live QALWUSRDMN validates mixed
*DIR/library lists and requires both *ALLOBJ and *SECADM. Explicit *SYSTEM remains
system-domain in QTEMP. Generic copies/moves and library renames enforce domain
policy atomically; same-library rename preserves an existing space. Replacement
archival retains its documented authority-preserving cleanup. Broader C11/C14
checklist items remain open; other QTEMP stores, MI/list APIs and richer native
error data still need implementation. See user-spaces.md and locks.md.


Final lifecycle verification (2026-10-17): all 1763 tests pass, zero failures/skips, including 53 additional user-space/deletion/lock/domain cases. Locked restore, zero-warning/error Release solution build, full display PTY with generic QTEMP deletion and EXCLRD allocations, generated inventory and whitespace checks pass. WP12-04 byte-store acceptance is complete under its documented CRTUSRSPC/QUSCRTUS/QUSRUSAT subset; wider C11/C14 checklist items remain open (35 complete / 71 open).


Data-area API checkpoint (2026-10-17): QSYS/QWCRDTAA is a versioned IPCAPI program over the existing typed named/job-area stores. Checked big-endian headers, packed DEC values through 24 digits, logical encoding, truncated receivers, error buffers, strict CCSID conversion, first-match library lookup, QTEMP isolation and special-area ownership are implemented. Forty-five new cases cover independent byte vectors, CL and direct borrowed RPG calls, live authority/signing revocation, allocations, ranges, cancellation and the documented routing-PIP extension. Native DDM retrieval, independent padding/CCSID-position fixtures and richer exception data remain open; the broad C11/C14 checklist stays unchecked.


Final QWCRDTAA verification (2026-10-17): all 1808 tests pass, zero failures/skips, including 45 new data-area API cases. Locked restore, zero-warning/error Release solution build, full display PTY including native decimal/header/LDA layouts and direct borrowed RPG retrieval, generated inventory and whitespace checks pass. Checklist remains 35 complete / 71 open; native DDM retrieval, padding/CCSID-position acceptance and wider C11/C14 work remain unfinished.


Private messaging checkpoint (2026-10-17): MSGQ and MSGF operations resolve QTEMP before SQL access. Private inquiry reply routes grant exact system delivery to a live originating job without granting foreign read/send/library access; reply locks and sender metadata belong to the replier. Atomic batch sends, quota/default/rule failures, copies/exports, queue replacement waits, job cleanup and exclusive-server recovery retain their invariants. Version-3 predefined snapshots preserve private source identity internally while exposing logical metadata; legacy snapshots are sanitized in the read snapshot and retired atomically at cleanup. Separate producer/consumer/reply processes, legacy compatibility, two CCSIDs, private CL/DSPMSG and rollback tests cover these changes. The broad C07/C11/C14 checklist remains open for delivery attributes, break/notify, subfiles and wider APIs.

Final private messaging verification (2026-10-17): all 1839 tests pass, zero failures/skips, including 31 new private message cases. Locked restore, zero-warning/error Release solution build, full display PTY with private MSGQ/MSGF/predefined/CL/display/remove lifecycle, generated inventory and whitespace checks pass. Checklist remains 35 complete / 71 open; delivery attributes, break/notify, message-subfile bindings and wider APIs remain unfinished.

Message-subfile binding checkpoint (2026-10-17): live job/frame queue sources resolve output callers, external queues and nearest recursive programs without foreign-job access. Row writes capture text/help; SFLINZ reads a single bounded queue snapshot and grows the subfile beyond SFLSIZ. Display reads leave seen, exception, inquiry and reply state unchanged. Paging/help survive queue entry and private MSGF removal; failed initialization/output restores rows, capacity, page and editor. Twenty-four new tests and a live terminal fixture cover opaque keys, two CCSIDs, 80/132-column output, more than 1000 messages, authority and DDS diagnostics. CL SNDRCVF, RPG WORKSTN/EXFMT, record/RRN bindings, terminal suspension and ILE 276-byte queue names remain open; broad C07/C09/C11 checklist items stay unchecked.

Final message-subfile verification (2026-10-17): all 1863 tests pass, zero failures/skips, including 24 new message-subfile cases. Locked restore, zero-warning/error Release solution build, full display PTY including live SFLINZ/paging/saved second-level help after source removal, generated inventory and whitespace checks pass. Checklist remains 35 complete / 71 open; CL/RPG terminal exchange and broader C07/C09/C11 work remain unfinished.

CL display exchange checkpoint (2026-10-17): DCLF persists selected DSPF records and typed field/indicator layouts; SNDF/RCVF/SNDRCVF drive live local/socket terminals with nested-call resumption, CA/CF semantics, two CCSIDs, packed character buffers and atomic normalized input. Display identity/layout/authority and captured adopted authority remain checked while suspended; exchange capabilities expire on return. Initial programs can wait before menu/signoff. Disconnect/ENDJOB joins the worker before frame/QTEMP cleanup. Verified built-in System Request group controls and job details preserve paused editors; intentional original-group completion leaves survivors active. Compiled message subfiles retain paging and F1 help after message/private MSGF removal. Thirty new cases cover these contracts, including two socket jobs, cancellation and startup failures. RPG WORKSTN/EXFMT/RRN, asynchronous/multiple-device receives, display overrides/shared ODPs, richer native layout/error fixtures and wider C07/C09/C11 work remain open; checklist stays 35 complete / 71 open.

Final CL display verification (2026-10-17): all 1893 tests pass, zero failures/skips, including 30 new CL display/socket cases. Locked restore, zero-warning/error Release solution build, full display PTY with compiled SNDF/RCVF/SNDRCVF fields/indicators/CA/message-subfile help and suspended-job transfers, generated inventory and whitespace checks pass. Checklist remains 35 complete / 71 open; RPG WORKSTN/EXFMT and the documented wider CL/device contracts remain unfinished.

Verification history: the first full run passed 1892 cases and timed out once in the existing two-session sign-on test. That case passed an isolated run and five additional repetitions while the full rerun was active; the complete rerun passed all 1893. No timeout or transport behavior was changed for this unreproduced failure.

RPG WORKSTN checkpoint (2026-10-17): declarations now import saved, source-bound display layouts. EXFMT and separate READ/WRITE use live local/socket terminals. SFILE RRNs, ordinary subfile WRITE/READC/CHAIN/UPDATE, hidden fields, SFLNXTCHG retries, response indicators, job-CCSID widths and opaque message keys are integrated with C05. Message subfiles support live frame queues, SFLINZ, paging and saved F1 help through nested CL producers. RETURN retains rows/open file and parent-library leases; later CALLs rebind the terminal. CLOSE/LR/*NEW/reclaim/job end release them. Receiver validation precedes READC acknowledgment; failed conversion preserves fields/RRN/changed rows. Compiled metadata/source/layout/authority checks and disconnect/ENDJOB cleanup are covered. See docs/rpg-spec.md#12-workstn-display-files-and-subfiles. Printer output, complete RPG messaging callbacks, commitment/ODP coordination, full native D/C/ILE ABI/status semantics and asynchronous/multiple-device I/O keep the broader C09/C11 items open; checklist remains 35 complete / 71 open.

Final RPG WORKSTN verification (2026-10-17): all 1949 tests pass, zero failures/skips, including 54 workstation cases and three additional socket variants. The obsolete DCL-S rejection case is removed because the supported scalar declaration now compiles. Locked restore, zero-warning/error Release solution build, expanded display PTY with compiled RPG forms/subfiles/message help and suspended-job transfers, generated inventory and whitespace checks pass. Tests cover private QTEMP layouts, live authority, saved metadata, retained allocations and failed READC retry. Decimal literals, blank-padded comparisons and literal *BLANK input are corrected. Checklist remains 35 complete / 71 open; the broader C09/C11 contracts remain unfinished.


Native message APIs and discovery checkpoint (2026-10-17): QMHSNDPM/QMHRCVPM,
QMHSNDM/QMHRCVM, QMHSNDRM and QMHRTVM now share the durable job/named queues,
reply rules and saved descriptions. RCVM0100/0200 and RTVM0100 use checked
big-endian layouts, opaque keys, conversion/truncation and output preparation
before state/removal/reply commits. SAME preserves state; explicit keys, sender
copies and default replies retain atomic behavior. Named waiting receivers hold
job-owned tokens and shared-read queue/library leases, allow sends, reject other
receivers/removers/repliers and release on cancellation/ENDJOB. Schema 27 expands
immediate messages to 6000 bytes while preserving old metadata/routes/key high-water
marks and rollback. RPG adds raw CHAR CCSID(*HEX)/65535, hexadecimal inputs,
byte %SUBST and signed INT API references with bounds/alias/lifetime checks.

WRKAPILS discovers the actual 17 adapters, supports name/prefix filters and
option-5 counts/formats/limits, and reports current manifest/authority/trust failures
without invocation or reseeding. WP15-03 is supported. Direct compiled CL/RPG
calls run all six adapters at CCSIDs 37/1208; the real terminal exercises native
RPG production/receipt/retrieval, message-subfile F1 help and registry details.
All 199 focused cases and expanded display PTY pass. Wider exception/UI dispatch,
break/notify/status/request delivery, rich formats, larger substitution data,
rejection exits, messaging callbacks, ILE/ABI and remaining API/crypto families
keep broad C09/C11/C14 checklist items open. Checklist remains 35 complete / 71 open.

Final native message/registry verification (2026-10-17): all 2036 tests pass, zero failures/skips. Locked restore, zero-warning/error Release solution build, expanded display PTY with native RPG send/receive/retrieve and saved message help, WRKAPILS list/details/back, generated inventory and whitespace checks pass. Independent vectors cover CYYMMDD century boundaries, native sender/profile offsets, incomplete groups, invalid receivers, FIRST/NEXT retrieval and empty-table deleted-key preservation. Independent socket jobs verify allocated native receive, conflicting receiver, delivery and lease cleanup. WP15-03 is supported; the broad checklist remains 35 complete / 71 open.


System-value API checkpoint (2026-10-17): QSYS/QWCRSVAL reads 27 backed native values from one committed catalog snapshot and one local clock capture. It checks five parameters, binary sizes/counts, fixed-name arrays, offset-directory/table layouts, four-byte alignment and complete-entry truncation without modifying tails. Native character/binary types, policy aliases and fixed library/domain arrays are explicit. CL/RPG callers, shared/reopened hosts, separate-process atomic snapshots, authority/signing/manifests and socket-job changes have acceptance coverage. WRKAPILS now discovers 18 adapters. System-value library defaults are bounded to native 15/25 names, separate from running-job 250. Legacy oversized values fail without truncation. A false RPG IF now enters its ELSE body, with fixed/free/nested regression cases. Protected/lockable values, wider native catalog/formats and broader C09/C14/C18 contracts remain open.

Final system-value API verification (2026-10-17): all 2108 tests pass, zero failures/skips, including 72 new native-layout, policy, clock, process/socket, CL/RPG and branch cases. All 109 focused cases, locked restore, zero-warning/error Release solution build, expanded display PTY with compiled CL/direct RPG QWCRSVAL and registry details, generated inventory and whitespace checks pass. QWCRSVAL provides 27 backed native values; WRKAPILS discovers 18 adapters. Shared snapshots observe committed updates, dates/times use one live capture, and native 15/25-name defaults preserve oversized legacy data with actionable retrieval errors. False RPG IF/ELSE and nested/fixed/free paths are corrected. Protected/lockable system values, wider API families and broader C09/C14/C18 work remain open; checklist stays 35 complete / 71 open.


Native job API checkpoint (2026-10-17): QSYS/QUSRJOBI supports JOBI0100 basic
metadata and protected active-job JOBI0700 libraries; QUSLJOB produces JOBL0100
and JOBL0200 with 17 backed keys. Shared schema-28 opaque IDs and authenticated
48-byte cursors persist across ordinary catalog opens and refresh atomically at
exclusive host startup. List publication preserves caller/tail bytes, implicitly
grows existing spaces regardless of auto-extension, checks live authority and
EXCLRD allocations, and rolls back bytes/size/change state on failure/cancellation.
Real maximum-space continuation covers 3000 jobs and 64 repeated keys, tampered,
foreign, mismatched and stale handles. Terminal history is not mislabeled OUTQ.
CL/direct RPG at CCSIDs 37/1208 and independent socket QTEMP jobs pass. Raw RPG
EVAL now avoids numeric indicator comparison when no result indicators exist.
WRKAPILS discovers 20 adapters. See docs/job-apis.md for layouts, bounds and
finite semantics. Wider job formats, unbacked counters, spool integration and
remaining API/cryptography families keep C09/C14 open: 35 complete / 71 open.

Final native job API verification (2026-10-17): all 2174 tests pass, zero failures/skips, including 66 additional job-layout, opaque-ID, migration, cursor, CL/RPG, socket and raw-EVAL cases. All 154 focused cases, locked restore, zero-warning/error Release solution build, expanded display PTY with native CL/direct RPG job list/retrieval and registry formats, generated inventory and whitespace checks pass. Schema-28 backfill and exclusive-host epoch refresh preserve job data; list generation preserves caller/tail bytes, grows implicitly and continues at the real maximum with authenticated handles. QUSRJOBI supports JOBI0100/0700 and QUSLJOB supports JOBL0100/0200 with 17 backed keys; WRKAPILS discovers 20 adapters. Wider API families/formats, performance statistics, spool integration and broader C09/C14 work remain open. Checklist stays 35 complete / 71 open.

Native date API checkpoint (2026-10-17): QSYS/QWCCVTDT now accepts complete
5/10/11-argument groups and native short/long calendar/Julian date-time layouts,
CURRENT/SYSVAL/DTS and DOS formats. The shared timestamp codec preserves standard
52-bit microsecond/12-bit uniqueness semantics; the API applies its eight-microsecond
conversion quantum and native millisecond rounding/ranges/wrap rules. Character
conversion preserves unvalidated time bytes and short century copy behavior.
UTC/actual host-zone conversion validates DST gaps, supports both repeated-time
segments and rounds on the UTC instant across transitions. UTC/out-of-range
information receivers use zero binary/blank character fields and preserve tails.
Compiled CL/direct RPG at CCSIDs 37/1208, live program authority/signing/manifests,
output preparation/cancellation and real terminal flows have acceptance coverage.
WRKAPILS discovers 21 adapters. Job DATFMT/time-zone attributes, named TIMZON
objects and in-range host-zone metadata are explicitly unsupported pending shared
services. Wider C14 API/crypto requirements remain open: 35 complete / 71 open.
See docs/date-time-apis.md for exact layouts, selectors and errors.

Final native date API verification (2026-10-17): all 2240 tests pass, zero failures/skips, including 66 new timestamp/layout/range/copy/DST/DOS/control/error/cancellation and CL/RPG integration cases. All 74 focused cases, locked restore, zero-warning/error Release solution build, expanded display PTY with compiled CL/direct RPG microsecond timestamp round-trips and WRKAPILS details, generated inventory and whitespace checks pass. Published MI vectors verify the shared full-precision codec; QWCCVTDT conversion applies an eight-microsecond quantum inferred from IBM precision notes/endpoints. Native copy rules preserve raw time/century bytes where validation is not required. Rounding across DST and New Year, invalid local times, both repeated-time segments and atomic output preparation are verified. WRKAPILS discovers 21 adapters. Job DATFMT/time-zone attributes, named TIMZON objects, in-range host-zone metadata and wider API/crypto families remain open. Checklist stays 35 complete / 71 open.

Native cryptography checkpoint (2026-10-17): six QSYS QC3 adapters now provide
hash/HMAC, AES encrypt/decrypt and opaque job-owned key contexts through shared
.NET services. Five hashes, three AES key sizes, ECB/CBC/CTR, native padding,
checked DATA0100/ALGD0500/ALGD0200/KEYD0100/0200 descriptors and atomic output/
length/error preparation have published known-answer acceptance. Schema 29 stores
keys under authenticated AES-GCM envelopes, preserves existing catalog data/IDs,
checks current job/profile ownership, limits contexts per job, supports wrapping
inventory/rotation and deletes contexts with completion/crash recovery. CL/direct
RPG at CCSIDs 37/1208 and real PTY run all six programs; independent socket jobs
verify cross-call use, foreign-token denial and signoff cleanup. Writable scalar
RPG %SUBST now constructs raw descriptors with checked bounds, exact byte tails
and literal-text preservation. WRKAPILS discovers 27 adapters. Streaming algorithm
contexts, persistent native keystores, additional cipher/format families and ILE
binding retain their broader open requirements. See docs/cryptography-apis.md.

36. C14: .NET-backed hash/HMAC/AES and encrypted job-owned key-management services,
    six native QC3 adapters, published known-answer vectors, invalid/denied-key
    handling, transactional lifetime/rotation and actual CL/RPG/socket/PTY callers.
    See [crypto contracts and acceptance](cryptography-apis.md).

Final C14 cryptography verification (2026-10-17): all 2309 tests pass, zero failures/skips, including 69 new crypto, key-context, migration, native CL/RPG/socket and writable-substring cases. All 176 focused cases, locked restore, zero-warning/error Release solution build, expanded display PTY with all six QC3 adapters and registry details, generated inventory and whitespace checks pass. Published NIST/RFC vectors cover five hashes/HMACs and AES 128/192/256 in ECB/CBC/CTR; native padding, key types/lengths, maximum key size, per-job limits, atomic receivers and cancellation are verified. Schema-29 encrypted context storage supports same-job reopen, denies foreign/JOBCTL access, authenticates ciphertext, participates in wrapping inventory/rotation and releases with completion/recovery. Failed create/cleanup/rewrap/migration preserve prior state. Independent socket jobs verify cross-call use, foreign-token denial and signoff cleanup; the actual server recovers contexts before readiness. Writable scalar RPG %SUBST preserves bytes/tails, checks ranges and retains literal text. WRKAPILS discovers 27 adapters; the normalized compatibility catalog has 304 entries. C14 software cryptography/key-management acceptance is complete under docs/cryptography-apis.md and the release contract. The three broader C14 API/ABI/reference-suite items and general C09/ILE work remain open. Checklist is now 36 complete / 70 open.
