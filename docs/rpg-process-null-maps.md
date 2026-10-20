# Process null-map parameters

Registered Linux process programs can opt into JSON pipe protocol 3 to receive
RPG `OPTIONS(*NULLIND)` arguments. This extends [null-map parameters](rpg-null-parameters.md)
using the same pinned executable/dependency manifests, authority checks, private
working directory, timeout, cancellation and bounded streams as protocols 1/2.
This is an emulator process contract; native ILE linking and independent native
foreign-language ABI acceptance remain open. C09 stays partial.

## Wire contract

Pass `protocolVersion: 3` to `ExternalPrograms.Register`. Requests contain one
`nullMaps` slot per data argument. A slot is `null` when no map was borrowed,
including `*OMIT`. Unpassed arguments have no slot. Mapped slots contain exactly
`nullable`, `values` and `readOnly`:

```json
{
  "version": 3,
  "parameters": [3, 4],
  "nullMaps": [
    { "nullable": [true], "values": [true], "readOnly": false },
    null
  ]
}
```

A successful response can update data and flags independently:

```json
{
  "version": 3,
  "success": true,
  "parameters": [12, 4],
  "nullMaps": [
    { "nullable": [true], "values": [false], "readOnly": false },
    null
  ]
}
```

Arrays and structures use the protocol 2 buffer representation:
`{"type":"buffer","ccsid":37,"data":"AwMD"}`. Null-map entries follow native
subfield declaration and array element order, including nonnullable fields and
anonymous padding. Nested layouts flatten recursively. Nonnullable structure
entries are always off; scalar/array nonnullable actuals passed to null-aware
formals receive their scoped temporary map. Empty LEN-only structures carry
empty arrays. A map-only response may omit `parameters` to preserve data.

Returned maps must preserve every capability, slot, length and readonly bit.
Values must be booleans. Unknown/duplicate fields, unexpected or missing mapped
slots, changed CONST flags and nonnullable flags set on reject the response.
When no arguments have maps, `nullMaps` may be omitted in the response. With any
mapped argument it is required. CONST data and maps may be echoed unchanged.

The session validates all returned data and maps and records flag changes before
publishing updates. A malformed later argument or sideband leaves all caller
storage unchanged. A valid response with `success:false` publishes its validated
updates before reporting the call failure, matching existing process semantics.
The lower-level service returns staged map values in `ExternalProgramResult.NullMaps`;
it never writes the supplied maps itself. The calling host owns publication.

## Bounds and aliases

The request remains limited to 64 KiB UTF-8, at most 256 parameters and JSON depth
16; stdout and stderr each retain the 1 MiB bound. Large maps reject before
allocating wire snapshots or starting a process. Buffer CCSIDs follow existing
supported code pages. No caller memory or backing storage is exposed over JSON.

Process calls reject overlapping data arguments and known overlapping null-map
storage before process startup. This includes named flags passed separately as
data, named flags contained in another passed buffer, and forwarded indicators.
Map storage identities follow scoped borrowing; detached snapshots own private
state. Synchronous native calls continue sharing aliases normally. A process
protocol that represents aliases remains open.

Protocols 1/2 continue rejecting null-map sidebands before process execution.
Their ordinary scalar/buffer behavior is unchanged. Built-in APIs without a map
contract also continue rejecting sidebands. Registration still defaults to
protocol 1, so existing programs do not silently opt into this contract.

Broader external/CCSID settings, aggregate `%NULLIND`, null-map INZ(*EXTDFT),
divided nullable EXTFLD arrays, broader EVAL-CORR precision/procedure expressions, varying/opaque database layouts,
multi-format/blocked I/O and native golden/scale acceptance remain open. Schema
31, catalog 306 and binding versions DISK 7/external descriptions 2/WORKSTN 5 are
unchanged.

Verification (2026-10-18): all 5791 solution tests pass with zero failures/skips
in 9m05s, including 30 new cases. All 3535 broad and 38 final focused
checks, locked restore, a zero-warning/error Release build and expanded RPGNWIRE
terminal acceptance pass. Buffer cases cover CCSIDs 37/1208; general display
acceptance covers 24x80 and 27x132. Generated inventory, local links and
whitespace checks pass.

[Corresponding-field assignment](rpg-corresponding-assignment.md) now transfers
selected native fields and null indicators, including nested/array and borrowed
maps. Its broader precision/procedure extensions remain open.
