# RPG file search arguments

SQLite RPG `CHAIN`, `SETLL`, `SETGT`, `READE`, `READPE` and keyed `DELETE` now
consume ordered compound search arguments. The compiler retains every component;
nested functions, spaces, colons within quoted text and doubled apostrophes do
not terminate an operand. Malformed lists, missing file names and extra operands
fail compilation with original member/include locations.

## Forms and conversions

Free calculations accept a scalar field/literal, a declared KLIST name, a
parenthesized expression list, or `%KDS(structure{:count})`. For example:

```text
chain (account:region) ORDERS;
setll (account:%subst(region:1:2)) ORDERS;
reade *KEY ORDERS;
delete (account:region) ORDERS;
```

Components map to the file's declared key fields in sequence order, independently
of source variable names. A shorter list is a contiguous leftmost key prefix.
Excess components fail before changing records. Expressions and current field
values are evaluated anew for each operation. SQLite's shared key functions
continue to supply exact decimal, CCSID, descending and member/RRN ordering.
IBM documents the [search-argument forms](https://www.ibm.com/docs/ssw_ibm_i_74/rzasd/fileopkeys.htm)
and [partial-key rules](https://www.ibm.com/docs/en/i/7.5.0?topic=file-valid-search-arguments).

Lists and `%KDS` require matching value categories. Numeric strings do not become
numeric keys implicitly. Compatible numeric values convert to the key's scale
by truncation toward zero; overflow fails through the shared record validator.
Character values convert to the file CCSID and byte width, padding through the
key codec and truncating at complete character boundaries. Explicit floating
literals must be finite. Decimal literals must fit `System.Decimal` exactly;
silent literal rounding into another key is rejected. Operators such as `A+1`
and `2*-3` no longer need spaces around their symbolic operators.

`%KDS` reads the selected structure's top-level scalar subfields in declaration
order. An omitted count uses all of them. A supplied count is evaluated at each
operation and must be an integer from 1 through both the available subfield
count and the file key count. It may be a variable or expression. A scalar
structure from the currently documented fixed D-spec subset is required;
arrayed structures are rejected. These rules follow IBM's
[%KDS reference](https://www.ibm.com/docs/en/i/7.6.0?topic=functions-kds-search-arguments-in-data-structure).
This feature uses the existing interpreter DS model; it does not establish native
packed/overlaid DS memory ABI or complete DS declaration support.

`READE`/`READPE` accept `*KEY` for the positioned full key. The existing traditional
no-search-argument form also remains supported. Bounds preserve program fields;
input fields change only after successful input. Failed input leaves them intact.

## KLIST declarations

`KLIST` and immediately following `KFLD` declarations bind scalar field names,
not cached values. They may occur in traditional calculations outside `/FREE`
regions; they are rejected inside a `**FREE` program. Both standard RPG IV
C-spec declaration positions and the existing interpreter C-spec layout are
accepted. Standard positions are: specification `C` in column 6, KLIST name in
12–25, opcode in 26–35 and KFLD scalar result field in 50–63. Conditioning
indicators, implicit length declarations and result indicators are rejected.

A list must have a unique name in its procedure scope and at least one KFLD.
Procedure-local lists shadow global lists; the compiler binds the appropriate
list to each statement before extracting procedure bodies. KFLDs must name
declared scalar fields or supported scalar DS subfields. Array fields are
rejected. KFLD attributes are checked against the live file at execution;
incompatible kinds, lengths, scale, varying form or character CCSID fail rather
than receiving the list-expression conversions. IBM describes
[KLIST grouping and scope](https://www.ibm.com/docs/en/i/7.5.0?topic=codes-klist-define-composite-key)
and [KFLD attribute requirements](https://www.ibm.com/docs/en/i/7.4.0?topic=codes-kfld-define-parts-key).

There are local compiler budgets of 256 key lists, 128 fields/components per list,
32768 operand characters and 32 parenthesis levels. Expression parsing also
bounds unary nesting to 128 levels. The existing cursor's 128 sort-term limit
still applies, including nullable terms and member/RRN ties.

## Keyed deletion and transaction boundaries

Keyed DELETE searches for one record, checks its full before image and deletes
its physical member/RRN. Duplicate-key siblings remain. Program input fields
are not overwritten by the search. `%FOUND(file)` reports success or a miss.
A miss preserves the prior cursor position and current record. Delete authority
is required even when the key is absent.

The database cursor holds the commitment definition's gate across search,
mutation and position update. Commit/rollback cannot split these steps. Successful
deletion consumes the input mutation target, and the next read continues after
the deleted record. Enlisted writes use the existing intent/journal publication
path, including logical-file source identity. A custom provider without the
physical cursor protocol rejects the new keyed DELETE operation. IBM documents
[DELETE selection, status and continuation](https://www.ibm.com/docs/en/i/7.4.0?topic=codes-delete-delete-record).

## Evidence and remaining work

`RpgFileKeyTests.cs` covers all search operations, nesting/quotes, source mapping,
live KLIST/DS values, partial keys, counts/types, large decimals, UTF-8 byte widths,
literal precision, compiler bounds, missing targets, authority and duplicate
deletion. `RpgFileKeyTransactionTests.cs` covers logical compound deletion across
rollback and committed journal publication. The terminal fixture compiles and
calls RPGKEYS with native KLIST declarations, `%KDS`, nested expressions and one
duplicate deletion.

Unkeyed CHAIN, limits and DELETE now use [relative record access](relative-records.md).
Hosts can select arrival access explicitly even when a PF has DDS keys.
Declared DISK input now binds external fields, access modes and open state; see
[disk file declarations](rpg-disk-files.md). Declared noncommit update/delete modes now use retained input, N and UNLOCK.
Commitment-open modes, complete RRN allocation/writeback, independent
IBM golden acceptance, `EXPROPTS(*STRICTKEYS)`, H/M/R extenders, full fixed
calculation layouts, nested DS declarations and complete memory/prototype ABI,
null-indicator attributes and ALWNULL control remain unfinished. KFLD null flags
fail explicitly. Full native update-read locks, isolation, access-path movement
and complete commitment commands remain open as described in
[cursor navigation](cursor-navigation.md) and [record transactions](record-transactions.md).

Declared output uses the [record addition contract](rpg-disk-files.md#output-and-record-addition).
E/EN/NE and numeric status/MONITOR now follow the bounded
[error handling contract](rpg-file-status.md).

Free/qualified/array DCL-DS layouts now have [native byte storage](rpg-native-structures.md).
%KDS uses native scalar subfield values; array-valued keys remain unsupported.
