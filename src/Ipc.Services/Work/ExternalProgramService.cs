using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

public sealed record ExternalProgramFile(string Path, string Sha256);
public sealed record ExternalProgramManifest(int Version, ExternalProgramFile Executable, string[] Arguments,
    ExternalProgramFile[] Dependencies, int TimeoutSeconds, int ProtocolVersion = 1);
public sealed record ExternalProgramResult(bool Success, string Message, IReadOnlyList<object?> Parameters);

/// <summary>Trusted native programs execute under the host Unix account using a versioned JSON pipe ABI.</summary>
public sealed class ExternalProgramService(SqliteConnectionFactory factory, ObjectSigningService signing, JobRuntimeManager? runtime = null)
{
    public const string Attribute = "EXTERNAL";
    private const int MaximumOutput = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public void Register(string library, string name, string executable, IEnumerable<string>? arguments = null,
        IEnumerable<string>? dependencies = null, int timeoutSeconds = 60, int protocolVersion = 1)
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.Service, allowAdopted: false);
        var args = arguments?.Select(a => Path.IsPathFullyQualified(a) && File.Exists(a) ? Pin(a).Path : a).ToArray() ?? Array.Empty<string>();
        var files = (dependencies ?? Array.Empty<string>()).Concat(args.Where(Path.IsPathFullyQualified).Where(File.Exists))
            .Distinct(StringComparer.Ordinal).Select(Pin).ToArray();
        var manifest = new ExternalProgramManifest(1, Pin(executable), args, files, timeoutSeconds, protocolVersion);
        Validate(manifest);
        new SqliteObjectStore(factory).Create(new ObjectDescriptor
        {
            Key = new(library, name), ObjectType = ObjectType.Program, Attribute = Attribute,
            Owner = OperationIdentity.Current?.Principal ?? "QSYS", PublicAuthority = Authorities.UseBits,
            Source = JsonSerializer.Serialize(manifest, JsonOptions),
        });
    }

    public ExternalProgramResult Execute(ObjectDescriptor descriptor, IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken = default) => ExecuteAsync(descriptor, parameters, cancellationToken).GetAwaiter().GetResult();

    private async Task<ExternalProgramResult> ExecuteAsync(ObjectDescriptor descriptor, IReadOnlyList<object?> parameters, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw Invalid("External programs require Linux.");
        new ServiceAuthorization(factory).RequireObject(descriptor.Library, descriptor.Name, ObjectType.Program, Authorities.UseBits);
        signing.RequireExecutable(descriptor);
        if (descriptor.ObjectType != ObjectType.Program || descriptor.Attribute != Attribute || descriptor.Source?.Length > 65536)
            throw Invalid("Invalid external program descriptor.");
        ExternalProgramManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ExternalProgramManifest>(descriptor.Source ?? "", JsonOptions) ?? throw Invalid("Missing external program manifest."); }
        catch (JsonException) { throw Invalid("Invalid external program manifest."); }
        Validate(manifest);
        foreach (var file in manifest.Dependencies.Prepend(manifest.Executable))
            if (Pin(file.Path) != file) throw Invalid("External program executable or dependency changed; register a new program version.");
        if (parameters.Count > 256 || parameters.Any(p => p is not (null or string or bool or byte or short or int or long or float or double or decimal or ProgramBuffer or ProgramConstant)))
            throw Invalid("External program parameters must be at most 256 scalar values or program buffers.");
        var adapted = parameters.Select(p => p is ProgramConstant constant ? manifest.ProtocolVersion == 1 ? constant.Value : constant.Buffer : p).ToArray();
        var wireParameters = adapted.Select(p => p is ProgramBuffer buffer
            ? manifest.ProtocolVersion == 1 ? (object)buffer.ToText() : new { type = "buffer", ccsid = buffer.Ccsid, data = buffer.ToBase64() }
            : p).ToArray();
        var request = JsonSerializer.Serialize(new { version = manifest.ProtocolVersion, parameters = wireParameters }, JsonOptions);
        if (Encoding.UTF8.GetByteCount(request) > 65536) throw Invalid("External program input exceeds 64 KiB.");
        var directory = Path.Combine(Path.GetTempPath(), "ipc-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(manifest.TimeoutSeconds));
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(manifest.Executable.Path)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = directory,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
        };
        process.StartInfo.Environment.Clear();
        process.StartInfo.Environment["LANG"] = "C.UTF-8";
        process.StartInfo.Environment["TMPDIR"] = directory;
        foreach (var argument in manifest.Arguments) process.StartInfo.ArgumentList.Add(argument);
        Task<string>? stdout = null, stderr = null;
        IDisposable? nativeUsage = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw Invalid("External program could not start.");
            nativeUsage = runtime?.TrackProcess(OperationIdentity.Current?.Job, process);
            stdout = ReadBounded(process.StandardOutput, timeout);
            stderr = ReadBounded(process.StandardError, timeout);
            using var kill = timeout.Token.Register(() => Kill(process));
            await process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout; var errors = await stderr;
            if (process.ExitCode != 0) return new(false, $"External program exited with code {process.ExitCode}. {Clean(errors)}", Array.Empty<object?>());
            using var response = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 16 });
            var root = response.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct().Count() != root.EnumerateObject().Count() ||
                root.EnumerateObject().Any(p => p.Name is not ("version" or "success" or "message" or "parameters")) ||
                !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var v) || v != manifest.ProtocolVersion ||
                !root.TryGetProperty("success", out var success) || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Invalid("External program returned an invalid protocol response.");
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var updated = root.TryGetProperty("parameters", out var p) ? p.EnumerateArray().Select(value => Parameter(value, manifest.ProtocolVersion)).ToArray() : adapted;
            if (updated.Length != parameters.Count) throw Invalid("External program returned a different parameter count.");
            return new(success.GetBoolean(), Clean(message + (errors.Length == 0 ? "" : "\n" + errors)), updated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw Invalid("External program timed out or exceeded the output limit."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Invalid("External program failed to start, communicate, or return a valid response.");
        }
        finally
        {
            Kill(process);
            try { if (process.Id != 0) await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
            timeout.Cancel();
            if (stdout is not null) try { await stdout; } catch { /* Preserve the execution failure. */ }
            if (stderr is not null) try { await stderr; } catch { /* Preserve the execution failure. */ }
            try { nativeUsage?.Dispose(); } finally { Directory.Delete(directory, recursive: true); }
        }
    }

    private static async Task<string> ReadBounded(StreamReader stream, CancellationTokenSource cancellation)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; var bytes = 0;
        while (await stream.ReadAsync(buffer.AsMemory(), cancellation.Token) is var read && read > 0)
        {
            bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            if (bytes > MaximumOutput) { cancellation.Cancel(); throw Invalid("External program output exceeds 1 MiB."); }
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    private static object? Parameter(JsonElement value, int version)
    {
        if (version == 2 && value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().Select(p => p.Name).ToArray();
            if (properties.Length != 3 || properties.Distinct(StringComparer.Ordinal).Count() != 3 ||
                !value.TryGetProperty("type", out var type) || type.GetString() != "buffer" ||
                !value.TryGetProperty("ccsid", out var ccsid) || !ccsid.TryGetInt32(out var codePage) ||
                !Ipc.Core.Text.CodePage.IsSupported(codePage) || !value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
                throw Invalid("Invalid program buffer response.");
            try { return new ProgramBuffer(data.GetBytesFromBase64(), codePage); }
            catch (FormatException) { throw Invalid("Invalid program buffer encoding."); }
        }
        return Scalar(value);
    }
    private static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null, JsonValueKind.String => value.GetString(), JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        _ => throw Invalid("External program output parameters must be scalar values."),
    };
    private static string Clean(string text) => new(text.Where(c => !char.IsControl(c) || c is '\n' or '\t').Take(8192).ToArray());
    private static ExternalProgramFile Pin(string path)
    {
        if (!OperatingSystem.IsLinux()) throw Invalid("External programs require Linux.");
        if (!Path.IsPathFullyQualified(path) || path.Length > 4096) throw Invalid("External program files require absolute paths.");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 256L * 1024 * 1024) throw Invalid("External program file is missing or exceeds 256 MiB.");
        path = file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
        if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw Invalid("External program files must not be writable by group or other accounts.");
        using var stream = File.OpenRead(path);
        return new(path, Convert.ToHexString(SHA256.HashData(stream)));
    }
    private static void Validate(ExternalProgramManifest manifest)
    {
        if (manifest.Version != 1 || manifest.ProtocolVersion is not (1 or 2) || manifest.Executable is null || manifest.Arguments is null || manifest.Dependencies is null ||
            manifest.TimeoutSeconds is < 1 or > 3600 || manifest.Arguments.Length > 64 || manifest.Dependencies.Length > 64 ||
            manifest.Arguments.Any(a => a is null || a.Contains('\0')) || manifest.Arguments.Sum(a => a.Length) > 16384 ||
            manifest.Dependencies.Prepend(manifest.Executable).Any(f => f is null || f.Path is null || f.Sha256?.Length != 64))
            throw Invalid("Invalid external program manifest limits.");
    }
    private static CpfException Invalid(string message) => new("IPC0121", message);
}
