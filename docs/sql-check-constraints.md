# SQL CHECK constraints

CREATE TABLE now accepts named or anonymous column and table CHECK constraints.
ALTER TABLE supports ADD CHECK, ADD CONSTRAINT … CHECK, DROP CHECK and DROP
CONSTRAINT. SQL scripts, saved queries, STRSQL and authenticated HTTP share the
same implementation.

```sql
CREATE TABLE QGPL/ORDERS(
  ID INTEGER NOT NULL,
  QUANTITY INTEGER CHECK(QUANTITY>0),
  PRICE DECIMAL(20,2),
  CONSTRAINT TOTAL CHECK(QUANTITY*PRICE<1000000)
)
ALTER TABLE QGPL/ORDERS ADD CONSTRAINT IDRULE CHECK(ID>0)
ALTER TABLE QGPL/ORDERS DROP CHECK IDRULE
```

A row satisfies a CHECK when its predicate is TRUE or UNKNOWN. FALSE rejects the
write with SQL0545 / SQLSTATE 23513. This differs from a view CHECK OPTION, which
requires TRUE and reports SQL0161 / 44000. Column checks can reference only their
declaring column. Table checks can reference columns of their own table; newly
added ALTER checks require unqualified column references. Subqueries, aggregates,
parameters, special registers and unsupported functions reject before publication.
Invalid definitions report SQL0546 / 42621. See IBM's
[CREATE TABLE](https://www.ibm.com/docs/en/i/7.6.0?topic=statements-create-table),
[ALTER TABLE](https://www.ibm.com/docs/en/i/7.6.0?topic=statements-alter-table) and
[constraint rules](https://www.ibm.com/docs/en/i/7.4.0?topic=objects-constraints).

Checks use the existing typed SQL expression engine, including exact decimal
arithmetic, three-valued predicates, padded character comparison, column CCSIDs,
casts and supported deterministic scalar functions. Decimal inputs do not pass
through SQLite REAL. UTF-8 character operations use the same rules as SELECT and
searched mutations. Expression evaluation errors preserve their SQL diagnostics.

The shared `Ipc.Sql` assembly contains database definitions, SQL models/parsing,
typed expressions and record-value conversion below the services layer. The
original public database/SQL namespaces remain available, with assembly type
forwarders and compatibility constructors for changed public records. Source files
remain linked from their established locations. Every normal and private workspace
connection registers the same versioned CHECK evaluator, even before a file store
is constructed. Enforcement therefore covers physical/logical record writes,
retained cursor updates, RPG, copies, query-output files and record-unit publication.

Generated member CHECK clauses hold a bounded, versioned predicate description and
only the referenced typed columns. Row values travel in chunks of at most 64
columns, avoiding SQLite's per-function argument limit. Evaluator caches hold at
most 16 compiled descriptions and two million description characters per
connection. Programs and row frames each have a two-million-character limit;
existing expression depth/node and record-layout bounds remain. Input SQL is never
executed as internal catalog SQL.

Adding a CHECK validates existing rows in every member within the ALTER transaction.
Failure restores definitions, rows, slots, indexes and dependent formats. Type and
null/default changes rebind checks and evaluate normalized existing values. Column
drops remove checks whose referenced columns are all dropped; partially affected
checks require CASCADE. Anonymous constraints receive schema-unique system names,
and check/primary/unique names share one catalog namespace. Object copies rename
conflicting check names in both metadata and member schema. Rename/move/copy and
restart retain predicate meaning, including original creation qualifiers.

PROCESS(*SYN) checks grammar; PROCESS(*VLD) validates definitions and dependencies
without evaluating existing rows or running publication hooks. Adding a check
requires ALTER and schema use, without granting data READ. Live authority,
credentials, jobs, intended metadata and member identities retain the shared
publication checks. Enlisted resources reject ALTER; ordinary writes inside a
record unit enforce the same predicates before commit.

Failed atomic SQL mutations, CPYF and initialization do not leave partial records,
slot growth or journal entries. Query-output append retains its documented ordered
prefix policy: accepted earlier rows remain when a later row fails. Native CPYF
and INZPFM report CPF502F, matching IBM's [check violation message](https://www.ibm.com/docs/en/i/7.5.0?topic=c-commit).
RPG file errors retain the existing E/MONITOR/INFSR handling and the generic I/O
status for a check failure, followed by normal status after a successful write.
Full native replacement data and diagnostic/feedback ABI remain open.

CHGPFCST can disable and enable named CHECK constraints or select *ALL / *CHKPND.
Enabling checks existing rows by default; mismatches leave the enabled constraint
pending. Reads then report SQL0971/57011, while new writes must satisfy the CHECK.
Disable the constraint, repair the data, then enable it again. Disabling preserves
pending selection history. CHECK(*NO) skips verification of existing rows, whose
validity is the caller's responsibility. New writes still enforce the constraint.
The command processes names sequentially and retains successful changes when
another name fails. Management or alter authority suffices, and retained query or
update-open allocations block the state change. See [constraint states](sql-foreign-keys-progress.md)
and IBM's [CHGPFCST reference](https://www.ibm.com/docs/en/i/7.5.0?topic=c-change-physical-file-constraint).

Foreign keys and their state/copy transitions are being implemented separately;
see [referential progress](sql-foreign-keys-progress.md). Public trigger DDL,
native ON INSERT/UPDATE VIOLATION actions, generated/LOB/distinct/temporal/partition
columns, wider expression/type/name/layout options, complete native constraint
timing/commitment/isolation, remaining member/attribute and archive lifecycle,
native diagnostic/feedback ABI and independent current-release golden/scale
acceptance remain unfinished. C07/C08 stay partial. The main catalog schema and
coordination format are unchanged; older payloads omit state flags when false.

Verification (2026-10-19): all 7481 solution tests and 1428 focused checks
pass with zero failures/skips, locked restore, a warning-free Release build,
1045 frozen source/build/cache inputs and full PTY CHECK/CL/RPG failure/recovery
at 24x80 and 27x132. Generated inventory, local links and whitespace pass. The
full suite uses four xUnit workers and completes in 10:51.13, retaining the
8203-row output and 20000-row native record allocation cases.


State administration development verification (2026-10-19): all 7639 solution
tests and 806 focused checks pass, with locked restore, a zero-warning/error build,
1074 frozen inputs and the full display PTY at both sizes. The terminal workflow
includes CHGPFCST disable, invalid enable, pending rejection, repair and enable.
The full run uses four xUnit workers and completes in 11:18.92; native acceptance
and the remaining lifecycle/feedback contracts above stay open.

Verification now checks existing CHECK and foreign-key rows again after schema
publication hooks, before committing an enable. Invalid late rows roll back the
state change with CPF32B2. CHECK(*NO) still skips existing-row verification.
Automatic-establishment development verification (2026-10-19): all 7659 solution
tests and 834 focused checks pass, with locked restore, a zero-warning/error build,
1077 frozen inputs and full PTY at both sizes. The full suite completes in 11:36.04.

Referential parent member removal preserves pending CHECK rows while rewriting
the surviving child's schema, without firing application/journal triggers or
changing record versions. Normal CHECK enforcement returns before publication.
Member-removal development verification (2026-10-20): all 7713 solution tests and
359 focused checks pass, with locked restore, a zero-warning/error build, 1335
unchanged source/build/test inputs and full PTY at both sizes (11:47.83 full suite).
The remaining native acceptance contracts stay open.

Relocation establishment and RMVM now share the same state-preserving member
rewrite. Existing pending CHECK rows survive copying, normal CHECK enforcement
returns before publication and catalog hooks referencing the replaced child stay
valid. Relocation development verification (2026-10-20): all 7745 solution tests,
482 focused checks, locked restore, a zero-warning/error build, 1338 unchanged
inputs and full PTY at both sizes pass (12:15.24 full suite). Native acceptance
contracts above remain open.

Native [CRTDUPOBJ DATA/CST](object-duplication.md) now defaults to an empty copy
with constraints. CST(*NO) omits registered SQL constraints and their managed
paths; explicit NOT NULL/default column attributes and native DDS UNIQUE remain.
CST(*YES) preserves CHECK state/status even for DATA(*NO). Pending old rows transfer
under a temporary CHECK override, which is restored before ordinary I/O; failed
copies roll back all schema/data/catalog changes. Source authority is rechecked
without requiring ordinary data access to a pending source. The shared API keeps
record-copy defaults. Duplication verification (2026-10-20): 35 new cases, 500
focused checks, all 7780 solution tests (12:24.78), zero-warning build, 1341
unchanged inputs and full PTY at both sizes pass. Broader native acceptance remains
open.
