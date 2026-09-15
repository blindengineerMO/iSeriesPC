using System.Text;
using Ipc.Core.Text;
using Ipc.Db.Definitions;

namespace Ipc.Db.Records;

public sealed partial class RecordCodec
{
    private static void ValidateRecord(RecordFormat format)
    {
        if (format.BufferLayoutVersion != 2 || format.RecordLength is < 1 or > 1048576 || format.Fields.Count is < 1 or > 1024)
            throw new ArgumentException("Record format exceeds supported buffer bounds.");
        long end = 0;
        foreach (var field in format.Fields)
        {
            if (field.Position <= end || (long)field.Position - 1 + field.StorageLength > format.RecordLength) throw new ArgumentException("Overlapping or out-of-bounds record field.");
            end = (long)field.Position - 1 + field.StorageLength;
        }
    }

    private static void ValidateField(byte[] buffer, FieldSpec field)
    {
        var valid = field.Type switch {
            FieldType.Alpha => field.Length is >= 1 and <= 32768 && CodePage.IsSupported(field.Ccsid),
            FieldType.Packed or FieldType.Zoned => field.Length is >= 1 and <= 29 && field.Decimals >= 0 && field.Decimals <= Math.Min(field.Length, 28),
            FieldType.Binary => field.Length is 2 or 4 or 8,
            FieldType.Float => field.Length is 4 or 8,
            FieldType.Logic => field.Length == 1,
            FieldType.Date => field.Length is 8 or 10,
            FieldType.Time => field.Length is 6 or 8,
            FieldType.Timestamp => field.Length is 14 or 26,
            _ => false,
        };
        if (!valid || field.VariableLength && field.Type != FieldType.Alpha) throw new ArgumentException("Unsupported record field dimensions or CCSID.");
        var width = field.StorageLength;
        if (field.Position < 1 || (long)field.Position - 1 + width > buffer.Length) throw new ArgumentException("Field exceeds the record buffer.");
    }

    private static byte[] EncodedText(int ccsid, string text, int width)
    {
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        var encoded = encoding.GetBytes(text);
        if (encoded.Length > width) throw new OverflowException("Character value exceeds its byte width.");
        var result = Enumerable.Repeat(encoding.GetBytes(" ")[0], width).ToArray();
        encoded.CopyTo(result, 0); return result;
    }

    private static bool NegativeSign(int sign) => sign switch {
        0xA or 0xC or 0xE or 0xF => false,
        0xB or 0xD => true,
        _ => throw new FormatException("Invalid decimal sign nibble."),
    };
}
