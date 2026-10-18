# Native RPG array searches

Traditional LOOKUP and %LOOKUPNE now operate on supported native arrays.
LOOKUP has its own comparison, index and status rules; it is not lowered to a
%LOOKUP expression. C09 remains partial.

[Native tables](rpg-tables.md) now add TAB definitions, paired current-element
LOOKUP, the six %TLOOKUP variants and reference calls to stored CL/RPG programs.

## Traditional LOOKUP

LOOKUP uses the [native C-spec columns](rpg-fixed-calculations.md). Factor 1 is
the search argument. Factor 2 names a standalone or DS subfield array, optionally
with a starting index. The result field is blank. At least one HI, LO or EQ
indicator is required; HI and LO cannot combine. HI/LO searches require ASCEND or
DESCEND. Shared HI/EQ or LO/EQ indicators combine the search outcomes.

EQ selects the first match at or after the starting index. HI/LO select the
nearest greater/lower value in that suffix; adding EQ gives an exact match
priority. HI chooses the first higher duplicate in ascending arrays and the
last higher duplicate in descending arrays; LO chooses the opposite boundary.
EQ-only LOOKUP searches sequentially, including sequenced arrays. See the
[IBM ILE RPG reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf),
array searches and LOOKUP, pp. 280–281 and 877–878.

An omitted index starts at 1. A numeric field, indexed numeric cell or named
constant can supply an index; it must be non-float with zero decimal positions.
Numeric literals are also accepted. A writable index receives the selected
position, or 1 after a miss. Literal and named-constant indexes do not change.
An initial index outside the dimension reports status 121. Index addresses are
captured once. Conversion failure, outer caller rejection and cancellation
preserve the prior index and flags. Index writeback can affect storage aliases.

IBM's references conflict on a missed indexed search: the array overview on
p. 281 says zero, while the opcode-specific rules on p. 878 say one. This
implementation follows the opcode-specific rule, also present in the
[published LOOKUP reference](https://www.ibm.com/docs/en/i/7.1.0?topic=codes-lookup-look-up-table-array-element).
Independent native-system validation of that discrepancy remains required.

Declared search fields must match the array's type and length. Supported
non-float numeric formats may differ when digit lengths match. Their decimal
positions are ignored: PACKED(3:1) value 12.3 matches PACKED(3:2) value 1.23.
Exact signed coefficients preserve 29 digits without decimal multiplication
overflow. Numeric literals/constants retain their coefficients. Float arrays
align decimal points. Date/time/timestamp/indicator arrays accept compatible
fields; general indicators can supply indicator search arguments.

Character searches compare unsigned job-CCSID bytes. Native/hexadecimal operands
can contain undecodable bytes because comparison does not decode them. Short
literals receive blank padding; oversized arguments reject. Literal byte length
is checked with the activation CCSID. Supported figurative constants include
*HIVAL/*LOVAL, context-appropriate *BLANKS/*ZEROS and indicator values. Integer
bounds use storage width; packed/zoned bounds use digits. ALTSEQ remains open.

## Status and built-ins

LOOKUP updates global %FOUND for any successful requested search. Global %EQUAL
is true only when EQ was requested and an exact match was selected. A relative
search without EQ clears global %EQUAL. Unspecified indicators retain their
values; specified indicators reset after a miss. A false conditioning indicator
leaves the index and search state unchanged.

SETLL now supplies global and per-file %EQUAL. %EQUAL(file) is independent of
array searches and file operations that do not perform SETLL. It applies to
files supporting SETLL, including relative-record access. Array searches preserve
per-file %FOUND/%EQUAL and existing EOF/error/numeric status. Failed index
publication leaves prior search state intact. See %FOUND and %EQUAL in the
[IBM reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf),
pp. 698–699 and 701–702.

%LOOKUPNE(argument:array{:start{:count}}) returns the first unequal element in
the requested range, or zero when all compare equal. It searches sequentially
in index order, including DESCEND arrays. It accepts scalar arrays and existing
scalar/indexed keys of qualified DS arrays, such as DS(*).KEY or DS(*).VALUES(2).
Numeric decimal positions are aligned, as with other %LOOKUPxx functions. These
built-ins do not change %FOUND, %EQUAL or indicators. See the
[IBM %LOOKUPxx reference](https://www.ibm.com/docs/en/i/7.5.0?topic=functions-lookupxx-look-up-array-element)
and [keyed arrays](rpg-keyed-arrays.md).

## Column-limited free declarations

Global DCL-S, DCL-C, DCL-F and DCL-DS/END-DS can precede native calculations
without **FREE. Columns 1–5 contain numeric/blank sequence text, columns 6–7 are
blank, and declaration code occupies columns 8–80. Text after column 80 requires
// commenting. Balanced declaration fragments can continue across records;
split literals and native +/- literal continuation remain unsupported. Only one
declaration statement is accepted per physical ending record.

The declarations share the fully free type/layout/constant/CTDATA/file pipeline.
They must precede calculations and remain outside /FREE blocks and procedures
in the current global subset. **FREE retains unrestricted declaration width;
native C-specs remain rejected there. This does not implement every column-limited free calculation or
native procedure specification. See Free-Form Statements in the
[IBM reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf), pp. 353–354.

## Evidence and remaining work

`RpgFixedLookupTests` covers exact/nearest searches, duplicates, suffixes,
constants, shared indicators, precision/bytes/figurative values, DS views,
captured indexes, rejection/cancellation, %LOOKUPNE ranges/keys and source
diagnostics. `RpgFixedLookupIntegrationTests` covers saved-source removal,
CTDATA/ALT descriptions, CL aliases on return/error, RETURN/LR/reclaim,
file/global status and column-limited DS/file declarations in actual CCSID
37/1208 jobs. The terminal fixture compiles/calls RPGLOOKUP for a DS array,
numeric coefficients, index reset, %FOUND/%EQUAL and %LOOKUPNE.

Pre-runtime table loading, full table-entry ABI, implicit *IN array operands, legacy
semantic arrays, ALTSEQ/graphic/UCS-2/varying/extended CCSID modes, complete
expression/array/procedure ABI, full native specifications and independent
native-system golden/scale acceptance remain open. C09 stays partial. No
performance target is claimed.


Verification (2026-10-18): all 4073 tests pass, zero failures/skips, including
104 new cases. All 1870 expanded RPG/CL/queue checks and 145 final RPG/queue
checks pass. Locked restore, zero-warning/error Release build, expanded display
PTY, generated inventory and whitespace checks pass. RPGLOOKUP runs at 24x80;
display fixtures also cover 27x132. The delay-based queue delete/recreate fixture
now observes a dedicated blocked receiver and verifies replacement identity,
CPF9801 and retained data. Queue runtime is unchanged. Full-suite runtime is
7m16s, not a performance target. Schema is 31, catalog 306 and checklist
36 complete / 70 open. C09 remains partial.
