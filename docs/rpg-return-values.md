# Native array and structure returns

[Forward LIKEDS templates](rpg-forward-structures.md) support later layouts,
inherited initialization, structure parameters and returns.

[Forward native types](rpg-forward-declarations.md) resolve later LIKE targets
and explicit/implicit return types while preserving source-order value binding.

[Native LIKE declarations](rpg-like-declarations.md) add inherited scalar types,
signed length adjustments and related type identities for exact parameters.

Same-unit native procedures can return fixed DIM arrays, LIKEDS structures and
arrays of structures. Both traditional PR/PI headers and free DCL-PR/DCL-PI
headers support these layouts. RTNPARM is supported for scalar and aggregate
returns. C09 remains partial.

The source contract follows the [IBM ILE RPG reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf):
return declarations, pp. 121–122; RTNPARM, pp. 530–531; and RETURN, pp. 949–951.
IBM's [RETURN examples](https://www.ibm.com/docs/en/i/7.4.0?topic=codes-return-return-caller)
specify scalar broadcasting, default values for missing array elements and
truncation of surplus elements. Independent IBM-system acceptance remains open.

```rpgle
**free
dcl-s RESULT int(3) dim(4) inz(9);
eval RESULT = BUILD(7);
return;

dcl-proc BUILD;
  dcl-pi *n int(3) dim(3) rtnparm;
    N int(3) value;
  end-pi;
  dcl-s LOCAL int(3) dim(2);
  eval LOCAL = N;
  return LOCAL;
end-proc;
```

RESULT becomes 7, 7, 0, 9. BUILD returns exactly three elements; its missing
third element receives the integer default. Assignment updates those three
elements and preserves the receiver's fourth element.

## Layouts and assignment

Array return element types use the native CHAR, PACKED, ZONED, signed INT,
FLOAT, IND and default ISO DATE/TIME/TIMESTAMP codecs. CHAR can specify
CCSID(*HEX)/65535. A scalar RETURN operand fills every declared array element.
An array expression supplies its available prefix, fills missing return elements
with native defaults and ignores elements beyond the declared return dimension.
Each selected value converts through the return element's codec, including
numeric precision/scale conversion. Missing character elements contain encoded
blanks; numeric elements are zero; indicators are off; dates/timestamps use
their native minimum and times use midnight.

`likeds(TEMPLATE)` describes a structure return; fixed `dim(N)` makes it an
array. Templates use the same plain native layout and bounds as
[aggregate parameters](rpg-aggregate-parameters.md). Structure return assignment
copies character/structure bytes, padding with encoded blanks or truncating to
the declared width. Missing structure-array images contain blanks. Scalar
structure data broadcasts to a declared structure array. Returned buffers must
use the current native CCSID.

Returned values are independent copies. They retain no borrowed reference to
callee locals, STATIC storage, parameters or module globals. Repeated calls and
later caller/callee mutations do not change earlier results. Array results feed
whole-array assignments, supported array arithmetic and %XFOOT expressions.
Structure results can be assigned to named structures, and scalar/aggregate
results can supply compatible CONST/VALUE parameters. Mutable reference actuals
still require variables.

Explicit prototypes must match the PI's return layout, fixed dimension and
RTNPARM mode. Automatic prototypes retain the same information. Fixed-form
LIKEDS return headers require blank type/length/decimal columns and permit
other header keywords before LIKEDS. Template resolution occurs during
declaration binding, after preceding templates have been read. File discovery
leaves this lookup to binding.

## RTNPARM, failures and storage

RTNPARM adds a hidden first reference parameter in the interpreted procedure
contract. `%PARMS()` includes that slot, even when no explicit parameters are
supplied. `%PARMNUM(P)` returns one more than P's apparent position, including
when used in a declaration constant. Optional and omitted explicit arguments
keep their own presence state and binding; %PASSED/%OMITTED account for the
offset. The main PSDS parameter count remains independent of local calls.
Within the existing 255-position native limit, RTNPARM permits 254 explicit
parameters. It requires a return type and matching PR/PI declarations.

Return normalization finishes before caller receiver assignment. An invalid
element, return conversion failure or missing RETURN signals procedure status
202 and leaves that receiver unchanged. A returned array cannot be assigned to
scalar storage; that assignment reports 105. Cancellation propagates through
the existing call cleanup. Other writes performed before a procedure fails
retain the existing live-reference semantics.

Return images count toward the 64 MiB declared invocation-storage bound before
entering the callee; the 128-frame and other existing native bounds remain.
Each aggregate return is at most 1 MiB with at most 32767 elements. No native
binary ABI or performance equivalence is claimed by these interpreter modes.

This work also fixes whole-structure CLEAR on borrowed parameters: initialization
occurs in a private image, the outer receiver validates it, and publication goes
through the binding. Structure-array prefixes preserve the caller's tail.

## Evidence and remaining work

Unit checks cover scalar broadcasts, type defaults, array conversion/expressions,
structure copies, array tails, named and automatic prototypes, both native column
origins, RTNPARM counts/constants/presence/bounds, storage admission, errors,
cancellation and bound CLEAR. Saved-program cases remove source before exercising
arrays, structures, structure arrays and real QSNDDTAQ inputs at CCSID 37/1208.
External processes exercise valid and malformed updates before a local array
result is returned. RPGRETURN supplies terminal compile/call acceptance.

Verification (2026-10-18): all 4789 solution tests pass with zero failures/skips,
including 73 new cases (63 unit, eight saved-program/integration and two
external-process cases). All 555 focused checks, locked restore, a Release
build with zero warnings/errors, RPGRETURN terminal acceptance, generated
inventory, documentation links and whitespace checks pass. RPGRETURN runs at
24x80; general display acceptance also covers 27x132. Full-suite wall time is
7m38s; no performance target is claimed.

Further return formats and types, external LIKE, WORKSTN/EXTNAME record layouts, RETURN precision extenders,
variable-size/dynamic layouts, procedure pointers, foreign ABI, cross-unit
module/service-program binding and independent native golden/scale acceptance
remain open. See [local procedures](rpg-local-procedures.md) for other limits.

[DISK LIKEREC record structures](rpg-record-structures.md) now supply native
layouts, result operands and parameter/return types.
