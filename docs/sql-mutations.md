# Searched SQL UPDATE and DELETE

`RUNSQL` supports bounded searched `UPDATE` and `DELETE` against single-format
physical files, including [SQL-created tables](sql-tables.md) and job-private
QTEMP tables. Both use the same member, record slots, authority checks and journal
hooks as CL and RPG. Explicit `COMMIT(*NONE)` or `*NC` is required.
[RUNSQLSTM scripts](sql-scripts.md) and [interactive STRSQL](sql-interactive.md)
use these same mutations. [SQL views](sql-views.md) now support single-source PF
write paths, direct-column mappings and LOCAL/CASCADED CHECK OPTION through this
transaction, allocation and journal machinery.

```cl
RUNSQL SQL('UPDATE QGPL/ACCOUNTS A SET AMOUNT=A.AMOUNT+1.25, NAME=''changed'' WHERE A.ID BETWEEN 7 AND 9') COMMIT(*NONE)
RUNSQL SQL('DELETE FROM QGPL/ACCOUNTS WHERE ID IN(7,9) AND AMOUNT IS NOT NULL') COMMIT(*NONE)
```

The parser binds names and types before scanning. A table correlation name may be
introduced with or without `AS`; it replaces the original table qualifier. SET
columns must be distinct. Assignments use original row values, including when
swapping columns or changing a search key. Every selected row is changed once.
An omitted WHERE selects all existing rows. The affected count includes selected
rows whose assigned values equal their old values.

Supported expressions include column references, numeric/string/NULL literals,
current datetime values, assignment DEFAULT, parentheses, unary signs, `+`, `-`,
`*`, `/`, character concatenation `||`, and short-circuit `COALESCE`. Predicates
include comparisons, `IS [NOT] NULL`, `[NOT] IN`, `[NOT] BETWEEN`, `NOT`, `AND`
and `OR`, with SQL precedence and three-valued NULL behavior. NULL predicates do
not select rows. General implicit string/numeric arithmetic conversions, CASE,
subqueries, row constructors, special registers and wider functions remain open.
LIKE, casts and the bounded scalar functions described by the shared
[query binder](sql-queries.md) are now available to mutations too; unsupported
combinations reject explicitly.

Integer arithmetic checks the 32/64-bit result range and discards division
remainders. Decimal intermediates use scaled arbitrary-precision integers,
including values beyond System.Decimal. Precision and scale follow the default
IBM i decimal arithmetic rules, with maximum precision 31 (63 for wider
operands), maximum scale 31 and minimum divide scale zero. Integer constants
retain their digit count, including leading zeros, when mixed with decimal.
Floating-point arithmetic uses checked finite doubles. Exact numeric assignment
truncates fractional digits toward zero and rejects whole-part overflow. Stored
fields retain the existing 29-digit/28-scale decimal codec limit. DECRESULT
options, floating underflow details and SQLCA warning delivery remain open.
These rules are documented in IBM's [SQL reference, arithmetic and assignments](https://www.ibm.com/docs/en/ssw_ibm_i_74/pdf/rbafzpdf.pdf),
pages 85 and 149–150.

Character comparisons use encoded, space-padded keys in the column CCSID.
Fixed CHAR operands contribute their declared padding to concatenation. Different
column CCSIDs currently reject; literal conversion uses the selected column's
CCSID. Datetime comparison uses validated canonical forms. Job time zones,
non-default collations, complete conversion rules and exact native diagnostic
replacement data remain open.

UPDATE requires object-operation/update authority; DELETE requires
object-operation/delete. Column references in a predicate or assignment also
require read authority. Literal/default assignments and unconditional DELETE do
not add a read requirement. Live job ownership, commitment eligibility and
ordinary file allocations are enforced. See IBM's [UPDATE](https://www.ibm.com/docs/en/i/7.5.0?topic=statements-update)
and [DELETE](https://www.ibm.com/docs/en/i/7.4.0?topic=statements-delete) references.

One writer transaction covers selection, assignment staging, publication, record
versions and journal capture. Staging is limited to one million cells (including
RRNs) and 64 MiB of normalized values. Predicates currently scan the member;
index-driven selection and independently measured scale budgets remain open.
Every assignment is computed before publication. A later value, constraint,
trigger, cancellation or journal failure rolls the entire operation back.
UPDATE checks managed unique indexes after all selected rows have changed, so
valid key shifts and swaps succeed. Those indexes are restored transactionally;
failed validation restores the original indexes and rows. Updating a referenced
unique key rejects before index changes pending native referential-constraint
support. Public foreign-key and trigger DDL, dependent-record lock policy and
complete native constraint timing remain open. Trusted internal SQLite trigger
and foreign-key behavior does not establish DB2 compatibility.

Selected records are protected by one compact record-set holder in the separate
coordination database. Normal record readers/writers, file allocations, earlier
waiters and independent update-read cursors obey the existing lock matrix.
Unselected records do not conflict with that record set. Leases remain held
through commit or rollback. Disposal, job completion and recovery remove both
holder and membership rows. Cancellation requests retain active leases until
execution has unwound. The lock coordination format upgrades transactionally
from version 1 to 2; the main catalog remains schema 35. WRKOBJLCK labels these
holders as selected records. Native row-lock escalation/wait policies remain open.

DELETE preserves the allocated RRN extent and removes the selected incarnations.
Later INSERT follows REUSEDLT; it can reuse a deleted slot. Native unconditional
DELETE optimizations and storage accounting remain unverified. Statement
expressions are limited to 128 levels and 8192 parser nodes; pathological chains
reject before recursive evaluation. Input SQL is never forwarded to SQLite, and
no per-statement SQLite functions or temporary tables survive pooled connections.

MERGE, positioned mutations, DDS logical-file view writes, INSERT SELECT,
complete embedded native SQL and commitment/isolation,
SQLSTATE/SQLCA/catalog ABI and independent IBM golden acceptance remain open.
The shared typed [SELECT service](sql-queries.md) now provides bounded result sets.
The earlier INSERT profile deliberately rolls back an entire VALUES list on
failure; IBM documents exceptions to INSERT atomicity and per-row unique checks
under COMMIT(*NONE). Exact native no-commit INSERT partial-failure behavior is
therefore also still open. C07/C08 and the broader SQL requirements remain partial.

Verification (2026-10-19): all 6949 solution tests pass with zero failures/skips
in 8m07s, including 51 added cases. All 347 focused checks and 4354 broad
RPG/binding/work cases pass. Locked restore, zero-warning/error Release build,
HTTP and compiled CL/RPG acceptance at both terminal sizes, frozen-source checks,
generated inventory, local links and whitespace checks pass.
