# SQL table alteration

`ALTER TABLE` now changes SQL-created physical files through RUNSQL, RUNSQLSTM,
STRSQL, saved QMQRY and authenticated HTTP. The same catalog definitions, member
tables and external record layouts continue to serve SQL, CL and RPG.

```sql
ALTER TABLE QGPL/ACCOUNTS
  ADD FLAG INTEGER NOT NULL DEFAULT 5
  ADD NOTE VARCHAR(30) BEFORE AMOUNT
  ALTER AMOUNT SET DATA TYPE DECIMAL(20,2)
  ALTER NOTE SET DEFAULT 'new'
ALTER TABLE QGPL/ACCOUNTS ADD CONSTRAINT ACCOUNTKEY PRIMARY KEY(ID)
ALTER TABLE QGPL/ACCOUNTS DROP COLUMN NOTE RESTRICT
ALTER TABLE QGPL/ACCOUNTS DROP PRIMARY KEY
```

The implemented actions are ADD [COLUMN], optional BEFORE, DROP [COLUMN] with
CASCADE or RESTRICT, ALTER [COLUMN] SET DATA TYPE, SET DEFAULT, DROP DEFAULT,
SET NOT NULL and DROP NOT NULL, and ADD/DROP primary or unique constraints.
Multiple actions use IBM i's consecutive-clause syntax. ADD supports the existing
SQL column types, CCSIDs, null/default clauses and inline primary/unique keys.
The existing name, record-length and supported type bounds still apply.

Changes bind against the live definition after obtaining the catalog writer.
Repeated attributes, conflicting column actions, incompatible type families,
missing columns, duplicate key signatures and schema constraint names reject.
Numeric types can change within the numeric family; character types can change
length/CCSID; datetime columns retain their type. Character shrinkage truncates at
encoded character boundaries. Numeric conversion retains exact decimal values and
truncates fractional digits toward zero; overflow rejects the whole statement.
Existing defaults must remain assignable to their new column types.

New columns fill existing records with their defaults. Nullable additions default
to NULL; nonnullable numeric/string additions without an explicit value use their
native type defaults. Implicit nonnullable date/time/timestamp defaults use the
historical 0001-01-01/midnight values for existing rows and current datetime for
future inserts. An explicitly supplied CURRENT datetime default uses one statement
clock across existing rows and members. NOT NULL checks existing records; DROP
DEFAULT requires a nullable column, and a primary-key column cannot drop NOT NULL.

Adding a column preserves frozen SQL-view wildcard layouts and existing SQL-index
record projections. Type changes rebind dependent view expressions and external
formats in source order, including nested views, and rebuild exact index keys.
Shared DDS logical formats follow supported base-format changes; other logical
projections retain their selected columns. A dropped column used by a view or
index rejects under RESTRICT; CASCADE drops that object and its dependent views.
CASCADE is the default. Constraints wholly covered by dropped columns are removed
under either mode; a partially affected key requires CASCADE. Unrelated views and
indexes remain usable. See IBM's [ALTER TABLE reference](https://www.ibm.com/docs/en/i/7.6.0?topic=statements-alter-table)
and [SQL reference, cascaded effects](https://www.ibm.com/docs/en/ssw_ibm_i_74/pdf/rbafzpdf.pdf).

The caller needs ALTER (*OBJALTER) and schema use authority. Data READ/ADD/UPDATE
rights are not required to perform the schema change. Ownership, creation time,
private/public/authorization-list grants and original source image are retained;
changed definitions invalidate object signatures. Publication rechecks the live
caller, job, ALTER authority, intended definition, owner and member identities on
the writer connection. A later failure rolls back metadata, dependency deletion,
data conversion, indexes and triggers together. The authority mapping follows
IBM's [table privilege reference](https://www.ibm.com/docs/ssw_ibm_i_74/db2/rbafzgnt.htm).

Member rewrites stream records through private staging tables in the same
transaction, retaining physical RRNs, sparse/deleted extents, member identities,
capacity and record incarnations. The staging table and its changes disappear on
rollback. All members receive one resulting layout; multi-member key constraints
reject until file-wide uniqueness is implemented. Persistent allocations on the
table or its logical descendants block alteration. Enlisted record resources
reject with IPC0211; there is no implicit boundary on the caller's record unit.
Cancellation and job/factory cleanup use the shared SQL writer lifetime.

Existing journal records remain intact. Rewriting old records does not emit
false PT/UB/UP/DL entries. Reinstalled capture hooks use the new schema for later
writes. Compatible trusted application hooks are recreated after loading and
compiled without executing them. Unregistered physical constraints, external SQL
relationships and column drops with unregistered application hooks reject before
rewrite; native trigger/foreign-key dependency handling remains unfinished.

PROCESS(*SYN) checks grammar only. PROCESS(*VLD) binds proposed columns, defaults,
constraints and dependent view/index layouts without rewriting data or running
catalog hooks. Actual conversion, existing-row constraint violations and other
runtime publication conditions remain execution checks. SQL0190/42837 describes
incompatible attributes and SQL0478/42893 describes restricted dependencies; full
native message replacement data and warning ABI remain open.

Remaining scope includes altering DDS-created/source files, foreign constraints, wider CHECK actions and triggers, generated/identity/hidden/LOB/distinct/temporal/partition
columns, wider type/name/layout options, native format-ID and DDL journal events,
native commitment/isolation and partial-failure policies, archive lifecycle, and
independent current-release native golden and scale acceptance. C07/C08 remain
partial. The main catalog schema and coordination format are unchanged.

Verification (2026-10-19): all 7449 solution tests and 1396 focused checks
pass with zero failures/skips, locked restore, a warning-free Release build,
1031 frozen source/build/cache files, full PTY/ALTER TABLE/CL/RPG acceptance at
24x80 and 27x132, generated inventory, local links and whitespace. The full suite
uses four xUnit workers and completes in 10:24.39. The existing 8203-row output and
20000-row native record allocation cases remain in full acceptance.
