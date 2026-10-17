# Source inclusion and conditional compilation

CL preprocessing is an iSeriesPC extension, defined separately from RPG syntax.
`CRTCLPGM` expands the source before creating the program. `/COPY`, `/UNDEFINE`,
`/TITLE`, `/EJECT` and other RPG-only directives are rejected by the CL preprocessor.
RPG has its own directive grammar, described below. Both compilers use
`ProgramSourceResolver` and the same bounded source snapshot/map format.

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

## RPG directives

`CRTBNDRPG` expands `/COPY` and `/INCLUDE` before parsing declarations and
statements. In this interpreted, non-SQL subset they perform the same inclusion.
The directive families follow IBM's [compiler directive reference](https://www.ibm.com/docs/en/i/7.5.0?topic=concepts-compiler-directives);
the finite grammar and limits here define the emulator's supported behavior.

```rpg
**free
/DEFINE REPORTING
/IF DEFINED(REPORTING)
/COPY QGPL/QRPGLESRC,PRINTPART
/ELSE
sndpgmmsg 'Reporting is disabled';
/ENDIF
```

`/DEFINE`, `/UNDEFINE`, `/IF`, `/ELSEIF`, `/ELSE` and `/ENDIF` are case-insensitive.
Conditions accept `DEFINED(symbol)` or `NOT DEFINED(symbol)`. Symbols use the
1–64-character alphabet and 256-symbol bound described above; definitions are
shared across included members. Each member must balance its own conditionals.
An ELSEIF chain selects at most one branch. Inactive branches neither resolve
sources nor change symbols. Invalid directive syntax fails even when inactive.
Bare-symbol conditions and `*NOT` are CL extensions and are rejected in RPG.

Member copies accept `MEMBER`, `FILE,MEMBER` or `LIB/FILE,MEMBER`.
An omitted file means QRPGLESRC. Unqualified files search the calling job's
library list for the member, continuing past matching files without that member;
`*CURLIB/FILE,MEMBER` selects the current library. These defaults follow IBM's
[/COPY and /INCLUDE reference](https://www.ibm.com/docs/en/i/7.6.0?topic=directives-copy-include).
`LIB/FILE(MEMBER)` is also accepted as an emulator extension. Unlike CL's sibling
member resolution, a bare RPG member always selects QRPGLESRC. Every resolution
checks current read authority; inaccessible earlier files are errors, rather
than silently falling through to another library.

Stream sources include paths relative to the including file or absolute paths;
single-quote paths containing spaces, doubling embedded apostrophes. They use
strict UTF-8 and require non-adopted *SERVICE authority on every read. Member
sources cannot include host paths. IFS fallback/search directories, suffix search,
double-quoted names, predefined compiler symbols, `/EOF`, `/SET`, `/RESTORE`, SQL
precompiler differences and directives inside continued statements are unsupported.
The existing RPG parser's one-statement-per-line and column rules still apply.

`/TITLE` accepts heading text; `/EJECT` takes no argument; `/SPACE` accepts an
unsigned count or no argument. These affect no executable behavior: their original
text remains in source snapshots, and no compiler listing is generated. `/FREE`
and `/END-FREE` pass through to the existing parser. Directives accept leading
whitespace or six sequence/blank columns before the slash. Fixed comment lines
and `//` comments do not execute directives; quoted text retains literal slashes.

RPG uses the same source/document/depth/conditional/expansion bounds, cancellation
and hash-bound map validation as CL. `SRCMBR(*PGM)` defaults to the target program
name and `SRCFILE` defaults to QRPGLESRC. Qualified PGM targets are accepted; the
legacy LIB/QSYS target default remains an extension. Program objects store the
expanded text and `ipc.source.map`; CALL restores the compiled snapshot without
reading original members. Copy, source deletion and source-authority changes do
not alter existing programs. Recompilation requires live source authority. Object
signatures cover the source and map through the existing signing envelope.

RPG statements retain expanded line numbers plus original member/line/column and
include chains. Block compilation errors and unhandled runtime errors report those
locations, including after source deletion. CPF errors keep their message IDs.
Stale or invalid maps fail CALL explicitly. Debugger controls remain C15 work.

`RpgPreprocessingTests` covers nested/repeated includes, conditionals, listing
directives, declarations, sequence columns, malformed syntax, library/current-library
resolution, cycles, missing and unauthorized members, source/expansion limits,
cancellation, strict UTF-8 streams, copied snapshots, revoked authority, stale maps
and compiler/runtime diagnostics. The terminal fixture compiles RPGPREP from nested
members with `/COPY`, `/INCLUDE`, conditions and listing directives, then calls it.
