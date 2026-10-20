# Object duplication

Native CRTDUPOBJ supports a single named object and a supported object type. It
now defaults to DATA(*NO) and CST(*YES), following IBM's
[command reference](https://www.ibm.com/docs/en/i/7.5.0?topic=c-create-duplicate-object).
The command remains partial; unsupported native options and final acceptance are
listed below.

```text
CRTDUPOBJ OBJ(ORDERS) FROMLIB(APP) OBJTYPE(*FILE) TOLIB(TEST)
CRTDUPOBJ ORDERS APP *FILE TEST ORDCOPY DATA(*YES) CST(*NO)
```

The first example creates TEST/ORDERS with its members and constraints but no
records. The second copies records to TEST/ORDCOPY without the registered SQL
constraints. Existing scripts that need source records must request DATA(*YES).

The first five parameters may be positional: OBJ, FROMLIB, OBJTYPE, TOLIB and
NEWOBJ. TOLIB defaults to *FROMLIB; *SAME also selects the resolved source library,
and *CURLIB selects the job's current library (QGPL for *CRTDFT). NEWOBJ defaults
to *OBJ; *SAME also retains the source name. Library-list source resolution uses
the shared command service. DATA/CST accept *YES or *NO; DATA(*YES) requires
OBJTYPE(*FILE), otherwise CPF2116 is returned. Prompt metadata and compiled CL
use the same defaults and validation. Unmodified older built-in command metadata
upgrades on restart through the existing seed-fingerprint mechanism.

| Choice | Implemented behavior |
| --- | --- |
| DATA(*NO) | Copies definitions and member names, starts record extent at zero, and resets dynamic capacity grants. |
| DATA(*YES) | Copies member rows, sparse RRNs, deleted-slot extent and capacity grants; assigns fresh member and record identities. |
| CST(*YES) | Copies registered primary/unique, CHECK and foreign constraints, with destination bindings and independent constraint names. |
| CST(*NO) | Omits that constraint metadata, physical clauses and constraint-owned access paths, including paths inherited through repeated copies. |

Source-file text layout, sequence fields and member widths are retained. Explicit
column defaults and NOT NULL attributes remain with CST(*NO), as does a native
DDS UNIQUE file attribute. Removing a primary constraint also removes its managed
non-null check, while explicit column nullability is preserved.

Copied CHECK state and check-pending status survive either DATA choice, consistent
with IBM's [referential-integrity Redbook](https://www.redbooks.ibm.com/redbooks/pdfs/sg244249.pdf),
in its CRTDUPOBJ discussion. Old rows under a pending CHECK can be transferred without replaying
validation; ordinary enforcement is restored after the transfer and pending-state
record restrictions remain. Application triggers are copied after the old rows;
managed slot/journal/constraint hooks are regenerated. Journal bindings are not
cloned and the old-row copy does not replay application or journal side effects.

Foreign constraints remap self references and same-library parents into the
destination library. Missing or incompatible parents leave a defined relationship;
matching parents validate the copied rows. An empty copy validates empty data,
and a zero-member child retains defined constraints until member addition.
Copying a compatible keyed parent can establish existing defined dependents;
CST(*NO) leaves those dependents awaiting a suitable key. See
[referential integrity](sql-foreign-keys-progress.md).

Source USE (object operation and read) plus OBJMGT and destination-library use/add
authority are required. The writer rechecks authority after catalog hooks; a late
revocation rolls back the entire copy. The new owner comes from the live caller.
Pending-source metadata copying bypasses ordinary data-state gating only for the
trusted catalog operation; ordinary record access keeps its state checks.
Cancellation interrupts writer waits and physical copying and is checked before
commit. No partial object, member, access path or authority is published on failure.

The generic `ObjectCatalogOperations.Relocate(copy: true)` API retains its existing
record-copy default. Callers can explicitly choose `copyData`, `copyConstraints`
and a cancellation token. It applies the same source authority and publication
checks as the native command.

Generic or multiple OBJ/OBJTYPE selection, TRG, FILEID, ACCCTL, native logical-file
rebinding, save-file/ASP/group-owner/automatic-journal contracts and independent
native golden comparisons remain unfinished. This checkpoint does not complete
the command, C07/C08/C09 or the broader referential acceptance plan.

Development verification: 35 duplication cases and 500 focused checks cover
record/source files, sparse/deleted slots, capacity, repeated copies, pending
CHECK/foreign state, zero-member files, compiled CL, prompt/restart metadata,
triggers/journal isolation, live ownership, cancellation and authority revocation.
The latest full-suite/build/PTY evidence is in the
[completion checkpoint](completion-progress.md).
