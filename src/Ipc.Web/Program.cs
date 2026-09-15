using System.Net;
using System.Text;
using Ipc.Session.Transport;

namespace Ipc.Web;

public static class Program
{
    public static Task Main(string[] args) => Build(args).RunAsync();

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (builder.Configuration["urls"] is null) builder.WebHost.UseUrls("http://127.0.0.1:5080");
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024);
        var app = builder.Build();
        var socketPath = builder.Configuration["server"] ?? Path.Combine("data", "run", "as400.sock");
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/account"))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                if (!context.Request.IsHttps && (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)))
                {
                    context.Response.StatusCode = 426;
                    await context.Response.WriteAsync("HTTPS is required.");
                    return;
                }
            }
            await next(context);
        });
        app.MapPost("/api/auth/{**action}", async (HttpContext context, string action, AuthenticationRequest body) =>
        {
            var operation = action switch
            {
                "login" => "IssueToken", "logout" => "RevokeToken", "mfa/enroll" => "BeginMfa",
                "mfa/confirm" => "ConfirmMfa", "mfa/disable" => "DisableMfa", _ => null,
            };
            if (operation is null) return Results.NotFound();
            if (operation == "RevokeToken" && ParseBearer(context.Request.Headers.Authorization.ToString()) is not { Length: 43 })
                return Results.Unauthorized();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var reply = await CommandConnection.AuthenticationRequestAsync(socketPath,
                    new CommandRequest(operation, body.User, body.Password, VerificationCode: body.Code,
                        Token: ParseBearer(context.Request.Headers.Authorization.ToString()), EnrollmentToken: body.EnrollmentToken), timeout.Token);
                return Results.Json(reply, statusCode: reply.Success ? 200 : 401);
            }
            catch (OperationCanceledException) { return Results.Problem("Authentication timed out or was cancelled.", statusCode: 504); }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            { return Results.Problem("Authentication server is unavailable.", statusCode: 503); }
        });
        AccountPage.Map(app);
        app.MapGet("/health/live", () => Results.Ok(new { service = "as400web", status = "live" }));
        app.MapPost("/api/commands", async (HttpContext context, ExecuteCommand body) =>
        {
            if (!context.Request.IsHttps && (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)))
                return Results.Problem("HTTPS is required.", statusCode: 426);
            if (string.IsNullOrWhiteSpace(body.Command) || body.Command.Length > 32768)
                return Results.BadRequest(new { code = "IPC0005", message = "Command length must be 1 through 32768 characters." });
            var credentials = ParseBasic(context.Request.Headers.Authorization.ToString());
            var bearer = ParseBearer(context.Request.Headers.Authorization.ToString());
            if (credentials is null && bearer is null)
            {
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"iSeriesPC\", charset=\"UTF-8\"";
                return Results.Unauthorized();
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await using var connection = bearer is not null
                    ? await CommandConnection.ConnectWithTokenAsync(socketPath, bearer, timeout.Token)
                    : await CommandConnection.ConnectAsync(socketPath, credentials!.Value.User, credentials.Value.Password, timeout.Token,
                        context.Request.Headers["X-iSeriesPC-Code"].ToString());
                var reply = await connection.ExecuteAsync(body.Command, timeout.Token);
                if (reply.Result?.Outcome != Ipc.Cl.Commands.CommandOutcome.SignOff)
                    await connection.SignoffAsync(timeout.Token);
                var status = reply.Success ? 200 : reply.Result?.Message?.StartsWith("IPC0002", StringComparison.Ordinal) == true ? 501 : 400;
                return Results.Json(reply, statusCode: status);
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            catch (OperationCanceledException) { return Results.Problem("Execution timed out or was cancelled.", statusCode: 504); }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            { return Results.Problem("Execution server is unavailable.", statusCode: 503); }
        });
        return app;
    }

    private static (string User, string Password)? ParseBasic(string header)
    {
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) || header.Length > 4096) return null;
        try
        {
            var decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(header[6..]));
            var separator = decoded.IndexOf(':');
            if (separator <= 0 || separator == decoded.Length - 1) return null;
            return (decoded[..separator], decoded[(separator + 1)..]);
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { return null; }
    }

    private static string? ParseBearer(string header) => header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header.Length == 50
        ? header[7..] : null;
}

public sealed record ExecuteCommand(string Command);
public sealed record AuthenticationRequest(string? User = null, string? Password = null, string? Code = null, string? EnrollmentToken = null);
