# Initialization journal recovery

`APYJRNCHG` and `RMVJRNCHG` now recover locally captured `F/IZ` initialization
entries alongside ordinary record entries. IBM specifies insertion of initialized
records on apply and deletion on remove in its [recovery action table](https://www.ibm.com/docs/en/i/7.6.0?topic=rjo-actions-applying-removing-journaled-changes-by-journal-code).
The native [entry layout](https://www.ibm.com/docs/en/i/7.5.0?topic=information-layouts-variable-length-portion-journal-entries)
identifies the requested total, deleted/default flag and default image.

New initialization capture uses `IPCINIT2`, with the original extent, resulting
extent, count, flag and typed SQL default image. Its `schema_description` records
physical columns and the file definition in the existing version-1 schema format.
The capture limit is 1 MiB of schema; exceeding it rolls back initialization.
This is a versioned local representation, not the external IBM journal buffer ABI.
No database schema or command-catalog change is needed.

Apply restores captured default records at their original positions, preserving
existing prefix records, deleted gaps and later records outside the initialized
range. It uses the recorded default image rather than recomputing DDS defaults.
Every destination position must be empty. Writable columns are supplied directly;
generated columns are recomputed and checked against the captured image.

Remove requires all initialized default records to be present. Atomic recovery
also checks every current image before deleting any record. Deletion preserves
the allocated extent and invalidates old cursor targets through the ordinary
incarnation triggers. A later apply can refill those same deleted positions.
The existing native End/Continue error modes retain their established image-check
policy and entry savepoints; they do not permit missing initialized records.

Deleted initialization reserves the captured extent without creating records.
Its apply/remove paths reject occupied positions. Remove requires the positions
to be allocated and leaves them allocated, with no record materialization or
renumbering. A reservation at RRN 4294967288 therefore requires no intervening rows.
This local extent policy and replay preconditions still need independent IBM
acceptance, including partial/restored-extent cases.

Recovery validates the payload version, integer counts and range, header total,
flag/image agreement, complete column order, physical schema and recorded format.
Missing/duplicate/extra payload fields and malformed images reject. Historical
`IPCINIT1` entries have no captured schema and remain unsupported; recovery does
not infer their old layout from current metadata. Default replay retains the
one-million-row/64 MiB normalized-value bounds. Payload reads are bounded by the
journal entry limit before loading initialization data.

Initialization shares the existing receiver authorization/range checks, record-
unit boundaries, file/member selection, exclusive allocation, cancellation,
foreign-key verification and per-entry savepoints. Each initialization entry is
one recovery change, even when it restores many records. End/Continue failures
roll back the whole initialization entry before stopping/skipping that member.
Application and journal capture triggers stay suspended during recovery, while
slot identity capture remains active. Start/end markers and receiver accounting
commit with the data; constraint, image, cancellation or marker failures restore
the prior state. Recovery now links factory shutdown cancellation and checks the
executing job again after acquiring a waiting writer.

Tests cover default/deleted round trips, sparse/native maximum positions, prefix
and later records, explicit members, DDS defaults at CCSIDs 37/1208, restart,
stale cursor rejection, malformed payloads/headers/schema, generated values,
unique/FK failures, marker rollback, quota and job cancellation/end. Terminal
acceptance compiles CL remove/apply and verifies all restored rows from RPG at
24x80 and 27x132.

Native binary IZ import/export, independent native golden acceptance, complete
record-codec validation, global SIZE/operator-extension and commitment/isolation
coordination, wider object recovery and scale budgets remain open. These extend
the [journal foundation](journals.md) and [member initialization](member-initialization.md);
C08/C09/C11 remain incomplete.

Verification (2026-10-19): all 6688 solution tests pass with zero failures/skips
in 11m15s, including 41 new cases. All 403 focused cases and 4313 broad
RPG/binding/work cases in the full suite pass. Locked restore, a Release build
with zero warnings/errors, 24x80/27x132 display PTY acceptance, generated inventory,
local links and whitespace checks pass.
