# SQL views

`CREATE VIEW`, `CREATE OR REPLACE VIEW` and `DROP VIEW` now use the shared SQL
parser, typed SELECT engine and transactional object catalog. RUNSQL with
COMMIT(*NONE), RUNSQLSTM, STRSQL, saved QMQRY, query output and authenticated HTTP
commands share this implementation. A view is an ordinary nonkeyed `*FILE`/`*LF`
with an external record format, logical member and registered source dependencies.
Its rows are produced by its stored query; no separate answer table is created.

```sql
CREATE VIEW QGPL/ACTIVE AS
  SELECT ID,CODE,AMOUNT FROM QGPL/ACCOUNTS WHERE AMOUNT>0
CREATE VIEW QGPL/TOTALS(N,TOTAL) AS
  SELECT COUNT(*),SUM(AMOUNT) FROM QGPL/ACCOUNTS
CREATE OR REPLACE VIEW QGPL/ACTIVE AS
  SELECT ID,CODE,AMOUNT FROM QGPL/ACCOUNTS WHERE AMOUNT>10
DROP VIEW QGPL/ACTIVE
```

Views support the current SELECT grammar, including projections, expressions,
inner/outer joins, predicates, grouping, aggregates and DISTINCT. Nested views
use the same exact decimal, null, padding and CCSID rules. The current statement
clock and evaluation budget are shared across nested plans. Warnings from inner
views reach callers and survive EOF. Wider exact decimal results remain typed
SQL results instead of passing through floating-point or System.Decimal storage.

Explicit column lists name result columns by position. Otherwise,
result names must be unique supported system names; expressions need aliases.
`RCDFMT name` overrides the default format name, which is the view's system name.
Unqualified system naming inherits the first source's schema when no default
collection is supplied. SQL naming uses its default schema/collection. Source
references bind to actual qualified objects at creation. Later library-list
changes do not redirect them. Wildcard columns are frozen at creation, including
when a source view later gains compatible columns. Copy/move/rename retain the
bound references and original query qualifiers. [ALTER TABLE](sql-alter-tables.md) now refreshes
dependent types, preserves frozen wildcard columns and implements supported
DROP COLUMN RESTRICT/CASCADE effects.

The current limits are 1,000 result columns, 32 references per SELECT, 256 expanded
references, 32 view nesting levels and 32,766 external record bytes. Query source,
compiled SQL, rows, cells, bytes and work retain the shared SELECT bounds. Native
parameter markers and outer ORDER BY/FETCH clauses are not accepted in view
definitions. Unsupported grammar rejects before publication. See IBM's
[CREATE VIEW reference](https://www.ibm.com/docs/en/i/7.4.0?topic=statements-create-view).

Creation checks schema create authority, CRTLF use/signature authority, target
data-dictionary CHANGE authority, source SELECT rights and OBJMGT on a sole base
table. Ownership/public authority use the shared [SQL object rules](sql-tables.md).
A matching SQL schema profile requires ADD authority from another creator and
must have the necessary source READ rights.

Readers require authority to the view. Expansion checks each view owner's READ
rights on direct and indirect sources, without logging in as the owner or
changing the reader's job/session/program identity. This permits view access
without granting the reader base-table access. Authority revocation, reader
credential/session changes, job termination and changed view ownership/definition
invalidate access, including cached results after EOF. An owner's disabled login
does not remove its object authorities. These checks model the view-owner source
authority rule in IBM's
[SQL reference, ownership section](https://www.ibm.com/docs/en/ssw_ibm_i_74/pdf/rbafzpdf.pdf).
Exact native grant-option, privilege-cascade and ownership-transfer behavior
still requires independent acceptance.

Creation validates the SELECT without executing its data query. Descriptor,
definition, member and dependencies publish together. Replacement preserves
existing granted/public/AUTL authority and validates dependent view shapes before
commit. Incompatible dependent layouts and circular references roll back the
whole replacement. DROP VIEW and SQL DROP TABLE preflight all affected authorities
and persistent allocations, then delete logical dependents in dependency order
in one transaction. A later failure restores the full graph. CL DLTF retains its
existing restrictive dependency behavior. Live credential/job publication hooks,
writer waits and cancellation are checked. Restart retains bound definitions.
QTEMP views remain private and permanent views cannot depend on private data.

SQL INSERT, UPDATE and DELETE now follow single-source view chains ending in a
single-format physical table. DISTINCT, joins, grouping and aggregate views reject
writes. Direct projected columns remain writable through aliases and nested
views; expression columns can be read in predicates but cannot be assigned.
Constant-only views support DELETE. INSERT needs a writable column, and an
explicit target list must exclude expression columns. Hidden columns and DEFAULT
use the base table's nullability, defaults, conversions and constraints.

UPDATE and DELETE select only rows visible through every underlying view filter.
INSERT can create an invisible row unless a CHECK OPTION rejects it. WITH CHECK
OPTION defaults to CASCADED. LOCAL checks the declaring view's predicate plus
inherited explicit checks; CASCADED also checks underlying view predicates.
UNKNOWN does not satisfy a check. Checks evaluate normalized proposed rows, so
column rounding and padding precede evaluation. Special-register-dependent check
predicates and nondeletable checked views reject creation. A failed check returns
SQL0161 / SQLSTATE 44000. See IBM's [INSERT reference](https://www.ibm.com/docs/en/i/7.4.0?topic=statements-insert).

The caller needs the operation's rights on the outer view, plus READ when the
statement references view columns. Each view owner needs READ on direct and
indirect sources, and the requested write right on its direct source. These
checks retain the caller's identity, job and journal attribution. The publication
connection rechecks authority, definitions, ownership, credentials and job state,
including changes made by transactional record hooks. Capacity recovery rechecks
the bound path without granting base-table access to a view-only writer.

View DML shares the existing SQL statement transaction, record allocations,
unique-index deferral, slot allocation and journal capture. A failing statement
rolls back all its changes. This is the project's current atomic SQL policy;
complete native COMMIT(*NONE) partial-prefix and isolation behavior remains open.
Enlisted base resources reject view DML with IPC0211. Job cleanup cancels and
joins a blocked SQL writer before publishing job completion. Native view cursor
updates and RRN operations remain separate unfinished work.

SQL queries through views can read the caller's pending record-unit data using
the shared private query snapshot. View creation/deletion reject enlisted base
resources. Native record input uses a separate bounded lazy disk scroll cache,
so a first read does not materialize all answer rows. Compiled CL DCLF/RCVF and
RPG nonkeyed READ consume its external records. The cache holds the view allocation
until close, validates access before retrieving cached data, supports forward/
reverse snapshot navigation and releases storage on close/cancellation/job end.
Positions are snapshot ordinals; physical RRN/keyed/update operations are not
implemented. Native view cursors reject open or late record-unit enlistment until
commit/rollback position semantics are implemented. Native record decimals retain
the existing 29-digit/28-scale codec limit; wider view results require CAST for
record input. SQL queries retain their wider exact types.

ALTER VIEW, INSTEAD OF triggers, complete updatable-column/default/grant-option
provenance, writes through DDS logical or source files, native record update/RRN/
key/commitment behavior, wider names/layouts, recursive/CTE/set-query grammar,
read triggers/row permissions, archive lifecycle, exact format-ID/diagnostic ABI,
independent current-release IBM golden cases and native scale budgets remain open.
These additions do not close C07/C08 or the broader SQL DDL requirement. The main
catalog schema and coordination format are unchanged.

Verification (2026-10-19): all 7406 solution tests and 1353 focused checks
pass with zero failures/skips, locked restore, a warning-free Release build,
1021 frozen source/build/cache files, full PTY/view DML/CHECK OPTION acceptance at
24x80 and 27x132, generated inventory, local links and whitespace. The full suite
uses four xUnit workers and completes in 10:24.47. The 20000-row native first-read
case retains its managed allocation bound below 4 MiB.
