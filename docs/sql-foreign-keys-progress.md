# SQL referential integrity: work in progress

C08 referential integrity is being implemented. This is a development checkpoint;
the foreign-key work and the broader C07/C08 requirements remain unfinished.
The latest full regression and terminal verification for this work in progress
passes 7,659 solution tests. Native referential-integrity acceptance remains open.

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

- Finish automatic member removal, move/rename, duplicate-parent and restore/archive
  transitions. Compatible parent creation, member addition and parent ALTER now
  establish defined relationships. CHGPFCST can reverify pending relationships.
- Finish constraint authority delegation and indirect publication in private
  record units; current private-workspace coverage enlists both files as an
  administrator. Native file/cursor publication-authority coverage remains open.
- Complete outgoing child-key parent read allocations, native implicit child
  allocation lifetimes through parent close, and full related-resource locking
  under native commitment/isolation rules.
- Implement CPYF CRTFILE(*YES), ensuring that newly created files omit constraints;
  finish the remaining native error-level/partial-copy and feedback contracts.
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
`SqlForeignEstablishmentTests.cs` and `SqlForeignEstablishmentPublicationTests.cs`.
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

Development verification (2026-10-19): since SQL CHECK, 165 foreign-key/constraint
cases, eight PREFIX cases, three factory/session shutdown cases and two job-owned
initialization-recovery cases are added. This continuation adds twenty cases.
All 7659 solution tests and 834 focused checks pass with zero failures/skips,
locked restore, a warning-free Release build and 1077 unchanged source/build/cache
inputs. Complete display PTY passes at 24x80 and 27x132, including automatic parent
establishment, CHGPFCST pending/repair/enable and SQL-FK-CL-RPG-CASCADE. Generated
inventory, local links and whitespace pass. The full suite uses four xUnit workers
and completes in 11:36.04. Native referential-integrity acceptance and broader
C07/C08/C09 requirements remain open.
