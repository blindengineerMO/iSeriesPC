# SQL referential integrity: work in progress

C08 referential integrity is being implemented. This is a development checkpoint;
the foreign-key work and the broader C07/C08 requirements remain unfinished.
The latest full regression and terminal verification for this work in progress
passes 7,817 solution tests. Native referential-integrity acceptance remains open.

The current SQL bridge accepts column REFERENCES and table FOREIGN KEY clauses,
named or anonymous constraints, explicit or implicit parent keys, and ALTER ADD /
DROP FOREIGN KEY / DROP CONSTRAINT. Parent keys must identify primary or unique
constraints. Composite keys support corresponding column permutations, NULL
exemption, exact decimals and padded character equality. Matching field attributes
and MAXMBRS(1) are validated before publication. Native definitions and rules are
described in IBM's [CREATE TABLE reference](https://www.ibm.com/docs/en/i/7.5.0?topic=statements-create-table)
and [referential file requirements](https://www.ibm.com/docs/en/i/7.5.0?topic=constraints-before-you-add-referential).

Constraint limits now include the table's primary, unique and CHECK constraints,
its outgoing foreign keys, and incoming foreign keys from every library. A self
reference consumes one relation. SQL validation, CREATE, ALTER and object copying
use the proposed definitions, including cascaded removals and destination parent
rebinding. Reaching 5,000 relations is allowed; exceeding it reports SQL0642 /
54021 before publication. The writer rechecks after catalog hooks, and concurrent
writers cannot both consume the last slot. Failed copies preserve the source and
leave no destination schema. These follow IBM's [constraint setup rules](https://www.ibm.com/docs/en/i/7.4.0?topic=constraints-setting-up-your-database)
and [SQL0642 reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/rzalapdf.pdf).
Independent native boundary verification remains part of final acceptance.

All five delete actions have an implementation: NO ACTION, RESTRICT, CASCADE,
SET NULL and SET DEFAULT. Partial nullable composite keys have a managed hook
that changes only nullable columns for SET NULL. Deferred checks implement
statement-end NO ACTION across searched updates and multi-row VALUES, including
self references and parent key swaps. RESTRICT tests the original referenced key
before its update. SQL0530/23503 identifies an unmatched child/default key;
SQL0531 and SQL0532 distinguish blocked parent updates/deletes, with 23001 for
RESTRICT and 23504 for NO ACTION. See IBM's [SQL messages and codes](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/rzalapdf.pdf).

The physical schema uses registered decimal/text collations, referenceable unique
indexes and child access paths. Every normal/private connection registers these
collations. Public record fields and typed expression semantics remain shared.
ALTER copies rows with foreign-key enforcement disabled only during the physical
rewrite, retaining ordinary catalog cascades. It validates the resulting database
before commit. Existing native record/logical/cursor and enlisted workspace paths
exercise the same physical constraints. Parent-key CASCADE and SQL DROP TABLE
remove dependent foreign constraints while retaining child objects and records.
Generic native deletion retains the default restriction on incoming relationships.

Rename/move/library rename now update incoming parent metadata and managed partial
NULL hooks atomically. Duplicate objects remap self references; same-library
parent names bind in the destination library, while external-library parents
retain their original identity. Missing or incompatible destination parents leave
a copied relationship defined. Matching parents with mismatched copied data leave
it check pending. Existing queries and file allocations block relocation. ALTER publication rechecks new parent REFERENCES
rights and the bound parent/member identity after publication hooks.

Live SQL, keyed/logical and retained-cursor parent deletes acquire child file
allocations and bounded sets of affected record locks, including descendant and
self cascades. One record-set holder per member avoids the 8,192-holder limit;
an 8,301-row cascade exercises this path. Transaction-local change tracking also
captures rows reached after a publication hook changes the dependent set; extra
locks must succeed before publication. Leases remain held through commit or
rollback. Constraint actions delegate child changes without granting the caller
child data access. Publication checks definitions and live caller/job state on
the writer connection, including late allocations after catalog hooks.

Parent-key UPDATE now acquires dependent file allocations and stable read sets
for the first matching dependent per parent row. SQL, SQL views, physical/keyed
files, logical files and retained cursors use the same check. NO ACTION/RESTRICT
deletes apply it throughout the planned cascade graph. Stable reads allow other
readers and the caller's own retained update read, but reject competing updates.
Unrelated parent-field updates do not acquire cascading child write locks;
validation requires neither DELETE nor dependent data authority. Physical keyed
DELETE now invokes the cascade planner at its actual deletion path.

SQL UPDATE/DELETE recheck every required root permission after hooks and before
publication, including READ required by predicates/expressions. Constant-only
mutations retain their existing narrower authority requirements. Parent checks
validate both member registry bindings and stable slot identities; a hook cannot
publish a changed dependent member under stale metadata.

The locking design follows the parent-validation and cascading-write distinction
in IBM's [referential integrity discussion](https://www.redbooks.ibm.com/redbooks/pdfs/sg244249.pdf),
section 3.5.1. Native child file allocations surviving until the parent file closes,
outgoing foreign-key parent reads, full native lock-error feedback and independent
current-release acceptance remain unfinished.

Full-suite stress also exposed connection/session shutdown ordering problems.
SQLite connection setup and memory keepalive creation now run outside the factory
lifecycle lock, with in-flight opens joined during disposal. Owned sessions cancel
their query resources before catalog access. Schema-writer regressions cover fresh
and initialized factories and session cleanup. See [query lifecycle](sql-queries.md).

CLRPFM rejects a nonempty parent even without dependent rows. CPYF/INZPFM report
CPF502D and retain atomic rollback; RPG WRITE and RESTRICT UPDATE report status
1022 and recover after valid I/O. RUNSQLSTM, QMQRY and compiled CL MONMSG share
the SQL diagnostics. HTTP and real terminal fixtures exercise child violations,
parent update rejection, CL/RPG recovery and cascading deletion. These follow
IBM's [referential integrity CL rules](https://www.ibm.com/docs/en/i/7.5.0?topic=constraints-referential-integrity-cl-commands)
and [copy-file error documentation](https://www.ibm.com/docs/en/i/7.6.0?topic=files-limitation-recoverable-errors-during-copy).
The RPG fixtures also exposed and fixed uppercase literal PREFIX support for
DISK and WORKSTN files; see [file definitions](rpg-file-definitions.md).

Work still required before this feature can receive complete acceptance:

- Finish restore/archive transitions and remaining native CRTDUPOBJ object/type
  selection, trigger/access-control and logical/save-file contracts. DATA/CST
  choices and native defaults are now covered; see [duplication](object-duplication.md).
  Move, file/library rename and parent duplication now attempt
  automatic establishment. Removing parent/dependent members leaves defined
  relationships; replacement members, parent creation and ALTER establish them.
  CHGPFCST can reverify pending relationships.
- Finish constraint authority delegation and indirect publication in private
  record units; current private-workspace coverage enlists both files as an
  administrator. Native file/cursor publication-authority coverage remains open.
- Complete outgoing child-key parent read allocations, native implicit child
  allocation lifetimes through parent close, and full related-resource locking
  under native commitment/isolation rules.
- Finish remaining CPYF SQL-view/query sources, member creation in existing
  targets, native error-level/partial-copy and feedback contracts. CRTFILE(*YES)
  now creates native output without SQL constraints; see [file copy](file-copy.md).
- Verify old-database upgrades, restart, rollback, cancellation, competing writers
  and larger related datasets with independent fixtures.
- Finish incompatible attribute-change and member/restore transitions around the
  implemented defined/disabled/check-pending administration. Referenced type changes
  currently reject rather than publishing mismatched metadata. Native pending
  OPEN/feedback and full diagnostic/completion-message ABI need independent acceptance.
- Finish native journal requirements/cause indicators, full diagnostic/feedback
  ABI, commitment/isolation timing, delete-path restrictions and current-release
  native golden/scale acceptance.
- Run final locked restore, full solution tests, both PTY geometries and the
  inventory/link/input checks against the completed implementation.

CHGPFCST now supports CHECK and referential constraints, FILE/CST positional or
keyword arguments, CST(*ALL/*CHKPND or up to 300 names),
STATE(*SAME/*ENABLED/*DISABLED), and CHECK(*YES/*NO). Management or alter authority
on the child file and library execute authority suffice; validation delegates
parent reads without granting parent data rights. Each constraint commits
separately, and a failed name does not undo preceding successes or prevent later
names from being processed. Defined relationships may be established by an enable
request after their parent and members become compatible. See IBM's [command reference](https://www.ibm.com/docs/en/i/7.5.0?topic=c-change-physical-file-constraint).

Disabled relationships retain their access paths and permit ordinary I/O without
that constraint's enforcement. Established disabled relationships are always
pending until verification. Enabling verifies existing rows by default; invalid
rows commit a check-pending state with CPF32B2 diagnostics. CST(*CHKPND) can select
the relationship again after repair. CHECK(*NO) skips existing-row verification;
the caller is responsible for valid existing data, and subsequent writes enforce
enabled constraints. These
follow IBM's [enable/disable rules](https://www.ibm.com/docs/bg/i/7.4.0?topic=constraints-enabling-disabling-referential).

An enabled pending foreign constraint blocks dependent I/O and parent UPDATE /
DELETE, while allowing parent READ / INSERT. A pending CHECK blocks reads and
operations requiring a read, while enforcing new insert/update values. SQL, view,
logical and native data authorization share SQL0971/57011 restrictions. Metadata
administration remains available for repair. See IBM's [check-pending rules](https://www.ibm.com/docs/en/i/7.4.0?topic=table-check-pending)
and [SQL0971 reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/rzalapdf.pdf).

Metadata lookup and allocation inspection remain available while data I/O is
pending. Data-state checks are separate from object permission checks, so checking
a file name or its locks does not read records or wait behind a running data query.
Library-list file resolution uses metadata, including for management-only callers.
Journal recovery performs final file, journal and receiver validation on its
existing writer transaction after rebuilding triggers.

State changes acquire exclusive allocations for the related files, reject retained
queries/update opens, and recheck authority, ownership, definitions and member
identities after hooks. Allocations remain until commit or rollback. Trusted
member rewrites preserve old invalid CHECK rows only within a scope that restores
normal CHECK enforcement on success or failure. State flags are omitted from old
JSON when false, existing constructors/deconstruction remain available, and CHECK
program serialization excludes administration flags.

Tests live in `SqlForeignKeyTests.cs`, `SqlForeignLifecycleTests.cs`,
`SqlForeignMutationTests.cs`, `SqlForeignRelocationTests.cs`,
`SqlForeignAuthorityTests.cs`, `SqlForeignNativeTests.cs` and
`SqlForeignLockTests.cs`, `SqlForeignUpdateLockTests.cs` and
`SqlForeignUpdatePublicationTests.cs`, `SqlConstraintLimitTests.cs`,
`SqlConstraintAdministrationTests.cs`, `SqlConstraintStateLifecycleTests.cs`,
`SqlForeignEstablishmentTests.cs`, `SqlForeignEstablishmentPublicationTests.cs`,
`SqlForeignMemberRemovalTests.cs`, `SqlForeignMemberRemovalPublicationTests.cs`,
`MemberRemovalSelectionTests.cs` and `SqlForeignRelocationEstablishmentTests.cs`.
Existing CHECK, ALTER, compatibility and SQL/native regressions remain part of acceptance. Cancellation tests now run their controller
and synchronous worker on dedicated tasks so xUnit's continuation queue cannot
delay the requested cancellation until the query exhausts its work budget.

Defined relationships now establish automatically when a compatible parent is
created, either member is added, or ALTER supplies a matching parent column,
attribute or key. Both member creation orders and nondefault member names work,
including self references. Incompatible parents retain the defined state.
Establishment preserves enabled/disabled state; unmatched rows become pending,
and established disabled relationships require later verification. This follows
IBM's [referential CL lifecycle rules](https://www.ibm.com/docs/en/i/7.5.0?topic=constraints-referential-integrity-cl-commands)
and [database programming state definitions](https://www.ibm.com/docs/en/ssw_ibm_i_74/pdf/rbafopdf.pdf).

Automatic establishment uses the initiating writer transaction and delegates
dependent changes without requiring child data or management authority. Related
allocations are checked before schema DDL, then exclusive leases stay through
commit/rollback. Publication rechecks initiating authority, related definitions,
owners, member registries and slot identities, compiles the new physical write
paths, and rechecks enforced rows after hooks. Explicit CHGPFCST verification also
rechecks rows before publication, preventing hook-introduced invalid data from
being accepted after the initial scan. CHECK(*NO) still skips verification.

Removing the sole physical parent or dependent member changes established
relationships to defined while preserving file-level constraints and state
history. Parent removal retains child records and access paths without executing
delete actions. Replacement members automatically reestablish relationships using
their actual names, making unmatched surviving data pending. These transitions
follow IBM's [referential CL lifecycle rules](https://www.ibm.com/docs/en/i/7.5.0?topic=constraints-referential-integrity-cl-commands).

RMVM requires file *OBJEXIST and *OBJOPR plus library execute, following IBM's
[file-command authority table](https://www.ibm.com/docs/en/i/7.5.0?topic=commands-file).
The shared catalog operation delegates related changes without requiring related
data/management rights. Allocation preflight precedes schema probes; exclusive
leases span commit/rollback, enlisted resources reject removal, and publication
checks root authority, live caller/job, definitions, owners and member/slot
identities. Surviving indexes, RRNs, incarnations, views, unaffected/self
references and application/journal triggers remain intact. Copying emits no
application/journal/version side effects and preserves pending CHECK rows with
normal enforcement restored. Unregistered SQL dependents and logical member
bindings still prevent removal.

FILE and MBR are required keyword or positional RMVM arguments. MBR accepts a
name, a nonempty prefix followed by * or *ALL, following IBM's
[RMVM command reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/cl/rmvm.htm).
Unsupported special values and malformed selectors reject before mutation.
Selection snapshots names and commits each successful removal separately; a
blocked member retains its diagnostic while later selected members are attempted.
Partial failure reports aggregate CPF7301, which compiled CL can monitor. The
complete native diagnostic/completion-message ABI remains an acceptance task.

Move, file/library rename and parent duplication now attempt to establish defined
relationships at the resulting parent identity, following IBM's
[referential CL lifecycle rules](https://www.ibm.com/docs/en/i/7.5.0?topic=constraints-referential-integrity-cl-commands).
Existing established relationships keep following relocated objects. Actual member
names bind, valid data enforces immediately, mismatched rows become pending and
disabled relationships remain disabled/pending. Incompatible or zero-member
parents retain the defined state. Native CRTDUPOBJ now supports DATA/CST choices,
defaulting to no records and copied constraints; the generic catalog copy API
retains its record-copy default. See [duplication](object-duplication.md) for
implemented options, pending state and remaining native command acceptance.

The shared catalog writer preflights related physical/logical allocations before
schema probes and rejects enlisted resources. Exclusive leases span publication
or rollback. Expected definitions, owners and member/slot identities are projected
from the pre-operation state; copy plans retain their exact prepared constraint
names/bindings. Publication rechecks those snapshots, required root and destination
library authority, live caller/job and enforced rows. Constraint establishment
delegates related work without requiring related data/management rights. Late
hook changes to identity, permissions, job state or orphan data roll back atomically.

Establishment and RMVM now share a member rewrite that retains indexes, RRNs,
incarnations, views, CHECK state, self/external references and application/journal
hooks without copying side effects. Catalog hooks can reference the child while
its same-name schema is replaced. The final rename temporarily uses SQLite's
[documented legacy rename mode](https://www.sqlite.org/pragma.html#pragma_legacy_alter_table)
to leave external references unchanged, preserves self references explicitly and
restores the caller's rename/CHECK/foreign settings on success or failure. New
partial SET NULL hooks refer to the newly established parent. Subsequent ordinary
renames still propagate through catalog trigger bodies.

Development verification (2026-10-20): all 7745 solution tests and 482 focused
checks pass with zero failures/skips, locked restore, a zero-warning/error Release
build and 1338 unchanged source/build/test inputs. This continuation adds 32
cases; the foreign-key/constraint phase now has 236 cases, alongside the prior
member-selection, PREFIX, shutdown and initialization-recovery regressions.
Complete PTY passes at 24x80 and 27x132, including automatic rename/move/duplicate
establishment, member replacement, pending repair and CL/RPG cascade flows.
The full suite uses four xUnit workers and completes in 12:15.24. Generated inventory,
local links and whitespace pass. Remaining native referential-integrity and broad
C07/C08/C09 acceptance stay open.


Native CRTDUPOBJ now uses DATA(*NO) / CST(*YES) defaults. Empty child copies
validate empty destination data when rebinding established relationships; zero-member
child definitions remain defined until member addition. CST(*NO) omits outgoing
foreign constraints and referenceable primary/unique keys, so copying a parent
without keys leaves its defined dependents awaiting a compatible key. Pending
CHECK state/status survives both empty and record copies; trusted transfer of old
rows does not relax subsequent I/O. Source USE plus OBJMGT is checked before
copying and on the publishing writer. Live cancellation interrupts writer waits
and row transfer before the final publication check.

Duplication development verification (2026-10-20): 35 new cases, all 500 focused
checks and all 7780 solution tests pass with zero failures/skips (12:24.78, four
workers). Locked restore, zero-warning/error build, 1341 unchanged inputs and
full 24x80/27x132 PTY pass, including CRTDUPOBJ-DATA-CST. Earlier checkpoint
counts above remain historical. C07/C08/C09 and native acceptance remain partial.


CPYF CRTFILE(*YES) now creates native physical/source targets without SQL
constraints, SQL defaults, application triggers or source journal bindings.
Copying a parent this way leaves defined dependents awaiting a referenceable SQL
key. Shared writer-side checks cover identities, exact definitions, copied
authorities and live caller/job state. The new target's exclusive file allocation
spans DDL/loading/publication without record-lock reentry under the schema lock.
Duplication and ordinary pending-state access remain separately enforced.

Creation verification (2026-10-20): 37 new cases, 515 focused checks and a final
37-case run pass; all 7817 solution tests pass with zero failures/skips (12:12.50).
Locked restore, zero-warning/error build, 1342 unchanged full-suite inputs, a separately frozen PTY script and full PTY
at 24x80/27x132 with CPYF-CRTFILE pass. Native partial-copy/feedback, SQL-view/member
and broader referential acceptance remain open. Earlier counts are historical.
