using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session.Transport;

namespace Ipc.Core.Tests;

public sealed class WebExecutionTests
{
    [Fact]
    public async Task Real_http_gateway_uses_server_jobs_and_preserves_authentication_and_diagnostics()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-web-" + Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using (var setup = IpcSystem.Create(directory))
        {
            setup.Start();
            setup.Security.Profiles.Create(new UserProfile { Name = "WEBUSER" });
            setup.Security.Profiles.SetPassword(setup.Security.Profiles.Get("WEBUSER"), "PASS1234");
        }
        var server = new SessionServer(directory);
        var run = server.RunAsync(stop.Token);
        await server.Ready.WaitAsync(stop.Token);
        await using var web = Ipc.Web.Program.Build(new[] { "--urls", "http://127.0.0.1:0", "--server", server.SocketPath });
        try
        {
            await web.StartAsync(stop.Token);
            using var client = new HttpClient { BaseAddress = new Uri(web.Urls.Single()) };
            var unauthenticated = await client.PostAsJsonAsync("/api/commands", new { command = "DSPJOB" }, stop.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("WEBUSER:WRONG")));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/commands", new { command = "DSPJOB" }, stop.Token)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("WEBUSER:PASS1234")));
            var response = await client.PostAsJsonAsync("/api/commands", new { command = "DSPJOB" }, stop.Token);
            response.EnsureSuccessStatusCode();
            var reply = await response.Content.ReadFromJsonAsync<CommandReply>(stop.Token);
            Assert.Equal("WEBUSER", reply!.Job!.Value.User);
            using var factory = new SqliteConnectionFactory(directory);
            var job = new JobService(factory).GetRequired(reply.Job.Value);
            Assert.Equal(JobCompletion.Normal, job.CompletionCode);
            Assert.Contains(reply.Job.ToString()!, reply.Result!.Message);
            var denied = await client.PostAsJsonAsync("/api/commands", new { command = "CRTLIB LIB(FORBIDDEN)" }, stop.Token);
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            var deniedReply = await denied.Content.ReadFromJsonAsync<CommandReply>(stop.Token);
            Assert.Contains("CPF9802", deniedReply!.Result!.Message);
            Assert.False(new SqliteObjectStore(factory).Exists("QSYS", "FORBIDDEN", "*LIB"));
            Assert.Equal(HttpStatusCode.NotImplemented, (await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('VALUES 1')" }, stop.Token)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/commands", new { command = "DSPJOB JOB(OTHER)" }, stop.Token)).StatusCode);
        }
        finally
        {
            await web.StopAsync(CancellationToken.None);
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(directory, recursive: true);
        }
    }
}
