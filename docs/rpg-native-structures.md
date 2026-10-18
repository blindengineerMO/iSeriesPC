# Native RPG data structures

Free-form DCL-DS/END-DS declarations now allocate byte-backed storage. Subfields
share those bytes through QUALIFIED references, POS and OVERLAY. This supports
native API receiver layouts without constructing an entire header as hexadecimal
text. The historical fixed D-spec layout retains its documented text-overlay
extension; it is not a native fixed-format memory ABI.

```rpgle
**free
dcl-ds INFO qualified len(128);
  COUNT int(10) pos(1);
  OFFSET int(10) pos(5);
  CCSID int(10) pos(33);
end-ds;
dcl-ds ERROR qualified inz len(16);
  PROVIDED int(10) inz(16);
  AVAILABLE int(10);
  IDENTIFIER char(7);
end-ds;
callp QWCRSVAL(INFO:%size(INFO):1:'QCCSID    ':ERROR);
```

The declaration grammar accepts named and anonymous structures, anonymous filler
subfields, optional matching END-DS names, DCL-SUBF, QUALIFIED, LEN, INZ, DIM and
LIKEDS. DCL-SUBF is required when a subfield name is a supported operation code.
Declarations precede calculations, may span lines and use one semicolon-terminated
declaration per source statement. Errors preserve original include locations.
See IBM's [data structures](https://www.ibm.com/docs/en/i/7.6.0?topic=prototypes-data-structures)
and [free subfield definitions](https://www.ibm.com/docs/ssw_ibm_i_74/rzasd/freesubfield.htm).

## Layout and initialization

CHAR, CHAR CCSID(*HEX)/65535, PACKED, ZONED, INT, FLOAT, IND and the existing
default ISO DATE/TIME/TIMESTAMP declarations are supported. INT(3/5/10/20) occupies
1/2/4/8 signed big-endian bytes. Packed decimal uses digit/sign nibbles, zoned
decimal uses decimal zones and float uses big-endian IEEE binary32/binary64.
Character fields use the activation's captured CCSID; numeric representations
retain their native byte format. See IBM's
[data types](https://www.ibm.com/docs/en/i/7.5.0?topic=definitions-data-types-data-formats).

Without DS INZ, storage starts as blanks, including numeric subfields. An explicit
subfield INZ still initializes that subfield. DS INZ initializes all subfields in
declaration order, so later overlapping fields can replace earlier bytes.
Reading blank or malformed packed/zoned storage reports status 907. Scalar numeric
assignment discards extra fractional digits and reports 103 on overflow, preserving
the old bytes. These are separate from assignment to the whole character image.
See IBM's [INZ rules](https://www.ibm.com/docs/en/i/7.4.0?topic=keywords-inzinitial-value)
and [program statuses](https://www.ibm.com/docs/en/i/7.5.0?topic=structure-program-status-codes).

LIKEDS copies a preceding native layout and is implicitly qualified. INZ(*LIKEDS)
inherits its initialization; plain INZ uses type defaults and no INZ leaves blanks.
The new structure owns separate storage. CLEAR uses type defaults rather than
restoring explicit initial values. Existing fields and gaps remain byte-backed.

POS uses one-based byte positions. OVERLAY references a previously defined
subfield and must fit within it. An optional relative position or *NEXT selects
the overlay's position. Overlaying an array inherits its DIM and stride and may
produce a noncontiguous array; an additional DIM is rejected. See IBM's
[OVERLAY rules](https://www.ibm.com/docs/en/i/7.4.0?topic=keywords-overlaynamestart-pos-next).

Structure arrays require QUALIFIED. Both `DS(i).FIELD` and `DS(i).ARRAY(j)` use
independent checked indexes. Scalar assignment broadcasts to an array; array copy
processes the shared element count and preserves remaining destination elements.
Numeric +, -, *, / and unary operations work element by element. Whole-array
comparisons, logical expressions and scalar conversions currently fail explicitly.
See IBM's [array structures](https://www.ibm.com/docs/en/i/7.5.0?topic=structures-array-data).

%SIZE returns an element's byte width; %SIZE(array:*ALL) multiplies by its count,
excluding gaps in noncontiguous arrays. %ELEM returns the dimension. Numeric %LEN
returns declared digits. The same numeric %SIZE/%LEN correction applies to existing
standalone fields. Native character/DS %SUBST uses byte positions and permits
writable slices with preserved tails; array operands require an index. See IBM's
[%SIZE contract](https://www.ibm.com/docs/en/i/7.5.0?topic=functions-size-get-size-in-bytes).

## Borrowed API views

An unprototyped built-in API call can borrow a whole structure, contiguous array
or typed subfield. Compatible existing scalar prototypes can borrow a subfield.
Whole structures and arrays expose raw CHAR storage; subfields retain their
native type/precision. Repeated references share a cell, and differently typed
overlays remain live views over the same bytes. Uninitialized numeric output
fields need not decode before the API writes them. Ordinary copied callers still
receive semantic values and can return qualified-field updates. Indexed addresses
are captured at call entry for both lazy borrowing and copied writeback, so a
changed index cannot redirect the receiver.

Borrowed cells expire when the call ends. Output normalization validates a private
image before writing. Normal CHAR receivers validate encoded text, including each
array element, before a receive can dequeue; opaque receivers preserve arbitrary
bytes. Complete native CHARCOUNT and string-conversion modes remain open.
Storage metadata contains an opaque identity and byte range,
never the backing array or a host address. Shared API guards use ranges to reject
partial output aliases before dequeue or object mutation. A partially overlapping
error receiver is rejected before error reporting can overwrite another argument.
Existing adapters retain their exact-cell error reporting; adjacent fields remain
valid receivers. The original ProgramArgument constructor
is retained for binary compatibility.

Contiguous arrays can be borrowed as one buffer; noncontiguous arrays require an
indexed element. Existing scalar prototype options remain unchanged. A retained
activation keeps its storage CCSID; borrowing after the call CCSID changes fails
explicitly pending native conversion negotiation.

## Bounds and unfinished contracts

There are at most 256 structures and 1024 subfields per structure, 1 MiB per
declaration, 16 MiB of native DS storage per program and 128 qualification levels.
Individual fields retain the existing 32767-byte and .NET decimal limits. Calls
with native storage are bounded to 256 parameters and 1 MiB of aggregate native
snapshots, before allocating copies or entering the host. API borrowing also
checks its existing aggregate argument budget.

Nested DS definitions, TEMPLATE, external/LIKEREC layouts, ALIGN/pointers,
varying/null/unsigned/graphic types, named-constant declaration expressions,
additional date/time formats, general array functions/relations, native fixed
D/C layouts and full ordinary CL/RPG entry-reference/prototype ABI remain open.
The [INFDS/PSDS subset](rpg-feedback-structures.md) now uses this storage for
common file feedback, program errors and last-file snapshots. Extended feedback
areas and INDDS remain open. The broader C09 requirements remain unchecked.

`RpgNativeStructureTests` covers byte vectors, initialization, overlays/stride,
arrays, bounds, metadata, call lifetime, alias ranges and conversion failures.
The integration cases compile saved programs, remove source, receive QWCRSVAL
headers/error structures at CCSIDs 37/1208 and retain native fields across RETURN.
The real terminal compiles and calls RPGNATDS using typed binary and character
receiver fields. Independent IBM i golden and scale acceptance remains required.
