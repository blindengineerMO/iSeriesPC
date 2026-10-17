# Software cryptography and job-owned keys

The C14 cryptography subset provides .NET-backed hash, HMAC, AES encryption and
decryption, plus creation/destruction of native job-owned key contexts. These six
QSYS IPCAPI programs use IBM's OPM names. Corresponding long `Qc3*` ILE procedure
names identify the IBM references; general ILE service-program binding remains C09.
All six retain ordinary program lookup, live use authority, signature and manifest
checks. `WRKAPILS API(QC3*)` lists their current availability and contracts.

## Callable contracts

| Program | IBM procedure | Arguments in order | Count |
|---|---|---|---|
| QC3CALHA | Qc3CalculateHash | data, length, data format, algorithm, algorithm format, provider, device, writable hash, error | 9 |
| QC3CALHM | Qc3CalculateHMAC | data, length, data format, algorithm, algorithm format, key, key format, provider, device, writable HMAC, error | 11 |
| QC3ENCDT | Qc3EncryptData | clear data, length, data format, algorithm, algorithm format, key, key format, provider, device, writable ciphertext, area length, writable returned length, error | 13 |
| QC3DECDT | Qc3DecryptData | ciphertext, length, algorithm, algorithm format, key, key format, provider, device, writable clear data, area length, writable returned length, error | 12 |
| QC3CRTKX | Qc3CreateKeyContext | key, length, key format, key type, key form, key-encrypting key, key-encrypting algorithm, writable token, error | 9 |
| QC3DESKX | Qc3DestroyKeyContext | token, error | 2 |

Lengths, algorithms, key types and returned lengths are signed big-endian BIN(4).
Format names are CHAR(8), provider/key-format/key-form controls CHAR(1), and device
names CHAR(10), all in the job CCSID. Data, digests, keys, IVs and eight-byte tokens
are opaque bytes. A CHAR literal such as `abc` hashes its actual job-CCSID bytes;
use `X'616263'` to select ASCII bytes independently of the CCSID.

DATA0100 selects contiguous input. Only the requested prefix participates; length
zero permits empty data. Software provider `1` and automatic provider `0` select
the same software implementation, with a blank/null device. Data is bounded to
1 MiB. Output must hold the whole result, including padding, within the program
argument size limit; no partial digest or clear/ciphertext is published.

The common error structure uses BIN(4) bytes provided/available and a seven-byte
exception ID. Bytes provided zero raises the failure. Nonzero storage requires
at least eight bytes. All output normalization and alias checks happen before
publication. Unselected tails remain unchanged. Invalid receivers, unsupported
formats, short output, failed padding removal and cancellation preserve other
outputs and their returned lengths. Typed CL/RPG BIN(4) length receivers work as
well as raw character storage.

## Hash and HMAC descriptors

ALGD0500 is a single BIN(4) algorithm ID:

| ID / key type | Algorithm | Digest bytes / minimum HMAC key bytes |
|---|---|---|
| 1 | MD5 | 16 |
| 2 | SHA-1 | 20 |
| 3 | SHA-256 | 32 |
| 4 | SHA-384 | 48 |
| 5 | SHA-512 | 64 |

HMAC keys can be longer than the hash block, up to 65,536 bytes. Key lengths
below the native minimum fail. Data is not text-converted. Hash/HMAC processing
uses .NET IncrementalHash and clears owned temporary key arrays after use.

KEYD0200 describes a supplied key: BIN(4) type at 0, BIN(4) key-string length
at 4, CHAR(1) format `0` at 8, binary-zero reserved bytes at 9–11, then raw key
bytes at 12. KEYD0100 is an opaque CHAR(8) context token for a compatible key.

## AES descriptor and padding

ALGD0200 occupies 52 bytes:

| Offset | Field | Supported value |
|---|---|---|
| 0 | BIN(4) cipher | 22: AES |
| 4 | BIN(4) block length | 16 bytes |
| 8 | CHAR(1) mode | `0` ECB, `1` CBC, `7` CTR |
| 9 | CHAR(1) pad option | operation-specific, below |
| 10 | raw pad character | used by encrypt option 1; binary zero for decrypt |
| 11 | reserved | binary zero |
| 12 | BIN(4) MAC length | zero |
| 16 | BIN(4) effective key size | zero |
| 20 | CHAR(32) IV/counter | first 16 bytes used; ECB requires all 32 zero |

AES keys use type 22 and exactly 16/24/32 bytes. CBC/CTR use the caller's IV or
counter. CTR increments the full 128-bit counter in big-endian order, including
carry, and accepts partial final blocks. ECB/CBC without padding require an
integral number of blocks. The IV is not changed in the caller's descriptor.

Encryption option 0 adds no padding. Option 1 fills with the supplied byte and
puts the binary padding count in the last byte. Option 2 fills all padding bytes
with that count, corresponding to PKCS padding. Padding always adds a block when
input is already aligned, including empty input. Decryption has its own control:
0 retains padding; 1 removes the final binary pad count. Its pad-character field
must be binary zero. Invalid/absent pad counts produce no partial clear output.

## Key-context lifecycle and protection

QC3CRTKX supports binary key format `0`, clear input form `0`, the five HMAC key
types and AES type 22. The key-encrypting key and algorithm arguments must be
blank/null. The native API accepts the caller's supplied key; the service encrypts
it for storage using the existing private per-catalog AES-GCM wrapping-key ring.
The authenticated purpose binds job number, token and key type. The catalog
contains ciphertext, never a plaintext key column. There is no context-key export
operation. Disk wrapping keys retain the existing owner-only directory/file rules.

Schema 29 adds encrypted context rows, unique random eight-byte tokens, job
ownership, an index and active-job/end-of-job triggers. Creating a context prepares
the token/error receivers before insertion and checks cancellation before commit.
A rejected token receiver or failed transaction leaves no context. Destroying
prepares error output before deletion. Contexts survive program returns, activation
groups and ordinary catalog opens. Calls through another connection/host can use
the same active job's token, with the existing shared catalog/key ring.

Native caller access is strictly scoped to the executing job. Another job cannot
use or destroy a token, including another job of the same profile or a JOBCTL
caller. Missing/foreign/destroyed tokens return CPF9DF5; invalid all-zero tokens
return CPF9DF4. Profile revocation, inactive jobs and cancellation retain the
existing job-environment authorization/liveness checks. Contexts are limited to
256 per job and release on successful completion, abnormal end and exclusive
server crash recovery. Cleanup commits with the job transition; a failed cleanup
rolls back the transition. Queued jobs cannot create contexts.

Existing wrapping-key inventory and rotation now include live crypto contexts.
Rotation rewraps their encrypted values transactionally while preserving tokens;
old key files remain available for historical ciphertext and failed-rotation
recovery. The catalog backup alone does not contain the wrapping-key ring. Job
contexts are temporary runtime state; persistent keystore records remain separate
compatibility work.

## Errors and remaining contracts

Native errors include CPF9DD0/1 for unsupported clear/input data formats,
CPF9DD2/3 for algorithm/key formats, CPF9DD4/5 for input lengths, CPF9DD6 for output
space, CPF9DDD for key lengths, CPF9DE7/8/9 for key type/form/format, CPF9DE4 for
padding, CPF9DEC for provider, CPF9DF8 for device, CPF9DEE for reserved bytes,
CPF9DF7 for incompatible keys and CPF9DC3 for failed decrypt/unwrapping. Runtime
resource limits report IPC0126. Other shared receiver/count failures use the
standard API error behavior.

The finite subset uses single-call operations: no DATA0200 pointers, ALGD0100
streaming algorithm contexts, hardware CSPs, external key managers, keystore-label
descriptors, BER/PEM/password-derived/wrapped inputs, RSA or additional ciphers and
hash algorithms are claimed. They reject explicitly. Broader native crypto/API
compatibility and general ILE binding remain open in their owning requirements.

## Examples and acceptance

Compiled CL hash:

```cl
DCL &HASH *CHAR LEN(32)
CALL QSYS/QC3CALHA PARM(X'616263' (3 (*INT 4)) 'DATA0100' (3 (*INT 4)) 'ALGD0500' '1' '' &HASH X'00000000')
```

Direct RPG uses raw output storage:

```rpg
**free
dcl-s HASH char(32) ccsid(*hex);
callp QC3CALHA(X'616263':3:'DATA0100':3:'ALGD0500':'1':'':HASH:X'00000000');
seton LR;
```

The full six-program CL/RPG acceptance programs are in
[CryptoProgramSources.cs](../tests/Shared/CryptoProgramSources.cs). They create an
AES context, encrypt/decrypt a known block, destroy it, create an HMAC context,
verify its digest and destroy it. They run at CCSIDs 37/1208 and in the display PTY.
Writable `%SUBST` constructs the RPG descriptor without decoding raw bytes.

| Requirement | Evidence |
|---|---|
| Hash/HMAC known answers and raw bytes | CryptoApiTests: published five-hash vectors; RFC 2202/4231 HMAC vectors, including keys longer than a block; two CCSIDs and unchanged tails |
| AES known answers and returned lengths | CryptoApiTests: NIST SP 800-38A ECB/CBC/CTR vectors for all three key sizes, four-block counter carry, both directions, padding and partial CTR blocks |
| Invalid input and atomic output | CryptoApiValidationTests: key types/lengths, formats, provider/device, reserved bytes, short/aliased/rejected outputs, invalid padding, cancellation and per-job limits |
| Encrypted keys, ownership and rotation | CryptoContextTests: authenticated ciphertext, same-job reopen, JOBCTL/foreign denial, stale keys, failed create/cleanup/rewrap, wrapping inventory/rotation |
| Migration and end/recovery | CryptoContextTests, MigrationTests: schema-28 preservation, failed upgrade rollback, cancellation, completion and actual exclusive server readiness |
| Real callers and trust | CryptoApiIntegrationTests, SessionServerTests, display PTY: six native adapters, compiled CL/direct RPG, signing/manifests/authority, concurrent socket jobs, cross-call use and signoff cleanup |

## Primary references

[IBM crypto API publication](https://public.dhe.ibm.com/systems/power/docs/systemi/v5r4/en_US/crypt.pdf),
[hash](https://www.ibm.com/docs/ssw_ibm_i_74/apis/qc3calha.htm),
[HMAC](https://www.ibm.com/docs/ssw_ibm_i_74/apis/qc3calhm.htm),
[encrypt](https://www.ibm.com/docs/ssw_ibm_i_74/apis/qc3encdt.htm),
[decrypt](https://www.ibm.com/docs/ssw_ibm_i_74/apis/qc3decdt.htm),
[create key context](https://www.ibm.com/docs/ssw_ibm_i_74/apis/qc3crtkx.htm),
[destroy key context](https://www.ibm.com/docs/ssw_ibm_i_74/apis/qc3deskx.htm),
[NIST SP 800-38A vectors](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38a.pdf),
[RFC 4231](https://www.rfc-editor.org/rfc/rfc4231.html) and
[RFC 2202](https://www.rfc-editor.org/rfc/rfc2202.html).
