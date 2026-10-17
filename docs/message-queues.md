# Message queues

Named `*MSGQ` objects now hold durable messages in schema 19. Queue creation,
send, receive, reply, rename and deletion use the catalog and its authority/lock
services. A message stores its bytes and CCSID, server-established sender, job,
timestamp, type, severity, key and receive/reply state. Queue renames also update
reply destinations. Deleting and recreating a reply queue cannot redirect an old
inquiry to the new object.

Schema 20 adds durable queues for individual program calls and the job's external
queue. It preserves schema 19 messages, routing, state and the last allocated key,
including when every old message has been deleted. Program queues belong to an
actual running job and are created lazily with the call frame. Returning closes
the frame to new traffic; retained messages remain available in DSPJOBLOG. Ending
a job closes all its queues. Empty returned frames are reclaimed.

The current command surface is:

| Command | Implemented parameters |
| --- | --- |
| CRTMSGQ | MSGQ, TEXT, AUT (including authorization lists and *LIBCRTAUT) |
| DLTMSGQ | MSGQ |
| SNDMSG | MSG, TOMSGQ, MSGTYPE(*INFO/*INQ), explicit RPYMSGQ for inquiries |
| SNDRPY | MSGKEY, MSGQ, RPY (including *DFT), RMV |
| SNDPGMMSG | MSG, MSGID, MSGF, MSGDTA, MSGTYPE, TOPGMQ(*PRV/*SAME/*EXT), TOMSGQ, RPYMSGQ, KEYVAR |
| RCVMSG | MSGQ (default *PGMQ), PGMQ, MSGTYPE, MSGKEY, WAIT, RMV, CCSID, KEYVAR, MSG, MSGLEN, MSGID, SEV, TXTCCSID, RTNTYPE, SENDER, SENDERFMT, SECLVL, SECLVLLEN, MSGDTA, MSGDTALEN, MSGF, MSGFLIB, SNDMSGFLIB, DTACCSID |
| DSPMSG | Explicit named MSGQ or *SYSOPR; bounded list with display, reply and confirmed removal |
| RMVMSG | MSGQ, PGMQ (including *ALLINACT with CLEAR(*ALL)), MSGKEY, CLEAR(*BYKEY/*ALL/*KEEPUNANS/*OLD/*NEW), RMVEXCP (OPM frames) |

RCVMSG runs in a compiled CL variable frame. It supports ANY, FIRST, LAST,
NEXT/PRV relative to a key, and INFO/INQ/RPY/COMP/DIAG/COPY selection. ANY receives the
first new message. RMV(*NO) marks the message old; explicit keys and FIRST/LAST
can retrieve it again. All return-variable declarations are checked before
receiving. Failed CCSID conversion also leaves the message unconsumed. No message
at the end of WAIT returns blanks/zeroes; a missing explicit key raises CPF2410.
Four-byte message keys remain opaque CL character buffers, including in UTF-8
jobs. Text is padded/truncated by bytes; MSGLEN reports available length.

Schema 22 snapshots sender job name/user/number, current profile, sending program
and original send time. Forwarded exceptions retain their origin and identify the
new recipient frame. RCVMSG SENDERFMT(*SHORT) requires at least 80 CHAR bytes;
*LONG requires 720. SHORT returns the current profile only when at least 87 bytes
are available. LONG follows the native fixed offsets; unavailable module,
procedure and instruction fields remain blank, with known program statement
counts zero. Extra storage and an empty receive are blank-filled. Times use UTC,
including the documented 0yymmdd date and six microsecond digits. Historical
unknown sender programs stay blank. Invalid layouts, encoding or job-number
overflow fail before consumption. Reply KEYVAR for ANY/RPY returns the sender-copy
correlation key. MessageSenderTests includes independent CCSID 37/1208 layouts,
sender job termination, exception forwarding and migration preservation.

Schema 23 snapshots predefined descriptions and replacement bytes. SNDPGMMSG
resolves MSGID/MSGF at send time; queued text, help, severity and inquiry defaults
survive edits, deletion and restart. RCVMSG MSGDTA returns replacement bytes,
SECLVL returns help, and the corresponding lengths report available bytes before
truncation. Length/CCSID outputs require DEC(5,0); file/library outputs require at
least ten CHAR bytes. MSGFLIB retains the requested library (including *LIBL);
SNDMSGFLIB is blank when the original file path/creation identity is gone.
DTACCSID is zero for immediate/empty results, 65535 without CCHAR fields and the
replacement encoding for CCHAR. CCSID(*HEX) retains original replacement bytes;
other targets convert only CCHAR and rewrite its varying-length prefix. Formatting
and conversion complete before receipt changes state. See
[message descriptions](message-descriptions.md) for formats and current limits.

Schema 21 retains exception-handled state and reply origin. RTNTYPE requires an
exact two-byte CHAR return variable: 01 completion, 02 diagnostic, 04 information,
05 inquiry, 06 sender copy, 14/16 handled/unhandled notify, 15/17 handled/unhandled
escape, 21 unchecked entered reply, 22 checked entered reply, 23 explicit message default,
24 system default (currently empty). An empty receive returns two blanks.
Unsupported type layouts fail before receipt. MONMSG marks its received exception
handled before recovery; RCVMSG reports the state at the time of receipt.
RMV(*NO) handles a received exception and marks it old. RMV(*KEEPEXCP) preserves
new/unhandled exceptions for repeated receives, otherwise marks the message old.
The broader ILE handler/action model is still pending.

Migration preserves existing message bytes and keys. Existing replies receive
code 21 because the prior schema did not retain origin. Nonempty historical
default data implies code 23; empty historical defaults use code 24 because their
original explicit/implicit distinction cannot be reconstructed.

SNDPGMMSG runs in a compiled CL frame. Ordinary messages default to the caller's
queue. Explicit escapes are queued there before MONMSG propagation; RCVMSG
MSGTYPE(*EXCP) retrieves the newest unreceived escape/notify entry. Immediate
SNDPGMMSG text permits 3000 encoded bytes. Inquiries target a named queue or *EXT
and create a sender copy in the current program queue by default. KEYVAR returns
the sender-copy key; RCVMSG MSGTYPE(*RPY) MSGKEY(&KEY) waits for its reply. Receiving
and removing that reply removes the copy too. A recipient can deliver a reply to
the exact active sending frame, without gaining access to read that frame's queue.
Replies after the frame returns fail atomically. Program queues allow waits only
for replies and share the 4096-entry/8-MiB quota across the whole job.

Arithmetic and command failures enter the current program queue before MONMSG
recovery. An unmonitored error propagates to each caller exactly once, preserving
its original message bytes, ID and severity. References are validated against the
owning job and cannot forward another job's exception. Source locations remain
in command diagnostics rather than being repeatedly prepended to queued data.
Host-generated text uses valid UTF-8 bounded to 4096 bytes; conversion happens at
receive time. A full or revoked queue raises a delivery error without recursively
trying to queue the reporting failure.

RMVMSG selects the requested key or a bounded snapshot of old/new/all messages.
KEEPUNANS retains unanswered inquiries and requires a named queue. Removal and
any generated default replies commit together; a later failed reply rolls back
the entire operation. Defaults delivered to the same queue survive the snapshot
removal. Removing a sender copy or its delivered reply removes both. ALLINACT
clears the owning job's returned frames and reclaims their empty queue records,
preserving active frames and other jobs. RMVEXCP is validated but has no effect
for the currently supported OPM CL frames; ILE unhandled-exception semantics
remain open.

DSPMSG provides option 5 for full text/metadata, 2 for an unanswered inquiry's
SNDRPY prompt and 4 for confirmed RMVMSG. Actions refresh the list and execute
with current command/object authority. WRKMSGQ option 8 opens the messages. Invalid
text bytes display as hexadecimal with their CCSID. Interactive MSGKEY values
use four-byte literals such as X'000000F1'; message/reply input also accepts hex
literals. Reply text prompts quote spaces/apostrophes, and a quoted '*DFT' is
literal reply text. Omitting RPY or specifying unquoted *DFT sends the default.

SNDMSG permits up to 50 distinct named destinations in one transaction and up to
512 message bytes. An inquiry has one destination and an explicit named reply
queue. Reply insertion and inquiry state changes commit together. Only the first
reply succeeds. Removing an unanswered inquiry sends its default reply in the
same transaction; a missing, denied or full reply destination leaves the inquiry
unchanged. Immediate inquiries have an empty default reply; the service interface
also accepts an explicit default, up to 132 bytes. Replies are bounded to 132 bytes.

The store permits 4,096 live entries and 8 MiB of message/default-reply bytes and serialized predefined metadata per
queue. Its service payload maximum is 6,000 bytes (native immediate-message API
capacity); SNDMSG retains its 512-byte command contract. Full queues raise CPF2460;
automatic wrapping is not implemented. Keys are globally monotonic unsigned
32-bit values, never reused after deletion, and fail on exhaustion. List calls
page by key with at most 1,000 results. DSPMSG currently displays at most 1,000
messages. Receive waits release SQLite connections and transactions between
100 ms polls, recheck current authority, and observe cancellation. Finite waits
are bounded to seven days; *MAX waits until delivery or cancellation.

Send requires object-operate/add authority; receive requires use authority and
delete authority when removing. RMVMSG requires operate/delete, without granting
read access. Reply requires use/add and optional delete on
the inquiry queue plus send authority on a permanent reply destination. An
inquiry's exact private reply route uses the system-delivery rule described below. Create, display,
delete, library access and automatic command locks use the shared services.
Explicit *EXCL allocations therefore apply to message operations too.

Still open: ILE exception-handler state, advanced predefined formats,
reply-handling exits, overrides, operator second-level presentation and remaining return layouts, user/workstation queue
selection and TOUSR, queue delivery attributes, break/notify dispatch,
wrapping, RPG message-subfile exchange and the public API
formats. The service can store completion/diagnostic/status/escape/notify entries;
this alone does not implement their delivery semantics. SNDPGMMSG also retains its
existing CL output behavior. C07 and C11 remain open.

MessageQueueTests covers independent bytes/keys, new/old selection, cancellation,
authority revocation during waits, concurrent consumers, durable restart,
rename/delete routing, count/byte limits, key exhaustion and atomic failures.
ClMessageCommandTests exercises real CL calls with EBCDIC/UTF-8, binary keys,
inquiry/reply, output validation, empty receives and failed conversion followed
by a raw receive. ProgramMessageQueueTests covers frame ownership, caller routing,
cross-job sender-copy replies, returned-frame rejection and schema 20 migration.
The display PTY includes CRTMSGQ → CRTCLPGM/CALL → named and program inquiry/reply
→ SNDMSG/DSPMSG → DLTMSGQ.

## Job-private queues and message files

Named queues and description files now resolve QTEMP at every storage boundary.
Jobs using the same profile and names have separate messages and descriptions.
Logical/physical aliases are normalized before validating a multi-destination
send, preventing duplicate deliveries to one object. Send batches retain atomic
failure behavior across private and permanent destinations. CL creation, mutation,
predefined sends, receive metadata, DSPMSG actions, removal and deletion all use
logical names. Authority errors also report QTEMP rather than an internal name.

An inquiry can direct its reply to the sender's private named queue. The saved
route grants only reply delivery to that exact queue while the originating job
remains active and uncancelled. It grants no read access, ordinary send access,
library enumeration or access to another object. Private routes are checked
against the persisted sending job. This uses a short internal access scope around
lock acquisition; the reply keeps the actual replier's sender identity and job.
Parent/object locks belong to the replier, so another job's exclusive allocation
still blocks delivery. The scope expires on success and failure. Invalid saved
reply rules, quota failures and allocation conflicts leave inquiry/copy state
unchanged. Default replies generated by removal use the same route.

Renames update queued content and reply routes through catalog foreign keys.
Copies start empty; exports survive the originating job's cleanup. Deleting and
recreating a private reply queue never rebinds an old inquiry. A waiting named
receive also pins the object's creation identity and rejects a replacement.
Job-end and exclusive-owner recovery cascade private entries and clear external
reply references in the same transaction as namespace cleanup. Permanent
inquiries remain retained; replies to a removed destination fail with CPF2469.
Unresolved legacy QTEMP reply routes also fail rather than selecting the replier's
own queue. Existing missing-destination removal behavior remains documented above.

Predefined snapshot payload version 3 remembers a private source file while
retaining its internal creation identity for lookup. Public queue/history results
and RCVMSG source-library fields expose QTEMP, never that internal identity.
After the source file/job disappears, text, replacement bytes and saved reply
rules remain available and SNDMSGFLIB becomes blank. Versions 1/2 remain readable.
Legacy private references are classified in the message read's SQLite snapshot;
cleanup retires their stored library names before deleting the namespace map,
without increasing old payload sizes. Schema 27 expands immediate-message storage
and adds job-owned native waiting-receiver allocation records.

TemporaryMessageTests covers private CRUD, two CCSIDs, privileged cross-job denial,
exact reply routes, locks, copy/export, invalid replies, legacy snapshots and
compiled CL. TemporaryMessagePersistenceTests covers separate producers/consumers
and a reply process, reopen/recovery, injected cleanup rollback and replacement
of a queue during an actual wait. Terminal acceptance creates a private queue and
file, adds a description, compiles/calls a private CL program, receives logical
metadata, displays the saved text and removes the private objects. Broader C11
delivery attributes, break/notify dispatch and wider API work remain open. Compiled
RPG [WORKSTN message subfiles](rpg-spec.md#12-workstn-display-files-and-subfiles)
now display live program-queue snapshots through EXFMT, including opaque keys and F1 help.

## Native message API access

[QMHSNDPM/QMHRCVPM, QMHSNDM/QMHRCVM, QMHSNDRM and QMHRTVM](api.md#native-message-program-apis)
now expose bounded native parameter/receiver contracts over these same stores.
Receive SAME preserves seen and handled state. Receiver preparation occurs inside
the mutation transaction before removal, default replies or marking; send-key
preparation precedes commit as well. All targets roll back on preparation failure.
Native immediate messages accept 6000 bytes; predefined replacement data remains
bounded to 512. Current formats are RCVM0100/0200 and RTVM0100.

A positive or indefinite native named receive reserves the queue in
sys_message_receivers and holds shared-read object/library allocations. Another
job can send while the receiver waits, but receive/remove/reply returns CPF2451.
The receiver releases on delivery, timeout, cancellation and terminal job outcome;
exclusive-owner recovery ends interrupted jobs and clears their reservation rows.
Stored sender metadata excludes internal IPCAPI frames; CYYMMDD dates encode
the century relative to 1900. API tests cover two CCSIDs, high-bit keys, binary
metadata, conversion, failed output preparation, replies, QTEMP isolation,
allocation/cancellation and upgrade/rollback. Native exception/UI dispatch,
rejection exits and richer formats remain open.

## Message-subfile display snapshots

The display runtime now binds to active job/frame queues through
`JobProgramMessageSource` and `DisplayFileStore.Open`. Exact-key row writes and
SFLINZ initialization copy text and second-level text without changing queue
receive/exception state or answering inquiries. Bulk initialization uses one
read snapshot up to the queue's 4,096-entry bound. Copied rows survive removal
of the original message or private MSGF. See [display-file binding and remaining
language work](display-files.md#message-subfiles) for DDS syntax, help and limits.
Compiled CL drives this path through DCLF and control-format SNDF/RCVF/SNDRCVF,
with active call queues, response indicators, MONMSG and saved F1 help. RPG exchange
and ILE procedure queues remain open.

References: [message command authority](https://www.ibm.com/docs/en/i/7.6.0?topic=commands-message),
[RCVMSG](https://www.ibm.com/docs/en/i/7.5.0?topic=r-receive-message),
[SNDPGMMSG](https://www.ibm.com/docs/en/i/7.4.0?topic=s-send-program-message),
[message removal](https://www.ibm.com/docs/en/i/7.6.0?topic=messages-removing-from-message-queue),
[CRTMSGQ](https://www.ibm.com/docs/en/i/7.5.0?topic=c-create-message-queue),
[DSPMSG](https://www.ibm.com/docs/en/i/7.5.0?topic=d-display-messages).


Predefined inquiry replies now enforce the saved description's TYPE/LEN/VALUES/
SPCVAL/RANGE/REL rules through SNDRPY and the operator F4 prompt. Invalid replies
raise CPF2422 inside the reply transaction, preserving inquiry and sender-copy
state. Earlier unchecked descriptions retain their behavior. See
[reply rules](message-descriptions.md#inquiry-reply-rules) for encoding, defaults,
limits, migration and acceptance evidence.
