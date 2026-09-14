![Header](assets/iSeriesPC_Banner.png)
# iSeriesPC

Emulation of the IBM iSeries/AS-400 experience on x64 Intel Linux, implemented in C# (.NET 8)
over SQLite, with host-level integrations for SSH/SFTP, LDAP, DNS, SMTP, UFW, Samba, CUPS,
Node.js and a Vite admin console.

The SSH login shell is replaced by an iSeries-style menu interface (`AS400Menu`) with a full
5250/ANSI full-screen experience: sign-on, menus, CL command line, prompting (F4), help (F1),
function keys, subfiles and paging.

## Status

Under construction. See [PLAN.md](PLAN.md) for the master task list.

| WP | Component | Status |
|----|-----------|--------|
| WP0 | Foundation | complete |
| WP1 | Ipc.Core domain model | complete (67 tests) |
| WP2 | Ipc.Services host (SQLite, config, logging, events, IpcSystem) | complete (77 tests) |
| WP3 | Security engine | complete |
| WP4 | Job/work-management engine | complete |
| WP5 | Terminal + display engine (ANSI 5250, fields) | complete |
| WP6 | Session host (as400menu) + sign-on | complete |
| WP7 | Menus system (MAIN/MAJOR, GO, *MENU objects) | complete |
| WP8 | CL command system + interpreter (*CMD catalog, CRTCLPGM, CALL, command line) | complete |
| WP9 | DDS compiler + SQLite file store (*FILE objects, members, record codec, CRTSRCPF/ADDSRCPFM/CRTPF/DSPPFM/ADDPFM/CPYF/DLTF) | complete (194 tests) |
| WP10 | RPG compiler + interpreter (fixed + free form, `/free` directives, data structures, expressions + `%` built-ins, file I/O, subroutines, subprocedures P/D + CALLP, `ON-ERROR`/`*INLR`, messaging) | complete (24 tests) |

## Layout

- `src/Ipc.Core` — domain model: objects, libraries, authorities, system values, CCSID, dates, *MENU objects
- `src/Ipc.Services` — job engine, spool engine, security, menu store, IFS, save/restore, messaging, journals
- `src/Ipc.Db` — SQLite catalog, DDS compiler, SQL bridge, SQL tools
- `src/Ipc.Cl` — command catalog, parser, prompt, CL program interpreter
- `src/Ipc.Rpg` — RPG interpreter (fixed + free form)
- `src/Ipc.Dsp` — DSPF compiler + panel/subfile runtime
- `src/Ipc.Terminal` — ANSI 5250 renderer + input editor
- `src/Ipc.Console` — `AS400Menu` SSH login-shell host + sign-on
- `src/Ipc.Api` — system API runtime (QCMDEXC, QMHSNDM, QUS*, ...)
- `src/Ipc.Console` — `AS400Menu` SSH login-shell host + sign-on
- `src/Ipc.Web` — ASP.NET Core REST + IWS-style program services + 5250-over-websocket
- `src/Ipc.WebUi` — Vite SPA admin console
- `src/Ipc.Bridge` — interop/migration CLI
- `src/Ipc.Installer` — provisioning + first-run wizard + systemd

See `docs/*` for architecture, object model, and command/RPG/DDS specifications.

## Build & test

```
dotnet build
dotnet test
```

`.NET 8` is targeted (LTS); development runtimes are supported via `RollForward=LatestMajor`.