# RPG feedback structures

Free declarations support `DCL-F ... INFDS(name)` for DISK and WORKSTN,
and one `DCL-DS ... PSDS` in the main source section. Feedback shares the
[native data-structure storage](rpg-native-structures.md): positional fields,
overlays, whole-structure byte reads and borrowed API arguments see the same bytes.
Source-bound saved programs retain these declarations after source removal.

```rpgle
**free
dcl-f DATA keyed usropn infds(F);
dcl-ds F qualified;
  STATUS *status;
  OP *opcode;
  IS_OPEN ind pos(9);
  AT_EOF ind pos(10);
  MSGID char(7) pos(46);
end-ds;
dcl-ds P qualified psds;
  NAME *proc;
  STATUS *status;
  PREVIOUS zoned(5) pos(16);
  PARAMETERS *parms;
  LAST_FILE char(10) pos(175);
  FILE_STATUS zoned(5) pos(209);
end-ds;
read(e) DATA;
// F.STATUS and P.FILE_STATUS are 1211; P.STATUS remains zero.
open DATA;
read DATA;
// F.STATUS is zero; the last-file error snapshot remains 1211.
seton LR;
```

## File feedback

Each file requires its own named scalar native INFDS. An implicit length grows
to 80 bytes; an explicit LEN below 80 fails compilation. This implementation
accepts only the first 80 bytes. Larger open/I/O/device feedback layouts fail
explicitly. INFDS binding is program-local and does not alter external record
signatures or saved DISK/WORKSTN binding versions. LIKEDS chains inherit the
final length after feedback binding and still enforce native storage bounds;
copies remain ordinary structures. IBM defines the
[minimum length and scope](https://www.ibm.com/docs/en/i/7.6.0?topic=exceptionerrors-file-information-data-structure).

Reserved subfields are `*FILE`, `*STATUS`, `*OPCODE`, `*ROUTINE` and `*RECORD`.
Specify the reserved word alone instead of a data-type keyword. These fields
have fixed offsets/types; extra POS, DIM or INZ options fail compilation.
The same words outside an INFDS or PSDS fail with original source locations.

The implementation maintains the program file name, open/EOF flags, status,
operation plus F/R/I operand suffix, routine, expanded source-listing line,
external record name and available service CPF ID. Status is zoned decimal
at bytes 11–15. Character fields use the activation's captured CCSID; zoned
digits retain their native numeric representation at every CCSID. Long
operation names use DELET, UPDAT, UNLCK and REDPE. Implicit opens use OPEN I
and *INIT. See IBM's [file feedback layout](https://www.ibm.com/docs/en/i/7.4.0?topic=structure-file-feedback-information).

Successful file operations refresh status. Open/EOF flags follow the live file
and retained cursor; closing its owner clears the open flag. Errors update
feedback before E, ER or MONITOR handling. Independent files retain independent
feedback. The common area exists internally even without an explicit INFDS,
so PSDS can retain the last file-error snapshot. Source IDs, POST/device fields
and machine message IDs without a backing service exception are not populated.
Unused areas remain blanks. Writing system feedback fields is unsupported.

## Program feedback

PSDS supports reserved `*PROC`, `*STATUS`, `*ROUTINE` and `*PARMS`, including
anonymous structures. Arrays, LIKEDS together with PSDS and multiple PSDS
declarations fail compilation. Short structures receive only bytes inside their
declared length.

Populated fields are program name/status, previous program status, failing
listing line/routine, entry parameter count, available CPF exception identity,
program library, diagnostic text, last file name and its 35-byte status snapshot,
job name/user/number, containing program/module name and initial current user.
Offsets follow IBM's [PSDS layout](https://www.ibm.com/docs/en/i/7.5.0?topic=exceptionerrors-program-status-data-structure).
Job identity comes from the session host; bare interpreter hosts may supply it
or receive blank names and a zero job number.

Program errors update PSDS status and previous status. Ordinary success and
file status changes preserve that program status. File errors separately update
last-file fields; normal EOF/misses and later successful I/O preserve the error
snapshot. EXSR propagation preserves the original error line/routine; CALL
failures report caller status 202 and the called target. Routine names describe
the supported full-procedural execution, not a complete native RPG cycle.

`*PARMS` refreshes at each program invocation, including retained RETURN.
`%PARMS` and `%PARMS()` return the current invocation's count; the entry bound
is 999 parameters. Local procedure execution temporarily uses its own count.
Job/current-user identity is initialized once per activation. Program error
history, byte storage and retained file state survive RETURN; LR/reclaim closes
the file owners. Diagnostic truncation preserves complete encoded characters.

Other PSDS fields, including dates/times, compiler/source attributes, extended
source IDs, internal message workspace, XML/DATA counters and native job/system
IDs, are not populated. [INFSR/*PSSR error subroutines](rpg-error-subroutines.md)
now use this feedback with scoped dispatch and recovery. INDDS, POST and extended INFDS feedback,
native fixed D-spec layouts, complete RPG cycle/status semantics and independent
native reference/scale acceptance remain open. C09/C11 therefore remain partial.

`RpgFeedbackTests`, `RpgFileFeedbackTests` and `RpgDisplayFileTests` cover known
bytes at CCSIDs 37/1208, initialization, errors, handler timing, independent
files, retained calls, saved-source execution and real workstation exchange.
The display PTY compiles and calls RPGFDBK.
