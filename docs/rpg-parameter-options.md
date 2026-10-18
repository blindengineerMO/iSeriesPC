# Exact and adjusted native parameters

[Native LIKE declarations](rpg-like-declarations.md) add inherited scalar types,
signed length adjustments and related type identities for exact parameters.

Native/free PR/PI support OPTIONS(*EXACT), OPTIONS(*TRIM) and
OPTIONS(*RIGHTADJ) for the native types described here. Both traditional column
origins support these keywords. C09 remains partial.

These rules follow IBM's [parameter OPTIONS reference](https://www.ibm.com/docs/en/i/7.4.0?topic=dsk-optionsnopass-omit-varsize-exact-string-trim-rightadj-nullind-convert)
and [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
pp. 509–515. Independent IBM-system acceptance remains open.

```rpgle
**free
dcl-s TEXT char(8) inz('AB');
dcl-s RESULT char(4);
callp GET(TEXT);
return;
dcl-proc GET;
  dcl-pi *n;
    P char(4) const options(*trim:*rightadj:*exact);
  end-pi;
  eval RESULT=P;
  return;
end-proc;
```

RESULT contains two blanks followed by AB. TEXT retains its eight original
bytes. A trimmed value longer than four bytes rejects before GET runs.

## Character transformations

*TRIM and *RIGHTADJ require character CONST or local VALUE parameters.
*TRIM removes leading and trailing encoded blanks before copying into a
private argument image. Fixed character parameters pad on the right.
*RIGHTADJ pads on the left; without *TRIM it preserves the supplied blanks.
Overflow retains the rightmost bytes for right adjustment and the leftmost
bytes otherwise. *EXACT rejects overflow before that truncation.

Arrays transform each element independently. *VARSIZE uses actual character
widths and dimensions; missing converted elements receive native blanks.
Fixed array actuals must contain enough elements. VALUE images remain writable
and private. Transformed CONST arguments are private readonly images even when
the actual is a compatible variable also passed through a writable alias.

Lengths count bytes in the job CCSID. CCSID 37 and 1208 are covered. Opaque
CCSID(*HEX)/65535 fields retain raw bytes; trimming removes the job's encoded
blank byte and preserves null bytes. There is no Unicode character-aware
truncation or additional CCSID conversion in this contract.

## Exact type checks

For writable references, *EXACT requires matching native kind, length,
precision/scale and opaque mode. Arrays must match dimension and declared
ASCEND/DESCEND attributes. A reference cannot silently borrow a shorter prefix.

CONST and VALUE permit shorter character values, smaller float formats and
numeric conversions that fit the formal's integer and fractional positions.
Packed, zoned and integer declarations can interconvert under those position
checks; float and non-float data remain distinct. Character declarations must
fit even when their current contents are mostly blank. With *TRIM, the trimmed
value's encoded length is checked instead. Literals and untyped expressions
are checked from their values before conversion, preventing silent character
truncation and numeric rounding. Declared array order is not inferred from an
array expression.

LIKEDS parameters require a common structure definition through the LIKEDS
chain. Independently declared structures with identical byte layouts are not
related. Definition identity distinguishes local shadowed names. Structure
return metadata preserves that relationship, including RTNPARM returns.
Structure arrays must also match dimensions. Unrelated raw expressions cannot
supply missing structure ancestry.

Known incompatible declarations reject during compilation. Remaining value
checks fail with emulator status 105 before target dispatch. Argument errors
are handled with MONITOR; CALLP(E) retains its existing target-error semantics.
Writable numeric output receivers need not contain valid input data merely to
pass the metadata check.

These three options are caller-side prototype attributes. An explicit PR can
specify them while its corresponding PI omits them. An implicit prototype uses
the PI attributes. PR/PI array sort declarations must agree. Existing optional,
omitted, CONST, VALUE and hidden RTNPARM argument positions remain intact.

## ABI and evidence

Converted buffers retain the existing 1 MiB admission bound, readonly checks
and call lifetime. Saved programs recompile the stored source/metadata after
source-member removal. Native APIs receive the transformed bytes. Process
protocol 2 permits unchanged readonly echoes and validates all returned values
before committing any writable receiver.

Tests cover scalar/array transformations, EBCDIC/UTF-8 lengths, raw bytes,
numeric position checks, sort attributes, LIKEDS ancestry/shadowing/returns,
optional and omitted arguments, fixed columns, admission and expired borrows.
Saved-program checks exercise real queues and source removal at CCSID 37/1208.
Process tests cover transformed bytes, atomic rejection and no-launch failures.
RPGOPTIONS supplies terminal compile/call acceptance.

Pointer/STRING/CONVERT/NULLIND modes, varying character declarations,
graphic/UCS-2 types, external LIKE and LIKEREC ancestry,
enumeration/object interfaces,
nondefault date/time formats and broader foreign/ILE binding remain open.
See [program signatures](rpg-program-signatures.md),
[local procedures](rpg-local-procedures.md),
[aggregate parameters](rpg-aggregate-parameters.md) and
[variable-size references](rpg-variable-parameters.md).

Verification (2026-10-18): all 4913 solution tests pass with zero failures/skips,
including 72 new cases: 61 unit, eight saved-program/integration and three
external-process cases. All 798 final focused checks, locked restore, a Release
build with zero warnings/errors, RPGOPTIONS terminal acceptance, generated
inventory, local links and whitespace checks pass. Full-suite wall time is 7m45s;
no performance target is claimed. Terminal option acceptance runs at 24x80;
general display acceptance also covers 27x132.

The first full run passed 4911/4913 cases, with a socket-frame timeout and a
queue concurrency fixture failure. Blocking queue workers now use dedicated
threads, one cancellation deadline and complete cleanup. Production queue and
session behavior is unchanged. All four disconnect/ENDJOB variants pass in the
expanded focused rerun; the final full run is clean.
