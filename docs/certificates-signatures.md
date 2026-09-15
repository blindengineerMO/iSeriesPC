# Certificates, keys and signed content

Schema 12 provides an instance certificate inventory, explicit purpose trust, service
bindings, key rotation, object signatures and signed code-package admission. These are
iSeriesPC services and command extensions. They cover the current source-bearing object
model; the later full archive, OS-package updater and IWS codecs build on these checks.

## Inventory and lifecycle

`IpcSystem.Certificates` imports DER/PKCS#12 or PEM certificates, creates a local CA,
issues certificates, renews a leaf with a new RSA key, revokes certificates, manages
service bindings, and reports validity dates and remaining days. Inventory IDs are
SHA-256 fingerprints of the DER certificate; entries are immutable and cannot be
overwritten or silently reactivated. A revoked CA invalidates its descendants.

Supported certificate keys are RSA, 3072–8192 bits. New keys have 3072 bits. Certificate
signatures use RSA PKCS#1 with SHA-256/384/512. Content signatures use RSA-PSS with
SHA-256. Other certificate algorithms fail explicitly during import. PKCS#12 imports
load private keys ephemerally, then store PKCS#8 keys encrypted with profile-independent,
certificate-bound AES-256-GCM envelopes. Public export never contains a private key.
Import each chain certificate separately; importing a PKCS#12 leaf does not implicitly
trust or import every certificate in its container.

Newly imported/generated certificates are not trusted automatically. An administrator
must explicitly trust a CA for each intended purpose: `ObjectSigning`, `Restore`,
`Update`, `ServiceDeployment`, or `TlsServer`. Signing leaves require digital-signature
key usage and the code-signing EKU; TLS leaves require server-auth EKU and issued TLS
certificates require DNS/IP SANs. The runtime uses
[.NET custom-root trust](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509chaintrustmode?view=net-8.0),
with instance intermediates and no host-root fallback or automatic certificate downloads.
This is an offline, explicitly managed trust store: online CRL/OCSP and ACME enrollment
are not implemented here. Apply `RVKCERT` or remove trust when an imported certificate
is revoked. Host CA/ACME adapter automation remains in C17/C18.

Renewal persists a new certificate and key, validates every affected binding, then
atomically retires the old certificate and switches those bindings. Failed validation
leaves previous bindings and certificate state intact, with the candidate unbound.
Retired certificates can verify existing signatures until expiry or revocation; they
cannot create new signatures. Verification requires a currently valid chain. There is
no trusted timestamp service allowing expired certificates to validate historical content.
TLS renewal requires explicit DNS/IP names. Root rollover requires a new CA and explicit
trust changes rather than silently extending a root's lifetime.

Certificate and signing changes require non-adopted `*SECADM`. Reading arbitrary host
files for import additionally requires non-adopted `*SERVICE`. CLI certificate/key/password
imports require private, unlinked owner-only files with bounded sizes. Passwords go in
private files, never CL parameters. The host owner and the trusted in-process API retain
the bootstrap authority documented in security-model.md.

## Commands

These commands are iSeriesPC extensions except the documented `CHKOBJITG` subset.
`CERT` and `CA` values are the fingerprint shown by inventory/create/import commands.

| Command | Supported parameters / behavior |
|---|---|
| `WRKCERT` | List public identity, state, expiry, remaining days and private-key presence |
| `CRTLOCALCA` | `LABEL`, `CN`, optional `DAYS` (1–3650) |
| `CRTLOCCERT` | `LABEL`, `CN`, `CA`, `PURPOSE`, optional `DAYS` (1–825), `DNS` list for TLS |
| `IMPCERT` | `LABEL`, `FILE`, optional private `PWDFILE` for PKCS#12 |
| `TRUSTCERT` | `CERT`, `PURPOSE`, `TRUST(*YES\|*NO)` |
| `RVKCERT` | `CERT`; immediately revoke trust in the certificate |
| `RNWCERT` | `CERT`, `CA`, `PURPOSE`, optional `DAYS`, `DNS` |
| `BINDCERT` | `SERVICE`, `CERT`, `PURPOSE`; reject invalid/untrusted certificates |
| `WRKKEYRING` | Wrapping-key IDs, active key and live ciphertext counts |
| `ROTKEYRING` | Rotate wrapping key and re-encrypt MFA/enrollment/certificate secrets |
| `SGNOBJ` | `OBJ(library/name)`, `OBJTYPE` (default `*PGM`), `CERT` |
| `CHKOBJITG` | Exactly one of `USRPRF(name\|prefix*\|*ALL)` or `OBJ(path\|*SYSTEM)`; `CHKSIG(*SIGNED\|*ALL)` |
| `RSTSGNOBJ` | `FILE`, `SIGFILE`; restore a verified source-object package into existing libraries |
| `APYSGNUPD` | `FILE`, `SIGFILE`; atomically install/replace verified source objects |
| `DPLYSGNSRV` | `FILE`, `SIGFILE`; publish a verified single-program service binding |

An exact integrity path is `/QSYS.LIB/QGPL.LIB/PROGRAM.PGM`. `OBJ(*SYSTEM)` requires
`CHKSIG(*ALL)`. Other IBM parameters, path wildcard scans, LIC/domain verification and
OUTFILE formats are rejected, not simulated. Checks require `*AUDIT`, matching the
[IBM command's authority requirement](https://www.ibm.com/docs/en/i/7.6.0?topic=c-check-object-integrity).
Results are retained in `sys_integrity_results` and shown as `VALID`, `UNSIGNED`, `NOSIG`,
`ALTERED`, `UNTRUSTED`, or `NOTCHECKED`; signature violations produce an error outcome.

## Object signature boundary

The v1 object codec signs identity/type, owner, creation time, description, CCSID,
attribute/format, public authority, source and ordinally sorted extended attributes.
It excludes only the signature itself and the operational last-change timestamp.
Adopted-authority flags are covered. Menu signing also covers its separately stored
title and ordered option records. Supported types are `*PGM`, `*MODULE`, `*SRVPGM`,
`*CMD`, `*PNLGRP`, `*MENU`, `*DOC` and `*QMQRY`. Types with other domain payloads,
including physical files, report `NOTCHECKED`; a descriptor-only signature is never
reported as full payload verification for them. New domain codecs must extend this
boundary when those services are implemented.

CL/RPG validate the exact descriptor snapshot whose source is compiled, then use that
same snapshot for adopted authority. Signed menus are checked while loading their
transactionally consistent payload. Changes to signed source/metadata/options fail
verification. A catalog policy remembers that an object was signed, so stripping its
signature does not make it executable as unsigned code. Newly authored unsigned code
remains executable in the development model. Deleting an object removes its policy;
normal object-existence authority controls deletion/recreation.

Content signatures bind a purpose, resource identity, SHA-256 content hash and UTC
signing time. The signed message is UTF-8 with zero-byte separators:

```text
iSeriesPC-signed-content-v1\0PURPOSE\0RESOURCE\0UPPERCASE_HEX_SHA256\0UTC_ROUNDTRIP_TIME
```

`ObjectSignature` records algorithm `RSA-PSS-SHA256/v1`, certificate fingerprint,
content hash, Base64 signature and signing time. No transport can reuse an object
signature as an update signature or transplant it to a different resource identity.

## Restore, update and service admission

`SignedDeployments.Export` produces v1 `SignedObjectPackage` JSON from signed code
objects. Sign the exact exported bytes with `ContentTrust.Sign` for the intended
operation. The `.json` signature sidecar is an `ObjectSignature`. Admission owns a copy
of the bytes, validates format/catalog versions, rejects duplicate JSON properties and
object identities, verifies the package's operation-specific signature, and verifies
each code object's separate object-purpose signature before installation.

Packages are bounded to 64 MiB and 1000 objects, with at most 4 MiB UTF-8 source per
object. They use the current exact catalog version. Restore is create-only; updates
can create or replace code objects. Library/create/management/adoption authority still
applies after signature validation. Objects, signature policies, artifact receipts and
service publication commit together or roll back together. Source changes do not gain
authority merely because an outer archive is signed.

Service deployment requires exactly one `*PGM` and an explicit service name.
`ResolveService` rechecks the stored publication signature and the current program's
signature/content. A changed or revoked program cannot be resolved until a valid signed
package is published again. C16's IWS codecs and web dispatch must call this publication
boundary; the complete IWS request/result surface remains open. C13 extends code restores
to complete data archives; C18 extends source updates to signed OS/package installation,
schema-aware rollback and scheduling. These broader checklist items are not complete.

## Wrapping-key recovery and evidence

The private `<database>.keys/` ring is shared by MFA and certificate secrets. Rotation
publishes a new private key before re-encrypting catalog secrets transactionally. Old
keys are retained for historical backups. If re-encryption fails, previous ciphertext
and its keys remain usable; the newly published key can encrypt subsequent values.
Never remove old keys just because the live-reference count is zero. Coordinate the
catalog/key-ring backup under C13; a database-only backup cannot recover private keys.

`CertificateTrustTests`, `CertificateKeyStorageTests` and `SignedDeploymentTests` cover
purpose trust, live revocation, expiry, renewal/binding rollback, import, encrypted-key
restart/rotation, failed rewrap, altered/stripped signatures, menu payload integrity,
package rollback and service republishing. OpenSSL independently verifies the generated
certificate chain and RSA-PSS signature, and rejects modified signed bytes.
