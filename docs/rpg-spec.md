# rpg-spec

RPG III / RPG IV-style emulation for iSeriesPC — fixed-form and fully free-form programs compiled
to an internal statement model and executed by `Ipc.Rpg.Runtime.RpgInterpreter` (`.NET 8`, x64 Linux).

## 1. Overview

A program is a named object (library/name, e.g. `CALL MYLIB/TESTPGM`) whose source is read,
declared, parsed into statements, restructured into blocks, and cached as a `RpgProgram`. The
interpreter walks `MainStatements` (plus `Subroutines`) against a `RpgRuntimeContext` holding
fields, data structures, indicators, and file status. Database operations use the SQLite
`IPCFILE` store through `RpgFileCursor`; declared WORKSTN files use the shared terminal
and display-file runtime described in §12.

## 2. Compiler pipeline

`RpgCompiler.Compile(library, name, source)`:

1. **Preprocess** — expand the bounded [RPG directive grammar](source-preprocessing.md#rpg-directives)
   and retain original source snapshots and include locations.
2. **ReadSpecs** — preserve physical line positions while masking blanks and `*` comments (except `**free`).
3. **Free-form detection** — true if any line starts with `**free`; repeated/malformed markers fail.
4. **ParseDeclarations** — collect supported fixed F/D specs, free DCL-F/DCL-S declarations,
   external display fields, and data structures. WORKSTN compilation requires a display resolver;
   CRTBNDRPG saves the source-bound external layouts alongside the program.
5. **ParseStatements** — distinguish fixed layouts from free text. Unsupported operations,
   specifications and D-spec types fail explicitly with original source locations.
6. **Validate structure** — reject crossed/unclosed blocks, duplicate branches, invalid routine
   boundaries and loop-control operations outside loops before extracting executable bodies.
7. **ExtractSubroutines/subprocedures** — move bodies into their owning lists and reject duplicate
   labels within each resulting statement list.
8. **ResolveBlocks** — pair `IF/ELSE/ENDIF`, `SELECT/WHEN/OTHER/ENDSL`, `DO/DOU/DOW/FOR/ENDDO`,
   and label/tag jumps; throws `RpgCompileException` for unbalanced blocks.
9. **AssignDsOffsets** — compute DS element offsets (sequential or `OVERLAY` overrides).

Compile errors throw `Ipc.Rpg.Runtime.RpgCompileException`; runtime errors throw
`RpgRuntimeException`.

## 3. Source columns (fixed form)

Columns are 1-based and parsed with `Col(line, start, end)`.

### D-spec (declarations)

| Column(s) | Meaning                            |
|-----------|------------------------------------|
| 7         | `D` (spec type)                    |
| 8–20      | field / DS name                    |
| 22–23     | type (`S`, `P`, `Z`, `C`, `DS`, …) |
| 39–40     | length                            |
| 41–42     | decimals (non-blank ⇒ numeric)     |
| 51–80     | keywords                           |

Keywords: `INZ`, `DIM(n)`, `OVERLAY`, `VARYING`. A field is **numeric** iff the decimals column is
non-blank (Zoned/Packed) regardless of the value; otherwise it is character.

### C-spec (calculations)

| Column(s) | Meaning                                  |
|-----------|------------------------------------------|
| 7         | `C` (spec type)                          |
| 8–17      | conditions (`N` prefix + indicator, e.g. `N99`, `11`) |
| 18–35     | Factor 1                                 |
| 36–42     | operation/opcode                         |
| 43–47     | Factor 2                                 |
| 48–58     | result                                   |
| 53–58     | length (suffix of result area)           |
| 65–70     | result indicators (primary/secondary)    |

Conditions gate execution (inverted when prefixed with `N`). For parameter lists, `PLIST` is
marked via Factor 1 (`*ENTRY`) with `PARM` targets in Factor 1.

## 4. Free form

Free statements accept any indentation, including column 1 and column 7. `**free` begins global free-form source and
free statements continue until the end of the source (no `**endfree` terminator). The block
directives `/free` and `/end-free` switch free-form parsing on/off between them, letting free
statements be interleaved with fixed C-specs in the same program (`/free` presence alone does not
make the whole program free). `/FREE` and `/END-FREE` must balance, cannot nest and
cannot appear in a `**free` program. Each statement ends with `;` (the interpreter's
legacy optional final semicolon remains accepted). Operands are bound per opcode:
`IF`/`WHEN` keep the whole expression as the condition; `MOVE`/`MOVEL`/`MOVA` bind Factor 1 = first
operand, result = remaining operand(s); `CHAIN`, `READ*`, `SETLL`, `SETGT` parse `(key) file` or
`key file` into Factor 1 (key) / Factor 2 (file). `EVAL`, `CALLP`, `FOR`, `DO`/`DOU`/`DOW`,
block delimiters, `RETURN`, `WRITE`, `UPDATE`, `DELETE`, `CLEAR`, `SETON`/`SETOFF`, `BITON`/
`BITOFF`, `DSPLY`, `SNDMSG`, `RCVMSG`, `EXSR`, `ON-ERROR`, `GOTO`/`TAG` are all recognized
(case-insensitive; `ZADD`/`ZSUB` are free synonyms). `CALLP proc(a:b:c)` and built-in calls use
`:` as the argument separator.

Block tokens: `IF…ELSE…ENDIF`, `SELECT…WHEN…OTHER…ENDSL`, `DO`/`DOU`/`DOW`/`FOR…ENDDO`/`ENDFOR`,
`ITER`, `LEAVE`, `BEGSR…ENDSR`.

## 5. Opcodes

### Arithmetic and assignment
`EVAL`, `ADD`, `SUB`, `MULT`, `DIV`, `Z-ADD`, `Z-SUB`, `MOVE`, `MOVEL`, `MOVA`, `MVR`, `CLEAR`, `TEST`.

- Free `EVAL` parses `target = expression`.
- `MOVE`/`MOVEL`/`MOVA` source lives in Factor 1, target in the result field.
- Arithmetic (`ADD`/`SUB`/`MULT`/`DIV`) accumulates: Factor 1 source, Factor 2 delta, result
  accumulator (result columns `48–58`); result fields are zero-length/blank ⇒ Factor 1 is used.
- Numeric operands coerce to decimals of the target; character operands concatenate.

### Flow control
`IF`, `WHEN`, `ELSE`, `ENDIF`, `SELECT`, `OTHER`, `ENDSL`, `DO`, `DOU`, `DOW`, `FOR`, `ENDDO`,
`ITER`, `LEAVE`, `GOTO`, `TAG`, `EXSR`, `BEGSR`, `ENDSR`, `RETURN`, `RETRN`, `CALL`, `CALLB`, `CALLP`,
`ON-ERROR`.

- `ON-ERROR` is a runtime error tail-trap (see §10). `SETON`/`SETOFF`/`*INLR` semantics in
  Indicators below.
- **Subprocedures**: P-specs (`P name` with `B`/`E` markers in column 24) delimit a subprocedure;
  the `B` and `E` marks become `BegProc`/`EndProc` sentinel statements and the block between them is
  re-parented into `RpgProgram.Subprocedures`. `CALLB`/`CALLP` invoke a subprocedure by name;
  `RETURN`/`RETRN` inside a subprocedure returns to the caller, while at the top level it ends the
  program. D-specs that appear between the `P B` and `P E` lines are procedure-local: they are
  declared in the program field namespace and bound positionally to the call arguments
  (`CALLP`/`CALLB` arguments are evaluated and written into the procedure's parameters in order).

### Indicators
`SETON`, `SETOFF`, and classic compound `*INxx` on indicators 01–99, `*INLR`. `*INLR` is
`Indicators[0]`: `SETON *INLR` (fixed `*INLR` in Factor 1) or free `seton *inlr` sets it, and once
set the main `Execute` loop terminates the program. `EVAL *INLR = *ON` likewise works (assignment
to an indicator reference). `BITON`/`BITOFF` take the bit number in Factor 1 (fallback Factor 2) of
the field named in the result; bits are numbered 1..length*8, MSB-first. `CHAIN`/`READ` with no
coded indicators default to `*IN01` (found / not-EOF) and `*IN02` (not found / EOF).

### File I/O
`OPEN`, `CLOSE`, `FORCE`, `CHAIN`, `READ`, `READE`, `READP`, `READPE`, `SETLL`, `SETGT`, `WRITE`,
`UPDATE`, `DELETE`, `EXFMT`, `READC`.

- File name resolves from Factor 2 (keyed ops) or Factor 1 (block ops), then the result; the
  runtime matches against opened file members by name.
- `CHAIN`/`READ*` copy the current record's fields into the context; `UpdateOpcode` requires a
  record to have been read (`cursor.Current`) and re-inserts preserving key values.
- `WRITE` builds values from the named format (default `*FILE`'s primary format).
- WORKSTN operations use the separate format/RRN contract in §12. EXFMT requires
  a declared workstation record; it no longer treats a database row as a display.

### Messaging
`DSPLY`, `SNDMSG`, `SNDPGMMSG`, `RCVMSG`.

## 6. Expressions

`RpgExpressionParser` implements a Pratt parser:

- Arithmetic `+ - * /` with standard precedence; unary `+`/`-`; parentheses; `NOT (...)`, `AND`, `OR`.
- Comparison operators: `= == *EQ EQ`, `<> != *NE NE`, `< *LT LT`, `<= *LE LE`, `> *GT GT`,
  `>= *GE GE`.
- Numeric and quoted literals (`'…'`), the special values `*BLANK`/`*BLANKS`, `*ZERO`/`*ZEROS`,
  `*YES`/`*ON`, `*NO`/`*OFF`, `*LOVAL`, `*HIVAL`, `*DATE`, `*TIME`, `*TIMESTAMP`.
- Field references (including arrays/dimensions and DS overlay elements via their own names),
  indicator references `*INnn`.
- Built-in functions `%len`, `%size`, `%subst`/`%substr`, `%trim`, `%triml`, `%trimr`, `%int`,
  `%dec`, `%char`, `%abs`. Arguments are separated by `,` or `:` (RPG list form), e.g.
  `%subst('HELLOWORLD':3:4)`.
- The `EQ..GE` word operators are valid as binary operators only; cases like `A EQ B` work,
  `EQ` standalone (e.g. after a field in a result column) is rejected at compile time.

## 7. Data handling

- **Fields**: standalone, DS elements, and fields lifted from record formats. Numeric fields
  (Zoned/Packed) store decimal values; character fields store padded strings (spaces initialize).
- **Data structures**: fixed-length buffers; `OVERLAY` forces an element to offset 1 (shared
  storage semantics — mutations on one overlay element are visible through the other).
- **Coercion** (`RpgRuntimeContext.Coerce`): decimals round to the field's precision; character
  values are trimmed/padded to the field length.
- **Entry parameters**: `PLIST *ENTRY` with `PARM` targets binds `Run(params)` values into fields.
- **Prototypes**: a D-spec with `PR` in the type column registers an `RpgPrototype` (name, optional
  `EXTPROC('name')`/`EXTPGM('name')` external name). Following typeless D-specs and
  explicit A/P rows marked PARM become formal parameters. Named formals use
  private generated storage; unnamed formals retain P1/P2 where available. `CALLP`
  resolves the callee name against prototypes before falling back to a bare external call, so
  `callp calc(1:2);` with an `EXTPROC('INNER')` prototype calls the host program `INNER`.
- **Procedure pointers**: `PROCPTR` on a standalone field stores a program name; `CALLP name(args)`
  against such a field reads it and calls the named program (so `eval FN = 'REALPGM'; callp FN(5);`
  invokes `REALPGM`).
- **PLIST writeback**: fixed `CALL 'pgm' PL` (and free `CALLP 'pgm'(PL)`) forwards the `PLIST`'s
  `PARM` fields to the host; when the host returns `RpgExternalCallResult.UpdatedParameters`, each
  entry is written back into the corresponding `PARM` field (by-reference semantics).
- **Typed system API calls**: built-in IPCAPI targets can borrow fixed CHAR, signed INT and
  packed/decimal scalars from PLIST, unprototyped and supported prototyped inline CALLP/CALLB arguments.
  Aliases remain live, outputs are checked before service mutation, and borrowed
  cells/factories expire when the synchronous call ends. Queue APIs now receive
  directly into RPG fields. Character byte widths, receiver conversion, quotas,
  constants, prototype limitations and the host callback are documented in
  [RPG scalar API calls](api.md#rpg-scalar-api-calls). Ordinary targets retain
  their semantic call/writeback behavior.
  Scalar prototypes check exact formal/actual layouts and keep literals immutable;
  see [scalar prototype calls](api.md#scalar-prototype-calls). Argument separators
  respect nested expressions and quoted colons; doubled apostrophes decode literally.

## 8. Files

`RpgFileCursor` wraps the store's keyed reader: `ReadNext`/`ReadPrior` (plain and keyed variants),
`Chain(search)`, `SetLowerBound`/`SetUpperBound` (`*INxx`-free; used by `SETLL`/`SETGT`), and
`Reload()` after writes. Key fields are `Sequence > 0` fields of the primary format (from the DDS
`K`-row markers). `_host.Files` (`ISqlFileStore`) provides `Insert`, `RowCount`, and
`ReadKeyPrefix` used by the interpreter.

## 9. Program object model

`RpgProgram` — name/library, expanded `Source` and original snapshots/locations,
`IsFreeForm`, `Fields`, `DataStructures`, `EntryPlist`,
`MainStatements`, `Subroutines`, `Subprocedures`, `Prototypes`. `RpgStatement` — `Opcode`,
`Factor1`/`Factor2`/`Result`, `Length`, `Value` (free operand text), `Conditions`, `Label`,
`Indicator1`/`Indicator2`, `LineNumber`/original `Location`, `Jump` (else/branch target index), `End` (block end index). `RpgField` —
name, kind (Zoned/Packed/Character/ProcPtr), length, decimals, `InitialValue`, `IsArray`/`Dimension`,
`IsDataStructure`, `Varying`, `Source`. Subroutines are `BEGSR…ENDSR` blocks re-parented out of the
main stream, and subprocedures are P-spec blocks re-parented into `Subprocedures`
(`RpgSubprocedure` — name, `Statements`, `Parameters`). Prototypes are D-spec `PR` rows in
`Prototypes` (`RpgPrototype` — name, `ExternalName`, `Parameters`, `ParameterKeywords`).

## 10. Limits and divergence notes

- Source preprocessing supports `/COPY`, `/INCLUDE`, `/DEFINE`, `/UNDEFINE`,
  `/IF`/`/ELSEIF`/`/ELSE`/`/ENDIF`, and listing-only `/TITLE`/`/EJECT`/`/SPACE`.
  Member/stream resolution, bounds, unsupported syntax, compiled snapshots and
  original diagnostics are specified in [source preprocessing](source-preprocessing.md#rpg-directives).
- The emulator's column layout uses result field width 11 in the 48–58
  band; the length column 53–58 overlaps the result band and is only meaningful when the result is
  blank.
- WORKSTN F specs additionally accept IBM's native column-6 layout. This does not
  extend native fixed D/C column coverage; native F specs can use `/FREE` calculations.
- Fixed specifications require the six leading sequence/blank columns and their
  D/P/C layout. Free text such as `dsply DATA` and `callp PRIVATE()` is not a fixed
  specification merely because column 7 contains D or P. Leading `*` comment lines
  remain an extension unless they start `**free`; `//` comments are also supported.
- Bit fields initialize to blanks (`0x20`) in the emulation's ASCII representation; `BITON` ORs
  into the existing value.
- `IF` without a matching `ENDIF`, and missing `ENDDO`/`ENDSL`, are compile-time errors.
- **`ON-ERROR` uses tail-trap semantics, not IBM's error-handler subroutines.** A runtime error
  (`RpgRuntimeException`, e.g. division by zero) jumps to the first `ON-ERROR` that follows it in
  the current statement list; the statements until the next `ON-ERROR` (or the list end) run as the
  handler; in normal flow an `ON-ERROR` statement bypasses its own handler, so multiple
  `ON-ERROR` partitions partition the block. No `ON-ERROR` present (or an error raised inside a
  handler) propagates the exception.
  Status filters on `ON-ERROR` and return-value operands on `RETURN`/`RETRN` are
  unsupported and fail compilation rather than being silently ignored.
- **Subprocedures share the program's field namespace**: procedure D-specs are registered in
  `program.Fields` and mirrored as the procedure's `Parameters`, so a global field and a
  procedure-local of the same name collide.
- **`**free` is global**: a program containing `**free` is free-form throughout, so fixed-form
  C-specs (e.g. a fixed `PLIST`) cannot coexist with `**free` in the same source — use fixed-form
  `CALL` with `PLIST` for that. `PLIST` parameter forwarding therefore lives in fixed form;
  prototypes and procedure pointers cover the free-form calling cases.
- **`*INLR` honored**: setting `*INLR` (via `SETON *INLR`, `seton *inlr`, or assigning it with
  `EVAL`) stops the main `Execute` loop, matching classic RPG's last-record indicator.

## 11. Compilation diagnostics and source coverage

The [generated opcode table](compatibility-matrix.md#observed-rpg-opcode-parser-mappings)
records fixed and free parser recognition separately. Unknown spellings produce
`RpgCompileException`, including code after RETURN and other unreachable source.
The fixed operation field is seven columns wide; `ON-ERROR` and `SNDPGMMSG` are
free-only extensions. F/H/I/E/O specifications, free declarations, MONITOR/ENDMON,
embedded SQL, COMMIT/ROLBK and unimplemented operation variants fail explicitly.
This reports missing implementation; these release requirements remain open.

Structural validation requires properly nested IF, loop and SELECT boundaries;
repeated ELSE/OTHER, WHEN after OTHER, branch boundaries inside an unclosed child
block and ITER/LEAVE outside a loop fail. Control nesting is bounded to 256 blocks.
BEGSR/ENDSR and P-spec B/E boundaries must balance, cannot cross control blocks,
and cannot nest a routine of the same kind. Procedure end names must match when
provided. Subroutine/procedure names cannot be empty or duplicated. TAG names
must be unique within a main, subroutine or procedure statement list. Missing
GOTO/EXSR targets still report runtime errors when reached.

Fixed BEGSR/EXSR/GOTO/TAG accept one name in factor 1, factor 2 or result as emulator
extensions, normalize it into the IR name slot, and reject competing operands.
This gives distinct fixed subroutines their actual names. Blank fixed C rows,
empty free semicolon statements, comments and supported listing directives remain
harmless. The compiler retains the existing fixed D/P declarations in global free
programs as a hybrid extension. Unsupported D-spec type codes fail; complete
declaration keyword, expression, operand/type and native indicator validation is
still required by C09.

Errors retain member/line/column and include chains, including compiled maps.
CRTBNDRPG creates no program after a compilation failure. CALL validates stored
legacy source before executing it and reports invalid source as a command error.
`RpgCompilerDiagnosticsTests` exercises rejection, valid nested/mixed source,
column classification, named routines, scoped labels and the no-side-effect
compile/create/call boundary. The terminal fixture verifies CALLP PRIVATE/DSPLY DATA
and confirms unsupported RPGBAD source creates no callable program.

## 12. WORKSTN display files and subfiles

`CRTBNDRPG` imports the external fields and formats of an existing compiled DSPF.
A minimal program is:

```rpgle
**free
dcl-f SCREEN workstn extdesc('QGPL/SCREEN') extfile(*extdesc);
eval NAME = 'START';
exfmt ENTRY;
dsply %trim(NAME);
seton LR;
```

The supported declaration is a full-procedural, externally described WORKSTN.
Free DCL-F accepts WORKSTN, USROPN, USAGE(*INPUT:*OUTPUT), constant EXTDESC/EXTFILE,
RENAME(external:local), PREFIX(prefix), and SFILE(format:rrn). EXTDESC selects the
compile-time description; the runtime default is the internal filename. Explicit
EXTFILE(*EXTDESC) uses the resolved compiled description; a constant EXTFILE may
select another compatible DSPF. Fixed F specs accept native column 6 or the
existing column-7 extension, type C/F or O, E description, WORKSTN, and the same
applicable keywords. Continuation specs and multiline free declarations retain
original diagnostic locations. Unknown options, dynamic file names, result data
structures, operation extenders and WORKSTN READP/READE/READPE/SETLL/SETGT/DELETE fail compilation.

Free DCL-S supports fixed CHAR, PACKED, ZONED, INT(3/5/10/20), IND, DATE, TIME,
and TIMESTAMP, with optional constant INZ. CHAR also accepts CCSID(*HEX) or
CCSID(65535) for opaque bytes and an exact-width hexadecimal INZ. Other CCSID
declarations are unsupported. Declarations precede calculations;
arrays, free DS/prototypes, varying strings, unsigned types and date/time format
options are outside this declaration subset. Existing compatible fixed D fields
can receive external fields. RENAME separates format namespaces and PREFIX separates
field namespaces across files; incompatible shared fields fail compilation.

Hexadecimal expressions such as X'00FF8000' preserve their bytes across job
CCSIDs. %SUBST on opaque storage returns a byte slice using one-based positions;
text substrings retain the existing string behavior. Raw buffers compare byte
sequences; a raw slice compared with a text literal decodes that selected slice
and uses blank-padded text comparison. Whole raw headers need no decoding.
Signed INT API references use 1/2/4/8-byte storage for precision 3/5/10/20,
checking exact ranges before writes. [Native message APIs](api.md#native-message-program-apis)
can send, receive, reply and retrieve directly from RPG; the text SNDPGMMSG/SNDMSG
extensions still use their existing callbacks. Full native messaging opcodes
and asynchronous I/O remain open. RpgNativeStorageTests covers binary vectors,
range checks, aliases/lifetimes, raw initialization/slices and aggregate quotas.
IBM documents [character byte storage and *HEX](https://www.ibm.com/docs/en/i/7.5.0?topic=type-character-format)
and [byte-oriented string operations](https://www.ibm.com/docs/en/i/7.5.0?topic=cdt-processing-string-data-by-natural-size-each-character).

EXFMT writes and reads the same non-subfile format. Separate WRITE followed by
READ of that format or its file also works. Character, packed numeric, date, time
and timestamp fields transfer through the shared editor. CF/Enter transfer input
and both-usage fields; CA/attention keys preserve the program's current variables.
Only response indicators are returned, preserving output selectors changed after
WRITE. Character comparisons pad the shorter operand with blanks; decimal literals retain their written value. Input text such as `*BLANK` remains literal text. Character
transfers enforce job-CCSID byte widths and supported single-cell glyphs; opaque
SFLMSGKEY fields remain four-byte buffers, including high-bit keys, through CL
parameters and subfile CHAIN. General native byte-backed RPG storage and collating
sequences remain broader C09 work.

Ordinary subfiles require SFILE for every subfile, with a declared numeric RRN
scalar, zero scale and enough digits for SFLSIZ. For example:

```rpgle
**free
dcl-f SCREEN workstn extdesc('QGPL/SCREEN') extfile(*extdesc)
  sfile(ROWS:RRN);
dcl-s RRN packed(4:0);
eval RRN = 1;
eval VALUE = 'FIRST';
write ROWS;
exfmt CTL;
readc ROWS;
dow not %eof(SCREEN);
  dsply %char(RRN) + ':' + %trim(VALUE);
  update ROWS;
  readc ROWS;
enddo;
seton LR;
```

WRITE uses RRN and rejects duplicate or out-of-capacity rows. READC reads edited
or SFLNXTCHG rows in ascending RRN order; CHAIN uses an integer RRN. Successful
READC/CHAIN returns all row fields, including hidden values, and sets RRN. UPDATE
requires a successful read of that subfile and replaces its current row. Missing
rows and READC EOF preserve the receivers and RRN. Receiver normalization finishes
before READC acknowledges the change or advances its position, so a conversion
failure can be retried. The scan resets on a display input cycle or EOF;
SFLNXTCHG updates behind the current position are available on the next scan.
Subfile clear/initialize resets the affected position. `%FOUND`/`%FOUND(file)` and
`%EOF`/`%EOF(file)` report the implemented retrieval operations; successful CHAIN
clears the named file's EOF without clearing the unqualified EOF. These statuses
are scoped to this workstation implementation. Exact native numeric status codes,
full subfile-full status behavior and complete database status parity remain open.

Files auto-open at invocation initialization, unless USROPN is specified. Explicit
OPEN/CLOSE operates on the file name. RETURN retains open files, rows and storage
in the activation group; each subsequent CALL reconnects them to its current
terminal. LR, *NEW return, RCLACTGRP and job end release the file allocations.
Private QTEMP DSPFs use the owning job namespace. Opening checks source-bound
compiled layouts; identity, layout and authority are rechecked during I/O and
while the terminal is suspended. Shared-read file and parent-library leases last
until close. Headless/batch/communication execution cannot open a workstation.
Disconnect/ENDJOB cancels the wait before program-frame and QTEMP cleanup.

Message subfiles use the existing live program-queue snapshots, explicit opaque
keys/SFLPGMQ, or control-format SFLINZ, including the current program selector `*`.
Paging and saved second-level F1 help use the same terminal viewer as CL. A called
CL program can send predefined messages to the RPG caller's queue with
TOPGMQ(*PRV); this does not expand RPG's text-only SNDPGMMSG/RCVMSG callbacks into a
complete native messaging implementation. Printer output, commitment coordination,
ILE procedure queue selection, asynchronous/multiple-device I/O, display overrides
and shared display ODPs remain open.

`RpgDisplayFileTests`, the socket tests in `SessionServerTests`, and the compiled
PTY fixture cover these contracts. Primary IBM references:
[EXFMT](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-exfmt-writethen-read-format),
[SFILE](https://www.ibm.com/docs/en/i/7.6.0?topic=keywords-sfilerecformatrrnfield),
[READC](https://www.ibm.com/docs/en/i/7.4.0?topic=codes-readc-read-next-changed-record),
[SFLNXTCHG](https://www.ibm.com/docs/en/i/7.5.0?topic=80-sflnxtchg-subfile-next-changed-keyword-display-files),
[%EOF](https://www.ibm.com/docs/en/i/7.4.0?topic=functions-eof-return-end-beginning-file-condition),
and the [ILE RPG Reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf)
for character comparison padding. These links establish the native contracts;
the limits above describe the emulator's tested subset.


Native system-value acceptance also exercises both IF branches after changing a
value between calls. False IF now enters the ELSE body directly; the ELSE
instruction on the true path skips that body. Fixed/free false/true paths and
nested joins are covered by RpgTests and RpgBranchTests. Direct QWCRSVAL callers
use raw byte comparisons and receive live system values without a CL wrapper;
see [system values](system-values.md).

### Raw EVAL result indicators

EVAL skips numeric result-indicator comparison when the instruction has no result
indicators. This preserves arbitrary binary CHAR assignments, including raw
16-byte job IDs copied by `%SUBST`, without attempting text/decimal conversion.
RpgNativeStorageTests covers high-bit bytes at CCSIDs 37/1208; JobApiIntegrationTests
executes direct RPG list/read/ID/retrieve calls against [native job APIs](job-apis.md).

### Writable scalar substrings

EVAL now accepts `%SUBST(field:start[:length])` as an assignment target for scalar
character fields. Raw CHAR uses byte positions and the job CCSID for text inputs;
raw source buffers copy without decoding. Unselected bytes remain unchanged,
short sources pad with job spaces and long sources truncate to the selected span.
Omitted length selects the remainder. Bounds and constant/reference writes are
checked before changing storage; literal `*BLANK` text remains literal.
RpgNativeStorageTests covers two CCSIDs, invalid ranges, raw bytes, tails and
literal text. Compiled CL/direct RPG [crypto callers](cryptography-apis.md) use
these writes to construct real native descriptors. Array/nested substring lvalues
and the broader C09 storage/language contracts remain open.
