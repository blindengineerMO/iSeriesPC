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
            setup.Security.Authority.Grant("QSYS", "QGPL", Ipc.Core.Objects.ObjectType.Library, "WEBUSER",
                Ipc.Core.Objects.Authorities.UseBits | Ipc.Core.Objects.AuthorityBit.Add);
            var scripts = new Ipc.Db.Store.SqliteFileStore(setup.Connections, setup.Objects);
            scripts.CreateSourceFile("QGPL", "WEBSQL");
            scripts.SaveSourceMember("QGPL", "WEBSQL", "SCRIPT", "INSERT INTO QGPL/WEBDATA VALUES(9);\nUPDATE QGPL/WEBDATA SET ID=ID+1;\n", null);
            scripts.SaveSourceMember("QGPL", "WEBSQL", "QUERY", "UPDATE QGPL/WEBDATA SET ID=&ID WHERE ID=10", null);
            scripts.SaveSourceMember("QGPL", "WEBSQL", "REPORT", "SELECT ID FROM QGPL/WEBDATA", null);
            setup.Security.Authority.Grant("QGPL", "WEBSQL", Ipc.Core.Objects.ObjectType.File, "WEBUSER", Ipc.Core.Objects.Authorities.UseBits);
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
            var apiResponse = await client.PostAsJsonAsync("/api/commands", new { command = "CALL QCMDEXC PARM('DSPJOB' 6)" }, stop.Token);
            apiResponse.EnsureSuccessStatusCode();
            var apiReply = await apiResponse.Content.ReadFromJsonAsync<CommandReply>(stop.Token);
            Assert.Equal("WEBUSER", apiReply!.Job!.Value.User);
            Assert.Contains(apiReply.Job.ToString()!, apiReply.Result!.Message);
            Assert.Equal(JobCompletion.Normal, new JobService(factory).GetRequired(apiReply.Job.Value).CompletionCode);
            var apiDenied = await client.PostAsJsonAsync("/api/commands", new { command = "CALL QCMDEXC PARM('CRTLIB LIB(FORBIDDEN)' 21)" }, stop.Token);
            Assert.Equal(HttpStatusCode.BadRequest, apiDenied.StatusCode);
            var apiDeniedReply = await apiDenied.Content.ReadFromJsonAsync<CommandReply>(stop.Token);
            Assert.Equal("CPF9802", apiDeniedReply!.Result!.MessageId);
            var denied = await client.PostAsJsonAsync("/api/commands", new { command = "CRTLIB LIB(FORBIDDEN)" }, stop.Token);
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            var deniedReply = await denied.Content.ReadFromJsonAsync<CommandReply>(stop.Token);
            Assert.Contains("CPF9802", deniedReply!.Result!.Message);
            Assert.False(new SqliteObjectStore(factory).Exists("QSYS", "FORBIDDEN", "*LIB"));
            var createSql = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('CREATE TABLE QGPL/WEBDATA(ID INTEGER NOT NULL PRIMARY KEY,CONSTRAINT POSID CHECK(ID>0))') COMMIT(*NONE)" }, stop.Token);
            createSql.EnsureSuccessStatusCode();
            var createReply = await createSql.Content.ReadFromJsonAsync<CommandReply>(stop.Token);
            Assert.Equal("WEBUSER", createReply!.Job!.Value.User);
            Assert.Equal("WEBUSER", new SqliteObjectStore(factory).GetRequired("QGPL", "WEBDATA", Ipc.Core.Objects.ObjectType.File).Owner);
            var alterSql = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('ALTER TABLE QGPL/WEBDATA ALTER ID SET DATA TYPE BIGINT') COMMIT(*NONE)" }, stop.Token);
            alterSql.EnsureSuccessStatusCode();
            Assert.Equal(8, new Ipc.Db.Store.SqliteFileStore(factory, new SqliteObjectStore(factory)).GetDefinition("QGPL", "WEBDATA")!.PrimaryFormat.Find("ID")!.Length);
            var insertSql = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('INSERT INTO QGPL/WEBDATA VALUES(7)') COMMIT(*NONE)" }, stop.Token);
            insertSql.EnsureSuccessStatusCode();
            var insertReply = await insertSql.Content.ReadFromJsonAsync<CommandReply>(stop.Token);
            Assert.NotEqual(createReply.Job, insertReply!.Job);
            Assert.Equal(JobCompletion.Normal, new JobService(factory).GetRequired(insertReply.Job!.Value).CompletionCode);
            var records = new Ipc.Db.Store.SqliteFileStore(factory, new SqliteObjectStore(factory));
            Assert.Equal(7L, Assert.Single(records.ReadAll("QGPL", "WEBDATA", "WEBDATA"))["ID"]);
            var checkedSql = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('INSERT INTO QGPL/WEBDATA VALUES(-1)') COMMIT(*NONE)" }, stop.Token);
            Assert.Equal(HttpStatusCode.BadRequest, checkedSql.StatusCode); Assert.Contains("SQL0545", await checkedSql.Content.ReadAsStringAsync(stop.Token));
            Assert.Equal(7L, Assert.Single(records.ReadAll("QGPL", "WEBDATA", "WEBDATA"))["ID"]);
            var createIndex = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('CREATE INDEX QGPL/WEBIDX ON QGPL/WEBDATA(ID DESC)') COMMIT(*NONE)" }, stop.Token);
            createIndex.EnsureSuccessStatusCode(); Assert.True(records.GetDefinition("QGPL", "WEBIDX")!.SqlIndex);
            Assert.Equal(7L, Assert.Single(records.ReadKeyed("QGPL", "WEBIDX", "WEBIDX"))["ID"]);
            var dropIndex = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('DROP INDEX QGPL/WEBIDX') COMMIT(*NONE)" }, stop.Token);
            dropIndex.EnsureSuccessStatusCode(); Assert.False(records.FileExists("QGPL", "WEBIDX"));
            var createView = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('CREATE VIEW QGPL/WEBVIEW AS SELECT ID FROM QGPL/WEBDATA WHERE ID<9 WITH CHECK OPTION') COMMIT(*NONE)" }, stop.Token);
            createView.EnsureSuccessStatusCode(); Assert.NotNull(records.GetDefinition("QGPL", "WEBVIEW")!.SqlView);
            Assert.Equal(7L, Assert.Single(records.ReadAll("QGPL", "WEBVIEW", "WEBVIEW"))["ID"]);
            var duplicateSql = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('INSERT INTO QGPL/WEBDATA VALUES(7)') COMMIT(*NONE)" }, stop.Token);
            Assert.Equal(HttpStatusCode.BadRequest, duplicateSql.StatusCode);
            var duplicateReply = await duplicateSql.Content.ReadFromJsonAsync<CommandReply>(stop.Token);
            Assert.Equal("SQL0803", duplicateReply!.Result!.MessageId);
            var updateSql = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('UPDATE QGPL/WEBVIEW SET ID=ID+1 WHERE ID=7') COMMIT(*NONE)" }, stop.Token);
            updateSql.EnsureSuccessStatusCode();
            Assert.Equal(8L, Assert.Single(records.ReadAll("QGPL", "WEBDATA", "WEBDATA"))["ID"]);
            var rejectedViewWrite = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('INSERT INTO QGPL/WEBVIEW VALUES(9)') COMMIT(*NONE)" }, stop.Token);
            Assert.Equal(HttpStatusCode.BadRequest, rejectedViewWrite.StatusCode);
            Assert.Contains("SQL0161", await rejectedViewWrite.Content.ReadAsStringAsync(stop.Token));
            Assert.Equal(8L, Assert.Single(records.ReadAll("QGPL", "WEBDATA", "WEBDATA"))["ID"]);
            var deleteSql = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('DELETE FROM QGPL/WEBVIEW WHERE ID=8') COMMIT(*NONE)" }, stop.Token);
            deleteSql.EnsureSuccessStatusCode(); Assert.Equal(0, records.RowCount("QGPL", "WEBDATA", "WEBDATA"));
            var dropView = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('DROP VIEW QGPL/WEBVIEW') COMMIT(*NONE)" }, stop.Token);
            dropView.EnsureSuccessStatusCode(); Assert.False(records.FileExists("QGPL", "WEBVIEW"));
            var scriptSql = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQLSTM SRCFILE(QGPL/WEBSQL) SRCMBR(SCRIPT) COMMIT(*NONE) OPTION(*NOLIST)" }, stop.Token);
            scriptSql.EnsureSuccessStatusCode();
            var scriptReply = await scriptSql.Content.ReadFromJsonAsync<CommandReply>(stop.Token);
            Assert.Equal("WEBUSER", scriptReply!.Job!.Value.User);
            Assert.Equal(10L, Assert.Single(records.ReadAll("QGPL", "WEBDATA", "WEBDATA"))["ID"]);
            var createQuery = await client.PostAsJsonAsync("/api/commands", new { command = "CRTQMQRY QGPL/WEBQUERY SRCFILE(QGPL/WEBSQL) SRCMBR(QUERY)" }, stop.Token);
            createQuery.EnsureSuccessStatusCode();
            Assert.Equal("WEBUSER", new SqliteObjectStore(factory).GetRequired("QGPL", "WEBQUERY", Ipc.Core.Objects.ObjectType.QueryManagementQuery).Owner);
            var runQuery = await client.PostAsJsonAsync("/api/commands", new { command = "STRQMQRY QGPL/WEBQUERY SETVAR((ID 11))" }, stop.Token);
            runQuery.EnsureSuccessStatusCode(); Assert.Equal(11L, Assert.Single(records.ReadAll("QGPL", "WEBDATA", "WEBDATA"))["ID"]);
            var missingVariable = await client.PostAsJsonAsync("/api/commands", new { command = "STRQMQRY QGPL/WEBQUERY" }, stop.Token);
            Assert.Equal(HttpStatusCode.BadRequest, missingVariable.StatusCode); Assert.Contains("QWM2701", await missingVariable.Content.ReadAsStringAsync(stop.Token));
            var createReport = await client.PostAsJsonAsync("/api/commands", new { command = "CRTQMQRY QGPL/WEBREPORT SRCFILE(QGPL/WEBSQL) SRCMBR(REPORT)" }, stop.Token);
            createReport.EnsureSuccessStatusCode();
            var queryDisplay = await client.PostAsJsonAsync("/api/commands", new { command = "STRQMQRY QGPL/WEBREPORT" }, stop.Token);
            Assert.Equal(HttpStatusCode.BadRequest, queryDisplay.StatusCode); Assert.Contains("interactive terminal", await queryDisplay.Content.ReadAsStringAsync(stop.Token));
            var queryFile = await client.PostAsJsonAsync("/api/commands", new { command = "STRQMQRY QGPL/WEBREPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/WEBOUT)" }, stop.Token);
            queryFile.EnsureSuccessStatusCode(); var fileReply = await queryFile.Content.ReadFromJsonAsync<CommandReply>(stop.Token);
            Assert.Equal("WEBUSER", fileReply!.Job!.Value.User); Assert.Equal(11L, Assert.Single(records.ReadAll("QGPL", "WEBOUT", "WEBOUT"))["ID"]);
            var outputDescriptor = new SqliteObjectStore(factory).GetRequired("QGPL", "WEBOUT", Ipc.Core.Objects.ObjectType.File);
            Assert.Equal("WEBUSER", outputDescriptor.Owner); Assert.Equal(Ipc.Core.Objects.AuthorityBit.None, outputDescriptor.PublicAuthority);
            var appendQueryFile = await client.PostAsJsonAsync("/api/commands", new { command = "STRQMQRY QGPL/WEBREPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/WEBOUT) OUTMBR(*FIRST *ADD)" }, stop.Token);
            appendQueryFile.EnsureSuccessStatusCode(); Assert.Equal(2, records.RowCount("QGPL", "WEBOUT", "WEBOUT"));
            var createChild = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('CREATE TABLE QGPL/WEBCHILD(ID BIGINT REFERENCES QGPL/WEBDATA ON DELETE CASCADE)') COMMIT(*NONE)" }, stop.Token);
            createChild.EnsureSuccessStatusCode();
            var invalidChild = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('INSERT INTO QGPL/WEBCHILD VALUES(12)') COMMIT(*NONE)" }, stop.Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalidChild.StatusCode);
            var foreignReply = await invalidChild.Content.ReadFromJsonAsync<CommandReply>(stop.Token);
            Assert.Equal("SQL0530", foreignReply!.Result!.MessageId); Assert.Equal(0, records.RowCount("QGPL", "WEBCHILD", "WEBCHILD"));
            var validChild = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('INSERT INTO QGPL/WEBCHILD VALUES(11)') COMMIT(*NONE)" }, stop.Token);
            validChild.EnsureSuccessStatusCode(); Assert.Equal(11L, Assert.Single(records.ReadAll("QGPL", "WEBCHILD", "WEBCHILD"))["ID"]);
            var blockedParent = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('UPDATE QGPL/WEBDATA SET ID=12') COMMIT(*NONE)" }, stop.Token);
            Assert.Equal(HttpStatusCode.BadRequest, blockedParent.StatusCode); Assert.Contains("SQL0531", await blockedParent.Content.ReadAsStringAsync(stop.Token));
            Assert.Equal(11L, Assert.Single(records.ReadAll("QGPL", "WEBDATA", "WEBDATA"))["ID"]);
            var cascadeParent = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('DELETE FROM QGPL/WEBDATA') COMMIT(*NONE)" }, stop.Token);
            cascadeParent.EnsureSuccessStatusCode(); Assert.Equal(0, records.RowCount("QGPL", "WEBCHILD", "WEBCHILD"));
            var dropChild = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('DROP TABLE QGPL/WEBCHILD') COMMIT(*NONE)" }, stop.Token);
            dropChild.EnsureSuccessStatusCode(); Assert.False(records.FileExists("QGPL", "WEBCHILD"));
            var dropSql = await client.PostAsJsonAsync("/api/commands", new { command = "RUNSQL SQL('DROP TABLE QGPL/WEBDATA') COMMIT(*NONE)" }, stop.Token);
            dropSql.EnsureSuccessStatusCode(); Assert.False(records.FileExists("QGPL", "WEBDATA"));
            Assert.Equal(HttpStatusCode.NotImplemented, (await client.PostAsJsonAsync("/api/commands", new { command = "STRQM" }, stop.Token)).StatusCode);
            var terminalOnly = await client.PostAsJsonAsync("/api/commands", new { command = "STRSQL" }, stop.Token);
            Assert.Equal(HttpStatusCode.BadRequest, terminalOnly.StatusCode);
            Assert.Contains("SQL6141", await terminalOnly.Content.ReadAsStringAsync(stop.Token));
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
