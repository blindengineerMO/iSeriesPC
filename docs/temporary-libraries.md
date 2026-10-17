# Job-private QTEMP checkpoint

Schema 26 adds a private catalog namespace for each interactive, communication,
system, group and submitted batch job. Creation is atomic with job creation;
failed submission/group initialization rolls back the namespace too. Jobs present
before the upgrade receive missing namespaces once during catalog startup.

The accepted integrations are user spaces, typed data areas, local standard data
queues, source/physical/simple logical files and their members, CL/RPG source
compilation and database input, catalog lookup/copy/move/delete, authority
changes, program verification and object allocations. Other domain-specific stores
still need integration; this is not a claim that all object types work in QTEMP.

## Names and access

Applications use the logical library name QTEMP:

```text
CRTUSRSPC QTEMP/WORK 1024 VALUE(X'00')
ADDLIBLE QTEMP
DLTUSRSPC WORK
```

QTEMP is not automatically inserted into configured library lists. ADDLIBLE,
CHGLIBL and CHGCURLIB preserve explicit job configuration. *LIBL searches QTEMP
when it appears in the job's list, and *CURLIB resolves to it when configured as
the current library. Native user-space attribute and return-library receivers
report `QTEMP`, as do public catalog and allocation views. Command prompting,
help and command listing establish the same job identity as command execution.

Each namespace has a runtime-owned backing library and an exact job-key binding.
The backing library has public authority *EXCLUDE. Access checks enforce the
executing job before ordinary object/special authority, including when a caller
supplies a backing name directly. Another job cannot read, change, create in,
delete or enumerate that namespace, even under QSECOFR. Calls without an executing
job cannot resolve the logical QTEMP name. Profiles, authentication sessions and
job cancellation remain live checks.

Trusted in-process host code without an OperationIdentity retains the existing
catalog maintenance privilege and can inspect raw storage. This privilege is not
available as a client-selected job or identity. The authenticated server owns each
session's execution context.

Namespace roots cannot be deleted or renamed through ordinary object/library
operations, or installed as backing-name library defaults. Objects can be copied
or moved out explicitly; their permanent destination survives the original job.
Copies receive independent payload identities. Registered permanent dependencies
cannot point into QTEMP. Exporting an object that still depends on another private
object fails; internal registered dependency cycles are removed during cleanup.

QALWUSRDMN checks use the logical library name, so a QTEMP entry authorizes user
domain creation there. When user-domain creation is allowed in QTEMP, QUSCRTUS
uses that domain, including the documented QTEMP *SYSTEM request behavior.
Native auxiliary-storage allocation and MI pointer behavior remain outside the
current implementation.

## Job lifetime and recovery

Programs in one job see the same temporary objects. Separate jobs can use the
same names without sharing objects or allocations. Group jobs have distinct
QTEMP namespaces even while sharing a GDA. Batch submission copies logical library
settings and receives a separate empty namespace; it does not inherit temporary
objects. Held jobs retain their namespace.

Normal, warning, abnormal and queued-cancellation completion removes private
payloads, metadata, authorities, internal dependencies, the binding and the backing
library in the job-completion transaction. A cleanup failure rolls back completion
and all preceding deletions, leaving the job and its data available for diagnosis
and retry. Job locks are released after a successful completion commit.

Ordinary catalog opens preserve active/queued job contents. Only the exclusive
session-server owner performs startup recovery: running interactive/communication/
system/batch jobs become interrupted and their temporary objects are removed.
Residual namespaces belonging to already terminal jobs are also reclaimed before
the server accepts sessions. Unclaimed queued jobs retain their namespaces. A
restart never silently replays an interrupted batch job.

## Private source and database files

File operations now resolve QTEMP before accessing definitions, members, record
tables, source revisions, cursors and access paths. This covers the existing PF
and simple LF subset: keyed/sequential reads, prefix lookup, insert/update/delete,
member limits, clear/remove, logical selection and member bindings, source saves,
CPYF and object copy/export. Native character, numeric, null and record layout
contracts remain those documented in [DDS schema](dds-schema.md).

Private source members can supply CRTCLPGM, CRTBNDRPG and CRTPF/CRTLF. CL DCLF/RCVF
and RPG overrides/shared open paths consume the owning job's files. The same
logical file names in another job select that job's records and cursor positions.
Existing unqualified lookup rules and explicitly configured library lists apply.

A private logical file can reference its job's private PF or an authorized permanent
PF. A permanent LF cannot reference a private PF. Stored LF references bind the
resolved physical storage; ADDLFM DTAMBRS still accepts the logical QTEMP name.
Cleanup releases private unique constraints on permanent PFs, retaining an index
when another LF still references it. Non-unique paths remain the existing bounded
cache owned by their permanent PF; dropping a private PF removes its own tables
and indexes. Registered and parsed external SQL dependencies prevent a destructive
cleanup and roll back job completion. Broader SQL dependency admission remains
part of the unfinished SQL bridge.

The file acceptance suites exercise independent process writers, exact large
decimals, private source revisions, two CCSIDs, CL/RPG compilation and execution,
shared cursor isolation, logical bindings/constraints, copy/export and startup
recovery. Failure injection proves that unsuccessful DDL leaves no private file
metadata, and a later cleanup failure restores dropped tables, rows and indexes.
The real terminal flow copies DDS and CL members into a private source file,
compiles and populates a private PF, compiles/calls its private CL reader, displays
the data and deletes the private source/data files.

File checkpoint verification (2026-10-17): all 1710 tests pass, zero failures or
skips, including 21 new private-file cases. Locked restore, zero-warning/error
Release build, expanded terminal acceptance, inventory and whitespace checks pass.
The broader checklist remains 35 complete / 71 open.

## Acceptance and remaining work

`JobTemporaryLibraryTests` checks exact job isolation, privileged cross-job denial,
logical lookup/output names, groups, submission, protected roots/dependencies,
copy/export, CL calls and signature tamper rejection, native queue APIs, data
areas/queues, authority changes and private allocations.
`JobTemporaryLibraryPersistenceTests` checks independent processes writing both
shared and separate job namespaces, simultaneous authenticated sessions, signoff,
catalog reopen, exclusive-server recovery, queued cancellation, creation/cleanup
rollback and schema-25 upgrade. Migration acceptance additionally covers upgrade
from versions 18–25.

Final verification (2026-10-17): all 1689 tests pass with zero failures or skips,
including 25 new QTEMP cases and eight migration-version cases. Locked restore,
zero-warning/error Release build, full display PTY with QTEMP user spaces, generated
inventory and whitespace checks pass. Checklist stays 35 complete / 71 open.

Display files, menus, command-object payloads, packaging and other specialized stores still need explicit
QTEMP integration and acceptance. Their global implementations do not by themselves
prove private namespace support. User-space generic deletion, EXCLRD writes and
domain-policy checks on relocation are now implemented; richer native errors,
MI/list APIs and broader C11/C14 contracts remain open.
The emulator's durable events still record these object changes; native QAUDCTL
QTEMP auditing exclusions are not implemented.

IBM's [QTEMP description](https://www.ibm.com/support/pages/qtemp-library-description-and-other-faqs)
defines job isolation, deletion at job end, IPL recovery and movement to permanent
libraries. The [QUSCRTUS contract](https://www.ibm.com/docs/en/i/7.4.0?topic=q-create-user-space-quscrtus)
defines user-space domain behavior in QTEMP. The implementation above uses those
contracts while recording its remaining emulator-specific integration work.

## Private messaging

Message queues and description files now resolve private storage for CRUD,
predefined delivery and receive metadata. Saved inquiry routes can deliver a reply
to another job's private queue without granting general access; originating-job
checks, replier-owned allocations and transaction rollback protect that path.
Queue/file cleanup, cascading entries, orphaned reply references and retained
predefined snapshots participate in the existing atomic job-end/recovery boundary.
Private references in current and legacy snapshots stay logical even after the
namespace is gone. Waiting receives reject a deleted/recreated queue. See
[message queues](message-queues.md#job-private-queues-and-message-files) and
[message descriptions](message-descriptions.md#private-message-files) for the
contract, process tests and remaining C11 delivery gaps.
