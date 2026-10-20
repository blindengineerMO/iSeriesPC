# Physical record slots and deleted-record reuse

Schema 32 stores each member's allocated extent separately from its active rows.
Deleting the last row leaves its record number allocated. Ordinary file-store
addition, declared RPG `WRITE`, logical-file addition and `CPYF` append use this
extent rather than SQLite's current maximum rowid.

`CRTPF REUSEDLT(*NO)` is the default. New records append after the allocated
extent. `REUSEDLT(*YES)` fills an available deleted position before extending it.
`CHGPF REUSEDLT(*YES|*NO|*SAME)` changes or preserves the persisted policy; invalid
combined changes leave all attributes unchanged. Reuse applies to the physical
source member when adding through a logical file. These commands remain partial.

IBM describes the [reuse attribute](https://www.ibm.com/docs/en/i/7.5.0?topic=ssw_ibm_i_75%2Fcl%2Fchgpf.html)
and its [arrival-order consequences](https://www.ibm.com/docs/en/i/7.4.0?topic=options-reusing-deleted-records).
iSeriesPC chooses the lowest available deleted position deterministically. This
choice is a local allocation policy; it does not reproduce IBM's reclaim
algorithm or guarantee native FIFO/LIFO behavior. SQL-created-table defaults and
native relative output acceptance remain separate work.

The native record-number limit is 4294967288. Exhausting the extent with reuse
disabled fails before insertion, even if the last active record was deleted.
Sparse extents require no intervening physical rows. `CLRPFM` resets the extent;
`CPYF MBROPT(*REPLACE)` clears it and allocates the copied records afresh. Append
and replace retain the existing atomic copy, cancellation, authority, lock,
constraint and trigger contracts. See [file copy](file-copy.md).

Object copy preserves the extent, including deleted trailing positions, and
creates independent member identities. Rename and move retain their original
identities. Member removal cascades bookkeeping; a recreated member receives a
new identity. The ledger does not add columns to external record formats. Generic object copy
continues to support custom WITHOUT ROWID tables; those tables have no native
RRN capture.

Each ordinary record insertion, update or deletion also changes an opaque
incarnation token in the same SQLite transaction. Current-record mutation checks
that token alongside the existing schema and typed before image. A cursor cannot
update or delete a replacement record merely because its member, record number
and values match the old record. Rolled-back writes preserve the old token and
extent. Journal capture, apply/remove recovery and live record-unit publication update this bookkeeping
atomically; bookkeeping triggers are omitted from private record workspaces and
are recreated for object copies.

Migration seeds existing physical/source member extents from their largest active
record number and creates tokens for active rows. Earlier schemas did not retain
deleted trailing extents, so an upgrade cannot reconstruct those lost positions.
Migration failure rolls back the new tables, triggers, tokens and history, with
the existing protected pre-upgrade backup available. See [catalog migrations](catalog-migrations.md).

[INZPFM and SIZE](member-initialization.md) now initialize default/deleted slots,
select first/last members and persist allocation policy. Ordinary additions and
copy also enforce SIZE. Initialization journaling is atomic; native operator
extension and native initialization-entry ABI/acceptance remain open;
[local initialization recovery](initialization-recovery.md) is implemented.

The private commitment workspace and arbitrary raw SQL still use their existing
SQLite insertion allocation. Live publication updates the ledger, but global
pending RRN reservation, reuse-aware SQL translation and native commitment
isolation remain open. Native `RECNO` field writeback, slot-directed `WRITE`,
multiple-member relative mapping, complete statuses, large-file budgets
and independent IBM acceptance are also open. C08/C09 remain partial.

Verification (2026-10-19): all 6586 solution tests pass with zero failures/skips
in 10m55s, including 19 new cases. All 498 focused cases and 4313 broad
RPG/binding/work cases in the full suite pass. Locked restore, a Release build
with zero warnings/errors, 24x80/27x132 display PTY acceptance, generated inventory,
local links and whitespace checks pass.
