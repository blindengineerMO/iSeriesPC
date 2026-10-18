# System API checkpoint

Executable catalog APIs are QSYS/QCMDEXC, QSNDDTAQ, QRCVDTAQ, QCLRDTAQ,
QUSCRTUS, QUSRUSAT, QUSCHGUS, QUSRTVUS, QUSCUSAT, QUSDLTUS, QWCRDTAA,
QMHSNDPM, QMHRCVPM, QMHSNDM, QMHRCVM, QMHSNDRM, QMHRTVM, QWCRSVAL, QUSRJOBI, QUSLJOB, QWCCVTDT,
QC3CALHA, QC3CALHM, QC3ENCDT, QC3DECDT, QC3CRTKX and QC3DESKX.
The data-object marshalling lives in `Ipc.Api`; QCMDEXC shares the session
command dispatcher. See [data-area retrieval](data-areas.md#qwcrdtaa-retrieval-api)
and [user spaces](user-spaces.md) for binary layouts, commands,
restart behavior and remaining compatibility gaps. The six message adapters and
runtime registry are described below. Broader work/security APIs and format contracts remain open.
Each adapter is a versioned `*PGM` object with attribute IPCAPI. Normal library lookup,
object use authority, signature validation and program scope apply. Startup seeds
each only when absent; it never overwrites an existing or signed program. Relocated
or changed adapter manifests fail validation. Programs with the same API names
in other libraries follow ordinary program lookup.

## Runtime API discovery

`WRKAPILS` lists all 27 implemented runtime adapters. `API(QMH*)` filters by a
trailing-star prefix; an exact name selects one adapter. Option 5 displays its
parameter counts, supported formats and limits. The registry checks the current
QSYS program's manifest, use authority and executable trust without invoking it.
Missing programs show `Not installed`; denied, untrusted or replaced slots show
`Blocked` with the failure ID. Listing never recreates a missing program. The
release compatibility inventory also includes planned APIs, which are not listed
as callable by this command. This is an iSeriesPC discovery command.

## Native job retrieval and lists

[QUSRJOBI and QUSLJOB](job-apis.md) retrieve JOBI0100/0700 and generate
JOBL0100/0200 in existing user spaces. Lists preserve the caller area and unused
tail, grow implicitly to the native maximum, and return authenticated continuation
handles. Seventeen keyed fields use actual job admission, identity, dates and CPU
accounting. Protected library lists and keyed foreign-job rows enforce current
job authority. Raw 16-byte IDs are shared within a host epoch; exclusive server
startup invalidates old IDs and cursors. See the linked layouts, CL/RPG examples,
error contracts and remaining job-format gaps.

## Date and time conversion

[QWCCVTDT](date-time-apis.md) converts short/long calendar and Julian layouts,
raw standard timestamps and DOS output. Five-, ten- and eleven-argument calls
support millisecond/microsecond precision, live QDATFMT selection, a captured
current instant and UTC/host-zone conversion, including DST gaps/repeated times.
Same-format and character conversion preserve native byte-copy semantics.
Job date/time-zone attributes and named time-zone description metadata remain
open; the linked contract identifies supported information cases and limits.

## Software cryptography

[The six QC3 adapters](cryptography-apis.md) implement hash/HMAC, AES encryption
and decryption, and opaque job-owned key contexts. Five hash algorithms and AES
128/192/256 with ECB/CBC/CTR use checked binary descriptors, native padding
controls and prepared output/length/error receivers. Encrypted contexts survive
calls, cannot be used by foreign jobs, participate in wrapping-key rotation and
release with job completion/recovery. See the linked byte layouts, key limits,
CL/RPG examples, known-answer evidence and remaining crypto compatibility work.

## System-value retrieval

[QWCRSVAL](system-values.md) retrieves 27 backed native system values from a
single committed catalog snapshot with one captured local clock instant. Its five
parameters select writable storage, BIN(4) receiver length/count, CHAR(10) names
and standard error storage. The receiver contains a returned count, offset
directory and complete aligned name/type/status/length/data tables. QSECURITY is
CHAR(2); QPWDLVL and QCCSID are BIN(4). Entries that do not fit are omitted whole,
and untouched tails remain unchanged. Direct RPG uses raw CHAR and signed INT.
WRKSYSVAL and native reads now observe other hosts' committed changes. See the
linked contract for every name/layout, native library-list bounds, errors and
remaining protected/lockable system values.

## Native message program APIs

These six adapters use the shared durable [message stores](message-queues.md),
including real call-frame queues, sender copies, stored reply rules, saved message
descriptions and private QTEMP objects. They do not implement separate queues.
Names and controls use job-CCSID CHAR storage, qualified names are object CHAR(10)
followed by library CHAR(10), integers are signed big-endian BIN(4), and message
keys are opaque unsigned four-byte values. Longer fixed input storage is allowed;
only the documented prefix is read. Parameter groups must be complete.

| API | Required parameters, in order | Total counts |
|---|---|---|
| QMHSNDPM | message ID(7), message file(20), data, data length, type(10), entry(10), stack counter, writable key(4), error | 9, 12, 14 |
| QMHRCVPM | writable information, length, format(8), entry(10), counter, type(10), key(4), wait seconds, action(10), error | 10, 12, 14, 15 |
| QMHSNDM | message ID(7), message file(20), data, length, type(10), queue array(20 each), queue count, reply queue(20), writable copy key(4), error | 10, 11 |
| QMHRCVM | writable information, length, format(8), queue(20), type(10), key(4), wait seconds, action(10), error | 9, 10, 11 |
| QMHSNDRM | inquiry key(4), queue(20), reply, length, remove(10), error | 6, 7, 8 |
| QMHRTVM | writable information, length, format(8), message ID(7), message file(20), replacement, length, substitute(10), formatting controls(10), error | 10, 13 |

QMHSNDPM sends INFO, COMP and DIAG to OPM `*`, `*EXT` or a named call frame with
a nonnegative counter. Runtime IPCAPI implementation frames are excluded from
selection and sender metadata. Its optional groups are entry length, two CHAR(10)
qualifiers (both `*NONE`), display wait (zero), then entry type (`*CHAR`) and input
CCSID. QMHRCVPM has the same length/qualification and entry-type/CCSID groups,
followed by default-reply rejection control (`*NO`). Native exception unwind,
notify/status/request delivery and terminal inquiries through QMHSNDPM are
explicitly unsupported before mutation. Existing CL exception messages can be
received through QMHRCVPM.

QMHSNDM sends INFO, COMP, DIAG or INQ to 1–50 distinct named queues atomically;
an inquiry has one destination and returns its sender-copy key. Its reply queue
may also be `*PGMQ` for the caller's program queue. Non-inquiry sends ignore the
reply queue and key output. Blank message ID selects immediate text of 1–6000
bytes; predefined sends require a real MSGF/description and at most 512 replacement
bytes. No missing-message fallback is fabricated. Optional input CCSID is supported.
QMHSNDRM applies stored validation/special mappings, accepts 1–132 bytes, uses a
blank reply for the stored default, and supports remove `*YES`/`*NO`. Its optional
groups are input CCSID and rejection-exit control (`*NO`). The queue may be `*EXT`.

Both receive APIs support RCVM0100/0200 and `*SAME`, `*OLD`, `*REMOVE`. SAME leaves
seen/exception state intact. OLD marks received; REMOVE deletes and applies an
unanswered inquiry's default in the same transaction. Receiver and error writes
are normalized before any send, removal, seen-state or reply mutation commits.
Rejected output preserves queues and all receiver storage. Bytes after the
returned prefix remain unchanged. Error structures use the bytes-provided,
bytes-available and seven-character ID contract described under data queues below.
Extended exception substitution data and full native error-ID parity remain open.

Receive selectors include ANY, FIRST, LAST, NEXT, PRV, INFO, COMP, DIAG, RPY and
COPY. Program queues also accept ESCAPE, NOTIFY and EXCP; named queues accept INQ.
Blank key selects a new message except FIRST/LAST, which traverse old and new.
FIRST/LAST require blank keys; NEXT/PRV require a key, including binary zero.
`*TOP` is valid with NEXT. Explicit missing keys fail with CPF2410. A keyed reply
can use its sender-copy key and can be reread after OLD. EXCP selects the newest
unhandled exception; program return types distinguish unhandled notify/escape
(16/17) from handled ones (14/15).

Wait is seconds: zero polls, positive BIN(4) values wait, and -1 waits until
cancellation or delivery. A waiting named receive allocates the queue to that job
and retains shared-read object/library leases. Other jobs can send but cannot
receive, remove or reply while allocated (CPF2451). Cancellation, ENDJOB and
terminal outcomes release the allocation. Named keyed nonreply receives and
unkeyed reply receives ignore the wait, following the native selection rules.
QMHRCVM optionally selects output CCSID and rejection control (`*NO`). Supported
text CCSIDs are those in the release contract; zero selects the job CCSID. Receive
65535 preserves source bytes; input CCSID 65535 is currently unsupported.

Offsets below are zero-based. All length/status fields are BIN(4).

| RCVM0100 offset | Field |
|---|---|
| 0, 4 | bytes returned, available |
| 8 | severity; unchanged receiver bytes for an immediate message |
| 12, 19, 21 | ID(7), return type(2), key(4); key is spaces after REMOVE |
| 25 | reserved(7) |
| 32, 36, 40, 44 | data conversion status, CCSID, returned length, available length |
| 48 | message data: immediate bytes or predefined replacement bytes |

RCVM0200 has a 176-byte header. Offsets 0–24 match RCVM0100. Message file,
requested library and actual library occupy CHAR(10) at 25/35/45. Sender job,
job user, number, program, instruction, CYYMMDD and HHMMSS occupy 55/65/75/81/93/97/104
(widths 10/10/6/12/4/7/6). Program receipt places recipient(10), instruction(4)
and program types(1 each) at 110/120/124/125. Named receipt instead places send
microseconds(6) and actual sender profile(10) at 110/116. Unknown instructions and
reserved fields are blank. Text/data conversion status is at 127/131, alert(9)
at 135, text/data CCSID at 144/148. Returned/available pairs for replacement data,
text and help are at 152/156, 160/164 and 168/172. Variable bytes follow in that
order at 176. Saved descriptions continue to work after the source MSGF is deleted;
actual library is blank when its original object no longer exists.

No matching message writes only returned=8, available=0. A receiver may be as
short as eight bytes; available describes the complete result. Conversion status
is 0 (unnecessary), 1 (not converted), 2 (space prevented conversion), 3 (converted)
or -1 (conversion failed, original bytes retained). Only *CCHAR replacement fields
are converted; other replacement data reports CCSID 65535. Valid UTF-8 text is
truncated at a character boundary; opaque replacement bytes can end at any byte.

QMHRTVM supports RTVM0100: returned/available at 0/4, text returned/available at
8/12, help returned/available at 16/20, then text/help at 24. Substitute and format
controls accept YES/NO. NO formatting strips the supported &N/&P/&B help markers.
The optional group selects MSGID/FIRST/NEXT, output CCSID and replacement CCSID;
FIRST/NEXT use message identifier ordering and return a blank receiver at end.
65535 output and richer RTVM/RCVM formats are outside this checkpoint.

```rpgle
**free
dcl-s KEY char(4) ccsid(*hex);
dcl-s INFO char(256) ccsid(*hex);
dcl-s SIZE int(10) inz(256);
callp QMHSNDPM('':'':'READY':5:'*INFO':'*':0:KEY:X'00000000');
callp QMHRCVPM(INFO:SIZE:'RCVM0200':'*':0:'*INFO':KEY:0:'*SAME':X'00000000');
if %subst(INFO:177:5) = 'READY';
  sndpgmmsg 'NATIVE MESSAGE RECEIVED';
endif;
seton LR;
```

MessageApiTests uses independent byte vectors, signed/high-bit keys, truncation,
conversion results, failed receiver preparation, atomic multicast/default replies
and allocated waits. MessageApiIntegrationTests compiles CL and direct RPG callers
for all six APIs at CCSIDs 37/1208, checks QTEMP isolation, authority/manifests,
real message-subfile output and ENDJOB cleanup. NativeMessageMigrationTests covers
schema-27 upgrade/rollback and deleted-key high-water preservation, including an
empty entry table. MessageSenderTests checks native CYYMMDD century boundaries.
SessionServerTests runs independent socket jobs through an allocated native
receive, conflicting receiver, native send and resource cleanup. The display
PTY exercises native RPG message production/receipt/retrieval with F1 help and
WRKAPILS option 5. Pointer/qualified ILE selectors, richer formats, 32767-byte
replacement data, rejection exits, delivery attributes and complete native
exception/UI semantics remain open.

Primary layout references: IBM's [QMHSNDPM](https://www.ibm.com/docs/en/i/7.4.0?topic=ssw_ibm_i_74%2Fapis%2FQMHSNDPM.html),
[QMHRCVPM](https://www.ibm.com/docs/en/ssw_ibm_i_74/apis/QMHRCVPM.htm),
[QMHSNDM](https://www.ibm.com/docs/en/i/7.4.0?topic=ssw_ibm_i_74%2Fapis%2FQMHSNDM.htm),
[QMHRCVM](https://www.ibm.com/docs/ja/ssw_ibm_i_74/apis/Qmhrcvm.htm),
[QMHSNDRM](https://www.ibm.com/docs/en/i/7.5.0?topic=q-send-reply-message-qmhsndrm)
and [QMHRTVM](https://www.ibm.com/docs/en/i/7.6.0?topic=q-retrieve-message-qmhrtvm).
The older [IBM Message Handling PDF](https://public.dhe.ibm.com/systems/power/docs/systemi/v5r2/en_US/messg.pdf)
provides stable base layouts; the current QMHRCVM definition supplies the actual
sending profile at offset 116 where the older layout reserved bytes.

## QCMDEXC

`CALL QSYS/QCMDEXC PARM('CHGCURLIB QGPL' 14)` executes one command using the same
dispatcher as terminal, CL and batch execution. The command sees the current job's
library list, identity, adopted authority, locks, execution budget and cancellation.
Commands and their processing programs retain live authority/signature checks.
Command failures preserve their message ID for CL MONMSG and exception receipt.
Nested dynamic calls retain the existing command/program recursion bounds.

| Parameter | Input contract |
|---|---|
| Command | CHAR storage in the executing job CCSID |
| Length | Packed DEC(15,5), an integer from 1 through 32702 within supplied storage |
| Optional IGC control | CHAR(3), uppercase `IGC` |

Direct and compiled CL calls use the same byte contract. Hex buffers can supply
independent packed lengths; malformed digits/signs and mismatched declarations
fail before dispatch. RPG scalar borrowing checks CHAR storage and DEC(15,5)
declarations on PLIST, unprototyped and supported scalar prototype calls;
DEC(15,0) is rejected even when its value is an integer. These interpreted
layouts do not certify a native compiled RPG ABI.
The typed host bridge also
accepts immutable ProgramBuffer inputs. Input storage is never written back.
Only the selected command bytes are decoded, so unused trailing storage may be
binary. UTF-8 lengths count bytes; splitting an encoded character fails. Buffers
with another CCSID fail rather than being silently transcoded. The supported
CCSIDs currently require no extra DBCS state for IGC.

This checkpoint accepts a single command line, rejects NUL/newline separators,
and does not implement interactive prompting, proxy commands, command exit points
or DBCS shift-state code pages. Commands returning a screen/menu/signoff request
fail with CPF0006; terminal screens are not driven by this API. Listing-producing
commands can return a normal command result to the host. CL-only declarations and
variable-receive commands still require their compiled CL context. The HTTP command bridge below can invoke CALL QCMDEXC; full domain REST and
debugger entry points remain pending their owning services.

QcmdexcTests covers direct/compiled CL and RPG callers, independent packed bytes,
CCSIDs 37/1208, malformed layouts, byte boundaries, unchanged inputs, maximum
length, MONMSG, authority/trust revocation, bounded recursion and cancellation.
The display PTY compiles CLAPI and checks a QCMDEXC call against RTVJOBA state.
The parameter reference is IBM's [Execute Command API](https://www.ibm.com/docs/en/i/7.5?topic=ssw_ibm_i_75%2Fapis%2Fqcmdexc.html).

## Local data-queue program APIs

These adapters operate on the durable STD queues in [data queues](data-queues.md).
They use the current job's library list, principal, adopted authority, object locks,
budget and cancellation. Both explicit names and *LIBL/*CURLIB resolve without
requiring read authority just to send. Queue access still uses the shared service:
send requires operate/add, destructive receive operate/read/delete, peek
operate/read, and clear operate/delete. These live checks intentionally differ
from IBM's cached authorization and native receive/clear authority requirements.

Names occupy CHAR(10); key order occupies CHAR(2); other controls occupy CHAR(10).
Only those fixed prefixes are decoded, in the executing job CCSID. Longer input
storage is permitted. Queue data and keys remain opaque bytes without conversion.
Packed DEC(5,0), DEC(3,0) and DEC(7,0) occupy 3, 2 and 4 bytes respectively.
Typed declarations must match the required precision and scale; raw CHAR/hex
storage must have exactly the packed field width and valid digits/signs. Direct
CL numeric constants default to DEC(15,5), so specify the API layout explicitly.

| API | Required arguments, in order | Accepted total counts |
|---|---|---|
| QSNDDTAQ | queue, library, DEC(5,0) data length, data | 4, 6, 7, 8 |
| QRCVDTAQ | queue, library, writable DEC(5,0) length, writable data, DEC(5,0) wait | 5, 10, 13, 14 |
| QCLRDTAQ | queue, library | 2, 6 |

QSNDDTAQ's optional pair is DEC(3,0) key length and key input. Parameters 7 and 8
are asynchronous-request and journal-entry controls; this checkpoint accepts only
*NO. Data length selects a positive input prefix up to 64512 bytes. Key length
must be zero for unkeyed queues, or match the keyed queue's declared length.
See IBM's [Send Data Queue API](https://www.ibm.com/docs/en/i/7.4.0?topic=q-send-data-queue-qsnddtaq).

QRCVDTAQ's first optional group contains key order, DEC(3,0) key length, writable
key, DEC(3,0) sender receiver length, and writable sender receiver. A zero key
length ignores key/order; a zero sender length ignores its receiver. The second
group contains *YES/*NO removal, DEC(5,0) data receiver size, and an error receiver.
The final argument selects *SEC or *MSEC. Wait zero polls once, any negative value
waits until receipt/cancellation, and positive values wait for the specified units.
All optional groups must be complete.

Without explicit data receiver size, an entry larger than actual storage raises
CPF9506 and stays queued. With explicit size, receipt copies that many bytes at
most; zero copies no data. Returned length always reports the full entry size.
Only received prefixes change; trailing bytes remain intact. Timeout changes only
the returned length to zero. Matching keyed receives return the selected entry's
actual key. All outputs are normalized before removal, including sender metadata
that cannot fit the native field widths. Checked failures and observed cancellation before commit preserve the
entry and output cells. Output aliases are rejected before receipt.

Sender receivers accept zero or 8–999 bytes. Headers at offsets 0 and 4 are packed
DEC(7,0) bytes returned/available. Identity fields start at offsets 8 (job name,
10 bytes), 18 (job user, 10), 28 (six-digit job number), and 34 (current sender,
10). Recorded sender data has 44 available bytes. Without retained sender metadata,
this implementation returns only the eight header bytes and preserves the tail.
Names use the receiving job CCSID; unrepresentable or oversized identities fail
before dequeue. Requested partial prefixes are supported.
See IBM's [Receive Data Queue API](https://www.ibm.com/docs/en/i/7.4?topic=ssw_ibm_i_74%2Fapis%2Fqrcvdtaq.htm).

QCLRDTAQ's optional group contains key order, DEC(3,0) key length, key input and
error receiver. Zero key length clears the entire queue, including keyed queues.
Positive length requires the declared queue key width and selects the comparison
range. See IBM's [Clear Data Queue API](https://www.ibm.com/docs/en/i/7.4.0?topic=q-clear-data-queue-qclrdtaq).

Error receivers begin with signed big-endian 32-bit bytes provided and bytes
available, then seven job-CCSID message-ID bytes and one reserved byte. Provided
zero propagates the exception (a four-byte literal zero is allowed). Otherwise,
provided must be 8 through actual writable storage length. Failure returns 16
available bytes, the message-ID prefix that fits, and reserved zero when it fits;
success sets available to zero. Bytes outside the supplied receiver remain intact.
Exception substitution data and extended error structures are not implemented.
An error receiver aliasing another argument is rejected without writing it.

```cl
PGM
DCL &DATA *CHAR LEN(80)
DCL &LENGTH *DEC LEN(5 0)
CALL QSYS/QSNDDTAQ PARM('WORK' 'QGPL' (5 (*DEC 5 0)) 'READY')
CALL QSYS/QRCVDTAQ PARM('WORK' 'QGPL' &LENGTH &DATA (0 (*DEC 5 0)))
CALL QSYS/QCLRDTAQ PARM('WORK' 'QGPL')
ENDPGM
```

Compiled CL, typed host ProgramArgument cells and the supported RPG scalar calls
provide writable receivers; immutable constants fail with CPF3C90. The RPG call
contract below includes direct receives without a CL wrapper. Invalid groups use
CPF3C36; checked layout/control
failures use CPF3C3A; queue failures preserve their shared service IDs. Exact IBM
error substitution data and all native error-ID distinctions remain open.

DataQueueApiTests includes independent packed/sender/error bytes, CCSIDs 37/1208,
truncation/peek/timeout, maximum entry lengths, unchanged tails, output atomicity,
MONMSG, authority/signing revocation and cancellation. Terminal acceptance compiles
CLDQAPI and verifies all three adapters with sender and error receivers. DDM,
journal replay, priority scheduling and address-cache behavior remain unimplemented.

## RPG scalar API calls

Fixed CALL/CALLB with PLIST, CALLP with a named PLIST, and unprototyped inline
CALLP/CALLB calls can borrow fixed CHAR, signed INT and packed/decimal fields. Procedure-pointer
targets use the same inline scalar path. Program lookup, authority and signing run
before borrowing; only built-in IPCAPI targets request typed cells. Ordinary CL,
RPG and external process targets keep their existing semantic argument/writeback
contract. RpgHost.ProgramCaller remains available for hosts using that contract;
TypedProgramCaller adds a lazy borrowing callback for hosts implementing typed APIs.

Repeated fields, including two RPG entry names referring to the same outer CL
cell, share one borrowed identity. The queue APIs can therefore reject overlapping
output receivers before dequeuing. Reads/writes are live during a synchronous call;
both cells and the lazy factory expire on return, failure or cancellation. Receivers
update storage directly; they are not copied through UpdatedParameters afterward.
An outer receiver's normalizer is checked during output preparation. At return,
outer character buffers detach into semantic text for retained RPG storage.

The adapter preserves the declared precision/scale and rejects fractional or
overflowing output values. Packed fields support up to .NET decimal's 28 digits.
CHAR requires both its character count and strictly encoded byte count to equal
the declared width. The job CCSID is read for each call. EBCDIC 37 permits binary
bytes that round-trip through the interpreter's text storage; UTF-8 receives must
also fit that storage without replacement or extra padding. Invalid UTF-8 and
multibyte output that would change the declared width fail before removal. Declare
CHAR CCSID(*HEX) or CCSID(65535) for arbitrary binary storage, including native
message headers. These fields remain ProgramBuffers: no text conversion occurs,
hexadecimal INZ must match the width, and %SUBST uses one-based byte positions.
Hex literals such as X'00000000' are immutable byte inputs at every job CCSID.
INT(3/5/10/20) borrows signed 1/2/4/8-byte big-endian storage with exact range and
integral-value checks; INT(10) satisfies native BIN(4) parameters. Raw API text such
as `*BLANK` is stored literally, without RPG assignment-keyword interpretation.

Named constants and evaluated expressions are immutable inputs. Expression values
are evaluated once. The original scalar path still excludes legacy text DS/overlays,
varying characters, zoned fields and RPG B-spec binary fields. New native DCL-DS
structures, contiguous arrays and typed subfields use the
[native byte-view path](rpg-native-structures.md#borrowed-api-views), including
zoned/float/default ISO datetime subfields.
Calls are bounded to 256 arguments and 1 MiB of aggregate storage. Scalar
prototype calls can also borrow receivers under the contract below. Full native
RPG storage, broader prototype modes and the C09/C14 contracts remain open.

RpgApiArgumentTests covers direct queue calls at CCSIDs 37/1208, real key/sender/
error outputs, peek/truncation/timeout, aliases and expired lifetimes, scalar shape
and quota failures, cancellation, outer receiver rejection, raw assignment-keyword
bytes, authority and CL MONMSG propagation. QCMDEXC's RPG fixture now declares
the required DEC(15,5); mismatched-scale coverage proves rejection before dispatch.
The terminal fixture's RPGDQAPI sends, receives and clears a keyed queue directly.

### Scalar prototype calls

CALLP/CALLB prototypes targeting built-in IPCAPI programs now pass the actual
caller's scalar fields to the same scoped adapter. Both formal and actual fields
must have identical CHAR byte widths/raw-storage mode, signed INT precision, or
packed precision/scale, with exactly one
actual argument per formal. Missing/ambiguous declarations and duplicate prototype
bindings fail before dispatch. Repeated actual fields retain one identity, so
native receiver overlap checks still apply. Signed INT and hexadecimal constants
retain their formal byte layouts. Prototype temporaries remain unchanged
while the API updates actual fields; no extra writeback follows a borrowed call.

In the documented emulator D-spec layout, PR selects EXTPGM/EXTPROC and explicit
A/P parameter rows use the PARM keyword. For example, this prototype accepts a
CHAR(10) queue/library, DEC(5,0) receiver length/wait, and CHAR(8) data receiver:

```text
      DGET           PR                           EXTPGM('QRCVDTAQ')
      DQUEUE         A                10          PARM
      DLIBRARY       A                10          PARM
      DLENGTH        P                5 0         PARM
      DTEXT          A                8           PARM
      DWAIT          P                5 0         PARM
```

These are the emulator's column conventions described in [RPG source columns](rpg-spec.md#3-source-columns-fixed-form).
Named formals receive private generated storage names and cannot shadow caller
variables with the same name. Multiple unnamed prototypes get independent storage;
the first legacy unnamed prototype retains P1/P2 names where available. This
checkpoint accepts fixed reference parameters with no options beyond PARM.
CONST, VALUE, OPTIONS and other parameter keywords are retained and rejected for
typed API borrowing. Whole-DS/array prototype formals, legacy DS/overlays, varying
fields and unsupported numeric types also fail; compatible scalar formals can now
borrow native subfields. These limits apply when a built-in target requests borrowing.

Literals and expression results become immutable ProgramConstant inputs with the
formal layout. Character values pad to that width without truncation; packed
values reject overflow or scale loss and use the shared record codec. The API
then checks the native signature as usual, including constants: a DEC(3,0)
prototype literal cannot satisfy a DEC(5,0) API parameter. Constants used as output
receivers fail with CPF3C90. Argument splitting respects nested expressions,
quoted colons and doubled apostrophes; expressions are evaluated once.

The host resolves program authority/signing before borrowing. Ordinary targets
keep their earlier copied formal values and UpdatedParameters behavior. Their
formal coercion is prepared lazily only when the host reads semantic arguments;
unused argument factories expire at call return. Native scalar receivers use
actual storage directly. Full ordinary-program reference binding, modules/service
programs, optional/value/const modes and complete native RPG ABI parity remain open.

RpgPrototypeApiTests covers QCMDEXC and the three queue adapters, independent
constant byte vectors, formal/actual mismatches, real key/sender/error receivers,
peek/truncation, immutability, aliases and expiration, ordinary fallback, signed
API trust revocation, authority/MONMSG and malformed expressions. Terminal
acceptance runs RPGPRAPI through prototyped keyed send/receive/clear.

## HTTP command bridge

Start `as400server`, then run:

```sh
dotnet run --project src/Ipc.Web -c Release -- --server /path/to/instance/run/as400.sock
```

The default development listener is `http://127.0.0.1:5080`. Configure Kestrel HTTPS
for remote access; non-loopback plaintext command requests are rejected. Authenticate
with HTTP Basic, then POST `/api/commands` with a JSON object containing `command`.
Do not put credentials in URLs. Example body:

```json
{"command":"SBMJOB CMD(CALL PGM(QGPL/REPORT)) JOB(REPORT)"}
```

Each request creates a server-owned session/job, executes through the same command
service as the terminal, and signs off. Responses contain `success`, `error`, `job`
and `result`; `result` uses the shared CommandResult contract. There is no client-
selected job/user field. Limits: 64 KiB HTTP body, 32768 command characters, 30 seconds.
Long-lived transactions and program parameter APIs are not exposed through this route.

Status: 200 success, 400 invalid/failed command, 401 authentication failure or required
password change, 426 remote plaintext rejected, 501 unavailable command, 503 unavailable
server, 504 timeout/cancel. Complete domain-specific error mapping remains C16 work.
`GET /health/live` reports the HTTP gateway's liveness, not database/server readiness.

## Local typed connection

`CommandConnection.ConnectAsync(socketPath, user, password)` consumes the terminal
protocol greeting and authenticates in command mode. `ExecuteAsync(command)` and
`SignoffAsync()` use that server-assigned identity. Callers serialize requests on each
connection and dispose it on failure/cancellation. Multiple clients get distinct jobs.
The wire format is length-prefixed JSON bounded to 256 KiB; the initial
`TerminalInput.CommandSession` selects command mode only before terminal sign-on.
Bad credentials, password expiry, identity replacement and malformed requests fail.

The server cancels interpreter loops when the connection closes. A disconnected job
ends abnormally. Shared service authority, PAM/SSO and audit are covered by C03 and the security
contracts. The complete domain REST surface remains C16 work.

`WebExecutionTests` uses a real HTTP listener and live Unix-socket server.
`SessionServerTests` proves terminal/headless catalog sharing and independent jobs.

The shared MFA and SSO endpoints, bearer authentication and Unix management protocol
are documented in [MFA and sessions](mfa-sessions.md).


[Native program signatures](rpg-program-signatures.md) now provide D-spec PR/PI
and free DCL-PR/DCL-PI for scalar references through EXTPGM, including borrowed
API receivers and exact-width native formats. Their formal metadata is separate
from runtime fields; the older project prototype convention above remains
supported. CONST/VALUE/OPTIONS and the complete native procedure/array ABI remain
open. Free CALLP(E) now shares the final-call error path with native C-specs.


[Native CONST parameters](rpg-const-parameters.md) now provide typed read-only
reference inputs and converted literal/expression temporaries. Native receivers
reject readonly storage before output preparation; queue rejection preserves
entries and other outputs. Character prefixes retain native byte ranges and
caller tails. Legacy project prototype options keep their separate contract.

[Optional native parameters](rpg-optional-parameters.md) now let a native
prototype describe required API inputs followed by OPTIONS(*NOPASS) formals.
Only the supplied prefix reaches the adapter; its native count/group validation
still applies. %PASSED/%PARMS guards and %PARMNUM identify native main PI inputs,
and forwarding an unpassed receiver fails with status 105 before API dispatch.

[Omitted native arguments](rpg-omitted-parameters.md) now preserve *OMIT positions
through native program prototypes and main interfaces. API adapters retain their
own storage requirements: omitted buffers/numbers/receivers reject, with queue
input/output tests preserving entries and other outputs. A prototype's *OMIT
option does not make every API argument nullable; additional per-API nullable
pointer contracts remain open.

[Native local procedures](rpg-local-procedures.md) can now call native API adapters
with automatic local receivers and CONST/VALUE inputs while sharing module
globals. Real queue tests verify local send/receive and borrowed-cell lifetimes;
external-process tests cover local output validation and valid error writeback.
[Native aggregate parameters](rpg-aggregate-parameters.md) also provide fixed
array/LIKEDS byte receivers through program and local procedure interfaces.
[Array and structure return values](rpg-return-values.md) can supply compatible
CONST API inputs through local procedures. Cross-unit binding, further return
layouts and foreign ABI remain open.
[Exact and adjusted RPG parameters](rpg-parameter-options.md) validate supported
actual types and prepare private trimmed/right-adjusted character images before
native dispatch. Readonly echoes and atomic output validation retain their ABI.

[Variable-size RPG references](rpg-variable-parameters.md) expose actual byte
capacities through scoped memory views; readonly status, alias identity and
complete response validation remain enforced across native API/process calls.
