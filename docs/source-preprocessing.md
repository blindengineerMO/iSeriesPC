# CL source inclusion and conditional compilation

CL preprocessing is an iSeriesPC extension, defined separately from RPG syntax.
`CRTCLPGM` expands the source before creating the program. `/COPY`, `/UNDEFINE`,
`/TITLE`, `/EJECT` and other RPG-only directives are rejected by the CL preprocessor.
RPG integration remains in C09; its future preprocessor can use the same
`ProgramSourceResolver` without inheriting CL's grammar.

```cl
PGM
/DEFINE REPORTING
/IF DEFINED(REPORTING)
/INCLUDE QGPL/QCLSRC(PRINTPART)
/ELSE
SNDPGMMSG MSG('Reporting is disabled')
/ENDIF
ENDPGM
```

Supported directives are `/INCLUDE`, `/DEFINE`, `/IF`, `/ELSE` and `/ENDIF`,
case-insensitively at the start of a line after whitespace. Symbols have 1–64
letters/digits/underscores and start with a letter. Definitions are shared across
included members and repeated definitions are harmless. `/IF` accepts
`DEFINED(symbol)`, `NOT DEFINED(symbol)` (`*NOT` also accepted), or a previously
defined symbol. An undefined bare symbol in an active condition is an error;
`DEFINED` is the explicit way to test an absent symbol. Other expressions fail.
Inactive branches do not resolve includes or change definitions. Each document
must balance its own conditionals; repeated ELSE and include cycles fail.

Member includes accept a sibling member name, `LIB/FILE(MEMBER)` or `LIB/FILE,MEMBER`.
Qualified source files use the caller's library-resolution and live read-authority
checks. Stream-source includes use relative paths from the including file or absolute
paths; quote paths containing spaces, doubling embedded apostrophes. Every stream
read requires non-adopted *SERVICE authority and the host account's filesystem access.
Host sources are strict UTF-8, with an optional UTF-8 BOM. Linux FIFOs are opened
without blocking and rejected before reading. Member sources cannot switch into
filesystem includes. Missing, unauthorized, malformed or oversized input prevents
program creation.

CL block comments are removed while preserving columns, and directive-looking text
inside comments or quoted strings is not interpreted. Existing `//` and leading-`*`
line comments remain extensions. Native CL continuation/control-flow work remains
in C07's interpreter item; this preprocessor does not claim that work is complete.

`SRCMBR(*PGM)`, including the CRTCLPGM default, selects the target program's name.
The default source file is QCLSRC on the job library list. Qualified PGM targets
are accepted; the legacy LIB parameter and unqualified QSYS target default remain
extensions pending the broader command contract. See IBM's
[CRTCLPGM parameters](https://www.ibm.com/docs/en/i/7.5.0?topic=c-create-cl-program).

## Compiled snapshots and diagnostics

The program stores expanded executable source and a versioned `ipc.source.map`
attribute containing original source snapshots, physical line/column locations and
include chains. A SHA-256 binding rejects stale maps when executable source changes.
Program copies retain this evidence. CALL reads the compiled snapshot: changing,
deleting or revoking access to the original include does not change existing programs.
Recompilation requires current source authority. Object signatures cover the expanded
source and the map attribute through the existing canonical object-signing envelope.

Compiler errors, missing block terminators and runtime command/call failures retain
original locations, including nested include chains. Blank lines no longer shift CL
error line numbers. Block resolution is linear in statement count; duplicate labels
and unmatched/duplicate ELSE/ENDIF fail during compilation. Debugger stepping and
source views remain C15 work, but the source locations and snapshots are retained.

The reader rejects unsupported map versions, unknown/duplicate JSON properties,
stale text hashes, invalid locations and oversized maps. Bounds are 1 MiB/10000 lines
per document, 8192 characters per line, 64 distinct documents, 16 total include levels,
64 nested conditionals, 256 symbols, 4 MiB total source, 4 MiB expanded text and
50000 expanded lines. Location accounting is bounded to 4 MiB; serialized source
maps have a 16 MiB ceiling. Expansion and compilation check job cancellation.

`ClPreprocessingTests` covers nested and repeated includes, compile-time snapshot
consistency, inactive branches/comments, malformed directives, source diagnostics,
limits/cycles/cancellation, stale maps, live authority, object copying and source
removal, strict UTF-8 streams and Linux FIFO rejection. The real terminal test creates
a CL program from source members containing conditional inclusion and calls it.
