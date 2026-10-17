# Data queues

The shared runtime provides durable local `*DTAQ` objects with opaque entries,
FIFO/LIFO/keyed ordering and atomic destructive or non-destructive receives.
The C11/C14 checkpoint adds checked QSNDDTAQ/QRCVDTAQ/QCLRDTAQ program bindings in
Ipc.Api; see [API layouts and callers](api.md#local-data-queue-program-apis).
Queues can use [job-private QTEMP](temporary-libraries.md). The byte-addressable
[user-space checkpoint](user-spaces.md) is also implemented. DDM, journaling and
the rest of C11 remain open.

| Command | Accepted parameters |
|---|---|
| CRTDTAQ | DTAQ, MAXLEN, TYPE(*STD), SEQ(*FIFO/*LIFO/*KEYED), KEYLEN, SENDERID, TEXT, AUT |
| DLTDTAQ | DTAQ |
| SNDDTAQ | DTAQ, DATA, optional LEN and KEY |
| RCVDTAQ | DTAQ, WAIT, KEY, KEYORDER, RMV, RTNVAR, LENVAR, KEYVAR |
| CLRDTAQ | DTAQ, optional KEY and KEYORDER |

SNDDTAQ, RCVDTAQ and CLRDTAQ are iSeriesPC extensions. All use persisted command
metadata, F4 and shared dispatch. CRTDTAQ requires MAXLEN of 1–64512 bytes. Keyed
queues require KEYLEN of 1–256; other queues use zero. Defaults: TYPE STD, SEQ FIFO,
SENDERID NO. Other options, including FORCE and SIZE, are rejected. Dimensions and
sequence names follow IBM's [CRTDTAQ contract](https://www.ibm.com/docs/en/i/7.5.0?topic=c-create-data-queue).

DATA/KEY accept quoted text, hex constants or compiled CL CHAR buffers. LEN selects
a positive DATA prefix; omission sends the full buffer. Keys require the exact
declared length. No bytes are transcoded. KEYORDER accepts EQ/NE/GT/GE/LT/LE with
an optional `*`. Keyed receive requires KEY and defaults to EQ; unkeyed queues
reject keys. Clear without KEY empties any queue; keyed clear can select a range.

```cl
CRTDTAQ DTAQ(QGPL/WORK) MAXLEN(80) SEQ(*KEYED) KEYLEN(2)
SNDDTAQ DTAQ(QGPL/WORK) DATA(X'FF00FE01') KEY(X'0011')
RCVDTAQ DTAQ(QGPL/WORK) KEY(X'0011')
CLRDTAQ DTAQ(QGPL/WORK)
DLTDTAQ DTAQ(QGPL/WORK)
```

RTNVAR/KEYVAR require compiled CL CHAR storage; LENVAR requires DEC(5,0). Results
preserve bytes and pad with job-CCSID blanks. Receiver declarations are checked
before receipt. Oversized entries raise CPF9506 and remain queued, with outputs
unchanged. CL CHAR's 32767-byte limit still applies. Empty receipt returns length
zero and blank character outputs. Without targets, receipt lists up to 256 data
bytes as hex, plus the full key and optional sender principal.

FIFO follows insertion order; LIFO reverses it. All keyed comparisons search
ascending unsigned bytes, including LE/LT/NE; duplicate keys are FIFO. See IBM's
[keyed receive ordering](https://www.ibm.com/docs/ssw_ibm_i_74/rzajq/rzajqudfreceivedtaq.htm).
Keys use neither Unicode nor SQLite text collation.

WAIT defaults to zero and accepts integer seconds up to 604800 or *MAX. RMV
defaults to YES; NO peeks. Selection/removal share an immediate transaction.
Waits poll every 50 ms without retaining transactions/readers. Cancellation is
checked around transactions and writes; contention uses one-second SQLite busy
intervals and a bounded retry window. Independent processes coordinate through
SQLite. A waiting receiver rejects a deleted/recreated queue's new identity.

Send requires operate/add; destructive receive operate/read/delete; peek
operate/read; clear operate/delete. Profile/session/library authority remains
live during waits. Name lookup does not demand read permission just to send.
Library default creation authority, AUTLs and job object allocations apply.
SENDERID YES snapshots the sending principal/job. Native QRCVDTAQ exposes the
checked packed headers and 44-byte sender identity layout documented in the API contract.

Schema 24 adds indexed entries with cascading object references. Version-1 IPCDQ1
definitions live in object payloads; earlier message bytes/state are preserved.
Malformed/duplicate/unsupported definitions fail before entry access. Renames and
moves preserve entries. Copies get a new identity and start empty; deletion
cascades entries. Each queue allows 4096 entries and 16 MiB of combined data/key
bytes, plus metadata bounded by entry count. Full queues reject sends atomically.
Committed entries survive reopening. Destructive receipt commits before returning:
a consumer crash after commit can lose application delivery. Acknowledgement/requeue
and journal replay are unfinished contracts.

DataQueueTests covers byte fixtures, selectors, ordering, bounds, quotas, concurrent
consumers, live revocation, sender identity, allocations, AUTLs and lifecycle.
ClDataQueueTests covers buffers, atomic output failures, MONMSG, lookup, authority
and two-job waits. DataQueueProcessTests covers independent producers/consumers,
killed waiters, disk reopen, schema-23 upgrade and write-contention cancellation.
DataQueueApiTests adds packed parameters, sender/error buffers, truncation and
peek, cancellation, live API/queue authority and signing trust, immutable/aliased
receivers, maximum lengths and an RPG→CL→API call. Display PTY acceptance runs
create → compile/call extension and native API callers → send/receive → clear → delete.
RpgApiArgumentTests adds direct typed RPG callers with live scalar aliases,
receiver conversion before dequeue, exact packed declarations, sender/error
outputs, cancellation, quotas and byte-width failures. RPGDQAPI runs the native
keyed lifecycle directly in terminal acceptance; prototype/native storage limits
are described in the [RPG API contract](api.md#rpg-scalar-api-calls).

Scalar prototypes now borrow actual receivers directly with exact formal/actual
layout validation and immutable literal buffers. RpgPrototypeApiTests and terminal
RPGPRAPI cover prototyped queue calls; see [scalar prototype calls](api.md#scalar-prototype-calls)
for supported reference modes and remaining limits.
