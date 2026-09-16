# Message descriptions

Message files now store versioned descriptions shared by display validation and
CL retrieval and queued program messages. New descriptions have a first-level template (1–132 characters),
second-level template (up to 3000), severity (0–99), substitution fields, CCSID and
optional default reply (up to 132 encoded bytes). Files contain at most 4096
entries and 8 MiB of serialized metadata. New identifiers follow the native
letter/alphanumeric prefix and four hexadecimal suffix positions. See IBM's
[identifier rules](https://www.ibm.com/docs/en/i/7.5.0?topic=file-assigning-message-identifier)
and [description fields](https://www.ibm.com/docs/ssw_ibm_i_74/rzajq/rzajqviewmessagefiledata.htm).

The previous version-1 text dictionary is read without modifying the catalog.
Its next successful change writes version 2, retaining old text, identifiers and
literal ampersands, including the earlier 512-character extension. New text uses
the native limit. Unknown versions, duplicate JSON properties, unknown properties,
invalid layouts and oversized payloads fail before mutation. Stored snapshots do
not expose mutable field lists. The object payload upgrade needs no migration. Catalog schema 23 adds durable
predefined-message snapshots to queue entries.

| Command | Current contract |
|---|---|
| CRTMSGF | MSGF, TEXT, CCSID; supported concrete job code pages |
| ADDMSGD | MSGID, MSGF, MSG, SECLVL, SEV, FMT, DFT |
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
conversion of character replacement data. UTC/date/pointer formats, reply-validity
rules, dump/default programs, alert/problem-log metadata and overrides are still
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
Operator second-level presentation, richer formats, reply validity, overrides and
the broader C07/C11 delivery requirements remain open.
