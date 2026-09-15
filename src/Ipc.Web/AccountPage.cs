namespace Ipc.Web;

internal static class AccountPage
{
    internal static void Map(WebApplication app)
    {
        app.MapGet("/favicon.ico", () => Results.NoContent());
        app.MapGet("/account", (HttpContext context) =>
        {
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
            return Results.Content(Html, "text/html; charset=utf-8");
        });
        app.MapGet("/account.js", () => Results.Content(Script, "text/javascript; charset=utf-8"));
    }

    private const string Html = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>iSeriesPC account security</title><script src="/account.js" defer></script></head>
        <body><main><h1>Account security</h1>
        <p>Use your profile password and, if enrolled, an authenticator or one-use recovery code.</p>
        <form id="account">
        <p><label>Profile <input id="user" name="username" maxlength="10" autocomplete="username" required></label></p>
        <p><label>Password <input id="password" name="password" type="password" maxlength="128" autocomplete="current-password" required></label></p>
        <p><label>Authenticator or recovery code <input id="code" type="password" maxlength="32" autocomplete="one-time-code"></label></p>
        <button type="submit">Sign in and issue SSO token</button>
        <button type="button" id="enroll">Enroll or replace authenticator</button>
        <button type="button" id="disable">Disable authenticator</button>
        </form>
        <section id="enrollment" hidden><h2>Confirm authenticator</h2>
        <p>Add the shared secret below to your authenticator, then enter its six-digit code. Enrollment expires after ten minutes.</p>
        <pre id="shared"></pre><form id="confirm"><label>New authenticator code <input id="new-code" type="password" maxlength="6" autocomplete="one-time-code" required></label>
        <button>Confirm enrollment</button></form></section>
        <section id="recovery" hidden><h2>Save your recovery codes</h2>
        <p>Each code works once, together with your password. Store them privately; they are shown only now. Existing sessions were revoked.</p>
        <pre id="codes"></pre><button id="dismiss" type="button">I have saved the codes</button></section>
        <section><h2>SSO session</h2><p>A token allows terminal and API sign-on without repeating credentials. It expires after eight hours or thirty idle minutes. Keep it private.</p>
        <label>Session token <input id="token" type="password" maxlength="43" autocomplete="off" size="45"></label>
        <button type="button" id="reveal">Show or hide token</button><button type="button" id="logout">Revoke token and sign out</button>
        <p>The page keeps your token only in memory. Reloading clears this page; use sign out to revoke the token.</p></section>
        <p id="status" role="status" aria-live="polite"></p>
        </main></body></html>
        """;

    private const string Script = """
        'use strict';
        const el = id => document.getElementById(id);
        let enrollmentToken = null;
        let busy = false;
        async function request(action, body, token) {
          if (busy) return null;
          busy = true;
          document.querySelectorAll('button').forEach(button => button.disabled = true);
          el('status').textContent = 'Working…';
          try {
            const headers = {'Content-Type': 'application/json'};
            if (token) headers.Authorization = 'Bearer ' + token;
            const response = await fetch('/api/auth/' + action, {method:'POST',headers,body:JSON.stringify(body),cache:'no-store',credentials:'omit'});
            const result = await response.json();
            if (!response.ok) throw new Error(result.mustVerifyMfa ? 'Enter an authenticator or recovery code and sign in again.' : 'Authentication or verification failed. Check credentials and try again.');
            el('status').textContent = 'Completed.';
            return result;
          } catch (error) { el('status').textContent = error.message; return null; }
          finally { busy = false; document.querySelectorAll('button').forEach(button => button.disabled = false); }
        }
        function credentials() {
          const body = {user:el('user').value,password:el('password').value,code:el('code').value};
          el('password').value = ''; el('code').value = '';
          return body;
        }
        el('account').addEventListener('submit', async event => {
          event.preventDefault(); const result = await request('login',credentials());
          if (result) { el('token').value = result.session.token; el('status').textContent = 'Signed in. Token expires at ' + result.session.expires; }
        });
        el('enroll').addEventListener('click', async () => {
          const result = await request('mfa/enroll',credentials());
          if (result) { enrollmentToken = result.enrollment.enrollmentToken; el('shared').textContent = result.enrollment.sharedSecret; el('enrollment').hidden = false; el('new-code').focus(); }
        });
        el('confirm').addEventListener('submit', async event => {
          event.preventDefault(); const code = el('new-code').value; el('new-code').value = '';
          const result = await request('mfa/confirm',{enrollmentToken,code});
          if (result) { enrollmentToken = null; el('shared').textContent = ''; el('enrollment').hidden = true; el('token').value = ''; el('codes').textContent = result.confirmation.recoveryCodes.join('\n'); el('recovery').hidden = false; el('dismiss').focus(); }
        });
        el('disable').addEventListener('click', async () => {
          if (await request('mfa/disable',credentials())) { el('token').value = ''; el('status').textContent = 'Authenticator disabled. Existing sessions were revoked.'; }
        });
        el('dismiss').addEventListener('click', () => { el('codes').textContent = ''; el('recovery').hidden = true; });
        el('reveal').addEventListener('click', () => { el('token').type = el('token').type === 'password' ? 'text' : 'password'; });
        el('logout').addEventListener('click', async () => {
          if (await request('logout',{},el('token').value)) { el('token').value = ''; el('status').textContent = 'Token revoked. Signed out.'; }
        });
        """;
}
