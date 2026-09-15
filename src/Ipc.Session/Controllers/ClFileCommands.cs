using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ipc.Cl.Interpreter;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Db.Records;
using Ipc.Core.Work;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private ClDatabaseFile DescribeClDatabaseFile(ClFileRequest request)
    {
        var (library, name) = ResolveWorkObject(request.File, ObjectType.File);
        var definition = _files.GetDefinition(library, name) ?? throw new CpfException("IPC0006", "DCLF currently requires a database file.");
        return DescribeClDatabaseFile(request, library, name, definition);
    }
    private static ClDatabaseFile DescribeClDatabaseFile(ClFileRequest request, string library, string name, FileDefinition definition)
    {
        if (definition.Formats.Count != 1) throw new CpfException("CPF0865", "DCLF requires a single-format database file.");
        var format = definition.PrimaryFormat; var fields = new List<ClFileField>();
        foreach (var field in format.Fields)
        {
            if (field.VariableLength && !request.AllowVariableLength) throw new CpfException("IPC0006", "DCLF requires ALWVARLEN(*YES) for variable-length fields.");
            var digits = ClFileDigits(field);
            var type = field.Type switch {
                FieldType.Alpha or FieldType.Date or FieldType.Time or FieldType.Timestamp => "*CHAR",
                FieldType.Packed or FieldType.Zoned => "*DEC",
                FieldType.Binary => request.BinaryAsInteger && digits < 10 && field.Decimals == 0 ? "*INT" : "*DEC",
                FieldType.Logic => "*LGL",
                _ => throw new CpfException("IPC0006", "CL database files cannot contain floating-point fields.") };
            var length = type == "*DEC" ? digits : field.Length;
            if (type == "*DEC" && digits > 15) { type = "*CHAR"; length = digits / 2 + 1; }
            else if (type == "*DEC" && field.Decimals > 9) throw new CpfException("IPC0006", "CL decimal variables support at most nine decimal positions.");
            if (field.VariableLength) length += 2;
            fields.Add(new(field.Name, type, length, type == "*DEC" ? field.Decimals : 0));
        }
        var signature = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(format.Fields.Select(f => new {
            f.Name, f.Type, f.Length, f.Decimals, f.DeclaredDigits, f.Position, f.Ccsid, f.VariableLength, f.NullCapable
        }))));
        var result = new ClDatabaseFile(library + "/" + name, format.Name, signature, fields);
        ClFileBindings.Validate(result); return result;
    }
    private static int ClFileDigits(FieldSpec field) => field.DeclaredDigits > 0 ? field.DeclaredDigits : field.Type == FieldType.Binary ? field.Length == 2 ? 4 : field.Length == 4 ? 9 : 18 : field.Length;
    private ClDatabaseCursor OpenClDatabaseFile(ClFileBinding binding)
    {
        var alias = binding.Request.File.Split('/')[^1];
        var environment = _job is null ? null : EnvironmentForJob();
        var overridden = environment?.Resolve(alias);
        var (library, name) = overridden is null ? ResolveWorkObject(binding.Request.File, ObjectType.File) : (overridden.Library, overridden.Name);
        var definition = _files.GetDefinition(library, name) ?? throw new CpfException("CPF0860", "Overriding file is not a database file.");
        var current = DescribeClDatabaseFile(binding.Request, library, name, definition);
        if (current.FormatSignature != binding.Definition.FormatSignature || !current.Fields.SequenceEqual(binding.Definition.Fields))
            throw new CpfException("CPF4131", "Database record format changed; recompile the CL program.");
        var member = overridden?.Member ?? _files.FirstMember(library, name);
        DatabaseRecordCursor Create()
        {
            var cursor = _files.OpenSequentialCursor(library, name, member, cancellationToken: _cancellationToken);
            try
            {
                // Recheck after the cursor holds its file allocation: a schema change
                // between the initial description and opening must not alter the CL ABI.
                var opened = _files.GetDefinition(library, name) ?? throw new CpfException("CPF4131", "Database file disappeared while opening.");
                var layout = DescribeClDatabaseFile(binding.Request, library, name, opened);
                if (layout.FormatSignature != binding.Definition.FormatSignature || !layout.Fields.SequenceEqual(binding.Definition.Fields))
                    throw new CpfException("CPF4131", "Database record format changed; recompile the CL program.");
                return cursor;
            }
            catch { cursor.Dispose(); throw; }
        }
        if (environment is null)
        {
            var cursor = Create(); return new(() => Receive(cursor), cursor.Dispose);
        }
        var path = environment.OpenPath("CLINPUT:" + library + "/" + name + "/" + member, overridden?.Share ?? false,
            overridden?.Scope ?? JobEnvironmentScope.Call, Create);
        return new(() => Receive(path.Value), path.Dispose, path.Close);

        ClFileReadResult? Receive(DatabaseRecordCursor cursor)
        {
            var row = cursor.Read(); if (row is null) return null;
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase); var nullError = false;
            var encoding = (Encoding)CodePage.FromCcsid(_job?.Ccsid ?? _system.Config.Ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
            foreach (var field in definition.PrimaryFormat.Fields)
            {
                var target = binding.Definition.Fields.Single(f => f.Name == field.Name);
                var value = row[field.Name];
                if (value is null)
                {
                    nullError |= !binding.Request.AllowNull;
                    value = field.Type is FieldType.Alpha or FieldType.Date or FieldType.Time or FieldType.Timestamp ? "" : target.Type == "*LGL" ? (object)false : 0m;
                }
                else if (field.Type == FieldType.Date) value = FileDate(value).ToString(field.Length == 8 ? "yyyyMMdd" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
                else if (field.Type == FieldType.Time)
                {
                    var time = value is TimeSpan span ? span : TimeSpan.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
                    value = time.ToString(field.Length == 6 ? "hhmmss" : @"hh\.mm\.ss", CultureInfo.InvariantCulture);
                }
                else if (field.Type == FieldType.Timestamp) value = FileDate(value).ToString(field.Length == 14 ? "yyyyMMddHHmmss" : "yyyy-MM-dd-HH.mm.ss.ffffff", CultureInfo.InvariantCulture);
                if (field.Type == FieldType.Binary && target.Type != "*INT" && Math.Abs(Convert.ToDecimal(value, CultureInfo.InvariantCulture)).ToString("0", CultureInfo.InvariantCulture).TrimStart('0').Length > ClFileDigits(field))
                    throw new CpfException("CPF0863", "Binary file value exceeds the declared decimal digits.");
                if (target.Type == "*CHAR" && field.Type is FieldType.Packed or FieldType.Zoned or FieldType.Binary)
                {
                    var bytes = new byte[target.Length];
                    new RecordCodec().WriteValue(bytes, new FieldSpec { Name = field.Name, Type = FieldType.Packed, Length = ClFileDigits(field), Decimals = field.Decimals, Position = 1 }, value);
                    value = new ProgramBuffer(bytes, _job?.Ccsid ?? _system.Config.Ccsid);
                }
                else if (target.Type == "*CHAR")
                {
                    try
                    {
                        var bytes = encoding.GetBytes((string)value);
                        if (bytes.Length > field.Length) throw new CpfException("CPF5029", "Character translation exceeds the declared file field.");
                        if (field.VariableLength)
                        {
                            var buffer = new byte[target.Length]; buffer.AsSpan(2).Fill(encoding.GetBytes(" ")[0]);
                            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buffer, checked((ushort)bytes.Length));
                            bytes.CopyTo(buffer, 2); value = new ProgramBuffer(buffer, _job?.Ccsid ?? _system.Config.Ccsid);
                        }
                    }
                    catch (EncoderFallbackException) { throw new CpfException("CPF5029", "File field cannot be represented in the job CCSID."); }
                }
                values[field.Name] = value;
            }
            return new(values, nullError ? "CPF0886" : null, nullError ? "Record contains null fields; default CL values were supplied." : null);
        }
        static DateTime FileDate(object value) => value is DateTimeOffset offset ? offset.DateTime : Convert.ToDateTime(value, CultureInfo.InvariantCulture);
    }
}
