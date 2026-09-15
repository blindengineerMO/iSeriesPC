# Security model and release boundaries

The server authenticates a profile before creating a terminal, HTTP or headless
session and owns the resulting job. Batch requests persist the execution profile.
`OperationIdentity` carries that principal, job and immutable program call stack
through service calls. Group membership, profile status and authorities are read
again on each protected operation, so changes affect live sessions. Session cleanup
can complete its own job after profile revocation without restoring general access.

`ServiceAuthorization` guards the SQLite object catalog, member data, menus,
profile and system-value administration, job access, queues, subsystems and durable
audit readers. Library access is checked independently of object access. Object
creation needs library ADD; data reads/writes use their corresponding authority
bits; catalog changes require OBJMGT or OBJEXIST. Other users' jobs require JOBCTL,
profile administration requires SECADM, and audit consumption requires AUDIT.
Native source-file imports require SERVICE from the actual caller, not an adopted
owner. IFS, websocket, bridge and system APIs introduced by later packages must
use these same guarded services and establish server-verified identities.

No operation identity denotes trusted in-process host/bootstrap code. The raw
SQLite connection factory and primitive memory/filesystem descriptor stores are
infrastructure, not public client APIs or a sandbox for arbitrary managed plugins.
Clients cannot supply a principal, group list, adopted owner or job selector to
command execution. New transports must not expose those host capabilities.

## Object authority

Objects are identified by library, name and type. Missing objects remain missing
even for ALLOBJ users. The precedence model checks user ALLOBJ, individual private
authority (including explicit exclusion), ownership, individual AUTL authority,
group authority and finally public authority. A private grant is not augmented by
public or group bits. Applicable group grants combine; a group exclusion suppresses
public fallback but does not suppress another group's grant. AUTL public mode uses
the list's public authority. Existing multiple-list attachments and attachment masks
are retained as an iSeriesPC extension; they are not IBM's single-list attachment model.

Program metadata controls owner adoption and use of prior adopted authority. CL
and RPG calls push frames and restore them on normal return and exception unwinding.
Adoption uses the owner's individual authority, including ALLOBJ, not the owner's
groups or public authority. A program can stop inheritance of earlier frames.
Enabling adoption of another owner's authority requires access to that profile.

QSECURITY 10 skips password verification; 20 verifies passwords but bypasses object
permissions; 30, 40 and 50 enforce the supported object-authority model. These are
application-level compatibility modes. They do not emulate IBM machine-interface
integrity, domain separation or every distinction between native levels 30/40/50.
Administrative special-authority checks remain active at every level.

Reference cases follow IBM's [security levels](https://www.ibm.com/docs/en/i/7.5.0?topic=overview-security-system-values-security-level),
[authorization lists](https://www.ibm.com/docs/en/i/7.5.0?topic=concepts-authorization-lists),
[owner authority checks](https://www.ibm.com/docs/en/i/7.5.0?topic=flowcharts-flowchart-4-how-owner-authority-is-checked)
and [adopted authority](https://www.ibm.com/docs/en/i/7.4.0?topic=examples-case-8-adopted-authority-without-private-authority).
`AuthorityPrecedenceTests`, `ObjectIdentityTests` and `ServiceAuthorizationTests`
cover precedence, type isolation, levels, denied direct operations, CL/RPG adoption,
revocation and exception cleanup. HTTP, terminal and batch tests cover actual entry
points; the HTTP gateway returns CPF9802 for denied commands.

## Authentication work still required

Profile and PAM providers, atomic lockout, expiry/recovery, generated initial
credentials and policy-sized hidden fields are implemented. Real OpenSSH/PAM/Unix
peer mapping is tested with isolated fixtures; see [authentication](authentication.md).
Kerberos/SSSD and EIM are covered by the [enterprise identity contract](enterprise-identities.md)
and an isolated native integration test. MFA/SSO, certificate lifecycle, signed
objects and protected key storage remain open. Authentication is audited without credentials; command authority
denials are audited after execution unwinds its transactions.

The initial server socket is restricted to its OS owner. Hidden display contents
are redacted from transmitted frames. Multi-account SSH access and host provisioning
need their own acceptance evidence. See [architecture](architecture.md),
[logging and events](logging-events.md) and [release scope](release-scope.md).
