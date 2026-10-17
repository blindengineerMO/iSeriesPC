# System values and QWCRSVAL

WRKSYSVAL lists the runtime's registered system values, with exact/generic SYSVAL
filters and option 2 to prompt CHGSYSVAL. Changes require SECADM; QSECURITY and
QALWUSRDMN also require ALLOBJ. Command authority remains checked. Supported changes
are QSYSNAME, QCCSID, QSECURITY, QALWUSRDMN, QPWDLVL, QPWDMINLEN, QPWDEXPITV,
QMAXSIGN, QPWDRQDDGT, QPWDRQDRPT, QSYSLIBL and QUSRLIBL. Other changes fail
explicitly. Password, domain and library validation use the existing services.

SystemValueStore.ReadSnapshot reads committed catalog values with one SQLite
reader. WRKSYSVAL and native API retrieval observe changes from other live hosts;
returned registries are detached. Host date/time is captured together for each
snapshot, rather than returning persisted startup dates. QDATE/QTIME remain
read-only; no host clock adjustment is implemented. System values are instance
properties. A job keeps its assigned CCSID and library list when defaults change.
This does not promise that every process's configuration cache is reloaded.

QSYSLIBL holds at most 15 names; the default QUSRLIBL holds at most 25. A running
job's user library list can hold 250. Older catalogs may contain larger default
lists accepted by previous builds; native retrieval reports CPF3C19 until an
administrator corrects them, preserving all stored names. No upgrade truncates
data. IBM distinguishes these [system-value limits](https://www.ibm.com/docs/en/i/7.5.0?topic=overview-library-lists-system-values-user-library-list)
from [running-job limits](https://www.ibm.com/support/pages/node/640761).

## Native retrieval

QSYS/QWCRSVAL is a version-1 IPCAPI program. CALL, compiled CL and direct RPG
use the same adapter, ordinary program authority/signature checks, job CCSID and
cancellation. WRKAPILS API(QWCRSVAL) shows its supported contract. It takes exactly
five parameters:

| Position | Contract |
|---|---|
| 1 | Writable CHAR receiver |
| 2 | BIN(4) receiver length, at least 28 and within actual storage |
| 3 | BIN(4) name count, 1–4096 |
| 4 | Array of uppercase CHAR(10) names, in requested order |
| 5 | Standard bytes-provided/available API error receiver |

BIN(4) fields are signed big-endian integers. Names, type/status bytes and character
values use the caller's job CCSID. Numeric system values can have different native
types: QSECURITY is CHAR(2), QPWDLVL is BIN(4). Internal registry types do not
determine the wire layout. The published name list is
SystemValueApi.SupportedNames; all 27 names below have backed semantics.

| Names | Native data |
|---|---|
| QCCSID, QPWDLVL, QPWDMINLEN, QPWDMAXLEN | BIN(4); maximum password length derives from the stored password level |
| QSECURITY | CHAR(2) |
| QPWDRQDDGT, QPWDLMTREP | CHAR(1), 0/1; QPWDLMTREP maps the runtime's QPWDRQDRPT repeat policy |
| QPWDEXPITV, QMAXSIGN | CHAR(6), blank padded; zero expiration maps to *NOMAX |
| QDATFMT, QDATSEP, QTIMSEP, QPRTTXT | CHAR(3), CHAR(1), CHAR(1), CHAR(30) |
| QSYSLIBL, QUSRLIBL, QALWUSRDMN | 15, 25, 50 CHAR(10) slots, including blank unused slots |
| QDATE, QCENTURY, QYEAR | CHAR(7) CYYMMDD, CHAR(1) century relative to 1900, CHAR(2) year |
| QMONTH, QDAY, QDAYOFWEEK | CHAR(2), CHAR(3), CHAR(4); Julian format returns blank month and a three-digit day of year |
| QHOUR, QMINUTE, QSECOND | CHAR(2) components of the same local timestamp |
| QTIME, QDATETIME | CHAR(9) HHMMSSmmm; CHAR(20) YYYYMMDDHHMMSSffffff |

The receiver starts with a BIN(4) returned count, then one BIN(4) offset per
returned value. Each offset addresses a four-byte-aligned information table:

| Table-relative offset | Field |
|---|---|
| 0 | Name, CHAR(10) |
| 10 | Type, CHAR(1): C or B |
| 11 | Information status, CHAR(1): blank for the implemented unlocked values |
| 12 | Data length, BIN(4) |
| 16 | Data |

Only a complete prefix of entries that fits is returned; an entry is never split
or skipped. The offset directory covers the actual returned count. With no room
for the first table, only the count zero is written. There is no bytes-returned/
available header in this API. Alignment gaps are zeroed; bytes after the written
prefix retain their previous contents. Repeated names return repeated entries.
All names are validated even when later entries would not fit. Receiver and error
writes are prepared before either output changes; aliased error storage is rejected.

CPF1860 reports invalid or unimplemented names, CPF1861 invalid receiver length,
and CPF1862 invalid count. Four-byte zero error input propagates the exception;
writable standard error storage returns its ID. Unsupported layouts, conversion
or receiver normalization failures preserve information storage. Full exception
substitution data remains open. Lockable values, audit/network-protected values,
native *NOTAVL/-1 masking and the wider IBM system-value catalog are not implemented;
requesting them fails rather than returning invented information. Runtime extensions
such as QSYSNAME are available through WRKSYSVAL, not this native name list.

```rpgle
**free
dcl-s INFO char(128) ccsid(*hex);
dcl-s SIZE int(10) inz(128);
callp QWCRSVAL(INFO:SIZE:1:'QCCSID':X'00000000');
if %subst(INFO:25:4) = X'00000025';
  sndpgmmsg 'SYSTEM CCSID IS 37';
endif;
seton LR;
```

SystemValueApiTests covers independent bytes in CCSIDs 37/1208, ordered/aligned
mixed types, complete-entry truncation, unchanged tails, clock capture/precision,
Julian dates, native policy arrays, bad names/counts/lengths, aliases and cancellation.
SystemValueApiIntegrationTests covers CL MONMSG, direct and repeated RPG calls,
live authority/signing/manifests, detached/shared/reopened snapshots, native default
list bounds and an independent writer process that cannot tear a multi-value read.
SessionServerTests verifies native reads and authorized/denied CHGSYSVAL through
independent socket jobs. The display PTY compiles and calls CL/RPG callers and
opens WRKAPILS details. Broader C14/C18 acceptance remains open.

Primary references: IBM's [QWCRSVAL](https://www.ibm.com/docs/en/i/7.5.0?topic=ssw_ibm_i_75%2Fapis%2Fqwcrsval.html),
[system-value parameters](https://www.ibm.com/docs/en/i/7.5?topic=values-system-value-parameters),
and [Work Management API layouts](https://public.dhe.ibm.com/systems/power/docs/systemi/v5r4/en_US/wm.pdf).
