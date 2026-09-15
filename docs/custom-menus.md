# Custom menus

Create/edit an SDA source member with `STRSDA`, use F10 for the menu and F6/F7/F11
for options, then save/compile with F5. The option form includes a required special
authority (`*NONE` by default). The setting survives DDS source save/reopen and
`CRTMNU MENU(QGPL/TASKS) SRCFILE(QGPL/SOURCE) SRCMBR(TASKS) REPLACE(*YES)`.
Replacement must be explicitly selected; the catalog checks this inside the write
transaction. Source edits use the revision checking described in screen-design.md.

The alternative JSON workflow imports an entire definition:

```json
{
  "Name": "TASKS",
  "Library": "QGPL",
  "Title": "Operator tasks",
  "Options": [
    {
      "Number": "1",
      "Text": "Work with jobs",
      "Target": "WRKACTJOB",
      "Kind": "Command",
      "RequiredAuthority": "*JOBCTL"
    },
    { "Number": "90", "Text": "Sign off", "Target": "SIGNOFF", "Kind": "SignOff" }
  ]
}
```

Run `CRTMNU MENU(QGPL/TASKS) JSONFILE('/private/tasks.json')`. Edit the JSON and
repeat with `REPLACE(*YES)`. MENU must match the JSON identity. JSONFILE is an
explicit host-file import requiring non-adopted *SECADM and *SERVICE, as well as
normal catalog create/change authority; regular source-member compilation uses
source/object authority. Host files must be regular, unlinked, and at most 64 KiB.
Unknown/duplicate JSON properties, invalid enum names, controls, unsupported
terminal glyphs, duplicate option numbers, excessive lengths, and malformed
identities are rejected before catalog changes. Menu titles and option labels
are at most 60 characters, targets 512, and there are at most 16 options numbered
1–999. Available kinds are Command, Prompt, SubMenu, Exit, and SignOff.

`GO QGPL/TASKS` runs the stored menu. `WRKOBJ OBJ(QGPL/*ALL) OBJTYPE(*MENU)` provides
display and confirmed deletion; `DLTMNU MENU(QGPL/TASKS)` is the application-defined
convenience command for `DLTOBJ OBJ(QGPL/TASKS) OBJTYPE(*MENU)`. Generic catalog
copy/rename/move operations carry the menu's options and restrictions atomically.
Replacement preserves ownership and invalidates the old signature. Signature
policy remains mandatory for previously signed objects, which must be signed again.
Signed code-package imports still exclude the separate *MENU domain payload.

An option may require one named special authority. The check uses the current
profile and group authorities, without adopted program authority. It is repeated
when a prompted option is submitted. Removed/changed options invalidate an open
prompt. Option restrictions govern that menu route; the underlying command still
has its own service authorization, including when entered directly. An allowed
menu option never grants authority to a command, object, profile, or job.

Open menus reload changed definitions without losing unsent command text. Revoking
menu access, deleting the active menu, or invalidating its required signature ends
the terminal session at its next menu input boundary. This clears the stale menu
and runs normal abnormal-disconnect cleanup. Navigation is bounded at 64 levels,
with F12/F3 available to return. New menu code uses the existing catalog schema;
option policy resides in signed object attributes and is covered by menu signatures.

Validation: six `CustomMenuTests`, signature tampering/legacy-upgrade tests, all
shipped menu routes, JSON copy/replace/delete/authority checks, SDA policy round
trip, live permission and prompted-option revocation, and bounded recursion.
The real PTY acceptance imports, runs, replaces, reloads, and deletes a JSON menu.
