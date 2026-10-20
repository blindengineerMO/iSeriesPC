# Program interoperability

CL and RPG programs share the job-aware CALL runtime and its authority, signing,
activation and cleanup policies. Native RPG procedures borrow typed caller storage
through bounded synchronous argument scopes. See [program signatures](rpg-program-signatures.md),
[aggregate parameters](rpg-aggregate-parameters.md), [return values](rpg-return-values.md)
and [null-map parameters](rpg-null-parameters.md) for the supported layouts and modes.

[Static RPG module binding](rpg-module-binding.md) adds CRTRPGMOD, CRTPGM and DSPMOD.
Captured modules retain separate globals and exported procedure interfaces within
one program activation; compile-time source and descriptions are not live call
dependencies. File authority and resources remain live runtime concerns.

[Service programs](rpg-service-definitions.md) add CRTSRVPGM/DSPSRVPGM,
current/previous export levels and dynamic BNDSRVPGM references from CRTPGM and
CRTBNDRPG. Services share globals and declared file cursors within their activation
group. Signature/ordinal/interface checks cover the immediate dependency closure
before main execution; deferred procedure imports are checked at first use.
Authority and signing remain live. Service-owned state survives caller LR/failure/reclaim and
closes at service-group reclaim or job end.

[System APIs](api.md) use the native buffer adapters in the curated registry.
Registered Linux processes use pinned executable/dependency manifests and bounded
JSON streams; [protocol 3](rpg-process-null-maps.md) carries nullable/value/readonly
sidebands alongside scalar/buffer arguments. Protocols 1/2 reject unsupported maps.

[Binding directories and service dependencies](rpg-binding-directories.md) add
ordered provider selection and graph validation before main execution. Parent
and child services retain their own activation-owned state, with cycle support
and weak cached bindings across reclaim.

[Source references and updates](rpg-program-updates.md) add module BNDDIR controls,
UPDPGM/UPDSRVPGM replacement and rebinding. Atomic publication preserves concurrent
edits, while existing activations keep their previous code and native interface.

[Deferred procedure activation](rpg-deferred-activation.md) preserves binding
modes and validates immediate dependencies when the deferred provider is called.
[Activation/library update policies](rpg-update-policies.md) add named group
changes, ALWLIBUPD and fixed/dynamic provider relocation with retained connections.
[Module/service data binding](rpg-data-binding.md) shares native bytes and null
maps, preserving exporter initialization through LR and abnormal returns.
[Native procedure pointers](rpg-procedure-pointers.md) preserve callback identity,
origin storage and activation ownership until reclaim.
[Data pointers and based storage](rpg-data-pointers.md) add native storage aliases
and heap management with activation/frame lifetime checks. These contracts remain partial. Broader foreign-language ABI and calling conventions,
wider pointer ABIs, process aliases, complete SQL/ILE integration and independent
native golden/scale acceptance remain open. See the [compatibility matrix](compatibility-matrix.md)
for requirement status and owning tests.
