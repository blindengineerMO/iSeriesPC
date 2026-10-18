# Database record waits

PF and simple LF definitions persist `RecordLockWaitSeconds` independently of
record formats. New definitions and older JSON without this property default to
60 seconds. `CRTPF` and `CRTLF` accept WAITRCD; `CHGPF` can change WAITRCD and
MAXMBRS atomically, and the CHGLF subset changes WAITRCD. The create/change values
are 1–32767, *IMMED (stored as zero), and *NOMAX (stored as 32767). Change commands
also accept *SAME. This follows IBM's [CRTPF](https://www.ibm.com/docs/en/i/7.5.0?topic=c-create-physical-file),
[CHGPF](https://www.ibm.com/docs/en/i/7.5.0?topic=c-change-physical-file) and
[CHGLF](https://www.ibm.com/docs/en/i/7.5.0?topic=c-change-logical-file) definitions.
Other CHGLF attributes remain unsupported and are rejected.

Changing wait attributes requires live object-management and object-alter
authority. Invalid values and failed member-limit changes leave both attributes
unchanged. Copy/rename and restart preserve the JSON attribute; no catalog
migration or RPG recompile is required. The existing record-unit foundation's
conservative definition checks can reject publication after metadata changes;
this does not establish native commitment/open-attribute behavior.

## Job overrides

```cl
OVRDBF FILE(DATA) TOFILE(*FILE) WAITRCD(*IMMED) OVRSCOPE(*JOB)
DSPOVR FILE(DATA)
DLTOVR FILE(DATA) LVL(*JOB)
```

OVRDBF accepts 1–32767, *IMMED and *NOMAX. Omitted WAITRCD uses the runtime file's
attribute. **OVRDBF *NOMAX is an indefinite wait**, whereas create/change *NOMAX is
32767 seconds. IBM documents the indefinite override under
[OVRDBF](https://www.ibm.com/docs/en/i/7.6.0?topic=o-override-data-base-file).
Zero must be written as *IMMED in commands. Numeric zero, negative numbers,
fractions, out-of-range values and unsupported special values are rejected.

Wait values follow the existing nearest-call/job override scope and appear in
DSPOVR. Explicit TOFILE(*FILE) resolves the original external filename. Removing
an override or changing the file attribute affects subsequent opens. An already
open path retains its original wait policy. The existing override subset replaces
complete file/member/share/scope values; general native parameter merging,
activation-group override scope and additional options remain open.

## RPG and service behavior

Declared noncommit RPG update/delete files capture the effective policy when
opened. The opened LF's own attribute is used for logical input; it does not
inherit the underlying PF's wait. The selected physical member/RRN is still the
resource locked. Native source-bound layout signatures do not include the wait,
allowing an unchanged compiled format to open with updated attributes.

CHAIN, READ, READP, READE and READPE use the policy when acquiring retained input.
N input remains inquiry and does not wait for a foreign update-read lease. Key/RRN
DELETE and current mutations use the policy for their physical mutation lease.
The existing exact-image/schema checks remain in force: a target changed while
waiting can require fresh selection rather than writing a stale image. Successful
locked input reselects fresh data and checks authority/schema after the wait.

Compatible SHARE paths retain one position and policy. Different effective wait
values have different declared ODP identities, alongside their access modes.
Call-token rebinding preserves the original policy. Unsupported providers must
explicitly support the new record-wait overload; a policy is never silently
ignored by the default provider method.

The service `OpenUpdateCursor` API keeps its zero-wait default. Explicit waits
accept 0–32767 seconds and `Timeout.InfiniteTimeSpan`; the latter is the exact
negative-one-millisecond sentinel, not a negative number of seconds. Finite
requests retain their budget across parent/object allocation. Indefinite waiters
use a durable nonexpiring deadline and continue checking cancellation and job
liveness. Cancellation, job termination and factory disposal release waiter
state through the existing lock lifecycle. Waits hold neither a main SQLite
reader nor its data writer. The public ALCOBJ subset keeps its existing 0–300
second input contract.

## Evidence and remaining native work

`RecordWaitFileTests` and `RpgRecordWaitTests` exercise metadata, command values,
atomic changes, authority, scope, all five input operations, fresh-after-wait
records, timeout cleanup, N input, keyed/RRN deletion, LF policy, retained calls
and mode-compatible sharing. `RecordWaitPersistenceTests` covers restart/copy,
legacy JSON and long/indefinite deadlines across factories with caller/factory
cancellation. Terminal acceptance runs create/change PF/LF wait keywords, RPG
under an override, and compiled CL CHGPF/OVRDBF/DLTOVR.

Service timeouts retain CPF1002 and deadlocks CPF1003. Declared RPG operations
now classify record-lock failures as 1218 with E/%ERROR/%STATUS and structured
MONITOR handling; see [numeric statuses](rpg-file-status.md). Full native status
mapping, commitment isolation/open flags, WAITFILE and complete ODP compatibility
remain required. Arbitrary SQL, trigger/FK and trusted jobless lock coordination,
intrajob deadlock/fairness, independent native golden behavior and release
performance targets also remain open. C08/C09/C11 are not closed by this work.

### Numeric status handling

The placeholder `%STATUS` has been replaced with integer global/per-file status.
E resets status/error before execution. Every file operation updates its numeric
status; ordinary file success leaves ERROR unchanged. Program operations that do
not set status retain preceding status. Expected operation errors use E or an ER indicator before
MONITOR; operand program failures can reach program handlers. Timed contention
reports 1218 without changing program fields or retaining an update target.
See [the status contract, IBM references and acceptance evidence](rpg-file-status.md).
Full INFDS/PSDS layouts, remaining mappings and independent native golden
comparisons remain required.
