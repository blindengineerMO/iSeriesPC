# Object catalog and persistence

Objects use `(library, name, type)` identity. File attributes such as `*PF`, `*SRCPF`,
`*DSPF` and `*PRTF` describe a `*FILE`; `*PNLGRP` is an object type. Descriptors persist
owner, creation/change timestamps, description, CCSID, attribute, format, source,
public authority and extended attributes. Private authorities remain type-qualified.
The in-memory store returns snapshots, rejects duplicate creation and rejects updates
to missing objects. SQLite updates likewise reject missing objects. Generic-name
search interprets a final `*` as a prefix wildcard; underscores remain literal.

## Transactional object operations

`ObjectCatalogOperations` supplies rename/move/copy/delete against the shared SQLite
catalog. Current program/descriptor, menu and PF/source-file payloads move with their
metadata and authorities. Member names are preserved when a file is copied or renamed;
the file definition's object name changes, while record-format names remain unchanged.
File creation, including the initial member/table, now uses one transaction. File
deletion removes tables, definitions, members, authorities and the descriptor together.
A failed create/copy/move rolls all catalog and table changes back.

Schema 5 records explicit type-qualified source→target dependencies. Both objects must
exist. Renames cascade registered references; incoming dependencies block deletion.
Deleting a source removes its outgoing edges. Dependencies must be registered by each
owning compiler/service as those integrations are completed; dynamic source references
are not inferred by scanning arbitrary text. Library renames move their contained
namespace/member tables in the same transaction, reject shipped system libraries,
and reject libraries referenced by active or queued jobs. Nonempty library deletion
is rejected; recursive operator workflows remain C07 work.

`CRTLIB`, `RNMOBJ`, `MOVOBJ`, `CRTDUPOBJ`, and `DLTOBJ` expose the implemented subset.
See the generated command catalog for accepted parameters. Existing target objects
are never overwritten. Member copies preserve constraints, explicit/implicit indexes,
generated columns, sparse record numbers, and AUTOINCREMENT high-water marks. SQLite
rewrites identifier references under a rolled-back savepoint; internal foreign keys
and trigger bodies target the copy, external references retain their original target,
and literals stay unchanged. Triggers are installed after loading to avoid copy-time
side effects. Deletion probes parsed schema dependencies, preventing cascades into
external tables or broken external views/triggers. See SQLite's
[rename propagation](https://www.sqlite.org/lang_altertable.html#alter_table_rename)
and [column metadata](https://www.sqlite.org/pragma.html#pragma_table_xinfo).

Schema 6 synchronizes profiles with `QSYS/*USRPRF` descriptors in the same database
statement; credentials remain solely in the profile table. Profile deletion checks
owned objects, group/owner references and active/queued jobs. Updates cannot recreate
a deleted profile. Generic profile copy/rename/move is rejected; creating a profile
uses the profile service. Schema 7 gives subsystem/job-queue payloads library-qualified
keys; copy preserves configuration/routing and creates stopped subsystems. Active
subsystems and queues with jobs cannot be relocated/deleted. Schema 8 inventories
existing authorization lists; list members/attachments follow rename, copies retain
membership, and attached lists cannot be deleted.

Qualified submenu and profile/routing program/menu references follow relocation.
Dynamic unqualified names remain runtime lookups. Menu registration is atomic and
preserves ownership on edits. Libraries configured in profile initial current-library
or system library-list values must be removed from that configuration before rename/
delete; shipped libraries cannot be deleted. These operations cover WP1's runnable
object foundation. New LF/display/spool/journal and other domain functionality remains
in its owning C05/C08/C10–C14 tasks, including its payload and dependency integration.

## Library resolution and ownership

Unqualified program/file lookup uses the configured system library list, current
library, then user library list, removing duplicates in order. It does not scan all
libraries. `*CURLIB` resolves only the job's current library. New unqualified files go
to that library. `ADDLIBLE`, `RMVLIBLE`, `CHGLIBL` and `CHGCURLIB` validate and persist
only the calling job's context; submitted batch jobs inherit that context. New program,
file and library commands record the creating profile as owner when a job is present.
Direct development calls without a job retain the QSECOFR default. Command-created
duplicates belong to the calling profile. Trusted low-level copying can specify a new
owner, otherwise preserving the source owner. Menu lookup uses the same job library
order; the standalone menu store searches only QSYS/QGPL/QUSRSYS by default.

Program loads re-read the descriptor; deletion or source replacement cannot leave an
old cached program callable. Profile initial current library and CCSID initialize new
sessions. Job-scoped QTEMP, overrides/ODPs and complete profile initial-program/menu
behavior remain C04/C06/C08 work.

## Filesystem implementation and signatures

`FileSystemObjectStore(directory)` is the standalone descriptor backend required by
WP1. It has one OS-file-lock owner, versioned JSON, private temporary files, data flush
and atomic snapshot replacement. Reopening restores type-qualified metadata; corrupt
or newer documents fail without overwrite. This backend stores descriptors/source,
not SQLite member tables. `SqliteFileStore` explicitly requires the SQLite catalog.
Power-loss durability of directory entries and full-system snapshots remain C13 work.

`ObjectSignature` persists algorithm, key ID, content hash, signature bytes-as-text and
signing time under the reserved `ipc.signature` extended attribute. Copy/rename/move
clear that signature because identity/content binding has changed. Metadata does not
establish authenticity: cryptographic signing, verification, trust and enforcement
remain C03/C14 requirements.

## System values and startup

Supported `CHGSYSVAL` changes validate input and update persisted values and the shared
runtime registry. QSYSNAME/QCCSID also update the active configuration and survive
restart. Library-list and password/security settings feed their existing consumers.
Changing values without implemented effects (including the virtual QDATE/QTIME clock)
fails explicitly. The command requires security administration authority; complete
service-level authorization remains C03 work. WRKSYSVAL lists the current values.

Default profiles are inserted only when missing. Restart preserves changed passwords,
disabled status, profile attributes and counters; it does not reset bootstrap accounts.

Evidence: `ObjectCatalogOperationTests`, `ObjectIdentityTests`, `LibraryResolutionTests`,
`FileSystemObjectStoreTests`, `SystemValuePersistenceTests`, and `HostLifecycleTests`.
