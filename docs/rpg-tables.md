# Native RPG tables and reference calls

Global standalone definitions whose names begin with TAB and specify DIM now
declare tables. Names contain 3–10 characters. Native D-specs, column-limited
free definitions and fully free DCL-S share this rule. Arrays defined within
data structures keep their array identity, including names beginning with TAB.
The rules follow Array Names, Table Names and Tables in the
[IBM ILE RPG reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf),
pp. 100–102 and 285–287. C09 remains partial.

## Storage and current elements

A table owns native storage for its declared entries, but its name denotes one
current scalar element. The first element is initially current. Reads, EVAL,
arithmetic, CLEAR and byte substrings use that element. An explicit table index
rejects with status 105; native indexed LOOKUP also rejects during compilation.
SORTA rejects table operands. Array-only %LOOKUP/%SUBARR/%XFOOT do not expose
the hidden table storage.

%ELEM(table) returns the entry count. %LEN(table) describes one element;
%SIZE(table) returns one element's native byte width, while %SIZE(table:*ALL)
returns all entries' bytes. These layout queries also work in later declaration
constants. See %ELEM and %SIZE in the
[IBM reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf),
pp. 695–696 and 756–757.

CTDATA, PERRCD, EXTFMT, ALT and inferred DIM(*CTDATA) use the existing
[compile-time data](rpg-compile-time-arrays.md) and
[alternating data](rpg-alternating-arrays.md) codecs. Default initialization and
the existing storage bounds apply: at most 32767 entries, 256 native standalone/
DS allocations, 1 MiB per allocation and 16 MiB per program. Native character
sequence validation uses the activation CCSID and retains source diagnostics.
RETURN retains bytes and selection. LR/reclamation reload initialization data
and restore the first element. CLEAR changes only the current element.

## Table searches

Native LOOKUP takes the search argument in factor 1, the table in factor 2 and
an optional second table in the result. A successful search selects its ordinal
in both tables. A miss preserves both previous selections. The tables need not
have been declared as an ALT pair. If an independently declared alternate lacks
the selected ordinal, status 121 preserves both selections and prior indicators.
Unequal ALT loading dimensions retain their existing explicit rejection.

HI/LO/EQ, nearest/duplicate selection, exact-match priority, native numeric
coefficient comparison, character bytes and global %FOUND/%EQUAL use the
[native LOOKUP rules](rpg-array-search.md). Searches inspect the whole table;
there is no starting-index operand. A table can supply factor 1 through its
current element. Native LOOKUP updates search indicators/status; a false
conditioning indicator skips the entire operation.

%TLOOKUP, %TLOOKUPLT, %TLOOKUPLE, %TLOOKUPGT, %TLOOKUPGE and %TLOOKUPNE accept
a search value, table name and optional alternate table name. They return a
boolean and select both entries only on success. They do not change %FOUND,
%EQUAL or result indicators. Relative searches require ASCEND/DESCEND. Equal
duplicates select the first match; nearest duplicates use the same boundaries
as the array built-ins. %TLOOKUPNE searches sequentially from the first entry.
Other variants use binary search for sequenced tables and sequential exact
search for unsequenced tables.

Built-ins compare numeric values with aligned decimal positions and compatible
precision. Native LOOKUP ignores decimal positions for non-float numeric data.
For example, a native search value 20 can match PACKED(3:1) value 2.0, while
%TLOOKUP(20:table) requires value 20. Character queries compare unsigned native
bytes; native character search arguments can be compared without decoding
invalid bytes. Both activation CCSID 37 and 1208 are covered. See
[IBM %TLOOKUPxx reference](https://www.ibm.com/docs/it/ssw_ibm_i_76/pdf/sc092508.pdf),
pp. 775–776.

## Calls and captured storage

An outgoing table argument borrows the current scalar element. A host changing
the selected element while the argument is active does not move that captured
address. Aliased arguments retain the same storage identity; argument access
expires when the calling scope ends. Native APIs can receive exact-width current
elements, including opaque binary receivers. QWCRSVAL integration checks the
selected 64-byte receiver while another entry stays blank.

Unprototyped stored RPG and CL calls now receive available reference cells.
CL entry binding validates type, length, scale, buffer width and CCSID before
executing the body. Repeated and partially overlapping cells observe writes
immediately; writes remain visible after a callee error. Nested calls forward
the original cell, storage range and readonly metadata. Byte operations on CL
character references retain raw buffers. Ordinary value arguments use private
cells. Native one-byte integer entries retain their actual ABI width.

Legacy semantic scalar cells can participate in ordinary program calls without
claiming a native byte format. Existing prototype calls to ordinary programs
retain their formal coercion/copy convention. Strict API borrowing keeps its
separate type/layout guards. Complete native prototype semantics remain open.

Current-element arguments work with scalar formal parameters. Native *ENTRY
declarations naming tables remain unsupported; complete table/array/procedure
entry ABI, additional prototype options and foreign-language ABI remain open.

## Evidence and remaining work

`RpgTableTests` covers table identity, queries, duplicate/nearest searches,
paired selection, misses, mutation/CLEAR, inferred ALT, byte order, cancellation,
captured API addresses and source diagnostics. `ClBorrowedArgumentTests` checks
incoming aliases, error writeback, raw bytes, partial overlap and layout guards.
`RpgTableIntegrationTests` covers saved-source removal, CTDATA pairs, RETURN/LR/
reclaim, real CL and RPG callees, one-byte aliases and QWCRSVAL receivers in
actual CCSID 37/1208 jobs. The terminal fixture compiles/calls RPGTABLE for
paired LOOKUP, %TLOOKUPNE and preserved selection after a miss.

Verification: all 4154 solution tests pass, including 81 new cases. The 2469
expanded RPG/CL/queue checks, 83 final targeted checks, locked restore,
zero-warning/error Release build and final display PTY pass. RPGTABLE runs at
24x80; general display fixtures also cover 27x132. Generated inventory, local
documentation links and whitespace checks pass. Full-suite time is 7m21s.

Pre-runtime FROMFILE/TOFILE, native table-entry/array/procedure ABI, complete
prototype/binding/service-program support, wider types/CCSID/ALTSEQ, remaining
native specifications and independent native-system golden/scale acceptance
remain open. C09 remains partial. No performance target is claimed.
