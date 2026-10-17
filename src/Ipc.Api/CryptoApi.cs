using System.Buffers.Binary;
using System.Security.Cryptography;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Api;

public sealed class CryptoApi(SqliteConnectionFactory factory, int ccsid = 37, JobKey? job = null, CancellationToken cancellationToken = default)
{
    private readonly ApiArguments _arguments = new(ccsid);
    private CryptographicService Service => new(factory);
    public void Invoke(string name, IReadOnlyList<object?> p)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = name switch { "QC3CALHA" => 9, "QC3CALHM" => 11, "QC3ENCDT" => 13, "QC3DECDT" => 12, "QC3CRTKX" => 9, "QC3DESKX" => 2, _ => -1 };
        if (p.Count != count) throw new CpfException("CPF3C36", "Invalid cryptographic API parameter count.");
        var error = new ApiArguments.ErrorReceiver(_arguments, p[^1]);
        if (error.Target is { } target && p.Take(p.Count - 1).Any(value => ReferenceEquals(value, target))) throw ApiArguments.Bad("Error storage cannot alias another argument.");
        try
        {
            if (name == "QC3CRTKX") { Create(p, error); return; }
            if (name == "QC3DESKX")
            {
                var success = error.Success(); var token = Bytes(p[0], 8); cancellationToken.ThrowIfCancellationRequested();
                Service.DestroyKeyContext(CurrentJob(), token, cancellationToken); ApiArguments.Apply(new[] { success }.OfType<ApiArguments.Assignment>()); return;
            }
            var decrypt = name == "QC3DECDT"; var hash = name == "QC3CALHA"; var hmac = name == "QC3CALHM";
            var dataLength = _arguments.Binary32(p[1]);
            if (dataLength < 0 || dataLength > CryptographicService.MaximumDataLength) throw Bad(name == "QC3ENCDT" ? "CPF9DD4" : "CPF9DD5", "Invalid input data length.");
            var data = dataLength == 0 ? Array.Empty<byte>() : Bytes(p[0], dataLength);
            if (!decrypt && _arguments.Text(p[2], 8) != "DATA0100") throw Bad(name == "QC3ENCDT" ? "CPF9DD0" : "CPF9DD1", "Only DATA0100 contiguous input is supported.");
            var algorithmIndex = decrypt ? 2 : 3; var keyIndex = decrypt ? 4 : 5;
            var providerIndex = hash ? 5 : decrypt ? 6 : 7; Provider(p[providerIndex], p[providerIndex + 1]);
            var outputIndex = hash ? 7 : decrypt ? 8 : 9; var output = _arguments.CharacterOutput(p[outputIndex]);
            var outputs = new List<ProgramArgument> { output.Target }; if (error.Target is { } e) outputs.Add(e);
            byte[] result;
            if (hash || hmac)
            {
                if (_arguments.Text(p[algorithmIndex + 1], 8) != "ALGD0500") throw Bad("CPF9DD2", "Only ALGD0500 hash descriptions are supported.");
                var algorithm = _arguments.Binary32(p[algorithmIndex]); _ = CryptographicService.HashLength(algorithm);
                if (hash) result = Service.Hash(algorithm, data, cancellationToken);
                else
                {
                    using var key = Key(p[keyIndex], _arguments.Text(p[keyIndex + 1], 8), algorithm);
                    result = key.Token is { } token ? Service.Hmac(CurrentJob(), token, algorithm, data, cancellationToken) : Service.Hmac(algorithm, key.Value!, data, cancellationToken);
                }
                if (result.Length > output.Buffer.Length) throw Bad("CPF9DD6", "Hash/HMAC output variable is too small.");
                ApiArguments.DistinctOutputs(outputs); var write = _arguments.Prefix(output, result, result.Length); Apply(write, error.Success()); return;
            }
            if (_arguments.Text(p[algorithmIndex + 1], 8) != "ALGD0200") throw Bad("CPF9DD2", "Only ALGD0200 AES descriptions are supported.");
            var description = Bytes(p[algorithmIndex], 52);
            int Integer(int offset) => BinaryPrimitives.ReadInt32BigEndian(description.AsSpan(offset));
            string Character(int offset) => new ProgramBuffer(description.AsSpan(offset, 1), ccsid).ToText();
            if (Integer(0) != 22 || Integer(4) != 16) throw Bad("CPF9DF0", "This software provider supports AES with a 16-byte block.");
            if (description[11] != 0) throw Bad("CPF9DEE", "Reserved byte must be binary zero.");
            if (Integer(12) != 0) throw Bad("CPF9DE2", "MAC length must be binary zero.");
            if (Integer(16) != 0) throw Bad("CPF9DD9", "Effective key size must be zero for AES.");
            var mode = Character(8) switch { "0" => 0, "1" => 1, "7" => 7, _ => throw Bad("CPF9DF0", "Unsupported AES mode.") };
            var padding = Character(9) switch { "0" => 0, "1" => 1, "2" when !decrypt => 2, _ => throw Bad("CPF9DE4", "Invalid pad option.") };
            var iv = description.AsSpan(20, 16).ToArray();
            if (mode == 0 && description.AsSpan(20, 32).IndexOfAnyExcept((byte)0) >= 0) throw Bad("CPF9DE1", "ECB requires a binary-zero IV.");
            var area = _arguments.Binary32(p[outputIndex + 1]); var length = BinaryOutput(p[outputIndex + 2]); outputs.Add(length.Target); ApiArguments.DistinctOutputs(outputs);
            if (area < 0 || area > output.Buffer.Length) throw Bad("CPF9DD6", "Invalid output area length.");
            using (var key = Key(p[keyIndex], _arguments.Text(p[keyIndex + 1], 8), 22))
                result = key.Token is { } token ? Service.Transform(CurrentJob(), token, data, mode, padding, description[10], iv, decrypt, cancellationToken)
                    : Service.Transform(key.Value!, data, mode, padding, description[10], iv, decrypt, cancellationToken);
            try
            {
                if (result.Length > area) throw Bad("CPF9DD6", "Output area is too small.");
                var dataWrite = _arguments.Prefix(output, result, result.Length); var lengthWrite = BinaryAssignment(length, result.Length); var success = error.Success();
                cancellationToken.ThrowIfCancellationRequested(); Apply(dataWrite, lengthWrite, success);
            }
            finally { CryptographicOperations.ZeroMemory(result); }
        }
        catch (CpfException failure) when (error.Report(failure)) { }
    }
    private void Create(IReadOnlyList<object?> p, ApiArguments.ErrorReceiver error)
    {
        if (_arguments.Text(p[2], 1) != "0") throw Bad("CPF9DE9", "Only raw binary key strings are supported.");
        if (_arguments.Text(p[4], 1) != "0") throw Bad("CPF9DE8", "Only clear key form is supported.");
        if (p[5] is not null && _arguments.Text(p[5], 8).Length != 0 || p[6] is not null && _arguments.Text(p[6], 8).Length != 0)
            throw Bad("CPF9DEE", "Key-encrypting key and algorithm must be blank for clear keys.");
        var type = _arguments.Binary32(p[3]); var length = _arguments.Binary32(p[1]); CryptographicService.ValidateKey(type, length);
        var key = Bytes(p[0], length);
        try
        {
            var output = _arguments.CharacterOutput(p[7]); if (output.Buffer.Length < 8) throw Bad("CPF9DD6", "Key token output is too small.");
            ApiArguments.DistinctOutputs(error.Target is { } target ? [output.Target, target] : [output.Target]);
            ApiArguments.Assignment? tokenWrite = null; var success = error.Success();
            Service.CreateKeyContext(CurrentJob(), type, key, token => tokenWrite = _arguments.Prefix(output, token, 8), cancellationToken);
            ApiArguments.Apply(new[] { tokenWrite, success }.OfType<ApiArguments.Assignment>());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private sealed class KeyMaterial(byte[]? value, byte[]? token) : IDisposable
    {
        internal byte[]? Value { get; } = value;
        internal byte[]? Token { get; } = token;
        public void Dispose() { if (Value is not null) CryptographicOperations.ZeroMemory(Value); }
    }
    private KeyMaterial Key(object? value, string format, int expected)
    {
        if (format == "KEYD0100") return new(null, Bytes(value, 8));
        if (format != "KEYD0200") throw Bad("CPF9DD3", "Only KEYD0100 contexts and KEYD0200 raw keys are supported.");
        var description = _arguments.Character(value, 12).ToArray();
        try
        {
            var type = BinaryPrimitives.ReadInt32BigEndian(description); var length = BinaryPrimitives.ReadInt32BigEndian(description.AsSpan(4));
            if (new ProgramBuffer(description.AsSpan(8, 1), ccsid).ToText() != "0") throw Bad("CPF9DE9", "Only binary key strings are supported.");
            if (description.AsSpan(9, 3).IndexOfAnyExcept((byte)0) >= 0) throw Bad("CPF9DEE", "Key reserved bytes must be binary zero.");
            if (type != expected) throw Bad("CPF9DF7", "Key type does not match the algorithm."); CryptographicService.ValidateKey(type, length);
            if (length > description.Length - 12) throw Bad("CPF9DDD", "Key string is shorter than its declared length.");
            return new(description.AsSpan(12, length).ToArray(), null);
        }
        finally { CryptographicOperations.ZeroMemory(description); }
    }
    private void Provider(object? value, object? device)
    {
        var provider = _arguments.Text(value, 1);
        if (provider == "2") throw Bad("CPF9DF0", "Hardware cryptographic providers are not available.");
        if (provider is not ("0" or "1")) throw Bad("CPF9DEC", "Invalid cryptographic provider.");
        if (device is not null && _arguments.Text(device, 10).Length != 0) throw Bad("CPF9DF8", "Software provider requires a blank device name.");
    }
    private ApiArguments.Output BinaryOutput(object? value)
    {
        if (value is not ProgramArgument target || target.Constant is not null) throw Bad("CPF3C90", "Length output requires writable storage.");
        if (target.Type == "*INT" && target.Length == 4) return new(target, target.ToBuffer() ?? new ProgramBuffer(new byte[4], ccsid));
        var output = _arguments.CharacterOutput(value); if (output.Buffer.Length < 4) throw ApiArguments.Bad("Length output requires BIN(4)."); return output;
    }
    private ApiArguments.Assignment BinaryAssignment(ApiArguments.Output output, int value)
    {
        if (output.Target.Type == "*INT") return new(output.Target, output.Target.Normalize(value));
        var bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); return _arguments.Prefix(output, bytes, 4);
    }
    private byte[] Bytes(object? value, int length) => _arguments.Character(value, length).ToArray().AsSpan(0, length).ToArray();
    private JobKey CurrentJob() => job ?? OperationIdentity.Current?.Job ?? throw Bad("CPF9DF5", "Key contexts require an executing job.");
    private void Apply(params ApiArguments.Assignment?[] values) { cancellationToken.ThrowIfCancellationRequested(); ApiArguments.Apply(values.OfType<ApiArguments.Assignment>()); }
    private static CpfException Bad(string id, string message) => new(id, message);
}
