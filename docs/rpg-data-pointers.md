# RPG data pointers and based storage

`POINTER`, `%ADDR`, `BASED`, heap allocation and pointer arithmetic connect native
RPG variables to byte-addressed storage. Data pointers are distinct from
[procedure pointers](rpg-procedure-pointers.md). Both occupy 16 bytes and default
to `*NULL`; their values cannot be exchanged by assignment, including when both
variables happen to be null.

```rpgle
**free
 dcl-s address pointer;
 dcl-s number int(10) based(address);
 address = %alloc(4);
 number = 123;
 address = %realloc(address:8);
 sndpgmmsg %char(number);
 dealloc(n) address;
 return;
```

## Definitions and addresses

Standalone native fields, arrays and structures can specify `BASED(pointer)`.
Each access follows the pointer's current value. Basing pointers can be local
parameters, qualified pointer fields or selected array elements. A missing simple
pointer name is implicitly declared; implicit basing pointers inside
subprocedures are local. Circular definitions and function expressions reject.

Traditional D specs use data type `*` without PROCPTR, omitted or 16-byte length
and no decimal positions. Pointer fields and structures containing them align to
16 bytes; based pointer storage also checks alignment when accessed. Explicit
positions and lengths must meet the native alignment contract. `LIKE`, `LIKEDS`,
array and multiple-occurrence layouts retain their usual types and strides.
Based declarations do not own or initialize their storage; INZ, CTDATA, STATIC,
TEMPLATE and IMPORT/EXPORT storage ownership reject on those definitions.
Independent null-indicator metadata on based storage is not implemented and is
rejected, including nested definitions.

`%ADDR(variable)` returns its storage address. Native fields, array elements,
structure members and imported database/display fields have addressable storage.
Addresses of imported fields share their actual record variables; writes through
a based view participate in the existing record UPDATE path. Address-taking in
native subprocedures retains the owning module's external-field storage.

`%ADDR(array)` selects its beginning; `%ADDR(array(*))` supplies each element's
address to a pointer array. Qualified structure-array subfields and indexed
subfield arrays use their actual offsets, including noncontiguous strides.
Multiple-occurrence structures use their current occurrence. For a based variable,
`%ADDR` returns its basing pointer plus any selected member/element offset.

Pointer `INZ` accepts `*NULL` or `%ADDR` of non-based storage. Referenced array
indexes must be compile-time literals/constants. Forward storage addresses are
available before initializers run; parameter-address initializers wait for actual
entry binding. Addresses of CONST PI parameters can be compared, but cannot be
saved, returned or assigned for mutation. A CONST pointer parameter can still
modify or deallocate the storage to which its pointer value refers.

`%SIZE`, `%LEN` and `%ELEM` use declared based layouts without dereferencing null
pointers. Supported pointer expressions have a 16-byte size without executing an
allocation merely to discover that size.

## Arithmetic, allocation and errors

Pointer plus/minus a non-float integer moves by bytes. Subtraction within the same
registered storage region returns the byte distance. Fractional/float offsets,
integer overflow, ordinary numeric/text conversions and cross-pointer-type
assignments reject. Equality and inequality preserve storage identity; ordering
between unrelated regions has no portable meaning.

`%ALLOC(length)` allocates heap bytes; `%REALLOC(pointer:length)` preserves the
prefix and replaces an existing allocation after preparation succeeds. Old aliases expire
on successful replacement. Failed preparation preserves the old pointer and
bytes. Storage growth is uninitialized; callers should clear or assign values
before interpreting new bytes. Fixed C-spec `ALLOC` and `REALLOC` use factor 2
for the length and a result pointer; free source uses the built-ins instead.

`DEALLOC pointer` frees a whole allocation. An interior pointer, ordinary variable
address, expired allocation or foreign token fails. `*NULL` deallocation succeeds.
The N extender sets the pointer to null only after success. E and traditional ER
indicators handle allocation/storage-management errors; E and ER cannot coexist.
A CONST pointer can be deallocated without N because its pointer cell is unchanged.

Each allocation is bounded to the single-level RPG maximum of 16,776,704 bytes.
The execution registry retains at most 64 MiB of heap and 4096 live regions.
Reclaim/free releases quota; addresses of many elements share their owning region.
Existing 1 MiB native declaration/argument and activation/frame bounds still apply.
`MONITOR` accepts status 222 for invalid data access, 425 for invalid size/quota,
426 for storage-management failure and 452 for incompatible native CCSIDs.
Bounds and lifetime checks precede storage mutation, including when a later call
argument frees memory whose reference was captured earlier.

## Lifetime and integration

Program and service activations share a job-owned registry. Addresses retain the
owning activation even when another callee takes an address of its parameter.
Ordinary program globals reinitialize in place through LR/abnormal return, so
retained addresses continue to refer to the current cycle's values. Heap and
procedure STATIC storage retain their contents until free/reclaim; automatic and
VALUE-parameter storage expire when the procedure returns. Caller-owned reference
addresses survive the callee when their original storage remains valid.

Service factories can expose private native storage. UPDSRVPGM keeps retained
storage and code until reclaim; expired addresses do not reconnect to replacement
storage. Reclaim/clear/job end invalidates the owner's regions. Memory accesses
recheck live origin authority/signing, retained snapshot trust and original CCSID,
including when the provider binding is deferred and no procedure is called again.
CL can borrow based native references with ordinary writeback.

The session and service runtimes release regions on activation disposal. Direct
hosts can share `RpgDataPointerRegistry` through `RpgHost.DataPointers` and use
`ReleaseProcedureAddresses` to end an activation's pointer resources.

## Remaining scope and references

These addresses are opaque registry tokens with byte offsets, not operating-system
addresses. Cross-region pointer subtraction, teraspace, varying/graphic/UCS-2
address layouts, based null-map aliases, wider external API/foreign-pointer ABIs,
weak/activation-group exports and independent native golden/scale acceptance
remain open. C09 stays partial. Module version 1, RPGBOUND/RPGSERVICE 1–5,
schema 31, DISK 7/external description 2/WORKSTN 5 and command catalog 315 are
unchanged.

The rules follow IBM's [ILE RPG Reference](https://www.ibm.com/docs/en/ssw_ibm_i_76/pdf/sc092508.pdf),
printed pages 324–326 (basing pointers), 462 (BASED), 668–670 (`%ADDR`/`%ALLOC`),
745 (`%REALLOC`), 787 (`ALLOC`), 831–832 (`DEALLOC`) and 943–944 (`REALLOC`).
See also IBM's [%ADDR documentation](https://www.ibm.com/docs/en/i/7.4.0?topic=functions-addr-get-address-variable)
and [memory-management operations](https://www.ibm.com/docs/en/i/7.4.0?topic=operations-memory-management).
Registry limits and guarded lifetimes above describe this emulator's implementation.

Verification (2026-10-19): all 6567 solution tests pass with zero failures/skips
in 11m11s, including 81 new cases (45 language/storage/quota, 18 procedure/
reference/lifetime, 16 saved activation/service/security and two record cases).
The obsolete POINTER rejection case is removed. All 4311 broad and 394
focused checks, locked restore, zero-warning/error Release build and expanded
terminal service-address/alias/reclaim/heap acceptance pass. Native/record cases
cover CCSIDs 37/1208; display acceptance covers 24x80 and 27x132. Generated
inventory, local links and whitespace checks pass.
