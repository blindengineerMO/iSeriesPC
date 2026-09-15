# Release scope and compatibility decisions

Recorded 2026-09-14 for C01. “Required” describes the release contract, not current
implementation status. The compatibility matrix and PLAN.md checklist track delivery.

## Client database connectivity — G11: included

The user explicitly selected ODBC/JDBC connectivity with documented drivers and
reporting-client tests. Implement a PostgreSQL protocol 3.0 reporting gateway over
the SQLite object/member catalog. Clients use the existing
[psqlODBC](https://odbc.postgresql.org/) and
[pgJDBC](https://jdbc.postgresql.org/documentation/) drivers. This is an application
gateway; it does not require a Db2 server or replace SQLite with PostgreSQL.

The required initial contract is read-only reporting: TLS, authenticated profiles,
object/record read authority, startup/cancel/terminate, simple and extended query
flows, parameter binding, metadata discovery, stable catalog/schema/table/column
names, precise decimal/null/date/CCSID mappings, bounded result streaming, read
transactions, and isolation from other sessions. Writes and PostgreSQL extensions
must return explicit unsupported-operation errors. SQL is translated through the
same authorized database service used by the app; clients cannot query internal
credential or authority tables. Do not advertise general PostgreSQL compatibility.

C08/C16 acceptance requires pinned driver versions and executable ODBC/JDBC tests
for connection, metadata, parameterized queries, joins/aggregates, transactions,
cancellation, decimals/nulls/Unicode, rejected writes, and denied objects. Also
retain reporting workflow results for LibreOffice Base through ODBC and DBeaver
through JDBC; Excel Power Query through ODBC is a release validation target when
a Windows/Excel test host is available. Missing client evidence stays open.

## IBM MQ — G13: included as an external adapter

The user explicitly selected integration with an existing IBM MQ queue manager.
Use IBM's managed .NET client (the package/library version must be pinned and tested
with the target queue manager). IBM documents its .NET client distribution in the
[XMS .NET installation guide](https://www.ibm.com/docs/en/ibm-mq/9.4.x?topic=applications-installing-mq-classes-xms-net).
Queue-manager provisioning, licensing, and IBM MQ server emulation are outside this
adapter's responsibilities. Native MSGQ/DTAQ remain independent app services.

C11/C17 must implement named connection profiles (host/port/channel/queue manager),
TLS trust and credentials from protected storage, queue allowlists, connection health,
put/get with wait/cancel, message/correlation IDs, persistence, encoding, syncpoint
commit/backout, error mapping, and bounded reconnect/backoff. Never silently retry
an uncertain put as if delivery were known to have failed. Document distributed
transaction limits: no atomic SQLite+MQ transaction is promised; use an outbox and
idempotency contract for workflows spanning the two systems.

Acceptance requires client doubles plus a disposable or designated queue manager:
put/get round-trip, correlation selection, commit/backout, expired credentials,
denied queue/channel, invalid TLS, timeout, disconnect, restart, and recovery.
CL/RPG/API entry points must carry the caller's authority and audit identity.

## Existing fidelity boundaries

The original plan's “full parity” is a target for explicitly catalogued behavior,
not an assertion that every IBM i command, API, language feature, or binary format
is implemented. Required features retain their original scope until an explicit
decision changes it. No unfinished feature is excluded merely to close a checkbox.

- CL: documented interpreter and command subset with typed arguments and explicit
  errors; no claim of universal command or compiler compatibility.
- RPG/ILE: interpreted fixed/free source subset, not compiled MI/RPG machine code.
  Source preprocessing and embedded SQL remain required. RPG slash directives in
  CL, if supported, must be labeled an iSeriesPC extension.
- SQL: SQLite-backed PF/LF/member model and a documented SQL translation subset;
  full Db2 optimizer/stored-procedure parity remains excluded.
- CCSID: 037 plus enumerated interop pages; every IBM CCSID is not required.
- SAVF: only independently proven native formats may be called compatible. The
  internal lossless `.isav` archive is a separate format. Native SAVF evidence is
  still required; a missing fixture is not an exclusion.
- SOAP: the optional inbound SOAP server phase in WP16 is included in the release;
  outbound REST/SOAP client generation added by G9 remains required.
- Host services: explicit Linux adapters, not byte-identical IBM implementations.
- Instances: Docker lifecycle and isolation are required by G12; PowerVM/hardware
  emulation, SNA/APPC/APPN wiring, and physical device I/O remain excluded.

The finite required CL/RPG/DDS/SQL/CCSID/SAVF/SOAP and host-service contracts are in
[compatibility-contracts.md](compatibility-contracts.md). Delivery remains tracked
separately: a scope contract is not an implementation acceptance result.
