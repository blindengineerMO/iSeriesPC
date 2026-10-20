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
2. **Extract data / ReadSpecs** — bind trailing CTDATA records separately from code while preserving physical source positions and prepared comments.
3. **Free-form detection** — true if any line starts with `**free`; repeated/malformed markers fail.
4. **ParseDeclarations** — collect supported fixed F/D specs, free DCL-F/DCL-S/DCL-DS declarations,
   external display/database input fields, and data structures. WORKSTN compilation requires a display resolver;
   DISK input compilation requires a database resolver;
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

KLIST/KFLD declarations additionally accept standard RPG IV C-spec positions:
`C` in column 6, factor 1 in 12–25, opcode in 26–35 and result in 50–63. A compiler
declaration pass binds scalar fields in source/procedure order and removes these
declarations from executable statements. This does not add standard layouts for
every calculation operation. See [RPG file keys](rpg-file-keys.md).

## 4. Free form

Free statements accept any indentation, including column 1 and column 7. `**free` begins global free-form source and
free statements continue until the end of the source (no `**endfree` terminator). The block
directives `/free` and `/end-free` switch free-form parsing on/off between them, letting free
statements be interleaved with fixed C-specs in the same program (`/free` presence alone does not
make the whole program free). `/FREE` and `/END-FREE` must balance, cannot nest and
cannot appear in a `**free` program. Each statement ends with `;` (the interpreter's
legacy optional final semicolon remains accepted). Operands are bound per opcode:
`IF`/`WHEN` keep the whole expression as the condition; `MOVE`/`MOVEL`/`MOVA` bind Factor 1 = first
operand, result = remaining operand(s). Keyed operations parse a scalar, KLIST,
parenthesized expression list or `%KDS` followed by a file name. Nested functions
and quoted separators remain intact. READ/READP/READC accept just the file/record
name; READE/READPE can use `*KEY`. Extra operands fail compilation. `EVAL`, `CALLP`, `FOR`, `DO`/`DOU`/`DOW`,
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
`MONITOR`, `ON-ERROR`, `ENDMON`.

- `MONITOR`/`ON-ERROR`/`ENDMON` provide structured numeric status handling; bare `ON-ERROR` retains a tail-trap extension (see §10). `SETON`/`SETOFF`/`*INLR` semantics in
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
- `CHAIN`/`READ*` copy the current record's fields into the context. SQLite `UPDATE`
  and current-record `DELETE` require a successful read and target its physical
  member/RRN, including duplicate keys and unkeyed files. UPDATE uses current
  program values, including changed keys. A successful mutation consumes the
  target; stale before images are rejected and require another read.
- `WRITE` builds values from the named format (default `*FILE`'s primary format).
- WORKSTN operations use the separate format/RRN contract in §12. EXFMT requires
  a declared workstation record; it no longer treats a database row as a display.

SQLite disk file operations now use the shared bounded database cursor for
forward/reverse navigation, CHAIN, lower/upper limits and adjacent equal reads.
Only one record and a sort tuple remain in the RPG adapter. Reload validates
live authority/schema without replacing the input buffer. Exact keys, nulls,
descending order, cancellation and service commit/rollback positions share the
CL/database backend. Disk EOF/FOUND status follows these operations. Custom
non-SQLite providers may retain the older materialized fallback. See
[cursor navigation and acceptance limits](cursor-navigation.md).

Compound search arguments and keyed DELETE are described in
[RPG file keys](rpg-file-keys.md). Keyed deletion selects one physical record and
updates `%FOUND`, preserving program input fields and the prior position on a
miss. Its search/mutation/position update share the commitment gate. Native null
attributes, disk commitment declarations/open flags, RRN allocation/writeback, complete DS layouts and
strict-key/extender/golden acceptance remain open. Unkeyed CHAIN/SETLL/SETGT/DELETE
now use physical RRNs, including sparse slots, typed zero-decimal operands,
explicit host arrival mode and job commitment positions. See
[relative record access and remaining native declarations/allocator work](relative-records.md).

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
- **Legacy shorthand procedure pointers**: `PROCPTR` on a standalone field stores a program name; `CALLP name(args)`
  against such a field reads it and calls the named program (so `eval FN = 'REALPGM'; callp FN(5);`
  invokes `REALPGM`). Native `POINTER(*PROC)`/traditional `*` with `PROCPTR`
  instead use the [opaque procedure-address contract](rpg-procedure-pointers.md).
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

`RpgFileCursor` uses the bounded SQLite navigation protocol (or a custom provider's
legacy keyed reader): `ReadNext`/`ReadPrior` (plain and keyed variants),
`Chain(search)`, `SetLowerBound`/`SetUpperBound` (`*INxx`-free; used by `SETLL`/`SETGT`), and
`Reload()` validates the live binding after writes. Key fields are `Sequence > 0` fields of the primary format (from the DDS
`K`-row markers). `_host.Files` (`IRpgFileAccess`) provides `Insert`, `RowCount`, and
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
- The older project column layout uses result field width 11 in the 48–58
  band; the length column 53–58 overlaps the result band and is only meaningful when the result is
  blank.
- Native column-6 WORKSTN F, standalone/DS D and supported C specifications have
  separate parsers. See the traditional definition/calculation contracts below;
  complete native DS/procedure/cycle specifications remain open.
- Older project fixed specifications require six leading sequence/blank columns
  and their D/P/C layout. Free text such as `dsply DATA` and `callp PRIVATE()` is not a fixed
  specification merely because column 7 contains D or P. Leading `*` comment lines
  remain an extension unless they start `**free`; `//` comments are also supported.
- Bit fields initialize to blanks (`0x20`) in the emulation's ASCII representation; `BITON` ORs
  into the existing value.
- `IF` without a matching `ENDIF`, and missing `ENDDO`/`ENDSL`, are compile-time errors.
- **Structured MONITOR and legacy ON-ERROR are distinct.** MONITOR/ON-ERROR/ENDMON
  support bounded numeric filters, nested handlers and file/program error groups.
  E or an ER indicator handles expected file errors before MONITOR. An unfiltered
  ON-ERROR outside MONITOR retains the earlier tail-trap extension. See
  [numeric status and monitor rules](rpg-file-status.md). RETURN/RETRN value
  operands remain unsupported.
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
free-only extensions. Unsupported F/H/I/E/O specifications, declaration variants,
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
are scoped to this workstation implementation. Numeric statuses now cover a bounded subset described in
[status rules](rpg-file-status.md); complete native display/database status parity
remains open.

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

## 13. Declared database input files

Externally described input DISK F-specs and free DCL-F now bind saved field/format
layouts, explicit keyed/arrival modes, EXTDESC/EXTFILE, RENAME/PREFIX and USROPN.
Hosted calls retain file position/open state until LR or activation cleanup;
%OPEN, live format/authority checks and external-name overrides are covered.
Declared update/output modes are described below; native commitment flags remain open. See
[the disk file contract and evidence](rpg-disk-files.md).

## 14. Declared noncommit update files

DISK USAGE(*UPDATE) and USAGE(*DELETE), plus fixed U files, now use retained
update input. N on CHAIN/READ/READP/READE/READPE retrieves inquiry input; UNLOCK
releases input without moving position. UPDATE requires the record format and
maps prefixed fields back to columns; delete access is explicit. Saved access
modes preserve earlier input snapshots and mode-compatible ODP sharing. See
[declared database files](rpg-disk-files.md). COMMIT flags/isolation,
Complete native status coverage, SQL/trigger coordination and independent
native acceptance remain open.


## 15. Declared database output

USAGE(*OUTPUT), alone or combined with input/update/delete, enables record-format
WRITE using all imported fields and RENAME/PREFIX mapping. Fixed O files and
column-20 A on I/U files permit addition. Output-only handles require Add and
object-operation authority without reading existing rows. Open/close, retained
calls, mode-compatible sharing, overrides, logical selection/uniqueness and
job-attributed journal capture use the shared file services. Version-3 bindings
preserve prior input/update metadata and reject output privileges in old versions.
See [declared database files](rpg-disk-files.md#output-and-record-addition).
RECNO-directed output, initialized/deleted slots, reuse/generation policy,
commitment flags, DS output buffers and full native error/status behavior remain open.


## 16. Database record-wait policy

Declared update/delete DISK opens now use the runtime file or OVRDBF WAITRCD.
Timed and immediate input, cancellable indefinite overrides, all five update
input operations and physical mutation waits share the lock service. File/LF
attributes default to 60 seconds; the legacy explicit service API defaults to
zero. Open paths retain their policy and share only compatible modes/waits.
See [record waits](record-waits.md) for command values, persistence and limits.
Numeric status 1218, E/%ERROR/%STATUS and structured MONITOR are implemented as
a bounded subset; see [status rules and limits](rpg-file-status.md). Commitment
isolation/open flags and complete SQL/trigger locking remain open.


## 17. Native free-form data structures

DCL-DS/END-DS now supplies native byte storage, qualified fields, POS/OVERLAY,
arrays and LIKEDS. INT/packed/zoned/float and default ISO datetime fields share
live byte images; uninitialized numeric storage remains blank and can raise 907.
Borrowed API receivers support structures and typed subfields with range-aware
alias protection. See [the native structure contract](rpg-native-structures.md).
[Native fixed DS definitions](rpg-fixed-structures.md) now use the same byte
engine. The historical project D-spec text overlays remain a separate extension.
External structures, complete native procedure/cycle forms, full
entry/prototype ABI and extended feedback remain open. Multiple-occurrence structures and indicator banks are described
in [the occurrence contract](rpg-occurrences.md).

## 18. Native feedback structures

Free file declarations now support INFDS with the 80-byte file feedback area.
One PSDS supports reserved and positional fields for program errors, previous
status, entry parameter count, last-file snapshots and initial job identity.
Feedback uses the same native bytes as ordinary fields and overlays. E/ER and
MONITOR handlers see the updated feedback; EXSR preserves original error details
and CALL failures identify the caller line and called target. See
[the feedback contract](rpg-feedback-structures.md) for populated fields and
limits. Extended INFDS/PSDS areas, native fixed feedback D-specs and
complete cycle/error semantics remain open.

## 19. Error subroutines

INFSR and procedure-local *PSSR now dispatch after E/ER and matching MONITOR
groups. Explicit EXSR, automatic default failure, RETURN/GOTO recovery,
six-byte main ENDSR return points and *CANCL cancellation are supported.
Abnormal exit closes retained file owners and discards failed activation state.
See [the error-subroutine contract](rpg-error-subroutines.md) for scope, bounds,
source diagnostics and native cycle/ILE requirements that remain open.

## 20. File-local indicator structures

INDDS now binds a named native structure, optionally OCCURS, to a WORKSTN file with DDS
INDARA. Default storage contains 99 character-zero bytes; named fields, arrays,
overlays and borrowed API views share that storage. Conditioning and response
flags remain separate from global *IN, and files may explicitly share a bank.
Version-two saved bindings include INDARA in their signature while version-one
snapshots remain readable. See [the indicator contract](rpg-indicator-structures.md)
for initialization, validation, lifetime and remaining native/PRINTER scope.

## 21. Multiple-occurrence structures

OCCURS allocates independent native byte images selected with EVAL %OCCUR.
Subfields, value images, CLEAR and %KDS use the active occurrence; CLEAR *ALL
clears all images and selects occurrence 1. %SIZE(*ALL) and whole-structure native
API parameters include all occurrences. Invalid selection reports status 122.
RETURN retains selection and storage, while LR/reclaim create fresh state.
See [the occurrence contract](rpg-occurrences.md) for bounds, parameter addresses,
INDDS exchange, verification and remaining declaration/fixed/ABI requirements.

## 22. Named constants and declaration evaluation

DCL-C supports direct and CONST forms with inferred literal types. Earlier
constants and pure ABS/LEN/SIZE/ELEM built-ins supply dimensions, native
LEN/DIM/OCCURS/POS/OVERLAY and INZ. Layout queries include final INFDS/INDDS sizes
and LIKEDS chains. Compiled constants remain immutable, and invalid definitions
retain include locations. ELEM now correctly returns a multiple-occurrence
structure's occurrence count. See [the declaration contract](rpg-declaration-constants.md)
for exact functions, bounds, evidence and remaining forward/fixed/array/ABI work.

## 23. Standalone runtime arrays

DCL-S DIM now allocates native contiguous element storage with typed defaults and
INZ, whole-array assignment/arithmetic, layout queries and borrowed API buffers.
Native indexes enforce non-float zero-decimal values; INT truncates numeric and
character fractions. Scaled whole-array API buffers expose raw CHAR metadata
with zero decimal positions. See [the standalone array contract](rpg-standalone-arrays.md)
for lifetimes, bounds, verification and remaining array/ABI requirements.

## 24. Array sorting, search, totals and slices

Free SORTA supports declared ASCEND/DESCEND sequences and compatible A/D
extenders. Character order follows native CCSID bytes; overlay keys move whole
base records. Five %LOOKUP variants implement sequence/range/duplicate rules,
%XFOOT totals numeric arrays and expressions, and %SUBARR supplies bounded EVAL,
sort and total operands. Subarray call arguments use detached copies. See
[the array operation contract](rpg-array-operations.md) for status codes,
verification and remaining fixed/keyed/ABI/native acceptance requirements.

## 25. Keyed data structure arrays

SORTA now accepts DS(*).KEY, explicitly indexed scalar-array subfields and
DS %FIELDS lists. Selected %SUBARR ranges preserve other records, and complete
DS bytes move together. Exact %LOOKUP searches keyed DS arrays sequentially;
relative variants are rejected. Keyed forms do not create ordinary array values
or writable API references. See [the keyed array contract](rpg-keyed-arrays.md)
for direction, source/type/range checks, verification and remaining nested,
collation, fixed/ABI and independent native acceptance requirements.

## 26. Standalone compile-time arrays

CTDATA/PERRCD and numeric EXTFMT(S/L/R) load trailing named or declaration-order
records into native standalone arrays. DIM(*CTDATA) infers a fixed dimension for
later layout constants. Data bypasses comments/directives, preserves source
origins and saved snapshots, and retains normal RETURN/LR/reclaim storage rules.
See [the compile-time array contract](rpg-compile-time-arrays.md) for values,
bounds, sequence/CCSID checks and remaining fixed/DS/pre-runtime scope.

## 27. Alternating compile-time arrays

ALT(main) loads paired main/alternate source entries into independent native
arrays. Either declaration order is supported; PERRCD on the main counts pairs,
and each member has its own type, width, EXTFMT and sequence. Both can infer
matching dimensions from the main section. SORTA and CLEAR affect only their
selected array. See [the alternating array contract](rpg-alternating-arrays.md)
for source/layout rules, saved-program lifecycle, byte vectors and remaining
unequal-dimension, fixed/DS, pre-runtime and native acceptance scope.

## 28. Traditional standalone definitions

Native D-spec columns now declare standalone S fields/arrays and C constants,
with the existing shifted-column convention also accepted. Type/default inference,
right-justified length/decimals, keyword continuations, CTDATA/ALT and inferred
counts enter the shared strict declaration pipeline. Fixed comments and commentary
columns cannot change quote/data state. See the
[traditional definition contract](rpg-fixed-declarations.md) for column layout,
type bytes, saved-program/API evidence and remaining external DS/procedure,
calculation, continued-literal/name and wider ABI/native acceptance requirements.

## 29. Native standalone scalars

Free and supported traditional standalone scalars now own native bytes while
retaining scalar identity. Integer width, decimal scale, float precision,
character CCSID/byte assignment, byte %SUBST and atomic conversion use the DS/
array codec. Borrowed entry fields retain live aliases and caller ranges,
readonly metadata and receiver prevalidation, then detach on return/error.
See [the scalar storage contract](rpg-native-scalars.md) for byte vectors,
queue/CL/API and saved-program evidence, bounds and remaining type/ABI work.

## 30. Traditional calculation specifications

Native C in column 6 now uses the ten-column operation field and physical
factor/result/indicator positions. Supported arithmetic, COMP, indicators,
extended expressions/continuations, structured loops, subroutines, CALL lists,
entry parameters and declared file operations enter the shared runtime.
Stored-result flags, job-byte COMP, DO pretests, dynamic program names and
ordered/aliased parameters have explicit coverage. See
[the traditional calculation contract](rpg-fixed-calculations.md) for columns,
operations, API/CL/file evidence and remaining cycle/opcode/ILE/native acceptance.

## 31. Native array searches and column-limited declarations

Native LOOKUP now supports exact/nearest array searches, result indicators,
captured starting-index writeback, native byte comparison and non-float numeric
coefficients. %LOOKUPNE searches scalar/keyed DS arrays sequentially. LOOKUP
and SETLL supply independent global/per-file %FOUND/%EQUAL state. Column-limited
global DCL-S/C/F/DS declarations can precede native calculations. See
[the array search contract](rpg-array-search.md) for physical columns, duplicate
rules, index-on-miss reference conflict, source/API/CL evidence and remaining
table-entry/collation/ABI/native acceptance requirements. C09 remains partial.

## 32. Native tables and stored-program reference calls

Global TAB-named DIM fields now own tables and expose one current scalar
element. CTDATA/ALT loading, paired native LOOKUP and six %TLOOKUP variants
share native storage/search rules. Scalar mutation, layout queries and API
borrowing use the selected element; failed searches preserve selection.
Stored CL/RPG calls now receive available reference cells, preserving live
aliases and writes on errors. See [the table contract](rpg-tables.md) for source,
selection/lifetime rules, exact layout validation and remaining native/ILE/ABI
acceptance requirements. C09 remains partial.


## 33. Native fixed data structures

Traditional D-spec DS headers and subfields now share native byte storage with
free DCL-DS. From/To entries supply byte positions, packed fields support
PACKEVEN, and blank numeric subfield types infer zoned storage. Named/anonymous
structures, fillers, continuations, overlays, arrays, LIKEDS, OCCURS and feedback
bindings retain source positions and activation behavior. See
[the fixed structure contract](rpg-fixed-structures.md) for columns, bounds,
API/CL/file/terminal evidence and remaining external DS/procedure/ABI/native
acceptance requirements. C09 remains partial.


## 34. Native program signatures and CALLP(E)

Traditional PR/main PI and free DCL-PR/DCL-PI now describe global scalar program
references through EXTPGM. Formal metadata stays outside runtime variables;
PI fields bind exact native layouts, aliases and captured addresses. Free and
native CALLP(E) handle final-call errors while preserving argument-processing
statuses for MONITOR. External process aliases reject before execution and
returned cells are normalized before writeback. See
[the program signature contract](rpg-program-signatures.md) for source forms,
legacy compatibility, scalar ABI, error stages, bounds and remaining parameter
modes/ILE/native acceptance requirements. C09 remains partial.


## 35. CONST parameters and native character references

Native program signatures now support scalar CONST on PR/main PI. Exact native
variables retain live read-only aliases; literals, expressions and different
formats use private formal-width images prepared before dispatch. Native character
references may address a prefix of a longer actual while preserving caller tails.
Compile/runtime/API guards protect readonly storage, and external responses can
echo unchanged CONST values without writing them. See
[the CONST/reference contract](rpg-const-parameters.md) for conversion/error rules,
byte ranges, saved CL/RPG/API/process/terminal evidence and remaining parameter
modes/ILE/native acceptance requirements. C09 remains partial.

## 36. Optional native parameters and short-circuit guards

Native PR/main PI and free DCL-PR/DCL-PI now accept OPTIONS(*NOPASS) on trailing
scalar parameters, including CONST. Calls preserve actual counts through RPG,
CL, APIs and external processes; missing required or excess actuals reject.
%PASSED tests optional native PI presence, %PARMNUM returns real one-based PI
positions (including declaration constants), and %PARMS/PSDS retain actual counts.
AND/OR skip the right operand once their result is known. Retained activations
recompute presence; unpassed storage access/forwarding diagnoses emulator status
105 before target dispatch. See [optional parameters](rpg-optional-parameters.md)
for source rules, IBM reference sections and finite diagnostics. Other OPTIONS,
VALUE, native aggregate entry ABI, local/bound procedures and independent native
reference/scale acceptance remain open; C09 is partial.

## 37. Omitted native arguments

Native scalar signatures accept OPTIONS(*OMIT), optionally combined with
*NOPASS. *OMIT occupies one call position without allocating storage. %PARMS
and PSDS include the slot; %PASSED is false and %OMITTED is true. An absent
trailing parameter makes both presence built-ins false. Scoped omission
descriptors, retained PI rebinding, explicit nested forwarding, no-alias storage
rules and external JSON null response validation preserve this distinction.
At runtime, omitted storage access diagnoses emulator status 105; native API
adapters retain their checked storage rules. See [omitted arguments](rpg-omitted-parameters.md)
for source forms, IBM semantics, bounds, saved/process/terminal evidence and
remaining aggregate/local/ILE/API-pointer/native-acceptance requirements.
C09 remains partial.

## 38. Native local procedures, VALUE and scalar returns

P-spec B/E and DCL-PROC/END-PROC now define local compilation-unit procedures.
Explicit/automatic scalar PR/PI signatures bind local calls and expressions,
including VALUE copies, live references, CONST, NOPASS and OMIT. Independent
invocations support recursion, automatic local arrays/structures and retained
scalar/array STATIC storage; module globals, tables, occurrences, indicators and
file positions remain shared. Local PSDS/files and use of INFSR files reject.
Scalar RETURN values use the native codec; local PSSR can recover or return,
while abnormal completion/cancellation preserves call boundaries and cleanup.
See [the local procedure contract](rpg-local-procedures.md) for forms, scopes,
limits, saved/API/process/terminal evidence and remaining return-layout, foreign,
module/service-program binding, cycle/exception and native-reference work.
C09 remains partial.

## 39. Native fixed array and LIKEDS parameters

Native and free PR/PI accept fixed DIM arrays and LIKEDS structures/structure
arrays. References preserve indexed/qualified aliases, formal prefixes and
caller tails through nested calls. CONST inputs retain live aliases or use
formal-layout conversion images; local VALUE arrays/structures use private
mutable copies. Optional/omitted storage guards, source diagnostics, CL character
storage, native API buffers and checked external-process writeback apply to
these interfaces. See [aggregate parameters](rpg-aggregate-parameters.md) for
compatibility, bounds, saved/API/process/terminal evidence and remaining
additional return-layout, variable-size, table, foreign and module-binding work.
C09 remains partial.

## 40. Native aggregate returns and RTNPARM

Native/free procedure PR/PI support fixed DIM, LIKEDS and RTNPARM return layouts.
Scalar operands broadcast to arrays; missing elements receive type defaults;
surplus elements are ignored. Return images detach from callee storage, convert
through the declared codec and validate before caller assignment. RTNPARM shifts
runtime/declaration %PARMNUM and %PARMS while preserving explicit optional/omitted
bindings and main PSDS counts. Return images participate in invocation storage
bounds. Bound whole-structure CLEAR now validates and publishes through its
reference binding. See [return values](rpg-return-values.md) for source forms,
saved/API/process/terminal evidence and remaining return formats, precision,
variable-size, foreign, module-binding and native-reference work. C09 remains partial.

## 41. Native variable-size reference parameters

Native/free PR/PI support OPTIONS(*VARSIZE) on reference character/structure
parameters and arrays, including CONST, NOPASS and OMIT combinations. Declared
sizes remain distinct from actual capacity and character-array stride. Checked
byte slices preserve live aliases and caller tails; bounds, readonly status,
scoped lifetimes and actual-storage admission apply across program/procedure,
CL-to-RPG, native API and process boundaries. Entry state detaches into owned
snapshots. See [variable-size parameters](rpg-variable-parameters.md) for forms,
saved/API/process/terminal evidence and remaining dynamic/type/descriptor,
foreign and module-binding work. C09 remains partial.


## 42. Exact and adjusted native parameters

Native/free PR/PI support OPTIONS(*EXACT) checks for supported scalar/array
lengths, numeric positions, dimensions/order and LIKEDS type ancestry.
OPTIONS(*TRIM) and *RIGHTADJ prepare private character CONST/VALUE images before
dispatch, including arrays. Known declarations reject at compile time; remaining
value checks reject with status 105 before conversion or target side effects.
These caller-side attributes can appear on an explicit PR while its PI omits
them. Existing optional/omitted/RTNPARM modes, bounds and readonly lifetimes apply.
See [exact and adjusted parameters](rpg-parameter-options.md) for forms, precise
byte semantics, evidence and remaining type/ILE limitations. C09 remains partial.


## 43. Native LIKE types and exact ancestry

Native/free standalone, subfield, parameter and return declarations support
LIKE of declared supported native types, with explicit signed length
adjustments. Kind, length, scale and opaque/text mode are inherited; dimensions,
sort order, initialization and passing/storage flags remain explicit. Fixed
forms use signed Length columns and blank type/decimal columns. Type identities
survive LIKE chains, related subfields, indexed actuals and return metadata for
OPTIONS(*EXACT). Existing bounds, readonly and pre-dispatch checks apply.
See [LIKE declarations](rpg-like-declarations.md) for target availability, byte
semantics, evidence and remaining external/type/ILE work. C09 remains partial.


## 44. Forward native declaration types

LIKE resolves later fields, subfields, PI parameters and explicit/implicit
procedure returns. Source-order storage, initialization and overlay binding stay
separate from cached metadata resolution. Later local types and constants shadow
global names, and type identities remain stable for EXACT. Missing/void types,
cycles and dependencies beyond 64 active levels fail with source/include
locations before object creation. Compiler callbacks detach on every exit.
See [forward declarations](rpg-forward-declarations.md) for supported forms,
constant visibility, evidence and remaining forward-value/external/type/ILE work.
C09 remains partial.


## 45. Forward native LIKEDS binding

Structure copies, PR/PI parameters and returns can reference later named native
templates. Metadata and inherited initialization resolve separately while
retaining declaration identities for EXACT. DIM/OCCURS remain explicit on the
copy; subfield layouts, defaults and filler/overlay bytes are preserved.
Feedback extents propagate through forward chains, and local template names do
not change imported global layouts. Existing source diagnostics, depth/storage
limits and interface restrictions apply. See
[forward structures](rpg-forward-structures.md) for initialization modes,
evidence and remaining external/type/ILE work. C09 remains partial.


## 46. Data templates without runtime storage

Standalone and native structure definitions accept TEMPLATE in free and
traditional D-specs. LIKE/LIKEDS copies and PR/PI/return layouts retain metadata
and ancestry while ordinary copies allocate storage. Template buffers and
scalar slots are omitted, including large arrays and procedure-local layouts.
%SIZE/%ELEM use declared metadata; declaration-only %LEN/%DECPOS follow the
supported type rules. Runtime data operands and storage-dependent bindings
reject during compilation, with status-105 guards for direct runtime access.
Component/count limits still apply; template layouts do not consume runtime
allocation budgets. Complete CTDATA template behavior,
Wider external layouts and independent native acceptance remain open.
See [data templates](rpg-templates.md). C09 remains partial.


## 47. DISK LIKEREC and result data structures

LIKEREC defines qualified layouts from saved DISK record descriptions, with
input/output/all extracts and packed key-order structures for %KDS. Native
parameters and returns accept those layouts; LIKEDS and TEMPLATE retain record
origins. CHAIN/READ/READP/READE/READPE and WRITE/UPDATE accept checked structures
or indexed structure-array elements. Input stages conversions before publishing
bytes, preserves imported scalar fields and leaves receivers unchanged on a
miss or EOF. Output validates one image before file mutation. Existing locks,
status handling, native storage/lifetime limits and source locations apply.
Metadata lookup is lazy, cached and source-bound. WORKSTN result buffers are
described in section 51 and file copies in section 52. Multi-format
layouts, null extracts, broader CCSIDs
and independent native acceptance remain open. See
[record structures](rpg-record-structures.md). C09 remains partial.


## 48. DISK TEMPLATE, LIKEFILE and QUALIFIED definitions

DISK templates retain metadata without scalar fields or runtime file opens.
LIKEFILE requires an earlier parent and inherits supported file properties,
qualifying record names and requiring result-buffer I/O. Constants for EXTFILE
inherit; default runtime names, USROPN and TEMPLATE do not. Copies have independent
cursors and share record ancestry for LIKEREC and exact interfaces. BLOCK(*NO)
resolves input-parent blocking explicitly. Version-4 saved bindings validate
families and every recomputed request/layout; ordinary versions remain compatible.
Free and native fixed F declarations, continuations and standard C result
operands are covered. WORKSTN local files/variants, blocked I/O
and broader native/ILE acceptance remain open. See
[DISK file definitions](rpg-file-definitions.md). C09 remains partial.


## 49. DISK LIKEFILE parameters

Native PR/PI interfaces accept LIKEFILE of an earlier DISK definition with
known blocking. Local procedures and saved RPG programs borrow caller-owned
files, sharing cursor, locks, open state and named-file status. Qualified
parameter records supply LIKEREC layouts and forward return metadata.
NOPASS/OMIT, parameter-number inquiries and scoped forwarding are supported;
data storage/options and non-RPG targets reject. Caller INFDS and INFSR remain
active, including owner-frame RETURN/GOTO after unhandled errors. Version-5
main-PI bindings preserve parameter flags and verify restored layouts; earlier
binding versions remain supported. WORKSTN local instances,
blocked I/O, broader external/member settings and independent
native/ILE acceptance remain open. See [DISK file parameters](rpg-file-parameters.md).
C09 remains partial.


## 50. Procedure-local DISK files and STATIC storage

Native subprocedures accept local free and fixed DISK definitions. Automatic
files and locks close on every exit; STATIC files retain their cursor, locks,
open state and feedback across calls, recursion and LR until explicit close or
activation cleanup. Cycle globals reset on LR while procedure STATIC files and
data remain in the activation. Local I/O requires result structures and never
imports scalar fields. LIKEFILE copies, file parameters, local shadows, LIKEREC,
forward return metadata and EXACT ancestry retain scope and lifetime checks.
INFDS must match its file's scope and STATIC setting. Local INFSR handles normal
I/O and implicit-open errors with RETURN/GOTO to the owning procedure. Native
DS STATIC, including arrays, uses module storage rather than charging each
recursive frame. Version-6 saved bindings retain scope/storage flags and verify
all local definitions; the metadata writer enforces its byte bound while
serializing. Older versions remain supported. WORKSTN variants, blocked I/O,
broader external/member options and ILE linking/native acceptance remain open.
See [procedure-local DISK files](rpg-local-files.md). C09 remains partial.


## 51. WORKSTN LIKEREC and result data structures

Global WORKSTN formats supply native LIKEREC layouts with input/output/all
extracts and zoned display numerics. EXFMT requires ALL; READ/CHAIN/READC accept
INPUT or ALL; WRITE/UPDATE accept OUTPUT or ALL. Subfile input includes stored
hidden/output fields. Result structures bypass imported scalar fields, capture
array-element receivers once and retain native ancestry through LIKEDS, exact
parameters and returns. Input conversions, RRN and indicators validate before
host acknowledgement; failed conversions, CA responses, misses and EOF preserve
receiver bytes. Readonly input rejects before transfer. Saved layouts, renamed
formats, prefixed fields, job CCSIDs and opaque message keys retain existing
binding checks. File-name input requires one format. Section 52 adds templates
and copies. Zero-field extracts and multi-format/null
layouts remain open, along with broader CCSIDs and independent native acceptance.
See [WORKSTN record structures](rpg-workstation-records.md). C09 remains partial.


## 52. WORKSTN TEMPLATE, LIKEFILE and QUALIFIED definitions

Global WORKSTN templates retain record descriptions without importing fields or
opening displays. LIKEFILE inherits an earlier parent's device, usage, description,
prefixes, renames and constant runtime target, and implies qualified record names.
Runtime copies require result structures and retain independent display handles,
pending writes, subfile positions and status. USROPN, INDDS, INFDS and INFSR do not
inherit. Subfile templates omit the RRN from SFILE; copies must bind each subfile
to their own RRN. SFILE/RENAME arguments remain unqualified. Related copies keep
EXACT record ancestry through parameters, returns and forward metadata. Version-3
bindings verify parent graphs, inherited layouts and every reconstructed copy;
versions 1/2 remain supported. Metadata writing enforces its byte bound, and file
counts follow inherited devices. Zero-field
extracts, multi-format/null layouts and broader native/ILE acceptance remain
open. See [WORKSTN file definitions](rpg-workstation-definitions.md). C09 remains partial.


## 53. WORKSTN LIKEFILE parameters

Native PR/PI LIKEFILE parameters borrow caller display files across local and
saved RPG calls. Qualified parameter records supply LIKEREC buffers and forward
return layouts. Actual handles, pending writes, subfiles, RRN variables, indicator
areas and named status stay shared; OPEN/CLOSE use the owner's settings. Caller
INFDS and INFSR remain active, with RETURN/GOTO to owner frames after unhandled
errors and normal E/MONITOR precedence. NOPASS/OMIT, forwarding, lifetime checks
and retained-entry rebinding apply without exposing data storage or native bytes.
Non-RPG targets reject before side effects. Parameter names can shadow global
files of either device, and local data shadows preserve global file operands.
Version-4 main-PI bindings verify flags, families and reconstructed layouts;
versions 1–3 remain compatible. Multi-format/
null layouts, zero-field extracts and broader native/ILE acceptance remain open.
See [WORKSTN file parameters](rpg-workstation-parameters.md). C09 remains partial.


## 54. Procedure-local WORKSTN files

Native free and fixed procedures can own automatic or STATIC WORKSTN files.
Automatic handles close on procedure exit; STATIC handles share pending writes
across calls and recursion, survive LR, and close with activation/job cleanup.
Local files import no scalar fields and require data structures for data I/O.
Local file/format names shadow global files of either device. LIKEREC, templates,
copies, forward return metadata and borrowed LIKEFILE parameters retain checked
layouts and actual-owner INDDS/INFDS/INFSR/RRN behavior. INFDS must match the
file's scope and duration. Version-5 saved WORKSTN bindings verify scoped
definitions, storage flags, families and runtime targets; versions 1–4 remain
supported. Source and separate description removal are supported.
See [procedure-local WORKSTN files](rpg-local-workstations.md) for the contract.
C09 remains partial; zero-field extracts, multi-format/null layouts,
blocked I/O, broader external/member/CCSID options, ILE linking and independent
native golden/scale acceptance remain open.


## 55. Nested native data structures

Qualified structures support inline DCL-DS, LIKEDS and LIKEREC children, nested
arrays, shared byte storage and POS/OVERLAY strides. Initialization, CLEAR,
layout inquiries, nested key sorting/lookup, reference/CONST/VALUE parameters,
EXACT ancestry, detached returns, RTNPARM and forward types retain their existing
contracts recursively. DISK/WORKSTN result buffers can select nested record
children. %KDS accepts nested layouts and indexed native structure elements,
capturing the index once. Source removal and protocol-2 process writeback are
supported. Limits are 64 nesting levels, 1024 recursive subfield definitions and
the existing byte/storage bounds. See [nested structures](rpg-nested-structures.md).
C09 stays partial; multi-format/null external layouts, zero-field record extracts,
broader external options, blocked I/O, ILE linking and independent native golden/
scale acceptance remain open.


## 56. EXT/EXTNAME data structures

Free and fixed EXT/EXTNAME definitions obtain native fields without opening a
file. First/explicit format selection, INPUT/OUTPUT/ALL/KEY extracts, EXTFLD,
PREFIX, DIM, additional subfields and external defaults retain native layouts.
Qualified/unqualified, template, local STATIC, nested and aggregate interface
uses are supported. Compatible unqualified file variables share complete fields
while divided arrays retain their expression types. Explicit EXTNAME result
operands require a compatible explicit extract. Version-1 external description
snapshots survive source/description removal, validate every binding, enforce
256 requests/1 MiB and participate in activation fingerprints. Existing file
binding versions remain unchanged. See [external structures](rpg-external-structures.md).
Named/aggregate null maps, varying/opaque layouts, wider external/CCSID settings, multi-format file I/O,
blocked I/O, ILE linking and independent native golden/scale acceptance keep C09
partial.

## 57. Native null values

Global CTL-OPT/H ALWNULL modes now accept nullable database descriptions. *NO
rejects null input with file status 1299 before publication; *INPUTONLY supplies
live DDS/system defaults and rejects nullable output and keys. *USRCTL supplies
separate native scalar/subfield indicators, argument-free NULLIND and indexed
%NULLIND queries/assignments. LIKEDS, nested arrays, occurrences, globals and
procedure automatic/STATIC lifetimes retain their storage rules. Ordinary data
operations leave flags unchanged; normal parameters do not implicitly borrow maps.
Implicit/result-buffer record input and output and scalar/list/KDS/KFLD keys
apply indicators while validating data. Saved nullable DISK and EXTNAME metadata
use versions 7 and 2, retaining nonnullable compatibility. See
[null values](rpg-null-values.md). Named associations, null-map extracts/ABI,
aggregate indicators, varying/opaque layouts, multi-format I/O, blocked I/O,
ILE linking and independent native golden/scale acceptance remain open. C09
stays partial; checklist 36 complete / 70 open remains unchanged.

## 58. Named null indicators and external maps

NULLIND(name) associates variables and subfields with matching IND storage.
LIKEREC/EXTNAME accept NULL with INPUT/OUTPUT/ALL/KEY extraction, preserve external
field identities through renames/prefixes, and reject maps as I/O receivers.
Named maps participate in input, output and keys; nested arrays, occurrences and
STATIC lifetimes retain their selected storage. LIKEDS inherits internal
subfield relationships without inheriting an outer map name. Scope, shape,
nonnullable targets, ownership and CONST checks protect associations. Saved
physical descriptions reconstruct roles without live description lookup; binding
versions stay unchanged. See [named null maps](rpg-null-maps.md). Formal-parameter
associations, OPTIONS(*NULLIND), aggregate indicators and the remaining C09
external/ABI/native acceptance work stay open; checklist 36 complete / 70 open.

## 59. Native null-map parameters

OPTIONS(*NULLIND) shares live automatic/named flags through exact variable
references, nested/array forwarding, CONST and omission rules. Scalar formals
become null-capable. Named NULLIND parameters bind another nonnullable IND or
external *NULL map in the same signature; PR/PI association positions must agree.
Runtime maps are bounded and scoped, validate entry capabilities, detach with
native data and rebind across retained calls. Data/map writes participate in
record I/O with readonly preflight. Strided subfields preserve actual spans and
selected scalar bounds. Registered process protocols 1/2 reject unsupported map
sidebands before process execution. See [null-map parameters](rpg-null-parameters.md).
Process sidebands, aggregate indicators and the remaining C09 ABI/external/native
acceptance work stay open; checklist remains 36 complete / 70 open.


## 60. Process null-map sidebands

Registered process protocol 3 carries data-aligned nullable/value/readonly maps
alongside the existing scalar/buffer representation. All returned data and maps
validate before session publication, including CONST, nonnullable, omission,
shape, duplicate/unknown property and capability checks. Known data/map aliases
reject before startup; native synchronous aliases retain their shared behavior.
Existing executable pinning, authority, process cleanup, timeout, cancellation
and stream/request limits remain in force. Protocols 1/2 reject mapped calls.
See [process null maps](rpg-process-null-maps.md). Alias-capable process protocols,
foreign-language native ABI and broader C09 acceptance remain open. Checklist
36 complete / 70 open remains unchanged.

## 61. Corresponding-field assignment

EVAL-CORR supports native structure variables in free source and fixed extended
factor 2, with free H half adjustment. Matching local names and EVAL-compatible
types select scalar/array leaves in source declaration order; nested layouts
recurse, current occurrences remain selected and unequal array tails remain
unchanged. Related identical layouts capture the whole source image before
writing. Other file extracts/prefixes use field correspondence. Null flags copy,
clear or are ignored according to each selected leaf's capabilities; named and
borrowed maps remain live, with readonly preflight. Saved nullable record output,
source locations, numeric monitor statuses and aliases have acceptance coverage.
See [corresponding assignments](rpg-corresponding-assignment.md). Native fixed
extenders, M/R precision/procedure expressions, aggregate indicators and broader
C09/native acceptance remain open; checklist stays 36 complete / 70 open.

## 62. Right-adjusted native assignment

EVALR supports fixed-length native character fields, structures, arrays, SUBARR
and writable SUBST in free source and fixed extended factor 2. Source blanks and
raw bytes are preserved, with left padding/truncation in the native CCSID. Source
snapshots protect aliases; scalar expressions broadcast once and array tails stay
unchanged. Substring selectors are evaluated once, require integer positions and
check actual VARSIZE bounds before writeback. Shared native index type checks use
static metadata to avoid repeating indexed subscript procedures. Complete array
images validate before publication. Null flags are unchanged, including borrowed
maps, nullable record output and saved retained-program calls. See
[right-adjusted assignment](rpg-right-assignment.md). M/R precision, varying,
graphic/UCS-2, broader CHARCOUNT/CCSID and the remaining C09/native acceptance
contracts remain open; checklist stays 36 complete / 70 open.

## 63. Source-bound modules and static programs

CRTRPGMOD creates RPGLE module snapshots with saved source maps and file/external
metadata. NOMAIN supports global declarations and exported subprocedures;
ALWNULL/NOMAIN controls may be combined or separate without repetition. CRTPGM
binds an explicit module list and ENTMOD, checks imports/exports and native
interfaces, and embeds captured module images. Native CALLP/prototyped CALLB and
functions use the owning module's isolated globals with existing borrowed data,
null maps and file contracts. Return/LR/reclaim and failure cleanup preserve or
release module resources at their owning activation boundaries. Source/module/
compile-time-description deletion, live authority, signing, malformed images,
UTF-8 quotas, current library and QTEMP isolation have acceptance coverage.
DSPMOD displays saved imports/exports. See [module binding](rpg-module-binding.md)
and [interoperability](interop.md). Module-embedded directory references,
update/rebind, wider ABI and independent native acceptance remain open. C09 stays partial; checklist remains 36 complete / 70 open.


## 64. Source-bound service definitions

CRTSRVPGM captures explicit module lists without a main entry, checks internal
native imports/interfaces, and saves public export levels. EXPORT(*ALL) uses
module/procedure order; EXPORT(*SRCFILE), the default, reads QSRVSRC or explicit
member/UTF-8 stream binder source. STRPGMEXP/EXPORT/ENDPGMEXP support one CURRENT
and append-only PRV levels, case-sensitive quoted exports, EBCDIC literal or hex
signatures, and deterministic emulator-generated signatures. Unsupported options,
private/missing/duplicate symbols and invalid interfaces reject before creation.
DSPSRVPGM verifies signing, restores captured modules, rebuilds export definitions
and displays signatures/exports without running module code. Saved images retain
source maps and file/external descriptions after original sources/modules are
removed. Current-library lookup, QTEMP isolation, authority and image quotas
retain the static-module contracts. See [service definitions](rpg-service-definitions.md)
for exact limits and signature encoding. ACTGRP records *CALLER/named policy.
Dynamic execution is described below; C09 remains partial.


## 65. Dynamic service binding and activation

CRTPGM and CRTBNDRPG accept explicit BNDSRVPGM lists. Static module procedures
resolve first; ordered service references resolve remaining imports. Binding
checks native return/parameter interfaces and excludes unused services. Version 2
RPGBOUND programs capture service identities, signatures and ordered exports,
retaining version 1 static-program support. Before main side effects, services
are checked for use/signing, matching signature levels, export positions and
native interfaces. A compatible PRV level can satisfy an older program.
Service module storage is owned by its activation group, independently of caller
program storage. *CALLER shares within the caller group; named groups share across
caller groups. Global/file/STATIC state survives caller LR, failure and reclaim.
Reentrant calls use the active service; automatic frames and references unwind
without discarding completed global changes. Caller arguments are captured before
entering the service group. Active groups cannot be reclaimed. Service reclaim,
transient caller-group exit and job end close owned resources. Cached bindings
refresh after service reclaim; activated code/policy remains pinned until then.
Authority, signing/trust and runtime-file checks remain live. Services keep the
calling program's effective identity. Trusted jobless calls retain service state
only within one outer call. See [service programs](rpg-service-definitions.md).
Module-embedded directory references, update/rebind, deferred activation,
wider ABI and independent native acceptance remain open. C09 stays partial.


## 66. Binding directories and service dependencies

CRTBNDDIR/DLTBNDDIR/ADDBNDDIRE/RMVBNDDIRE/DSPBNDDIR manage ordered strict object
payloads. Add/remove batches use transactional validation with native data-authority
requirements, positions, generic removals, current-library defaults and QTEMP
isolation. CRTPGM, CRTBNDRPG and CRTSRVPGM use BNDDIR for provider discovery, after
explicit bindings. Only providers satisfying unresolved native imports are
selected; newly introduced imports can revisit earlier entries. Missing providers
are diagnostic, duplicate resolved identities are ignored and unused trailing
providers are not opened. Native imports cannot be hidden by legacy semantic
procedure names. Captured modules and dynamic service references survive directory
removal. Services accept explicit or directory-discovered dependencies, saving a
version 2 RPGSERVICE envelope while retaining version 1 support. Before main
side effects, graph preflight checks signatures/exports/interfaces and live
use/signing in each parent service's group. Cycles terminate without initializing
globals. Child reclaim refreshes weak bindings; parent/child data and cursor
ownership, borrowed structures and null maps remain independent of caller storage.
See [binding directories](rpg-binding-directories.md) for limits and remaining
module-embedded reference, deferred activation, update/rebind, data export and
native acceptance scope. C09 remains partial; checklist counts are unchanged.


## 67. Source reference directories and program updates

BNDDIR control literals are captured with module source, including free multiline
controls, preprocessed includes and supported traditional H origins. They are
resolved at binding after explicit directories as imports require, including
references introduced by selected modules. CRTBNDRPG automatically binds when
source controls supply directories. Saved images do not reopen directories at
runtime. UPDPGM and UPDSRVPGM replace captured modules under RPLLIB selection,
preserve recorded module origin and rebind live service signatures. MODULE(*NONE)
performs rebinding without module replacement. EXPORT(*CURRENT) preserves saved
service export levels/signatures; *ALL and binder source can publish a new public
interface. Complete artifact restoration precedes an owner-authorized compare-and-
update transaction. Concurrent changes, cancellation and validation failures leave
the original object intact; success preserves metadata and clears stale signing.
ALWUPD(*NO) forbids updates. Existing activations retain old code, entry interfaces,
module data and cursors until reclaim; new activations use the new version.
See [program updates](rpg-program-updates.md) for the exact partial contract and
remaining activation/library/deferred/listing/ABI scope. C09 stays partial.

## 68. Deferred service procedure activation

BNDSRVPGM tuples and binding-directory service entries select *IMMED or *DEFER.
Immediate closures preflight before main; deferred imports locate and validate
providers when called, including their immediate dependencies before procedure
effects. Parent activation-group ownership, cyclic graphs, shared globals/files,
live authority/signing and retained versions continue to apply. Update tuples
support *SAME and explicit mode changes. Version 3 RPGBOUND/RPGSERVICE images
capture deferred references; immediate-only images retain the version 1/2 shape.
See [deferred activation](rpg-deferred-activation.md) for timing, limits, tests and
the remaining data/pointer/library/ABI/native acceptance scope. C09 stays partial.

## 69. Activation-group and service-library update policies

UPDPGM/UPDSRVPGM can change named activation groups and relocate existing service
references to a named library or *LIBL when creation allowed ALWLIBUPD(*YES).
Missing preferred providers preserve existing references; denied or invalid ones
reject the complete update. Dynamic references resolve per caller image and pin
the actual connection, sharing service storage by qualified object and group.
Deferred lookup waits for first use. Version 4 captures runtime lookup metadata;
explicit references retain prior formats. Source and group policy publish in one
compare-and-update transaction. Old groups/connections retain captured versions
until reclaim. See [update policies](rpg-update-policies.md) for the exact partial
contract, verification and remaining native scope.

## 70. Module and service data imports and exports

Global IMPORT/EXPORT declarations link supported standalone and structured native
storage across static modules and service programs, including fixed arrays,
nested layouts, occurrences and null maps. Exporter initialization runs at
activation; exported data survives LR and abnormal returns. Data references force
service activation checks even with *DEFER. Public data kinds are recorded in
version 5 images and validated with signatures, positions and layouts. Provider
storage, authority, reclaim, retained updates and typed argument aliases remain
live. See [data binding](rpg-data-binding.md) for syntax, ownership, validation,
verification and remaining pointer/binder/ABI/native acceptance scope. C09 stays
partial.

## 71. Native procedure pointers

`POINTER(*PROC)`, traditional `*`/`PROCPTR`, `%PADDR` and pointer-based `EXTPROC`
use opaque 16-byte addresses. Native scalar/array/structure parameters and returns
carry callbacks through the existing ABI. Address imports participate in module,
service and directory binding and force provider activation even with `*DEFER`.
Callbacks retain origin code/storage and re-enter their original group; reclaim
invalidates addresses instead of rebinding them. See the
[procedure pointer contract](rpg-procedure-pointers.md) for alignment, validation,
lifetime, limits and remaining data-pointer/foreign-ABI scope. C09 stays partial.

## 72. Basing pointers and native heap storage

`POINTER`, traditional `*`, `BASED`, `%ADDR`, pointer byte arithmetic, `%ALLOC`,
`%REALLOC` and fixed `ALLOC`/`REALLOC` plus fixed/free `DEALLOC` share guarded
native storage. Fields, array elements, structures, records and native parameters
retain alias identity. Global/STATIC/heap/automatic lifetimes follow their owners;
reclaim/free invalidates addresses. Size/element metadata does not dereference
null based variables. See the [data pointer contract](rpg-data-pointers.md) for
initialization, alignment, bounds, status codes, quotas, security and remaining
cross-region/teraspace/null-map/foreign-ABI acceptance scope. C09 stays partial.
