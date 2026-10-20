# Query-management SQL objects

`CRTQMQRY`, `RTVQMQRY`, `STRQMQRY` and exact-name `DLTQMQRY` now use saved
`*QMQRY` objects and the shared [typed query](sql-queries.md) and
[table/mutation](sql-tables.md) services. They remain partial native adapters.

Given member REPORT in QQMQRYSRC containing
`SELECT ID,NAME FROM QGPL/DATA WHERE ID=&ID`, an interactive job can run:

```cl
CRTQMQRY QMQRY(QGPL/REPORT) SRCFILE(QGPL/QQMQRYSRC)
STRQMQRY QMQRY(QGPL/REPORT) SETVAR((ID 7))
RTVQMQRY QMQRY(QGPL/REPORT) SRCFILE(QGPL/QQMQRYSRC) SRCMBR(BACKUP)
DLTQMQRY QMQRY(QGPL/REPORT)
```

The parameter/default references are IBM's
[CRTQMQRY](https://www.ibm.com/docs/api/v1/content/ssw_ibm_i_74/cl/crtqmqry.html?lang=en&parsebody=true),
[RTVQMQRY](https://www.ibm.com/docs/api/v1/content/ssw_ibm_i_74/cl/rtvqmqry.html?lang=en&parsebody=true),
[STRQMQRY](https://www.ibm.com/docs/api/v1/content/ssw_ibm_i_74/cl/strqmqry.html?lang=en&parsebody=true) and
[DLTQMQRY](https://www.ibm.com/docs/api/v1/content/ssw_ibm_i_74/cl/dltqmqry.html?lang=en&parsebody=true).
Source/substitution behavior was checked against IBM's
[Query Management Programming](https://public.dhe.ibm.com/systems/power/docs/systemi/v6r1/en_US/sc415703.pdf),
especially its source, variable and import sections. That older reference supplies
independent rules; current-release native golden acceptance remains open.

## Source and object lifecycle

Creation defaults to the current library, QQMQRYSRC in the library list, and a
member with the query's name. It reads an authorized source physical file snapshot,
imports the first 79 encoded bytes of each record and pads shorter records.
Splitting an encoded character at that boundary fails. Record boundaries disappear
when running SQL: tokens can continue in column 79, and padding inside continued
quoted values is significant. Local source limits are one MiB/10000 records.
Creation stores source without SQL syntax or object resolution, following native
import behavior; execution checks syntax and the current catalog.

Source lives in the ordinary object payload, with attribute SQL, format IPCQMQRY1,
source CCSID and sort/language metadata. Shared copy/move/rename/delete, QTEMP
privacy and job cleanup apply. Restart needs no original source file. Main schema
stays 35 and coordination format stays 2; there is no private query table.

`REPLACE(*YES)` is the default; `*NO` returns QWM2706 for an existing object.
Creation applies public authority, library CRTAUT or an authorization list.
Replacement requires object management/existence authority, preserves owner,
creation time and authorities, updates SQL/description and removes the old
signature. Required-signature policy remains, so replacement requires signing
again before execution. Create/replace and the audit event are atomic. Audit
failure preserves the old object or leaves no new one; payloads contain identity
and replacement status rather than SQL. Native replacement archival/identity
acceptance remains open.

Retrieval creates an absent member or replaces its complete contents through the
source editor's authority, CCSID, width and optimistic revision checks. Failure
leaves the old member unchanged. Comments and substitution variables remain in
retrieved source. Descriptions accept 50 printable characters. The member catalog
has no member text yet, so `TEXT(*SRCMBRTXT)` yields blank object text.

## Variables and execution

Case-sensitive ASCII variable names start with a letter and contain up to 30
letters/digits/underscores. SETVAR uppercases names and accepts up to 50 pairs,
each with a 0–55 character value. DSQ-prefixed user variables reject. Names in
comments, constants or delimited identifiers are not expanded. Substitution is
one pass, with no extra blanks between adjacent variables. Line comments are
removed first; the shared SQL extension also accepts nested block comments.
Substituted SQL is limited to 32767 encoded job-CCSID bytes after source whitespace
normalization. This bounded local normalization still needs native boundary acceptance.

Values are SQL fragments, rather than bound SQL parameters. For `NAME=&NAME`,
the CL command must preserve the SQL apostrophes:

```cl
STRQMQRY QGPL/CHANGE SETVAR((ID 7) (NAME '''Ada'''))
```

IBM's [CL variable example](https://www.ibm.com/support/pages/opnqryf-strqmqry-receive-variables-cl-program)
documents this quoting layer. Fragments can represent identifiers or expressions.
The shared AST enforces one supported statement and prevents host/catalog SQL
access. Substitution confers no authority. Typed CL values and MONMSG use the
normal command runtime. SQL failures write their enriched SQL diagnostic to the
job log and return the query command's QWM2701 escape; invalid *LIBL under SQL
naming returns QWM2707. Complete native message sequencing/ABI remains open.

Missing values prompt in source order with an interactive terminal. Empty Enter,
F3 or F12 cancels with QWM2701; `*BLANK` supplies one space. Explicit values are
scoped to this invocation. Mixed-case source names require interactive input.
Callable-interface global pools and DSQ configuration variables remain open.

DDL/mutations use RUNSQL's no-commit record/constraint/journal paths. SELECT uses
the existing bounded disk-backed browser: headings, paging, positioning, horizontal
scrolling and F5 refresh. F3/F12 returns to the caller, including interactive CL.
Browsing does not change STRSQL saved sessions. Display interactions recheck query
authority and signed source, live credentials/job and original file access, even
for EOF-cached rows. Disconnect/job cleanup closes cursors, caches and allocations.

System naming is the default. `*SQL`/`*SAA` use SQL naming and require an explicit
query library or `*CURLIB`, following the native restriction on `*LIBL`. The local
hex profile accepts `SRTSEQ(*HEX/*JOB/*JOBRUN)` and default `*SRC` without externalized
sort metadata. Complete job collation and language attributes remain open.

Authenticated command-only/HTTP execution supports DDL and mutations. Missing
variables fail without a terminal. SELECT display requires a terminal; native default
printer output for batch remains unavailable and rejects explicitly.

`OUTPUT(*OUTFILE)` now saves SELECT results without a terminal. It creates a
private typed PF or replaces/appends to an existing compatible member, including
output overrides and normal CL/RPG reads. See the [output-file contract](sql-query-output.md)
for authority, source snapshots, quotas and the native clear/partial-write boundary.

## Remaining acceptance

Print output, remaining outfile/native acceptance, QMFORM formatting, callable/global/DSQ state, externalized
H/V and sort/language records, QRYDFN fallback, WRKQRY/WRKQMQRY/STRQM, generic and
multiple-library DLTQMQRY, member text, native message/display/source ABI and width
warnings, job locale/commitment/isolation, non-hex collations, remote RDB connections,
and current-release native golden/scale fixtures remain open. Unsupported modes
reject before SQL effects. Wider grammar and reporting gateways remain governed
by the [SQL contract](compatibility-contracts.md#sql-and-reporting).

The inventory adds four partial commands: 324 entries, 181 defined adapters.
C07/C08 stay unchecked; checklist status stays 36 complete / 70 open.

Verification (2026-10-19): all 7218 solution tests and 71 focused checks
pass, with zero failures/skips, a warning-free Release build, locked restore,
full terminal acceptance and QMQRY/CL/SQL checks at both screen sizes, 993 frozen
source/build/cache files, inventory, local links and whitespace. Full acceptance
uses four xUnit workers and completes in 9:48.95.
