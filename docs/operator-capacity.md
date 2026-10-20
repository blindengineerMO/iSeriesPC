# Operator recovery when a member is full

Ordinary physical/logical additions, declared RPG WRITE and CPYF now recover
from exhausted member SIZE through a CPA5305 inquiry to QSYS/QSYSOPR. The
executing job's [inquiry policy](inquiry-replies.md) controls the reply. Default
mode cancels with reply 0; required mode waits for an operator; system reply-list
mode uses the matching entry. C cancels, I grants one configured increment, and
1–9999 grants that many increments. Invalid replies remain available for correction.

```cl
ADDRPYLE SEQNBR(8200) MSGID(CPA5305) CMPDTA(MYFILE 11) RPY(I)
CHGJOB INQMSGRPY(*SYSRPYL)
/* The next ordinary addition may use the approved extra increment. */
```

Capacity is durable and belongs to the immutable physical member identity and
its SIZE configuration. It does not create deleted positions or advance the
relative record extent. Other members remain independent. Object copy/move
preserves the allowance; member deletion cascades it. CLRPFM resets it. Actual
SIZE changes replace the allowance, while SAME and unrelated attribute changes
preserve it. INZPFM can consume an existing allowance, including next-increment
initialization, within its existing materialization quotas. Its own beyond-limit
[CPA3138 inquiry](initialization-capacity.md) now shares the grant/retry mechanism.

A full record transaction unwinds before sending or waiting for the inquiry.
The grant uses a separate transaction, then retries the data operation. Granting
capacity does not weaken authority: live job, current permissions, member identity,
format, commitment eligibility and configuration are checked again after writer
acquisition. Accepted capacity remains allocated if a later record constraint or
trigger fails; the failed record and its journal effects roll back.

Manual waits set MSGW without holding a SQLite writer. Cancellation closes the
private reply queue and restores an active job's wait state. Ending/interruption
cleanup closes registered inquiries even when their worker is absent. Factory
shutdown cancels waits; interrupted-job recovery clears persisted requests.

CPYF keeps one bounded TEMP source snapshot across the inquiry/retry. Changed
source records do not alter that snapshot, and recreated source/target members
reject the old operation. Publication remains atomic. Scratch tables are removed
before disk connections return to their pool. The existing one-million-row and
64 MiB staging bounds remain in force.

Schema 34 adds member capacity and registered size-wait state. Startup supplies
bounded QCPFMSG CPA5305 and CPA3138 descriptions without replacing existing descriptions.
The existing generic CPF9898 CL adapter remains available when the generated
file does not supply a generic description. This is a local description/typed
replacement representation; independent native catalog/layout acceptance remains
open.

IBM's [published CPA5305 recovery text](https://www.ibm.com/support/pages/message-handling-remote-command-server-rexec)
defines cancel, single/multiple increments and default reply 0. Its
[CLRPFM reference](https://www.ibm.com/docs/en/i/7.4.0?topic=c-clear-physical-file-member)
describes resetting member data and allocated storage.

Trusted calls without an executing job report CPF5030 instead of waiting.
Zero increments and grants beyond RRN 4294967288 fail explicitly. Native RPG
continues through the existing bounded file-status mapping. Native
ALLOCATE/storage-size accounting, raw SQL
and commitment/isolation allocation, wider status/catalog ABI parity, independent
IBM acceptance and scale budgets remain open. C08/C09/C11 remain incomplete.

Verification (2026-10-19): all 6806 solution tests pass with zero failures/skips
in 11m04s, including 35 new cases. All 251 focused cases and 4350 broad
RPG/binding/work cases in the full suite pass. Locked restore, a Release build
with zero warnings/errors, 24x80/27x132 display PTY acceptance, generated inventory,
local links and whitespace checks pass.
