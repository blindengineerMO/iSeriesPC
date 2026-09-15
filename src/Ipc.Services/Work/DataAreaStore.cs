using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

public sealed record DataAreaValue(string Type, int Length, int Decimals, int Ccsid, object Value, string Text = "");

/// <summary>Library data areas use atomic catalog transactions and job object locks.</summary>
public sealed class DataAreaStore(SqliteConnectionFactory factory)
{
    private sealed record Payload(int Version, string Type, int Length, int Decimals, int Ccsid, string Value);
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8
    };
    public void Create(string library, string name, string type, int length, int decimals = 0, object? value = null,
        int ccsid = 37, string text = "", string authority = "*LIBCRTAUT")
    {
        library = library.ToUpperInvariant(); name = name.ToUpperInvariant(); type = type.ToUpperInvariant(); authority = authority.ToUpperInvariant();
        ValidateDimensions(type, length, decimals, ccsid);
        if (text == "*BLANK") text = "";
        if (text.Length > 50 || text.Any(char.IsControl)) throw Invalid("Data-area text requires at most 50 printable characters.");
        var payload = new Payload(1, type, length, decimals, ccsid, Encode(type, length, decimals, ccsid, value));
        var objects = new SqliteObjectStore(factory); var authorization = new ServiceAuthorization(factory);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var ownerLibrary = objects.GetRequired("QSYS", library, ObjectType.Library);
        if (authority == "*LIBCRTAUT") authority = ownerLibrary.ExtendedAttributes?.GetValueOrDefault("ipc.library.createAuthority") ?? "*CHANGE";
        var isList = ObjectName.IsValid(authority);
        if (isList)
        {
            _ = objects.GetRequired("QSYS", authority, ObjectType.AuthorizationList);
            authorization.RequireObject("QSYS", authority, ObjectType.AuthorizationList, AuthorityBit.ObjectReference);
        }
        AuthorityBit bits;
        try { bits = isList ? AuthorityBit.None : Authorities.FromLevel(authority); }
        catch (ArgumentException) { throw Invalid("Invalid data-area public authority."); }
        var descriptor = new ObjectDescriptor { Key = new(library, name), ObjectType = ObjectType.DataArea,
            Attribute = type[1..], Format = "IPCDA1", Owner = OperationIdentity.Current?.Principal ?? "QSECOFR",
            Ccsid = ccsid, Description = text, PublicAuthority = bits, UseAuthorizationListPublicAuthority = isList,
            Source = JsonSerializer.Serialize(payload, JsonOptions) };
        try { objects.Create(descriptor, connection, transaction); }
        catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteExtendedErrorCode is 1555 or 2067)
        { throw new CpfException("CPF1023", "Data area already exists."); }
        if (isList)
        {
            using var attach = connection.CreateCommand(); attach.Transaction = transaction;
            attach.CommandText = "INSERT INTO sys_authorities(lib,name,type,holder,is_authl,bits) VALUES($lib,$name,'*DTAARA',$list,1,$bits)";
            attach.Parameters.AddWithValue("$lib", library); attach.Parameters.AddWithValue("$name", name);
            attach.Parameters.AddWithValue("$list", authority); attach.Parameters.AddWithValue("$bits", (int)Authorities.AllBits); attach.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public DataAreaValue Read(string library, string name, int? start = null, int? length = null)
    {
        library = library.ToUpperInvariant(); name = name.ToUpperInvariant();
        var descriptor = new SqliteObjectStore(factory).GetRequired(library, name, ObjectType.DataArea);
        var payload = Parse(descriptor.Source);
        ValidateDescriptor(descriptor, payload);
        var (offset, count) = Range(payload, start, length);
        object value = payload.Type switch {
            "*CHAR" => new ProgramBuffer(Convert.FromBase64String(payload.Value).AsSpan(offset, count), payload.Ccsid),
            "*LGL" => payload.Value == "1",
            _ => decimal.Parse(payload.Value, CultureInfo.InvariantCulture) };
        return new(payload.Type, payload.Length, payload.Decimals, payload.Ccsid, value, descriptor.Description ?? "");
    }
    public void Change(string library, string name, object value, int? start = null, int? length = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        library = library.ToUpperInvariant(); name = name.ToUpperInvariant();
        var authorization = new ServiceAuthorization(factory);
        authorization.RequireObject(library, name, ObjectType.DataArea, AuthorityBit.ObjectOperate | AuthorityBit.Update);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        authorization.RequireObject(library, name, ObjectType.DataArea, AuthorityBit.ObjectOperate | AuthorityBit.Update);
        var descriptor = ObjectSigningService.Read(connection, transaction, library, name, ObjectType.DataArea)
            ?? throw new CpfException("CPF9801", "Data area not found.");
        var payload = Parse(descriptor.Source); ValidateDescriptor(descriptor, payload);
        var (offset, count) = Range(payload, start, length);
        string replacement;
        if (payload.Type == "*CHAR")
        {
            var bytes = Convert.FromBase64String(payload.Value);
            Convert.FromBase64String(Encode(payload.Type, count, 0, payload.Ccsid, value)).CopyTo(bytes, offset);
            replacement = Convert.ToBase64String(bytes);
        }
        else replacement = Encode(payload.Type, payload.Length, payload.Decimals, payload.Ccsid, value);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE sys_objects SET source=$source,changed=$at,attrs=json_remove(attrs,'$.\"ipc.signature\"') WHERE lib=$lib AND name=$name AND type='*DTAARA'";
        command.Parameters.AddWithValue("$source", JsonSerializer.Serialize(payload with { Value = replacement }, JsonOptions));
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name);
        if (command.ExecuteNonQuery() != 1) throw new CpfException("CPF9801", "Data area not found.");
        transaction.Commit();
    }
    private static void ValidateDescriptor(ObjectDescriptor descriptor, Payload payload)
    {
        if (descriptor.Format != "IPCDA1" || descriptor.Attribute != payload.Type[1..] || descriptor.Ccsid != payload.Ccsid)
            throw Invalid("Data-area attributes and payload do not match.");
    }
    private static Payload Parse(string? json)
    {
        try
        {
            if (json is null || json.Length > 16384) throw Invalid("Missing or oversized data-area payload.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)) throw Invalid("Invalid data-area payload.");
            var payload = JsonSerializer.Deserialize<Payload>(json, JsonOptions) ?? throw Invalid("Invalid data-area payload.");
            if (payload.Version != 1 || payload.Value is null) throw Invalid("Unsupported data-area payload version.");
            ValidateDimensions(payload.Type, payload.Length, payload.Decimals, payload.Ccsid);
            if (payload.Type == "*CHAR") { if (Convert.FromBase64String(payload.Value).Length != payload.Length) throw Invalid("Data-area byte length does not match."); }
            else if (payload.Type == "*LGL") { if (payload.Value is not ("0" or "1")) throw Invalid("Invalid logical data-area value."); }
            else if (!decimal.TryParse(payload.Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ||
                Encode(payload.Type, payload.Length, payload.Decimals, payload.Ccsid, number) != payload.Value) throw Invalid("Invalid decimal data-area value.");
            return payload;
        }
        catch (Exception error) when (error is JsonException or FormatException or OverflowException) { throw Invalid("Malformed data-area payload."); }
    }
    private static (int Offset, int Count) Range(Payload payload, int? start, int? length)
    {
        if (start is null && length is null) return (0, payload.Length);
        if (payload.Type != "*CHAR" || start is null || length is null || start < 1 || length < 1 || start > payload.Length || length > payload.Length - start + 1)
            throw new CpfException("CPF1012", "Invalid data-area substring; only CHAR data supports substrings.");
        return (start.Value - 1, length.Value);
    }
    private static void ValidateDimensions(string type, int length, int decimals, int ccsid)
    {
        if (!CodePage.IsSupported(ccsid) || !(type switch {
            "*CHAR" => length is >= 1 and <= 2000 && decimals == 0,
            "*DEC" => length is >= 1 and <= 24 && decimals >= 0 && decimals <= Math.Min(9, length),
            "*LGL" => length == 1 && decimals == 0, _ => false })) throw Invalid("Unsupported data-area type, length, scale or CCSID.");
    }
    private static string Encode(string type, int length, int decimals, int ccsid, object? value)
    {
        if (type == "*CHAR")
        {
            var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
            byte[] bytes;
            try { bytes = value switch { null => Array.Empty<byte>(), string text => encoding.GetBytes(text),
                ProgramBuffer buffer => buffer.Ccsid == ccsid ? buffer.ToArray() : encoding.GetBytes(buffer.ToText()),
                _ => throw Invalid("Character data areas require character data.") }; }
            catch (EncoderFallbackException) { throw Invalid("Value cannot be represented in the data-area CCSID."); }
            if (bytes.Length > length) throw new CpfException("CPF1011", "Value exceeds the selected data-area length.");
            var result = Enumerable.Repeat(encoding.GetBytes(" ")[0], length).ToArray(); bytes.CopyTo(result, 0);
            return Convert.ToBase64String(result);
        }
        if (type == "*LGL") return value switch { null or false or "0" => "0", true or "1" => "1", _ => throw Invalid("Logical data areas require 0 or 1.") };
        if (value is not (null or decimal or byte or short or int or long or sbyte or ushort or uint or ulong)) throw Invalid("Decimal data areas require an exact numeric value.");
        var number = Convert.ToDecimal(value ?? 0, CultureInfo.InvariantCulture);
        if (decimal.Round(number, decimals) != number || Math.Abs(decimal.Truncate(number)).ToString("0", CultureInfo.InvariantCulture).TrimStart('0').Length > length - decimals)
            throw new CpfException("CPF1011", "Value exceeds the data-area precision or scale.");
        return number.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
    private static CpfException Invalid(string message) => new("IPC0140", message);
}
