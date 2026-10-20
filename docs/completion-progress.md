# Completion progress — 2026-10-18

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
  report, now extended by 306 normalized command/API entries and release contracts.
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


## C11 journal foundation — 2026-10-17

Schema 30 introduces durable journal/receiver identities, chains and retention
guards, versioned images, accounting and managed PF/source capture triggers.
CRTJRNRCV/CRTJRN/CHGJRN/DLTJRN/DLTJRNRCV/STRJRNPF/ENDJRNPF/SNDJRNE/DSPJRN
now expose a documented finite subset through the shared service. CRTJRN requires
explicit user management; threshold configuration is separate from runtime
capacity. Entry read pages and terminal values are bounded and continuation is
explicit. File copies omit only registered capture triggers, while renames and
new members retain capture. Record mutations, journal sequence and receiver
accounting share one SQLite transaction.

Acceptance covers rollback, injected failure between update images, receiver
capacity and sequence exhaustion, exact integer/real/BLOB/null images, SQL
REPLACE/IGNORE behavior, source saving/CPYF, copy/rename, live authority, object
allocation, cancellation, ordinary reopen and independent process writers.
Compiled CL preserves binary buffers; user entry text preserves numeric leading
zeros and distinguishes literal from special *BLANK. Schema-29 upgrade and
failed-30 DDL/history rollback preserve prior state. Real display PTY covers
receiver creation, binary entry/detail view, rotation and acknowledged purge.
The normalized command/API inventory now contains 305 entries.

C11 remains open for replay/apply/remove, audit/data integration, private PF
bindings, receiver save state and automatic management/notification, native
retrieval/exit formats and recovery acceptance with C08 commitment control.
Per-operation atomicity does not satisfy job-wide CL/RPG/SQL commitment control.
See [journals.md](journals.md). Checklist remains **36 complete / 70 open**.

Final journal-foundation verification (2026-10-17): all 2335 tests pass, zero failures/skips, including 26 new journal/migration cases. All 133 focused checks, locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. The final run includes recursive SQL REPLACE delete capture, OR IGNORE sequence-exhaustion protection, receiver threshold versus capacity, binary/numeric/literal user data, atomic record/sequence/accounting rollback, independent process writers and schema-30 failure/retry. Nine journal commands are available as finite partial contracts; the normalized inventory has 305 entries. Replay/apply, audit integration, private capture, automatic management/save state and native retrieval remain open. Checklist stays 36 complete / 70 open.


## C11 journal replay and recovery — 2026-10-17

Forward apply and reverse removal now stream retained receiver ranges through
JournalStore.Recover, preserving physical record numbers and exact storage.
The APYJRNCHG/RMVJRNCHG commands share this service with compiled CL and
authenticated socket jobs. Receiver order follows operation direction; native
UP apply and UB remove endpoints can be selected independently. Large sequence
bounds, all/first/named members and repeated file/member selections are covered.

Recovery suspends user/capture triggers, avoids duplicate FK actions and checks
final referential integrity. Native END/CONTINUE policies retain a consistent
successful prefix and report failure per member, continuing other members of the
same file when requested. The additional atomic service policy checks expected
images and rolls back the full range. Local F/SA/AY/SR/RC markers record counts,
source bounds, actual caller/job and normal/abnormal state. Missing/corrupt
history, incompatible formats and prior recovery boundaries cannot silently
produce a successful partial result.

Acceptance includes a 10005-record stream, killed-process rollback after
uncommitted WAL spill, source/schema/image corruption, exact integer/IEEE/BLOB/
null values, triggers/FKs, native error policies and endpoints, receiver rotation
and deletion, authority/cancellation, CL and two independent socket jobs. The
expanded display PTY removes/reapplies a source record and displays its data.
A cursor-plan check exposed repeated scans; recovery now seeks directly from
the current sequence. The normalized compatibility inventory has 306 entries.

C11 remains open for save/restore-based recovery, object/DDL and other data
objects, private journaling, audit hooks, automatic receiver management, native
retrieval formats and joint acceptance with C08 job commitment control. This
recovery operation does not substitute for job-wide CL/RPG/SQL transactions.
Checklist remains **36 complete / 70 open**. See [journals.md](journals.md).

A binary-send review found generic CL argument rendering could trim trailing
blanks before SNDJRNE. The builtin now borrows CHAR byte storage through the
checked CL context path, preserving all 256 byte values and trailing encoded
spaces at CCSIDs 37/1208. Its command-object binding and authority checks remain
shared with other context commands; user-defined command processors retain
ordinary dispatch. The original CL send acceptance now includes trailing blanks.

Final journal recovery verification (2026-10-17): all 2372 tests pass, zero failures/skips, including 35 recovery cases and two all-byte CL send cases beyond the 2335-test foundation. All 182 focused checks, locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. APYJRNCHG/RMVJRNCHG preserve RRNs and exact storage, use forward UP/reverse UB endpoints, honor receiver direction, report native per-member END/CONTINUE failures and support large bounds/repeated file selections. Atomic service recovery, trigger/FK bypass, 10005-record keyset streaming, killed-process WAL rollback, corruption handling, CL and independent socket identity/authority are verified. SNDJRNE now borrows CL CHAR buffers and preserves all 256 bytes and trailing blanks at CCSIDs 37/1208. Eleven journal commands are available as finite partial contracts; the catalog has 306 entries. Save/restore/object recovery, private journaling, audit integration, automatic receiver management, native receiver APIs and C08 job commitment control remain open. Checklist remains 36 complete / 70 open.


## C08/C11 job record transaction foundation — 2026-10-17

Explicitly enlisted physical/source files share one private per-job workspace and
ordered intent log across parameterized record SQL, physical/logical file I/O,
RPG's SQLite adapter and sequential cursors. Publication uses one live transaction
for every changed record and its journal entries. Job logs, events and messages
continue independently while data is pending. Schema 31 tags local C/SC, forced
UB/UP and C/CM entries with the same cycle identity; migration preserves retained
journal data/accounting and failed upgrades roll back atomically.

Acceptance covers compiled CL/RPG callers at CCSIDs 37/1208, multi-file
commit/rollback, dependent triggers/FKs, exact decimal/null/float storage, live authority,
exact before-image conflicts, record/file leases, final-marker failure and retry,
cancellation while waiting for a writer/statement, reopened service ownership,
job/factory cleanup, a killed staged owner and publication killed during WAL spill.
A failed SQL scope cannot complete or retain rolled-back row intents. Cancellation
during SQL rolls back its writes and permits subsequent enlistment; CPYF cannot
bypass pending data through a logical source.
APY/RMV rejects cycle-tagged records even if the selected range excludes markers.

This is an internal foundation. Native commitment commands/open flags and lock
levels, activation groups, SQL translation, pending/rollback journal visibility,
cycle-aware recovery, RRN/key coordination and scalable workspace storage and
performance remain open. The prototype refreshes a full private catalog copy per
statement; it is not ready for native or large-catalog commitment acceptance.
See [record-transactions.md](record-transactions.md) for the precise contract.
C08/C11 remain unchecked; checklist stays **36 complete / 70 open**.


Final record-unit foundation verification (2026-10-17): all 2409 tests pass, zero failures/skips, including 35 record-transaction cases and two additional schema-31 migration cases beyond the 2372-test recovery baseline. Locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. Compiled CL/RPG at CCSIDs 37/1208 and SQL share pending records; multi-file publication commits records and local journal cycles atomically. Trigger/FK dependents, unjournaled-cascade denial, exact large-decimal/null/float storage, conflicts, live authority, retained locks, final-marker failure/retry, SQL/gate/writer cancellation, failed-statement intent rollback, logical-source CPYF rejection, reopen/disposal/job cleanup and killed-owner/publish recovery are verified. Native commitment commands/open flags/isolation, activation groups, pending/rollback journal visibility, cycle-aware replay, RRN/key coordination and scalable workspace storage/performance remain open. The normalized catalog remains 306 entries; checklist stays 36 complete / 70 open.


## C08/C11 committed-cycle journal recovery — 2026-10-17

Committed-cycle recovery checkpoint (2026-10-17): APY/RMV now validates and
recovers local IPCCMT1 SC/R/CM frames, rejects starts inside a cycle, stops before
an incomplete ending cycle with a visible diagnostic and supports explicit
CMTBDY(*NO) record endpoints. Native END/CONTINUE retain successful record prefixes
on error; the atomic service policy rolls back the range. Cycle lookahead stays
inside authorized receivers, bounds cached metadata and checks source identity,
integer RRNs/sequences, update pairs, marker types/JSON and journal/receiver links.
Recovery rejects active commitment definitions and honors persistent allocations.
Authenticated recovery holds exclusive file leases and parent-library read leases
through writer waits. Source
IMAGES(*BOTH) remains required for removal, including CMT-forced before images.
A 10005-update/20012-entry cycle, cache eviction, multiple journals, compiled CL,
independent socket jobs and terminal replay/partial-range paths are covered.
Native pending/rollback/interleaved cycles, save state, native receiver imports
and full commitment command/isolation acceptance remain open. C08/C11 stay
unchecked; checklist remains 36 complete / 70 open. See docs/journals.md.

Final committed-cycle recovery verification (2026-10-17): all 2457 tests pass, zero failures/skips, including 47 new cycle-recovery cases and one additional nonintegral-RRN corruption case beyond the 2409-test record-unit baseline. All 186 focused checks, locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. Tests cover real 10005-update/20012-entry cycles, cache eviction, shared IDs across journals, default boundaries and explicit CMTBDY(*NO), native per-member error prefixes, schema/image/source metadata corruption, receiver authority, exclusive allocation and cancellation, compiled CL at CCSIDs 37/1208 and independent authenticated socket jobs. Terminal acceptance removes/reapplies a committed cycle, displays an incomplete-ending diagnostic and executes individual partial entries. Source IMAGES(*BOTH) remains required for removal. Native pending/rollback/interleaved cycles, partial save state, binary entry imports and complete commitment commands/isolation remain open. Catalog remains 306 entries; checklist stays 36 complete / 70 open.


Record-only workspace checkpoint (2026-10-17): C08 no longer backs up the whole catalog before each record statement. A single committed read snapshot copies journaled PF/source records while catalog/security/job/message/event/journal-history and unrelated payload tables remain empty. Regular table definitions preserve unjournaled FK rejection; member indexes, generated values, sparse RRNs, autoincrement high-water marks and exact integer/BLOB storage are preserved. A 16 MiB unrelated payload is excluded and the fixture workspace stays below 4 MiB. Concurrent-writer tests check consistent cross-file snapshots. Publication preflights file authority under the live writer before DDL and rechecks credentials and active/noncancelled job state on that connection before commit, including shared-cache in-memory catalogs. Final-check failures roll back and retain pending intents for retry. All journaled members are still copied per statement; lazy loading, aggregate budgets, large-file targets and complete native commitment semantics remain open. C08/C11 remain unchecked; checklist stays 36 complete / 70 open.

Final record-only workspace verification (2026-10-17): all 2471 tests pass, zero failures/skips, including 49 record-transaction cases (14 more than the 2457-test baseline). All 108 focused transaction/recovery/authorization checks, locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. Tests verify excluded catalog/security/history/unrelated payload rows, the under-4-MiB workspace with a 16-MiB unrelated payload, sparse RRNs/autoincrement, generated/unique values, integer/BLOB storage, unjournaled FK denial, cross-file snapshot consistency, trusted/authenticated in-memory publication and final credential/job-check rollback with retry. Full-suite runtime is 5m26s; this is verification timing, not a release performance target. Catalog remains 306 entries and schema remains 31. All journaled members are still copied per statement; lazy loading, aggregate budgets and native commitment semantics remain open. Checklist remains 36 complete / 70 open.

Demand-loaded workspace checkpoint (2026-10-17): C08 no longer copies every journaled member before a statement. Authorized SQL preparation discovers referenced members, including joins and nested trigger/FK programs; physical/logical/cursor operations supply bound member names and prepare actual commands through the same path. Each needed member loads once from one source read snapshot, then replays only its pending intents without repeating triggers/FK effects. Untouched enlisted/pending members stay empty and still publish from the intent log. Cross-file and later-load reads keep the original snapshot while a separate WAL writer commits and logs messages. Disposal releases source snapshots after success, SQL errors and cancellation. Unexpected unloaded access is denied. Imports preserve last_insert_rowid(), exact values, RRNs and autoincrement state. Tests exclude a 16 MiB unrelated journaled member and another member of the same PF while retaining a workspace below 4 MiB. Joins/batches, pending replay, nested triggers, file-store FK checks/cascades, denied reads and shared-cache memory publication are covered. A used member is still loaded in full; schema metadata, aggregate growth/row overlays, WAL retention budgets, large-file targets and native commitment semantics remain open. C08/C11 remain unchecked; checklist stays 36 complete / 70 open.

Final demand-loaded workspace verification (2026-10-17): all 2490 tests pass, zero failures/skips, including 68 record-transaction cases (19 more than the 2471-test baseline). All 140 focused transaction/cycle/cursor/logical checks, locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. Large unrelated journaled files and other members of the same file stay unloaded below the fixture's 4 MiB workspace bound. Joins/batches, late loads from one committed snapshot, independent WAL writes/logging, unloaded pending publication, intent replay, nested triggers, file-store FK checks/cascades, authority denial and last_insert_rowid() preservation are verified. Source snapshots release after success, SQL failure and cancellation, including WAL writes after the snapshot starts. Full-suite runtime is 5m38s, not a release performance target. Used members still load in full; aggregate/schema/WAL budgets, row overlays and native commitment semantics remain open. Catalog remains 306 entries, schema 31 and checklist 36 complete / 70 open.

Commitment cursor checkpoint (2026-10-17): C08 bounded physical/logical cursors and compiled CL's cursor protocol now attach to enlisted job definitions. Successful commit preserves and saves positions even with no changed records; rollback restores the previous successful checkpoint or initial join/open position, including EOF and key/member/RRN tie breakers. Failed/cancelled publication leaves the previous checkpoint intact; accepted positions advance immediately after live commit before private cleanup. Read/close and transaction boundaries share the unit gate, so concurrent reads update one position atomically. Existing cursors join on their first read after enlistment; ordinary unenlisted cursors are unaffected. Closed cursors stay closed; job/factory cleanup invalidates reads and permits disposal; same-process reopens share registration. End requires attached cursors closed, with a reusable 256-cursor budget. Immutable enlistment snapshots support concurrent membership checks. Tests cover data/no-data commits, repeated rollback, EOF, failed final markers, cancellation, multiple cursors, physical/logical ties, inserts/deletes before position, late enlistment, ownership, job end, reopen, concurrent reads and capacity reuse. This remains a local keyset contract; native moved-key access paths, legacy RPG cursor conversion, CL/RPG boundary operations, commitment-open flags, isolation, activation groups, identifiers/notification, and complete native journal visibility remain open. C08/C11 stay unchecked; checklist remains 36 complete / 70 open.

Final commitment cursor verification (2026-10-17): all 2511 tests pass, zero failures/skips, including 89 record-transaction cases (21 more than the 2490-test baseline). All 145 focused transaction/cycle/sequential checks, locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. Tests verify initial and successful commit positions, commits with/without changed records, repeated rollback, EOF, failed final markers, cancellation, multiple cursors, late enlistment, ownership, job cleanup, same-process reopen, logical-member/RRN ties, other inserts/deletes before the position, concurrent readers, gate cancellation and reusable 256-cursor capacity. Checkpoints and reads share the unit gate; immutable enlistment snapshots make membership reads safe. Full-suite runtime is 5m43s, not a performance acceptance target. Native moved-key access paths, legacy RPG cursor conversion, language boundary operations, open flags/isolation/activation groups and complete native journal/notification semantics remain open. Catalog remains 306 entries and schema 31; checklist remains 36 complete / 70 open.

Bounded RPG navigation checkpoint (2026-10-17): C08/C09 SQLite RPG file cursors now use the same live one-record cursor as CL/database consumers, replacing whole-file ReadKeyed caches for production SQLite access. Forward/reverse reads, keyed CHAIN, SETLL/SETGT and adjacent READE/READPE share exact decimal/CCSID/null/descending order and physical member/RRN ties. Limits project key/index information without decoding nonkey payloads; failed searches require repositioning and do not replace record buffers. Host cancellation reaches SQL. Disk EOF/FOUND state follows input/positioning, and reload validates authority/schema without reloading records or overwriting buffers. RPG cursors now join job commit/rollback checkpoints, including a before-target lower limit; rollback invalidates the mutation target until a new input operation. A compiled RPG program resumes at the restored position after separate boundary helper calls. Tests cover corrupt later/nonkey data, live changes, large exact keys, search validation, cancellation, authority and a later allocated record. An ordinary 20000-row RPG open/first-read case allocates less than 4 MiB of managed memory on the calling thread. This is not a native-memory/latency or commitment-workspace budget. Custom non-SQLite providers retain the legacy materialized fallback. Native boundary commands/opcodes, commitment-open flags, update locks/isolation, activation groups, compound source-key operands, random RRN access, native moved-key/golden acceptance and whole-member workspace budgets remain open. C08/C09/C11 remain unchecked; checklist stays 36 complete / 70 open.

Final bounded RPG navigation verification (2026-10-17): all 2537 tests pass, zero failures/skips, including 94 record-transaction cases and 21 navigation cases (26 new cases beyond the 2511-test baseline). All 223 focused RPG/display/storage/cursor/transaction checks, locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. Production SQLite RPG cursors retain one record/position and support live forward/reverse navigation, exact/null/descending key searches and adjacent equal reads. Position-only queries avoid decoding malformed nonkey payloads; failed searches require repositioning; cancellation and authority are checked. Service commit/rollback checkpoints apply to RPG cursors, including a compiled caller with separate boundary helpers, and disk EOF/FOUND status is verified. The 20000-row ordinary open/first-read test stays below 4 MiB of managed allocations; a later allocated record does not block opening or reading the first row. This does not establish SQLite native-memory, latency or CMT workspace limits. Native commands/opcodes, open/update locks/isolation, activation groups, compound source keys/random RRN access, RRN-targeted duplicate-key writes, moved-key/golden acceptance and full-member workspace budgets remain unfinished. Full-suite runtime is 5m40s, not a performance target. Catalog remains 306 entries and schema 31; checklist remains 36 complete / 70 open.

Current-record write checkpoint (2026-10-17): SQLite database/RPG UPDATE and current-record DELETE now target the retrieved physical member/RRN, preserving duplicate-key siblings and supporting unkeyed files and changed keys. Logical projections preserve hidden fields and member identity. Full typed before images and physical schemas are checked under the writer; stale/deleted/differently reused records fail, and authority is rechecked after writer acquisition. Successful writes consume the target; failed validation/trigger operations retain it for retry. Exact RRN allocations honor other jobs and ordinary writer waits are cancellable. Enlisted writes share intents, retained locks and atomic record/journal publication, including pending inserts; rollback restores position and clears the mutation target. Trigger effects stage once. Compiled RPG, conflicts, schema/rowid aliases, logical source authority, allocations and cancellation are covered. Native update-read locks/isolation, identical-image RRN reuse detection, global RRN reservation, native moved-key/golden acceptance and full-member workspace budgets remain open. Custom providers and bulk keyed APIs retain their existing contracts. C08/C09/C11 stay unchecked; checklist remains 36 complete / 70 open. See docs/cursor-navigation.md.

Final current-record write verification (2026-10-17): all 2577 tests pass, zero failures/skips, including 40 new cases beyond the 2537-test baseline, 50 navigation cases and 105 record-transaction cases. All 188 focused RPG/cursor/transaction checks, locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. SQLite RPG/database writes affect one retrieved physical member/RRN, including duplicate keys, unkeyed files, changed keys, logical projections/members and pending inserts. Tests verify full typed before-image conflicts, deleted/reused RRNs, physical schema changes, generated rowid aliases, live source/view authority and revocation during writer waits, exclusive allocations and cancellation, retained transaction locks, trigger failure/retry, one-time trigger staging and compiled RPG commit/rollback. The concurrent 40-record cursor fixture retains its exact-once assertion and now uses a cooperative one-minute execution budget after a load-sensitive 20-second wait timed out in the parallel suite; the isolated fixture passes in about one second. Full-suite runtime is 5m54s, not a performance target. Native update-read locks/isolation, global RRN reservation/identical-image reuse detection, native moved-key/golden acceptance and full-member workspace budgets remain open. Catalog remains 306 entries and schema 31; checklist stays 36 complete / 70 open.

Compound RPG file-key checkpoint (2026-10-17): CHAIN, SETLL/SETGT, READE/READPE and keyed DELETE now bind complete parenthesized expression lists, KLIST names and scalar %KDS structures. Balanced parsing retains nested functions, spaces, quoted colons and doubled apostrophes, and rejects extra/missing operands with original include locations. KLIST/KFLD declarations accept standard declaration columns and the historical layout, bind procedure scope, evaluate live scalar fields and validate exact key attributes. Lists/%KDS convert compatible numeric/character values to the file key shape and reject bad types/counts. %KDS dynamic prefix counts are evaluated per operation; *KEY equal reads use the positioned key. Exact decimal literals, finite floating literals and bounded nesting prevent silent rounding or parser stack overflow. Keyed DELETE checks authority on misses, preserves input fields/position on a miss and holds the commitment gate across selection, one-RRN mutation and positioning. Logical compound deletion shares rollback and atomic journal publication. An expanded terminal program compiles/calls KLIST, KDS, nested expressions and one duplicate deletion. Native disk/open declarations, RRN access, full DS declarations/memory ABI and null attributes, strict-key/extender/golden acceptance, update-read locks/isolation and whole-member workspace budgets remain open. C08/C09/C11 stay unchecked; checklist remains 36 complete / 70 open. See docs/rpg-file-keys.md.

Final compound RPG file-key verification (2026-10-17): all 2656 tests pass, zero failures/skips, including 79 new cases beyond the 2577-test baseline. All 630 focused RPG/display/API/cursor/transaction checks, locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. CHAIN, bounds, equal reads and keyed DELETE consume full expression-list/KLIST/scalar-KDS prefixes with live values, nested/quoted arguments, exact literal precision, type/shape/count validation and original include locations. Tests cover procedure key-list scope, symbolic operators without spaces, compiler bounds, UTF-8 byte widths, partial keys, missing targets, authority, duplicate selection and logical compound deletion through rollback/commit and journal publication. The terminal compiles/calls RPGKEYS and verifies KLIST, KDS, nested expression keys and deletion of one duplicate. Existing WORKSTN operand diagnostics remain intact. Keyed DELETE holds selection/mutation/positioning under the unit gate, requires delete authority on misses and preserves fields/position on a miss. Native disk/open declarations, RRN access, full DS declarations/memory ABI and null attributes, strict-key/extender/golden acceptance, update-read locks/isolation and workspace scale budgets remain open. Full-suite runtime is 5m56s, not a performance target. Catalog remains 306 entries and schema 31; checklist stays 36 complete / 70 open.

Relative-record input checkpoint (2026-10-17): database cursors now expose physical RRN lookup, lower/upper limits, one-record deletion and the current input RRN. Predicates use the actual rowid alias, preserve deleted gaps and accept the native 1..4294967288 range without signed 32-bit conversion. Subsequent reads retain the configured keyed/arrival path. Logical projection/selection is honored; ambiguous multiple-member RRNs fail explicitly. Unkeyed RPG CHAIN/SETLL/SETGT/DELETE now use RRN operands, with strict literal/zero-decimal numeric-field and indexed-array validation, live authority, allocations, ownership and cancellation. Hosts can request explicit arrival mode on keyed PFs through an optional provider overload; unsupported providers reject explicit modes or RRN inference from cached lists. Commit/rollback retains RRN checkpoints and before-target limits; pending inserts can be changed/deleted, and large RRNs survive workspace/journal publication. Tests cover sparse slots, limits/EOF, malformed payloads, members, missing targets, authority, allocations, arrays and typed compiled RPG. The terminal RPGRRN program verifies lookup, deleted gaps, bounds, update and deletion continuation. Native DISK declarations/USAGE/open flags, RECNO writeback, reserved/deleted-slot output, INZPFM/REUSEDLT/global RRN allocation, multi-member mapping, native locks/isolation and independent golden/performance acceptance remain open. C08/C09/C11 stay unchecked; checklist remains 36 complete / 70 open. See docs/relative-records.md.

Final relative-record input verification (2026-10-17): all 2703 tests pass, zero failures/skips, including 47 new cases beyond the 2656-test baseline. All 677 focused RPG/display/API/cursor/transaction checks, locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. Explicit cursor RRN lookup/bounds/deletion preserve sparse physical slots and configured sequential order; logical selection, missing-target position, authority on misses, allocations, ownership and cancellation are verified. RRN operands reject character/fractional/out-of-range input and invalid scalar/array indexing. Compiled RPG supports unkeyed files and explicit host arrival mode on keyed PFs, with updates/deletes through job rollback/commit. Pending inserts, before-target checkpoints and journal publication preserve exact RRNs, including 4294967288 without signed 32-bit wraparound. The terminal RPGRRN program verifies lookup, deleted gaps, limits, update and deletion continuation. Multiple-source-member RRNs are rejected until native mapping is verified. Native DISK declarations/USAGE/open flags, RECNO writeback, initialized/deleted-slot output, INZPFM/REUSEDLT/global allocation/generations, native locks/isolation and independent golden/performance acceptance remain open. Full-suite runtime is 5m58s, not a performance target. Catalog remains 306 entries and schema 31; checklist stays 36 complete / 70 open.

Declared RPG DISK input checkpoint (2026-10-17): externally described input F-specs and free DCL-F now import source-bound database fields/formats and select explicit keyed or arrival access. DISK/USAGE defaults, native K position, continuations, EXTDESC/EXTFILE, RENAME/PREFIX, USROPN and %OPEN are connected to bounded physical/logical input. Compile/runtime overrides use the external filename; saved layouts detect source/JSON/request/format changes and reload without consulting a live compile resolver. Native handles preserve position/open state after RETURN and close on LR, activation cleanup or job end. Compatible SHARE paths retain shared position while keyed/arrival paths remain separate. Failed initialization releases open paths. Retained cursors rebind call cancellation without transferring ownership or losing service commit/rollback checkpoints. Imported KFLD fields bind after external descriptions. Update/delete/output declarations, retained update-read locks, WAITRCD/extenders/UNLOCK, native COMMIT/open flags/isolation, RECNO and slot allocation, general DS/null/varying/multi-format support, broader ODP compatibility and independent golden/performance acceptance remain open. C08/C09/C11 remain unchecked; checklist stays 36 complete / 70 open. See docs/rpg-disk-files.md.

Final declared RPG DISK input verification (2026-10-17): all 2764 tests pass, zero failures/skips, including 61 new cases beyond the 2703-test baseline. All 824 checks in the earlier focused RPG/display/cursor/transaction run and all 61 final disk/transaction cases pass. Locked restore, zero-warning/error Release solution build, expanded terminal PTY, generated inventory and whitespace checks pass. Native input F-spec/DCL-F imports external fields and validates saved source-bound layouts; keyed/arrival modes, external-name compile/runtime overrides, RENAME/PREFIX, USROPN/%OPEN, retained positions, close/LR/reclaim cleanup, failed-open release, authority revocation and durable reopen are verified. Imported KFLD keys bind after external descriptions. Retained cancellation rebinding preserves ownership and service rollback checkpoints. The real terminal RPGDISK program verifies both modes, imported/prefixed fields, CHAIN and open/close status. Native update/delete/output modes and read locks, WAITRCD/extenders/UNLOCK, COMMIT/open flags/isolation, RECNO/slot allocation, general DS/null/multi-format handling, wider ODP sharing and independent native golden/scale acceptance remain open. Full-suite runtime is 6m24s, not a performance target. Catalog remains 306 entries and schema 31; checklist stays 36 complete / 70 open.

Retained update-read foundation checkpoint (2026-10-17): OpenUpdateCursor now retains a physical member/RRN lease between input and mutation. Independent cursors in the same job contend; inquiry remains compatible, and only the owning cursor can upgrade its input lease to a point write, including while another reader waits. Later input/positioning, unlock, successful update/delete and close release the prior lease; inquiry input cannot supply an update target. Waits reselect fresh images, recheck authority/schema/cancellation and retain a blocked forward/reverse retry position. Failed current-record validation/trigger writes retain the target for retry. Job end releases persisted leases; factory shutdown cancels live waits and closes registered cursors before its lock catalog. Logical/RRN identity, other factories and call-token rebinding share the same scopes. Enlisted/late-enlisted update reads fail explicitly pending commitment read-lock policy. Native RPG update/delete/output declarations, N/E extenders/UNLOCK and statuses/WAITRCD, COMMIT/open flags/isolation, arbitrary SQL and trigger/FK lock coordination, intrajob deadlock/fairness and independent native golden/scale acceptance remain open. C08/C09/C11 stay unchecked; checklist remains 36 complete / 70 open. See docs/update-read-locks.md. Direct record writes now establish an operation lock scope when invoked without a command scope; clear, replace-copy and source-save establish it before their object/record checks. Failed multi-row writes roll back and release earlier row leases, while successful writes release the automatic scope. Existing enclosing command scopes are preserved.

Final retained update-read verification (2026-10-17): all 2825 tests pass, zero failures/skips, including 61 new cases beyond the 2764-test baseline. The 927-check broader RPG/display/cursor/transaction/lock run and five final clear/copy/source-save cases pass. Locked restore, zero-warning/error Release solution build, real terminal PTY, generated inventory and whitespace checks pass. Scoped noncommit update input retains one physical record, distinguishes same-job cursors, permits inquiry and allows the owner to write while others wait. Fresh-after-wait images, post-wait authority, cancellation, inclusive forward/reverse retry positions, explicit unlock and mutation eligibility, validation/trigger retry, logical/RRN identity, reopen and job/factory cleanup are verified. Job-owned record writes outside command execution now create bounded operation scopes; direct update/delete, clear, replace-copy and source-save honor retained reads and release rollback/commit leases. Existing enclosing command scopes are preserved. Enlisted update input is rejected pending commitment read-lock policy, while another job's commitment writer cannot stage over a retained read. Native RPG update/delete/output declarations, N/E extenders/UNLOCK/status/WAITRCD, COMMIT/open flags/isolation, arbitrary SQL and trigger/FK coordination, trusted jobless write coordination, intrajob deadlock/fairness and independent golden/scale acceptance remain open. Full-suite runtime is 6m21s, not a performance target. Catalog remains 306 entries and schema 31; checklist stays 36 complete / 70 open.

Declared RPG update/delete checkpoint (2026-10-17): free DISK USAGE(*UPDATE) now enables locked input and UPDATE; USAGE(*DELETE) implies input/update/delete, and fixed U files imply update/delete without addition. Attached N on CHAIN/READ/READP/READE/READPE retrieves inquiry input, and file UNLOCK releases a retained target without moving position. UPDATE requires the external record-format name, maps prefixed/renamed fields to columns and consumes the selected physical target. Current/key/RRN deletion respects delete usage, preserves duplicate siblings and uses retained ownership. Hosted RETURN retains input locks; LR/close/reclaim release them. Compatible shared update paths use one cursor; incompatible modes have separate identities. Version-2 saved bindings persist access flags and retain version-1 input-only compatibility; source/request mismatches cannot upgrade access. Providers must explicitly support retained input. Noncommit journal capture and explicit rejection of enlisted update files remain connected to the service foundation. Output/addition/RECNO allocation, native COMMIT/open flags/isolation, WAITRCD/status/E handling, full fixed calculation layouts, DS/null/multi-format support, arbitrary SQL/trigger/FK and trusted jobless coordination, broader ODP negotiation and independent native golden/scale acceptance remain open. C08/C09/C11 stay unchecked; checklist remains 36 complete / 70 open. See docs/rpg-disk-files.md.

Final declared RPG update/delete verification (2026-10-17): all 2874 tests pass, zero failures/skips, including 49 new cases beyond the 2825-test baseline. All 193 final file/compiler-diagnostic/journal checks pass; the earlier 980-check broader run exposed one unrelated extender diagnostic regression, which was corrected and verified by the final checks and full suite. Locked restore, zero-warning/error Release solution build, expanded terminal PTY, generated inventory and whitespace checks pass. Free update/delete usage and fixed U files now use retained physical input ownership; N input, UNLOCK, mapped record-format UPDATE, current/key/RRN DELETE, duplicate/logical identity and ordinary job-attributed journaling are verified. Retained calls, same-job contention, compatible SHARE paths, mode separation, authority revocation, missing-input buffers and version-2 access metadata/version-1 input compatibility are covered. The terminal RPGUPD program compiles/calls N input, UNLOCK, prefixed UPDATE and keyed DELETE. Enlisted update files fail explicitly pending commitment policy. Output/record addition, RECNO/allocator semantics, COMMIT/open flags/isolation, WAITRCD/status/E handling, full fixed calculation layouts, DS/null/multi-format support, broader ODP sharing, arbitrary SQL/trigger/FK and trusted jobless coordination, and independent native golden/scale acceptance remain open. Full-suite runtime is 6m15s, not a performance target. Catalog remains 306 entries and schema 31; checklist stays 36 complete / 70 open.


Declared RPG output/addition checkpoint (2026-10-17): free DISK USAGE(*OUTPUT), alone or combined with input/update/delete, now enables record-format WRITE with imported and prefixed/renamed fields. Fixed O and column-20 A on I/U permit addition. Output-only handles do not read or decode existing records and require object-operation/Add without Read/Update/Delete; library-list lookup and persistent allocations preserve that distinction while public object-lock authority stays unchanged. Separate writer handles validate owner, active job, live authority, member, format, cancellation and enlistment, again after acquiring SQLite writer access and before insertion; retained calls rebind cancellation, and factory shutdown cancels blocked SQLite writer waits before closing handles. Logical output requires one source member, validates selection and uses physical defaults for hidden columns. Unique/selection errors roll back, WRITE does not supply an update target or move/release existing input, and incompatible SHARE modes remain separate. Version-3 saved modes preserve version-1 input and version-2 input/update snapshots without allowing old-version output privileges. Ordinary journal capture remains job-attributed; open/late enlistment is rejected pending native commitment policy. RECNO-directed output, initialized/deleted-slot allocation and reuse/generations, native COMMIT/isolation, WAITRCD/status/E handling, full fixed calculations and DS/null/multi-format I/O, broader ODP negotiation, arbitrary SQL/trigger/FK and trusted jobless lock coordination, and independent native golden/scale acceptance remain open. C08/C09/C11 stay unchecked; checklist remains 36 complete / 70 open. See docs/rpg-disk-files.md.

Final declared RPG output/addition verification (2026-10-17): all 2932 tests pass, zero failures/skips, including 58 new cases beyond the 2874-test baseline. All 165 final focused cases, locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. Free output/combinations, fixed O/A flags, imported/prefixed/renamed WRITE, Add-without-Read authority, output-open requirements, retained calls/USROPN, runtime member overrides, incompatible SHARE modes, version-3 metadata and prior input/update compatibility are verified. Logical selection, unique failure/retry, hidden source defaults, source authority and ambiguous-member rejection are covered. Physical/logical output rechecks authority and job state after SQLite writer waits; committed revocation/cancellation prevents insertion and releases operation locks. Call cancellation and both factory-shutdown timings leave no added records or operation leases; a shutdown preceding connection open can legitimately report a disposed factory. Ordinary output journal entries retain job attribution and no commitment cycle. Open/late-enlisted output is rejected explicitly. The terminal RPGOUTPUT program adds and retrieves a renamed/prefixed record and verifies explicit close status. RECNO-directed output, initialized/deleted-slot allocation/reuse/generations, native COMMIT/isolation, WAITRCD/status/E handling, full fixed calculations and DS/null/multi-format I/O, wider ODP negotiation, arbitrary SQL/trigger/FK and trusted jobless locking, and independent native golden/scale acceptance remain open. Full-suite runtime is 6m47s, not a performance target. Catalog remains 306 entries, schema 31 and checklist 36 complete / 70 open.


Database WAITRCD checkpoint (2026-10-17): PF/LF metadata now persists native 60-second defaults and 0–32767-second record waits; legacy JSON defaults to 60 without schema migration. CRTPF/CRTLF accept WAITRCD, CHGPF changes it atomically with MAXMBRS, and a CHGLF WAITRCD subset is registered. Create/change *NOMAX means 32767 seconds; OVRDBF *NOMAX means an indefinite cancellable wait. Overrides retain nearest call/job scope and DSPOVR values, with TOFILE(*FILE) supported. Declared RPG update input captures the runtime file/LF or override policy at open; all five input operations and point mutation leases use it, N input remains inquiry, and SHARE separates incompatible policies. Retained calls preserve the original policy after metadata changes. Core waits support native long durations and indefinite deadlines across parent/object allocation, caller/factory cancellation and existing job cleanup. The explicit service API retains its zero-wait default. Restart/copy, authority, invalid inputs, atomic changes, timed fresh input, timeout cleanup, logical policy, keyed/RRN deletion and cross-factory deadlines are covered. Native status 1218/E handling, commitment/open flags/isolation, WAITFILE, full ODP/override merging, arbitrary SQL/trigger/FK and trusted jobless locking, RRN allocation and independent native golden/scale acceptance remain open. The first 251 focused checks, final 455 broader checks, locked restore, zero-warning/error Release build and expanded terminal acceptance pass; full-suite verification follows. Catalog remains 306 entries, schema 31 and checklist 36 complete / 70 open. See docs/record-waits.md.

Native file-status research (2026-10-17): verified IBM %STATUS/%ERROR and record-locking references while WAITRCD full-suite verification runs. RpgRuntimeContext currently returns an empty-string placeholder for %STATUS and stores a general file error flag; this is not the native numeric/per-file/global status contract. E operations reset status/error before execution, while ordinary successful operations need not reset previous status. Lock contention status 1218, extender combinations, indicator/MONITOR precedence, per-file/global lifetime and preserved-buffer acceptance remain required. See docs/record-waits.md#next-native-status-requirement; C09 remains open.

Final database WAITRCD verification (2026-10-17): all 2974 tests pass, zero failures/skips, including 42 new cases beyond the 2932-test baseline. All 455 final focused navigation/RPG/lock/environment/file/persistence checks pass, alongside the earlier 251 checks. Locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. PF/LF record-wait defaults, CRTPF/CRTLF and atomic CHGPF/CHGLF changes, override values/scope/display, long and indefinite deadline handling, fresh timed input for all five operations, timeout/cancellation cleanup, keyed/RRN mutation waits, logical-file policy, retained open policies and compatible SHARE identities are verified. Restart/copy and legacy JSON preserve metadata; other factories observe native long deadlines and cancellation/factory shutdown clears waiters. Terminal acceptance covers file wait keywords, a declared RPG update call under an override, and compiled CL CHGPF/OVRDBF/DLTOVR. CHGLF is now a registered partial WAITRCD command; other attributes remain unsupported. Service cursors retain their explicit zero-wait default. Native status 1218/E and numeric status lifetime, commitment/open flags/isolation, WAITFILE, broader ODP/override merging, arbitrary SQL/trigger/FK and trusted jobless lock coordination, RRN allocation and independent native golden/scale acceptance remain open. Full-suite runtime is 6m49s, not a performance target. Catalog remains 306 entries, schema 31 and checklist 36 complete / 70 open.


RPG numeric status/MONITOR checkpoint (2026-10-17): declared DISK/WORKSTN E operations reset independent global/per-file integer status and ERROR; each file operation refreshes numeric status, ordinary file success preserves ERROR, program operations without status changes preserve preceding status, EOF/miss/subfile-full report normal codes, and retained RETURN keeps status. Actual record-lock failures map to 1218, distinct from explicit-open allocation 1217; closed/already-open, unique, foreign-key, update-target and general file failures have bounded mappings. N+E combinations retain inquiry semantics. E/ER precede structured MONITOR, while operand program failures escape E. MONITOR/ON-ERROR/ENDMON provide ordered status filters, nested propagation, handler bypass, EXSR original errors and called-program 202, with job end/cancellation preserved. Staged input conversion preserves all program fields and releases failed update targets. SELECT boundaries/stacks and loop exits preserve enclosing control state; compact FOR expressions parse quoted TO/BY safely. Legacy bare ON-ERROR remains an extension. Full INFDS/PSDS, INFSR/*PSSR, ON-EXCP, remaining status/extender families, native fixed calculations, commitment policy and independent native reference acceptance remain open. See docs/rpg-file-status.md. C09/C11 stay unchecked; checklist remains 36 complete / 70 open.


Final RPG numeric status/MONITOR verification (2026-10-17): all 3050 tests pass, zero failures/skips, including 76 new cases beyond the 2974-test baseline. All 404 final focused cases and two referential-constraint cases pass. Locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. Every file operation refreshes numeric status, including ordinary success; ERROR retains the last E result, while program operations without status changes preserve preceding status. Global/per-file independence, retained RETURN, E/ER priority, normal EOF/miss, N combinations, record contention 1218 versus open allocation 1217, unique 1021 and referential violation 1022 are verified. Native allocation-specific 1222 is not substituted for SQLite foreign-key violations. Failed input conversion preserves all program fields and releases the update target. Structured handlers cover filter order, nesting, handler errors, EXSR and caller 202, with job end/cancellation preserved. SELECT end boundaries/stacks, LEAVE/ITER and compact FOR parsing preserve enclosing control state. The terminal RPGSTATUS program verifies closed-file E, numeric reset, N inquiry and ON-ERROR 1221. Full INFDS/PSDS byte layouts, INFSR/*PSSR, ON-EXCP, remaining statuses/extenders, native fixed calculations, commitment policy and independent native reference acceptance remain open. Full-suite runtime is 6m47s, not a performance target. Catalog remains 306 entries, schema 31 and checklist 36 complete / 70 open. See docs/rpg-file-status.md.


Native RPG data-structure checkpoint (2026-10-17): free DCL-DS/END-DS now allocates native byte storage with qualified fields, anonymous fillers/DCL-SUBF, POS/OVERLAY including inherited noncontiguous array stride, DIM and LIKEDS. Blank numeric initialization, INZ order/defaults, packed/zoned/signed integer/IEEE and default ISO datetime fields, elementwise numeric arrays and checked assignment use shared live bytes. %SIZE distinguishes element size from *ALL and %LEN returns numeric digits. Borrowed API views support whole structures, contiguous arrays and typed subfields, including initially invalid numeric output receivers; compatible scalar prototypes retain exact types. Indexed addresses are captured for lazy borrowing and copied writeback. Opaque storage identities/ranges extend API alias guards to partial overlaps without exposing backing arrays; adjacent receivers and existing exact-cell error reporting remain supported. Native buffers retain their activation CCSID and cells expire at call return. Bounded snapshots reject oversize calls before copying or entering the host. Saved QWCRSVAL programs work after source removal at CCSIDs 37/1208 and retained calls preserve native fields. The terminal RPGNATDS program passes. All 71 final targeted checks, locked restore and zero-warning/error Release build pass; full-suite verification follows. Normal CHAR scalar/array receivers also reject malformed UTF-8 before dequeue, while opaque receivers preserve the bytes. Nested/external/template layouts, complete array functions/relations, native fixed D/C, full entry/prototype ABI, INFDS/PSDS/INDDS population and independent native golden/scale acceptance remain open. Checklist remains 36 complete / 70 open. See docs/rpg-native-structures.md.


Final native RPG structure verification (2026-10-17): all 3119 tests pass, zero failures/skips, including 69 new cases beyond the 3050-test baseline. All 71 final targeted native/alias/regression checks pass. Locked restore, zero-warning/error Release solution build, expanded display PTY, generated inventory and whitespace checks pass. Known byte vectors verify native signed integer, packed/zoned and IEEE layouts at CCSIDs 37/1208. Native initialization, anonymous fillers, qualifier/array indexing, inherited overlay stride, LIKEDS, metadata, elementwise arithmetic, preserved overflow/datetime failures and byte substrings are covered. Borrowed whole-DS/array/subfield receivers retain live aliases and expire at call return; indexed addresses remain fixed for lazy borrowing and copied writeback. Range-aware output/error checks reject partial overlaps before dequeue or overwriting another argument, while adjacent receivers and existing exact-cell reporting remain accepted. Normal CHAR scalar/array output rejects invalid UTF-8 before dequeue; opaque output preserves arbitrary bytes and tails. Saved QWCRSVAL programs run after source removal at CCSIDs 37/1208, retained RETURN keeps native fields and terminal RPGNATDS compiles/runs typed headers and error structures. The original ProgramArgument constructor remains available. Nested/external/template layouts, complete array/CHARCOUNT semantics, native fixed D/C, full entry/prototype ABI, INFDS/PSDS/INDDS population and independent native golden/scale acceptance remain open. Full-suite runtime is 6m52s, not a performance target. Catalog remains 306 entries, schema 31 and checklist 36 complete / 70 open. See docs/rpg-native-structures.md.


Native RPG feedback checkpoint (2026-10-17): free INFDS/PSDS declarations now share native storage. Common file feedback reports status, open/EOF, native opcode/operand, routine/listing line, external record and available service CPF identity before E/ER or MONITOR handling. Program status/history remain separate from file status, while last-file snapshots work without an explicit INFDS. Entry counts, initial job identity, short PSDS layouts, EXSR origins, caller 202, retained cursors and saved-source programs at CCSIDs 37/1208 are covered by 54 new focused tests. Real WORKSTN exchange and terminal RPGFDBK are included. Extended feedback, INDDS/INFSR/*PSSR, native fixed layouts and independent native reference/scale acceptance remain open; C09/C11 stay partial and checklist remains 36 complete / 70 open. Full-suite verification follows. See docs/rpg-feedback-structures.md.


Final native RPG feedback verification (2026-10-17): all 3173 tests pass, zero
failures/skips, including 54 new cases beyond the 3119-test baseline. All 54 final
focused cases pass. Locked restore, zero-warning/error Release solution build,
expanded display PTY with CRTBNDRPG/CALL RPGFDBK, generated inventory and whitespace
checks pass. Native zoned status bytes at CCSIDs 37/1208, declaration validation,
implicit/explicit open and EOF, opcode abbreviations, independent program/file
status, pre-handler snapshots, EXSR origins/caller 202, parameter-count restoration,
short/anonymous PSDS, initial job identity, retained cursors and source removal
are verified. LIKEDS chains inherit final INFDS lengths without becoming feedback
storage or exceeding bounds. Extended feedback and other native RPG contracts
remain open. Full-suite runtime is 6m51s, not a performance target. Schema remains
31, command catalog 306 and checklist 36 complete / 70 open. See
docs/rpg-feedback-structures.md.


RPG error-subroutine checkpoint (2026-10-17): declared DISK/WORKSTN files accept
INFSR and each procedure can define *PSSR. E/ER and matching MONITOR precede
error subroutines, including caller monitors around nested same-procedure EXSR;
callee *PSSR precedes caller CALL monitors. Explicit/automatic blank ENDSR,
RETURN/GOTO recovery, six-byte return-point reset and main *CANCL/RNX9001 are
supported. Errors can recursively invoke handlers within a 64-invocation bound;
job end/cancellation bypass handlers. Abnormal exits release retained file owners
and discard failed activation state. Saved-source programs, real workstation
errors, fresh calls and retained lock cleanup are covered by 60 new cases; the
60-case focused run passes and terminal RPGERRSR compiles/runs INFSR GOTO and
PSSR RETURN. Full regression verification follows. Native cycle return points,
local-file declarations, full fixed calculations, remaining error/status/ILE
contracts and independent native reference acceptance remain open. C09/C11 stay
partial; checklist remains 36 complete / 70 open. See docs/rpg-error-subroutines.md.


Final RPG error-subroutine verification (2026-10-17): all 3233 tests pass, zero
failures/skips, including 60 new cases beyond the 3173-test baseline. All 60 final
targeted cases pass. Locked restore, zero-warning/error Release solution build,
expanded display PTY with CRTBNDRPG/CALL RPGERRSR, generated inventory and
whitespace checks pass. E/ER/MONITOR precedence, nested EXSR, procedure-local
PSSR, explicit versus automatic ENDSR, feedback visibility, RETURN/GOTO,
case-insensitive labels and active FOR/SELECT recovery are verified. Native
CHAR subfield/array return-point reset, main CANCL/RNX9001, recursive handlers,
cancellation/job end, implicit-open default handling, saved-source programs
and workstation errors are covered. Abnormal exits release retained update
locks and discard failed activation state; fresh calls are verified. Native
cycle restart points, local files, remaining fixed/error/ILE families and
independent native-system acceptance remain open. Full-suite runtime is 6m49s,
not a performance target. Schema remains 31, command catalog 306 and checklist
36 complete / 70 open. See docs/rpg-error-subroutines.md.


RPG INDDS checkpoint (2026-10-17): named scalar native structures now bind
WORKSTN DDS INDARA to independent or explicitly shared 99-byte indicator banks.
Character-zero defaults, typed initialization/CLEAR, native indicator/CHAR
subfields and arrays, final LIKEDS lengths, byte/API aliases and retained
RETURN/LR lifetime are supported. Conditioning and response deltas stay separate
from global IN; copied output flags and pre-commit response validation preserve
program storage. Version-two saved bindings hash INDARA while version-one
snapshots remain readable against live display files. 52 new cases cover native
bytes, sharing/isolation, real CA/CF, saved-source removal, retained/fresh calls,
metadata and declaration failures; all 115 focused cases pass. Earlier saved
QWCRSVAL/feedback and new display tests now explicitly create/assert actual
CCSID 1208 jobs; changing the profile alone did not change those job-argument
fixtures. Corrected cases pass. The terminal now exercises RPGINDDS; full-suite
and terminal verification follow. Multiple-occurrence structures, PRINTER,
native fixed D-specs and independent native-system acceptance keep C09 open.
Schema remains 31, command catalog 306 and checklist 36 complete / 70 open.
See docs/rpg-indicator-structures.md.


Validation fixture correction (2026-10-17): the waiting-receiver data-queue test now starts a dedicated worker, observes its blocked state and cancels/drains it on every exit. It no longer assumes a 100 ms pool continuation runs before a five-second receive timeout; a loaded full run exposed that assumption. Queue production behavior is unchanged.


Final INDDS focused verification: all 156 targeted display/indicator/native-CCSID/data-queue cases pass, including the corrected waiting-receiver fixture. Final full-suite verification follows.


Final RPG INDDS verification (2026-10-17): all 3285 tests pass, zero failures/
skips, including 52 new cases beyond the 3233-test baseline. All 156 final focused
display/indicator/native-CCSID/data-queue cases pass. Locked restore, zero-warning/
error Release solution build, expanded display PTY with CRTBNDRPG/CALL RPGINDDS,
generated inventory and whitespace checks pass. Known bytes, 99-byte defaults,
subfields/arrays/overlays, final LIKEDS lengths, native API alias lifetime, local
and shared banks, pre-commit validation, global IN independence and real CA/CF
exchange are verified. Saved-source and retained RETURN/LR tests now explicitly
create/assert real CCSID 1208 jobs, as do corrected earlier QWCRSVAL/feedback
fixtures. Version-two INDARA signatures detect live layout changes and reject
tampering/downgrades; legacy version-one snapshots still execute. The queue wait
fixture uses a dedicated receiver, blocked-state observation and unconditional
cancellation/drain rather than a timing-dependent pool continuation. No queue
production behavior changed. Multiple-occurrence structures, PRINTER, native
fixed D-specs and independent native-system acceptance remain open. Full-suite
runtime is 7m24s, not a performance target. Schema remains 31, command catalog
306 and checklist 36 complete / 70 open. See docs/rpg-indicator-structures.md.


RPG multiple-occurrence checkpoint (2026-10-17): native free DCL-DS supports
OCCURS, EVAL %OCCUR selection and status 122 recovery. Independent occurrence
bytes apply to initialization, qualified/unqualified fields, arrays, value
images, SUBST, current CLEAR and %KDS. CLEAR *ALL clears every occurrence and
selects occurrence 1; %SIZE(*ALL) returns the total byte allocation. Whole-MO
native API/copied-host parameters contain all occurrences, while subfield
addresses freeze at call entry. INDDS supports separate 99-byte occurrences
with conditioning and response addresses captured at I/O entry. Retained RETURN
keeps selection; LR/reclaim start fresh. 59 new cases and all 236 focused cases
pass, including actual CCSID 37/1208 saved programs, QWCRSVAL, separate CA/CF
banks, cancellation and final expanded storage bounds. Locked restore and
zero-warning/error Release solution build pass. Full-suite and expanded display
PTY verification follow. Named-constant declaration counts, native fixed D/C,
pointer alignment, extended feedback, PRINTER, full expression typing/entry ABI
and independent native reference acceptance remain open. C09 stays partial;
checklist remains 36 complete / 70 open. See docs/rpg-occurrences.md.


Final occurrence type check (2026-10-17): source metadata also rejects native
FLOAT(4) behind %ABS; conversion through %INT remains valid. All 237 final focused
cases pass, including 60 new cases beyond the 3285-test baseline. Release build
passes with zero warnings/errors. The preceding full run was stopped before
completion to include this regression; final full-suite verification is running.
The expanded display PTY has passed with CA/CF responses in separate occurrences.


Final RPG multiple-occurrence verification (2026-10-17): all 3345 tests pass,
zero failures/skips, including 60 new cases beyond the 3285-test baseline. All
237 final focused cases pass. Locked restore, zero-warning/error Release solution
build, expanded display PTY with separate INDDS CA/CF occurrences, generated
inventory and whitespace checks pass. Native occurrence byte vectors at CCSIDs
37/1208, initialization/LIKEDS, current value/SUBST/CLEAR and %KDS, status 122
with MONITOR/PSDS, zero-scale/non-float selection including native FLOAT(4)
behind %ABS, atomic cancelled CLEAR *ALL, total %SIZE and storage limits are
verified. Whole-MO API/copied-host arguments include all occurrences; captured
subfield addresses and pending INDDS response banks survive selection changes.
Saved-source removal, real QWCRSVAL and CA/CF exchange, retained selection and
LR/reclaim are verified in actual CCSID 37/1208 jobs. Named-constant declaration
counts, native fixed D/C/OCCUR, pointers/alignment, extended feedback, PRINTER,
full expression typing/entry ABI and independent native-system reference
acceptance remain open. Full-suite runtime is 7m26s, not a performance target.
Schema remains 31, command catalog 306 and checklist 36 complete / 70 open.
C09 remains partial. See docs/rpg-occurrences.md.


RPG declaration-constant checkpoint (2026-10-17): free DCL-C direct/CONST forms
infer literal types and remain immutable. Earlier named constants and supported
ABS/LEN/SIZE/ELEM built-ins supply scalar type dimensions, native LEN/DIM/OCCURS,
subfield dimensions/POS/OVERLAY and typed INZ. Evaluation uses declaration
metadata and folded values, without creating a runtime context or invoking host
callbacks. Final INFDS/INDDS lengths propagate through LIKEDS before layout
queries; discovery skips complete multiline literals. Counts, literal budgets,
nesting, precision and integer/float byte bounds remain enforced. Source/include
errors prevent program creation. Research corrected the prior %ELEM(MO)
restriction: IBM permits this and returns total occurrences. Runtime, tests and
documentation now match that rule. Saved QWCRSVAL fixtures use the single-entry
offset directory and system QCCSID, while explicitly creating/asserting actual
CCSID 37/1208 jobs. All 292 focused cases pass, including 60 new cases beyond
the 3345-test baseline. Locked restore and zero-warning/error Release build pass;
full-suite and display PTY verification follow. Additional pure built-ins, forward
dependencies, native fixed/local/standalone-array declarations, external/nested
layouts, CHARCOUNT/CCSID, full entry ABI and independent native acceptance remain
open. C09 stays partial; checklist remains 36 complete / 70 open.
See docs/rpg-declaration-constants.md.


Final RPG declaration-constant verification (2026-10-17): all 3405 tests pass,
zero failures/skips, including 60 new cases beyond the 3345-test baseline. All
292 focused cases pass. Locked restore, zero-warning/error Release solution
build, display PTY using named occurrence/character constants, generated
inventory and whitespace checks pass. Direct/CONST declarations, inferred
numeric/character/hex/indicator/float types, literal preservation, native
dimensions/positions/offsets/initializers, pure metadata queries, immutable
targets, include locations and count/literal/nesting/byte bounds are verified.
Final INFDS/INDDS/LIKEDS sizes work in either declaration order; multiline
literals cannot introduce phantom file hints. Saved-source QWCRSVAL callers
run in actual CCSID 37/1208 jobs with the correct single-entry offset and system
QCCSID. IBM's %ELEM multiple-occurrence contract replaces the prior incorrect
rejection in runtime, tests and docs. Additional pure functions/forward
dependencies, native fixed/local/standalone-array declarations, external/nested
layouts, complete CHARCOUNT/CCSID and entry ABI, and independent native-system
acceptance remain open. Full-suite runtime is 7m02s, not a performance target.
Schema remains 31, command catalog 306 and checklist 36 complete / 70 open.
C09 remains partial. See docs/rpg-declaration-constants.md.


RPG standalone-array checkpoint (2026-10-17): free DCL-S DIM now allocates
contiguous native element storage with typed defaults/INZ, constant dimensions,
whole-array assignment/arithmetic, common-count copy, CLEAR and layout queries.
Native DS and standalone indexes reject scaled/float/nonnumeric values with 105
and out-of-range indexes with 121. INT now truncates numeric/character fractions,
reports invalid data/overflow and handles float-to-signed-integer boundaries
without decimal rounding. Whole arrays borrow raw CHAR buffers; indexed elements
keep native types, aliases share bytes and lazy/copy addresses freeze at entry.
Scaled numeric arrays correctly expose zero-decimal raw-buffer metadata through
compatible scalar prototypes. Private conversion images and final cancellation
checks preserve entire arrays on failure. UTF-8 queue output normalization
precedes dequeue; opaque arrays preserve arbitrary bytes. Saved QWCRSVAL, source
removal and RETURN/LR/reclaim run in actual CCSID 37/1208 jobs. All 318 final
focused cases pass, including 53 new cases beyond the 3405-test baseline. Locked
restore and zero-warning/error Release build pass. Full-suite and expanded
RPGARRAYS terminal verification follow. Varying/compile-time/pre-runtime arrays,
sorting/search/aggregation/subarray functions, full expression/entry ABI, native
fixed arrays, CHARCOUNT/CCSID and independent native reference acceptance remain
open. C09 remains partial; checklist is 36 complete / 70 open.
See docs/rpg-standalone-arrays.md.


Final RPG standalone-array verification (2026-10-17): all 3458 tests pass, zero
failures/skips, including 53 new cases beyond the 3405-test baseline. All 318
final focused cases pass. Locked restore, zero-warning/error Release solution
build, expanded display PTY with CRTBNDRPG/CALL RPGARRAYS, generated inventory
and whitespace checks pass. Native byte vectors, typed defaults/INZ, DIM(1),
constant layout queries, scalar broadcast/common-count arithmetic and copy,
CLEAR, detached values and numeric/storage bounds are verified. Strict native
indexes report 105/121; INT truncates decimal/character/float values and handles
signed 64-bit float boundaries without decimal rounding. Raw array API buffers
expose zero decimals while indexed elements retain their native types. Aliases,
scalar prototypes, lazy/copied address capture and borrowed lifetime are covered.
Private images preserve entire arrays on conversion failure/cancellation,
including cancellation during the final conversion. Invalid UTF-8 queue output
remains queued with unchanged receivers; opaque arrays preserve bytes. Saved
QWCRSVAL, source removal, retained RETURN and LR/reclaim use actual CCSID 37/1208
jobs. Varying/compile-time/pre-runtime arrays, sorting/search/aggregation/subarray
functions, full expression/entry ABI, native fixed arrays, complete CHARCOUNT/
CCSID and independent native-system reference acceptance remain open. Full-suite
runtime is 7m02s, not a performance target. Schema remains 31, command catalog
306 and checklist 36 complete / 70 open. C09 remains partial.
See docs/rpg-standalone-arrays.md.


RPG array-operation checkpoint (2026-10-17): free SORTA with compatible A/D
extenders, native ASCEND/DESCEND metadata, five LOOKUP variants, XFOOT and SUBARR
now share native standalone/DS array storage. Sequenced lookup follows IBM's
first-equal and nearest-duplicate rules within start/count bounds; character
comparison uses captured CCSID bytes and native blank padding. Overlay sorting
moves complete base records, including chained overlays in selected structure
array elements/current occurrences. Numeric totals use exact scaled coefficients
or index-ordered floats and report overflow. Subarrays support EVAL, sort and
totals while copied call values cannot write back into their source. Numeric,
range, conversion and source diagnostics retain status/location. All 574 expanded
focused checks pass, including 75 new cases beyond the 3458-test baseline. Saved
program/source-removal cases use actual CCSID 37/1208 jobs. Locked restore and
zero-warning/error Release build pass; full-suite and expanded RPGARRAYS terminal
verification follow. Keyed DS/%FIELDS sorts, alternate collation, LOOKUPNE,
fixed/legacy array operations, full expression/array ABI and independent native
acceptance including overlap remain open. C09 remains partial; checklist is
36 complete / 70 open. See docs/rpg-array-operations.md.


Final RPG array-operation verification (2026-10-17): all 3533 tests pass,
zero failures/skips, including 75 new cases beyond the 3458-test baseline. All
574 expanded focused checks pass. Locked restore, zero-warning/error Release
solution build, display PTY with SORTA/LOOKUP/XFOOT/SUBARR, generated inventory
and whitespace checks pass. Both actual CCSID 37/1208 saved-program jobs and
source removal are verified. Lookup covers first-equal/nearest duplicates,
start/count ranges and numeric search precision; sorting covers native byte
collation, opaque invalid-UTF8 bytes, compatible direction extenders and complete
base-record movement through chained overlays in selected occurrences. Subarray
assignment preserves surrounding elements; copied call values cannot write back
to source arrays. Exact decimal and index-ordered float totals detect overflow
without source mutation. Keyed DS/%FIELDS sorts, alternate collation, LOOKUPNE,
fixed/legacy operations, full expression/array ABI and independent native-system
acceptance including overlap remain open. Full-suite runtime is 7m02s, not a
performance target. Schema remains 31, command catalog 306 and checklist
36 complete / 70 open. C09 remains partial. See docs/rpg-array-operations.md.


RPG keyed-array checkpoint (2026-10-17): native DS arrays now sort complete
records through DS(*).KEY, indexed scalar-array keys and scalar %FIELDS lists.
Subarray selections support explicit/default counts; dynamic key indexes freeze
before sorting. Default/A sort ascending and D descending, ignoring an indexed
key array's sequence metadata. Exact keyed LOOKUP is sequential with first-match
range behavior; relative variants reject keyed DS arrays. Nonkey numeric blanks,
opaque bytes and record gaps move without conversion. Multiple keys are read in
priority order on ties without a count-by-key matrix; private byte publication
preserves storage on key conversion errors/cancellation. Source validation rejects
missing keys and non-scalar/qualified/expression %FIELDS operands, enforcing
the 1024-key bound. Keyed
forms do not become writable API arguments. All 655 expanded focused checks
pass, including 81 new cases beyond 3533. Saved-source removal and RETURN/LR/
reclaim run in actual CCSID 37/1208 jobs. Locked restore and zero-warning/error
Release build pass. Full-suite and RPGKEYARR display PTY verification follow.
Nested keyed paths/DS declarations, alternate collation, additional array BIFs,
fixed arrays, full expression/array ABI and independent native-system golden/
scale/overlap acceptance remain open. C09 stays partial; checklist remains
36 complete / 70 open. See docs/rpg-keyed-arrays.md.


RPG keyed-array validation follow-up (2026-10-17): a rejection test confirmed
that the generic expression parser accepted comma-separated %FIELDS lists.
SORTA now uses the existing balanced keyword/colon-list parser for that operand,
normalizes case before name validation and rejects commas/non-name expressions.
Missing closing/trailing delimiters also have explicit rejection coverage. All
661 expanded focused checks pass, including 87 new cases beyond 3533. Release
build remains zero-warning/error; final full-suite and display PTY checks follow.
The earlier 3614-test full run passed before these final validation additions.
C09 remains partial; see docs/rpg-keyed-arrays.md.


Final RPG keyed-array verification (2026-10-17): all 3620 tests pass,
zero failures/skips, including 87 new cases beyond the 3533-test baseline. All
661 expanded focused checks pass. Locked restore, zero-warning/error Release
solution build, display PTY with CRTBNDRPG/CALL RPGKEYARR, generated inventory
and whitespace checks pass. Keyed scalar/indexed-array sorts, multi-key priority,
direction and captured indexes, explicit/default subarray ranges, typed keys,
CCSID byte order, record gaps and opaque/uninitialized nonkey bytes are verified.
Exact keyed lookup is sequential and range/duplicate aware; relative variants
reject DS keys. Source validation rejects missing keys and non-scalar/qualified/expression
%FIELDS operands, comma lists and malformed delimiters, accepts mixed-case names and enforces
1024 keys. Private record images preserve data on key conversion failure;
keyed forms cannot enter writable API borrowing. Saved programs after source
removal, retained RETURN, LR and reclamation use actual CCSID 37/1208 jobs.
Nested DS/key paths, additional array BIFs, alternate collation, fixed arrays,
full expression/array ABI and independent native-system golden/scale/overlap
acceptance remain open. Full-suite runtime is 7m17s, not a performance
target. Schema remains 31, command catalog 306 and checklist 36 complete /
70 open. C09 remains partial. See docs/rpg-keyed-arrays.md.


RPG compile-time-array checkpoint (2026-10-17): free standalone CTDATA,
PERRCD, numeric EXTFMT(S/L/R) and inferred DIM(*CTDATA) now load validated
trailing records into native array storage. Preprocessing preserves directive/
comment-looking data and multiline code literals; includes retain origins and
synthetic EOF lines do not inflate inferred counts. Named sections bind by name,
unnamed sections by declaration order; missing entries use type defaults and
partial final slots must be blank. Numeric source widths, implied scales/signs,
float formats, ISO datetime and native precision/byte bounds reject bad data
before program creation. Numeric sequence checks include default tails; character
sequence and encoding checks use activation CCSID with record locations before
calculations. CLEAR applies defaults; RETURN retains changes and LR/reclaim reload
saved initializers. All 798 expanded focused checks pass, including 65 new cases
and retirement of one obsolete unconditional CTDATA rejection (net 64 beyond
3620). Saved-source removal and lifecycle tests use actual CCSID 37/1208 jobs.
Locked restore and zero-warning/error Release build pass; full-suite and RPGCTDATA
terminal verification follow. Alternating/fixed/DS-subfield CTDATA, pre-runtime
FROMFILE/TOFILE, opaque/multibyte source-CCSID rules, full array/entry ABI and
independent native-system golden/scale acceptance remain open. C09 remains
partial; checklist is 36 complete / 70 open. See docs/rpg-compile-time-arrays.md.


CTDATA regression verification follow-up (2026-10-17): the first full suite
ran all 3684 cases; 3683 passed and an existing data-queue authority-revocation
wait returned without the expected denial. Its four-second receive and delayed
continuation allowed expiry before revocation, consistent with the observed
null return. Receive already rechecks authority each loop.
The fixture now uses a dedicated cancellable receiver, start/observed-wait
synchronization and an infinite receive with a 30-second cancellation bound.
It also verifies an unrelated keyed entry remains queued and readable after
denial. Queue service behavior is unchanged. All 839 expanded focused checks,
including the queue fixtures, pass; Release build is zero-warning/error. The
final full-suite rerun is in progress. The CTDATA terminal PTY already passed
on both screen sizes. C09 remains partial; see docs/rpg-compile-time-arrays.md.


Final RPG compile-time-array verification (2026-10-17): all 3684 tests pass,
zero failures/skips. There are 65 new CTDATA cases and one retired obsolete
rejection, a net increase of 64 beyond 3620. All 839 expanded focused checks
pass, including the corrected queue fixtures. Locked restore, zero-warning/error
Release solution build, display PTY with CRTBNDRPG/CALL RPGCTDATA, generated
inventory and whitespace checks pass. CTDATA/PERRCD, S/L/R numeric source formats,
float/ISO datetime, named/declaration-order sections, inferred dimensions and
later constants, literal data preservation, include origins, EOF accounting,
defaults, sequence/encoding errors and native byte vectors are verified. Saved
programs after source removal and RETURN/LR/reclaim run in actual CCSID 37/1208
jobs. The full-suite timing-sensitive authority-revocation fixture now uses a
dedicated observed waiter, cancellable infinite receive and retained keyed data
checks; queue service behavior is unchanged. Alternating/fixed/DS-subfield CTDATA,
pre-runtime FROMFILE/TOFILE, opaque and full multibyte source-CCSID rules, full
expression/array ABI and independent native-system golden/scale acceptance remain
open. Full-suite runtime is 7m20s, not a performance target. Schema remains
31, command catalog 306 and checklist 36 complete / 70 open. C09 remains partial.
See docs/rpg-compile-time-arrays.md.


RPG alternating-array checkpoint (2026-10-17): free standalone ALT(main)
loads interleaved pairs into separate native arrays, with either declaration
order, named/declaration-order main sections, independent formats/sequences and
matching fixed or inferred dimensions. PERRCD counts pairs and combined source
widths enforce the 100-position bound. Space-padded numeric digit fields now load
for both paired and standalone CTDATA. SORTA/CLEAR and native API buffers remain
independent. Invalid targets/options, duplicate alternates, chains/cycles,
mismatched dimensions, unused final pair data and source/value errors reject
with locations. Saved-source removal and RETURN/LR/reclaim run in actual CCSID
37/1208 jobs. All 891 expanded focused checks pass, including 52 new cases beyond
3684. Locked restore and zero-warning/error Release build pass; final full-suite
and RPGALTARR display PTY verification are running. Unequal-dimension native
acceptance, alternate-name section edges, pre-runtime/fixed/DS-subfield arrays,
source-CCSID/opaque rules, full array ABI and independent native golden/scale
acceptance remain open. C09 stays partial; checklist remains 36 complete / 70
open. See docs/rpg-alternating-arrays.md.


Final RPG alternating-array verification (2026-10-17): all 3736 tests pass,
zero failures/skips, including 52 new cases beyond 3684. All 891 expanded focused
checks pass. Locked restore, zero-warning/error Release solution build, display
PTY with CRTBNDRPG/CALL RPGALTARR on both screen sizes, generated inventory and
whitespace checks pass. ALT supports either declaration order, named/bare main
sections, PERRCD pair counts, independent types/formats/sequences, matching
fixed/inferred dimensions and later layout constants. Exact 100-position
records, blank unused pairs, defaults, include origins and creation failures are
covered. Space-padded numeric fields now work in paired and standalone CTDATA.
SORTA, CLEAR and native API bytes preserve independent storage. Saved programs
after source removal and RETURN/LR/reclaim use actual CCSID 37/1208 jobs.
Unequal-dimension native acceptance, alternate-name section edges, pre-runtime,
fixed/DS-subfield arrays, opaque/full source-CCSID rules, complete array ABI and
independent native-system golden/scale acceptance remain open. The matching-
dimension requirement is the supported implementation contract. Full-suite
runtime is 7m03s, not a performance target. Schema remains 31, command catalog
306 and checklist 36 complete / 70 open. C09 remains partial.
See docs/rpg-alternating-arrays.md.


RPG traditional-definition checkpoint (2026-10-17): native fixed D-spec S
fields/arrays and C constants now use the shared strict declaration pipeline.
Both native and existing shifted origins, type/default inference, right-justified
length/decimals, keyword continuations, CTDATA/PERRCD/EXTFMT/ALT and inferred
counts/layout constants are covered. Arrays use native byte storage and existing
operations/API views. Fixed comments and commentary columns no longer alter
quote/data state; literals retain slashes/doubled apostrophes. Continuations use
bounded collection with cancellation. Unsupported native forms/options, invalid
columns, duplicate symbols and split literals reject with source locations.
All 964 expanded focused checks pass, including 67 new cases beyond 3736 and
legacy fixed parser/API regressions. Saved-source removal, QWCRSVAL native
input/receivers, CCSID ordering and RETURN/LR/reclaim use actual CCSID 37/1208
jobs. Locked restore and zero-warning/error Release build pass; final full-suite
and RPGFIXARR display PTY checks are running. Native fixed DS/PR/PI/local and
calculation forms, continued names/literals, additional types/keywords,
pre-runtime and DS-subfield CTDATA, full ABI and independent native golden/scale
acceptance remain open. C09 remains partial; checklist stays 36 complete / 70
open. See docs/rpg-fixed-declarations.md.


Traditional-definition validation follow-up (2026-10-17): review found that
shared scalar INT initializer validation enforced signed 64-bit limits but not
the declared 1/2/4-byte width. The shared check now enforces every signed native
width before program creation, including free scalars; array/DS/CTDATA checks
remain consistent. Eighteen additional cases cover exact minimum/maximum and
adjacent invalid values for scalar/array widths plus free-scalar regressions.
All 982 expanded focused checks pass, including 85 new cases beyond 3736.
Release build is zero-warning/error. The earlier full run was interrupted to
apply this correction; final full-suite and terminal acceptance are running
against the rebuilt assembly. C09 remains partial; see docs/rpg-fixed-declarations.md.


Final RPG traditional-definition verification (2026-10-17): all 3821 tests
pass, zero failures/skips, including 85 new cases beyond 3736. All 982 expanded
focused checks pass. Locked restore, zero-warning/error Release solution build,
display PTY with CRTBNDRPG/CALL RPGFIXARR on both screen sizes, generated inventory
and whitespace checks pass. Native and shifted D-spec S fields/arrays and C
constants share strict declaration validation; runtime arrays, CTDATA/ALT,
continued keywords, inferred counts/layout constants, type byte vectors,
comment/quote state, includes and source rejection paths are verified. Saved
programs after source removal, native QWCRSVAL input/receivers and RETURN/LR/
reclaim use actual CCSID 37/1208 jobs. Shared scalar integer INZ now rejects values
outside the declared 1/2/4/8-byte signed range; exact limits and adjacent invalid
values are covered for scalar/array definitions and free-scalar regressions.
DIM arrays use native storage; non-array S definitions retain the existing scalar
runtime. Full scalar byte/overflow behavior, native fixed DS/procedure/local and
calculation forms, continued names/literals, additional types/keywords,
pre-runtime and DS-subfield CTDATA, full ABI and independent native-system golden/
scale acceptance remain open. Full-suite runtime is 7m39s, not a performance
target. Schema remains 31, command catalog 306 and checklist 36 complete / 70
open. C09 remains partial. See docs/rpg-fixed-declarations.md.


RPG native-scalar checkpoint (2026-10-17): supported free/traditional
standalone scalars now own native bytes without becoming arrays. Integer widths,
decimal truncation/precision, FLOAT(4), CHAR byte padding/truncation, byte %SUBST,
opaque and ISO values use the DS/array codec. Conversion images and final
cancellation checks preserve old bytes on failure. Allocation follows file
binding and shares native bounds. Borrowed entries refresh/publish caller
storage, enforce width/scale/CCSID, preserve same-cell and partial-range aliases,
forward readonly metadata and detach with a byte snapshot on return/error.
Outer receiver normalization precedes queue mutation; real queue rejection and
alias cases preserve entries and receivers. Native API views retain scoped
lifetimes and opaque identities. All 1672 expanded RPG/CL/queue checks pass,
including 54 new cases beyond 3821. The filler regression now proves a real
filler-like scalar stays independent of anonymous DS bytes rather than assuming
scalars lack element metadata. Saved-source removal, QWCRSVAL, CL aliases on
return/error and RETURN/LR/reclaim use actual CCSID 37/1208 jobs. Locked restore
and zero-warning/error Release build pass; full-suite and RPGSCALAR display PTY
verification are running. Legacy/imported-field storage, full expression typing/
precision/extenders, additional types/CCSID modes, fixed DS/procedure/local/
calculation forms, pre-runtime arrays, full ABI and independent native golden/
scale acceptance remain open. C09 remains partial; checklist stays 36 complete /
70 open. See docs/rpg-native-scalars.md.


Native-scalar cleanup follow-up (2026-10-17): a borrowed semantic entry
snapshot was passing through write-cancellation validation during detachment.
Caller reads/snapshots now use pure byte conversion, while native mutation retains
its pre-publication cancellation checks. A regression changes a caller cell,
cancels execution, then verifies the detached value and later caller isolation.
All 1673 expanded RPG/CL/queue checks pass, including 55 new cases beyond 3821.
Release build is zero-warning/error. The first full suite was interrupted for
this correction; the final full-suite and terminal reruns are active. C09 remains
partial; see docs/rpg-native-scalars.md.


Final RPG native-scalar verification (2026-10-17): all 3876 tests pass,
zero failures/skips, including 55 new cases beyond 3821. All 1673 expanded RPG/CL/
queue checks pass. Locked restore, zero-warning/error Release solution build,
display PTY with CRTBNDRPG/CALL RPGSCALAR on both screen sizes, generated inventory
and whitespace checks pass. Supported free/traditional scalars now use native
bytes with scalar metadata, signed integer bounds, decimal truncation/precision,
FLOAT(4), CHAR CCSID/byte operations, opaque and ISO values. Private conversion
images and cancellation checks preserve prior storage. Allocation follows file
binding and shares native bounds. Borrowed entries retain live same-cell/partial-
range aliases, caller layout/CCSID validation, readonly metadata and opaque
identities. Outer receiver validation and API alias checks preserve real queued
entries and all receivers. Return/error/cancellation detach the latest caller
bytes; reads/snapshots remain available after cancellation while mutation checks
remain enforced. Saved-source removal, QWCRSVAL, CL aliases on return/error and
RETURN/LR/reclaim run in actual CCSID 37/1208 jobs. The filler regression now
checks actual scalar/filler independence. Legacy/imported-field storage, complete
expression typing/precision/extenders, additional types/CCSID modes, fixed DS/
procedure/local/calculation forms, pre-runtime arrays, full ABI and independent
native-system golden/scale acceptance remain open. Full-suite runtime is 8m19s,
not a performance target. Schema remains 31, command catalog 306 and checklist
36 complete / 70 open. C09 remains partial. See docs/rpg-native-scalars.md.


RPG native C-spec checkpoint (2026-10-18): actual column-6 calculations now
use ten-column operations, physical factors/results/HI-LO-EQ and continued
extended factor 2. Supported arithmetic flags inspect stored native results;
COMP uses unsigned job-CCSID bytes with blank padding. DO pretests/defaults,
conditioned blocks, structured control, subroutines, arrays, inline definitions,
ordered named/inline CALL parameters and native entry borrowing use the shared
runtime. Declarative lists inside unexecuted subroutines survive extraction;
repeated arguments preserve aliases. CALL(E)/CALLP(E)/LO precede MONITOR for
called-program errors. Native file columns expose the existing declared file
services and correct SETLL/SETGT end flags. Unsupported cycle/control groups,
special indicators, opcode/extender and file-result-DS forms reject explicitly.
Locked restore, zero-warning/error Release build, RPGNATCAL terminal compilation/
execution at 24x80 and display fixtures at 24x80/27x132 pass; all 1761 expanded RPG/CL/queue checks pass, including
88 new native calculation cases beyond 3876. Full-suite verification is pending.
The generated inventory separates native and older opcode layouts. Complete
cycle/remaining opcode semantics, native DS/procedure/local specs, complete
expression/collation/ABI and independent native golden/scale acceptance remain
open. C09 remains partial; schema 31, catalog 306 and checklist 36 complete / 70
open are unchanged. See docs/rpg-fixed-calculations.md.


Native C-spec DO follow-up (2026-10-18): IBM's DO execution steps require
reading the limit after counter initialization and again after each increment.
The native path now does so rather than caching it. Five additional checks
cover shrinking/growing limits, an aliased counter/limit, a changing increment
and a native DO with a free-form body/ending. All 93 native calculation checks
pass, including matching-key SETLL EQ and SETGT end assertions in actual CCSID
37/1208 jobs. Release build is zero-warning/error. Earlier full runs were
interrupted to strengthen file assertions and correct the dynamic DO limit;
final expanded regression, full-suite and terminal verification are pending.
C09 remains partial; see docs/rpg-fixed-calculations.md.


Final RPG native C-spec verification (2026-10-18): all 3969 tests pass,
zero failures/skips, including 93 new cases beyond 3876. All 1766 expanded
RPG/CL/queue checks and all 93 native calculation checks pass. Locked restore,
zero-warning/error Release solution build, expanded display PTY, generated
inventory and whitespace checks pass. RPGNATCAL compiles/runs through the
terminal at 24x80; display fixtures also exercise 27x132. Native calculations
use physical ten-column operations, factor/result/indicator positions and
continued extended factor 2. Arithmetic flags use stored native results; COMP
uses unsigned job-byte ordering. DO initializes before reading an aliased
limit and rereads limits/increments each iteration, including mixed fixed/free
endings. Matching-key SETLL EQ and SETGT end are verified in both actual job
CCSIDs. Declarative lists survive subroutine extraction; ordered inline/named
CALL parameters retain typed aliases and semantic writeback. Saved-source
removal, QWCRSVAL, CL return/error aliases, retained RETURN/LR/reclaim, native
file flags and E/error precedence are covered in CCSID 37/1208 jobs. Unsupported
cycle/control/indicator, opcode/extender and file-result-DS forms reject
explicitly. Native and older opcode inventories are separate. Complete cycle/
remaining opcode semantics, native DS/procedure/local specs, complete expression/
collation/ABI and independent native golden/scale acceptance remain open.
Full-suite runtime is 7m13s, not a performance target. Schema remains 31,
command catalog 306 and checklist 36 complete / 70 open. C09 remains partial.
See docs/rpg-fixed-calculations.md.


RPG native array-search checkpoint (2026-10-18): traditional LOOKUP now
implements exact/nearest suffix searches, duplicate boundaries, shared result
indicators and captured writable-index publication. Non-float comparisons use
exact signed coefficients through 29 digits; character comparisons use job
bytes. %LOOKUPNE adds sequential scalar/keyed DS searches. LOOKUP global
FOUND/EQUAL and SETLL per-file EQUAL remain independent; array searches preserve
EOF/error/numeric status. Global column-limited DCL-S/C/F/DS declarations now
share the native declaration pipeline and can precede native C calculations.
Physical code/sequence/comment columns, declaration order and unsupported split
literals reject explicitly. All 104 new targeted and all 1870 expanded RPG/CL/
queue checks pass. Saved-source removal, CTDATA/ALT descriptions, CL aliases on
return/error, RETURN/LR/reclaim, file/global status and native DS/file source
forms run in actual CCSID 37/1208 jobs. Locked restore, zero-warning/error Release
build and RPGLOOKUP terminal compilation/execution pass. Full-suite verification
is pending. The opcode-specific IBM rule sets a missed writable index to 1;
conflicting overview text says 0, so native-system validation remains required.
Tables, broader collation/type/ABI/native specifications and independent native
golden/scale acceptance remain open. C09 remains partial; schema 31, catalog 306
and checklist 36 complete / 70 open are unchanged. See docs/rpg-array-search.md.


Native array-search verification follow-up (2026-10-18): all 104 targeted
checks pass after strengthening file-status coverage to preserve EOF, ERROR and
status 1211 across LOOKUP. All 1870 expanded checks pass. The first full run
found a timing-sensitive queue delete/recreate fixture: Task.Run plus a fixed
150ms delay did not establish an existing receive wait. The fixture now uses
a dedicated receiver, observes its blocked state, bounds cleanup with cancellation,
and checks CPF9801, changed queue identity and retained replacement data. Queue
runtime identity checks are unchanged. All 145 targeted RPG/queue checks and the
final display PTY pass; Release build is zero-warning/error. The final full-suite
rerun is active. C09 remains partial; see docs/rpg-array-search.md.


Final RPG native array-search verification (2026-10-18): all 4073 tests
pass, zero failures/skips, including 104 new cases beyond 3969. All 1870 expanded
RPG/CL/queue checks and 145 final targeted RPG/queue checks pass. Locked restore,
zero-warning/error Release solution build, expanded display PTY, generated
inventory and whitespace checks pass. RPGLOOKUP compiles/runs at 24x80; display
fixtures also cover 27x132. Native LOOKUP now supports exact/nearest suffixes,
duplicate boundaries, shared indicators, captured writable indexes, native byte
comparison, figurative values and exact non-float coefficients through 29 digits.
%LOOKUPNE searches scalar/keyed DS arrays sequentially without changing search
status. LOOKUP global FOUND/EQUAL and SETLL per-file EQUAL remain independent;
EOF, ERROR and status 1211 survive a successful array search. Column-limited
DCL-S/C/F/DS declarations share the native pipeline and can precede C-specs.
Saved-source removal, CTDATA/ALT descriptions, CL entry aliases on return/error,
RETURN/LR/reclaim and DS/file forms run in actual CCSID 37/1208 jobs. Failed index
conversion, outer rejection and cancellation preserve index/flags. The first
full run exposed a delay-based queue delete/recreate fixture; it now observes
a dedicated blocked receiver and verifies CPF9801, replacement identity and
retained data. Queue runtime identity checks are unchanged. The opcode-specific
IBM rule sets a missed writable index to 1; conflicting overview text says 0,
so independent native-system validation remains required. Tables, broader
collation/type/ABI/native specifications and native golden/scale acceptance
remain open. Full-suite runtime is 7m16s, not a performance target. Schema stays
31, catalog 306 and checklist 36 complete / 70 open. C09 remains partial.
See docs/rpg-array-search.md.


RPG table/reference-call checkpoint (2026-10-18): global TAB-prefixed DIM
fields now own native tables and expose one current scalar. CTDATA/ALT/inferred
loading, paired native LOOKUP and six TLOOKUP built-ins select matching ordinals;
misses preserve selection. Scalar mutation, layout queries and captured API
arguments use the selected element. Unprototyped stored CL/RPG calls now receive
live caller cells, retaining aliases and writes on callee errors. CL entry
binding validates type/width/scale/CCSID and forwards original storage metadata.
Legacy semantic scalars and ordinary-program prototype coercion/copy remain
supported separately from strict APIs. All 81 new targeted checks and both
related compatibility regressions pass. Locked restore and zero-warning/error
Release build pass. Earlier expanded testing found those two old call contracts;
all 2469 expanded RPG/CL/queue checks and final RPGTABLE/display terminal
acceptance pass. Full-suite verification is pending. Table-entry/array/
procedure ABI, pre-runtime loading, full prototype/binding/service programs,
broader types/collation and independent native golden/scale acceptance remain
open. C09 remains partial; schema 31, catalog 306 and checklist 36 complete / 70
open. See docs/rpg-tables.md.


Final RPG table/reference-call verification (2026-10-18): all 4154 tests
pass, zero failures/skips, including 81 new cases beyond 4073. All 2469 expanded
RPG/CL/queue checks and 83 final targeted checks pass. Locked restore,
zero-warning/error Release build, final RPGTABLE/display PTY, generated inventory,
local documentation links and whitespace checks pass. RPGTABLE compiles/runs at
24x80; general display fixtures also cover 27x132. Table searches select paired
ordinals, preserve selections on misses and keep built-in search flags unchanged.
Current elements support scalar updates and captured native API references.
Stored unprototyped CL/RPG calls preserve aliases and writes after callee errors;
CL reference binding checks type/length/scale/CCSID before execution and retains
raw character buffers. Existing semantic scalar and ordinary-program prototype
coercion/copy contracts remain supported; strict API guards remain unchanged.
Saved-source removal, CTDATA/ALT, RETURN/LR/reclaim, native one-byte integer
aliases and QWCRSVAL receivers run in actual CCSID 37/1208 jobs. Full-suite runtime
is 7m21s, not a performance target. Pre-runtime loading, native table-entry/array/
procedure ABI, complete prototypes/binding/service programs, broader types/
collation/native specifications and independent native golden/scale acceptance
remain open. C09 stays partial; schema 31, catalog 306 and checklist 36 complete /
70 open are unchanged. See docs/rpg-tables.md.


Native fixed-DS checkpoint (2026-10-18): traditional DS headers/subfields
now enter the shared native byte engine. From/To byte positions, packed PACKEVEN,
default zoned subfields, fillers, overlays, arrays, LIKEDS, OCCURS and feedback
bindings preserve source locations. Saved-source removal, QWCRSVAL receivers,
INFDS/PSDS, retained occurrences and live CL aliases run in CCSID 37/1208 jobs.
Terminal acceptance exposed arithmetic validation that only resolved standalone
fields; numeric DS layouts now use the shared lookup resolver, and DO loops write indexed
cells. All 325 final targeted checks and final RPGFIXDS/display terminal acceptance
pass. An earlier expanded run passed 2549 checks before the arithmetic/counter
correction; full-suite verification of the latest build is pending. Locked restore
and zero-warning/error Release build pass. External/data-area/
nested/local DS, PR/PI, wider types/formats and complete native entry/procedure
ABI and independent native golden/scale acceptance remain open. C09 stays
partial; schema 31, catalog 306 and checklist 36 complete / 70 open are unchanged.
See docs/rpg-fixed-structures.md.


Final native fixed-DS verification (2026-10-18): all 4252 tests pass, zero
failures/skips, including 98 new cases beyond 4154 (84 unit and 14 saved-program
integration cases). All 325 final targeted checks pass; the full run includes
2567 RPG/CL/queue checks. The earlier expanded run passed 2549 checks before the
arithmetic/counter correction. Locked restore, zero-warning/error Release build,
final RPGFIXDS/display PTY, generated inventory, documentation links and whitespace
checks pass. RPGFIXDS compiles/runs at 24x80; general display fixtures also cover
27x132. Native DS definitions preserve physical columns, implicit boundaries,
source maps, byte positions, packed PACKEVEN/default zoning, overlays, fillers,
arrays, LIKEDS, occurrences and feedback bindings. Native arithmetic and XFOOT
resolve DS numeric views; DO loops write indexed DS/standalone counter cells and
retain private unnamed counters. Failed conversion preserves overlapping bytes.
Saved-source removal, native QWCRSVAL receivers, INFDS/PSDS, RETURN/LR/reclaim and
live CL aliases on return/error run in actual CCSID 37/1208 jobs. Terminal testing
found and verified the arithmetic-layout correction. Full-suite runtime is
7m13s, not a performance target. External/data-area/nested/local DS, DS-subfield
CTDATA, PR/PI, wider formats/types, full native entry/array/procedure ABI and
independent native golden/scale acceptance remain open. Free-form CALLP(E) remains
unsupported; native C-spec CALLP(E) is covered. C09 stays partial; schema 31,
catalog 306 and checklist 36 complete / 70 open are unchanged.
See docs/rpg-fixed-structures.md.


Native program-signature checkpoint (2026-10-18): native PR/main PI and free
DCL-PR/DCL-PI now support scalar EXTPGM references. Private formal metadata stays
outside runtime variables. Native layouts, aliases, captured addresses, count
checks and retained PI rebinding share the native engine. Free/native CALLP(E)
preserves argument-processing statuses and handles final-call errors as 202;
cancellation still escapes. External process aliases reject before startup, and
all returned reference values normalize before writeback. Saved CL/RPG/API and
complete scalar byte images run in CCSID 37/1208 jobs. First full testing exposed
legacy untyped null-return void acknowledgments; that convention is preserved
separately from typed unresolved-target semantics. Native calls also bypass the
old PLIST shortcut and malformed known-native calls reject with source locations.
All 304 final targeted checks, locked restore, zero-warning/error Release build
and final RPGSIG/RPGSIGUP display terminal acceptance pass. All 2714 final
expanded RPG/CL/queue/external checks pass, including 128 new cases (99 unit,
21 saved-program and 8 external-process cases). Full-suite verification is pending. The obsolete CALLP(E) rejection fixture now checks
unsupported CALLB(E), preserving original-source assertions. A keyed-argument
fixture now preserves original status 105 during preparation, retaining its
rejection/message/no-target assertions and adding free/native E coverage.
CONST/VALUE/OPTIONS, wider reference compatibility,
arrays/DS/table entry ABI, local/bound procedures and complete ILE/native golden/
scale acceptance remain open. C09 stays partial; schema 31, catalog 306 and
checklist 36 complete / 70 open are unchanged. See docs/rpg-program-signatures.md.


Final native program-signature verification (2026-10-18): all 4380 tests
pass, zero failures/skips, including 128 new cases beyond 4252 (99 unit,
21 saved-program and 8 external-process cases). All 304 targeted checks and
2714 final expanded RPG/CL/queue/external checks pass. Locked restore,
zero-warning/error Release build, final RPGSIG/RPGSIGUP display PTY, generated
inventory, documentation links and whitespace checks pass. RPGSIG/RPGSIGUP run
at 24x80; general display fixtures also cover 27x132. Native D-spec PR/main PI
and free DCL-PR/DCL-PI now describe scalar EXTPGM references. Formal metadata
stays outside runtime variables; PI cells retain native layouts, aliases,
captured addresses and activation rebinding. Source removal, CL/RPG/API calls,
complete scalar byte images, PSDS counts and retained calls run at actual CCSID
37/1208. Free/native CALLP(E) handles final target failures as 202 while argument
processing retains 102/105/121 and cancellation escapes. Native calls bypass
PLIST-name shortcuts and malformed known-native calls reject at source locations.
External process aliases reject before startup; all returned reference values
normalize before assignment, including valid writes with reported failures.
Verification retained legacy untyped null-return void acknowledgments, replaced
an obsolete CALLP(E) rejection fixture with unsupported CALLB(E), and updated
keyed-selection expectations to original status 105 while preserving rejection,
message and no-target assertions. Full-suite runtime is 7m39s, not a performance
target. CONST/VALUE/OPTIONS, wider compatibility, native array/DS/table entry ABI,
local/bound procedures, modules/service programs/binding and independent native
golden/scale acceptance remain open. C09 stays partial; schema 31, catalog 306
and checklist 36 complete / 70 open are unchanged.
See docs/rpg-program-signatures.md.


Native CONST/prefix checkpoint (2026-10-18): native scalar PR/main PI
parameters now accept CONST. Matching native fields remain live read-only aliases;
literals, expressions and different formats use private formal-width images before
dispatch. Native character prefixes expose formal byte ranges and preserve parent
tails through nested calls. Compile/runtime/API guards prevent readonly writes;
process responses may echo unchanged CONST values while validating all values
before mutable output assignment. Source removal, queue input/receiver rejection,
live PI aliases, numeric copies and nested prefixes run at CCSID 37/1208. All
241 targeted checks, locked restore, zero-warning/error Release build and final
RPGCONST/RPGCSTUP terminal acceptance pass. The expanded run passed 3165 of
3169 checks; four waiting/process checks failed, then all eight cases in their
focused rerun passed. The full Release suite now passes all 4439 tests with
zero failures/skips in 7m25s. The net 59 new cases comprise 43 scalar/guard/codec
cases, 12 saved-program cases and four external-process cases; two existing
CONST-rejection cases now reject malformed CONST(7). Generated inventory, local
document links and whitespace checks pass. No performance target is claimed.
VALUE/OPTIONS, array/DS/table entry ABI, local/bound procedures, wider modes and
independent native golden/scale acceptance remain open. C09 stays partial;
schema 31, catalog 306 and checklist 36 complete / 70 open are unchanged.
See docs/rpg-const-parameters.md.


Native optional-parameter checkpoint (2026-10-18): native scalar PR/main PI
and free DCL-PR/DCL-PI accept trailing OPTIONS(*NOPASS), including CONST. Calls
preserve actual counts through RPG, CL variables/typed constants, native APIs
and external processes. %PASSED tests optional presence; %PARMNUM now returns
real PI positions and works in declaration constants. AND/OR short-circuit
before absent values, invalid indexes or arithmetic. Retained activations
recompute presence, and absent storage/forwarding fails with emulator status
105 before dispatch. Invalid modes, nontrailing optionals, mismatched named PI,
invalid builtin operands and wrong counts retain source diagnostics. All 261
targeted checks, locked restore, zero-warning/error Release build and final
RPGOPTION/RPGOPTUP terminal acceptance pass. All 4509 solution tests pass with zero failures/skips
in 7m33s. The 70 new cases comprise 57 unit, 10 saved-program and three
external-process cases; three obsolete native NOPASS rejection fixtures now
reject unsupported *OMIT modes. Generated inventory, local document links and
whitespace checks pass. No performance target is claimed.
Other OPTIONS modes (*OMIT/*VARSIZE etc.), %OMITTED, VALUE, native array/DS/table
entry ABI, local/bound procedures, modules/service programs/binding, wider modes
and independent native golden/scale acceptance remain open. C09 stays partial;
schema 31, catalog 306 and checklist 36 complete / 70 open are unchanged.
See docs/rpg-optional-parameters.md.


Native omitted-argument checkpoint (2026-10-18): scalar native PR/main PI
and free DCL-PR/DCL-PI accept OPTIONS(*OMIT), including CONST and combined
*NOPASS. Explicit omissions occupy parameter positions without storage;
%PARMS/PSDS include them, %PASSED is false and %OMITTED is true. Retained
activations recompute present/omitted/unpassed states. Scoped descriptors
preserve null semantic values, no buffer/range, non-aliasing and expiration;
external protocols 1/2 encode null slots and reject altered omission responses
before mutable writeback. Native API storage checks preserve queue entries and
other outputs on rejection. Both native column origins, C-spec CALLP, maximum
255-position omissions, nested saved-program forwarding and CCSID 37/1208
states pass. All 312 targeted checks, locked restore, zero-warning/error Release
build and final RPGOMIT/RPGOMITUP terminal acceptance pass. All 4573 solution tests pass with zero
failures/skips in 7m43s wall time. The 64 new cases comprise 50 unit, 10
saved-program and four external-process cases. Obsolete native *OMIT rejection
fixtures now reject duplicate *OMIT options. Generated inventory, local links
and whitespace checks pass. No performance target is claimed. Other OPTIONS modes, VALUE, native array/DS/table entry ABI,
local/bound procedures, modules/service programs/binding, CL receiver-side
optional/omitted entry handling, additional API pointer contracts, wider modes
and independent native golden/scale acceptance remain open. C09 stays partial;
schema 31, catalog 306 and checklist 36 complete / 70 open are unchanged.
See docs/rpg-omitted-parameters.md.


Native local-procedure checkpoint (2026-10-18): P-spec B/E and DCL-PROC/END-PROC
now define same-unit native procedures. Explicit/automatic scalar PR/PI bind
CALLP/implicit/expression calls with VALUE copies, CONST/live references,
NOPASS/OMIT and native scalar returns. Separate invocation storage supports
recursion and automatic locals/arrays/structures; scalar/array STATIC values
persist with the module and reset on activation reclaim. Global names resolve
in the module, preserving shared buffers, indicators, file positions, table
selections and occurrences. PSDS main counts remain distinct from local %PARMS;
local PSSR can return/recover, must use blank ENDSR operands and abnormally ends
on fallthrough. Native procedure I/O against INFSR files and local files/PSDS
reject. Cancellation and borrowed-cell expiry preserve cleanup. Limits cover
128 frames, 64 MiB declared invocation images and 16 MiB retained STATIC banks.
All 377 targeted checks, locked restore, zero-warning/error Release build and
final RPGPROC terminal acceptance pass. All 4643 solution tests pass with zero
failures/skips in 7m30s wall time. The 70 new checks comprise 58 unit, nine
saved-program/integration and three external-process cases; no existing cases
were removed. Generated inventory, document links and whitespace checks pass.
No performance target is claimed. Cross-unit
modules/service programs/binding, aggregate/foreign ABI, procedure pointers,
further storage/CCSID modes, full cycle/exception/activation semantics and
independent native golden/scale acceptance remain open. C09 stays partial;
schema 31, catalog 306 and checklist 36 complete / 70 open are unchanged.
See docs/rpg-local-procedures.md.


Native aggregate-parameter checkpoint (2026-10-18): native/free PR/PI now
accept fixed DIM arrays and LIKEDS structures/structure arrays through program
and same-unit procedure calls. Byte-backed references preserve element/subfield
aliases, formal prefixes and caller tails through nested calls. CONST aliases
remain live; local VALUE copies are private; numeric array conversions use the
formal codec and CONST/VALUE SUBARR inputs use selected private images. Native
column origins, readonly/source guards, named PR/PI layouts, NOPASS/OMIT
presence and pre-dispatch short-array rejection retain existing bounds.
Aggregate call layouts remain separate from fixed LOOKUP validation, preserving
its whole-structure-array rejection. Bulk reads refresh one image and bulk
assignment validates and publishes one complete image, avoiding a full refetch
per element. CL character storage/constants, plain host arrays, real queue API
buffers and external protocol-2 response/overlap checks are covered. All 602
targeted checks, locked restore, zero-warning/error Release build and RPGAGG
terminal acceptance pass. All 4716 solution tests pass with zero failures/skips
in 7m40s wall time. The 73 new checks comprise 60 unit, ten
saved-program/integration and three external-process cases; no cases were
removed. Two obsolete native fixed-DIM rejection fixtures now reject
unsupported DIM(*VAR). Generated inventory, document links and whitespace
checks pass. No performance target is claimed. Aggregate returns, variable-size
or dynamic dimensions, general table entry ABI, nested structures, foreign ABI,
procedure pointers, modules/service programs/cross-unit binding and independent
native golden/scale acceptance remain open. C09 stays partial; schema 31,
catalog 306 and checklist 36 complete / 70 open are unchanged. See
docs/rpg-aggregate-parameters.md.


Native return-value checkpoint (2026-10-18): traditional/free procedure PR/PI
now support fixed DIM arrays, LIKEDS structures/structure arrays and RTNPARM.
RETURN uses assignment conversions: scalars broadcast, missing array elements
receive native defaults and surplus elements are ignored. Detached results
preserve earlier return values and caller receiver tails, compose through array
expressions and feed CONST parameters/API inputs. Named/automatic prototypes
retain matching return layouts and modes; native column origins and LIKEDS
header keyword order are covered. Template lookup is deferred from file
discovery to declaration binding. RTNPARM includes its hidden first slot in
%PARMS and runtime/declaration %PARMNUM while preserving optional/omitted
explicit bindings and main PSDS counts. It permits 254 explicit parameters
within the existing 255-position bound. Return images are admitted under the 64
MiB invocation budget before entering a frame. Conversion/fallthrough errors,
scalar receiver rejection, cancellation and cleanup preserve caller receivers.
Whole-structure CLEAR now validates and publishes through borrowed bindings,
including structure-array prefixes and outer-normalizer rejection. All 555
targeted checks, locked restore, zero-warning/error Release build and RPGRETURN
terminal acceptance pass. All 4789 solution tests pass with zero failures/skips
in 7m38s wall time. The 73 new cases comprise 63 unit, eight
saved-program/integration and two real external-process cases; no cases were
removed. Generated inventory, document links and whitespace checks pass. No
performance target is claimed. Additional return types/formats, LIKE/LIKEREC,
RETURN precision extenders, variable-size/dynamic layouts, table entry ABI,
nested structures, procedure pointers, foreign ABI, module/service-program
binding and independent native golden/scale acceptance remain open. C09 stays
partial; schema 31, catalog 306 and checklist 36 complete / 70 open are
unchanged. See docs/rpg-return-values.md.


Native variable-size checkpoint (2026-10-18): native/free PR/PI now accept
OPTIONS(*VARSIZE) for reference character/LIKEDS storage and native arrays,
including CONST, NOPASS and OMIT combinations. Declared sizes remain distinct
from actual capacity and character-array stride; the IBM search pattern runs at
CCSID 37/1208. Bounded ProgramArgumentMemory views retain live aliases, backing
capacity through fixed prefixes, readonly status, byte-range validation and
scope expiry. Partial reads/writes, numeric SUBARR and short-array RHS
assignment preserve other bytes/elements; out-of-actual access reports 105
before mutation and declared index violations retain 121. Byte-buffer character
comparison blank-pads shorter operands and substring addresses evaluate once.
Entry detachment owns its byte snapshots without retaining expired cells.
Actual backing capacities count toward the existing 1 MiB call/entry limit
before dispatch. Scalar/array rank, mutable numeric layouts and named PR/PI
passing modes remain checked. CL variables/explicit character constants,
retained rebinding, unprototyped readonly API rejection, real queue buffers,
external actual-byte transfer, complete response validation and process alias
rejection are covered. All 812 targeted checks, locked restore,
zero-warning/error Release build and RPGVARSIZE terminal acceptance pass. All
4841 solution tests pass with zero failures/skips in 7m36s wall time. The 52
new cases comprise 41 unit/core-memory, eight saved-program/integration and
three external-process cases; no cases were removed. Generated inventory, local
links and whitespace checks pass. No performance target is claimed. VARYING,
graphic/UCS-2 types, dynamic arrays, other OPTIONS modes, operational
descriptors, CL receiver-side variable layouts, general table entry ABI,
procedure pointers/foreign ABI, module/service-program binding and independent
native golden/scale acceptance remain open. C09 stays partial; schema 31,
catalog 306 and checklist 36 complete / 70 open remain unchanged. See
docs/rpg-variable-parameters.md.

Native parameter-options checkpoint (2026-10-18): native/free PR/PI now accept
OPTIONS(*EXACT), *TRIM and *RIGHTADJ for the supported native types. Exact
references check lengths, precision, array dimensions/order and common LIKEDS
origins; CONST/VALUE conversions must fit their numeric positions and
character widths. Related structure return metadata and local shadowed
definitions retain type identity. Character transformations use private byte
images, preserve caller storage and apply to each array element; *EXACT checks
the trimmed value before truncation. UTF-8 byte lengths and opaque null bytes
are covered. Explicit PR caller options need not repeat on PI; PR/PI array
order must agree. Optional/omitted/RTNPARM positions, writable output
receivers with invalid input bytes, readonly lifetime checks and 1 MiB
temporary admission retain their rules. Known incompatible declarations reject
during compilation; remaining argument failures report 105 through MONITOR
before target dispatch. Saved programs run after source removal at CCSID
37/1208, including real queue bytes. External processes verify transformed
inputs, unchanged readonly echoes, atomic response rejection and no launch on
invalid exact values. A loaded full run exposed a queue fixture that blocked
pool workers before producers could run; concurrent queue workers now use
dedicated threads, a shared cancellation deadline and complete cleanup.
Production queue/session behavior is unchanged. The first full run passed
4911/4913 cases with queue/socket timing failures; all four disconnect/ENDJOB
variants and the corrected queue test pass in the expanded focused rerun. All
798 focused checks, locked restore, zero-warning/error Release build and
RPGOPTIONS terminal acceptance pass. All 4913 solution tests pass with zero
failures/skips in 7m45s wall time. The 72 new cases comprise 61 unit, eight
saved-program/integration and three external-process cases; no cases were
removed. Generated inventory, local documentation links and whitespace checks
pass. Pointer/STRING/CONVERT/ NULLIND modes, varying/graphic/UCS-2 types,
LIKE/LIKEREC ancestry, nondefault date/time formats, enumeration/object
interfaces, foreign ABI and cross-unit module/service-program binding remain
open. Independent IBM-system golden and scale acceptance remain open; no
performance target is claimed. C09 remains partial; schema 31, catalog 306 and
checklist 36 complete / 70 open are unchanged. See
docs/rpg-parameter-options.md.

Native LIKE checkpoint (2026-10-18): native/free standalone fields, subfields,
PR/PI parameters and returns now accept LIKE of available supported native
types and returning prototypes. Kind, length, scale and opaque/text mode are
inherited; initialization, dimension/order, passing and storage flags remain
explicit. Character and numeric signed adjustments retain native bounds and
integer digit widths. Fixed forms use signed Length columns with blank type/
decimal columns at both origins; structure-relative OVERLAY/*NEXT mappings
remain intact. CHAR CCSID(*DFT) can select job text after opaque inheritance.
LIKE chains retain definition identities through local/global scopes,
LIKEDS-cloned subfields, array elements and declared return metadata. EXACT
requires related types plus existing layout/precision/dimension/order checks;
unrelated declarations reject during compilation and untyped values fail 105
before dispatch. Resolved character modes support CONST/VALUE transformations
and compatible VARSIZE interfaces. Unsupported varying/date-time attributes
cannot silently become fixed/default types. Source-removal/retained PI
rebinding, real queue bytes, related subfield returns, failed program
creation, process readonly echoes/atomic rejection and no-launch errors are
covered. All 1115 focused checks, locked restore, zero-warning/error Release
build and RPGLIKE terminal acceptance pass. All 4977 solution tests pass with
zero failures/skips in 7m51s wall time, including 64 new cases: 53 unit, eight
saved-program/integration and three external-process cases. No cases were
removed. Generated inventory, documentation links and whitespace checks pass.
Forward references/unavailable implicit prototypes, LIKE(*EXT)/object/
enumeration forms, varying/graphic/UCS-2/pointer types, nondefault date/time
formats, LIKEREC lineage, foreign ABI, cross-unit modules/service programs and
independent native-system golden/scale acceptance remain open. No performance
target is claimed. C09 remains partial; schema 31, catalog 306 and checklist
36 complete / 70 open are unchanged. See docs/rpg-like-declarations.md.

Native forward-type checkpoint (2026-10-18): LIKE now resolves later native
fields, subfields, PI parameters and explicit/implicit return types without
reordering storage, initializers or overlays. Lazy cached metadata uses stable
source-definition identities across module/procedure scopes, retaining EXACT
ancestry through preview and final binding. Later local fields/structures and
constant widths shadow globals; original case normalization applies in both
phases. Return types can use later local declarations and constants available
at their own definitions, including prior PI parameter numbers/RTNPARM
offsets. Forward members, anonymous structures, LIKEDS-copied fields and final
INFDS widths are covered. Preview omits initializers and never calls a
procedure to obtain a value. Ordinary forward value references remain
unsupported. Missing/ void types, cycles across types/constants/implicit
returns and more than 64 active dependencies report source/include diagnostics
before object creation; cancellation and existing declaration/storage limits
remain enforced. Compiler lookup callbacks detach on success and failure. All
1154 focused checks, locked restore, zero-warning/error Release build and
RPGFORWARD terminal acceptance pass. All 5026 solution tests pass with zero
failures/skips in 7m49s wall time. The 49 new cases comprise 38 unit, eight
saved-program integration and three external-process cases; none were removed.
Source-removal, real queue bytes, cyclic include rejection, process readonly
echoes/atomic writeback and no launch on invalid exact types are verified.
Generated inventory, local documentation links and whitespace checks pass.
General forward value expressions, implicit calculation-result types, forward
LIKEDS structure binding, external/object/ enumeration forms,
varying/graphic/UCS-2/pointer types, nondefault date/time formats, LIKEREC,
foreign ABI and cross-unit module/service-program binding remain open.
Independent native golden/scale acceptance remains open; no performance target
is claimed. C09 remains partial; schema 31, catalog 306 and checklist 36
complete / 70 open are unchanged. See docs/rpg-forward-declarations.md.

Native forward-LIKEDS checkpoint (2026-10-18): structure declarations, PR/PI
parameters and returns now accept later named native templates. Metadata-only
and initialized definition images resolve separately; INZ(*LIKEDS) inherits
subfield initialization and blank/default treatment without sharing runtime
storage. Plain LIKEDS and argument-free INZ retain their distinct behaviors;
CLEAR still applies type defaults. DIM/OCCURS stay explicit on the copy while
subfield arrays, fillers, positions and overlays survive copying. Stable
structure ancestry tokens preserve EXACT across preview and final binding;
independently defined equal layouts remain unrelated. Later local templates
and constants shadow globals without changing imported global copied layouts.
Forward chains propagate final INFDS extents independent of source order,
including interface layouts. Status/indicator parameter restrictions and
explicit feedback-size validation remain enforced. Missing/non-structure
parents, cycles, depth and existing storage limits retain source/include
locations, and compiler callbacks detach on every exit. All 1265 focused
checks, locked restore, zero-warning/error Release build and RPGFWDDS terminal
acceptance pass. All 5071 solution tests pass with zero failures/skips in
7m56s wall time. The 45 new cases comprise 31 unit, 11 saved-program
integration and three external-process cases; none were removed.
Source-removal, retained PI rebinding, real queue bytes, inherited return
defaults, reverse feedback chains, cyclic include rejection and process atomic
writeback/no-launch errors are covered at the documented CCSIDs. A loaded
verification run exposed a short-lived CPU fixture that could exit before live
accounting sampled it; the fixture now waits for observed CPU/thread data and
releases/drains its child with bounded cancellation. Production process
accounting is unchanged. Generated inventory, local links and whitespace
checks pass. Nested/external/LIKEREC structures, TEMPLATE storage suppression,
general forward value expressions, broader native types/formats, foreign ABI
and module/service-program binding remain open. Independent native
golden/scale acceptance remains open; no performance target is claimed. C09
stays partial; schema 31, catalog 306 and checklist 36 complete / 70 open
remain unchanged. See docs/rpg-forward-structures.md.

RPG data-template checkpoint (2026-10-18): standalone fields and native data structures
now accept argument-free TEMPLATE in free declarations and both supported traditional
D-spec origins. Templates retain layouts, initializers and exact-type ancestry without
allocating runtime slots, arrays, structures or occurrences. LIKE/LIKEDS copies and
PR/PI/return layouts use ordinary storage under existing limits; template attributes do
not leak onto copies. Forward lookup, local shadowing and inherited INZ(*LIKEDS) remain
supported. Runtime %SIZE/%ELEM use declared metadata with constant subscripts;
declaration-only %LEN/%DECPOS are supported, with floating-point %DECPOS rejected. Data
access, calls, traditional PLIST/KLIST operands and INFDS/INDDS/SFILE runtime bindings
reject template storage, with status-105 guards on direct runtime access. Literal
prefixes, exponents, special values, labels and qualified member names retain their
proper meaning, including whitespace around qualification. Templates do not consume
allocation-dependent storage/invocation budgets: tests describe 1,073,676,289-byte
scalar arrays and 34,358,689,792-byte structure arrays without allocating them.
Component limits and a 256-visible-template bound remain enforced. Saved programs remove
source and exercise real queue buffers, inherited local returns, large metadata and
include diagnostics at CCSIDs 37/1208. Process tests verify valid/malformed response
atomicity and rejection before launch. All 5122 solution tests pass with zero
failures/skips in 7m39s, including 51 new cases: 39 unit, nine saved-program integration
and three external-process cases. All 779 final focused checks, locked restore, a
zero-warning/error Release build and expanded terminal acceptance with RPGTMPL pass.
RPGTMPL runs at 24x80; general display acceptance also covers 27x132. Generated
inventory, local links and whitespace checks pass. File TEMPLATE/LIKEFILE, complete
CTDATA template behavior, nested/external/LIKEREC layouts, broader types and ILE
binding, and independent native golden/scale acceptance remain open. C09 stays partial;
schema 31, catalog 306 and checklist 36 complete / 70 open remain unchanged. See
docs/rpg-templates.md.

DISK record-structure checkpoint (2026-10-18): LIKEREC now defines qualified native
structures from declared DISK records with default/*INPUT, *OUTPUT, *ALL and packed
key-order *KEY layouts. RENAME/PREFIX, original record positions, both supported
traditional D-spec origins, TEMPLATE/LIKEDS inheritance, forward type metadata and exact
ancestry are supported. PR/PI parameters and returns accept record layouts, including
fixed DIM, CONST/VALUE and RTNPARM. CHAIN/READ/READP/READE/READPE and WRITE/UPDATE
accept checked result structures or indexed array elements in free and native fixed
calculations. Origins and extract modes must match; unrelated equal-sized buffers, whole
arrays, template receivers, readonly input and feedback-resized layouts reject before
execution. Receiver addresses are captured once before cursor movement; misses/EOF
preserve bytes. Input stages conversions before publishing, leaves imported scalar
fields unchanged and releases update locks on conversion failure. Output snapshots and
validates fields before file mutation. Metadata discovery is lazy and cached per file
request, preserving existing feedback/INFSR validation order; callbacks detach on
success/failure. Saved programs restore layouts after source and a separate compile-time
description file are removed. All 5167 solution tests pass with zero failures/skips in
8m13s, including 45 new cases: 42 record/metadata/integration cases and three
external-process cases. Historical calculation extensions retain their legacy record
operands. All 1202 broad focused checks and 124 final record/display/legacy checks pass,
as do locked restore, a zero-warning/error Release build and expanded RPGREC terminal
compile/call acceptance. RPGREC runs at 24x80; general display acceptance also covers
27x132. Process cases verify valid/malformed output atomicity and rejection before
launch; source-removal and native record cases cover CCSIDs 37/1208. Generated
inventory, local links and whitespace checks pass. This supplies prerequisites required
by file LIKEFILE; file TEMPLATE/LIKEFILE itself remains open, together with WORKSTN
result structures, EXTNAME/nested/multi-format/null layouts, complete CHARCOUNT/CCSID
behavior, broader ILE binding and independent native golden/scale acceptance. C09 stays
partial; schema 31, catalog 306 and checklist 36 complete / 70 open remain unchanged.
See docs/rpg-record-structures.md.

DISK file-definition checkpoint (2026-10-18): module-level TEMPLATE, QUALIFIED, LIKEFILE
and explicit BLOCK(*NO) now work in free and native fixed F specifications, including
continuations and both supported column origins. Templates preserve descriptions without
scalar imports, runtime file slots or automatic opens; file/record symbols reject
runtime data/status access, including data-name shadows and direct runtime guards.
LIKEFILE requires an earlier DISK parent, inherits usage/keyed
access/descriptions/prefixes/renames and constant EXTFILE settings, qualifies record
names and requires result-buffer I/O. Default runtime filenames follow the copy's own
name; TEMPLATE and USROPN do not inherit. Copies and template intermediates share record
ancestry for LIKEREC and exact calls while retaining independent cursors/open state.
Native fixed blank file attributes require LIKEFILE. Inherited-property overrides,
ambiguous input blocking, unsupported blocked/workstation modes and template runtime
settings reject explicitly. Version-4 bindings persist templates, qualification and
family relationships; ordinary bindings retain version 3 with versions 1–3
compatibility. Restore validates family graphs and inherited layouts; runtime
recompilation verifies every saved binding, including derived copies, before execution.
Root descriptors are read once, and source/description-file removal is supported with
compatible runtime files. All 5208 solution tests pass with zero failures/skips in
8m24s, including 41 new cases: 38 file-definition/metadata/integration cases and three
process cases. All 1031 final focused checks, locked restore, a zero-warning/error
Release build and expanded RPGFILES terminal compile/call acceptance pass. Process
checks cover related exact buffers, valid/malformed response atomicity and rejection
before launch. Saved cases cover CCSIDs 37/1208; RPGFILES runs at 24x80 and general
display acceptance covers 27x132. Generated inventory, local links and whitespace checks
pass. LIKEFILE file parameters, procedure-local files, WORKSTN templates/copies/result
structures, blocked I/O, broader external/member options, ILE linking and independent
native golden/scale acceptance remain open. C09 stays partial; schema 31, catalog 306
and checklist 36 complete / 70 open remain unchanged. See docs/rpg-file-definitions.md.

DISK file-parameter checkpoint (2026-10-18): native PR/PI LIKEFILE parameters now
borrow caller-owned DISK files across local procedures and saved RPG programs.
Earlier parent definitions and determined blocking are required; file parameters
qualify record names and supply LIKEREC layouts, including forward return types
and RTNPARM positions. Only NOPASS/OMIT options are accepted. File actuals require
the same family at call sites and compatible external layouts/modes at separate
program entries. Cursors, current records, update rights, locks, open state and
named-file feedback remain shared. Callee OPEN/CLOSE uses the actual owner's
settings. Caller INFDS and INFSR remain active; unhandled errors support RETURN
and GOTO to owner frames, including across saved program calls. E extenders and
matching MONITOR handlers run first. References expose no bytes or native
memory, expire at call boundaries in borrowed cells and semantic host arguments, and rebind retained entries on every call.
Forwarding preserves owner/lifetime checks. File parameters can shadow other
global file names, while local data shadows retain global file-parameter bindings
for file operands. Shadowing a parameter's own parent rejects explicitly.
File data/layout access, unsupported options, templates/unrelated actuals,
non-RPG targets and more than 16 visible files reject. Main PI bindings use
version 5; ordinary programs retain prior versions and versions 1–4 restore.
Parameter flags, family graphs and every reconstructed binding are verified.
Record metadata caches separate equal layouts from unrelated parent families.
Source and separate description-file removal are supported. All 5264
solution tests pass with zero failures/skips in 8m21s, including 56 new cases:
54 parameter/metadata/integration cases and two external-process cases. All 1355
broad and 158 final focused checks, locked restore, a zero-warning/error Release
build and expanded RPGFPRM terminal compile/call acceptance pass. Process cases
reject before side effects in both protocol versions; saved cases cover CCSIDs
37/1208. RPGFPRM runs at 24x80, and general display acceptance covers 27x132.
Generated inventory, local links and whitespace checks pass. Procedure-local file
instances, WORKSTN file parameters/templates/copies/result structures, blocked I/O,
broader external/member options, ILE linking and independent native golden/scale
acceptance remain open. C09 stays partial; schema 31, catalog 306 and checklist
36 complete / 70 open remain unchanged. See docs/rpg-file-parameters.md.

Procedure-local DISK checkpoint (2026-10-18): native free DCL-F and both supported
traditional F-spec origins now define automatic and STATIC files inside
subprocedures. Local record I/O requires result structures and does not import
scalar fields. Local DISK names/records shadow global DISK definitions while
retaining independent file ownership; private file/record names reject in other
scopes. LIKEFILE copies use earlier local/global parents without inheriting
STATIC, preserve default/constant runtime-name rules and borrow through file
parameters with the actual procedure's ownership and lifetime. Automatic files
open per invocation and close on every normal/abnormal exit; recursive calls
have independent cursors. STATIC files retain cursors, locks, open state and
named feedback across calls, recursion and LR; explicit CLOSE does not re-open
on the next call. Activation reclaim/end, replacement, job end and failed
activation cleanup close retained handles. Cycle globals reset on LR while
procedure STATIC data/files remain in the activation. INFDS must match file scope
and storage. Local INFSR supports RETURN/GOTO, including implicit-open failures
and errors through borrowed parameters. STATIC native structures/arrays allocate
once under the module's 16 MiB bound and do not consume each recursive frame's
automatic-storage budget. Named global file status follows its shared owner.
Version-6 bindings save local scopes and STATIC flags, verify all reconstructed
requests/layouts and retain versions 1–5 compatibility. Parent graphs and
metadata caches preserve nominal record ancestry; forward LIKE of local record
returns keeps EXACT identity through preview/final binding. Bounded streaming
serialization rejects oversized metadata while writing; existing source,
layout and visible-file limits remain enforced. Saved programs survive removal
of source and a separate description file. All 5311 solution tests pass
with zero failures/skips in 8m27s, including 47 new cases: 44 local
file/metadata/integration cases and three external-process cases. All 1409 broad
and 208 final focused checks, locked restore, a zero-warning/error Release build
and expanded RPGLOCAL terminal compile/call acceptance pass. Process cases cover
valid/malformed writeback atomicity and rejection before launch. Saved cases
cover CCSIDs 37/1208; RPGLOCAL runs at 24x80, and general display acceptance covers
27x132. Generated inventory, local links and whitespace checks pass. Local
WORKSTN files/cross-device shadows, WORKSTN templates/copies/result structures,
blocked I/O, broader external/member options, ILE linking and independent native
golden/scale acceptance remain open. C09 stays partial; schema 31, catalog 306
and checklist 36 complete / 70 open remain unchanged. See docs/rpg-local-files.md.

WORKSTN record-buffer checkpoint (2026-10-18): global display formats now supply
native LIKEREC INPUT/OUTPUT/ALL extracts. Display numerics use zoned bytes with
one byte per digit. EXFMT requires ALL; READ/CHAIN/READC accept INPUT or ALL;
WRITE/UPDATE accept OUTPUT or ALL. Subfile buffers include stored hidden/output
fields on input. Native free and fixed result operands accept a matching
structure or one indexed element, captured once, and bypass imported scalar
fields. LIKEDS copies, exact native parameters, value returns and RTNPARM retain
record ancestry. Readonly input rejects before terminal transfer; readonly
output parameters remain usable. Input stages field conversions, RRN and
response-indicator updates before host acknowledgement, preserving receiver
bytes and pending input on conversion failure. CA responses, misses and EOF
preserve bytes. Output snapshots one image before transfer. Lazy cached display
metadata supports forward return types and detaches compiler callbacks.
Saved layouts reconstruct buffers after source removal, preserving RENAME,
PREFIX, CCSIDs 37/1208, typed dates/times and opaque SFLMSGKEY bytes. Native
external-process buffers use the zoned layout and preserve atomic writeback.
All 5347 solution tests pass with zero failures/skips in 8m25s,
including 36 new cases: 32 record/metadata/integration cases and four external-
process cases. The broader run passed 1447 other checks; its date/time fixture
failure was corrected and passes in all 36 final focused checks and the full
suite. Locked restore, a zero-warning/error Release build and expanded RPGWREC
terminal compile/call acceptance pass. RPGWREC runs at 24x80, and general display
acceptance covers 27x132. Generated inventory, local links and whitespace checks
pass. WORKSTN templates/copies/file parameters, local WORKSTN files/cross-device
shadows, zero-field extracts, nested/multi-format/null layouts, blocked I/O,
broader external/member/CCSID options, ILE linking and independent native golden/
scale acceptance remain open. C09 stays partial; schema 31, catalog 306 and
checklist 36 complete / 70 open remain unchanged. See docs/rpg-workstation-records.md.

WORKSTN file-definition checkpoint (2026-10-18): global free and native fixed
WORKSTN declarations now support TEMPLATE, QUALIFIED and LIKEFILE. Templates
supply descriptions without imported scalar fields, runtime handles or terminal
connections. Copies inherit earlier parent descriptions, usage, prefixes,
renames and explicitly constant runtime targets; default runtime names follow
the copy. LIKEFILE implies qualified formats and result-buffer I/O. Runtime
copies keep independent display sessions, pending writes, subfile positions,
open state and file status. USROPN, INDDS, INFDS and INFSR are not inherited;
copies may supply their own. Template runtime settings, file/status/data uses
and inherited attribute overrides reject. SFILE templates omit the RRN; runtime
copies must bind each inherited subfile to their own RRN using unqualified
format names. Qualified format/control mappings retain subfile input behavior.
Related copies share LIKEREC/EXACT ancestry through native parameters, returns
and forward metadata; unrelated equal layouts remain distinct. Root metadata
resolves once, and copies derive from cached parent snapshots. Version-3 saved
WORKSTN bindings verify parent order/families, inherited layouts, names/prefixes
and every reconstructed copy, including requests that bypass the resolver.
Ordinary versions 1/2 and legacy host indicator metadata remain supported.
Bounded serialization enforces its 1 MiB limit while writing, and file counts
follow inherited devices. Source and separate description removal are supported.
All 5399 solution tests pass with zero failures/skips in 8m37s,
including 52 new cases: 48 definition/metadata/integration cases and four added
external-process cases. All 1534 broad, 248 final focused and 42 job-lock checks, locked
restore, a zero-warning/error Release build and expanded RPGWCOPY terminal
compile/call acceptance pass. Process cases cover CCSIDs 37/1208, related exact
buffers and valid/malformed atomic writeback. RPGWCOPY runs at 24x80; general
display acceptance covers 27x132. Generated inventory, local links and whitespace
checks pass. The deadlock regression fixture now uses a dedicated cancellable
worker, preventing its observation from racing a short waiter expiry while
retaining deadlock/wakeup assertions. Procedure-local WORKSTN files/cross-device shadows, WORKSTN file
parameters, zero-field extracts, nested/multi-format/null layouts, blocked I/O,
broader external/member/CCSID options, ILE linking and independent native golden/
scale acceptance remain open. C09 stays partial; schema 31, catalog 306 and
checklist 36 complete / 70 open remain unchanged. See docs/rpg-workstation-definitions.md.

WORKSTN file-parameter checkpoint (2026-10-18): native PR/PI LIKEFILE interfaces
now borrow caller-owned WORKSTN files across local procedures and saved RPG
programs. Qualified parameter records supply checked LIKEREC buffers, exact
interfaces and forward return layouts. Actual display sessions, pending writes,
subfile state, RRN variables, indicator areas, open state and named status stay
shared. OPEN/CLOSE use the actual owner's runtime settings. Caller INDDS, INFDS
and INFSR remain active, with normal E/MONITOR precedence and RETURN/GOTO to
owner frames across saved calls. Review found that a main owner's RETURN set
program-end state without unwinding the callee; owner-error dispatch now unwinds
on every handled ending state, with regressions for both DISK and WORKSTN.
NOPASS/OMIT, parameter positions, forwarding and retained-entry rebinding retain
their lifetime checks. Borrowed cells and semantic references expose no native
bytes/storage and expire at call boundaries. Templates, unrelated actuals,
incompatible layouts, data uses/options and non-RPG targets reject. Local
parameters can shadow global files of either device, while local data shadows
retain global file operands. LIKEREC resolves active parameter qualifiers before
imported record metadata, including forward returns; records hidden by a
parameter of another device reject. Later local definitions retain precedence.
Parameter parents must be earlier file definitions;
a parameter cannot shadow its own parent. WORKSTN blocking is already known.
Version-4 main-PI WORKSTN bindings preserve parameter flags and verify every
reconstructed binding/family; ordinary versions 1–3 remain supported. Scoped
local parameter layouts derive from saved global snapshots. Source and separate
description removal are supported. Existing visible-file and metadata limits
remain active. All 5438 solution tests pass with zero failures/skips in
8m39s, including 39 new cases: 36 WORKSTN parameter/metadata/integration
cases, one DISK owner-return regression and two external-process cases. All 1642
broad and 219 final scoped checks, locked restore, a zero-warning/error Release
build and expanded RPGWFPRM terminal compile/call acceptance pass. Saved cases
cover CCSIDs 37/1208; external cases reject before side effects in both protocol
versions. RPGWFPRM runs at 24x80; general display acceptance covers 27x132.
Generated inventory, local links and whitespace checks pass. Two existing API
failures in the first full run did not recur in all 16 owning-class checks or
the full rerun. Procedure-local
WORKSTN file instances and local-file cross-device shadows, zero-field extracts,
nested/multi-format/null layouts, blocked I/O, broader external/member/CCSID
options, ILE linking and independent native golden/scale acceptance remain open.
C09 stays partial; schema 31, catalog 306 and checklist 36 complete / 70 open
remain unchanged. See docs/rpg-workstation-parameters.md.

Procedure-local WORKSTN checkpoint (2026-10-18): native free and fixed
procedures now own automatic and STATIC display files. Automatic handles close
on return/error; recursive invocations have independent pending writes. STATIC
handles share state across calls and recursion, survive main-cycle LR, rebind
on retained calls, and release at *NEW/reclamation/job end. Explicit CLOSE stays
closed on later calls. LIKEFILE does not inherit STATIC or USROPN. Local files
import no scalar fields and require compatible result data structures for data
I/O. Local files and record names can shadow global DISK or WORKSTN definitions.
Local/global templates, qualified copies, forward return metadata and borrowed
LIKEFILE parameters retain checked layouts and actual-owner state. INDDS, SFILE
RRNs, named status, INFDS and INFSR remain with the owner, including owner RETURN
across borrowed calls. Local INFDS requires matching scope and storage duration.
Initialization errors release opened automatic handles. Earlier global parents
remain available to copies declared before a later local name shadow; both
DISK and WORKSTN family graphs retain that ancestry. Same-named files in different
procedures remain distinct. Version-5 saved WORKSTN bindings verify scope,
STATIC flags, runtime targets, layouts and families; versions 1–4 remain
supported. Source and separate description removal are supported. Existing
visible-file/storage/metadata bounds remain active. All 5486 solution tests
pass with zero failures/skips in 8m46s, including 48 new cases: 42 local
WORKSTN/metadata/integration cases and six external-process cases. All 48 final
focused checks and 1686 other broad regression cases pass. Locked restore,
a zero-warning/error Release build, expanded RPGWLOCAL terminal compile/call
acceptance, generated inventory, local links and whitespace checks pass.
Process tests cover CCSIDs 37/1208 and valid/malformed atomic result writeback;
file arguments reject external protocols 1/2 before side effects. RPGWLOCAL
runs twice at 24x80 against one STATIC file; general display acceptance covers
27x132. Zero-field extracts, nested/multi-format/null layouts, blocked I/O,
broader external/member/CCSID options, ILE linking and independent native golden/
scale acceptance remain open. C09 stays partial; schema 31, catalog 306 and
checklist 36 complete / 70 open remain unchanged. See docs/rpg-local-workstations.md.

Nested native structure checkpoint (2026-10-18): qualified structures now support
inline DCL-DS, LIKEDS and LIKEREC children, including nested arrays and traditional
fixed referenced subfields. Children share the enclosing byte storage; aliases,
POS/OVERLAY strides, recursive initialization and selected CLEAR preserve sibling
bytes. INZ(*LIKEDS) retains explicit defaults and uninitialized parent bytes;
ordinary INZ supplies recursive defaults. Nested views retain their own widths
inside multiple-occurrence structures. Forward metadata resolves nested scalar
LIKE, templates, layout inquiries and return types without runtime storage.
Reference/CONST/VALUE parameters, EXACT ancestry, detached returns and RTNPARM
retain nested layouts and shared scalar aliases. Nested arrays support %SUBARR,
keyed SORTA/%FIELDS and keyed lookup. DISK and WORKSTN result I/O accept selected
nested LIKEREC children with checked record/extract ancestry. %KDS accepts nested
key structures and selected native array elements, capturing indices once.
Saved programs reconstruct layouts after source removal; existing file binding
versions remain unchanged. Protocol-2 processes receive contiguous nested
buffers with unchanged binary layout and atomic writeback. Bounds are 64 nesting
levels, 1024 recursive subfield definitions and the existing native byte/storage
limits. Invalid/cyclic declarations, unsupported nested storage keywords and
CONST writes reject before execution with source locations. Column-limited
inline header continuations retain their physical positions.
All 5540 solution tests pass with zero failures/skips in 9m01s,
including 54 new cases: 42 structure/metadata/ABI cases, eight real saved-record
cases and four external-process cases. All 1957 broad and 311 final focused
checks, locked restore, a zero-warning/error Release build, expanded RPGNESTED
terminal acceptance, generated inventory, local links and whitespace checks
pass. Record/process cases cover CCSIDs 37/1208. RPGNESTED runs at 24x80; general
display acceptance covers 27x132. Multi-format/null external layouts, zero-field
record extracts, broader external/member/CCSID options, blocked I/O, ILE linking,
complex nested keyed paths and independent native golden/scale acceptance remain
open. C09 stays partial; schema 31, catalog 306 and checklist 36 complete / 70 open
remain unchanged. See docs/rpg-nested-structures.md.

External structure checkpoint (2026-10-18): free and fixed EXT/EXTNAME definitions
now obtain native database/display fields without declaring or opening files.
First/explicit format selection, INPUT/OUTPUT/ALL/KEY extraction, PREFIX replacement,
EXTFLD renames/initializers/DIM, additional subfields and inline END-DS headers
retain native storage. EXTFLD names bypass PREFIX; file prefixes remain independent.
INZ(*EXTDFT) initializes original field storage before DIM interpretation and
preserves inherited defaults through LIKEDS. Current date/time defaults evaluate
at runtime, with zero-argument DATE/TIME/TIMESTAMP using the host clock and native
second/microsecond precision. Qualified/unqualified, template, array, nested,
multiple-occurrence and local STATIC forms retain existing storage rules.
Compatible unqualified imported fields share native scalar/array storage;
implicit DISK/WORKSTN I/O transfers the original complete field with conversion
preflight. Explicit result buffers map renamed/divided fields and require an
explicit compatible extract. Format/byte checks preserve compatible logical
access paths independently of key order. Forward types, EXACT ancestry, parameters,
returns and protocol-2 process buffers use cached descriptions; canonical aliases
share type identity and conflicting snapshots reject.
Version-1 ipc.rpg.externalstructures metadata is source-bound, verifies every
binding, rejects malformed/stale/unused descriptions and enforces 256 requests
and 1 MiB while writing. Activation fingerprints include it; existing DISK and
WORKSTN binding versions remain unchanged. Source and separate descriptions can
be removed before calls; actual runtime file authority/layout checks remain live.
All 5602 solution tests pass with zero failures/skips in 8m51s,
including 63 new accepted cases: 40 structure/metadata/default/ABI cases, 19 real
record/integration cases and four process cases. One obsolete external-E rejection
case is replaced by positive saved-program acceptance; data-area U still rejects.
All 2025 broad and 324 final focused checks, locked restore, a zero-warning/error
Release build, expanded RPGEXTDS terminal acceptance, generated inventory, local
links and whitespace checks pass. Record/process cases cover CCSIDs 37/1208.
RPGEXTDS runs at 24x80; general display acceptance covers 27x132. Null/varying/opaque
layouts, wider external/CCSID settings, multi-format file I/O, blocked I/O, ILE
linking, complex nested keyed paths and independent native golden/scale acceptance
remain open. C09 stays partial; schema 31, catalog 306 and checklist 36 complete /
70 open remain unchanged. See docs/rpg-external-structures.md.

Native null-value checkpoint (2026-10-18): global CTL-OPT/H ALWNULL now accepts
nullable database descriptions. *NO raises file status 1299 before changing
receiver data/flags when input contains NULL; *INPUTONLY supplies live DDS/system
defaults and rejects nullable output/key searches. *USRCTL adds argument-free
NULLIND, scalar/indexed %NULLIND and independent native null state. Nullable
imports, LIKEREC/EXTNAME subfields, LIKEDS, nested arrays, occurrences, globals
and automatic/STATIC procedures retain their storage rules. STATIC flags survive
LR restart in named activations. Ordinary data operations leave flags unchanged;
normal aggregate parameters do not implicitly borrow null maps. Implicit/result
record input updates only its target, preserves data/flags on not-found or staged
conversion failure and invalidates failed update-read cursors. WRITE/UPDATE
validate data even with flags on. Scalar/list/KDS keys use native flags; KFLD
accepts explicit scalar indicators. Singleton EXTFLD arrays route the indexed
indicator through implicit and result-buffer I/O. Borrowed/local files use executing-frame flags.
All nine database field types use valid native placeholders for NULL under
*USRCTL; input-only defaults remain live after file replacement. Source and
separate nullable descriptions can be removed before calls.
Nullable DISK/EXTNAME snapshots use versions 7/2, preserve nonnullable signatures,
reject nullable attributes in older envelopes and retain strict source/layout/
family/bounds checks. WORKSTN metadata versions, schema 31 and catalog 306 stay
unchanged. All 5667 solution tests pass with zero failures/skips in
9m09s, including 65 new cases: 15 native/control/metadata cases and 50
real-database/integration/metadata cases. The obsolete blanket nullable-schema
rejection assertion now has positive schema acceptance and null-row mapping
coverage. All 3409 broad and 292 final focused checks, locked restore, a Release
build with zero warnings/errors, expanded RPGNULL terminal acceptance, generated
inventory, local links and whitespace checks pass. Record acceptance covers
CCSIDs 37/1208. RPGNULL runs at 24x80; general display acceptance covers 27x132.
Named NULLIND associations, OPTIONS(*NULLIND) maps, *NULL extracts, aggregate/
whole-array indicators, divided nullable EXTFLD arrays, EVAL-CORR, varying/opaque
layouts, multi-format I/O, wider external/CCSID settings, blocked I/O, ILE linking
and independent native golden/scale acceptance remain open. C09 stays partial;
checklist 36 complete / 70 open is unchanged. See docs/rpg-null-values.md.

Named null-map checkpoint (2026-10-18): NULLIND(name) now associates native
variables and program-described subfields with live IND storage. Forward names,
scalar/array shapes, scope, automatic/STATIC lifetime, nonnullable targets and
unique ownership are checked before execution. Named flags preserve global and
CONST protections; selected array indices are captured once. LIKEDS and nested
layouts inherit internal sibling relationships, while scalar LIKE and an outer
LIKEDS copy do not inherit an outer named association. Named IND subfields move
and borrow as ordinary aggregate data; automatic maps retain independent formal
state. STATIC named maps survive LR restart in retained activations.
LIKEREC/EXTNAME now accept *NULL with INPUT/OUTPUT/ALL/KEY in either order;
NULL alone selects INPUT. Indicator-only layouts preserve external identities
through PREFIX/EXTFLD and use original database field or selected key/display
order. Null maps reject as I/O buffers, and EXACT distinguishes data/map roles
even when the data consists only of indicators. Named maps supply implicit and
result-buffer null I/O and KEY/KDS searches. Successful input clears nonnullable
map bits, including received WORKSTN fields. Nested arrays and occurrences select
corresponding sibling maps without changing the map's current occurrence.
Native templates, ordinary aggregate parameters and return values retain IND
bytes as data. Saved source/descriptions reconstruct map roles without live
resolution; separate descriptions/source can be removed before calls.
All 5715 solution tests pass with zero failures/skips in 9m16s,
including 48 new cases: 28 native/control/metadata/ABI cases and 20 real record/
integration cases. Four obsolete *NULL-rejection cases now reject malformed
or duplicate extracts, with positive extraction and saved-program coverage.
All 3459 broad and 160 final focused checks, locked restore, a zero-warning/error
Release build, expanded RPGNMAP terminal acceptance, generated inventory, local
links and whitespace checks pass. Record cases cover CCSIDs 37/1208; RPGNMAP
runs at 24x80, and general display acceptance also covers 27x132.
Formal-parameter named associations, OPTIONS(*NULLIND) maps, aggregate/whole-array
%NULLIND, null-map INZ(*EXTDFT), divided nullable EXTFLD arrays and EVAL-CORR remain
open, alongside broader external/CCSID settings, varying/opaque database layouts,
multi-format I/O, blocked I/O, ILE linking and independent native golden/scale
acceptance. Binding versions stay DISK 7, external descriptions 2 and WORKSTN 5;
schema 31, catalog 306 and checklist 36 complete / 70 open remain unchanged.
C09 stays partial. See docs/rpg-null-maps.md.

Null-parameter checkpoint (2026-10-18): OPTIONS(*NULLIND) now borrows live null
maps through native procedures and stored RPG calls. ALWNULL(*USRCTL), exact
variable types/dimensions/related structures and matching structure capabilities
are checked, including for CONST. Scalar/array formals become null-capable;
nonnullable scalar/array actuals receive scoped, initially-off maps. Only *NOPASS
and *OMIT companion options are accepted. Nested/array forwarding, repeated
aliases, named indicators, strided overlay fields, anonymous padding and empty
LEN structures retain data/map identity and bounds. Scalar views are limited by
both declared width and actual stride, preserving VARSIZE element isolation.
Named NULLIND parameters bind one nonnullable indicator or matching external
*NULL-map parameter in the same signature. PR/PI source names may differ, but
association positions and modes must match. Explicit indicator parameters remain
ordinary data and do not implicitly borrow the data actual's automatic map.
Readonly maps are preflighted before record input; successful input/output uses
borrowed attributes. Runtime entry maps validate presence, size and capabilities
before execution; data plus maps share the 1 MiB call bound. ProgramArgument.NullMap
exposes bounded synchronous access to aware hosts, with readonly/capability
checks and call-lifetime invalidation. Cleanup detaches private map snapshots
alongside data; retained activations rebind fresh maps on subsequent calls.
Existing process protocols 1/2 and built-in APIs without map contracts reject
sidebands before execution rather than silently dropping them.
All 5761 solution tests pass with zero failures/skips in 9m18s,
including 46 new cases: 30 native/compiler cases, eight core/host map cases,
six real saved-program/record cases and two external-process rejection cases.
All 3505 broad and 174 final focused checks, locked restore, a Release
build with zero warnings/errors, expanded RPGNPRM terminal acceptance, generated
inventory, local links and whitespace checks pass. Native/record cases cover
CCSIDs 37/1208; RPGNPRM runs at 24x80 and general display acceptance also covers
27x132. Binding versions remain DISK 7, external descriptions 2 and WORKSTN 5;
schema 31, catalog 306 and checklist 36 complete / 70 open are unchanged.
Process null-map sidebands/foreign-language ABI, aggregate/whole-array %NULLIND,
null-map INZ(*EXTDFT), divided nullable EXTFLD arrays, EVAL-CORR, wider external/
CCSID settings, varying/opaque database layouts, multi-format/blocked I/O, ILE
linking and independent native golden/scale acceptance remain open. C09 stays
partial. See docs/rpg-null-parameters.md.

Process null-map checkpoint (2026-10-18): registered process protocol 3 now
carries aligned nullable/value/readonly maps alongside existing scalar/buffer
arguments. Requests preserve OMIT/NOPASS slots, empty structures, declaration
and array order, nested/mixed nullable layouts and supported buffer CCSIDs.
Strict responses preserve map capabilities, mutability, counts and slots;
unknown/duplicate fields, wrong types, missing maps, changed CONST and nonnullable
flags reject. Session writeback validates all data and maps before publication,
records flag changes before data writes, accepts map-only updates and preserves
valid updates on success:false. The service returns staged map values without
mutating callers. Known overlapping null maps and named indicators shared with
data reject before process side effects, including forwarded storage. Scoped
storage identities expire at call return; detached snapshots own private state.
Protocols 1/2 still reject sidebands; ordinary legacy behavior and registration's
protocol 1 default are preserved. Executable/dependency pinning, authority,
private directories, cleanup, cancellation, timeout and stream limits remain
in force; large maps reject before wire snapshots/process startup.
All 5791 solution tests pass with zero failures/skips in 9m05s,
including 30 new cases: 29 process/ABI/integration cases and one scoped identity
case. All 3535 broad and 38 final focused checks, locked restore,
a Release build with zero warnings/errors, expanded RPGNWIRE terminal acceptance,
generated inventory, local links and whitespace checks pass. Process buffers
cover CCSIDs 37/1208; RPGNWIRE runs at 24x80 and general display acceptance covers
27x132. The terminal fixture references the process executable and its updated
lock file passes locked restore. Schema 31, catalog 306 and binding versions
DISK 7/external descriptions 2/WORKSTN 5 remain unchanged. Alias-capable process
protocols, native foreign-language ABI, aggregate/whole-array %NULLIND, null-map
INZ(*EXTDFT), divided nullable EXTFLD arrays, EVAL-CORR, wider external/CCSID
settings, varying/opaque database layouts, multi-format/blocked I/O, ILE linking
and native golden/scale acceptance remain open. C09 stays partial; checklist
36 complete / 70 open is unchanged. See docs/rpg-process-null-maps.md.

Corresponding-assignment checkpoint (2026-10-18): EVAL-CORR now assigns native
structure variables in free source and fixed extended factor 2, with free H
numeric half adjustment. Compatible local names select fields after PREFIX/
EXTFLD naming, independently of external identity and offsets. Nested structures
recurse; scalar/array subfield ranks must agree. Structure arrays and SUBARR
slices use common prefixes or scalar broadcast; unused source tails are not
decoded and extra target data/flags stay unchanged. Source and target indices
are captured once; multiple occurrences retain their independent selection.
Assignments follow source declaration order, including overlays and live named
IND data. Related identical layouts capture whole images, preserving padding and
overlapping array slices without decoding overlaid numeric data. The optimization
checks full layout/name/rank/null-capability identity; different record extracts
or prefixes retain field correspondence. Selected nullable leaves copy source
flags or clear them for nonnullable sources; nonnullable targets ignore flags.
Named and borrowed maps remain live, CONST targets reject at compilation and
readonly maps preflight associated movement. Native conversion/overflow monitor
statuses preserve failing fields while completed earlier fields remain assigned.
Saved nullable record output, source/description deletion and both supported
CCSIDs have acceptance coverage. The IR opcode is appended without renumbering
existing values; schema 31, catalog 306 and DISK 7/external description 2/WORKSTN 5
bindings remain unchanged.
All 5844 solution tests pass with zero failures/skips in 9m27s,
including 53 new cases: 39 native/compiler/ABI cases and 14 saved-program/record
cases. All 3588 broad and 53 final focused checks, locked restore, a Release
build with zero warnings/errors, expanded RPGCORR terminal acceptance, generated
inventory, local links and whitespace checks pass. Native/record coverage includes
CCSIDs 37/1208; RPGCORR runs at 24x80 and general display acceptance also covers
27x132. Fixed EVAL-CORR extenders, M/R precision/procedure expressions, aggregate/
whole-array %NULLIND, null-map INZ(*EXTDFT), divided nullable EXTFLD arrays, wider
external/CCSID settings, varying/opaque database layouts, multi-format/blocked I/O,
ILE linking, alias-capable process protocols, native foreign-language ABI and
independent native golden/scale acceptance remain open. C09 stays partial;
checklist 36 complete / 70 open is unchanged. See docs/rpg-corresponding-assignment.md.

Right-adjustment checkpoint (2026-10-18): EVALR now assigns fixed-length native
character fields, structures and arrays in free source and fixed extended factor
2. It pads or truncates on the left without trimming source blanks. Scalar
expressions broadcast once; elementwise character expressions and explicit (*)
arrays use common prefixes, preserving extra target elements. SUBARR aliases
snapshot source bytes before publication. Nested structures, opaque HEX data and
selected multiple occurrences retain byte layout and bounds. Writable SUBST
aligns within its selected width, preserves neighbors and permits bounded empty
end slices. Its selectors require non-float integer values with zero decimal
positions, capture indices once and check actual VARSIZE capacity before writing.
Shared native index type checks now use static metadata rather than resolving
indexed references again, preventing repeated subscript procedure effects.
Native conversion and host validation stage complete array images before
writeback. CONST targets and known incompatible direct types/ranks reject at
compilation; runtime values require character data and matching buffer CCSIDs.
Native statuses distinguish invalid positions, bounds and unrepresentable text.
Data movement leaves automatic/named/borrowed null flags unchanged. Nullable
implicit and result-buffer output preserves nulls and leading blanks; saved
programs work after source removal and retained calls rebind fresh parameters
and maps. The opcode is appended without renumbering existing IR values; schema
31, catalog 306 and DISK 7/external description 2/WORKSTN 5 bindings are unchanged.
All 5901 solution tests pass with zero failures/skips in 8m54s,
including 57 new cases: 47 native/compiler/host cases and ten saved-program/record
cases. All 3645 broad and 57 final focused checks, locked restore, a Release
build with zero warnings/errors, expanded RPGRIGHT terminal acceptance, generated
inventory, local links and whitespace checks pass. Native/record coverage includes
CCSIDs 37/1208; RPGRIGHT runs at 24x80 and general display acceptance also covers
27x132. EVALR M/R precision, varying/graphic/UCS-2 targets, broader CHARCOUNT/CCSID,
null-map INZ(*EXTDFT), divided nullable EXTFLD arrays, aggregate/whole-array
%NULLIND, EVAL-CORR precision/procedure extensions, multi-format/blocked I/O, ILE
linking, native foreign-language ABI, process aliases and independent native
golden/scale acceptance remain open. C09 stays partial; checklist 36 complete /
70 open is unchanged. See docs/rpg-right-assignment.md.

Static-module checkpoint (2026-10-18): CRTRPGMOD now creates source-bound RPGLE
module objects; CRTPGM binds explicit module lists and ENTMOD, while DSPMOD lists
saved imports/exports and main eligibility. NOMAIN permits global declarations
and exported native procedures without a main PI/calculations. ALWNULL/NOMAIN may
be combined or separate before declarations without repetition. Normal CRTBNDRPG
still requires local resolution. Binding checks distinct modules, eligible entry,
case-sensitive exports, missing/private/duplicate symbols and native return/
parameter modes/layouts before program creation. Native CALLP, prototyped CALLB,
functions and callbacks use the owning module's globals and existing borrowed
argument scopes, including null maps, file parameters and structural ABIs. Bound
lookup is separate from legacy semantic dispatch; unprototyped module CALLB fails
compilation. Captured source maps and file/external descriptions survive source,
module and compile-time-only description deletion. Global data initializes on
first module use; separate bound programs have separate activation resources.
RETURN and entry LR retain other module globals/cursors; reclaim/job end/failure
close owned resources. Live file authority is rechecked and failed activations
are discarded. Bind/inspect enforce module use and signing policy; calls enforce
program use/signing while retaining live runtime-file authority. Module owners
do not introduce separate adopted authority. Current-library defaults and QTEMP
job isolation are covered. Strict version 1 images reject unknown/duplicate/case-
variant fields, noncanonical identities, stale maps and inconsistent descriptions.
Module arrays are bounded before deserialization; limits are 16 modules, 4 MiB
UTF-8 source, 16 MiB metadata, 32 MiB encoded image and 64 MiB declared globals.
Schema 31 and DISK 7/external description 2/WORKSTN 5 bindings are unchanged.
Module metadata uses ipc.rpg.moduleVersion=1 and bound programs use RPGBOUND with
a captured-module envelope. Catalog now has 307 names: existing CRTRPGMOD/CRTPGM
contracts become partial implementations and DSPMOD is added. The interop.md
stub is replaced with current supported contracts and remaining scope.
All 5962 solution tests pass with zero failures/skips in 9m31s,
including 61 new cases: 26 compiler/binder cases, 28 command/metadata/security/
lifecycle cases and seven real file/ABI cases. All 3706 broad and 61 final
focused checks, locked restore, a Release build with zero warnings/errors,
expanded terminal CRTRPGMOD/DSPMOD/CRTPGM/CALL acceptance, generated inventory,
local links and whitespace checks pass. Native/file cases cover CCSIDs 37/1208;
the RPGBIND terminal case runs at 24x80 and general display acceptance covers
27x132. Service programs, binding directories, binder source/signatures, update/
rebind, data exports/imports, wider language/calling conventions and procedure
pointers remain open, alongside full cycle/exception/activation semantics, wider
RPG null/CCSID/database contracts and independent native golden/scale acceptance.
C09 stays partial; checklist 36 complete / 70 open is unchanged. See
docs/rpg-module-binding.md and docs/interop.md.

Service-definition checkpoint (2026-10-18): CRTSRVPGM now creates source-bound
RPGSERVICE objects from captured modules using EXPORT(*ALL) or saved binder
source; DSPSRVPGM validates and displays their exports, signatures, modules and
activation policy. Service module binding needs no main entry and checks internal
imports and native interfaces before publication. Binder source supports one
CURRENT plus append-only PRV levels, quoted case-sensitive symbols, uppercase
unquoted symbols, EBCDIC 37 literal/hex signatures and deterministic emulator
*GEN signatures. Unsupported options, invalid levels, duplicate/missing/private
exports and incompatible interfaces reject. Strict version 1 images rebuild and
compare definitions, bound arrays before deserialization, and retain source maps
and file/external metadata after original module/source/binder deletion. Source,
module and destination authority, service inspection authority, module/service
signing, current-library lookup and QTEMP isolation have acceptance coverage.
ACTGRP records *CALLER or named policy; creation and inspection do not run code.
Catalog now has 308 names: CRTSRVPGM becomes partial and DSPSRVPGM is added.
Schema 31 and DISK 7/external description 2/WORKSTN 5 bindings are unchanged.
All 6056 solution tests pass with zero failures/skips in 9m40s,
including 94 new cases: 47 binder/compiler cases and 47 command/image/security
cases. All 3800 broad and 148 focused checks, locked restore, a Release
build with zero warnings/errors, expanded terminal service creation/inspection,
generated inventory, local links and whitespace checks pass. Dynamic BNDSRVPGM
linking, activation-time signature/ordinal checks, shared service state, service
dependencies, binding directories, update/rebind, data exports/imports, broader
ABI/calling conventions and independent native acceptance remain open. C09 stays
partial; checklist remains 36 complete / 70 open. See docs/rpg-service-definitions.md.

Dynamic-service checkpoint (2026-10-18): CRTPGM and CRTBNDRPG now accept explicit
BNDSRVPGM lists and *NONE. Static procedures resolve first, followed by services
in list order; unused services are excluded. Binding checks native interfaces,
while programs capture service identity/signature/export references without
copying service code. Before main side effects, activation checks live use and
signing, matching CURRENT/PRV signature levels, export positions and native
interfaces. Native CALLP/prototyped CALLB/functions retain existing parameter,
return, file and null-map contracts. Argument effects occur in the caller group
before entering the service scope. Services own independent module/global/STATIC
storage and declared files: *CALLER shares within a caller group, named services
share across caller groups, and jobs remain isolated. Caller LR/failure/reclaim
does not dispose service state. Reentrancy preserves global updates; automatic
frames and borrowed references unwind on errors. Activated code/group policy is
pinned until service reclaim, while object/file authority, signing and trust stay
live. Reclaim refreshes cached bindings; active services cannot be reclaimed.
Service-group reclaim, transient caller-group exit and job end close owned
resources. Weak caller bindings and disposed runtime contexts release reclaimed
module storage. Service owners add no adopted authority. Trusted jobless calls
share services only within the outer call. Deleted sources/modules/descriptions,
nullable structure and LIKEFILE interfaces, file cursors and job-end lock cleanup
have acceptance coverage at CCSIDs 37/1208.
Dynamic programs use a strict version 2 RPGBOUND reference envelope; version 1
static programs and RPGSERVICE service definitions remain supported. Catalog
remains 308 names, with BNDSRVPGM metadata added to CRTPGM/CRTBNDRPG. Schema 31 and
DISK 7/external description 2/WORKSTN 5 are unchanged.
All 6114 solution tests pass with zero failures/skips in 10m00s,
including 58 new cases: 48 binding/activation/security/lifecycle cases and ten
real file/structure/null-map cases. All 3858 broad and 206 focused checks,
locked restore, a zero-warning/error Release build, expanded terminal dynamic
binding/native-null-map/shared-counter/caller-and-service-reclaim acceptance,
generated inventory, local links and whitespace checks pass. Service dependencies,
binding directories, update/rebind, data exports/imports, deferred activation,
broader ABI/calling conventions and independent native acceptance remain open.
C09 stays partial; checklist remains 36 complete / 70 open. See
docs/rpg-service-definitions.md and docs/interop.md.

Binding-directory/dependency checkpoint (2026-10-18): CRTBNDDIR, DLTBNDDIR,
ADDBNDDIRE, RMVBNDDIRE and DSPBNDDIR manage ordered strict object payloads.
Positioned add/replace, generic removal and complete-batch validation are atomic;
concurrent additions read fresh payloads inside write transactions. Native add/
remove data-authority requirements do not introduce read/management requirements.
Library authority, create-authority defaults, authorization-list attachment,
current library, QTEMP isolation and object copy/rename are covered. Directories
hold at most 256 entries and 512 KiB encoded UTF-8, rejecting malformed/unknown/
duplicate/case-variant data. CRTPGM, CRTBNDRPG and CRTSRVPGM accept BNDDIR lists
and *NONE, select providers for unresolved imports in order, revisit earlier
providers for newly introduced imports, ignore duplicate resolved identities and
stop before unused trailing entries. Missing providers produce nonfatal CPD5D1D
diagnostics. Live provider authority/signing and native interface checks precede
publication. Directory-selected modules are captured; services remain references.
Legacy semantic procedure nodes cannot hide native imports or bound targets.
CRTSRVPGM also accepts BNDSRVPGM dependencies. Version 2 RPGSERVICE images retain
only used qualified service references while version 1 remains supported. Graph
preflight checks every dependency edge's authority, signing, signature, export
positions and native interface before main side effects, in the parent service's
activation group. Cycles terminate without initializing data. Limits are 64
dependency levels and 256 activations; child reclaim refreshes weak parent bindings.
Directory/source/module/description deletion, transitive nullable structure
aliases and leaf-owned cursor state have acceptance coverage at CCSIDs 37/1208.
Examined directory artifacts are bounded to 64 MiB UTF-8, with existing selected
module/service/source/image/storage limits retained. Catalog now has 313 names;
BNDDIR metadata is added to the three RPG creation commands and BNDSRVPGM to
CRTSRVPGM. Schema 31 and DISK 7/external description 2/WORKSTN 5 are unchanged.
All 6199 solution tests pass with zero failures/skips in 9m58s,
including 85 new cases: 40 directory lifecycle/authority/image/concurrency cases,
19 discovery cases, 22 service dependency cases and four real record/ABI cases.
All 3943 broad and 291 focused checks, locked restore, a zero-warning/error
Release build, expanded terminal directory CRUD/static discovery/service dependency/
shared-leaf/reclaim/source-independence acceptance, generated inventory, local
links and whitespace checks pass. Module-embedded directory references/control
specifications, deferred activation, update/rebind, data imports/exports, richer
work/output operations, wider binder/ABI conventions and independent native
acceptance remain open. C09 stays partial; checklist remains 36 complete / 70 open.
See docs/rpg-binding-directories.md and docs/rpg-service-definitions.md.

Source-reference/update checkpoint (2026-10-18): BNDDIR control literals are now
captured from free/multiline source, includes and supported traditional H origins.
CRTRPGMOD defers directory lookup to binding and DSPMOD lists saved references.
CRTBNDRPG detects controls after preprocessing and automatically uses module
binding. Explicit command directories precede module references; selected modules
can introduce further reference directories, which are examined only while imports
remain unresolved. Captured programs do not reopen directories at runtime.
UPDPGM and UPDSRVPGM replace existing bound modules by name using RPLLIB(*ONLY,
*FIRST, *MODULE or an origin library), preserving recorded origins and entry
identity. Explicit/generic/*ALL module selection is bounded; MODULE(*NONE) rebinds
current service signatures without original source/module objects. Previously
bound services precede new providers and directory discovery. Ordinary saved
CRTBNDRPG programs can become single-module RPGBOUND artifacts during update.
UPDSRVPGM EXPORT(*CURRENT) preserves public levels/signatures, including synthetic
exact-signature binder source when needed; *ALL and source export modes can
publish new interfaces with prior-level compatibility. Missing exports and
incompatible interfaces reject the entire update.
Updates require owner/effective owner-group/nonadopted *ALLOBJ authority, library
use/add and live target/provider integrity. ALWUPD(*NO) rejects updates. Complete
artifact restoration precedes transactional publication, comparing current
canonical contents, timestamp and signing data. Concurrent changes fail with
IPC0130; cancellation/errors preserve the original object. Success retains owner,
creation metadata, authority and activation policy while clearing stale signing.
Existing program/service activations keep old code, entry interfaces, data and
cursors until reclaim; selection occurs before entry argument borrowing. New
activations use the updated version. Live authority and trust checks still apply.
ACTGRP/SRVPGMLIB *SAME remain the supported update policies. Object image versions,
schema 31 and DISK 7/external description 2/WORKSTN 5 are unchanged. Catalog now
has 315 names, adding UPDPGM/UPDSRVPGM and ALWUPD creation metadata.
All 6253 solution tests pass with zero failures/skips in 10m26s,
including 54 new cases: 19 source-control cases, 33 update/binding/security/
publication cases and two real retained-cursor cases at CCSIDs 37/1208. All
3997 broad and 345 focused checks, locked restore, a zero-warning/error
Release build, expanded terminal source-BNDDIR/module-update/retained-code/service-
update/reclaim/MODULE(*NONE) acceptance, generated inventory, local links and
whitespace checks pass. Named activation changes, service-library relocation,
per-provider deferred activation, ALWLIBUPD, wider update/listing/event options,
data exports/imports, broader language/ABI and independent native golden/scale
acceptance remain open. C09 stays partial; checklist remains 36 complete / 70 open.
See docs/rpg-program-updates.md.

Deferred service activation checkpoint (2026-10-18): BNDSRVPGM now accepts native
(service *IMMED/*DEFER) tuples in CRTPGM, CRTBNDRPG and CRTSRVPGM, including the
single flat tuple form. Directory service entries retain their activation mode
when selected. Creation still validates available providers and native interfaces;
runtime preflight follows immediate edges only. Unused deferred providers are not
located or checked for authority, signing, signatures or interfaces until called.
Earlier caller effects survive a later activation failure. First use checks the
provider's immediate dependency closure before its procedure body; deferred child
edges wait for their own calls. Parent-group ownership, shared service state,
cyclic/reentrant graphs and existing preflight/invocation budgets remain enforced.
First use selects the then-current service version; active services retain code,
globals and cursors until owning-group reclaim. Live authority/signing and retained
snapshot trust remain mandatory. UPDPGM/UPDSRVPGM preserve modes by default and
accept per-provider *SAME/*IMMED/*DEFER; *SAME for a new provider uses *IMMED.
Existing caller activations retain their prior binding policy after an update.
RPGBOUND/RPGSERVICE version 3 captures deferred references. Immediate-only images
retain versions 1/2 and omit the activation property. Strict image checks reject
invalid modes and inconsistent versions; older snapshots remain readable. Binding
directory image version 1, schema 31, DISK 7/external description 2/WORKSTN 5 and
catalog 315 are unchanged.
All 6297 solution tests pass with zero failures/skips in 10m21s,
including 44 new cases: 40 activation/binding/update/security/version cases and
four real nullable-record/cursor cases covering CCSIDs 37/1208. All 4041
broad and 210 focused checks, locked restore, a zero-warning/error Release build,
expanded terminal unused-missing-provider/first-call-failure/recovery/*SAME
acceptance, generated inventory, local links and whitespace checks pass.
Named activation-group updates, service-library relocation/ALWLIBUPD, data imports/
exports, procedure pointers, wider update/listing/ABI controls and independent
native golden/scale acceptance remain open. C09 stays partial; checklist remains
36 complete / 70 open. See docs/rpg-deferred-activation.md.

Activation/library update policy checkpoint (2026-10-18): UPDPGM and UPDSRVPGM
support ACTGRP(*SAME) and changes between named groups. *CALLER/*NEW policies
cannot become named policies or replace named policies. Group changes publish
atomically with the validated artifact; cancellation, concurrent modification,
denied policy and invalid providers preserve the entire original object.
Program calls select the requested group while old groups retain captured code,
interfaces, globals and cursors. Existing service connections retain their old
service group/version; new connections use the updated policy, and owning-group
reclaim refreshes old connections.
CRTPGM, CRTBNDRPG and CRTSRVPGM capture ALWLIBUPD(*YES/*NO), defaulting to *NO for
new and older objects. Non-*SAME SRVPGMLIB updates require *YES. Named policies
relocate each found existing provider and preserve original references when a
preferred object is missing. Authority, signing, image or interface failures never
trigger fallback. *LIBL policies record runtime lookup with the resolved binding
origin. *SAME preserves lookup policy while rebinding current signatures; named
relocation can restore fixed lookup. Existing activation modes remain preserved.
Dynamic references resolve per caller image at activation/first deferred use.
Connected references pin their actual provider despite library-list changes;
other callers/jobs can resolve a different provider. Connections to the same
qualified object/group share service storage. Live authority and signing checks
apply to the actual connected provider and retained snapshot trust. Reclaim or
fresh caller activation permits lookup again; runtime lookup does not fall back
to a binding origin outside the current list.
RPGBOUND/RPGSERVICE version 4 captures runtime lookup metadata; explicit references
retain versions 1/2/3. Strict validation and DSPSRVPGM support lookup metadata.
Schema 31, DISK 7/external description 2/WORKSTN 5 and catalog 315 are unchanged;
creation metadata now includes ALWLIBUPD.
All 6347 solution tests pass with zero failures/skips in 10m32s,
including 50 new cases: 46 policy/binding/security/publication/image cases and
four real cursor cases at CCSIDs 37/1208. All 4091 broad and 260 focused
checks, locked restore, a zero-warning/error Release build, expanded terminal
named group/explicit relocation/runtime lookup/connection pinning/reclaim
acceptance, generated inventory, local links and whitespace checks pass.
Implicit system service providers, data imports/exports, procedure pointers,
wider update/listing/ABI controls and independent native golden/scale acceptance
remain open. C09 stays partial; checklist remains 36 complete / 70 open. See
docs/rpg-update-policies.md.

Module/service data binding checkpoint (2026-10-19): global IMPORT/EXPORT
standalone fields and native structures now resolve across static modules,
BNDSRVPGM services and binding directories. Literal/constant external names and
free *DCLCASE retain case; supported traditional D origins share the same model.
Missing/duplicate symbols, procedure/data collisions, incompatible representations,
widths, scales, dimensions, nested offsets, occurrences and null capabilities
reject before publication. Procedure-local/template/entry bindings, import INZ
(including nested initialization) and compile-time imported data reject explicitly.
Imports alias exporter-owned bytes and null maps, including arrays, nested
structure arrays and independently selected multiple occurrences. Typed CL calls
can borrow the aliases. Exported module data initializes before main effects and
retains values/null flags through LR and abnormal returns while ordinary cycle
storage restarts. Services share data with their procedures within the owning
group; caller reclaim does not discard separately owned data. Service reclaim
refreshes surviving imports, and disposed borrowed memory/null maps reject access.
Data imports force service storage activation and integrity/interface checks before
main even with *DEFER. Procedure-only deferred imports remain deferred when a
provider also exports data. Dependency checks preserve parent-group ownership,
cycle/graph limits, live authority/signing and original CCSIDs.
CRTSRVPGM public tables and binder source support data-only and mixed exports.
CURRENT/PRV signatures preserve typed positions; EXPORT(*CURRENT) updates cannot
change a data symbol into a procedure or vice versa. Ordinary service replacement
and fixed/*LIBL relocation retain old activations and typed metadata until reclaim.
RPGBOUND/RPGSERVICE version 5 records ordered data-export subsets; strict checks
reject forged, missing, duplicate or oversized kind metadata. Interfaces without
data retain previous shapes. Static links rebuild from captured module source;
module version 1, schema 31, DISK 7/external description 2/WORKSTN 5 and catalog
315 are unchanged. DSPSRVPGM lists public data symbols.
All 6422 solution tests pass with zero failures/skips in 10m57s,
including 75 new cases: 36 compiler/static/lifetime cases, 37 service binding/
interface/security/lifetime cases and two real nullable-record round trips at
CCSIDs 37/1208. All 4166 broad and 502 focused checks, locked restore,
a zero-warning/error Release build, expanded terminal mixed data/procedure
sharing/LR/caller reclaim/service reclaim acceptance, generated inventory,
local links and whitespace checks pass.
Procedure/data pointers, weak/activation-group exports, wider native binder/
language/ABI controls and independent native golden/scale acceptance remain open.
C09 stays partial; checklist remains 36 complete / 70 open. See
docs/rpg-data-binding.md.

Native procedure pointers checkpoint (2026-10-19): POINTER(*PROC), traditional
*/PROCPTR definitions and PR/PI types, %PADDR and pointer-based EXTPROC now carry
opaque 16-byte addresses through native scalar/array/structure references,
CONST/VALUE arguments and scalar/array/LIKEDS/RTNPARM returns. Pointer fields and
nested containers align to sixteen bytes; invalid explicit positions/lengths
reject. Arrays, indexed structure members, LIKE and LIKEDS retain typed layouts.
%PADDR accepts bound procedure prototypes and case-sensitive literal/constant
entry symbols, including hex constants. Pointer-based prototype addresses can be
read at runtime but cannot initialize definitions. EXTPGM/variable-name operands,
whole pointer-array call targets, character/numeric assignments and ordinary
scalar conversions reject. Pointer targets and interfaces validate before
argument effects, including null/expired/foreign/forged values and incompatible
origin CCSIDs. Target selection remains stable while arguments change a pointer.
Address references participate in captured module/service/directory links;
procedure-pointer imports force activation before effects even with *DEFER.
Private service procedures can return callbacks without exporting their entries.
Job-scoped registries retain origin code/storage and deduplicate at most 4096
live targets. Callbacks re-enter their original activation group, including
*CALLER service owners, so nested program calls use that group. Live authority,
object signing, retained snapshot trust and original CCSIDs remain checked;
procedure calls add no program-owner adopted authority. LR and abnormal returns
restart ordinary cycle storage while retained callbacks use the current root.
UPDSRVPGM preserves old callback code until reclaim. Reclaim/clear/job termination
invalidates targets without rebinding old addresses; newly acquired addresses
connect to the current activation. Legacy shorthand program-name PROCPTR fields
retain their previous behavior. Module version 1, RPGBOUND/RPGSERVICE 1–5,
schema 31, DISK 7/external description 2/WORKSTN 5 and catalog 315 are unchanged.
All 6487 solution tests pass with zero failures/skips in 11m06s,
including 65 new cases: 51 language/compiler/native-storage cases and 14 saved
program/service activation, authority, signing, update, LR/error and reclaim cases.
All 4231 broad and 314 focused checks, locked restore, a
zero-warning/error Release build, expanded terminal callback-factory/retention/
service-reclaim/expired-target acceptance, generated inventory, local links and
whitespace checks pass. Native cases cover CCSIDs 37/1208; general display
acceptance covers 24x80 and 27x132. Data/basing pointers, weak/activation-group
exports, broader native binder/language/ABI controls and independent native
golden/scale acceptance remain open. C09 stays partial; checklist remains
36 complete / 70 open. See docs/rpg-procedure-pointers.md.

Basing pointers/native heap checkpoint (2026-10-19): native POINTER and traditional
* declarations now carry typed 16-byte data addresses, distinct from procedure
pointers even when both variable values are null. BASED fields, arrays, structures,
qualified/indexed basing variables and implicit local pointer names share native
byte storage. LIKE/LIKEDS and pointer-bearing layouts preserve alignment; based
pointer accesses check actual sixteen-byte alignment. Unsupported initialization,
separate ownership, circular/function basing expressions and independent nested
null metadata reject. %ADDR preserves field/array/member/occurrence identity and
supplies array/wildcard/noncontiguous subfield addresses. Imported external fields
become addressable in their owning module, including subprocedure address-taking;
real database updates through packed/text aliases pass at CCSIDs 37/1208.
Forward address initialization waits for storage, parameter initialization waits
for actual binding, and initialization indexes must be compile-time constants.
%SIZE/%LEN/%ELEM inspect null-based layouts without dereferencing or allocating.
CONST parameter addresses support comparisons while rejecting storage/return for
mutation; CONST pointer values can modify/free pointee storage without changing
the pointer cell. Null/omitted/unpassed, expired, foreign, incompatible-CCSID and
out-of-range access reject before mutation. Integer byte arithmetic and same-region
subtraction work; incompatible assignments/conversions and overflow reject.
%ALLOC/%REALLOC and fixed ALLOC/REALLOC share heap preparation; free/fixed DEALLOC
supports E/N and traditional ER indicators. Failed allocation/reallocation keeps
old pointers/bytes; successful reallocation/free expires all old aliases, including
references captured before later argument effects. Native statuses 222/425/426
participate in MONITOR, E handling and feedback. Per-allocation single-level
maximum 16,776,704 bytes, 64 MiB execution heap and 4096 live regions are enforced;
free/reclaim releases capacity and element addresses share regions.
Job-scoped program/service aliases preserve live origin authority/signing,
retained snapshot trust and original CCSIDs, including deferred bindings with no
subsequent procedure call. Private service factories expose data addresses;
UPDSRVPGM retains old storage until reclaim. LR/errors reinitialize globals in
place while heap/STATIC allocations remain owned by the activation. Parameter
address-taking marks the original owner for retention. Automatic/VALUE storage
expires on return; valid caller references survive the callee. Reclaim/clear/job
end invalidates addresses without reconnecting replacements. CL borrowing and
writeback through based native storage pass at CCSIDs 37/1208.
All 6567 solution tests pass with zero failures/skips in 11m11s,
including 81 new cases: 45 language/storage/quota cases, 18 procedure/reference/
lifetime cases, 16 saved activation/service/security cases and two real record
round trips. The obsolete POINTER rejection case was removed because that native
declaration is now supported; the traditional rejection fixture now uses an
invalid fifteen-byte length. All 4311 broad and 394 focused checks,
locked restore, a zero-warning/error Release build, expanded terminal service
address/alias/reclaim/heap realloc/deallocation acceptance, generated inventory,
local links and whitespace checks pass. General display acceptance covers 24x80
and 27x132. Module version 1, RPGBOUND/RPGSERVICE 1–5, schema 31, DISK 7/external
2/WORKSTN 5 and catalog 315 are unchanged. Cross-region subtraction, teraspace,
varying/graphic/UCS-2 addresses, based null-map aliases, wider pointer/API/foreign
ABIs, weak/activation-group exports and independent native golden/scale acceptance
remain open. C09 stays partial; checklist remains 36 complete / 70 open.
See docs/rpg-data-pointers.md.

Durable record slots/deleted reuse checkpoint (2026-10-19)

Schema 32 gives every member an immutable identity, durable allocated extent and
transactional record incarnation tokens. Ordinary physical/logical addition,
declared RPG WRITE and CPYF append allocate from that extent. Deleted trailing
positions are retained; REUSEDLT(*NO) appends and *YES deterministically fills the
lowest available deleted position. CRTPF/CHGPF accept persisted REUSEDLT options,
including CHGPF *SAME and atomic rejection of invalid combined changes. This local
reuse choice does not claim IBM reclaim-algorithm/FIFO/LIFO parity.

Current-record UPDATE/DELETE checks the selected incarnation as well as schema
and typed before image. Delete/reinsert of identical values cannot revive an old
cursor target. Rollback restores versions/extents. CLRPFM and CPYF replacement
reset allocation atomically. Object copy preserves deleted extents with fresh
member identities; move/rename preserve identities and managed triggers. Live
record-unit publication and journal recovery keep bookkeeping triggers; private workspaces and
object copies exclude the original managed triggers appropriately.

Migration seeds active sparse physical/source members, installs capture without
changing record formats and rolls back schema/triggers/history on failure.
Pre-32 deleted trailing extents were never recorded and cannot be reconstructed.
The private commitment workspace and arbitrary raw SQL retain SQLite allocation;
live publication updates the ledger but does not provide pending RRN reservation.

All 6586 solution tests pass with zero failures/skips in 10m55s,
including 19 new cases: twelve slot/identity/copy/rollback cases, two persisted
command-policy cases, two compiled RPG WRITE round trips, two migration cases and
one journal recovery/cursor identity case.
All 498 focused checks and 4313 broad RPG/binding/work cases in the
full suite pass, together with locked restore,
a zero-warning/error Release build, display PTY acceptance, generated inventory,
local links and whitespace checks pass. Catalog 315, DISK 7/external 2/WORKSTN 5,
module 1 and RPGBOUND/RPGSERVICE 1–5 are unchanged. RECNO field writeback,
slot-directed WRITE, INZPFM, global pending RRN/key reservation, native commitment
flags/isolation, broader SQL allocation and multi-member relative mapping,
scale budgets and independent IBM acceptance remain open. C08/C09/C11 stay
partial; checklist remains 36 complete / 70 open. See docs/record-slots.md.

Member initialization/SIZE/authority checkpoint (2026-10-19)

INZPFM now initializes physical data/source members with DDS default records or
allocated deleted positions. Explicit totals preserve existing records/gaps;
FIRST/LAST use creation order, positional operands and NXTINCR/default behavior
are supported. Deleted reservation reaches native RRN 4294967288 with constant
metadata. Default materialization is bounded to one million rows and 64 MiB.

CRTPF/CRTSRCPF and CHGPF persist SIZE lists/NOMAX, with default 10000/1000/3,
individual SAME updates, atomic combined validation and existing-extent guards.
Ordinary physical/logical/RPG additions and CPYF enforce capacity; deleted reuse
can remain within a full extent. Failed copy restores the old records and extent.
All-SAME changes preserve legacy definition bytes. Omitted size now enforces native
default capacity on ordinary live additions; existing large data files can use
CHGPF SIZE(*NOMAX). Private workspaces/raw SQL retain their existing allocation.

Shared authorization now supports common required bits plus a genuine alternative
bit set, preserving private/group/adopted/credential checks and selecting an
actually granted bit for allocation. INZPFM checks OBJOPR/ADD plus OBJMGT or
OBJALTER and DLT for deleted initialization. CHGPF/CHGLF accept either management
bit without OBJOPR; ADDPFM/CLRPFM accept the same alternatives, and member addition
requires library ADD. CLRPFM gains cancellable writer waits and live authority/job
rechecks. Initialization checks cancellation/shutdown, live permissions/job state
and commitment enlistment after a writer wait, and uses one file allocation.

Application insert triggers reject initialization; quoted names/comments/body
text do not confuse event classification. Managed journal/slot triggers remain
recognized. Unsupported logical/custom WITHOUT ROWID members fail before reserve.
One atomic F/IZ entry captures initialization extent, type and default image with
caller/job and receiver accounting; ordinary PT capture is suppressed during
initialization and resumes afterward. Native binary IZ ABI and initialization
apply/remove remain open; recovery rejects those entries explicitly.

All 6647 solution tests pass with zero failures/skips in 11m17s,
including 61 new cases (44 initialization/size/default/rollback/cancellation/CL
and 17 authority cases). All 598 focused checks and 4313 broad
RPG/binding/work cases in the full suite pass, together with locked restore, a
zero-warning/error Release build, expanded CL INZPFM/default/deleted/SIZE/CLRPFM
and RPG slot-reuse terminal acceptance, generated inventory, links and whitespace.
Display acceptance covers 24x80/27x132. Schema 32, module 1, RPGBOUND/RPGSERVICE
1–5 and DISK 7/external 2/WORKSTN 5 are unchanged; catalog increases to 316.
Native operator inquiry/size extension, file/member lock granularity, INZPFM
recovery/ABI, complete native statuses, commitment/SQL allocation, RECNO writeback,
slot-directed WRITE, multi-member mapping and independent native golden/scale
acceptance remain open. C03/C07/C08/C09/C11 retain their existing acceptance
boundaries; checklist remains 36 complete / 70 open. See docs/member-initialization.md.

Initialization journal recovery checkpoint (2026-10-19)

New F/IZ capture now carries IPCINIT2 count/range/type/default data and the existing
version-1 physical-column/file-definition schema snapshot. Oversized schema capture
rolls back initialization. Historical IPCINIT1 entries without their old schema
remain explicitly unsupported rather than inferring layout from current defaults.

APYJRNCHG/RMVJRNCHG now recover these captured initialization entries in the shared
stream with ordinary record entries. Default apply restores only the recorded
positions from the captured image; removal checks presence and atomic-mode images
before deleting records. Prefix records, deleted gaps and later records/extent are
preserved. Deleted initialization reserves metadata without creating rows; removal
leaves positions allocated and refuses occupied positions. RRN 4294967288 works
without enumerating holes. Generated values are recomputed and checked after each
insert. Counts/header/type/image/schema/format/column/JSON-field checks reject
malformed input before effects can commit. Default replay retains the one-million
row/64 MiB bounds, and initialization payload reads are bounded before loading.

Initialization uses existing receiver authorization/ranges, members, file allocation,
commitment boundaries, entry savepoints, native End/Continue handling, application/
journal-trigger suspension, slot incarnation capture, FK checking and recovery
markers/accounting. Each initialization entry counts as one recovery change.
Late constraints or marker failures restore all of the entry's rows/versions/extent.
Recovery now links factory shutdown and rechecks the executing job after writer
acquisition. Restart, explicit members, DDS defaults and CCSIDs 37/1208 are covered.

All 6688 solution tests pass with zero failures/skips in 11m15s,
including 41 new recovery cases; the two initialization-capture cases are updated
for IPCINIT2/schema capture and ordinary journal resumption. All 403 focused
journal/initialization/migration/record-unit checks and 4313 broad RPG/binding/
work cases in the full suite pass. Locked restore, a zero-warning/error Release
build, expanded compiled CL remove/apply and RPG all-default-record verification
in real terminal acceptance at 24x80/27x132, generated inventory, links and
whitespace checks pass. Schema 32, catalog 316, module 1, RPGBOUND/RPGSERVICE 1–5
and DISK 7/external 2/WORKSTN 5 are unchanged.

Native external IZ binary import/export, independent native replay/extent acceptance,
complete record-codec validation, operator SIZE extension and global commitment/
isolation allocation, wider object recovery, RECNO/slot-directed WRITE, member
mapping and scale budgets remain open. C08/C09/C11 retain partial acceptance;
checklist remains 36 complete / 70 open. See docs/initialization-recovery.md.

RPG RECNO/relative output checkpoint (2026-10-19)

Free DISK declarations and fixed F-specs now accept RECNO numeric zero-decimal
scalar associations, including qualified native fields. Input OPEN writes zero;
CHAIN, READ/READP/READE/READPE and successful SETLL/SETGT positioning return the
physical RRN from the actual access path. EOF/misses preserve feedback. Checked
conversion precedes input record import; overflow retains old fields and reports
numeric status 103 through MONITOR. Output-only OPEN preserves the requested slot.
CLOSE/reopen, retained calls/reclaim, result structures, global shadowing and
local automatic/STATIC files use the correct owning storage.

Non-keyed RECNO WRITE fills only an allocated deleted position from 1 through
4294967288, preserving extent, cursor position and existing update ownership.
Active/unallocated/invalid positions fail without record mutation. LIKEFILE can
specify its own association but does not inherit RECNO; file parameters resolve
the actual owner's association for input and output. Version-8 saved DISK metadata
persists associations, validates targets/source requests and rejects downgraded
images; historical versions retain their compatibility. RECNO implies unblocked
processing. Native keyed input remains keyed, and ordinary keyed WRITE keeps its
existing allocation policy.

Relative output shares PF/LF selection, physical defaults, uniqueness, row locks,
job/authority/schema/lifecycle checks, cancellation/rebinding/shutdown, live checks
after writer acquisition, trigger rollback and ordinary job-attributed journal
capture. Commitment workspaces are explicitly rejected. Sparse maximum-RRN output
does not enumerate allocated holes. Real terminal acceptance compiles RECNO WRITE
and verifies OPEN/SETLL/READ feedback at 24x80/27x132.

All 6732 solution tests pass with zero failures/skips in 10m34s,
including 44 new cases and an updated obsolete RECNO-rejection case; all 65 focused cases and 4348 broad
RPG/binding/work cases in the full suite pass. Locked restore, zero-warning/error
Release build, terminal acceptance, frozen-source checks, generated inventory,
local links and whitespace checks pass. The final full run uses eight xUnit
threads. The initial default-parallel run exposed one obsolete rejection assertion
and three coincident timing failures; focused reruns cover all four. Native
timeouts and implementation behavior were not relaxed. Schema 32, catalog 316, external layout 2,
WORKSTN 5, module 1 and RPGBOUND/RPGSERVICE 1–5 are unchanged; associated DISK
metadata advances to 8, and ordinary files retain the prior version selection.

Eligibility errors currently use local IPC0138/RPG 1299. Complete native status
mapping, indexed RECNO targets, program-described/multi-format and multi-member
mapping, native COMMIT/open/isolation allocation, operator SIZE extension,
independent native golden acceptance and scale budgets remain open. C08/C09/C11
stay partial; checklist remains 36 complete / 70 open. See docs/rpg-recno.md.

Job inquiry policy/system reply list checkpoint (2026-10-19)

CHGJOB now changes durable INQMSGRPY(*RQD/*DFT/*SYSRPYL), with SAME preservation,
normal job authority and active-job checks. CRTJOBD/CHGJOBD persist policy;
submission snapshots it and later description changes do not alter an existing
job. Interactive jobs default to RQD and group jobs inherit current policy.
WRKJOB reports it, and job-description copy retains it.

ADDRPYLE/CHGRPYLE/RMVRPYLE/WRKRPYLE supply ordered durable entries with native
exact/generic/ANY matching, original replacement-data comparisons at offsets
1–999, 28 encoded-byte comparison bounds, supported CCSID conversion and HEX.
Explicit, DFT and RQD replies use first-match precedence. Invalid or unrepresentable
automatic replies leave manual inquiries pending; immediate messages do not
match entries. Defaults preserve return types 23/24; list replies carry 25.
The send transaction includes inquiry, sender copy and automatic reply, so
receiver-preparation failures roll back all of them. Existing queue authority,
quotas, snapshots, reply delivery and job checks remain shared. Changes require
QPGMR or non-adopted ALLOBJ, and read/modify/write is atomic.

Schema 33 adds job/job-description policies and the reply list; existing rows
get RQD defaults. Failure-injected upgrades roll back schema/history and preserve
old jobs. Restart preserves policy, comparison bytes, offsets and replies.
All 6771 solution tests pass with zero failures/skips in 10m54s,
including 39 new cases and an updated native-ID migration retry check. All 222 focused checks and 4348 broad
RPG/binding/work cases in the full suite pass. Locked restore, zero-warning/error
Release build, compiled CL reply-list comparison and return-type-25 terminal
acceptance at 24x80/27x132, frozen-source checks, generated inventory, links and
whitespace pass. Final full acceptance uses eight xUnit threads. The native-ID retry test now
checks its intended schema-28 upgrade independently of later added columns. Catalog increases
from 316 to 320; RPG/record metadata versions are unchanged.

This is the shared inquiry prerequisite for operator SIZE extension, whose
CPA5305 emission, durable capacity grants and database retry remain open.
DUMP(YES) is explicitly rejected pending native job-dump/spool delivery. Complete
queue break/delivery modes, external batch queue defaults, inquiry exits, native
job-information attribute formats and independent IBM acceptance remain open.
C07/C08/C11 stay partial; checklist remains 36 complete / 70 open.
See docs/inquiry-replies.md.

Operator member-capacity recovery checkpoint (2026-10-19)

Ordinary physical/logical additions, declared RPG WRITE and CPYF now issue
CPA5305 to QSYSOPR when configured member capacity is exhausted. Shared job
inquiry policy handles defaults, required replies and the system reply list.
C/default 0 cancels, I adds one increment and 1–9999 adds that many. Invalid
replies remain pending for correction. Trusted jobless calls retain CPF5030.

Schema 34 persists allowance by immutable member ID and SIZE signature, without
advancing allocated RRNs. Copy/move retains it, deletion cascades it, CLRPFM
resets it, actual SIZE changes replace it and SAME/unrelated attributes preserve
it. Existing allowances also support INZPFM explicit/next-increment initialization.
Zero increments and physical-RRN overflow fail explicitly.

Transactions unwind before inquiry wait. Grants commit separately before record
retry and recheck executing job, authority, member/format/configuration and
enlistment after writer acquisition. Later trigger/constraint failure preserves
accepted capacity but rolls back records and journal effects. Private queues and
MSGW state are cleaned on return/cancellation; registered waits close on job end
or interrupted-job recovery, including absent workers. Shutdown cancels waits.
A bounded QCPFMSG description is seeded without replacing existing descriptions;
the generic CPF9898 CL adapter remains compatible.

CPYF keeps its bounded TEMP snapshot across inquiry/retry, checks source/target
identities and format again, publishes atomically and releases staging on pooled
disk connections. Concurrent source-row changes cannot alter the captured copy.
No SQLite writer is held while awaiting an operator. Ordinary journal capture
records the successful requested RRN once with the executing job.

All 6806 solution tests pass with zero failures/skips in 11m04s,
including 35 new cases and an updated startup seed/count assertion. All 251 focused checks and 4350 broad
RPG/binding/work cases in the full suite pass. Locked restore, zero-warning/error
Release build, compiled RPG automatic CPA5305 grant/retry terminal acceptance at
24x80/27x132, frozen-source verification, generated inventory, links and whitespace
checks pass. Full acceptance uses eight xUnit threads. Catalog 320 and existing
RPG/record/message metadata versions remain unchanged.

INZPFM's own beyond-limit inquiry, native ALLOCATE/storage accounting, raw SQL/
commitment/isolation allocation, complete file-status mapping and independent
native catalog/layout/golden/scale acceptance remain open. The seeded message
uses a bounded local typed representation. C08/C09/C11 stay partial; checklist
remains 36 complete / 70 open. See docs/operator-capacity.md.

INZPFM operator-capacity recovery checkpoint (2026-10-19)

INZPFM now sends CPA3138 to QSYSOPR when an explicit or next-increment total
exceeds configured/approved member capacity. Shared inquiry policy handles
required/default/reply-list replies. I approves one increment; C/default C
cancels with CPF3140. Numeric multi-increment replies reject without consuming
the request. Successive I approvals can reach a larger requested total. Zero
increments fail with CPF3143, and impossible default-materialization quotas
reject before any inquiry or allowance. Trusted jobless calls retain CPF5030.

The initial member identity/format and desired total are fixed across waits:
adding a newer member cannot retarget LAST, and another writer reaching the
NXTINCR target cannot advance it again. Transactions/file allocations unwind
before waiting. Management/alter, object-operation/add and deleted-initialization
delete authority are rechecked before grant and after writer acquisition, along
with live job, member/format and commitment eligibility. Cancellation cleans
private queues, registered waits and active-job MSGW state.

Approved capacity commits separately. Default records/deleted positions and one
F/IZ capture publish atomically after all required grants. Failed journal capture
rolls back records/extent while retaining the accepted allowance; apply/remove
of the successful initialization preserves allowance and original prefix records.
Startup adds missing CPA3138 without replacing existing customized system
messages. Schema 34, catalog 320 and RPG/record/message metadata versions remain
unchanged.

All 6832 solution tests pass with zero failures/skips in 11m20s,
including 26 new cases. All 238 focused checks and 4350 broad RPG/binding/work
cases pass. Locked restore, zero-warning/error Release build, compiled CL
CPA3138 grant/default-record read and monitored CPF3140 cancellation at 24x80
and 27x132, frozen-source verification, generated inventory, local links and
whitespace checks pass. Full acceptance uses eight xUnit threads.

The exact shipped CPA3138 text/default/severity and replacement ABI remain
unverified; the seed uses a bounded local description and 42-byte representation.
Native ALLOCATE/storage-byte accounting, raw SQL/private commitment allocation,
complete native statuses, native initialization/journal ABI, independent IBM
golden acceptance and scale budgets remain open. C08/C09/C11 remain partial;
checklist stays 36 complete / 70 open. See docs/initialization-capacity.md.

SQL table/RUNSQL checkpoint (2026-10-19)

RUNSQL now implements CREATE TABLE, DROP TABLE and literal/default multirow
INSERT VALUES through shared physical files. SQL-created descriptors, field/
record layouts, first members, RRN slots, authorities, indexes and journal hooks
publish atomically. CL DCLF/RCVF and RPG DISK read the same permanent/QTEMP rows.
Concurrent creators publish one object, and failed creation leaves no fragments.
SQL naming gives PUBLIC EXCLUDE and matching-schema-profile ownership with an
ADD check for another creator. System naming retains creator ownership and uses
library CRTAUT or its authorization list, all in the same creation transaction.

The parser accepts native system/SQL naming, default schema/collection rules,
explicit uppercase system identifiers, numeric/character/datetime types, column
CCSIDs, nullable/NOT NULL metadata, literal/NULL/current/type defaults and compound
primary/unique constraints. NUMERIC uses zoned and DECIMAL packed external
storage. Primary keys keep the declared null layout and add null checks without
aliasing RRNs or inventing a keyed file. Unique keys allow multiple NULL values
and use exact decimal/encoded character comparisons. Named constraints are
schema-unique; copying reconciles name collisions and moving into a conflict
rejects atomically. Restart/copy/move retain data, defaults and constraints.

RUNSQL exposes its 5000 encoded-byte limit in command/prompt metadata and checks
it at CCSIDs 37/1208. Explicit COMMIT(*NONE/*NC) is supported; the native CHG
default and other commitment modes reject pending real native commitment control.
Existing record units are not implicitly committed or bypassed. Unsupported SQL
and additional statements fail before effects; no input SQL is sent to SQLite.

VALUES binding validates all columns/values/defaults and freezes current datetime
values once per statement. One transaction includes rows, extents, versions and
journal capture. CPA5305 recovery releases the writer, retains the bound values
and retries without leaking prefix rows or duplicate entries. Cancellation,
writer waits, live authority, QTEMP isolation and private error names are covered.
Staging is bounded to one million normalized cells and 64 MiB.

All 6898 solution tests pass with zero failures/skips in 11m17s,
including 66 new SQL cases and an expanded real HTTP gateway test. All 250 focused cases and 4354 broad RPG/binding/work
cases pass. Locked restore, zero-warning/error Release build, 24x80/27x132 PTY
RUNSQL QTEMP create -> compiled CL INSERT/RCVF -> compiled RPG READ -> DROP,
fixed binary/packed/zoned/EBCDIC byte fixtures, frozen-source verification,
generated inventory, local links and whitespace checks pass. Full acceptance
uses eight xUnit threads. The first full run exposed an obsolete RUNSQL
unavailable-command assertion and was stopped. The revised HTTP test verifies
authenticated SQL create/insert/duplicate-error/drop across distinct server jobs,
and retains the planned-command check with RUNSQLSTM. A subsequent review
identified and corrected SQL naming authority/ownership defaults; its obsolete
full run was stopped before final acceptance. Schema 34, catalog inventory 320 and existing RPG/record
metadata versions remain unchanged; RUNSQL moves from planned to partial.

CREATE VIEW/INDEX, ALTER, foreign keys/general CHECK, identity/long SQL names,
CTAS/INSERT SELECT, UPDATE/DELETE/MERGE, expressions/functions, query interfaces,
RUNSQLSTM/STRSQL/QMQRY, native commitment/isolation, SQLSTATE/SQLCA/catalog ABI,
exact native generated-constraint naming and independent IBM golden/scale
acceptance remain open. C07/C08 stay partial; checklist remains 36 complete /
70 open. See docs/sql-tables.md.

Searched SQL mutation checkpoint (2026-10-19)

RUNSQL now executes searched UPDATE and DELETE against shared single-format
physical/QTEMP files. A bounded typed expression parser supports aliases,
original-value SET assignments, DEFAULT/current datetime, exact numeric
arithmetic, concatenation, COALESCE and three-valued comparisons/NULL/IN/BETWEEN/
NOT/AND/OR predicates. Names and operand/assignment types bind before scanning.
Integer ranges, decimal intermediate precision/scale and truncating exact
assignments are checked without passing decimal arithmetic through SQLite REAL.
Fixed CHAR padding and encoded CCSID comparisons use existing file rules.

Selection and normalized assignment values are staged before publication, under
one data transaction. The one-million-cell/64-MiB bounds include record numbers.
Each selected original row changes once; unique key shifts/swaps validate at
statement end. Constraint/trigger/journal/cancellation failures roll back rows,
indexes, versions and capture together. Successful updates capture exact UB/UP
pairs; DELETE captures DL and retains allocated sparse RRN extents. Referenced
unique-key updates reject before index changes pending native FK semantics.

Compact record-set holders protect selected RRNs through commit/rollback and
interoperate with ordinary record/file locks, earlier waiters, same-job retained
update-read cursors and authority. A 9,000-row SQL update exceeds the old holder
budget without leaking allocations; a 10,000-record lease uses one holder plus
its library parent. Cancellation unwinds leases, while job completion/recovery
removes memberships transactionally. Lock coordination upgrades atomically from
format 1 to 2; main catalog remains schema 34. WRKOBJLCK labels selected records.
UPDATE/DELETE require their write authority plus READ only for column references.

All 6949 solution tests pass with zero failures/skips in 8m07s,
including 51 added cases. All 347 focused checks and 4354 broad
RPG/binding/work cases pass. Locked restore, zero-warning/error Release build,
authenticated HTTP INSERT/UPDATE/DELETE, compiled permanent/QTEMP RPG reads,
24x80/27x132 terminal CL INSERT/UPDATE -> RPG read -> SQL DELETE -> RPG EOF,
frozen-source verification, generated inventory, local links and whitespace pass.
The first full run was stopped after an existing CLRPFM cancellation fixture
timed out. Both cases passed in isolation; the fixture now uses a dedicated
worker, confirms startup and always drains before releasing its writer. The
five-second cancellation assertion is retained. An earlier terminal fixture
ELSEIF was replaced with supported nested IF syntax before final acceptance.
A second eight-worker full run hit process-startup/job-API catalog timeouts;
all 12 cases in those suites pass directly. Final full acceptance uses four
xUnit workers to reduce concurrent process/catalog pressure.

Selection currently scans members. Index-driven selection, full native scale,
foreign-key/trigger DDL and dependent-record locking, full conversion/constraint
rules, job time zones/collations, SQLCA warnings/SQLSTATE/native message ABI,
MERGE/positioned mutations, broader DDL/functions/queries, native embedded SQL,
commitment/isolation and independent IBM golden acceptance remain open. Research
also confirms that native COMMIT(*NONE) INSERT has partial-failure exceptions;
the earlier deliberately atomic VALUES profile is stronger and native parity
there remains open. Catalog inventory stays 320 with 175 defined adapters;
C07/C08 remain partial and checklist stays 36 complete / 70 open.
See docs/sql-mutations.md.

RUNSQLSTM source-script checkpoint (2026-10-19)

RUNSQLSTM now processes named source members, single-format physical-file source
records and strict UTF-8 host streams through the shared SQL engine. The parser
handles required statement semicolons, quoted delimiters/doubled quotes, line
and nested block comments, source locations, source CCSIDs and member margins.
SRCSEQ/SRCDAT are excluded; deleted records are skipped. Stream BOMs are removed
and member margins do not truncate streams. Source snapshots are bounded before
execution and an individual statement may exceed RUNSQL's 5000-byte limit.

Explicit COMMIT(*NONE/*NC) runs statements in order, retaining completed work
after a later error. Automatic allocations end per statement; an operator wait
in a later statement does not retain a completed row's lock. Mixed CL commands
use normal command resolution/binding/authority and affect subsequent SQL library
resolution. ERRLVL uses strict severity comparison, stops execution above the
threshold and syntax-checks the remainder. Located diagnostics/completions go to
the job log; SQL9010 is monitorable by compiled CL. PROCESS(*SYN) executes no
statements and does not start commitment control. Unsupported routine/trigger/
compound sources reject before effects so their internal semicolons cannot
expose body fragments, including under ERRLVL(40).

A script sequence exposed a real in-memory UPDATE publication self-lock after
unique-index DDL: final validation opened another connection against its own
schema lock. Credentials and live job state now validate on the publisher's
existing connection, matching record-unit publication. Profile/job failures and
cancellation roll back data and indexes. Source and target authority, UTF-8
margin boundaries, source self-clear snapshots, large statements, capacity-wait
cancellation, final credential checks and HTTP/compiled CL/RPG paths are covered.

All 6999 solution tests pass with zero failures/skips in 8m31s,
including 50 added cases. All 397 focused checks and 4354 broad
RPG/binding/work cases pass. Locked restore, zero-warning/error Release build,
real authenticated HTTP script execution, 24x80/27x132 terminal compiled CL
RUNSQLSTM INSERT/UPDATE -> DCLF read -> RPG read -> SQL DELETE -> RPG EOF,
frozen-source verification, generated inventory, local links and whitespace pass.
Full acceptance uses four xUnit workers. Focused development failures identified
one over-margin fixture, one invalid test job-state value and the corrected
publisher self-lock; the accepted source contains no diagnostic instrumentation.

Native commitment/isolation/default-activation-group binding, printed listings,
semantic catalog preparation in syntax-only mode, procedural/routine/trigger
segmentation and execution, full source/CCSID/date/conversion and shipped-message
parity, native SQLSTATE/SQLCA, queries, no-commit INSERT partial-failure parity
and independent IBM golden/scale acceptance remain open. Execution requires
explicit OPTION(*NOLIST); native LIST/default CHG and other unfinished options
reject rather than silently print/commit. Host streams retain existing SERVICE
source-access policy. Main schema 34, lock format 2 and catalog inventory 320
remain unchanged; RUNSQLSTM moves from planned to partial and defined adapters
increase from 175 to 176. C07/C08 remain partial; checklist stays 36 complete /
70 open. See docs/sql-scripts.md.

Typed SELECT service checkpoint (2026-10-19)

The common SQL service now parses and binds SELECT projections/wildcards, aliases,
INNER/outer/CROSS joins, WHERE and three-valued predicates, GROUP BY/HAVING,
COUNT/COUNT_BIG/SUM/AVG/MIN/MAX with distinct values, ORDER BY/NULL placement and
FETCH FIRST. Positional typed markers preserve exact values and validate empty
results too. Generated SQLite SQL addresses authorized file members; input text
cannot reach private catalog tables, PRAGMA/extensions or a second statement.
Single-format logical sources expand physical-member bindings and select/omit
rules. QTEMP remains private. The shared scalar binder also adds casts, LIKE,
length/substrings/trim/case, NULLIF, ABS/ROUND and bounded datetime extraction and
labeled arithmetic to searched mutations.

Decimal expressions/results preserve up to 63 digits without SQLite REAL or
System.Decimal conversion. Group/distinct/sort keys preserve exact numeric and
space-padded CCSID equality. Aggregate widths/intermediate overflow, fixed versus
varying character metadata, Unicode character versus byte substring semantics,
SECOND versus EXTRACT precision and outer-join nullability are covered. Query
errors expose reason-specific SQLSTATE/SQLCODE; CAST truncation retains bounded
SQL0445/01004 diagnostics. Native message/SQLCA and broader conversion parity
remain open.

Forward cursors own one current row and a read transaction with bounded result,
parameter and aggregate state, evaluation and structure quotas. Credentials,
execution identity, job state and file/source read authority are rechecked on
fetch. EOF/error/disposal remove per-query functions before pool return. Job end,
end requests and factory shutdown close readers before QTEMP/workspace deletion;
separate factories attached to one catalog coordinate job resources, with reusable
128-cursor capacity. A private record-unit query first imports referenced members
and pending intents, finishes its synchronous statement, anchors an independent
read-only WAL snapshot and releases the thread-owned unit gate. Fetch/dispose can
move between threads while later record statements execute; earlier query values
remain stable. Commit/rollback/end explicitly require query readers closed until
native hold/isolation policy is implemented.

All 7116 solution tests pass with zero failures/skips in 8m19s,
including 117 added cases (115 query cases and two script SELECT checks).
All 514 focused SQL/record/initialization/lock/HTTP checks and 4354
broad RPG/binding/work cases pass. Locked restore, zero-warning/error Release
build, existing authenticated HTTP and compiled CL/RPG terminal acceptance at
24x80 and 27x132, frozen-source verification, generated inventory, local links
and whitespace checks pass. Acceptance uses four xUnit workers. Development
checks caught missing lifecycle handling, logical-file query expansion and scalar
padding/precision issues, all corrected before final acceptance; file-backed
reopen/WAL/pool cleanup, cross-thread/cross-factory job cleanup, active-query
cancellation, authority revocation and query capacity reuse are covered.

The service is a foundation, not completed STRSQL/QMQRY/WRKQRY, embedded SQL,
IWS SQL or the PostgreSQL reporting gateway. Native commitment/open/isolation/
hold semantics, catalog/name/type/diagnostic ABI, broader DDL/functions/conversions,
subqueries/CTEs/set operations, binary/graphic strings, general substring positions,
no-commit INSERT partial-failure parity, whole-member workspace/WAL/metadata scale
budgets and independent IBM golden/performance acceptance remain open. Main
schema 34, coordination format 2, catalog inventory 320 and 176 defined adapters
are unchanged. C07/C08 and G11 retain their existing partial/planned acceptance;
checklist stays 36 complete / 70 open. See docs/sql-queries.md.

STRSQL interactive session checkpoint (2026-10-19)

STRSQL now opens Enter SQL Statements through the existing suspended-program
terminal path. Default COMMIT(*NONE), system/SQL naming, interactive CL callers,
F4 command prompting, CRUD and typed SELECT share the catalog/file/query engines.
Display Data provides headings, NULL indicators, empty-result metadata, paging,
line/column positioning and horizontal scrolling. F4 builds SELECT clauses,
F9 retrieves history, F6/F10 edit lines, F13 changes supported attributes, and
F1/F12 preserve drafts/forms and show full SQL diagnostics. Bracketed paste
preserves SQL comment line breaks while retaining the terminal parser's pasted
function/attention-key protections and other screens' existing paste behavior.

Exit choices save, discard, resume or export terminated history through the
source-file store. Schema 35 adds private profile-owned saved settings/history/
drafts with optimistic revisions and cascading profile cleanup. Atomic save/
delete rolls back audit failures and excludes SQL/draft text from audit payloads.
Restored sessions retain saved attributes; conflicting sessions cannot replace
newer state. Discard can exit while retaining another session's newer save.
Source export uses live authority, CCSID and revision checks and reports a
saved-state conflict after a successful source write without hiding that effect.

The new shared ValidateSql/PrepareSqlQuery paths support PROCESS(*VLD) without
executing statements or evaluating query rows. PROCESS(*SYN) only parses. Static
mutation/creation binding is reused; actual data/constraint/trigger outcomes stay
execution-time checks. Optional conversion settings normalize date/time and
period/comma numeric input and session output/casts; QDECFMT is seeded without
replacing customized values. Strict numeric text conversion also fixes silent
scale changes from surrounding spaces, multiple points/signs and invariant
floating strings mistakenly treated as grouped digits; signed zero is preserved.
Full job attribute inheritance, century/timezone and conversion ABI remain open.

Results use a private disk-backed bounded scroll cache, one page at a time.
ALWAYS refresh reopens requested pages; FORWARD retains displayed rows and refreshes
unseen simple-table pages while materialized query results retain their snapshot.
Explicit refresh starts a new result. Per-statement automatic allocation scopes
also surround display access checks, so completed operations do not retain file
locks until STRSQL exits. Cached EOF results still validate original credentials,
job and physical/logical source authority. Disconnect interrupts executing SQLite
work and joins cleanup; job/factory end cancels readers and deletes caches.

All 7170 solution tests pass with zero failures/skips in 8m30s,
including 54 added interactive/persistence/security/history/validation cases.
All 1004 focused SQL/migration/command/session/display/system-value/record/
lock/HTTP checks and 4354 broad RPG/binding/work cases pass. Locked restore,
zero-warning/error Release build, real STRSQL CRUD/SELECT/F4/history/save/CL return/
RPG read/paste/validation at 24x80 and 27x132, the existing terminal acceptance
suite, frozen-source verification, generated inventory, local links and whitespace
checks pass. Full acceptance uses four xUnit workers. Development checks corrected
a save audit failure, result/draft state, exit-choice editing after a conflict,
locale/numeric conversions and the obsolete HTTP planned-STRSQL expectation.
The authenticated HTTP bridge now returns SQL6141/400 without a terminal while
another planned command still verifies the 501 fallback.

Native non-*NONE commitment/open/hold/isolation, job attribute and native
ISQL internal-object/device/history identity, complete services/list/prompt/
print/outfile/source/diagnostic/display ABI, changed-key/membership/access-plan
and scale semantics, broader SQL grammar, QMQRY/WRKQRY, embedded SQL, IWS/reporting
and independent current-release IBM golden/performance acceptance remain open.
C07/C08 remain partial; checklist stays 36 complete / 70 open. Main schema is 35,
coordination format stays 2 and the catalog still has 320 entries. STRSQL moves
from planned to partial; defined adapters increase from 176 to 177. See
docs/sql-interactive.md.

QMQRY saved SQL objects checkpoint (2026-10-19)

C07/C08 now have CRTQMQRY/RTVQMQRY/STRQMQRY and exact-name DLTQMQRY adapters.
Queries store source/CCSID/version/sort metadata in ordinary *QMQRY payloads,
retain shared object copy/move/rename/delete and QTEMP privacy/cleanup, and survive
restart without the source file. Create/replace audit publication is atomic;
replacement preserves existing identity/authorities, invalidates signatures and
retains required-signature policy. Explicit public/library-create/auth-list
creation authority and live credentials/job checks apply.

Source import obeys 79-byte truncation/padding and record continuation rules.
Retrieval atomically creates/replaces source members with shared authority,
CCSID/width/revision checks. Case-sensitive non-recursive substitution excludes
comments/quoted constants/names, supports identifiers/expressions and native CL
quoting, enforces 50 SETVAR pairs/55-character values and a 32767-byte substituted
statement bound. Metadata reserves 8192 characters for the longest escaped lists.
Missing variables prompt interactively in source order; empty Enter/F3/F12 cancels,
and *BLANK supplies one space. SQL AST isolation and shared record authority
remain in force. Typed CL variables and monitorable QWM2701 escapes with retained SQL diagnostics
are verified; SQL naming with *LIBL returns QWM2707.

Mutations/DDL use shared no-commit SQL, while SELECT uses bounded paged/shifted
output with refresh and returns to interactive CL without changing STRSQL saved
sessions. Query signatures/source identity, credentials/job and file authority
are rechecked while displaying, including cached EOF results. Disconnect closes
readers/caches/allocations. Authenticated HTTP creates and runs mutation queries;
missing-variable or SELECT output without a terminal fails explicitly.

All 7218 solution tests pass with zero failures/skips in 9:48.95, including
48 new query-management cases. All 71 focused query/interactive-SQL/HTTP checks pass. Locked restore,
zero-warning/error Release build, 993 frozen source/build/cache files, full
existing PTY acceptance plus QMQRY create/retrieve/run/delete/SETVAR/prompt/cancel/
CL return at 24x80 and 27x132, generated inventory, local links and whitespace
checks pass. Full acceptance uses four xUnit workers. Research uses IBM's native
command references and independent Query Management Programming source/variable
rules; current-release native golden acceptance remains open.

Print/outfile/QMFORM output, callable/global/DSQ state, externalized H/V and
sort/language formats, QRYDFN fallback, WRKQRY/WRKQMQRY/STRQM, generic/multiple-
library deletion, member text, exact native message/display/source/replace ABI,
job locale/commitment/isolation, non-hex collations, remote RDB and independent
native golden/scale acceptance remain open. C07/C08 stay partial; checklist
remains 36 complete / 70 open. Main schema stays 35; coordination format stays 2.
The catalog adds four partial commands: 324 entries and 181 defined adapters.
See docs/query-management.md.

QMQRY typed output files checkpoint (2026-10-19)

C07/C08 now support STRQMQRY OUTPUT(*OUTFILE), including command-only/batch/HTTP
execution, native *FIRST/*REPLACE/*ADD, current/library-list naming, output file/
member overrides and ordinary CL/RPG reads. The shared WriteSqlQueryFile service
spools a typed SELECT snapshot to bounded private disk storage before changing
the target, including self-replace/self-append and current private record-unit
source data. New output is a private physical table owned by the executing profile;
source character CCSIDs/types/nulls survive and keys/constraints/defaults/triggers
are not copied. Calculated/duplicate names follow IBM's SEL/COL shared-counter
example. Calculated query metadata distinguishes numeric aliases from expressions.
Existing targets match columns by ordinal with pre-clear layout/null checks.

Native SAVE boundaries are preserved: an existing member is cleared separately,
then answer rows publish in order through shared record/slot/constraint/journal/
capacity paths. A later failure retains the clear and successful prefix; messages
identify effects and count. CHANGE/OBJALTER, CLRPFM command authority, source/query
signatures/access, current job/credentials and member/format identity are enforced.
Per-row scopes keep allocations bounded past 8192 rows; operator recovery retries
only the failing row. Final creation/clear/insert checks use the publisher's own
connection, avoiding schema self-locks and rolling back failed publication hooks.
Query warnings survive the spool and reach the job log. Job/factory cancellation
interrupts spooling, joins work and closes/deletes temporary storage.

All 7270 solution tests pass with zero failures/skips in 9:57.45, with
53 added output cases and one obsolete unsupported-output case removed. All
930 focused SQL/query/record/lock/file/migration/command/terminal/HTTP checks
pass; the complete suite also verifies the 8203-row allocation case. Locked restore,
zero-warning/error Release build, 1000 frozen source/build/cache files, full PTY
acceptance plus native outfile create/empty-append/append and compiled CL/RPG reads
at 24x80 and 27x132, generated inventory, local links and whitespace pass.
Full acceptance uses four xUnit workers. Primary IBM command/SAVE documentation
informs types, naming, authority, CCSID and partial-effect boundaries; independent
current-release native golden acceptance remains open.

Physical targets, the implemented external field range and current native-like
prefix policy are partial. Print/QMFORM, LF/view output targets, wider decimals/
names/types, automatic first-member creation, remaining overrides/open/hold/
commitment/isolation, schema ownership, exact native clear/constraint/diagnostic/
archive semantics, orphan recovery, remote output and native golden/scale budgets
remain open. C07/C08 stay partial; checklist remains 36 complete / 70 open.
Main schema stays 35 and coordination format stays 2; no catalog entry or adapter
was added (324 entries / 181 defined adapters). See docs/sql-query-output.md.

SQL keyed index objects checkpoint (2026-10-19)

C07/C08 now support shared SQL CREATE/DROP INDEX execution through RUNSQL,
RUNSQLSTM, STRSQL and saved QMQRY, with syntax/validation modes and authenticated
HTTP use. SQL-created indexes are ordinary keyed *FILE/*LF objects with formats,
one logical member, maintained SQLite paths and base-file dependencies. ASC/DESC,
up to 120 distinct keys, exact decimals/CCSIDs, UNIQUE/UNIQUE WHERE NOT NULL and
RCDFMT ADD ALL COLUMNS/KEYS ONLY/explicit columns are implemented. Searched unique
updates validate at statement end. Compiled RPG keyed CHAIN consumes the index.

Native INDEX privilege, CRTLF use/signature authority, schema create/dictionary
authority and shared SQL ownership/public-authority rules apply. INDEX privilege
does not confer base READ. Creation and deletion publish transactionally, with
writer cancellation, live credential/job checks on the publisher connection,
record-unit rejection and rollback of invalidation/cancellation hooks. Restart,
competing factories, shared unique path retention and generic object/base rename,
copy and deletion are covered. PF copy excludes index-owned unique constraints.
Nonunique SQL index bindings follow base member addition/removal; unique indexes
explicitly reject more than one physical member. SQL DROP TABLE preflights and
cascades registered logical dependents atomically. Persistent allocations block
deletion. CL DLTF keeps its existing dependency behavior.

All 7316 solution tests pass with zero failures/skips in 10:32.76, including
46 added index cases. All 1107 focused SQL/query/file/member/object/lock/
migration/command/HTTP/terminal checks pass. Locked restore, zero-warning/error
Release build, 1006 frozen source/build/cache files, full PTY acceptance with SQL
index creation/deletion and compiled keyed RPG at 24x80 and 27x132, generated
inventory, local links and whitespace pass. Full acceptance uses four xUnit
workers and retains the 8203-row output allocation case. Primary IBM CREATE INDEX,
DROP, authority and traditional-I/O references inform the implementation;
independent current-release native golden acceptance remains open.

Expression/repeated/filtered/EVI/INCLUDE/partition indexes, wider native layouts/
names, DDS LF DROP INDEX, multi-member uniqueness, native optimizer/access-plan
integration, commitment/isolation, full diagnostics/format-ID/save/restore and
native golden/scale acceptance remain open. C07/C08 stay partial; checklist
remains 36 complete / 70 open. Main schema stays 35, coordination format stays 2,
and the catalog remains 324 entries / 181 defined adapters. See docs/sql-indexes.md.

SQL stored view queries checkpoint (2026-10-19)

C07/C08 now support CREATE VIEW, CREATE OR REPLACE VIEW and DROP VIEW through
RUNSQL/RUNSQLSTM, STRSQL, saved QMQRY, typed query output and authenticated HTTP.
Views are ordinary nonkeyed *FILE/*LF objects with external formats, logical
members and source dependencies. Stored SELECT definitions share typed nested
joins/projections/predicates/grouping/aggregates, exact wide decimals, nulls,
CCSIDs, one statement clock/evaluation budget and retained warning diagnostics.
Actual source identities and wildcard columns bind at creation and survive
library-list changes, source/view rename/move/copy and catalog restart.

Creation checks SELECT, sole-base OBJMGT, schema/CRTLF/dictionary authority and
SQL/system ownership rules. View-owner direct/indirect source READ is checked
without changing reader identity or granting base access. Live authority,
credentials, jobs and changed view ownership/definition are rechecked, including
cached EOF results. Owner login status is distinct from object rights. Replacement
preserves existing granted/public/AUTL authority and atomically checks dependent
layouts/cycles. DROP VIEW/TABLE preflight all logical descendants and delete in
dependency order; failed/cancelled/invalid publication hooks roll back the graph.
Private QTEMP dependencies and persistent allocations remain enforced.

SQL view queries read pending record-unit data through shared private snapshots.
Native record input uses a bounded lazy disk scroll cache, with CL DCLF/RCVF and
RPG nonkeyed READ, forward/reverse snapshot navigation, access checks and resource
cleanup. A 20000-row first-read case allocates less than 4 MiB on the managed
calling thread. Snapshot positions are ordinals; native key/RRN/update and record-
unit boundary behavior remain unimplemented and reject explicitly, including late
enlistment. Native record decimals retain the current codec bounds; wider query
results remain exact and require CAST for native input.

All 7361 solution tests pass with zero failures/skips in 9:56.31, including
45 added view cases. All 1308 focused SQL/query/file/member/object/lock/
migration/command/authority/security/HTTP/terminal checks pass. Locked restore,
zero-warning/error Release build, 1015 frozen source/build/cache files, full PTY
acceptance including SQL view create/query/drop and compiled CL/RPG input at
24x80 and 27x132, generated inventory, local links and whitespace pass. Full
acceptance uses four xUnit workers and retains the 8203-row output allocation
case. Primary IBM CREATE VIEW and SQL ownership references inform the design;
independent current-release native golden acceptance remains open.

View INSERT/UPDATE/DELETE, LOCAL/CASCADED CHECK OPTION, ALTER VIEW, INSTEAD OF
triggers, complete updatability/default/grant-option provenance, native record
identity/update/commitment behavior, wider names/layouts/grammar, archive lifecycle,
format-ID/diagnostic ABI and native golden/scale acceptance remain open.
C07/C08 stay partial; checklist remains 36 complete / 70 open. Main schema stays
35, coordination format stays 2 and the catalog stays 324 entries / 181 defined
adapters. See docs/sql-views.md.

SQL view writes and CHECK OPTION checkpoint (2026-10-19)

C07/C08 now support SQL INSERT/UPDATE/DELETE through single-source view chains
ending in a single-format PF. Direct projected columns remain writable through
aliases and nested views; derived columns work in predicates and reject target
assignment. Hidden columns and DEFAULT retain base nullability, defaults,
conversions and constraints. Constant-only views remain deletable. Joins,
DISTINCT and aggregate/grouped views reject writes before publication.

WITH [LOCAL|CASCADED] CHECK OPTION persists with the definition; omitted kind
means CASCADED. LOCAL checks the declaring predicate and inherited explicit
checks; CASCADED adds underlying predicates. The independent IBM five-view
matrix, NULL/UNKNOWN, normalized proposed rows, inherited hidden defaults and
unstable/nondeletable checked definitions are covered. Failed checks return
SQL0161/44000 and roll back the current SQL statement. Unchecked inserts may
create rows outside the view. UPDATE/DELETE respect all source visibility filters.

Root write rights and READ for referenced view columns remain the caller's.
View owners need READ on direct/indirect sources and the requested write right
on each direct source. Publisher-connection checks catch changed authority,
credentials, ownership, definitions and jobs without impersonation or granting
base access. Delegated capacity recovery revalidates the bound path. Shared
record allocations, unique swaps, slots and journals remain in force; journal
entries retain the real caller. Cancellation and job cleanup join blocked SQL
writers before job-catalog publication. Enlisted base resources reject with
IPC0211. Restart and competing factories preserve checks/base uniqueness.

All 7406 solution tests pass with zero failures/skips in 10:24.47; this phase
adds 46 cases and removes one obsolete unsupported-CHECK-OPTION rejection case.
All 1353 focused SQL/query/file/member/object/lock/migration/command/
authority/security/HTTP/terminal checks pass. Locked restore, warning-free Release
build, 1021 frozen source/build/cache files, full PTY including checked view writes
and failed-update rollback at 24x80 and 27x132, generated inventory, local links
and whitespace pass. SQL scripts, saved QMQRY and authenticated HTTP exercise
view writes. Full acceptance uses four xUnit workers and retains the 8203-row
output allocation test. Primary IBM CREATE VIEW/INSERT/SQL ownership references
inform the implementation; independent current-release native acceptance is open.

ALTER VIEW, INSTEAD OF triggers, DDS LF/source-file view writes, complete grant-
option/default provenance, native record identity/update/commitment/isolation,
native COMMIT(*NONE) partial-prefix policy, wider names/layouts/grammar, archive
lifecycle, format-ID/diagnostic ABI and native golden/scale acceptance remain
open. C07/C08 stay partial; checklist remains 36 complete / 70 open. Main schema
stays 35, coordination format stays 2 and the catalog stays 324 entries / 181
defined adapters. See docs/sql-views.md.

SQL ALTER TABLE checkpoint (2026-10-19)

C07/C08 now support ALTER TABLE for SQL-created PFs through RUNSQL/RUNSQLSTM,
STRSQL, saved QMQRY and authenticated HTTP. Consecutive clauses add columns with
optional BEFORE, drop columns with default CASCADE or RESTRICT, alter compatible
types/defaults/nullability and add/drop primary or unique constraints. Existing
rows receive native addition defaults, including historical values for implicit
nonnullable datetime defaults. Explicit CURRENT defaults share one statement
clock across all rows and members. Character shrinkage respects encoded character
boundaries; exact numeric conversion preserves the shared assignment rules.

Pure proposed-metadata binding validates dependent view/index layouts without
rewriting data or running catalog hooks. Added columns retain frozen view stars
and index projections; type changes refresh nested view and logical record
formats and exact keys. Shared DDS formats follow supported base layout changes.
DROP COLUMN enforces actual referenced-column RESTRICT rules or cascades affected
views/indexes and their descendants. Whole dropped keys disappear under either
mode; partially affected keys require CASCADE. Invalid select/omit constants and
failed conversions/constraints roll back metadata, data and the dependency graph.

ALTER-only authority requires no caller data READ/ADD/UPDATE. Publication checks
live credentials, jobs, ALTER rights, intended definition/owner and member
identities on its writer connection. Object ownership, creation time and granted
private/public/AUTL authorities remain. Streaming transactional rewrites preserve
RRNs, sparse extents, capacity, member identity and record incarnations. Persistent
allocations block changes; enlisted resources reject IPC0211. Journal capture is
reinstalled for new layouts without emitting false record entries while copying;
compatible application hooks are recreated after loading. Restart, competing
factories, cancellation, metadata-hook rollback, UTF-8, multi-member consistency,
scripts, QMQRY and HTTP are covered.

All 7449 solution tests pass with zero failures/skips in 10:24.39, including
43 added ALTER TABLE cases. All 1396 focused SQL/query/file/member/object/
lock/migration/command/authority/security/HTTP/terminal checks pass. Locked restore,
warning-free Release build, 1031 frozen source/build/cache files, full PTY with
ALTER and compiled CL/RPG reads of a newly added column at 24x80 and 27x132,
generated inventory, local links and whitespace pass. The full suite uses four
xUnit workers and retains the 8203-row output and 20000-row view allocation cases.
Primary IBM ALTER TABLE, cascaded-effect, privilege and SQL-message references
inform the implementation; current-release native golden acceptance is open.

DDS-created/source-file ALTER, foreign/general CHECK constraints and public
triggers, generated/identity/hidden/LOB/distinct/temporal/partition columns,
wider names/types/layouts, native format-ID/DDL-journal/diagnostic ABI,
commitment/isolation/partial-failure policy, archive lifecycle and independent
native golden/scale acceptance remain open. Unregistered physical constraints,
external SQL relationships and column drops with unregistered hooks/indexes
reject before rewrite. Multi-member keys reject pending file-wide uniqueness.
C07/C08 stay partial; checklist remains 36 complete / 70 open. Main schema stays
35, coordination format stays 2 and catalog stays 324 entries / 181 defined
adapters. See docs/sql-alter-tables.md.

SQL CHECK constraints checkpoint (2026-10-19)

C07/C08 now accept named or anonymous column/table CHECK constraints in CREATE
TABLE and ALTER TABLE ADD, with DROP CHECK/CONSTRAINT. TRUE and UNKNOWN satisfy a
check; FALSE reports SQL0545 / 23513. Column checks reference only their own
column. Definitions reject parameters, aggregates, special registers, unsupported
functions and invalid references with SQL0546 / 42621. Existing typed expressions
retain exact decimals, character padding, CCSIDs, deterministic scalar functions
and expression diagnostics.

A shared Ipc.Sql assembly places SQL models, definitions, parsing, binding and
record conversion below services, with original public type names forwarded and
legacy constructor signatures retained. Every normal/private SQLite connection
registers the same bounded versioned evaluator. Physical/logical record writes,
retained cursors, compiled RPG, native copies/initialization, query output and
record-unit publication therefore enforce the same predicates. Descriptions and
row frames are bounded; referenced columns travel in 64-column chunks, and
per-connection caches retain at most 16 plans/two million description characters.

ALTER validates all members atomically, rebinds checks after type changes and
evaluates normalized values. Dropped-column dependencies follow RESTRICT/CASCADE;
constraint names share a schema namespace, and object copies rename conflicts in
both catalog definitions and member schema. Restart and relocation preserve
predicate meaning. PROCESS(*VLD) checks metadata without evaluating data or
running publication hooks. ALTER-only authority, live publication checks,
cancellation, multi-member consistency, journal/slot rollback and private
workspaces retain their existing contracts. CPYF/INZPFM report CPF502F; RPG file
error handling recovers after a subsequent successful write. Query-output append
retains its documented accepted-prefix behavior.

All 7481 solution tests pass with zero failures/skips in 10:51.13. The phase adds
33 checks and replaces one obsolete unsupported-CHECK rejection case. All
1428 focused SQL/query/file/member/object/lock/migration/command/authority/
security/HTTP/terminal checks pass. Locked restore, warning-free Release build,
1045 frozen source/build/cache inputs, full PTY CHECK/CL/RPG failure and recovery
at 24x80 and 27x132, generated inventory, local links and whitespace pass. The full
suite uses four xUnit workers and retains the 8203-row output and 20000-row native record
allocation cases. Primary IBM constraint/CREATE TABLE/ALTER TABLE/SQL-message/file
status references inform the implementation; independent native acceptance is
still open.

The initial focused run passed 1426/1428 and exposed two timing-sensitive
cancellation failures (query-output shutdown and a view writer wait). The same
frozen inputs then passed all five isolated cancellation cases and the full
1428-case focused repeat; the final full-suite result is recorded above. The
initial failure log is retained at /tmp/iseriespc-sqlcheck-focused-initial-failed.log.

Foreign keys, public triggers, check-pending administration and native violation
actions, DDS-created/source-file ALTER, wider generated/identity/LOB/distinct/
temporal/partition/name/type/layout options, native format-ID/diagnostic/DDL-
journal/commitment/isolation ABI, archive lifecycle and independent current-
release golden/scale acceptance remain open. C07/C08 stay partial; checklist
remains 36 complete / 70 open. Main schema stays 35, coordination format stays 2
and catalog stays 324 entries / 181 defined adapters. See
docs/sql-check-constraints.md.

SQL referential integrity development checkpoint (2026-10-19)

C08 referential integrity now has a working SQL core: named/anonymous column and
table foreign keys, CREATE/ALTER ADD/DROP, matching parent key/field binding,
MAXMBRS(1), composite and NULL semantics, exact decimal and padded-character
comparisons. All five delete actions are implemented, including partial nullable
SET NULL keys. NO ACTION validates complete statements and self references;
RESTRICT rejects a changed parent key immediately. Parent key swaps, SQL0530/
0531/0532 diagnostics, atomic schema constraint cascades, SQL DROP retaining
child rows, unrelated ALTER, physical/logical/cursor writes and journaled private
workspace publication are covered by 26 added checks. Reference/child indexes
and registered collations retain external record layouts. Referential checks are
disabled only during physical rewrites, preserving ordinary catalog cascades.

All 1454 focused SQL/query/file/member/object/lock/migration/command/authority/
security/HTTP/terminal regressions pass with zero failures/skips on a warning-free
Release build and 1054 unchanged source/build/cache inputs. An initial broad run
passed 1453/1454, reproducing the query-output shutdown timing failure seen in
the preceding CHECK phase. Query-output and view-writer cancellation controllers
and synchronous workers now run on dedicated tasks; the complete focused repeat
passes, including both shutdown cases. No timeout was increased and no assertion
was removed. The earlier failed log is retained in
/tmp/iseriespc-sqlforeign-focused4.log; the successful log is focused5.log in the
same directory prefix.

This is a development checkpoint, not complete referential-integrity acceptance.
Rename/move/duplicate/member/archive rebinding, cascading record/file allocations
and authority delegation, live indirect publication checks, native command/RPG/
HTTP/terminal flows, old-database/restart/concurrency/scale fixtures, native
constraint states and journal/diagnostic/commitment/delete-path rules remain.
Final locked restore, full solution tests, both PTY geometries and final inventory/
link/input acceptance must follow the completed implementation. The last full
solution/PTY checkpoint remains SQL CHECK with 7481 tests. C07/C08 stay partial;
checklist remains 36 complete / 70 open. Main schema stays 35, coordination stays
2 and catalog stays 324 entries / 181 defined adapters. See
docs/sql-foreign-keys-progress.md. Continue with lifecycle metadata rebinding and
cascading record allocation enforcement before marking this work accepted.

SQL referential lifecycle and allocation development checkpoint (2026-10-19)

C08 now rebinds incoming references across parent rename/move and library rename,
remaps self references on duplicate objects, binds same-library copied parent
names in the destination and retains external-library parents. Managed partial
SET NULL hooks and schema constraint names follow those identities. Retained
queries/allocations block relocation. ALTER publication rechecks new REFERENCES
rights and the bound parent/member identity after catalog hooks.

Live searched, keyed/logical and retained-cursor parent deletes now protect child
file allocations and affected record sets through commit/rollback. Relationship
walks cover empty descendant paths; transaction-local tracking captures additional
rows reached after a publication hook changes the dependent set. Late allocations
validate live caller/job state on the publishing connection, preserving catalog
hook atomicity without opening a competing reader. One holder per member handles
an 8301-row cascade without exhausting the 8192-holder budget. Child actions
operate under constraint delegation without granting caller child data access.

CLRPFM rejects a nonempty parent even without child rows; native CPYF/INZPFM report
CPF502D with rollback. RPG WRITE and RESTRICT UPDATE report 1022 and recover after
valid I/O. Saved scripts, QMQRY, compiled CL MONMSG, HTTP and real terminal flows
exercise violation diagnostics and cascades. DISK/WORKSTN PREFIX now accepts an
uppercase character literal and persists imported field names after source removal.
This continuation adds 40 cases: 32 foreign-key and eight PREFIX cases. The full
foreign-key phase contains 58 referential cases and eight PREFIX cases.

All 7547 solution tests pass with zero failures/skips in 10:58.84; all 67 targeted
foreign-key/PREFIX/HTTP checks pass. Locked restore, warning-free Release build,
1064 unchanged source/build/cache inputs, full PTY including SQL-FK-CL-RPG-CASCADE
at 24x80/27x132, generated inventory, local links and whitespace pass. Full-suite
concurrency remains four xUnit workers and retains the 8203-row output and
20000-row native record allocation cases. An initial 1240-case foreign-key/RPG
run passed 1238 and exposed two duplicate-audit failures during SET NULL/DEFAULT;
explicit existence checks replaced trigger OR IGNORE, whose policy was overridden
by the outer update. The 67-case repeat and complete solution suite pass. The first
PTY run rejected missing EVAL keywords in the new fixture; the corrected full
smoke passes. Failed logs remain at /tmp/iseriespc-sqlforeign-focused11.log and
/tmp/iseriespc-sqlforeign-pty-initial-failed.log.

This remains development verification, not complete native referential-integrity
acceptance. Defined/disabled/check-pending states, missing-parent/mismatched-data
copy transitions, member/archive rebinding, CPYF CRTFILE, private-unit delegation
and indirect publication, full publication-authority coverage, parent UPDATE
indirect allocations, dedicated legacy/
restart/competing-writer fixtures, native journal/cause/feedback/commitment and
delete-path contracts, incoming constraint limits and independent current-release
golden/scale acceptance remain open. C07/C08/C09 stay partial; checklist stays
36 complete / 70 open. Main schema remains 35, coordination remains 2 and catalog
remains 324 entries / 181 defined adapters. See docs/sql-foreign-keys-progress.md
and docs/rpg-file-definitions.md. Continue with native constraint states and member/
archive transitions, without treating this checkpoint as feature completion.

SQL parent validation and publication development checkpoint (2026-10-19)

C08 parent-key UPDATE now protects dependent file allocations and stable reads
of the first matching dependent per parent row. SQL tables/views, physical keyed
files, logical files and retained cursors share this validation. NO ACTION and
RESTRICT deletes acquire the same stable reads across the planned cascade graph.
Read sets allow concurrent readers and the caller's own retained update read,
while rejecting a competing update. Validation filters assigned parent-key fields,
uses bounded record-set holders, retains them through commit/rollback and delegates
child validation without requiring child data permissions or parent DELETE rights.

A misplaced cascade-planner call in physical keyed UPDATE has been replaced with
parent validation; physical keyed DELETE now plans and locks its actual indirect
actions. Unrelated updates avoid cascading child write locks. Member publication
checks both registry bindings and slot identities. Publisher-aware file and row
allocations use the existing writer connection for live caller/job/resource
validation, including hooks that own catalog table/schema locks.

SQL UPDATE/DELETE now recheck all required root object permissions immediately
before publication, including READ needed by predicates or expressions. Constant-
only statements retain their narrower requirements. Revocation by parent or child
hooks rolls back data, indirect changes and authority edits. Existing view-owner
and grant semantics remain enforced through their existing publication callback.

The continuation adds 46 cases. All 7593 solution tests pass with zero failures/
skips in 11:02.71; all 824 focused SQL/view/file/lock/transaction/object/HTTP checks
pass. Locked restore, zero-warning/error Release build, 1068 unchanged source/
build/cache inputs, complete display PTY at 24x80 and 27x132, generated inventory,
local links and whitespace pass. The full suite retains four xUnit workers and
the 8203-row output and 20000-row native record allocation cases.

Initial probes reproduced missing publication checks (3/10 passed) and coordination
gaps (1/13 passed). The first expanded run (75/81) also exposed that ordinary read
mode allowed retained updates and that the new logical fixture used its physical
format name instead of the logical format name. Later runs (90/91 and 100/101)
showed the member guard needed to inspect the registry in addition to slot identity;
final focused and full repeats pass. Logs remain under
/tmp/iseriespc-sqlforeign-update- for review. IBM's referential-integrity locking
reference informed the stable-read/cascading-write distinction; independent
current-release native acceptance remains open.

Full-suite stress also found a production shutdown ordering bug: connection setup
held the factory lifecycle lock while waiting on SQLite, delaying cancellation.
Connection opening and initial memory keepalive setup now run outside that lock;
disposal signals cancellation, closes runtime resources and joins in-flight opens
before closing the pool. An owned session closes its captured job's query resources
before catalog access, while catalog changes still authorize normally. Three
schema-writer/session regressions cover warm/cold opening and owned cleanup.

The first full run passed 7588/7590 and exposed two existing lifecycle timing
failures: journal recovery cancellation and cross-factory private-query shutdown.
Their controllers and synchronous workers now use dedicated tasks, with a start
signal for recovery and unconditional cleanup joins. Existing four-/ten-second
bounds and all cancellation/authority/data/cleanup assertions remain. The 770-case focused repeat passed, but the second full run (7589/7590) exposed
query-spooling shutdown exhausting its evaluation budget. Dedicated schema-writer
probes reproduced the production lock ordering; their original two cases failed,
and all three strengthened warm/cold/session cases pass after the fix. The final
expanded focused and complete full repeats pass. Initial/second full logs remain
at /tmp/iseriespc-sqlforeign-update-full-initial-failed.log and
/tmp/iseriespc-sqlforeign-update-full-second-failed.log. No timeout or query budget
was increased, no cancellation assertion was removed, and runtime/terminal inputs
were reverified after the production fix.

This is development regression verification, not complete referential-integrity
acceptance. Native constraint states and CHECK administration, member/archive and
missing-parent/mismatched-data copy transitions, private-unit delegation and
publication, outgoing foreign-key parent read allocations, implicit child file
allocation lifetimes through parent close, native file/cursor publication-authority
coverage, CPYF CRTFILE, dedicated legacy/restart/competing-writer fixtures, incoming
constraint limits, journal/cause/diagnostic/feedback/commitment/delete-path rules
and native golden/scale acceptance remain open. C07/C08/C09 stay partial; checklist
stays 36 complete / 70 open. Main schema stays 35, coordination stays 2 and catalog
stays 324 entries / 181 defined adapters. See docs/sql-foreign-keys-progress.md.

SQL constraint relation limits development checkpoint (2026-10-19)

C08 now counts primary, unique and CHECK constraints, outgoing foreign keys and
incoming referential relations across libraries against the same 5000-relation
limit. A self reference consumes one relation. Proposed definitions replace their
old versions, and cascaded incoming removals release capacity within the same
ALTER statement. SQL validation and execution share the limit check, including
tables without outgoing foreign keys. Overflow reports SQL0642 / 54021.

Object copies count their remapped destination parents before physical copying.
Final writer checks catch overflow introduced by catalog hooks and roll back
metadata, schema and indirect changes together. Concurrent CREATE statements
cannot both consume the last parent slot. Dropping constraints or child tables
releases capacity for subsequent CREATE, ALTER and copy operations.

Eleven new cases cover 5000/5001 CHECK boundaries, mixed own/incoming relations,
self references, copies, destination-library parent binding, competing writers,
post-hook rollback, definition replacement and atomic/cascaded capacity reuse.
The original two CHECK-overflow probes failed because validation and execution
accepted 5001 constraints. Their fixtures use simple IS NULL predicates to stay
within the existing expression-node budget, which remains unchanged.

All 7604 solution tests pass with zero failures/skips in 11:00.96, using four xUnit
workers. All 348 broader focused checks and the final eleven boundary cases pass.
Locked restore, a zero-warning/error Release build, 1069 unchanged source/build/
cache inputs and complete display PTY at 24x80 and 27x132 pass. Generated inventory,
local links and phase whitespace pass. Logs remain under /tmp/iseriespc-sqllimits-.

This closes the implemented-path constraint-counting gap, not native referential
acceptance. Independent native boundary/golden verification, defined/disabled/
check-pending states and CHECK administration, member/archive/copy transitions,
private-unit delegation/publication, outgoing parent read allocations, implicit
child allocations through parent close, native publication-authority coverage,
CPYF CRTFILE, legacy/restart/scale and journal/feedback/commitment/delete-path
contracts remain open. C07/C08/C09 stay partial; checklist stays 36 complete / 70
open. Main schema stays 35, coordination stays 2 and catalog stays 324 entries /
181 defined adapters. See docs/sql-foreign-keys-progress.md.

SQL constraint administration development checkpoint (2026-10-19)

C07/C08 now provide CHGPFCST FILE/CST positional or keyword arguments, up to 300
explicit names or *ALL/*CHKPND, STATE(*SAME/*ENABLED/*DISABLED) and CHECK(*YES/*NO).
Each constraint commits separately; a failed name leaves preceding successful
changes intact and processing continues. Management or alter authority on the
physical child and library execute authority suffice. Parent validation delegates
reads without granting parent or child data authority.

Disabled constraints retain their access paths and permit I/O without that
constraint's enforcement. Verification on enable commits check-pending state for
invalid existing rows with CPF32B2 diagnostics. Pending selection history survives
disable/repair so *CHKPND can select the constraint again. CHECK(*NO) skips old-row
verification, with valid existing data remaining the caller's responsibility;
subsequent writes enforce enabled constraints. SQL0971/57011 blocks all dependent
I/O and parent UPDATE/DELETE for enabled pending foreign keys. Pending CHECKs block
reads and operations requiring reads while enforcing new insert/update values.
SQL, view, logical and native data authorization share the restrictions.

State changes acquire related exclusive file allocations, reject retained query/
update opens, and retain their leases through commit/rollback. Publication checks
live management authority, owner, definitions, member registries and slot identities.
Trusted CHECK row copying restores normal enforcement after success or failure.
Old JSON omits false state flags, existing constructors/deconstruction remain,
and CHECK program serialization excludes administration flags.

Copies preserve disabled CHECK/foreign state and managed partial SET NULL hooks.
Absent/incompatible destination parents now leave defined relationships; matching
parents with copied-data mismatches leave pending relationships. CHGPFCST can
establish/reverify them later. Automatic parent/member/attribute/archive transitions
remain unfinished. CLRPFM permits clearing a parent whose relationships are disabled.

The continuation adds 35 cases: 33 state/administration/copy/metadata cases and
two job-owned initialization-recovery cases. All 7639 solution tests and 806 focused checks
pass with zero failures/skips in 11:18.92, using four xUnit workers. Locked restore,
a zero-warning/error Release build, 1074 unchanged source/build/cache inputs and
complete display PTY at 24x80 and 27x132 pass. PTY now verifies disable, invalid
enable, SQL0971 rejection, repair and re-enable. Generated inventory, local links
and phase whitespace pass. Logs remain under /tmp/iseriespc-sqladmin-.

The initial 13 cases passed. An expanded run passed 238/240; its two allocation
fixtures used a sequential cursor without an enclosing command/read allocation.
They now use retained update opens, with separate retained-query cases added.
No production allocation assertion was weakened. The final focused/full repeats
and terminal workflow pass.

The first full run passed 7633/7636. Two initialization-recovery cases exposed
file-state checking opening a connection behind recovery's schema writer. Recovery
now rechecks file/journal/receiver authority and file state on its existing writer
transaction, with two additional job-owned cases. A query-shutdown controller also
missed its allocation; improved diagnostics and unconditional worker joins showed
budget exhaustion in the 804/805 expanded repeat. Metadata permission checking had
been coupled to data-state I/O, causing file/lock inspection to wait behind that
query. A deterministic pending-file metadata probe reproduced SQL0971 even in file
existence lookup. Data boundaries now opt into state checks separately, while
metadata and library-list name resolution remain available for repair. The new
probe, management-only unqualified CLI, lowercase special selectors and shutdown
checks pass. Existing ten-second bounds and query evaluation budgets remain.
Initial full/focused logs and the source manifest are retained under the prefix.

This is development regression verification, not complete native constraint
acceptance. Automatic lifecycle and archive transitions, legacy/restart and larger
scale/cancellation/competing-writer fixtures, private-unit delegation/publication,
outgoing parent reads, implicit child allocations through parent close, native
file/cursor publication authority, pending OPEN and full diagnostic/feedback ABI,
CPYF CRTFILE, journal/cause/commitment/delete-path contracts and independent native
goldens remain open. C07/C08/C09 stay partial; checklist stays 36 complete / 70
open. Main schema stays 35 and coordination stays 2. Catalog now has 325 entries /
182 defined adapters, with CHGPFCST partial. See docs/sql-foreign-keys-progress.md.

SQL automatic constraint establishment development checkpoint (2026-10-19)

C08 now establishes defined foreign relationships when a compatible parent is
created, a parent or dependent member is added, or ALTER supplies the required
parent column, attributes or key. Both zero-member creation orders and actual
nondefault member names work, including self references. Incompatible parents
leave the relationship defined. Establishment preserves disabled state; an
established disabled relation is pending until verification. Enabled relations
with unmatched existing data also become pending instead of rejecting the parent.

Establishment shares the initiating writer transaction and delegates child changes
without requiring child data or management authority. Allocation preflight runs
before schema DDL, so a retained child query reports CPF1002 rather than a SQLite
schema-lock failure. Related exclusive leases remain through commit/rollback.
Final checks cover root permission, live caller/job, definitions, owner, member
registries and slot identities. Rewritten schemas compile their INSERT/UPDATE/
DELETE paths, and enforced relationships recheck rows after hooks. Explicit
CHGPFCST enable likewise rejects rows introduced after its initial verification;
CHECK(*NO) retains its documented skip-verification behavior.

Twenty new cases cover automatic establishment, incompatible-to-compatible ALTER,
member order/names, disabled state, management/data delegation, retained queries,
late authority revocation and atomic rollback of hook changes to related members,
slot identities, ownership or data. The original five probes failed before the
implementation. An expanded run passed 445/446 and exposed allocation preflight
running after schema DDL; moving it before DDL restores the native CPF1002 result.
The final 834 focused checks and all 7659 solution tests pass with zero failures/
skips. The full run uses four xUnit workers and completes in 11:36.04.

Locked restore, a zero-warning/error Release build, 1077 unchanged source/build/
cache inputs and complete display PTY at 24x80 and 27x132 pass. The terminal now
copies a child into a library without its parent, creates the parent, and confirms
new orphan writes reject automatically (SQL-AUTO-ESTABLISH). Existing constraint
administration and CL/RPG cascade flows remain covered. Generated inventory, local
links and phase whitespace pass. Logs remain under /tmp/iseriespc-sqlauto-.

This is development verification. Automatic member removal, relocation,
duplicate-parent and restore/archive transitions, mismatching referenced-type
ALTER behavior, private-unit delegation/publication, outgoing parent reads,
implicit child allocations through parent close, native pending OPEN/feedback,
CPYF CRTFILE, legacy/restart/cancellation/scale, journal/commitment/delete-path
contracts and independent native goldens remain open. C07/C08/C09 stay partial;
checklist stays 36 complete / 70 open. Main schema stays 35, coordination stays 2
and catalog stays 325 entries / 182 defined adapters.
See docs/sql-foreign-keys-progress.md.
