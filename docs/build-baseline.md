# Reproducible build and runtime policy

The build SDK is pinned to 8.0.425 by `global.json` with SDK roll-forward disabled.
Install that SDK from [Microsoft's .NET 8 distribution](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).
The root solution is `iSeriesPC.sln`: SLNX requires newer tooling than the original
SDK 8 build contract ([Microsoft's SLNX announcement](https://devblogs.microsoft.com/dotnet/introducing-slnx-support-dotnet-cli/)).
CI reads the same global.json. Per-project NuGet lock files are committed; CI uses
locked restore to reject unexpected dependency changes.

```sh
dotnet --version
dotnet restore iSeriesPC.sln --locked-mode
dotnet build iSeriesPC.sln --no-restore -c Release
dotnet test iSeriesPC.sln --no-build -c Release --logger 'trx;LogFileName=results.trx'
python3 tools/compatibility-report.py --check
```

SDK updates are intentional changes to global.json, validated with clean locked
restore/build/test and the supported Ubuntu target before CI moves. Dependency
updates explicitly regenerate/review lock files. Production targets net8.0 and
must install the supported .NET 8 runtime patch; the existing LatestMajor runtime
roll-forward is for developer compatibility and is not production qualification.
Microsoft's [release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json)
lists .NET 8 end of support as 2026-11-10. Requalifying a supported runtime before
that date is a release requirement if delivery/support extends beyond it.

On the current development workstation, the verified SDK was installed without
changing system packages at `/home/matthewp/.local/share/iseriespc-dotnet/dotnet`.
Invoke that executable for the commands above if the system `dotnet` still points
to SDK 10; an SDK 10-only installation intentionally fails the SDK 8 pin.

## Recorded observations — 2026-09-14

The managed SQLite provider is pinned to `SQLitePCLRaw.provider.e_sqlite3` 3.0.5
with its compatible core dependency, retaining the native SQLite library from
the Microsoft.Data.Sqlite bundle. The 2.1.x provider can throw a range exception
when native prepare returns a schema-lock error with no SQL tail. The
[3.0.5 provider source](https://github.com/ericsink/SQLitePCL.raw/blob/v3.0.5/src/SQLitePCLRaw.provider.e_sqlite3/Generated/provider_e_sqlite3_funcptrs_notwin.cs)
validates that pointer before slicing. SqlitePrepareTests exercises a held schema
write and successful preparation after commit; the complete 964-test checkpoint
passed with this provider. Physical member definition reads also occur inside the
creation transaction, so concurrent schema/member changes share one boundary.

- Before edits: SDK 10.0.112 / runtime 10.0.12 on Ubuntu 26.04, Release build and
  223 tests passed, 0 failed, 0 skipped; local `baseline.trx` retains results.
- The former SDK 8 CI configuration and SLNX solution were inconsistent; the local
  SDK 10 result did not establish compatibility with the intended CI toolchain.
- SDK 8.0.425 / runtime 8.0.31 on Ubuntu 26.04: restore and Release build passed
  with zero warnings/errors; all 223 baseline tests passed, 0 failed, 0 skipped
  (`sdk8-baseline.trx`). Ubuntu 24.04 host integration remains a separate release
  gate; a local test run does not substitute for it.

The test suite currently combines assembly-level tests in `Ipc.Core.Tests`.
Historical README test counts were cumulative snapshots, not per-package acceptance.
