# Release compatibility contracts

These are release requirements, not claims about the current build. The [matrix](compatibility-matrix.md)
tracks implementation separately. This document resolves C01's subset boundaries. No required
work package or PLAN2 gap is deferred. Optional inbound SOAP is included in the release contract.
The exclusions already recorded in [release scope](release-scope.md) remain the only broad exclusions.

## Names, aliases, and diagnostics

The machine-readable catalog is [catalog.json](../src/Ipc.Cl/Compatibility/catalog.json).
It distinguishes IBM names, application extensions, legacy aliases, corrected planning spellings,
and rejected brainstorming placeholders. References establish names and intended operations;
they do not certify our implementation. A command/API being listed never makes it executable.
The generated matrix includes each catalog entry and its delivery status.

Only `DSPMENU → GO`, `MSG → DSPMSG`, and the previously shipped typo `RMVFM → RMVM` are accepted
legacy aliases. Aliases pass through the same parameter validation and authority checks as their
canonical operation. Other corrections are diagnostic suggestions, not additional executable names.
For example, `QUSCRTDQ` in the old plan denotes creation using the `CRTDTAQ` command; it is not an
invented IBM API. `RSTSECDTA` denotes the two operations `RSTUSRPRF` and `RSTAUT`, not a single alias.
`WRKNETSVR`, `WRKSECLST`, `DSPTCPTBL`, `DLTMNU`, `QSHC`, `CRTHTTPC`, `STRDOWN`, `WRKAPILS`,
`CRTUSRSPC`, `SNDDTAQ`, `RCVDTAQ`, and `ADDSRCPFM` are explicitly application-defined surfaces.
Their intended service operations remain required; IBM spelling parity is not claimed for them.

Required failure behavior before any side effect:

| Code | Meaning | Current enforcement |
|---|---|---|
| IPC0001 | Unknown command | Shared command catalog |
| IPC0002 | Catalogued operation unavailable in this build | Shared command catalog |
| IPC0003 | Unsupported parameter or positional shape | Registered built-in command contracts |
| IPC0004 | Provisional spelling; canonical suggestion supplied | Shared command catalog |
| IPC0005 | Malformed syntax, quote, parentheses, duplicate keyword | Command parser/catalog |
| IPC0006 | Unsupported language/DDS/API syntax, keyword, format or option | Required compiler/API contract; C05/C09/C14 implementation remains open |

IPC codes are application diagnostics, not invented IBM CPF equivalents. Native CPF IDs are used
only where the failure semantics have been established. Compile errors must include original
member/path, line, column, token and include stack. Unknown keywords/opcodes must never become
no-ops. API unsupported format/length failures must not write beyond the receiver or change state.
REST maps invalid input to 400, unsupported operations to 501, forbidden operations to 403, and
missing resources to 404 subject to the authorization policy; response bodies carry a correlation
ID and stable diagnostic code. No stack traces or secrets appear in client diagnostics.

Current command keyword lists describe the implemented subset, including existing extensions
such as compiler `LIB`/`SRCSTMF`; built-in adapters are now persisted alongside typed user command definitions.
Repeated keywords fail; multiple-value parameters use one keyword with a validated list
or a parenthesized positional list. Remaining built-in semantics belong to C07 command categories. Quoted values retain whitespace, parentheses, and doubled apostrophes.

## CL

Required: `PGM/ENDPGM`, typed `DCL` and parameters (`*CHAR`, `*DEC`, `*LGL`, `*INT`, `*UINT`),
`CHGVAR`, arithmetic/relational/boolean expressions, `IF/ELSE`, `DO/ENDDO`, `DOWHILE`, `DOUNTIL`,
`DOFOR`, `LEAVE/ITERATE`, `SELECT/WHEN/OTHERWISE/ENDSELECT`, labels/GOTO, RETURN, CALL/CALLPRC,
MONMSG with scoped escape handling, message send/receive, file declarations and record I/O,
overrides, library lists, data areas and commitment control. The catalog's command categories
remain release-required. Original member positions survive includes and compilation.

Existing standalone `ENDIF`, `ENDSL`, `DSPLY`, and `//` comments are CL extensions where used;
RPG's similarly spelled constructs do not establish native CL syntax. The required CL inclusion
mechanism is the explicitly named `/INCLUDE` extension with `/DEFINE`, `/IF`, `/ELSE`, `/ENDIF`,
using the same source resolver as RPG but separate grammar/diagnostics. Reject cycles, missing
members, duplicate else, undefined conditional expressions and unbalanced nesting.

## RPG and ILE

Required source: fixed D/F/C/E/P/O specifications, mixed `/free` regions, and `**free` programs;
free declarations for scalars, constants, arrays, DS, files, prototypes and procedures. Fixed
operations retain their fixed syntax; accepting legacy arithmetic opcodes in free form is an
application extension. Required values: character/varying, packed/zoned/integer/unsigned/float,
indicators, dates/times/timestamps; compile-time, pre-runtime and runtime arrays; overlays and
procedure-local storage. Required arithmetic, control-flow, I/O, print/display, calls, indicators
and error operations are those enumerated in WP10 and the matrix. Required built-ins include
`%LEN`, `%SIZE`, `%SUBST`, `%TRIM`, `%TRIML`, `%TRIMR`, `%INT`, `%DEC`, `%CHAR`, `%ABS`, `%DATE`,
`%TIME`, `%TIMESTAMP`, `%FOUND`, `%EOF`, `%ERROR`, `%STATUS`. A recognized opcode is still partial
until its operands, result indicators, errors and effects pass acceptance.

Required preprocessing: `/COPY`, `/INCLUDE`, `/DEFINE`, `/UNDEFINE`, `/IF`, `/ELSE`, `/ENDIF`,
`/TITLE`, `/EJECT`, `/SPACE`, with include/source maps and nested conditional tests. Required ILE:
modules, binding directories, service programs, activation groups, by-reference/value/const
parameters, return values and writeback, CL/RPG/C#/Node calls through the shared execution context.
`MONITOR/ON-ERROR/ENDMON` must handle the enclosing scope; today's tail-trap behavior is a gap.
No native MI instruction execution or IBM compiled-program binary loading is promised.

Embedded SQL includes SELECT INTO, INSERT/UPDATE/DELETE, prepared statements with host variables
and null indicators, DECLARE/OPEN/FETCH/CLOSE cursors, SQLCODE/SQLSTATE, COMMIT/ROLLBACK, and
job-shared transactions. Compile-time SQL diagnostics retain RPG source locations.

## DDS and terminal

Required PF/LF: the enumerated A/P/S/B/F/L/D/T/Z representation contract, lengths/scales,
VARLEN, ALWNULL, defaults, CCSID, ALTSEQ, TEXT, keyed ASC/DESC, select/omit, joins, multiple
formats and members, references and dependent access paths. Existing nonstandard source-column
and binary/date layout assumptions must be checked against independent fixtures in C08.

Required DSPF: DSPSIZ, DSPATR, COLOR, DFT/DFTVAL, ERRMSG/ERRMSGID, MSGCON, COMP, CHECK/CHKMSGID,
DATE/DATFMT, EDTCDE/EDTWRD, INDARA, CF01–CF24/CA01–CA24, PAGEDWN/PAGEUP/ALTPAGEDWN, ALIAS,
cursor position and help; SFL/SFLCTL and SFLMSGRCD/SFLMSGKEY/SFLPGMQ (the plan's SFLMSG record type), sizes/pages/display/clear/end/next-changed flags,
hidden keys, fold, WINDOW (including SDA WDWSFL record types) and message subfiles. Required printer DDS includes record and
field positioning, spacing/skipping, page overflow, copies/forms, edit codes/words and indicators.
PRTF is a *FILE attribute, not a separate IBM object type. UIM requires help panels, indexed
help areas and links; SDA must save source accepted by the same compilers. Unsupported DDS
keywords must fail rather than silently changing screen/data layout.

## SQL and reporting

The common authorized SQL subset includes SELECT with projections, joins, predicates, NULL,
GROUP BY/HAVING, aggregates, ORDER BY, FETCH FIRST, parameter markers, INSERT/UPDATE/DELETE,
CREATE/ALTER/DROP TABLE/VIEW/INDEX, PK/unique/FK/check/default constraints and row triggers.
Required scalar functions: character length/substrings/trim/case, COALESCE/NULLIF, numeric
ABS/ROUND, casts, current date/time/timestamp, date/time extraction and interval arithmetic.
Exact decimal comparisons, overflow, padding/CCSID, null semantics, and transaction isolation
must be tested. SQLite-only internal tables and PRAGMA/ATTACH/extensions are not public SQL.
`*N` is handled only in documented command/program contexts; it is not a universal SQL token.

The ODBC/JDBC gateway is read-only but shares read transactions and typed query semantics with
local SQL. The local SQL service supports writes. Full Db2 optimizer and stored-procedure catalog
parity remain excluded. Unsupported SQL must return a stable SQLSTATE, never pass unrestricted
text directly to the internal catalog connection. Required reporting clients remain those in
[release scope](release-scope.md).

## Encodings, archive interchange, and host interfaces

Required CCSIDs: 37, 500, 1047, 850, 819, 1208, 367. Conversion must use explicit decoder/encoder
failure policies, incremental state for split UTF-8 sequences, and reference byte fixtures;
unsupported CCSIDs fail before writing. Primary EBCDIC is 37. Date formats are YMD/DMY/MDY with
configured separators and a documented century window. Storage timestamps use UTC with explicit
zone conversion at interfaces. No assertion of every IBM CCSID is made.

The `.isav` archive must be lossless and versioned for every supported object plus authority,
configuration, IFS, spool and journal dependencies. The native SAVF acceptance subset is source
physical files and single-format physical data files with character, packed/zoned decimal and
binary fields, including members, names and CCSID metadata, saved/restored against IBM i 7.4 or
7.5. Native SAVF reader AND writer require independent real-system fixtures and import/export
results. Encrypted/compressed/native compiled objects outside that subset must fail explicitly;
there is no invented binary-layout contract. Missing native evidence leaves C13/C20 open.

Inbound SOAP and outbound SOAP clients use WSDL 1.1, SOAP 1.1 document/literal wrapped operations,
scalar/nullable/array/structure XSD types and SOAP Faults over HTTPS. REST uses JSON/OpenAPI and
bounded requests. RPC/encoded SOAP, arbitrary XSD schema constructs, and WS-* protocols outside
this subset return explicit unsupported diagnostics. Service identity/authorization and parameter
codecs are shared with command/program execution; no independent privileged dispatch path.

Required host adapters remain SSH/SFTP, Samba, LDAP, Kerberos/SSSD/EIM, BIND, SMTP/IMAP, UFW/NAT,
CUPS, Node, systemd, Docker, external IBM MQ and object transfer. They expose validated operations,
structured health/error results, timeouts/cancellation and dry-run previews with redacted secrets.
No shell interpolation of untrusted names or passwords. Docker instance isolation includes
catalog, ports, volumes, credentials and lifecycle; host provisioning remains separately tested.
