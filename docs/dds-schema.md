# DDS compiler, file store, and SQLite schema

WP9 (Ipc.Db) turns DDS source written into source-physical-file members into
compiled `FileDefinition` objects and durable SQLite-backed *FILE objects.
One DDS source member describes one file: `CRTSRCPF` creates the source file,
`ADDSRCPFM` appends DDS lines, `CRTPF SRCFILE/QDDS SRCMBR/MBR` compiles the
member into a physical file and materializes its primary record format as a
SQLite table per member.

## Catalog tables (database schema v2)

Both tables live in the Ipc.Services SQLite database; the compiled definition
is serialized as JSON inside the catalog:

| Table | Columns | Purpose |
|-------|---------|---------|
| `sys_file_defs` | `lib, name, type='*FILE', def` | One row per *FILE; `def` holds `FileDefinition.ToJson()` (format name, fields, lengths, positions, keys, descending, ALWNULL, VARLEN, CCSID, text) |
| `sys_file_members` | `lib, name, type, mbr, created` | Member rows for every *FILE |
| `sys_objects` (inherited) | key `LIB/NAME`, object type `*FILE` | Object identity; `Attribute` = `*SRCPF` or `*PF`, `Format` = primary format name, `Source` = full DDS source text, `Description` = file text |

`FileDefinition` JSON round-trips through System.Text.Json: `Formats` and
`RecordFormat.Fields` are settable collections, `RecordLength` is serialized
via `[JsonInclude]`, and the computed `PrimaryFormat` is `[JsonIgnore]`.

## Member data tables

Each member gets a physical table named `"<LIB>.<FILE>.<MBR>"` (uppercased,
quoted identifiers, always full-width quoting so dots are safe). Columns are
created per the type mapping below, all `NULL`-able. A physical file is
created with its first member (same name as the file); `ADDPFM` adds more.
Both `AddMember` and the file create path go through one transaction so the
member row and the data table are created atomically.

## DDS source grammar (fixed columns)

| Column(s) | Meaning |
|-----------|---------|
| 7 | `A` — field specification |
| 8 | `K` — key field line (marks the field as a key; `DESC` here means descending) |
| 16 | `R` — record-format line (mandatory: compilation fails with *no record formats* if absent) |
| 19–28 | name (record format on `R` lines, field name otherwise) |
| 35 | field type |
| 36–39 | length |
| 40–41 | decimals |
| 45+ | keywords on the field line: `VARLEN`, `ALWNULL`, `CCSID(n)`, `TEXT('…')` |

Field types: `A` Alpha, `P` Packed, `S` Zoned, `B` Binary, `F` Float, `L`
Logic (1/0), `D` Date, `T` Time, `Z` Timestamp. Descending order comes from
the `K` line's `DESC` keyword, not from the field line. `TEXT('…')` is
unquoted on read (outer apostrophes are stripped). Adjacent source lines of
whitespace-free width are the canonical 100-column form.

## Type mapping to SQLite columns

| Field type | SQLite column |
|------------|---------------|
| Alpha, Date, Time, Timestamp | `TEXT` |
| Zoned/Packed, decimals = 0 | `INTEGER` |
| Zoned/Packed, decimals > 0 | `TEXT` (canonical `"F<n>"` decimal) |
| Binary | `INTEGER` |
| Float | `REAL` |
| Logic | `INTEGER` (`0`/`1`) |

`ToSqlValue` normalizes before binding: scale>0 S/P → invariant `"F<n>"`
string; Logic → `1`/`0`; Date → `yyyy-MM-dd`; Time → `hh:mm:ss`; Timestamp →
`yyyy-MM-dd HH:mm:ss`. Omitted values fall back to `DefaultFor` (Alpha `""`,
Logic `false`, Binary `0`, Float `0`, S/P `0`, Date/Timestamp `1900-01-01`,
Time `00:00:00`) — they are stored as defaults, not SQL `NULL`.

`FromSqlValue` reverses back to CLR types: Alpha → `string`, Zoned/Packed →
`decimal` (even for scale 0), Binary → `long`, Float → `double`, Logic →
`bool`, Date/Timestamp → `DateTimeOffset`, Time → `TimeSpan`.

## Record buffer layout (codec)

`RecordCodec` packs one record into a `RecordLength` byte buffer according to
field positions:

- **Zoned** — EBCDIC zone digits: each digit in the low nibble with `0xF0`
  zone in the high nibble; the sign (`0xD` negative, `0xF` positive) rides in
  the last byte's high nibble. Scale is applied via the `10^-n` decimal
  constant, so encode divides and decode multiplies by that constant.
- **Packed** — BCD nibble pairs; odd-length fields reserve the first nibble as
  `0x0F`; the sign nibble (low nibble of the last byte, `0xF`/`0xD`) holds the
  least-significant digit position on odd lengths according to standard
  Packed-Decimal layout.
- **Alpha / text** — padded to length with spaces; the `VARLEN` length-prefix
  byte precedes the content.
- **Binary** — big-endian 8-byte two's complement.
- **Float** — IEEE-754 big-endian (4 or 8 bytes).
- **Logic** — `0xF0`/`0xF1` per DDS.
- **Date/Time** — stored as Julian day count / seconds-of-day packed values.

`ReadZoned` and `WriteZoned` are the inverse of one another; both derive
digits from the low nibble and the sign from the last byte's high nibble.

## Keyed access

Key fields are `Sequence > 0`, ordered by key sequence. `ReadKeyed` orders by
every key; `ReadKeyPrefix` constrains equality on the leading key values
(supplied in key order) and throws `CPF3201` when the file has no keys or no
key values are given. Ordering on scale>0 Zoned/Packed keys uses
`CAST("col" AS REAL)` so the TEXT-stored decimals sort numerically;
descending keys append `DESC`. Files with no keys order by `_rowid_`.
Sequential read also uses `_rowid_` (insert order).

## Source files and member editing

`CRTSRCPF` builds a file whose primary format is fixed:
`SRCSEQ P(6,0)`, `SRCDAT D(8)`, `SRCDTA A(100) VARLEN CCSID(37)`.
`ADDSRCPFM` (a workbench-only command) appends a source line: the data value
is truncated to 100 characters on store; an optional `SEQ(n)` sets the source
sequence number, otherwise the next numeric value is chosen. Compilation
(`CRTPF`) loads consecutive member lines as the DDS source and runs the
compiler over them, so multi-row record-format + field lines assemble into one
`FileDefinition`.

## Limitations

- Only the primary record format is materialized; multi-format record files
  create one table from the primary format.
- CPF codes used by the store: `CPF9801` file not found, `CPF2817` member not
  found, `CPF3201` no key fields / no key values.