# SQL indexes

`CREATE INDEX` and `DROP INDEX` now use the shared SQL parser, catalog and keyed
logical-file engine. They run through `RUNSQL COMMIT(*NONE)`, `RUNSQLSTM`, STRSQL
and saved QMQRY statements. Syntax and validation modes retain their existing
no-execution behavior. An index is an ordinary `*FILE` with attribute `*LF`, a
record format, one logical member, authorities and a dependency on its base PF.
CL and RPG keyed file operations use the maintained access path.

```sql
CREATE INDEX QGPL/BYAMOUNT ON QGPL/ACCOUNTS(AMOUNT DESC,ID ASC)
CREATE UNIQUE INDEX QGPL/BYID ON QGPL/ACCOUNTS(ID)
CREATE UNIQUE WHERE NOT NULL INDEX QGPL/BYNAME ON QGPL/ACCOUNTS(NAME)
CREATE INDEX QGPL/KEYSONLY ON QGPL/ACCOUNTS(ID) RCDFMT ACCOUNTKEY ADD KEYS ONLY
DROP INDEX QGPL/BYAMOUNT
```

Keys are existing columns, with ASC as the default. The parser accepts at most
120 distinct keys; their combined storage plus nullable-key bytes must fit
32,766 bytes. Exact decimals, character CCSIDs/padding, null ordering and key
direction use the existing database sort encoding. UNIQUE considers null keys
equal. UNIQUE WHERE NOT NULL permits repeated keys containing nulls. SQL and
record writes maintain the same constraints; searched UPDATE validates changed
unique keys at statement end, allowing swaps when the final result is unique.
See IBM's [CREATE INDEX reference](https://www.ibm.com/docs/en/i/7.4.0?topic=statements-create-index).

The default external record contains every base column and uses the index name
as its format name. `RCDFMT name` overrides that name; `ADD ALL COLUMNS`,
`ADD KEYS ONLY`, or `ADD column,...` selects the record layout. Explicit added
columns follow the keys. The implemented external record is limited to 1,024
columns. Long SQL names and mixed-case delimited identifiers still require the
remaining system-name mapping.

With system naming, an unqualified index name inherits its base table's library.
SQL naming uses the default schema/collection rules. Creation requires library
create authority, base INDEX privilege (OBJALTER), CRTLF use/signature authority,
and CHANGE authority to any target-schema data dictionary. INDEX privilege alone
does not grant base READ access. Ownership and public authority follow the shared
[SQL table rules](sql-tables.md): system naming uses the creator and library
CRTAUT; SQL naming uses the matching schema profile when present and public
EXCLUDE. QTEMP privacy and dependency restrictions apply.

Creation publishes descriptor, definition, member, dependency and access paths in
one transaction. Duplicate existing keys, metadata failures, cancellation or
invalid live credentials/jobs roll back publication. Writer acquisition is
cancellable. Creation/deletion reject a base enlisted in the caller's record
unit. Restart preserves the object and uniqueness. Shared unique paths remain
until their final logical owner is deleted. Nonunique paths are bounded caches
owned by the physical member and may remain after the logical object is deleted.
Each physical member retains the shared 64-index bound.

Nonunique indexes combine up to 256 current base members. Adding/removing a base
member updates their bindings and paths transactionally. SQL index members cannot
be added or removed separately. Unique indexes currently require at most one
base member; adding another member rejects before effects. Native partitioning,
multi-member uniqueness and exact traditional-I/O member rules remain open.

DROP INDEX requires object OPR and EXIST authority and released persistent
allocations. It currently accepts SQL-created indexes; DDS LF deletion remains
available through DLTF. SQL DROP TABLE now cascades its registered logical-file
dependents, preflighting all object authorities and allocations and rolling back
the entire cascade on failure. Other registered dependent kinds reject. CL DLTF
keeps its existing dependency checks. Generic object copy/move/rename/delete
preserve shared path ownership and base dependencies. Copying a PF does not copy
uniqueness owned only by its logical indexes. IBM's
[DROP reference](https://www.ibm.com/docs/en/i/7.4.0?topic=statements-drop)
describes native dependent-object deletion and authority.

Derived/expression keys, repeated key columns, WHERE filters, INCLUDE, encoded
vector indexes, partition clauses, AS/column transformations and wider naming/
record-layout options remain unimplemented and reject explicitly. These paths
provide keyed record I/O and uniqueness; the SELECT compiler's expression-based
queries do not establish native SQL optimizer/access-plan parity. Native
commitment/isolation, full SQLSTATE/message/format-ID fidelity, save/restore,
independent current-release IBM golden cases and scale budgets remain open.
C07/C08 and the broader DDL requirement remain partial. No catalog migration,
coordination-format change or command entry is required by this addition.

Verification (2026-10-19): all 7316 solution tests and 1107 focused checks
pass, zero failures/skips, with locked restore, a warning-free Release build,
1006 frozen source/build/cache files, full terminal acceptance and SQL index/RPG
checks at both geometries, inventory, local links and whitespace. The full suite
uses four xUnit workers and completes in 10:32.76.
