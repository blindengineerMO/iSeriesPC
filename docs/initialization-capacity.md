# INZPFM capacity recovery

`INZPFM` sends CPA3138 to QSYS/QSYSOPR when its requested total exceeds the
physical member's configured or previously approved capacity. The executing
job's [inquiry policy](inquiry-replies.md) controls manual, default or system
reply-list handling. Reply I grants one configured increment. C cancels with
CPF3140. Numeric multiple-increment replies are rejected; a larger requested
total can require successive I replies. The bounded seeded description defaults
to C. Trusted calls without an executing job retain CPF5030 instead of waiting.

```cl
ADDRPYLE SEQNBR(8300) MSGID(CPA3138) CMPDTA(MYFILE 1) RPY(I)
CHGJOB INQMSGRPY(*SYSRPYL)
INZPFM FILE(QGPL/MYFILE) TOTRCDS(15000)
RMVRPYLE SEQNBR(8300)
```

The first attempt fixes the selected member, its immutable identity/format, and
the desired total. Adding a later member while `MBR(*LAST)` waits cannot retarget
the command. Another writer reaching the requested `*NXTINCR` total cannot make
the retry advance a second increment. Existing prefix records and deleted gaps
retain their RRNs; default records or deleted positions are appended only after
sufficient capacity is approved. Zero increments report CPF3143 before asking
for an extension that cannot be granted.

The failed attempt releases its transaction and file allocation before waiting.
Live job, object-operation/add authority, either management or alter authority,
and delete authority for `RECORDS(*DLT)` are rechecked before granting capacity
and again after writer acquisition. Enlisted resources remain unsupported.
Cancellation cleans up the private reply queue and wait registration and restores
an active job's status. Other members can be written during the inquiry.

[Approved capacity](operator-capacity.md) is durable metadata committed separately
from initialization. It does not advance the member's record extent. If a later
constraint or journal-capture failure rejects initialization, records, extent and
journal effects roll back together; the accepted allowance remains usable.
The one-million-record/64 MiB default-materialization limits are checked before
asking for approval. Deleted reservation keeps its constant-size metadata path.

Multiple approvals still produce one atomic F/IZ event. Applying or removing
that [initialization entry](initialization-recovery.md) preserves the allowance.
Startup adds a missing CPA3138 description to an existing QCPFMSG without
replacing customized CPA3138 or CPA5305 descriptions. No catalog, database-schema,
RPG or record metadata version changes are required.

IBM's [INZPFM reference](https://www.ibm.com/docs/api/v1/content/ssw_ibm_i_74/cl/inzpfm.html?lang=en&parsebody=true)
documents the beyond-limit QSYSOPR inquiry, preserved prefix, next-increment
behavior and CPF3140/CPF3143 escapes. Precisely's
[full-file management guide](https://help.precisely.com/r/n/Assure-QuickEDD-General-Information/Full-file-management)
identifies CPA3138 as the INZPFM capacity inquiry and documents automatic I replies.
These establish the command/inquiry relationship. The seeded text, default,
severity and 42-byte replacement representation remain a bounded local contract;
the exact shipped IBM message description and replacement ABI have not been
independently verified. Local replacement fields are file, member, library and
format (ten bytes each), followed by a two-byte member ordinal.

Native ALLOCATE/storage-byte accounting, raw SQL/private commitment allocation,
complete native status mapping, full native initialization/journal ABI, IBM golden
acceptance and scale budgets remain open. C08/C09/C11 remain partial.

Verification (2026-10-19): all 6832 solution tests pass with zero failures/skips
in 11m20s, including 26 new cases. All 238 focused cases and 4350 broad
RPG/binding/work cases in the full suite pass. Locked restore, a Release build
with zero warnings/errors, 24x80/27x132 display PTY acceptance, generated inventory,
local links and whitespace checks pass.
