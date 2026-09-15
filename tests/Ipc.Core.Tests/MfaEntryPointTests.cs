using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ipc.Console.Session;
using Ipc.Core.Security;
using Ipc.Services;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session.Transport;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed class MfaEntryPointTests
{
    [Fact]
    public void Terminal_enrollment_challenge_recovery_token_signon_and_logout_share_the_same_identity()
    {
        using var system = IpcSystem.Create(":memory:");
        Seed(system);
        using var settings = new AccountSecurityController(system);
        settings.Form!.WriteValue(0, "ALICE"); settings.Form.WriteValue(1, "Password2");
        Assert.Equal("Confirm", settings.Handle(new(AidKey.Enter)).State);
        var text = TerminalFrame.Capture(settings.Buffer).Text;
        var secret = text.Substring((5 - 1) * 80 + 1, 32);
        var code = TotpCode.Generate(MfaLifecycleTests.Decode(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        settings.Form!.WriteValue(0, code);
        Assert.Equal("Saved", settings.Handle(new(AidKey.Enter)).State);
        var recovery = TerminalFrame.Capture(settings.Buffer).Text.Substring((4 - 1) * 80 + 1, 32);
        Assert.Equal(32, recovery.Length);
        using var signon = new SignOnController(system);
        signon.SignOn.Form.WriteValue(0, "ALICE"); signon.SignOn.Form.WriteValue(1, "Password2");
        Assert.Equal("MfaChallenge", signon.Handle(new(AidKey.Enter)).State);
        Assert.Empty(system.Jobs.List());
        Assert.DoesNotContain("Password2", TerminalFrame.Capture(signon.Buffer).Text);
        foreach (var ch in recovery) signon.Handle(new(AidKey.None, Character: ch));
        Assert.DoesNotContain(recovery, TerminalFrame.Capture(signon.Buffer).Text);
        var signedIn = signon.Handle(new(AidKey.Enter));
        using var menu = Assert.IsType<MenuController>(signedIn.Next);
        Assert.Equal("ALICE", menu.JobKey.User);
        Assert.NotNull(system.Jobs.GetRequired(menu.JobKey).AuthSessionId);
        var replay = system.Security.OpenSession("ALICE", "Password2", recovery);
        Assert.False(replay.Success);
        var secondCode = TerminalFrame.Capture(settings.Buffer).Text.Substring((5 - 1) * 80 + 1, 32);
        var token = system.Security.OpenSession("ALICE", "Password2", secondCode).Session!;
        using var tokenSignon = new SignOnController(system);
        Assert.Equal("Token", tokenSignon.Handle(new(AidKey.Pf9)).State);
        foreach (var ch in token.Token) tokenSignon.Handle(new(AidKey.None, Character: ch));
        Assert.DoesNotContain(token.Token, TerminalFrame.Capture(tokenSignon.Buffer).Text);
        var tokenMenu = Assert.IsType<MenuController>(tokenSignon.Handle(new(AidKey.Enter)).Next);
        tokenMenu.Dispose();
        Assert.False(system.Security.ResumeSession(token.Token).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_enrollment_and_token_logout_or_expiry_deny_an_open_headless_connection(bool expire)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-mfa-http-" + Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using (var setup = IpcSystem.Create(directory)) Seed(setup);
        var server = new SessionServer(directory);
        var run = server.RunAsync(stop.Token);
        await server.Ready.WaitAsync(stop.Token);
        await using var web = Ipc.Web.Program.Build(new[] { "--urls", "http://127.0.0.1:0", "--server", server.SocketPath });
        try
        {
            await web.StartAsync(stop.Token);
            using var client = new HttpClient { BaseAddress = new Uri(web.Urls.Single()) };
            var page = await client.GetAsync("/account", stop.Token);
            Assert.True(page.Headers.CacheControl!.NoStore);
            Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
            var enrollResponse = await client.PostAsJsonAsync("/api/auth/mfa/enroll", new { user = "ALICE", password = "Password2" }, stop.Token);
            enrollResponse.EnsureSuccessStatusCode();
            Assert.True(enrollResponse.Headers.CacheControl!.NoStore);
            var enrollment = (await enrollResponse.Content.ReadFromJsonAsync<CommandReply>(stop.Token))!.Enrollment!;
            var confirm = await client.PostAsJsonAsync("/api/auth/mfa/confirm", new {
                enrollmentToken = enrollment.EnrollmentToken,
                code = TotpCode.Generate(MfaLifecycleTests.Decode(enrollment.SharedSecret), DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            }, stop.Token);
            confirm.EnsureSuccessStatusCode();
            var recovery = (await confirm.Content.ReadFromJsonAsync<CommandReply>(stop.Token))!.Confirmation!.RecoveryCodes;
            var challenge = await client.PostAsJsonAsync("/api/auth/login", new { user = "ALICE", password = "Password2" }, stop.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
            Assert.True((await challenge.Content.ReadFromJsonAsync<CommandReply>(stop.Token))!.MustVerifyMfa);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CommandConnection.ConnectAsync(server.SocketPath, "ALICE", "Password2", stop.Token));
            var login = await client.PostAsJsonAsync("/api/auth/login", new { user = "ALICE", password = "Password2", code = recovery[0] }, stop.Token);
            login.EnsureSuccessStatusCode();
            var token = (await login.Content.ReadFromJsonAsync<CommandReply>(stop.Token))!.Session!;
            using var factory = new SqliteConnectionFactory(directory);
            Assert.Empty(new JobService(factory).List());
            await using var existing = await CommandConnection.ConnectWithTokenAsync(server.SocketPath, token.Token, stop.Token);
            Assert.True((await existing.ExecuteAsync("DSPJOB", stop.Token)).Success);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            // Each HTTP command opens, executes and signs off a separate job while
            // the authenticated headless connection remains alive.
            for (var i = 0; i < 12; i++)
            {
                using var command = await client.PostAsJsonAsync("/api/commands", new { command = "DSPJOB" }, stop.Token);
                Assert.True(command.StatusCode == HttpStatusCode.OK, await command.Content.ReadAsStringAsync(stop.Token));
            }
            if (expire)
            {
                using var connection = factory.Open();
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE sys_auth_sessions SET expires=unixepoch('now')-1 WHERE id=$id";
                update.Parameters.AddWithValue("$id", token.Id); update.ExecuteNonQuery();
            }
            else (await client.PostAsJsonAsync("/api/auth/logout", new { }, stop.Token)).EnsureSuccessStatusCode();
            var revoked = await existing.ExecuteAsync("DSPJOB", stop.Token);
            Assert.False(revoked.Success);
            Assert.Contains("expired or revoked", revoked.Result!.Message);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/commands", new { command = "DSPJOB" }, stop.Token)).StatusCode);
            await existing.SignoffAsync(stop.Token);
            var events = new Ipc.Services.Events.DurableEventStore(factory).Read("mfa-entry-audit", 1000).Events;
            Assert.DoesNotContain(events, e => e.Payload.Contains(token.Token) || e.Payload.Contains(recovery[0]) || e.Payload.Contains("Password2"));
        }
        finally
        {
            await web.StopAsync(CancellationToken.None); stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(10)); Directory.Delete(directory, true);
        }
    }

    private static void Seed(IpcSystem system)
    {
        system.Start();
        system.Security.Profiles.Create(new UserProfile { Name = "ALICE" });
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("ALICE"), "Password2");
    }
}
