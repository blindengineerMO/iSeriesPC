# MFA and shared sessions

Schema 11 adds authenticator enrollment, recovery codes and revocable SSO sessions.
These are iSeriesPC extensions shared by terminal, Unix command transport and HTTP;
they do not implement an OIDC or SAML identity provider. Kerberos remains delegated
to the configured SSSD/PAM provider. An enrolled factor is checked after primary
authentication, including PAM and SSSD sign-on.

## Terminal and browser workflows

At sign-on, F8 opens account security. Enter the profile, its password and its current
factor if enrolled. Enter/F5 enrolls or replaces an authenticator, F6 disables MFA,
and F7 issues an SSO token. Enrollment displays a Base32 secret for an authenticator;
enter its six-digit code to activate it. Save the eight recovery codes shown once.
F3 clears the display and returns to sign-on. No interactive job exists during this
credential-management flow. Passwords and factor codes never enter CL command text.

Normal sign-on asks for an authenticator or recovery code when needed. The challenge
expires after two minutes; the retained primary password is released on a timer,
cancellation, failure or success. Input is hidden in terminal frames. Password changes
also require the enrolled factor, including when the password has expired. After
changing a password, sign-on requires a fresh code. The enrollment-confirmation code
is consumed: wait for the next authenticator time step before signing on with TOTP.

F9 accepts an SSO token. Mapped PAM/SSSD identities still require the matching Linux
peer account. Ending a terminal session revokes its token. Other connections sharing
that token lose authorization at their next command or protected service operation.

The browser account page is `/account`. It provides enrollment, confirmation,
recovery-code display, token issuance and logout. It clears password/code inputs after
submission, keeps the token only in page memory, and offers an explicit show/hide
control. Reloading clears the page but does not revoke the token; use logout first.
Pages and authentication responses use `Cache-Control: no-store`. Remote HTTP access
requires HTTPS; loopback HTTP is allowed for development. No cookie authentication or
cross-origin access is enabled.

## Protocol

All HTTP requests below are JSON POSTs. Field names are case-insensitive. Authentication
errors return 401 with a generic error; `mustVerifyMfa: true` requests another sign-in
attempt with a code. No job is created by these endpoints.

| Endpoint | Request fields | Successful response data |
|---|---|---|
| `/api/auth/login` | `user`, `password`, optional `code` | `session`: `id`, `token`, `profile`, `expires` |
| `/api/auth/logout` | empty object; `Authorization: Bearer …` | `success: true` (idempotent revocation) |
| `/api/auth/mfa/enroll` | `user`, `password`, current `code` if enrolled | `enrollment`: capability token, shared secret, `otpauth` URI, expiry |
| `/api/auth/mfa/confirm` | `enrollmentToken`, new authenticator `code` | `confirmation`: profile and eight recovery codes |
| `/api/auth/mfa/disable` | `user`, `password`, current `code` | `success: true`; existing sessions revoked |

`/api/commands` accepts a bearer token or existing Basic credentials. With Basic,
`X-iSeriesPC-Code` carries the authenticator/recovery code. Each Basic-authenticated
request needs a fresh code; bearer sessions support multiple requests. Password
hashes and internal authentication-proof fields are never returned by HTTP.

The Unix transport uses `CommandRequest` operations `IssueToken`, `RevokeToken`,
`BeginMfa`, `ConfirmMfa`, `DisableMfa`, and `AuthenticateToken`. These pass through the
same `SecurityService`. `CommandConnection.AuthenticationRequestAsync` performs a
one-shot management request; `ConnectWithTokenAsync` creates a job bound to a token.
`ConnectAsync` accepts an optional verification code and uses a transient token that
is revoked when that connection ends. `Signoff` ends a command job normally;
`Logout`, the CL `SIGNOFF` command, or explicit token revocation also revoke a reusable
bearer token. HTTP ends each request's job with `Signoff` so the caller's bearer token
remains reusable. The client cannot replace a connection's identity or job.

## Validity and storage

TOTP follows [RFC 6238](https://www.rfc-editor.org/rfc/rfc6238): a random 160-bit secret,
HMAC-SHA1, six digits, 30-second steps, and at most one adjacent time step of clock
skew. The catalog atomically consumes an accepted time step, rejecting replay across
concurrent sessions. Five failures lock TOTP for 15 minutes. A one-use 128-bit recovery
code can unlock it while retaining MFA. Only SHA-256 hashes of recovery codes are
stored. Enrollment has a random 256-bit capability, ten-minute expiry and five
confirmation attempts. Credential/factor changes invalidate pending enrollments.

SSO tokens are random 256-bit opaque values, stored only as SHA-256 hashes. They have
an eight-hour absolute lifetime and a 30-minute idle timeout, with at most 32 active
tokens per profile. Expired, idle and revoked rows are pruned during issuance.
Sessions bind the credential hash and MFA revision. Password reset/change, profile
disable, directory revocation, MFA replacement/removal and logout invalidate tokens.
Current profile, directory, factor and expiry state are checked again at admission
and protected service boundaries; authority remains live rather than cached in claims.
Jobs persist the nonsecret session ID for correlation. Batch jobs have their own
execution identity and do not depend on an interactive token remaining alive.

MFA secrets use AES-256-GCM with random nonces and profile-bound authenticated data.
The per-catalog key ring is `<database>.keys/`, mode 0700, with mode 0600 keys and an
active-key pointer. Missing, linked, publicly readable or altered key material fails
closed. Keep this directory with a coordinated instance backup: a SQLite backup alone
cannot decrypt MFA secrets. The C13 complete backup/recovery boundary remains open.
Audit events contain profile/session identifiers and outcomes, never passwords,
authenticator secrets, codes or bearer tokens.

If all factors are lost, an instance owner can stop the server and run the existing
`as400server --reset-admin` recovery operation. This resets QSECOFR's factor and writes
a private generated temporary password requiring immediate change. It holds the
catalog ownership lock; ordinary password resets preserve MFA. The recovery is audited.
If interrupted before factor reset completes, repeat the recovery operation.

## Evidence

`MfaCryptographyTests` checks RFC vectors, replay windows, encrypted-secret tampering,
purpose binding, private permissions, restart and missing keys. `MfaLifecycleTests`
covers enrollment expiry, attempt limits, concurrent recovery consumption, password
change/reset, factor replacement/removal, idle/absolute expiry and service revocation.
`MfaEntryPointTests` exercises terminal enrollment/challenge/token/logout and real
HTTP-to-Unix execution, including expired/revoked tokens on already-open connections.
The browser workflow was also exercised with Playwright against an isolated catalog;
SSH/PAM and the isolated Kerberos/LDAP/SSSD acceptance tests continue to pass.
