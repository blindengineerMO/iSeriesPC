using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Ipc.Cl.Interpreter;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Core.Work;

namespace Ipc.Api;

/// <summary>Checked byte layouts and prepared writes for borrowed API arguments.</summary>
internal sealed class ApiArguments(int ccsid)
{
    internal sealed record Output(ProgramArgument Target, ProgramBuffer Buffer);
    internal sealed record Assignment(ProgramArgument Target, object? Value);

    internal byte[] Encode(string text)
    {
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        try { return encoding.GetBytes(text); }
        catch (EncoderFallbackException) { throw Bad("API text cannot be represented in the job CCSID."); }
    }
    internal ProgramBuffer Character(object? value, int minimum = 0)
    {
        if (value is ProgramArgument reference)
        {
            if (reference.Type is not ("*CHAR" or "*RAW")) throw Bad("API requires character storage.");
            value = (object?)reference.ToBuffer() ?? reference.Value;
        }
        if (value is ProgramConstant constant)
        {
            if (constant.Type is not ("*CHAR" or "*RAW")) throw Bad("API requires character storage.");
            value = constant.Buffer;
        }
        if (value is string text)
        {
            var data = Encode(text); var bytes = Enumerable.Repeat(Encode(" ")[0], Math.Max(minimum, data.Length)).ToArray();
            data.CopyTo(bytes, 0); value = new ProgramBuffer(bytes, ccsid);
        }
        if (value is not ProgramBuffer buffer || buffer.Length < minimum || buffer.Ccsid != ccsid) throw Bad("Invalid API character buffer length or CCSID.");
        return buffer;
    }
    internal string Text(object? value, int width) => new ProgramBuffer(Character(value, width).ToArray().AsSpan(0, width), ccsid).ToText().TrimEnd(' ');
    internal int Binary32(object? value)
    {
        if (value is ProgramArgument reference)
        {
            if (reference.Type == "*INT" ? reference.Length != 4 : reference.Type is not ("*CHAR" or "*RAW")) throw Bad("API requires a four-byte signed binary integer.");
            value = (object?)reference.ToBuffer() ?? reference.Value;
        }
        if (value is ProgramConstant constant)
        {
            if (constant.Type != "*RAW" && (constant.Type != "*INT" || constant.Length != 4)) throw Bad("Invalid binary API constant layout.");
            value = constant.Buffer;
        }
        if (value is ProgramBuffer buffer)
        {
            if (buffer.Length < 4) throw Bad("Binary API parameter requires four bytes.");
            return BinaryPrimitives.ReadInt32BigEndian(buffer.ToArray());
        }
        if (value is not (int or long or short or decimal)) throw Bad("API requires a signed binary integer.");
        var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if (number < int.MinValue || number > int.MaxValue || number != decimal.Truncate(number)) throw Bad("Binary API integer is out of range.");
        return (int)number;
    }
    internal int Packed(object? value, int digits)
    {
        if (value is ProgramArgument reference)
        {
            if (reference.Type == "*DEC" ? reference.Length != digits || reference.Decimals != 0 : reference.Type is not ("*CHAR" or "*RAW"))
                throw Bad("API numeric parameters require packed DEC(" + digits + ",0).");
            value = (object?)reference.ToBuffer() ?? reference.Value;
        }
        if (value is ProgramConstant constant)
        {
            if (constant.Type != "*RAW" && (constant.Type != "*DEC" || constant.Length != digits || constant.Decimals != 0)) throw Bad("Invalid packed API constant layout.");
            value = constant.Buffer;
        }
        if (value is ProgramBuffer buffer)
        {
            try { value = ClCallArgument.Bind(new("*RAW", buffer.Length, 0, buffer, buffer), "*DEC", digits).Value; }
            catch (ClRuntimeException) { throw Bad("Malformed packed API number."); }
        }
        if (value is not (decimal or int or long or short)) throw Bad("API requires a packed decimal number.");
        var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if (number != decimal.Truncate(number) || Math.Abs(number) >= Pow10(digits)) throw Bad("API number is outside its declared precision.");
        return (int)number;
    }
    internal Output CharacterOutput(object? value)
    {
        if (value is not ProgramArgument reference || reference.Constant is not null || reference.IsReadOnly) throw new CpfException("CPF3C90", "API receiver requires writable storage.");
        var buffer = Character(reference);
        if (reference.Length < 1 || reference.Length != buffer.Length) throw Bad("API receiver length does not match storage.");
        return new(reference, buffer);
    }
    internal ProgramArgument PackedOutput(object? value, int digits)
    {
        if (value is not ProgramArgument reference || reference.Constant is not null || reference.IsReadOnly) throw new CpfException("CPF3C90", "API receiver requires writable storage.");
        if (reference.Type == "*DEC")
        {
            if (reference.Length != digits || reference.Decimals != 0) throw Bad("Invalid packed API receiver layout.");
        }
        else if (CharacterOutput(reference).Buffer.Length != (digits + 2) / 2) throw Bad("Invalid packed API receiver size.");
        _ = PackedAssignment(reference, digits, 0); return reference;
    }
    internal Assignment PackedAssignment(ProgramArgument target, int digits, int value)
        => new(target, target.Normalize(target.Type == "*DEC" ? (object)(decimal)value : PackedBuffer(digits, value)));
    internal ProgramBuffer PackedBuffer(int digits, int value)
        => ClCallArgument.Compile("(" + value.ToString(CultureInfo.InvariantCulture) + " (*DEC " + digits + " 0))").Evaluate(_ => "", ccsid).Buffer;
    internal Assignment Prefix(Output output, byte[] bytes, int count)
    {
        if (count < 0 || count > output.Buffer.Length || count > bytes.Length) throw Bad("API receiver is too small.");
        var result = output.Buffer.ToArray(); bytes.AsSpan(0, count).CopyTo(result);
        return new(output.Target, output.Target.Normalize(new ProgramBuffer(result, ccsid)));
    }
    internal static void DistinctOutputs(IEnumerable<ProgramArgument> outputs)
    {
        var seen = new List<ProgramArgument>();
        foreach (var output in outputs)
        {
            if (seen.Any(previous => ProgramArgument.Overlaps(previous, output))) throw Bad("Overlapping API output arguments are unsupported.");
            seen.Add(output);
        }
    }
    internal static void DistinctErrorReceiver(ErrorReceiver? error, IReadOnlyList<object?> parameters, int errorIndex)
    {
        // Preserve these adapters' existing exact-cell error reporting. Distinct views
        // with intersecting byte ranges must not overwrite each other while reporting.
        if (error?.Target is { } target && parameters.Where((_, index) => index != errorIndex).Any(value => !ReferenceEquals(value, target) && ProgramArgument.Overlaps(value, target)))
            throw Bad("API error receiver partially overlaps another parameter.");
    }
    internal static void Apply(IEnumerable<Assignment> assignments)
    { foreach (var assignment in assignments) assignment.Target.Value = assignment.Value; }
    private static decimal Pow10(int digits) { var number = 1m; for (var i = 0; i < digits; i++) number *= 10; return number; }
    internal static CpfException Bad(string message) => new("CPF3C3A", message);

    internal sealed class ErrorReceiver
    {
        private readonly ApiArguments _arguments;
        private readonly Output? _output;
        private readonly int _provided;
        internal ProgramArgument? Target => _output?.Target;
        internal ErrorReceiver(ApiArguments arguments, object? value)
        {
            _arguments = arguments;
            var buffer = arguments.Character(value, 4);
            _provided = BinaryPrimitives.ReadInt32BigEndian(buffer.ToArray());
            if (_provided == 0) return;
            if (_provided < 8 || _provided > buffer.Length) throw new CpfException("CPF3CF1", "Invalid API error-code receiver size.");
            _output = arguments.CharacterOutput(value);
            _ = Success();
        }
        internal Assignment? Success()
        {
            if (_output is null) return null;
            var bytes = _output.Buffer.ToArray(); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), 0);
            return new(_output.Target, _output.Target.Normalize(new ProgramBuffer(bytes, _output.Buffer.Ccsid)));
        }
        internal bool Report(CpfException error)
        {
            if (_output is null) return false;
            var bytes = _output.Buffer.ToArray(); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), 16);
            var id = _arguments.Encode(error.MessageId);
            id.AsSpan(0, Math.Min(7, Math.Max(0, _provided - 8))).CopyTo(bytes.AsSpan(8));
            if (_provided >= 16) bytes[15] = 0;
            _output.Target.Value = _output.Target.Normalize(new ProgramBuffer(bytes, _output.Buffer.Ccsid)); return true;
        }
    }
}
