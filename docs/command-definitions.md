# Command definitions

The command-definition engine persists built-in adapters and user commands as
`*CMD` catalog objects. The compiler subset below satisfies the first C07 item;
command categories and interpreted language completion have separate open items.

Compile a source member with:

```text
CRTCMD CMD(QGPL/REPORT) PGM(QGPL/REPORTCPP) SRCFILE(QGPL/QCMDSRC) SRCMBR(REPORT)
```

A minimal definition:

```text
CMD PROMPT('Customer report')
PARM KWD(CUSTOMER) TYPE(*NAME) LEN(10) MIN(1) PROMPT('Customer' 2)
PARM KWD(COUNT) TYPE(*DEC) LEN(3 0) DFT(10) RANGE(1 100) PROMPT('Count' 1)
```

`REPORT ACME COUNT(12)` validates before calling the program. Text invocation,
structured `CommandCall`, terminal F4, and authenticated headless execution read
the same stored definition. `DSPCMD CMD(QGPL/REPORT)` generates a reference from
that metadata. Positional/CPP order follows PARM source order; PROMPT's optional
order changes the form only. F4 displays defaults and retains errors for correction.
`HLPPNLGRP` and `HLPID` on CRTCMD bind F1 to the shared UIM help service.

The implementation follows IBM's [command-definition statements](https://www.ibm.com/docs/en/i/7.5.0?topic=commands-cl-command-definition-statements),
[PARM statement](https://www.ibm.com/docs/en/i/7.6.0?topic=p-parameter-definition),
and [CL/HLL processing-program correspondence](https://www.ibm.com/docs/en/i/7.5.0?topic=command-cl-hll-processing-program).
Its current compiler supports CMD, PARM and two-level object/library QUAL, quoted
prompts, LEN, MIN/MAX, DFT, RSTD/VALUES, SPCVAL, constant numeric RANGE, CASE, FILE
usage and INLPMTLEN(*PWD). Source errors retain their member/line. Unknown syntax
is rejected; this does not claim every IBM command-definition statement is implemented.

Types currently include *CHAR, *NAME, *GENERIC, *PNAME, *DEC, *INT2/*INT4,
*UINT2/*UINT4, *LGL, *DATE and *TIME. Lists use one keyword containing multiple
values or a parenthesized positional list; repeated keyword occurrences are rejected. Numeric overflow, excess scale,
unknown/conflicting/missing parameters, invalid choices and excess list counts
fail before CPP entry. Decimal precision is bounded by the CLR decimal range.
Unquoted *MONO values fold to uppercase; quoted text and *MIXED retain case.
FILE uses IBM's *IN/*OUT/*UPD/*INOUT/*UNSPFD classifications. Qualified layout comes
from a pair of QUAL definitions, not from the FILE classification alone.

Simple-list CPP buffers carry a big-endian two-byte count followed by contiguous
values. Qualified names carry the object and library in two ten-byte fields.
Character conversion checks the declared byte width and job CCSID; packed decimal
and binary integer codecs have independent byte fixtures. CL/RPG receive semantic
numeric scalars, while native adapters receive JSON numeric values and strings
through the existing host ABI. Complex buffers remain immutable byte values through command dispatch. A native
CPP registered with `CRTEXTPGM ... PROTOCOL(2)` receives explicit base64 buffers,
including arbitrary binary data under UTF-8. Protocol 1 and the current interpreted
string runtimes require an exact CCSID round-trip and fail before CPP entry when
that is impossible. Typed, byte-addressed CL/RPG variables remain C07/C09 work;
they are not silently coerced through replacement characters.

Source and compiled metadata share one signed object payload. Runtime loading
checks their agreement and live authority, including built-in command-object
restrictions. CRTCMD replacement is explicit and atomic, preserves ownership,
and invalidates the old signature. CPP/help dependencies prevent deletion or
relocation that would leave a stored command pointing at a missing object.
Signed packages validate all objects and permissions before their first mutation,
and restore command dependencies in the same transaction as their programs.

Built-in adapter definitions preserve existing finite handler contracts. Their
raw operands still use the owning service's semantic validation; completing the
release-wide built-in parameter catalog remains part of the C07 work. Startup
updates only fingerprinted, unchanged, unsigned QSYS adapter definitions.

Current evidence: compiler/binder tests; persisted restart and structured-call
parity; F4 order/default and object-authority tests; authenticated headless calls;
independent CPP bytes and a real native CPP; signed multi-object restore; a real
PTY compile/prompt/execute/reference scenario. The full 622-test suite and OpenSSH acceptance pass.
