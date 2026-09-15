# Logical files — implementation in progress

CRTLF is being implemented as a service dependency of C07's open command-category
item and C08's open database items. It is still partial in the compatibility matrix.

The current simple-LF compiler reads native DDS columns (A in column 6, record/key/
select/omit level in column 17, names in 19–28, keywords from 45). It supports one
PFILE and one record format, inherited fields or a projection, ascending/DESCEND
keys, and ordered S/O rules with COMP, RANGE, VALUES, AND continuations, and final
ALL. The default selection action is opposite the final S/O rule. Unknown keywords,
field layout overrides, malformed predicates and currently unsupported LF forms
fail explicitly. Keyword statements continue on blank specification lines; trailing
`+` strips leading spaces on the next functions field and `-` preserves them.
Statements are bounded to 5000 characters and errors retain the originating line.
See IBM's [DDS keyword rules](https://www.ibm.com/docs/en/i/7.4.0?topic=terms-rules-dds-keywords-parameter-values).

References: IBM [PFILE](https://www.ibm.com/docs/en/i/7.5.0?topic=p-pfile),
[DESCEND](https://www.ibm.com/docs/en/i/7.5.0?topic=80-descend-descend-keyword-physical-logical-files),
and [physical member bindings](https://www.ibm.com/docs/en/i/7.5.0?topic=attributes-specifying-physical-file-data-members-dtambrs-parameter).

```text
CRTLF FILE(QGPL/ACTIVE) SRCFILE(QGPL/QDDSSRC) SRCMBR(ACTIVE) MAXMBRS(2)
DSPPFM FILE(QGPL/ACTIVE)
ADDLFM FILE(QGPL/ACTIVE) MBR(NEWBINDING)
RMVM FILE(QGPL/ACTIVE) MBR(NEWBINDING)
```

Creation snapshots the current physical members into one logical-member binding.
ADDLFM snapshots again. `DTAMBRS((PF (MBR1 MBR2)))` binds explicit members in the
specified order; `*CURRENT/PF` resolves against the compiled PFILE library.
`DTAMBRS((PF *NONE))` creates an empty binding. `MBR(*NONE)` creates the LF without
an initial member. CRTLF defaults MAXMBRS to 1; explicit `*NOMAX` uses the current
256-member bound. Adding
physical members later leaves existing LF bindings unchanged. Bound physical
members cannot be removed until their logical bindings are removed. Logical-member
removal preserves physical records. The current bounds are 256 logical members,
256 physical members per binding, 1024 projected fields and 256 selection rules.

Logical records are queried from physical member tables on each read; they are
not copied into another data table. Select/omit predicates and key prefixes use
bound SQL values. Exact fixed-width decimal byte keys avoid floating-point
conversion, and character keys use padded encoded CCSID bytes. Versioned,
deterministic SQLite functions are installed on every catalog connection.
Live LF and base-PF authority apply; row locks identify the actual physical member
and record. Copying or deleting a simple LF preserves the base data. Physical-file
rename/move updates compiled LF bindings in the same transaction as member-table
and catalog relocation. Existing unique index identities remain valid. Persistent
allocations on dependent LFs prevent relocation; command locks cover their metadata
updates. Original DDS remains available for inspection and deliberate recompilation.

Keyed LFs create maintained SQLite expression indexes, shared for identical
definitions, with a 64-index bound per physical member. Nonunique indexes remain
as a physical-member cache after LF deletion. File-level UNIQUE uses partial
indexes so only selected records participate. These constraints apply to PF writes
as well as LF writes. LF copies share constraints; removing the last owning LF or
member releases them. PF copies omit LF-owned constraints while retaining external
SQL indexes. UNIQUE currently requires at most one PF member per LF binding;
uniqueness across multiple physical members remains an open requirement.

Insert/update/delete through a simple LF change physical records. Insert requires
a single physical-member binding and a record satisfying the LF selection. Updates
and deletes require a complete LF key and affect only currently selected records;
updates can move a record out of the selection. CLRPFM remains a physical-file
operation. The record-number/ODP interfaces will refine mutation targeting in C08.

LF and independent byte-fixture tests cover live data, projections, selection ordering and AND/ALL,
large exact decimal values, multiple-member snapshots, physical row locks, base
permission revocation, object copy/delete, source-member CRTLF, writes, member
lifecycle, continuations, explicit DTAMBRS, uniqueness cleanup, query-plan index use,
precision/overflow and invalid constants. Real terminal acceptance compiles an LF,
displays physical records, adds/removes a member, and deletes the LF.
Broader C07/C08 completion is still open: joins,
multi-format/multi-PFILE definitions, native field transformations and collations,
multi-member unique paths, multi-file DTAMBRS, complete buffer codecs,
bounded ODP cursors, and job commitment control. These remain requirements.

Nullable key components sort above non-null values (last ascending, first descending),
including compound PF/LF keys and CPYF ordering. `UNIQUE`/`UNIQUE(*INCNULL)` considers
nulls equal for duplicate detection; `UNIQUE(*EXCNULL)` allows repeated keys containing
null. Separate null flags prevent collisions with actual zero or blank values.
Filtered uniqueness still applies only to selected records and survives LF copies
until the last owner is removed. Schema 18 upgrades existing paths transactionally;
see [catalog migrations](catalog-migrations.md). Tests cover compound order, prefix
reads, copying, nullable duplicates, failed creation and upgrade rollback.

References: IBM [UNIQUE null options](https://www.ibm.com/docs/en/i/7.5.0?topic=80-unique-unique-keyword-physical-logical-files)
and [Db2 for i SQL reference, CREATE INDEX and null ordering](https://www.ibm.com/docs/en/ssw_ibm_i_74/pdf/rbafzpdf.pdf).
