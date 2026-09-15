# Shared UIM help

`CRTPNLGRP` compiles a source member into a typed `*PNLGRP` object with attribute
`UIM`, original source and versioned compiled help metadata. Source member defaults
to the panel-group name. The iSeriesPC `DSPHELP` command opens a module interactively:

```
CRTPNLGRP PNLGRP(QGPL/CUSTOMERS) SRCFILE(QGPL/QPNLSRC) SRCMBR(CUSTOMERS)
DSPHELP PNLGRP(QGPL/CUSTOMERS) MODULE(GENERAL)
```

The standalone DSPHELP command is an iSeriesPC convenience. UIM's DSPHELP dialog
action is accepted only inside a help link, with the distinct syntax shown below.
Batch/headless callers can compile groups but cannot open an interactive viewer.
Generic typed object commands can copy, rename, move and delete panel groups. Their
module names remain stable when the containing object is renamed.

## UIM subset

```
:PNLGRP.
:HELP NAME=GENERAL.Customer help
:P.Enter a customer name, or read the
:LINK PERFORM='DSPHELP NAME'.field description:ELINK..
:EHELP.
:HELP NAME=NAME.Customer name
:ISCH ROOTS='customer entry'.
:XH3.Name entry
:P.Enter the :HP2.customer name:EHP2. here.
:EHELP.
:EPNLGRP.
```

The syntax follows IBM's
[Application Display Programming help definitions](https://www.ibm.com/docs/zh-tw/ssw_ibm_i_71/rzakc/sc415715.pdf)
and [index-search tags](https://www.ibm.com/docs/en/i/7.4.0?topic=manager-index-search-tags).
The implemented tags are PNLGRP/EPNLGRP, HELP/EHELP, P, XH1–XH3, HP1–HP3 with matching
end tags, ISCH ROOTS, and LINK/ELINK. HELP's first text is its title. Subsequent
paragraphs and headings wrap to the display width. HP1 underlines, HP2 intensifies,
and HP3 combines those attributes. Tags/attributes and module lookup are case
insensitive. A line beginning with `.*`, after indentation, is a source comment.

LINK permits only `PERFORM='DSPHELP module [panel-group]'`. It cannot call a program
or execute a CL command. Local targets must exist when compiled; external groups
resolve through the current job's library list when followed. Each selection
rechecks object authority and signature policy. Missing or denied targets leave
the current help topic available with a visible error. Names are late-bound;
renaming a target does not rewrite other groups' source references.

ISCH accepts one ROOTS attribute containing up to 50 alphanumeric words. The viewer
searches those roots, module names and titles. The supported symbols are `&amp.`,
`&colon.`, `&period.`, `&apos.`, `&quot.`, `&lt.` and `&gt.`. Quoted attribute values
can double their quote character. Limits are 1 MiB, 10,000 source lines, 8,192 tags,
256 modules, 32-character module names and 120-character titles. Unknown tags,
attributes, actions, symbols, duplicate modules/attributes, mismatched nesting,
unclosed quotes and unsupported glyphs fail with source line/column diagnostics.
Imports, embedded modules, variable pools, executable dialogs and other full UIM
features remain explicitly unsupported.

## Display-file help areas

The DDS compiler accepts file-level HELP, HLPTITLE and HLPPNLGRP. H specifications
in column 17 precede the record's fields and contain HLPARA plus HLPPNLGRP:

```
     A                                      HELP
     A                                      HLPTITLE('Customer help')
     A                                      HLPPNLGRP(GENERAL QGPL/CUSTOMERS)
     A          R ENTRY
     A          H                           HLPARA(*FLD NAME)
     A                                      HLPPNLGRP(NAME QGPL/CUSTOMERS)
     A            NAME          20A  B  4  2
```

HLPARA supports `*FLD name`, `*RCD`, `*NONE`, or `top left bottom right`.
The first matching area whose HLPPNLGRP indicator conditions are enabled supplies
the module; file-level HLPPNLGRP supplies the fallback. HELP and HLPTITLE must be
available at the file/record level. Area rectangles and field references are checked
at compile time, while target groups need not yet exist. Subfile help specifications
belong to the control record; message subfiles use their program-message help
context. See IBM's [HLPARA](https://www.ibm.com/docs/en/i/7.5.0?topic=h-hlpara) and
[HLPPNLGRP](https://www.ibm.com/docs/en/i/7.6.0?topic=80-hlppnlgrp-help-panel-group-keyword-display-files)
contracts. Choice-number and constant-identifier areas, document help and display
record help are outside this subset.

## Operator interaction

F1 on a menu opens its help without submitting the current selection. If a command
is typed, F1 opens that command's help without executing it. A custom menu's default
help group and module have the menu's name in its library; command help uses the
command's name through the job library list. If no custom group exists, built-in
menu guidance or current registered-command/parameter metadata is shown. Built-in
help distinguishes unavailable catalog entries and missing parameter metadata.
Persisted command definitions and complete prompt metadata are C07 work.

The same HelpSession viewer handles menus, command entry, DSPHELP and DDS help:

- Page Up/Down scrolls wrapped content.
- Tab/Shift-Tab selects links; Enter follows the selection.
- F12 returns to the previous topic or closes the initial topic.
- F5 opens an index; typing filters, arrows select, and Enter opens a topic.
- F3/Attention closes help and restores the caller's unsent input and cursor.

History is limited to 64 entries. The host rechecks group access on each key;
revocation closes help without modifying the caller's field values. Source and
compiled metadata must agree on load, and the existing object-signature admission
policy applies to `*PNLGRP` content. No new catalog schema is required.

## Evidence

`HelpPanelTests` covers compilation, diagnostics, indexing/link navigation and
wrapping, conditioned DDS areas/fallbacks, compilation from a real source member,
typed rename, headless rejection, source/signature-policy failure, shared menu and
command help, unsent display input and live access revocation.

`tools/pty-display-smoke.py` also drives the actual MenuController and TtySession:
command F1 → restore command entry → RUNPNL → type unsent input → DDS F1 → follow
link → back → index search → return to unchanged input/cursor → close and sign off.
This acceptance runs in CI alongside both supported screen sizes and keyboard tests.
