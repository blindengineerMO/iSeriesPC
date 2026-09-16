# CL runtime checkpoint

C07's interpreter item remains open. The current runtime executes bounded scalar
expressions, structured control flow, scalar argument buffers, reference writeback,
scoped escape monitors, and sequential database file reads. Display file I/O,
wider message delivery and procedure/external bindings still need completion.

`IF COND(...) THEN(command)` and `ELSE CMD(command)` support nested commands and
DO groups. `DO` groups commands, `DOWHILE` checks before the body, and `DOUNTIL`
checks after it. `DOFOR` uses a declared INT/UINT variable, evaluates FROM once,
and reevaluates TO after each increment; BY is a constant integer, including zero.
`LEAVE` and `ITERATE` target the closest iterative group or a labelled enclosing
group. SELECT executes the first true WHEN, including a WHEN with no action;
OTHERWISE is optional. RETURN exits the current invocation. The existing
IF/ELSE/ENDIF and ENDSL spellings remain extensions. Trailing `+` continues a
command and removes the next line's leading spaces; `-` preserves those spaces.

Expressions compile once. Arithmetic uses CLR decimal with checked overflow;
comparison uses numeric ordering for numeric operands and padded job-CCSID bytes
for text. AND/OR short-circuit; this is the current runtime policy. CAT/TCAT/BCAT,
NOT, parentheses, and quoted strings with doubled apostrophes are supported.
Text `+` remains an extension. CLR decimal division has finite precision; this
checkpoint does not claim the native compiler's full intermediate-precision rules.

DCL supports CHAR (1–32767 bytes; initial VALUE is limited to 5000 characters), DEC (1–15 digits, up to 9 decimal places),
INT/UINT (2 or 4 bytes) and LGL. CHAR assignments pad or truncate on the right;
a text truncation splitting a multibyte character fails. Opaque ProgramBuffer
arguments and assignments preserve bytes; nested aliases, calls, concatenation
and comparison do not force binary content through text decoding. Text operations
on invalid encoding raise a monitorable error. Numeric-to-CHAR conversions
pad with leading zeroes and put a negative sign first. Character-to-DEC conversion
truncates fractional positions; numeric assignments exceeding declared precision
or scale fail. Untyped DCL and undeclared CHGVAR targets remain legacy string
extensions. Hex constants preserve exact bytes, including invalid text encodings;
inferred CHAR lengths count encoded bytes. Pointer/based storage remains open.

Limits are 25 nested DO groups, 64 nested control constructs/expression levels,
4096 expression tokens, 32768 characters per continued command/expression, 100000
lowered instructions, and 20 active CL calls. Every executed instruction checks
cancellation and the job execution budget. Errors retain original source/include
locations. Unsupported constructs fail at compile time or through the command
catalog; they do not gain compatibility status from parsing alone.

Validation: 1187 tests, a warning-free Release build and full display PTY pass. ClControlFlowTests
covers ascending/descending/empty loops, changing TO, zero BY, nested labelled
exits, SELECT, IF/ELSE binding, typed values, errors, continuations and cancellation.
ClExpressionTests supplies independent expression cases. The display PTY compiles
and calls CLFLOW through CRTCLPGM, verifying typed arithmetic, DOFOR, ITERATE and
SELECT against the expected result.

`%SST`/`%SUBSTRING` read or assign a byte range in a declared CHAR variable or
the job's 1024-byte `*LDA`. Positions are one-based; position and length must be
positive integers within the buffer. Substring assignments preserve neighboring
bytes, pad with job-CCSID blanks, and may split a multibyte character. The right
side is evaluated before an overlapping write. `%BIN`/`%BINARY` read and assign
signed, big-endian two- or four-byte fields in CHAR storage. Omitting position
and length selects the whole variable, which must have one of those widths;
an explicit length must be the constant 2 or 4. Writes truncate fractional
numeric values and reject overflow with MCH1210 before changing storage.
These functions work in expressions, CHGVAR targets and CALL expression
temporaries; general command expression metadata remains open.
ClByteFunctionTests checks independent signed byte fixtures, overlap, invalid
ranges, atomic failures, bounded parsing and real job-local storage at CCSIDs
37 and 1208. See IBM's [substring](https://www.ibm.com/docs/en/i/7.6.0?topic=procedure-substring-built-in-function)
and [binary](https://www.ibm.com/docs/en/i/7.4.0?topic=procedure-binary-built-in-function)
function contracts.

References: [IBM DOFOR](https://www.ibm.com/docs/en/i/7.5.0?topic=d-do),
[LEAVE](https://www.ibm.com/docs/en/i/7.5.0?topic=procedure-leave-command-in-cl-program),
[CHGVAR](https://www.ibm.com/docs/en/i/7.4.0?topic=c-change-variable), and
[SELECT](https://www.ibm.com/docs/nl/ssw_ibm_i_74/rbam6/selectcmd.htm).


## Parameters and escape handling

RunWithArguments accepts semantic scalars or immutable ProgramBuffer inputs and
returns updated parameters. Typed buffer lengths must match DCL. DEC uses packed
sign/digit nibbles, INT/UINT use big-endian 2/4-byte storage, and CHAR/LGL use the
buffer CCSID. Invalid signs/digits/padding/lengths fail before execution. Returned
buffers use the executing job CCSID. CL CALL/PGM support at most 255 parameters and
1 MiB of incoming argument data. CL reference parameters share cells, including
aliases passed more than once; updates survive callee errors. Declared reference
types and lengths must match. Constant arguments use private temporary storage.
RPG-to-CL calls receive semantic writeback through the existing RPG host result;
CL-to-RPG scalar references and CL-to-native version-2 buffers now write back.
CALLPRC and procedure bindings remain open.

CALL character constants and character expressions pass at least 32 bytes;
longer values retain their encoded byte length. Numeric constants default to
packed DEC(15,5). Hex constants pass their exact bytes and can initialize a
different receiver layout. A shorter CHAR receiver reads its prefix; a larger
receiver or incompatible numeric declaration fails before the callee executes.
Explicit attributes such as `PARM((25.5 (*DEC 5 2)))` create a temporary with the
specified layout. CHAR supports 1–32767 bytes, DEC up to 24 digits/9 decimals,
INT/UINT 2/4/8 bytes, LGL one byte, and FLT IEEE big-endian 4/8 bytes. Eight-byte
integer CALL temporaries are available even though OPM-style DCL INT remains
limited to four bytes. Numeric fractions and overlong character values truncate
when assigned to explicitly sized temporaries; overflow fails before dispatch.
Expressions compile with the program and cannot write back into their operands.
A plain variable retains its live reference even when attributes accompany it.

Direct and compiled CALLs use the same constant layouts for CL, supported RPG
scalar receivers and native protocol 2. Native protocol 1 retains its documented
semantic scalar adapter. Untyped CL receivers retain the legacy scalar extension;
raw hex buffers remain byte-preserving. Null CALL parameters are rejected. The
1 MiB aggregate argument limit also applies to nested and host calls. Batch routing
uses the shared typed program entry within the same identity, accounting, budget
and lock scope, preserving the submitted command without rebuilding a CALL string.
ClCallConstantTests and ExternalProgramTests supply independent bytes, native
process captures, declaration failures, expression isolation and response validation.
See IBM's [CALL parameter attributes](https://www.ibm.com/docs/en/i/7.5.0?topic=ssw_ibm_i_75%2Fcl%2Fcall.htm)
and [parameter passing rules](https://www.ibm.com/docs/en/i/7.5.0?topic=pp-using-call-program-command-pass-control-called-program).

MONMSG supports command and program scopes, exact IDs and two/four trailing-zero
generic IDs, and constant CMPDTA prefixes up to 28 encoded bytes. Command monitors
precede program monitors; this implementation selects the first matching monitor
within a scope. Limits are 50 IDs per monitor, 100 monitors per scope and 1000 per
program. Recovery can use command groups, GOTO or RETURN; program-level EXEC is
restricted to GOTO. No-action handlers continue after the failed command, treating
a failed IF condition as false. Invalid placement and parameters fail compilation.

SNDPGMMSG supports immediate INFO/COMP/DIAG output and predefined MSGID/MSGF
messages with raw MSGDTA and durable definition snapshots. MONMSG text/hex CMPDTA
compares raw replacement prefixes independently of formatted text. MSGTYPE(*ESCAPE) ends the sender and propagates
to its caller; it bypasses the sender's own monitors. IDs and substitution data
remain separate from source-location text across calls. Arithmetic overflow and
zero division identify MCH1210/MCH1211. Named and program queues support inquiry/
reply, sender-copy keys, RCVMSG, atomic removal and operator screens; see
[message queues](message-queues.md). Runtime arithmetic/command failures enter the
current program queue before MONMSG recovery. Unmonitored failures propagate to
each caller, preserving their original queued bytes and recording one delivery
per frame. Explicit SNDPGMMSG escapes retain their original delivery key, avoiding
a duplicate when the caller receives them. Returned-frame exceptions remain in
DSPJOBLOG until removed. Host diagnostics use valid UTF-8 bounded to 4096 bytes;
RCVMSG performs requested conversion. A full/revoked queue produces a monitorable
delivery error without recursive error reporting. Advanced message formats, reply
validity, overrides and status/notify/break delivery remain open, as do ILE
exception-handler semantics.

ClParameterTests supplies independent packed/integer/logical/character bytes,
aliased references, error writeback, counts/signatures, variable program targets
and a real session RPG→CL→RPG update. ClMonitorTests covers recovery precedence,
blocks, nested handlers, ignored errors, generic IDs/data, escape propagation,
RETURN, bounds and cancellation. The PTY acceptance adds parameter writeback and
CRTCLPGM→CALL→nested escape→MONMSG data match→RETURN.

The full regression run exposed an intermittent HTTP signoff failure. The final
acknowledgement now uses the connection cancellation token because ending the job
can cancel/dispose the job token. Repeated authenticated HTTP command/signoff
cycles, the session-server suite and all 848 tests pass after the fix.

Message references: [MONMSG scope and restrictions](https://www.ibm.com/docs/en/i/7.5.0?topic=m-monitor-message),
[identifier and comparison-data rules](https://www.ibm.com/docs/en/i/7.4.0?topic=ssw_ibm_i_74%2Fcl%2Fmonmsg.html),
[escape messages](https://www.ibm.com/docs/en/i/7.6.0?topic=program-escape-notify-messages).


## Database files and sequential cursors

DCLF compiles up to five database file declarations into typed `&FIELD` or
`&OPNID_FIELD` variables. CRTCLPGM persists the source hash, file bindings and field
layout signature. Loading the program validates this metadata; RCVF checks live
file authority and layout when it opens the file. A changed layout raises CPF4131
instead of silently rebinding the compiled variables. Compatible OVRDBF targets,
member selection and CL-to-CL SHARE(*YES) paths use the job call environment.
The default member is the first created member.

RCVF opens lazily and reads a physical file or a supported single-format logical
file in access-path order. EOF raises CPF0864 and preserves the previous variables;
further reads also report EOF. CLOSE permits reopening from the beginning and is
harmless before opening. Call return and error unwinding release handles. Nullable
fields supply default CL values; ALWNULL(*NO) also raises CPF0886 after assignment.
DCLBINFLD(*INT) maps eligible binary fields to INT; default DEC declarations report
CPF0863 when a binary value exceeds their decimal range. ISO date, time and
microsecond timestamp fields retain their external character layouts.

DatabaseRecordCursor retains only the last sort position. Each read uses a bounded
keyset query, locks the candidate record and checks it again before returning it.
No SQLite reader or transaction survives a read, and the row lock is released
before returning to CL. File allocations last until close. Reads check the owning
job/profile, current authority, layout, cancellation and exact decimal/CCSID key
ordering. NULL ordering and member/RRN tie-breakers make duplicate-key traversal
deterministic. Reads observe committed future rows; this is a live cursor, not a
snapshot. It supports at most 128 sort terms. SQLite's progress handler interrupts
executing queries and is removed before connection reuse; lock busy-waits have
separate timeout behavior.

ClDatabaseFileTests covers compiled-layout changes, nullable and binary values,
five independent open identifiers, override sharing, cleanup, invalid metadata,
EOF and CLOSE/reopen. SequentialCursorTests covers lazy validation, live changes,
nullable keys in both directions, member ties, competing locks, authority changes,
cancellation and 8,300 reads within one command without exhausting the 8,192-lock
budget. SqliteCancellationTests cancels an executing recursive query and verifies
that the connection remains usable. Terminal acceptance compiles and calls CLREAD,
monitors EOF and reopens the file before checking its data.

Call/job cleanup attempts every open-path and frame close even if one fails, then
reports the first failure. Failed closes remove cached handles, and callers regain
their parent frame. Out-of-order unwind attempts can be retried in the correct order.
JobEnvironmentTests verifies remaining resource release, inactive message queues,
reopening after failure and repeated disposal without affecting another job.

ALWVARLEN(*YES) exposes a two-byte big-endian data length followed by the maximum
field payload padded with job-CCSID blanks. Numeric fields exceeding 15 digits map
to CHAR buffers containing packed decimal, including zoned/binary source fields.
Their size is integer(digits/2)+1. Independent reference bytes cover EBCDIC/UTF-8,
negative/even-digit numerics and null defaults. Binary values must still fit their
declared decimal digit count. CPI0306 listing warnings are not emitted yet.

Display DCLF/RCVF, backward/update cursors, RPG/CL shared positions and OPNQRYF
remain open. Floating-point database fields are rejected by CL DCLF.

References: [DCLF](https://www.ibm.com/docs/en/i/7.5.0?topic=d-declare-file),
[RCVF](https://www.ibm.com/docs/en/i/7.5.0?topic=r-receive-file),
[CLOSE](https://www.ibm.com/docs/en/i/7.5.0?topic=c-close-database-file), and
[Microsoft SqliteCommand.Cancel](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqlitecommand.cancel?view=msdata-sqlite-9.0.0).


## Cross-language call checkpoint

A synchronous ProgramArgument cell carries scalar type/length, a checked setter
and an optional exact byte buffer. CL passes shared cells to RPG entry parameters;
aliases observe updates immediately, including changes before a callee error.
RPG validates entry count and supported scalar shapes, then releases references
on return or error so retained activation storage cannot keep a caller's cells.
The scalar bridge supports CHAR, packed/decimal, eligible signed integers and
logical fields; arrays, structures and other layouts remain explicit errors.

Registered native programs receive typed CL variables through the existing
version-2 buffer protocol. The entire returned parameter array is validated before
any caller variable changes. A valid failure response writes back before MONMSG
handles its error. Variable program names resolve before dispatch; quoted text
continues to retain spaces and apostrophes. Native aliased arguments are rejected
before launching because the current process protocol does not express shared
storage. Constants retain the existing semantic extension rather than claiming
native minimum-32-character and packed(15,5) storage.

ClExternalCallTests checks live aliases, buffer bytes, RPG success/error writeback,
entry shape/count failures and reference cleanup across retained calls.
ExternalProgramTests launches actual processes and covers successful/error buffer
updates, malformed-output atomicity and alias rejection before side effects. All
887 tests pass; Release builds without warnings. Terminal acceptance compiles a
CL caller, calls RPG, and confirms the changed packed parameter back in CL.


## Job environment commands

ADDLIBLE supports FIRST/LAST/BEFORE/AFTER/REPLACE positions; RMVLIBLE removes user
entries. CHGLIBL supports SAME/NONE user lists and SAME/CRTDFT current libraries.
CHGCURLIB and explicit profile CRTDFT preserve the absence of a current entry;
explicit CURLIB object creation still defaults to QGPL. Unset legacy profiles keep
the existing QGPL default. Service validation checks every library's USE authority
before changing the persisted job or its in-memory list. Invalid lists and denied
libraries leave both components unchanged. The user list remains bounded to 250.

RTVJOBA writes JOB, USER, NBR, CURUSER, CURLIB, USRLIBL, SYSLIBL and CCSID to typed
CL variables. Library entries occupy ten bytes plus a separator; USRLIBL requires
2750 bytes, SYSLIBL 165 and CCSID DEC(5,0). NBR rejects job IDs beyond the native
six-character range. Unsupported attributes fail explicitly. Command-object
lookup, authority, signing and shadowing still apply to retrieval commands.
Direct command entry cannot supply a CL variable frame.

RTVDTAARA reads LDA/GDA/PDA selections, including nested start/length variables,
with typed return assignment. Short CHAR outputs fail instead of truncating; LGL
accepts a single 0/1 character. CHGDTAARA uses the same byte-range parser. All
return variables are validated before the first is changed. Named data areas are
implemented; see [data areas](data-areas.md). Other job attributes remain open.

Named SNDMSG/RCVMSG/SNDRPY use the durable queue service and preserve four-byte keys
through CL character variables. See [message queues](message-queues.md) for the
implemented selectors, return layouts, transaction behavior and remaining gaps.

All 900 tests pass and the Release build has no warnings. ClEnvironmentCommandTests
covers positions, current-library defaults, failure atomicity, library and command
authority, typed retrieval and byte ranges. Terminal acceptance compiles CLENV and
checks RTVJOBA and CHGDTAARA→RTVDTAARA results.

References: [ADDLIBLE](https://www.ibm.com/docs/en/i/7.6.0?topic=beginning-add-library-list-entry),
[CHGLIBL](https://www.ibm.com/docs/en/i/7.5.0?topic=ssw_ibm_i_75%2Fcl%2Fchglibl.html),
[RTVJOBA](https://www.ibm.com/docs/en/i/7.6.0?topic=r-retrieve-job-attributes), and
[RTVDTAARA](https://www.ibm.com/docs/en/i/7.5.0?topic=r-retrieve-data-area).


## Command arguments

CL resolves padded variables in each qualified-name component, individual list
elements and nested lists. Values containing spaces, quotes or parentheses remain
one scalar; quoted source text is not interpolated. Missing variables and invalid
qualified components fail before dispatch. Expanded commands are bounded to 32768
characters and list nesting to 64 levels. CALL program targets, data-area names
and dimensions, and message destinations use the same resolver. CHGLIBL rejects
a single variable containing multiple space-separated library names. The existing
single-variable qualified CALL target remains an extension. General command
expression/EXPR metadata is still pending. ClCommandArgumentTests covers typed
CMD/CPP case and quote preservation, lists, failures and real service commands.
Terminal CRTCLPGM→CALL verifies qualified data-area names and a dimension variable.
See IBM's [list and qualified-name variables](https://www.ibm.com/docs/en/i/7.5.0?topic=commands-variables-use-specifying-list-qualified-name).


QSYS/QCMDEXC now executes dynamic command strings through the shared dispatcher.
Direct and compiled CL callers pass CHAR bytes and packed DEC(15,5) lengths;
RPG has a semantic adapter. Live authority/signatures, job state, cancellation,
recursion limits and command escape IDs are retained. See [API contracts](api.md)
for exact layouts and the remaining prompting/API limitations.
