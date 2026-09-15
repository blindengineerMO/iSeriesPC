# rpg-spec

RPG III / RPG IV-style emulation for iSeriesPC — fixed-form and fully free-form programs compiled
to an internal statement model and executed by `Ipc.Rpg.Runtime.RpgInterpreter` (`.NET 8`, x64 Linux).

## 1. Overview

A program is a named object (library/name, e.g. `CALL MYLIB/TESTPGM`) whose source is read,
declared, parsed into statements, restructured into blocks, and cached as a `RpgProgram`. The
interpreter walks `MainStatements` (plus `Subroutines`) against a `RpgRuntimeContext` holding
fields, data structures, indicators, and PLC/PRC status; file operations delegate to the SQLite
`IPCFILE` store via `RpgFileCursor`.

## 2. Compiler pipeline

`RpgCompiler.Compile(library, name, source)`:

1. **ReadSpecs** — split lines, drop blanks and `*` comment lines (except `**free`).
2. **Free-form detection** — true if any line starts with `**free`.
3. **ParseDeclarations** — collect D-spec fields and data structures.
4. **ParseStatements** — collect statements. Fixed form: lines with `C` in column 7.
   Free form: every non-`D`, non-`**free` line is a free statement.
5. **ExtractSubroutines** — pull `BEGSR…ENDSR` blocks from the main list into `Program.Subroutines`.
6. **ResolveBlocks** — pair `IF/ELSE/ENDIF`, `SELECT/WHEN/OTHER/ENDSL`, `DO/DOU/DOW/FOR/ENDDO`,
   and label/tag jumps; throws `RpgCompileException` for unbalanced blocks.
7. **AssignDsOffsets** — compute DS element offsets (sequential or `OVERLAY` overrides).

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

Statements start after column 7 (typically indented). `**free` begins global free-form source and
free statements continue until the end of the source (no `**endfree` terminator). The block
directives `/free` and `/end-free` switch free-form parsing on/off between them, letting free
statements be interleaved with fixed C-specs in the same program (`/free` presence alone does not
make the whole program free). Each statement ends with `;`. Operands are bound per opcode:
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
`UPDATE`, `DELETE`, `EXFMT`.

- File name resolves from Factor 2 (keyed ops) or Factor 1 (block ops), then the result; the
  runtime matches against opened file members by name.
- `CHAIN`/`READ*` copy the current record's fields into the context; `UpdateOpcode` requires a
  record to have been read (`cursor.Current`) and re-inserts preserving key values.
- `WRITE` builds values from the named format (default `*FILE`'s primary format).

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
  `EXTPROC('name')`/`EXTPGM('name')` external name). Blank-named D-specs that follow it become the
  prototype's parameters (synthetic program fields `P1`, `P2`, … bound by reference). `CALLP`
  resolves the callee name against prototypes before falling back to a bare external call, so
  `callp calc(1:2);` with an `EXTPROC('INNER')` prototype calls the host program `INNER`.
- **Procedure pointers**: `PROCPTR` on a standalone field stores a program name; `CALLP name(args)`
  against such a field reads it and calls the named program (so `eval FN = 'REALPGM'; callp FN(5);`
  invokes `REALPGM`).
- **PLIST writeback**: fixed `CALL 'pgm' PL` (and free `CALLP 'pgm'(PL)`) forwards the `PLIST`'s
  `PARM` fields to the host; when the host returns `RpgExternalCallResult.UpdatedParameters`, each
  entry is written back into the corresponding `PARM` field (by-reference semantics).

## 8. Files

`RpgFileCursor` wraps the store's keyed reader: `ReadNext`/`ReadPrior` (plain and keyed variants),
`Chain(search)`, `SetLowerBound`/`SetUpperBound` (`*INxx`-free; used by `SETLL`/`SETGT`), and
`Reload()` after writes. Key fields are `Sequence > 0` fields of the primary format (from the DDS
`K`-row markers). `_host.Files` (`ISqlFileStore`) provides `Insert`, `RowCount`, and
`ReadKeyPrefix` used by the interpreter.

## 9. Program object model

`RpgProgram` — name/library, `IsFreeForm`, `Fields`, `DataStructures`, `EntryPlist`,
`MainStatements`, `Subroutines`, `Subprocedures`, `Prototypes`. `RpgStatement` — `Opcode`,
`Factor1`/`Factor2`/`Result`, `Length`, `Value` (free operand text), `Conditions`, `Label`,
`Indicator1`/`Indicator2`, `Jump` (else/branch target index), `End` (block end index). `RpgField` —
name, kind (Zoned/Packed/Character/ProcPtr), length, decimals, `InitialValue`, `IsArray`/`Dimension`,
`IsDataStructure`, `Varying`, `Source`. Subroutines are `BEGSR…ENDSR` blocks re-parented out of the
main stream, and subprocedures are P-spec blocks re-parented into `Subprocedures`
(`RpgSubprocedure` — name, `Statements`, `Parameters`). Prototypes are D-spec `PR` rows in
`Prototypes` (`RpgPrototype` — name, `ExternalName`, `Parameters`).

## 10. Limits and divergence notes

- Column arithmetic/reference limitations mirror classic RPG (result field width 11 in the 48–58
  band; the length column 53–58 overlaps the result band and is only meaningful when the result is
  blank).
- Free-form statements must be indented so that column 7 is blank and the first token is not `D`;
  `*` comment lines are always stripped unless they start `**free`.
- Bit fields initialize to blanks (`0x20`) in the emulation's ASCII representation; `BITON` ORs
  into the existing value.
- `IF` without a matching `ENDIF`, and missing `ENDDO`/`ENDSL`, are compile-time errors.
- **`ON-ERROR` uses tail-trap semantics, not IBM's error-handler subroutines.** A runtime error
  (`RpgRuntimeException`, e.g. division by zero) jumps to the first `ON-ERROR` that follows it in
  the current statement list; the statements until the next `ON-ERROR` (or the list end) run as the
  handler; in normal flow an `ON-ERROR` statement bypasses its own handler, so multiple
  `ON-ERROR` partitions partition the block. No `ON-ERROR` present (or an error raised inside a
  handler) propagates the exception.
- **Subprocedures share the program's field namespace**: procedure D-specs are registered in
  `program.Fields` and mirrored as the procedure's `Parameters`, so a global field and a
  procedure-local of the same name collide.
- **`**free` is global**: a program containing `**free` is free-form throughout, so fixed-form
  C-specs (e.g. a fixed `PLIST`) cannot coexist with `**free` in the same source — use fixed-form
  `CALL` with `PLIST` for that. `PLIST` parameter forwarding therefore lives in fixed form;
  prototypes and procedure pointers cover the free-form calling cases.
- **`*INLR` honored**: setting `*INLR` (via `SETON *INLR`, `seton *inlr`, or assigning it with
  `EVAL`) stops the main `Execute` loop, matching classic RPG's last-record indicator.