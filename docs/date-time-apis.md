# Date and time conversion API

QSYS/QWCCVTDT converts native character date/time layouts and raw eight-byte
system timestamps. It is a versioned IPCAPI program with normal library lookup,
live use authority, signature and manifest validation. `WRKAPILS API(QWCCVTDT)`
shows its supported parameter groups. The date family remains partial for job
date-format/time-zone attributes, named `*TIMZON` objects and their metadata.

## Parameters and storage

| Position | Parameter | Storage |
|---|---|---|
| 1 | input format | CHAR(10) |
| 2 | input variable | CHAR(*) |
| 3 | output format | CHAR(10) |
| 4 | writable output variable | CHAR(*) |
| 5 | error structure | CHAR(*) |
| 6, 7 | input, output time zones | CHAR(10) each |
| 8 | writable time-zone information | CHAR(*) |
| 9 | information receiver length | signed big-endian BIN(4) |
| 10 | precision: `0` milliseconds, `1` microseconds | CHAR(1) |
| 11 | repeated-time segment: `1` first/DST, `0` second/standard | CHAR(1) |

Total counts are 5, 10 or 11. Arguments 6–10 form one complete optional group;
11 is a second optional group. Defaults are *SYS/*SYS, zero information length,
millisecond precision and the first repeated-time segment. When information
length is zero, argument 8 is ignored. *CURRENT ignores both its input variable
and input time-zone parameter and captures the clock once.

CHAR controls/dates use the caller's job CCSID. *DTS and *DOS are raw binary;
do not decode them as text. Output must hold the complete selected layout. The
API writes only its prefix and leaves remaining storage unchanged. Error storage
uses the shared BIN(4) bytes-provided/available and seven-character exception ID
contract; zero bytes provided raises the exception. Aliased output/error receivers,
immutable receivers and rejected normalization fail before other outputs publish.
Cancellation also leaves outputs unchanged.

## Character layouts

Supported formats are *YMD, *MDY, *DMY, *JUL and their long forms *YYMD, *MDYY,
*DMYY, *LONGJUL. *SYSVAL resolves QDATFMT from one committed catalog snapshot
per call. *CURRENT is input only; *DOS is output only and requires *CURRENT or
*DTS input. *JOB date format is rejected until a job DATFMT attribute is backed.
Input and output do not contain separators.

| Layout | Date bytes | Time bytes | Fraction bytes | Total |
|---|---|---|---|---|
| short, milliseconds | C + six-byte date | HHMMSS | three digits | 16 |
| long, milliseconds | eight-byte date | HHMMSS | three digits | 17 |
| short, microseconds | C + six-byte date | HHMMSS | six digits | 19 |
| long, microseconds | eight-byte date | HHMMSS | six digits | 20 |

C is 0 for 1900–1999 through 9 for 2800–2899. Short dates use YYMMDD, MMDDYY,
DDMMYY or YYDDD plus a trailing space. Long dates use YYYYMMDD, MMDDYYYY,
DDMMYYYY or YYYYDDD plus a trailing space. For example, leap day 2024 at
23:59:58.123 is `1240229235958123` in *YMD and `20240229235958123` in *YYMD.
Julian day 366 is rejected in non-leap years; zero years/days and invalid calendar
dates fail. Long-to-long character conversion supports years 0001–9999, and
short output supports 1900–2899.

With the same resolved format, identical zone selectors and zero information
length, the selected bytes are copied without validating their contents. With
different character formats and no zone processing, date conversion preserves
the time/fraction bytes without validating or decoding them. Short-to-short
conversion also copies the century byte without validation, as IBM specifies.
Time and fraction validation occurs when converting to *DTS, changing zones or
requesting zone information. These rules also apply to invalid UTF-8 time bytes.

## Binary timestamps and DOS output

Standard time is an unsigned big-endian eight-byte value. Its upper 52 bits count
microseconds; the lower 12 bits distinguish otherwise equal clock values and do
not add precision. The midpoint `8000000000000000` represents 2000-01-01 midnight;
`4A2FEC4C82000000` represents 1970-01-01 midnight. The shared NativeTimestamp
codec preserves one-microsecond standard-time units and ignores uniqueness bits
on decode. The adapter applies an eight-microsecond timestamp conversion quantum, inferred
from IBM's round-trip precision note and documented timestamp endpoints.
New timestamps contain zero uniqueness bits; identical-format copies preserve
the original bytes. Millisecond character results round to the nearest millisecond.

Timestamp conversion spans 1928-08-23 12:03:06.314752 through
2071-05-10 11:56:53.685240. Four-digit character-to-*DTS input is restricted to
1928-08-24 through 2071-05-09; outside values fail. Native short-date input may
wrap across the 52-bit timestamp period. Ordinary character conversions do not
apply that wrap rule.

*DOS occupies 11 bytes: hour/minute/second/hundredths/day/month as single-byte
integers at 0–5, signed two-byte year at 6, signed two-byte negative UTC offset
in minutes at 8, and Sunday-zero weekday at 10. All multi-byte fields are
big-endian. The actual offset of a captured/converted instant is retained through
repeated DST hours.

## Zones and information

*SYS uses the host's actual configured time zone; *UTC uses UTC. Different zone
selectors trigger date/time validation even if the host happens to use UTC.
Zone conversion uses the native input range 1928-08-25 through 2071-05-08.
Nonexistent local times during a DST jump fail. Repeated local times use the first
segment by default; argument 11 can select the second segment. Rounding across
a clock transition occurs on the UTC instant before formatting the output zone.

The 116-byte native information structure has BIN(4) fields at 0, 4, 20 and 112;
the remaining bytes are character fields. For *UTC, or same-zone dates outside
the native information range, IBM specifies zero binary fields (including the
length fields) and blank character fields. These cases support any nonnegative
receiver length, copying at most 116 bytes and preserving the remaining tail.
Positive information requests for an in-range *SYS date fail explicitly because
native time-zone description metadata is not backed. Use length zero for host-zone
conversion. *JOB and named *TIMZON selectors also fail explicitly. Host zone names
are not presented as native time-zone objects.

| Failure | ID |
|---|---|
| invalid calendar/range/nonexistent local time | CPF1060 |
| invalid time | CPF1061 |
| invalid century when validation is required | CPF1848 |
| invalid millisecond/microsecond digits | CPF1849 |
| unsupported format or DOS input combination | CPF1850 |
| incomplete parameter groups | CPF3C36 |
| invalid precision, segment, length or unsupported zone metadata | CPF3C3C |
| immutable receiver / invalid error storage | CPF3C90 / CPF3CF1 |

## CL and RPG calls

```cl
DCL &RAW *CHAR LEN(8)
DCL &OUT *CHAR LEN(17)
CALL QSYS/QWCCVTDT PARM('*YYMD' '20000101000000000' '*DTS' &RAW X'00000000')
CALL QSYS/QWCCVTDT PARM('*DTS' &RAW '*YYMD' &OUT X'00000000')
```

Direct RPG can use raw CHAR for both timestamp and character result:

```rpg
**free
dcl-s RAW char(8) ccsid(*hex);
dcl-s OUT char(20) ccsid(*hex);
callp QWCCVTDT('*YYMD':'20000101000000123456':'*DTS':RAW:X'00000000':'*UTC':'*UTC':'':0:'1');
callp QWCCVTDT('*DTS':RAW:'*YYMD':OUT:X'00000000':'*UTC':'*UTC':'':0:'1');
seton LR;
```

DateTimeApiTests/DateTimeApiValidationTests cover published timestamp vectors,
all eight character formats, leap/year/range errors, raw copy rules, DST, DOS,
live QDATFMT reads, partial information and output failure/cancellation.
DateTimeApiIntegrationTests runs compiled CL/direct RPG at CCSIDs 37/1208 and
checks live authority, signing trust and manifests before writes. The display PTY
exercises both callers and WRKAPILS contract details.

## Primary references

[IBM QWCCVTDT](https://www.ibm.com/docs/ssw_ibm_i_74/apis/qwccvtdt.htm),
[IBM Date and Time API publication](https://public.dhe.ibm.com/systems/power/docs/systemi/v6r1/en_US/time.pdf),
and [IBM standard time format](https://www.ibm.com/docs/en/i/7.5.0?topic=interface-machine-introduction).
Job attributes, time-zone objects/metadata and the remaining C14 API/crypto work
are still required; this checkpoint does not close the broad checklist items.
