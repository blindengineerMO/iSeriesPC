# RPG numeric status and MONITOR

Declared DISK and WORKSTN operations support the E extender and numeric
`%STATUS`/`%STATUS(file)`. `%ERROR` reports the most recent E operation, separately
from EOF and FOUND. E clears the global status, that file's status and ERROR
before execution. An expected file failure sets ERROR and continues after the
operation. File status updates after every file operation, including ordinary
success (zero), as specified by IBM's
[file status rules](https://www.ibm.com/docs/en/i/7.4.0?topic=structure-file-status-codes).
Successful program operations that do not set status leave it unchanged. Ordinary
file operations leave ERROR unchanged; only E resets it. Nonzero normal statuses
update status without setting ERROR. Retained RETURN
activations preserve this state. These rules follow IBM's
[%STATUS](https://www.ibm.com/docs/en/i/7.5.0?topic=functions-status-return-file-program-status)
and [%ERROR](https://www.ibm.com/docs/en/i/7.5.0?topic=functions-error-return-error-condition)
contracts. Independent native golden comparisons remain required.

File arguments must name the program file. Operations using a renamed record
format update the owning file's status. `%STATUS` without parentheses and
`%STATUS()` both report the most recently changed global program/file status.
`%ERROR` takes no arguments. Status values are integers, so `%CHAR(%STATUS)`
produces ordinary decimal text rather than a zero-padded five-byte INFDS field.

| Status | Implemented condition |
| --- | --- |
| 11 | Sequential input reaches EOF |
| 12 | CHAIN or positioning finds no qualifying record |
| 13 | WORKSTN subfile WRITE reports full |
| 1021 | SQLite unique-key/record-number constraint |
| 1022 | SQLite foreign-key constraint violation |
| 1211 | Operation against a closed declared file |
| 1215 | OPEN against an already open declared file |
| 1216 | Initial implicit open failure |
| 1217 | Explicit open/close service failure |
| 1218 | Classified record-lock acquisition failure |
| 1221 | UPDATE/current DELETE without locked update input |
| 1299 | Other supported file-service failure |

IBM documents these [file status codes](https://www.ibm.com/docs/es/i/7.5.0?topic=structure-file-status-codes).
A foreign-key violation maps to 1022; native 1222 concerns a different allocation
failure and is not emitted for SQLite constraint violations. See IBM's
[referential-error distinction](https://www.ibm.com/docs/en/i/7.4.0?topic=as400-io).
Object allocation failure is distinct from record contention: an explicit OPEN
allocation failure reports 1217, while an actual record-lock timeout reports 1218.
Service CPF identities remain available when an error propagates to CL callers.
Deadlock CPF1003 currently maps to general file status 1299. Cancellation and job
end propagate through E and MONITOR.

`CHAIN`, `READ`, `READP`, `READE` and `READPE` accept E alone or EN/NE on an
update file. N retains its inquiry semantics; a later UPDATE therefore reports
1221. Other implemented file operations accept E alone. A declared file's
explicit ER indicator takes precedence over MONITOR, as does E; combining E
with an ER indicator is rejected in fixed source. Only expected operation
errors are handled this way: division by zero while computing a file operand
reports program status 102 to MONITOR. This follows IBM's
[exception priority](https://www.ibm.com/docs/ssw_ibm_i_74/rzasc/hexmain.htm).

Input values are converted before any imported program field changes. A failed
conversion invalidates and releases the update target while preserving all
program fields. EOF, misses and record contention also preserve fields, following
IBM's [file operation contract](https://www.ibm.com/docs/en/i/7.5.0?topic=operations-file).

## Structured handlers

```rpgle
monitor;
  chain(e) 20 ORDERS;
  if %error and %status(ORDERS)=1218;
    sndpgmmsg 'Record is locked';
  endif;
  update ORDERROW;
on-error 1221;
  sndpgmmsg 'No locked input record';
on-error *file;
  sndpgmmsg %char(%status);
endmon;
```

MONITOR chooses the first matching ON-ERROR in the nearest enclosing monitored
body, then resumes after ENDMON. Unfiltered ON-ERROR means `*ALL` (100–9999);
`*PROGRAM` means 100–999 or 9999 and `*FILE` means 1000–2999. Colon-separated
filters and leading-zero numeric spellings are supported. Explicit numeric
filters currently accept 100, 102, 103, 105, 112, 121, 122, 123, 202, 452, 907,
1021, 1022, 1211, 1215, 1216, 1217,
1218, 1221, 1222, 1299 and 9999; unsupported codes fail compilation. Limits are
32 filters per handler, 256 handlers per group and 256 nested control blocks.
See IBM's [MONITOR](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-monitor-begin-monitor-group)
and [ON-ERROR](https://www.ibm.com/docs/ssw_ibm_i_74/rzasd/zzonerr.htm) rules.

Handler errors bypass that same monitor and can reach an outer handler. EXSR
preserves the original numeric failure; a called program/procedure failure
reports 202 to the caller. Typed program failures currently cover division by
zero (102), substring bounds (100), ordinary array bounds (121) and
multiple-occurrence selection bounds (122). Native DS
fields additionally report overflow (103), numeric conversion (105), invalid
datetime (112), character conversion (452) and decimal-data errors (907). Inner loop
and SELECT state is unwound during handler transfer, preserving enclosing
groups. LEAVE, ITER and RETURN work in monitored bodies/handlers. GOTO is
rejected inside a monitor body and cannot enter a monitored body or handler;
it can leave a handler. A monitor requires at least one ON-ERROR block.

An unfiltered ON-ERROR outside MONITOR remains the earlier tail-trap extension.
It is separate from structured handling and does not accept status operands.

## Evidence and remaining work

`RpgFileStatusTests`, `RpgMonitorTests`, `RpgStatusCompilerTests` and
`RpgDisplayFileTests` cover reset/retention, independent files, extenders,
lock/unique/open failures, ER precedence, staged input, nested handlers,
subroutine/call propagation, bounds, loop/SELECT transfers and source diagnostics.
The real terminal compiles and calls RPGSTATUS, exercising closed-file E,
numeric status reset, N inquiry and ON-ERROR 1221.

This is a bounded implementation. Full INFDS/PSDS byte layouts, native cycle return points,
ON-EXCP, CALLP(E), other extenders, remaining program/file/display status mappings,
all native fixed calculation layouts, commitment open policy/isolation and
independent native reference suites remain open. These changes do not close
the broader C09/C11 requirements.

The [error-subroutine subset](rpg-error-subroutines.md) now supports INFSR,
procedure-local *PSSR, explicit/automatic ENDSR, RETURN/GOTO and *CANCL cleanup.
The [feedback structure subset](rpg-feedback-structures.md) now binds free
INFDS/PSDS declarations to native bytes, including common file feedback,
program errors and last-file snapshots. Extended areas and the other native
error-handler contracts remain open.
