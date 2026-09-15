# Object command completion

C07's command-category item remains open. The entries below record completed
operations while the remaining categories and service dependencies are implemented.

`DLTLIB LIB(name)` deletes a library, its objects, member tables, and dependency
records in one transaction. It checks library use/existence authority and every
contained object's existence authority before writing. Registered external
references, active work, shipped system libraries, persistent allocations, and
unresolvable dependency cycles block the operation. The command accepts one exact
library and at most 4000 objects; unsupported generic/multiple-library forms fail.
The work-with-libraries delete option displays a confirmation for deleting the
library and contents, then refreshes the list.

IBM documents [library deletion](https://www.ibm.com/docs/en/i/7.4.0?topic=libraries-deleting-clearing)
as potentially partial when a contained object is unauthorized. iSeriesPC uses
atomic rollback for this operation, consistent with its catalog contract. An error
preserves every object and member; do not rely on IBM's partial-deletion behavior.
Four service/command tests cover success, dependencies, denied children, and locks;
real PTY acceptance creates a source file inside a library and deletes it through
the confirmed work-screen action.

`CRTDTADCT DTADCT(name) TEXT('description') AUT(*LIBCRTAUT)` creates an empty catalog
`*DTADCT` whose name and library match. The library must exist. Text is limited to
50 printable characters. AUT accepts *USE, *CHANGE, *ALL, *EXCLUDE, or an existing
authorization list. *LIBCRTAUT uses the library's recorded create authority, with
*CHANGE as this build's default. Missing lists fail before creation; list attachment
is atomic and later membership changes take effect immediately. Three tests cover
command creation/listing/deletion, live list membership, failed attachment, library
creation authority, and invalid text.

See IBM's [CRTDTADCT reference](https://www.ibm.com/docs/en/i/7.5.0?topic=c-create-data-dictionary)
for the native operation. This command creates the dictionary container; an IDDU
editor and a native IBM dictionary binary format are not supplied by this command.
Logical files, remaining object commands, and the other required categories remain
tracked in PLAN.md and the compatibility matrix.
