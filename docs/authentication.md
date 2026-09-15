# Authentication and enrollment

The server owns password verification, profile state and session creation. All
existing terminal, headless and HTTP entry points use the same security service.
Authentication failures never create jobs. Profile password hashes use salted
PBKDF2-SHA256 (210,000 iterations by default); malformed hashes fail closed.

## First start and recovery

A new persistent catalog creates a random QSECOFR credential in
`<database-path>.initial-password`, with mode 0600. The default credential contains
24 distinct randomly selected characters; stricter length policies are respected.
The catalog stores only a hash. Startup preserves enrollment material until the
initial password is changed. Successful change removes the file. Memory-only
catalogs have no retrievable initial password; test/development hosts explicitly
provision their profiles through the trusted host API.

Read the private file locally as the instance owner and sign on through the
terminal. The initial password requires replacement before creating a session.
There is no shared default password. API callers requiring a password change are
rejected and must finish enrollment through the terminal.

For recovery, stop the instance and run as its OS owner:

```sh
as400server --data-dir /path/to/instance --reset-admin
```

This obtains the same exclusive catalog ownership lock as the server, replaces the
QSECOFR credential, resets lockout and writes a new private enrollment file. It
prints the file path, never the credential. Password change is required again.
Possession of the instance's files is a host administration capability; clients
cannot invoke this offline recovery path. Protect and back up the catalog directory
as private application data. Recovery is audited without the generated password.

## Password and account policy

QPWDLVL 0/1 limits new passwords to 10 characters; 2/3/4 allows 128. Fresh catalogs
use level 3. The legacy `*SYSVAL` stored value is treated as the extended-length
mode until explicitly changed. These values select the supported length policy,
not native IBM hash formats or case-folding behavior. Passwords are case-sensitive
and are not trimmed; controls are rejected. QPWDMINLEN cannot exceed the level's
maximum. QPWDRQDDGT, QPWDRQDRPT and QPWDEXPITV are enforced; a zero expiry interval
means no scheduled expiry. Hidden terminal fields retain full passwords and scroll
within their display width, including trailing spaces.

Failed sign-ons and failed current-password checks atomically increment QMAXSIGN
counters. Reaching the limit disables the profile. Successful authentication uses
a conditional update that cannot undo a concurrent disable or password reset.
Credential changes update credential fields only, preserving newer profile edits.
Disabled/expired accounts require administrator recovery; knowing the old password
does not reactivate them. Expired passwords can be changed by their holder after
verification. Profile status/expiry is checked again during live service execution.

## Linux-PAM and SSH

Configure mappings in the instance's private `system.json`, then restart:

```json
{
  "Authentication": {
    "PamService": "iseriespc",
    "PamAccounts": { "OPERATOR": "linux-operator" },
    "BindTerminalPamToUnixAccount": true,
    "PamRequireProfilePassword": true,
    "AllowGroupSocketAccess": false
  }
}
```

Provision the target iSeriesPC profile separately. Mappings are explicit and
one-to-one; ambiguous profile names or Linux accounts fail configuration loading.
Configured PAM mappings require both Linux and profile password verification by
default, including at QSECURITY 10. Set `PamRequireProfilePassword` to false explicitly
for external-only credentials. A correct local profile password never bypasses PAM
failure. Unmapped profiles, including the recovery administrator, use local credentials. Linux account and password expiry are checked
through `pam_acct_mgmt` after password verification. Expired Linux passwords must
be changed through the Linux account provider (`passwd` or the site's recovery
workflow); the app does not replace host passwords. With dual verification, change
the Linux password first, then enter the profile name and press F6 on the sign-on
screen. Supply the old profile password and new Linux password; both proofs are
required. The server reauthenticates, including Unix peer binding, before creating
a session. An independently disabled or
expired iSeriesPC profile also denies sign-on.

Install a site-reviewed `/etc/pam.d/iseriespc` stack using the site's authentication
and account modules. Missing services fail closed rather than selecting PAM's
`other` fallback. Optional `PamConfigurationDirectory` selects an explicit private
PAM service directory, useful for isolated deployments and tests. Service files and
directories must not be group/other writable. The native provider supports standard
username/password conversations; interactive module-specific MFA prompts require
the separate MFA integration. PAM runs with the server's OS permissions, so configure
its modules and helper access accordingly.

PAM checks admit at most eight native calls, with a 15-second caller timeout. A
hung native call keeps its slot until it returns; timed-out work cannot create a
session or change a profile later. Saturation, provider errors and unavailable
accounts deny access without incrementing local lockout counters. Incorrect
credentials do increment them. No local fallback is attempted.

For terminal sessions, Linux `SO_PEERCRED` supplies the connecting process's UID;
NSS resolves its account name. The server compares this verified account with the
configured PAM mapping. Client-supplied usernames cannot replace the kernel peer
identity. OpenSSH authenticates the Linux login, and its `as400menu` process then
connects as that account. HTTP/headless callers authenticate their supplied PAM
credentials through the same service; they do not claim an SSH peer identity.

The default socket is owner-only. To serve multiple Linux accounts, pre-provision
a dedicated group-owned socket directory with mode 0750, outside the private 0700
catalog directory, and set `AllowGroupSocketAccess` to true. Start the server with
`--socket /run/iseriespc/as400.sock`; the socket uses 0660. Only the dedicated access
group should traverse that directory. The installer and multi-account host
qualification remain separate release tasks.

## Evidence and remaining security work

`AuthenticationLifecycleTests` covers concurrency, expiry/recovery, enrollment,
policy, trailing spaces, malformed hashes and credential secrecy.
`PamAuthenticationTests` exercises the actual Linux-PAM conversation and account
stack as well as unavailable/expired provider and mapping failures.

`python3 tools/ssh-smoke.py --dotnet /path/to/dotnet` runs a real loopback OpenSSH
server with temporary keys and a temporary PAM test module, authenticates the
current Linux account, rejects a mismatched profile mapping, verifies the server-owned
job and signoff, and checks audit/terminal secrecy. It does not modify system
accounts, SSH keys or `/etc/pam.d`. OpenSSH and libpam-modules are required. The test
supports extracted OpenSSH binaries through `--sshd`, `--sshd-session` and
`--sshd-auth`. This proves the app integration; it does not certify a site's
pam_unix/SSSD policy or multiple OS account provisioning.

The native interface follows Linux-PAM's [application interface](https://github.com/linux-pam/linux-pam/blob/master/libpam/include/security/_pam_types.h),
[transaction initialization](https://github.com/linux-pam/linux-pam/blob/master/doc/man/pam_start.3.xml)
and [account management](https://github.com/linux-pam/linux-pam/blob/master/doc/man/pam_acct_mgmt.3.xml).
[Kerberos/SSSD and EIM mappings](enterprise-identities.md) have separate integration
evidence. MFA/SSO, certificates and signed objects remain subsequent C03 tasks.

MFA enrollment, recovery, shared session tokens, factor-protected password changes,
and offline recovery of QSECOFR are documented in [MFA and sessions](mfa-sessions.md).
