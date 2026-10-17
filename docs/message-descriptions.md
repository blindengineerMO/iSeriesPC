# Message descriptions

Message files now store versioned descriptions shared by display validation and
CL retrieval and queued program messages. New descriptions have a first-level template (1–132 characters),
second-level template (up to 3000), severity (0–99), substitution fields, CCSID and
optional default reply (up to 132 encoded bytes). Files contain at most 4096
entries and 8 MiB of serialized metadata. New identifiers follow the native
letter/alphanumeric prefix and four hexadecimal suffix positions. See IBM's
[identifier rules](https://www.ibm.com/docs/en/i/7.5.0?topic=file-assigning-message-identifier)
and [description fields](https://www.ibm.com/docs/ssw_ibm_i_74/rzajq/rzajqviewmessagefiledata.htm).

Version-1 text dictionaries and version-2 descriptions are read without modifying the catalog.
Their next successful change writes version 3, retaining old text, identifiers and
literal ampersands, including the earlier 512-character extension. New text uses
the native limit. Unknown versions, duplicate JSON properties, unknown properties,
invalid layouts and oversized payloads fail before mutation. Stored snapshots do
not expose mutable field or reply-rule lists. Queued snapshot version 2 adds reply rules;
version-1 snapshots remain readable. The payload upgrade needs no migration. Catalog schema 23 adds durable
predefined-message snapshots to queue entries.

| Command | Current contract |
|---|---|
| CRTMSGF | MSGF, TEXT, CCSID; supported concrete job code pages |
| ADDMSGD | MSGID, MSGF, MSG, SECLVL, SEV, FMT, DFT, TYPE, LEN, VALUES, SPCVAL, RANGE, REL |
| CHGMSGD | Same description fields; omitted fields or unquoted *SAME retain values |
| RMVMSGD | MSGID (one identifier or *ALL), MSGF |
| DSPMSGD | MSGID (one identifier or *ALL), MSGF; text listing |
| DLTMSGF | MSGF |
| RTVMSG | Compiled CL retrieval; MSGID, MSGF, MSGDTA, MSG, MSGLEN, SECLVL, SECLVLLEN, SEV, CCSID, MDTACCSID, TXTCCSID, DTACCSID |

Native positional ordering is accepted for the documented leading parameters.
SECLVL(*NONE) clears help text; DFT(*NONE) clears the default. Changes validate the
whole resulting description inside one transaction. Adds require use/add, changes
use/update, and removal operate/delete authority. File/library checks and job
allocation locks remain live. Duplicate/missing entries and denied mutations
leave the file unchanged. Listings use message ordering with letters before digits.

## Replacement fields and retrieval

FMT accepts up to 99 fixed CHAR, quoted CHAR, hexadecimal, packed decimal and
signed/unsigned binary fields. CHAR/QTDCHAR/HEX also accept *VARY with a two- or
four-byte signed big-endian length prefix; CCHAR requires a varying prefix.
Binary widths are 2/4/8 bytes. The current packed subset is up to 24 digits and
9 fractional positions. The concatenated replacement buffer is bounded to 512
bytes. Short fields become empty; negative varying lengths and invalid packed
signs/digits fail. Repeated substitutions read the same field and never interpret
replacement contents as further substitutions. These layouts follow IBM's
[ADDMSGD formats](https://www.ibm.com/docs/fi/i/7.4.0?topic=ssw_ibm_i_74%2Fcl%2Faddmsgd.html).

CHAR preserves original bytes and trims trailing blanks; QTDCHAR adds apostrophes;
HEX produces X'…'. Only CCHAR replacement bytes are converted between CCSIDs.
Templates and numeric formatting use the requested output CCSID. *HEX suppresses
conversion of character replacement data. UTC/date/pointer formats,
dump/default programs, alert/problem-log metadata and overrides are still
unsupported. Help formatting controls are retained in text; full terminal wrapping
and indentation remain open. Predefined inquiries snapshot the description default
reply at send time; later edits do not change their default.

RTVMSG first validates all targets and formats the complete result, then assigns
outputs. MSG and SECLVL accept CHAR storage, truncate by bytes and pad with blanks.
MSGLEN/SECLVLLEN return the available encoded lengths before truncation; these and
CCSID outputs require DEC(5,0), while SEV requires DEC(2,0). No output changes on
bad declarations, malformed replacement data or failed conversion. Returned bytes
can include unconverted binary CHAR data. DTACCSID is 65535 when no CCHAR field
exists; otherwise it describes that field's encoding. See IBM's
[RTVMSG contract](https://www.ibm.com/docs/en/i/7.4.0?topic=r-retrieve-message).

MessageDescriptionTests supplies independent packed/binary reference bytes,
CCSID 37/1208 cases, varying and short fields, raw data, invalid metadata,
concurrent mutations, authority and legacy upgrade tests. CL and real terminal
fixtures retrieve negative packed values into first-/second-level text and check
severity. PredefinedMessageTests covers immutable queue snapshots across edits,
delete/recreate and disk restart, raw MONMSG comparisons, separate CCHAR conversion,
message-file authority, inquiry defaults, migration and metadata quota accounting.
Terminal acceptance also sends a predefined escape across a call, matches packed
replacement bytes in MONMSG and receives text, help, replacement and severity.

SNDPGMMSG resolves MSGID/MSGF variables and library lists with live command and
message-file authority. Each queued message retains the definition, raw replacement
bytes, requested file library and original file identity. RCVMSG formats the saved
first/second-level templates in the requested CCSID and returns replacement data
separately; only CCHAR replacement fields convert, with varying lengths rewritten.
Conversion and return-layout failures leave the message unconsumed. Delivered
messages remain readable after the original file is edited, deleted or access is
revoked. SNDMSGFLIB is blank if the original path/creation identity no longer exists.

The existing CPF9898 adapter remains available when no real QCPFMSG file exists;
it supplies immediate text without a predefined snapshot. MONMSG text and hex
CMPDTA compare raw substitution prefixes, independently of formatted display text.
Operator second-level presentation, richer formats, reply-handling exits, overrides and
the broader C07/C11 delivery requirements remain open.


## Inquiry reply rules

ADDMSGD/CHGMSGD accept TYPE, LEN, VALUES, SPCVAL, RANGE and REL. TYPE supports
CHAR, DEC, ALPHA and NAME; explicit NONE requires LEN(*NONE) and cleared constraints
and default. LEN accepts *TYPE or a length and optional decimal positions. Decimal
replies support 15 digits and nine fractional positions; LEN without a scale
uses zero. Lists and special mappings allow at most 20 entries of 32 encoded bytes.
VALUES, RANGE and REL are mutually exclusive. See IBM's
[reply-validation overview](https://www.ibm.com/docs/en/i/7.4.0?topic=file-specifying-validity-checking-replies).

CHGMSGD applies omitted/*SAME fields to the current description inside its write
transaction. TYPE changes require LEN; incompatible retained fields reject the
entire change. DSPMSGD lists the saved rules. Earlier descriptions retain their
unchecked behavior until rules are explicitly configured.

Explicit replies use the sent snapshot, even after the original file is edited,
deleted or revoked. Matching special values map before type/length checking and
bypass VALUES/RANGE/REL. Default replies satisfy type/length independently of the
selection constraints. Removing an unanswered inquiry uses its saved default;
without one, the existing empty system-default response is retained.

The current character contract measures encoded bytes, uses message-file CCSID
collation and trims trailing padding blanks. ALPHA/NAME use ASCII letters/digits;
all-letter explicit NAME replies become uppercase. Decimal syntax uses an optional leading
sign and dot, exact comparison, and rejects exponent/grouping notation. Character
range/relation comparisons pad or truncate to the boundary width while preserving
the full accepted reply. Mapped text must fit the replying buffer's CCSID. Explicit
NONE and older descriptions preserve opaque reply bytes.

SNDRPY and the DSPMSG option-2/F4 flow share transactional validation. CPF2422
leaves the inquiry, seen/replied flags and sender copy unchanged. Success returns
RCVMSG RTNTYPE 22 for checked replies; unchecked replies retain 21 and defaults
retain 23/24. Reply exits, notify-reply delivery and native predefined-reply
65535 handling remain open; this runtime uses each ProgramBuffer's concrete CCSID.
See IBM's [send-reply contract](https://www.ibm.com/docs/en/i/7.4.0?topic=s-send-reply).

MessageReplyTests covers independent numeric and EBCDIC comparisons, CL monitoring,
special mappings, atomic changes/replies, immutable snapshots, legacy payloads,
authority, encoding failures, default removal and restart after file deletion.
The display PTY fixture rejects a bad operator reply, cancels/reopens F4, sends a
mapped reply and receives its checked return code through compiled CL.

## Private message files

QTEMP message files support create, add/change/remove/list, snapshots, copy/export
and job cleanup. Their description payload keeps the existing format; sent
predefined snapshots use version 3 to retain a private-source marker. Queued text,
help, replacement bytes and reply validation survive source edits/deletion and
job end. Public metadata uses QTEMP; SNDMSGFLIB becomes blank when the original
source object disappears. Legacy snapshot versions 1/2 remain readable and their
private names are sanitized across cleanup. See
[private messaging](message-queues.md#job-private-queues-and-message-files) for
reply routing, lifecycle and acceptance evidence.
