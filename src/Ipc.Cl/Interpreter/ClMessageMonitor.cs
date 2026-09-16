using System.Text;
using Ipc.Cl.Commands;
using Ipc.Core.Text;

namespace Ipc.Cl.Interpreter;

public sealed record ClMessageMonitor(IReadOnlyList<string> MessageIds, string? ComparisonData, int Handler, string? ComparisonHex = null)
{
    internal void Validate(int ccsid)
    {
        if (!CodePage.IsSupported(ccsid)) throw new ClRuntimeException("Unsupported job CCSID.");
        if (ComparisonHex is not null)
        {
            if (ComparisonHex.Length > 56 || ComparisonHex.Length % 2 != 0 || ComparisonHex.Any(c => !char.IsAsciiHexDigit(c)))
                throw new ClRuntimeException("Invalid hexadecimal MONMSG CMPDTA.");
            return;
        }
        if (ComparisonData is null) return;
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        try { if (encoding.GetByteCount(ComparisonData) > 28) throw new ClRuntimeException("MONMSG CMPDTA exceeds 28 bytes in the job CCSID."); }
        catch (EncoderFallbackException) { throw new ClRuntimeException("MONMSG CMPDTA is not representable in the job CCSID."); }
    }
    internal bool Matches(CommandResult error, int ccsid)
    {
        if (error.MessageId is not { } id || !MessageIds.Any(pattern => pattern.EndsWith("0000", StringComparison.Ordinal) ? id.StartsWith(pattern[..3], StringComparison.Ordinal)
            : pattern.EndsWith("00", StringComparison.Ordinal) ? id.StartsWith(pattern[..5], StringComparison.Ordinal) : id == pattern)) return false;
        if (ComparisonData is null && ComparisonHex is null) return true;
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        try
        {
            var comparison = ComparisonHex is not null ? Convert.FromHexString(ComparisonHex) : encoding.GetBytes(ComparisonData!);
            if (comparison.Length > 28) throw new ClRuntimeException("MONMSG CMPDTA exceeds 28 bytes in the job CCSID.");
            return (error.MessageDataBuffer?.ToArray() ?? encoding.GetBytes(error.MessageData ?? "")).AsSpan().StartsWith(comparison);
        }
        catch (EncoderFallbackException) { throw new ClRuntimeException("MONMSG data is not representable in the job CCSID."); }
    }
}
