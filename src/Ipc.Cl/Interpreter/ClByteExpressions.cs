using Ipc.Core.Work;
using System.Buffers.Binary;
using System.Text;
using Ipc.Core.Text;

namespace Ipc.Cl.Interpreter;

public sealed partial class ClExpression
{
    internal bool IsStorageTarget => _root is ByteFunction;
    internal void AssignStorage(object value, Func<string, object> read, Action<string, object> write, int ccsid)
    {
        if (_root is not ByteFunction function) throw new ClRuntimeException("Invalid byte-storage assignment target.");
        function.Assign(value, read, write, ccsid);
    }
    internal IEnumerable<string> CharacterStorageVariables => StorageVariables(_root);
    private static IEnumerable<string> StorageVariables(Node node)
    {
        if (node is ByteFunction function) yield return function.Name;
        var children = node switch {
            ByteFunction f => new[] { f.Start, f.Length }, Binary b => new[] { b.Left, b.Right },
            Unary u => new[] { u.Operand }, _ => Array.Empty<Node?>() };
        foreach (var child in children) if (child is not null) foreach (var name in StorageVariables(child)) yield return name;
    }

    private sealed record BinaryLiteral(byte[] Bytes) : Node(1)
    {
        public override object Evaluate(Func<string, object> variable, int ccsid) => new ProgramBuffer(Bytes, ccsid);
    }
    private sealed record ByteFunction(string Name, Node? Start, Node? Length, bool BinaryValue)
        : Node(Math.Max(Start?.Depth ?? 0, Length?.Depth ?? 0) + 1)
    {
        private (byte[] Bytes, int Offset, int Count) Select(Func<string, object> read, int ccsid)
        {
            var value = read(Name);
            var bytes = value switch { ProgramBuffer raw => raw.ToArray(), string text => EncodingFor(ccsid).GetBytes(text),
                _ => throw new ClRuntimeException("Byte functions require character storage.") };
            int Dimension(Node expression)
            {
                var number = Number(expression.Evaluate(read, ccsid));
                if (number != decimal.Truncate(number) || number is < 1 or > 32767) throw new ClRuntimeException("Byte positions and lengths must be positive integers.");
                return (int)number;
            }
            var start = Start is null ? 1 : Dimension(Start); var length = Length is null ? bytes.Length : Dimension(Length);
            if (bytes.Length > 32767 || start > bytes.Length || length > bytes.Length - start + 1 || BinaryValue && length is not (2 or 4))
                throw new ClRuntimeException("Byte-function range exceeds character storage or has an invalid binary width.");
            return (bytes, start - 1, length);
        }
        public override object Evaluate(Func<string, object> variable, int ccsid)
        {
            var (bytes, offset, count) = Select(variable, ccsid);
            if (!BinaryValue) return new ProgramBuffer(bytes.AsSpan(offset, count), ccsid);
            return count == 2 ? (decimal)BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset, count)) : BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, count));
        }
        internal void Assign(object value, Func<string, object> read, Action<string, object> write, int ccsid)
        {
            var (bytes, offset, count) = Select(read, ccsid); var target = bytes.AsSpan(offset, count);
            if (BinaryValue)
            {
                var number = decimal.Truncate(Number(value));
                var minimum = count == 2 ? short.MinValue : int.MinValue; var maximum = count == 2 ? short.MaxValue : int.MaxValue;
                if (number < minimum || number > maximum) throw new ClRuntimeException("Binary assignment overflow.", "MCH1210");
                if (count == 2) BinaryPrimitives.WriteInt16BigEndian(target, (short)number);
                else BinaryPrimitives.WriteInt32BigEndian(target, (int)number);
            }
            else
            {
                var data = value is ProgramBuffer raw ? raw.ToArray() : EncodingFor(ccsid).GetBytes(value is decimal
                    ? new ClVariableDefinition("*CHAR", count, 0).Assign(value, ccsid) : Text(value));
                target.Fill(EncodingFor(ccsid).GetBytes(" ")[0]); data.AsSpan(0, Math.Min(data.Length, count)).CopyTo(target);
            }
            write(Name, new ProgramBuffer(bytes, ccsid));
        }
        private static Encoding EncodingFor(int ccsid)
        {
            var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback; return encoding;
        }
    }
}
