using System.Globalization;
using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Core.Work;

namespace Ipc.Services.Messages;

public sealed record MessageOrigin(string JobName, string JobUser, string Program, string Recipient, DateTimeOffset Sent);

/// <summary>RCVMSG's OPM sender layouts. Unknown native instruction/module data
/// stays blank, with zero available statement numbers in the long format.</summary>
public static class MessageSenderLayout
{
    public static ProgramBuffer Encode(QueuedMessage message, int length, bool longFormat, int ccsid)
    {
        if (length < (longFormat ? 720 : 80) || length > 32767 || !CodePage.IsSupported(ccsid))
            throw new CpfException("IPC0003", "Invalid SENDER buffer length or CCSID.");
        if (message.SenderJob is < 0 or > 999999) throw new CpfException("IPC0003", "Sender job number exceeds its six-character layout.");
        var data = new string(' ', length).ToCharArray(); var origin = message.Origin;
        void Put(int offset, int width, string value)
        {
            if (value.Length > width) throw new CpfException("IPC0136", "Stored sender information exceeds its layout.");
            value.CopyTo(0, data, offset, value.Length);
        }
        Put(0, 10, origin?.JobName ?? ""); Put(10, 10, origin?.JobUser ?? "");
        Put(20, 6, message.SenderJob?.ToString("D6", CultureInfo.InvariantCulture) ?? "");
        var sent = (origin?.Sent ?? message.Sent).UtcDateTime;
        var date = "0" + sent.ToString("yyMMdd", CultureInfo.InvariantCulture);
        var time = sent.ToString("HHmmss", CultureInfo.InvariantCulture);
        var micros = ((sent.Ticks % TimeSpan.TicksPerSecond) / 10).ToString("D6", CultureInfo.InvariantCulture);
        var program = origin?.Program ?? ""; var recipient = origin?.Recipient ?? "";
        if (!longFormat)
        {
            Put(26, 12, program); Put(42, 7, date); Put(49, 6, time); Put(55, 10, recipient);
            Put(69, 1, program.Length == 0 ? "" : "0"); Put(70, 1, recipient.Length == 0 ? "" : "0");
            Put(71, 6, micros); if (length >= 87) Put(77, 10, message.Sender);
        }
        else
        {
            Put(26, 7, date); Put(33, 6, time);
            Put(39, 1, program.Length == 0 ? "" : "0"); Put(40, 1, recipient.Length == 0 ? "" : "0");
            Put(41, 12, program); Put(320, 4, "0000");
            if (recipient.Length != 0) { Put(354, 10, recipient); Put(640, 4, "0000"); }
            Put(674, 6, micros); Put(680, 10, message.Sender);
        }
        try
        {
            var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
            var bytes = encoding.GetBytes(data);
            if (bytes.Length != length) throw new CpfException("IPC0136", "Stored sender information is not single-byte text.");
            return new(bytes, ccsid);
        }
        catch (EncoderFallbackException) { throw new CpfException("IPC0136", "Stored sender information cannot be encoded in the job CCSID."); }
    }
}
