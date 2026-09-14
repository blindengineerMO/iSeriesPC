using System.Text;

namespace Ipc.Core.Text;

public static class CodePage
{
    static CodePage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public const int EbcdicUsCanada = 37;
    public const int EbcdicInternational = 500;
    public const int EbcdicLatin1 = 1047;
    public const int Pc850 = 850;
    public const int Latin1 = 819;
    public const int Utf8 = 1208;
    public const int Ascii = 367;

    public const int DefaultSystem = EbcdicUsCanada;

    public static bool IsSupported(int ccsid) => ccsid switch
    {
        EbcdicUsCanada or EbcdicInternational or EbcdicLatin1 or Pc850 or Latin1 or Utf8 or Ascii => true,
        _ => false,
    };

    public static Encoding FromCcsid(int ccsid) => ccsid switch
    {
        Utf8 => new UTF8Encoding(false),
        Ascii => Encoding.GetEncoding(20127),
        _ => Encoding.GetEncoding(ccsid),
    };

    public static byte[] ToBytes(int ccsid, string text) => FromCcsid(ccsid).GetBytes(text);

    public static string FromBytes(int ccsid, byte[] bytes, int index, int count) =>
        FromCcsid(ccsid).GetString(bytes, index, count);

    public static string FromBytes(int ccsid, ReadOnlySpan<byte> bytes) =>
        FromCcsid(ccsid).GetString(bytes);
}

public sealed class EbcdicTextStream
{
    private readonly int _ccsid;

    public EbcdicTextStream(int ccsid = CodePage.DefaultSystem)
    {
        _ccsid = ccsid;
    }

    public byte[] Encode(string text, int fixedWidth, char pad = ' ') =>
        CodePage.ToBytes(_ccsid, PadTo(text, fixedWidth, pad));

    public string Decode(byte[] bytes, int index, int count) =>
        CodePage.FromBytes(_ccsid, bytes, index, count);

    public string Decode(ReadOnlySpan<byte> bytes) => CodePage.FromBytes(_ccsid, bytes);

    private static string PadTo(string text, int width, char pad)
    {
        if (text.Length >= width)
        {
            return text[..width];
        }

        return text + new string(pad, width - text.Length);
    }
}