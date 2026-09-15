# DDS compiler, file store, and SQLite schema

Display DDS uses the separate [Ipc.Dsp compiler and panel runtime](display-files.md).
Physical and simple logical files accept native DDS columns. The earlier
iSeriesPC physical-file layout remains available for existing source members.

WP9 (Ipc.Db) turns DDS source written into source-physical-file members into
compiled `FileDefinition` objects and durable SQLite-backed *FILE objects.
One DDS source member describes one file: `CRTSRCPF` creates the source file,
`ADDSRCPFM` appends DDS lines, `CRTPF FILE(LIB/FILE) SRCFILE(LIB/QDDS) SRCMBR(MBR)` compiles the
member into a physical file and materializes its primary record format as a
SQLite table per member.

## Catalog tables (introduced in database schema v2)

The current catalog is schema v18. [Ordered migrations](catalog-migrations.md)
preserve these file tables and add migration history.

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
created per the type mapping below, all `NULL`-able. CRTPF creates a same-name first member by default; MBR(name) chooses its name and
MBR(*NONE) creates no member. MAXMBRS defaults to 1 for CRTPF; 1–32767 and *NOMAX
(the 32767 system maximum) are supported. Typed/legacy definitions retain a 32767
maximum unless explicitly limited. ADDPFM rejects duplicate names and atomically
checks the current limit before creating the member and its indexes. Source saves
use the same limit check. CHGPF currently changes MAXMBRS, accepting *SAME/*NOMAX;
shrinking below the existing member count fails before any catalog change.
See IBM [CRTPF](https://www.ibm.com/docs/en/i/7.5.0?topic=c-create-physical-file)
and [CHGPF](https://www.ibm.com/docs/en/i/7.5.0?topic=c-change-physical-file).
Both `AddMember` and the file create path go through one transaction so the
member row and the data table are created atomically.

## DDS source grammar (fixed columns)

Native physical DDS uses the following columns:

| Column(s) | Meaning |
|-----------|---------|
| 6 | `A`; `*` in column 7 starts a comment |
| 17 | `R` record, `K` key, blank field |
| 19–28 | Record or field name |
| 30–34 | Right-aligned length or declared digits |
| 35 | Data type |
| 36–37 | Right-aligned decimal positions |
| 45+ | Keywords, including continued statements |

One record is supported, followed by fields and optional key specifications.
Blank type means A when decimals are blank and P otherwise. Types A/P/S/B/F/L/T/Z
map to character, packed, zoned, binary, floating point, date, time and timestamp.
Binary declared lengths 1–4/5–9/10–18 map to 2/4/8 storage bytes; declared digits
are retained separately. Binary values can use the full signed storage range.
`FLTPCN(*SINGLE|*DOUBLE)` selects 4/8-byte floats. Native datetime fields use
implicit lengths 10/8/26; `DATFMT(*ISO)` and `TIMFMT(*ISO)` are supported.
Other formats and unsupported keywords fail with source-member and line diagnostics.

Field keywords include `TEXT`, `CCSID`, `ALWNULL`, `VARLEN` and `DFT`; keys use
`DESCEND`. Decimal support is bounded by CLR decimal (1–29 declared digits,
scale up to 28, with runtime range checks). Character and record lengths obey
the 32766-byte native storage bound, including variable-field prefixes, the
24-byte variable-storage overhead and the null bitmap. The latter two are
storage-limit accounting, separate from the program record data area below.
Sources are bounded to 1 MiB/10000 lines, 1024 fields and 5000 characters per
continued statement. Quoted keyword values and `+`/`-` continuations are parsed.
`SRCMBR(*FILE)`, including the default, selects the target file's name.

The legacy layout is selected only when native column-6 specifications are absent:
A in column 7, K in column 8, R in column 16, names in columns 19–28, type in 35,
length in 36–39 and decimals in 40–41. Its L means the iSeriesPC logic extension,
and D is a basic date; native L means date. Legacy sources remain a compatibility
extension and do not constitute native DDS syntax.

References: IBM [physical DDS syntax](https://www.ibm.com/docs/en/i/7.4.0?topic=syntax-dds-physical-file),
[field lengths and storage limits](https://www.ibm.com/docs/en/i/7.5.0?topic=peplfp1t4-length-physical-logical-files-positions-30-through-34),
[data types](https://www.ibm.com/docs/en/i/7.5.0?topic=44-data-type-physical-logical-files-position-35),
[binary input range](https://www.ibm.com/docs/en/i/7.5.0?topic=format-processing-externally-described-binary-input-field).

## Type mapping to SQLite columns

| Field type | SQLite column |
|------------|---------------|
| Alpha, Date, Time, Timestamp | `TEXT` |
| Zoned/Packed, decimals = 0 and precision ≤18 | `INTEGER` |
| Zoned/Packed, decimals = 0 and precision >18 | `TEXT` |
| Zoned/Packed, decimals > 0 | `TEXT` (canonical `"F<n>"` decimal) |
| Binary | `INTEGER` |
| Float | `REAL` |
| Logic | `INTEGER` (`0`/`1`) |

`ToSqlValue` normalizes before binding: scale>0 S/P → invariant `"F<n>"`
string; Logic → `1`/`0`; Date → `yyyy-MM-dd`; Time → `hh:mm:ss`; Timestamp →
`yyyy-MM-dd HH:mm:ss.ffffff` (legacy 14-byte formats use whole seconds). Omitted values fall back to `DefaultFor` (Alpha `""`,
Logic `false`, Binary `0`, Float `0`, S/P `0`, Date/Timestamp `1900-01-01`,
Time `00:00:00`). Nullable character/numeric fields default to SQL `NULL`; explicit nulls in non-null fields are rejected. See IBM [DFT defaults](https://www.ibm.com/docs/en/i/7.4.0?topic=80-dft-default-keywordphysical-files-only). Native date/time/timestamp fields default to the current UTC date/time when omitted, including nullable datetime fields; explicit null remains null. Explicit DFT accepts quoted character/ISO datetime values, numeric constants, exact-width
hex character values and *NULL with ALWNULL. Empty DFT('') requires VARLEN.
Defaults are validated against the codec before object creation and persisted in the
field definition. Omitted PF fields, unprojected LF output fields and CPYF MAP target
fields use those defaults; explicit supplied values and null indicators remain authoritative.

`FromSqlValue` reverses back to CLR types: Alpha → `string`, Zoned/Packed →
`decimal` (even for scale 0), Binary → `long`, Float → `float`/`double` by declared precision, Logic →
`bool`, Date/Timestamp → `DateTimeOffset`, Time → `TimeSpan`.

## Record buffer layout (codec)

Record buffer layout version 2 uses byte widths for packed fields and includes
VARLEN length prefixes. The reader adapts version 1 catalog definitions to this
layout; member tables store typed SQL values and are unchanged. Version 1 raw
binary buffers are not accepted as layout 2. Recompiled/serialized definitions
carry the version explicitly.

- Zoned: one EBCDIC digit per byte; output uses F zones and F/D signs.
- Packed: `(digits + 2) / 2` bytes, with a leading zero nibble for even digit counts
  and a final C/D sign. Valid alternate A/B/E/F input signs are recognized.
- Binary: signed big-endian two's complement, 2/4/8 bytes.
- Float: IEEE-754 big-endian, 4/8 bytes; output requires finite representable values.
- Character: strict declared CCSID encoding and encoded-space padding. VARLEN adds
  a two-byte big-endian byte count; its reader preserves significant trailing spaces.
- Logic: an iSeriesPC extension using encoded character 0/1 in the codec CCSID.
- Date/time/timestamp: external ISO text widths 10/8/26, with microsecond timestamp
  precision. Legacy basic text widths 8/6/14 remain supported.

Explicit nulls use the codec's separate signed-short indicator overload: -1 for
null and 0 for a value. Nulls require nullable fields and do not occupy bytes in
the record data area. Native API-specific null-map marshalling remains separate.
Omitted fields use their type defaults. Malformed digits/signs, overflow, excess
scale, invalid character bytes and out-of-bounds layouts fail explicitly.

Independent hex fixtures cover packed/zoned numbers, negative binary limits,
CCSID padding/UTF-8 boundaries, leap dates, ISO timestamps, packed/VARLEN offsets
and null indicators. Decimal support uses the exact CLR decimal range (up to 29
significant digits and scale 28). Full native DDS type/format coverage remains open.

References: IBM [packed/zoned representations](https://www.ibm.com/docs/en/module_1678991624569/pdf/SA22-7832-14.pdf),
[external datetime formats](https://www.ibm.com/docs/en/i/7.4.0?topic=concepts-date-time-timestamps),
and [timestamp fields](https://www.ibm.com/docs/en/i/7.5.0?topic=formats-timestamp-data-type).

## Keyed access

Key fields are `Sequence > 0`, ordered by key sequence. `ReadKeyed` orders by
every key; `ReadKeyPrefix` constrains equality on the leading key values
(supplied in key order) and throws `CPF3201` when the file has no keys or no
key values are given. Physical and logical keys use the same exact decimal and
CCSID byte-key functions. New keyed physical members have maintained expression
indexes. Native file-level UNIQUE(*INCNULL|*EXCNULL) is enforced per physical member;
PF constraints have separate ownership from logical constraints, survive object copies,
and cannot be removed by deleting an LF. Nulls sort above non-null key values. Equality/update/delete do not cast decimals to floating point; mutations
require the complete key. Files with no keys use record-number order.

Large integral decimals use TEXT storage in newly created members. Writes to
legacy INTEGER-affinity members with precision above 18 are rejected before they
can round a value. Their storage migration remains open; already-rounded values
cannot be reconstructed from SQLite REAL data. Reads reject malformed decimal
values instead of returning zero. These checks apply through logical files and
CPYF as well. See [logical files](logical-files.md) and [file copy](file-copy.md).

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
