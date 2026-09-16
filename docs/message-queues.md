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
escape, 21 entered reply without validity checks, 23 explicit message default,
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
queue. Its service payload maximum is 4,096 bytes. Full queues raise CPF2460;
automatic wrapping is not implemented. Keys are globally monotonic unsigned
32-bit values, never reused after deletion, and fail on exhaustion. List calls
page by key with at most 1,000 results. DSPMSG currently displays at most 1,000
messages. Receive waits release SQLite connections and transactions between
100 ms polls, recheck current authority, and observe cancellation. Finite waits
are bounded to seven days; *MAX waits until delivery or cancellation.

Send requires object-operate/add authority; receive requires use authority and
delete authority when removing. RMVMSG requires operate/delete, without granting
read access. Reply requires use/add and optional delete on
the inquiry queue plus send authority on its reply destination. Create, display,
delete, library access and automatic command locks use the shared services.
Explicit *EXCL allocations therefore apply to message operations too.

Still open: ILE exception-handler state, advanced predefined formats,
reply validity, overrides, operator second-level presentation and remaining return layouts, user/workstation queue
selection and TOUSR, queue delivery attributes, break/notify dispatch,
wrapping, message subfiles and the public API
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

References: [message command authority](https://www.ibm.com/docs/en/i/7.6.0?topic=commands-message),
[RCVMSG](https://www.ibm.com/docs/en/i/7.5.0?topic=r-receive-message),
[SNDPGMMSG](https://www.ibm.com/docs/en/i/7.4.0?topic=s-send-program-message),
[message removal](https://www.ibm.com/docs/en/i/7.6.0?topic=messages-removing-from-message-queue),
[CRTMSGQ](https://www.ibm.com/docs/en/i/7.5.0?topic=c-create-message-queue),
[DSPMSG](https://www.ibm.com/docs/en/i/7.5.0?topic=d-display-messages).
