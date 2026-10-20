# Interactive SQL

`STRSQL` now opens Enter SQL Statements in an interactive terminal. It runs the
shared [typed query service](sql-queries.md) and [table/mutation engine](sql-tables.md),
so SQL, CL and RPG use the same catalog objects and records. The default is
`COMMIT(*NONE)` and system naming; `*NC` is accepted. An interactive CL caller
waits for the SQL session and resumes after it exits. Command-only HTTP/batch
contexts return SQL6141 when they have no interactive terminal.

```cl
STRSQL
STRSQL NAMING(*SQL) REFRESH(*FORWARD)
STRSQL PROCESS(*SYN)
STRSQL PROCESS(*VLD)
```

Enter a statement and press Enter. CREATE/DROP TABLE, INSERT, searched UPDATE
and DELETE use their existing authority, allocation, record-slot, constraint and
journal paths. [SQL views](sql-views.md) share these writes with direct-column
mapping and LOCAL/CASCADED CHECK OPTION. SELECT opens Display Data with headings, typed values, NULL
indicators, row positioning, paging and horizontal scrolling. Failed SQL stays
editable and reports its message ID, SQLSTATE and SQLCODE; F1 shows the full
message when it does not fit the status line.

The native command parameters/defaults were checked against IBM's
[STRSQL reference](https://www.ibm.com/docs/api/v1/content/ssw_ibm_i_74/cl/strsql.html?lang=en&parsebody=true).
The screen/exit navigation follows IBM's
[interactive SQL example](https://www.redbooks.ibm.com/redbooks/pdfs/sg246208.pdf)
and [session save options](https://www.ibm.com/support/pages/strsql-sessions-save-options).
Independent current-release screen/message fixtures are still required for full
native acceptance.

The current keyboard flow includes:

- F3 opens Exit Interactive SQL: 1 saves/exits, 2 discards/exits, 3 resumes and
  4 exports statement history to a source member. F12 cancels a form or returns
  from results without ending the SQL session.
- F9 retrieves the latest statement, or a history entry selected with the roll
  keys. F6 inserts a statement line and F10 copies a line. Arrow/Home/End,
  Insert/Delete/Backspace and erase-to-end edit the draft.
- F4 builds a SELECT from select/from/where/group/having/order/fetch fields.
  Quoted/commented keywords and nested expressions do not split clauses. Enter
  returns the built draft for review; the next Enter runs it.
- F13 opens session services, with option 1 for the implemented session
  attributes. Invalid changes leave the previous settings intact. F24 switches
  key hints. F1/F12 preserves the pending draft and form fields.
- In Display Data, roll keys page, F19/F20 shift columns, Enter applies line and
  column positions, and F5 explicitly refreshes. Empty results retain headings.

Bracketed paste preserves SQL line breaks, including the end of `--` comments,
without submitting the statement. The terminal parser still suppresses pasted
attention/function keys. Other screens retain their existing paste-to-space
behavior. The fixed-cell terminal continues to substitute unsupported display
glyphs; SQL storage is not rewritten by presentation.

`PROCESS(*SYN)` parses without object resolution or execution. `*VLD` uses the
new shared `ValidateSql`/`PrepareSqlQuery` paths to resolve files/columns,
authorize targets and bind implemented expression/literal types without running
a statement or evaluating query rows. Validation reuses mutation and creation
binding rules. Actual row values, constraints, triggers, journal outcomes and
concurrent changes remain execution-time checks. It is not a complete native
catalog/procedural SQL preparation service.

Date/time and decimal settings now reach the shared binder through optional
`SqlConversionOptions`. Implemented formats normalize DATE/TIME inputs to the
canonical file representation and format output/casts for the session. ISO/USA/
EUR/JIS input forms and the configured two-digit or Julian forms are supported;
two-digit years use the existing 1950–2049 window. Numeric casts trim surrounding
spaces and reject multiple decimal points/signs. With decimal comma enabled,
both period and comma decimal points are accepted; a comma separating numeric
items must be followed by whitespace, as in `VALUES(1, 2,50)`. Character literal
contents are changed only by the requested typed conversion.

QDECFMT is seeded without replacing an existing value; changing it accepts blank,
I or J. `*JOB` format defaults currently come from runtime system defaults captured
when STRSQL starts, while `DECPNT(*SYSVAL)` reads the current value per statement.
Full job-attribute inheritance/CHGJOB formats, native century handling, timezone,
conversion and edited-output parity remain open. Unconfigured API/RUNSQL callers
retain their existing canonical profile.

Results use a private disk-backed scroll cache, bounded to 64 MiB of serialized
values, with only one page in managed memory. `REFRESH(*ALWAYS)` reopens the query
for requested pages. `*FORWARD` retains already displayed rows; simple unordered
single-table queries refresh previously unseen pages, while queries requiring
ordering/grouping/distinct/join materialization retain their result snapshot.
F5 starts a new result. Ordinal replay can rescan earlier rows; native ODP,
changed-key/membership, access-plan and scale acceptance remain open. This local
refresh policy does not complete native isolation or uncommitted cross-job reads.
`ALWCPYDTA(*OPTIMIZE)` requires `REFRESH(*FORWARD)`; `*NO` rejects until native
access-plan validation can enforce it.

Every statement has its own automatic allocation scope. Result cursors/cache
allocations remain only while browsing. Even cached EOF results recheck the
original credentials, job and physical/logical source authority before handling
another key. Disconnect cancels SQLite work and joins the worker; job/factory
cleanup cancels and deletes private scroll caches before releasing workspace
resources. STRSQL uses the existing suspended-program terminal handoff, including
CL callers, F4 command prompts and preserved call authority.

Schema 35 stores private saved sessions by executing profile, with a revision,
settings, statement history and draft. Save uses a transaction and an optimistic
revision check; a second session cannot silently replace newer state. Discard
removes the loaded saved state, or retains a newer state if another session saved
first. Profile deletion cascades the private state. Audit failures roll back
save/delete, and audit payloads contain counts/identity rather than SQL/draft
text. Saved attributes/history restore on the next STRSQL; command attributes
are then ignored, matching the native saved-session flow. History is bounded to
1000 statements/8 MiB and input/drafts to 32768 encoded bytes. Oldest history is
evicted at the bound. Native ISQL internal-object/backup/device identity remains
unimplemented; the local saved-state scope is one profile.

Exit option 4 uses the shared source-file store, live authority, CCSID conversion
and optimistic source revision. The target must exist and source lines must fit
its record width. Statements receive terminators for [RUNSQLSTM](sql-scripts.md).
Unsubmitted drafts are retained by internal save rather than added to the export.
A saved-state conflict after source export reports that the source was written
and preserves the other session's newer history.

STRSQL remains partial. Non-*NONE commitment, native hold/open/isolation,
non-hex collations, embedded-language syntax modes, object-list support, prompts
for other SQL statements, complete services/session attributes, print/outfile
output, exact native source/history/message/display ABI, wider SQL grammar,
[QMQRY print/outfile and WRKQRY](query-management.md), IWS/reporting endpoints and independent native golden/scale tests
remain open. Unsupported command modes reject explicitly. Main schema is 35,
coordination format remains 2, and STRSQL adds one defined adapter (177 total)
without changing the 320-entry release catalog. C07/C08 remain partial.

Verification (2026-10-19): all 7170 solution tests pass, zero failures/skips,
in 8m30s, including 54 new cases. All 1004 focused checks and 4354 broad
RPG/binding/work cases pass. Locked restore, zero-warning/error Release build,
real STRSQL/CL/RPG acceptance at 24x80 and 27x132, the existing terminal suite,
985 frozen-source checks, generated inventory, local links and whitespace pass.
Full acceptance uses four xUnit workers.
