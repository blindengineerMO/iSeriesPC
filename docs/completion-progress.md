# Completion progress — 2026-09-15

The user requested completion of all 106 PLAN.md checklist items. **33 items are
complete and 73 remain open.** The app is under construction. PLAN.md is intentionally
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
  report, now extended by 288 normalized command/API entries and release contracts.
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
    tasks remain open, as do RPG preprocessing and debugger integration in C09/C15.


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

Paused at the user's request after this verified checkpoint. PLAN.md remains
33 complete / 73 open. Resume within C07; display file I/O, procedure bindings,
wider command/message delivery and the remaining completion categories are still
unfinished. No broad checklist item was marked complete for this partial checkpoint.
