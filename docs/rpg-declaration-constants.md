# RPG named constants and declaration values

[Forward native types](rpg-forward-declarations.md) resolve later LIKE targets
and explicit/implicit return types while preserving source-order value binding.

[Native LIKE declarations](rpg-like-declarations.md) add inherited scalar types,
signed length adjustments and related type identities for exact parameters.

Free-form `DCL-C name value;` and `DCL-C name CONST(value);` define immutable
named constants. Numeric constants retain their precision and scale; character,
hexadecimal, indicator and finite floating-point literals retain their value
types. Earlier named constants may supply later declarations. This follows IBM's
[named constant syntax](https://www.ibm.com/docs/en/i/7.5.0?topic=statement-free-form-named-constant-definition)
and [CONST rules](https://www.ibm.com/docs/ssw_ibm_i_74/rzasd/dconst.htm).

```rpgle
**free
dcl-c NUM_FORMATS 2;
dcl-c DEFAULT_AMOUNT 7.50;
dcl-ds BANK qualified occurs(NUM_FORMATS) inz;
  AMOUNT packed(9:2) inz(DEFAULT_AMOUNT);
end-ds;
dcl-c TOTAL_BYTES %size(BANK:*all);
dcl-s RECEIVER char(TOTAL_BYTES) ccsid(*hex);
eval %occur(BANK)=NUM_FORMATS;
```

## Evaluation and layout

Constant evaluation accepts literals, earlier named constants, signed numeric
values and the supported pure built-ins `%ABS`, `%LEN`, `%SIZE` and `%ELEM`.
Arguments can nest supported built-ins. It reads declaration metadata and folded
constant values without creating a runtime context or invoking host callbacks.
Ordinary variable values, current file/program status and clock values cannot
supply a declaration constant, even when a variable has a literal initializer.
Arithmetic expressions remain unsupported in declaration keywords, matching
the distinction in IBM's
[definition-time built-in rules](https://www.ibm.com/docs/en/i/7.4.0?topic=operations-built-in-functions).

Named constants and supported built-ins supply scalar type dimensions, native
DS LEN/DIM/OCCURS, subfield dimensions/POS/OVERLAY offsets and typed INZ values.
Dimension values require non-float numbers with zero decimal positions and stay
within the existing declaration/storage bounds. Referenced constants and layouts
must already be declared. See IBM's
[DIM rules](https://www.ibm.com/docs/en/i/7.5.0?topic=keywords-dimautoctdatavarnumeric-constant).

`%LEN` uses declared lengths or numeric precision, rather than initialized text.
`%SIZE` uses native byte widths; `*ALL` includes all array elements or structure
occurrences. Qualified fields, prior subfields in the current structure and
constant-indexed DS/subfield arrays support layout queries. `%ELEM` returns an
array's element count **or a multiple-occurrence structure's occurrence count**,
independently of active selection. The earlier occurrence-only increment
incorrectly rejected the latter case; runtime behavior and its regression test
are corrected. See IBM's
[%ELEM contract](https://www.ibm.com/docs/en/i/7.4.0?topic=functions-elem-get-number-elements).

File declarations are discovered before folding layout-dependent values.
Queries therefore see final 99-byte INDDS and 80-byte INFDS layouts, including
LIKEDS chains, even when file declarations appear later. Discovery consumes
complete declarations so file-like text inside multiline literals cannot create
phantom bindings or change inferred lengths. Final binders still validate
explicit lengths and all storage budgets.

INZ values are folded to literals. Typed scalar/native-field checks retain
character/hex widths, numeric precision, integer byte limits and finite FLOAT(4)
range. Literal text such as `'*BLANKS'` remains literal text. Quoted apostrophes,
colons and hexadecimal bytes survive initialization. Borrowed API receivers
cannot modify constants; existing scalar prototype conversion rules still apply.
EVAL, CLEAR, writable SUBST and host rebinding cannot change a compiled constant.

## Bounds and remaining work

Declarations precede calculations and use the existing `**FREE` global declaration
grammar, statement/source limits and original include maps. There are at most
1024 free named constants and 1 MiB of aggregate constant literal units; each
value is limited to 32767 units and constant/layout nesting to 64 levels. Alias
expansion counts toward the aggregate budget. Failed definitions prevent program
creation and do not execute calculations.

Additional constant-valued built-ins, forward/layout dependency resolution,
native fixed D-spec constants, date/time literal prefixes, declarations in local
procedures, compile-time/pre-runtime array declarations, file templates,
EXTNAME/WORKSTN record/nested layouts, complete CHARCOUNT/CCSID rules, broader expression
typing, full entry/prototype ABI and independent native-system reference
acceptance remain open. Historical fixed D-spec constant handling retains its
documented extension behavior. C09 remains partial.

[Standalone runtime arrays](rpg-standalone-arrays.md) now support DIM with these
constant values and expose native byte widths to layout queries.

`RpgDeclarationConstantTests` covers inferred types, literal preservation,
dimensions/initializers, layout queries, immutable targets, include locations,
invalid/runtime-dependent definitions and count/storage/nesting bounds.
`RpgDeclarationConstantIntegrationTests` compiles saved QWCRSVAL callers and
removes their source in actual CCSID 37/1208 jobs. Indicator tests cover final
INFDS/INDDS/LIKEDS sizes and invalid explicit lengths. The display PTY compiles
RPGINDDS using named occurrence and character constants.


Final verification (2026-10-17): all 3405 tests pass, zero failures/skips,
including 60 new cases and all 292 focused checks. Locked restore, zero-warning/
error Release build, expanded display PTY, generated inventory and whitespace
checks pass. Full-suite runtime is 7m02s, not a performance target. Schema is
31, command catalog 306 and checklist 36 complete / 70 open. C09 remains partial.

[Data templates](rpg-templates.md) support declaration `%LEN`/`%DECPOS` and
layout-only `%SIZE`/`%ELEM` without allocating runtime storage.

[DISK LIKEREC record structures](rpg-record-structures.md) now supply native
layouts, result operands and parameter/return types.
