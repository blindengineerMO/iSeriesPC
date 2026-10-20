using Ipc.Core.Messages;

namespace Ipc.Db.Sql;

public sealed record SqlQueryDiagnostic(string MessageId, string SqlState, int SqlCode, string Message, long Occurrences = 1);

public static class SqlDiagnostics
{
    // IBM i SQL messages and codes, 7.4. The chosen state represents the
    // implemented reason when a native message has multiple possible states.
    public static CpfException Enrich(CpfException error)
    {
        if (error.SqlState is not null) return error;
        var state = error.MessageId switch {
            "SQL0104" => "42601", "SQL0120" => "42903", "SQL0122" => "42803", "SQL0125" => "42805",
            "SQL0130" => error.Message.Contains("requires one", StringComparison.OrdinalIgnoreCase) ? "22019" : "22025",
            "SQL0138" => "22011", "SQL0150" => "42807", "SQL0151" => "42808", "SQL0160" => "42813", "SQL0161" => "44000", "SQL0171" => "42815", "SQL0180" => "22007", "SQL0183" => "22008", "SQL0203" => "42702",
            "SQL0204" => "42704", "SQL0206" => "42703", "SQL0214" => "42822", "SQL0301" => "07006", "SQL0313" => "07001",
            "SQL0190" => "42837", "SQL0478" => "42893",
            "SQL0332" => "57017", "SQL0402" => "42819", "SQL0404" => "22001", "SQL0406" => "22003", "SQL0407" => "23502",
            "SQL0408" => "42821", "SQL0530" => "23503", "SQL0531" or "SQL0532" => "23504", "SQL0629" => "42834", "SQL0538" => "42830", "SQL0539" => "42888", "SQL0642" => "54021", "SQL7008" => "55019", "SQL0420" => "22018", "SQL0440" => "42884", "SQL0545" => "23513", "SQL0546" => "42621", "SQL0551" => "42501",
            "SQL0803" => "23505", "SQL0802" => error.Message.Contains("Division by zero", StringComparison.OrdinalIgnoreCase) ? "22012" :
                error.Message.Contains("overflow", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("out of range", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("exceeds", StringComparison.OrdinalIgnoreCase) ? "22003" : "22023",
            "SQL0901" => "58004", "SQL0904" or "SQL0971" => "57011", _ => "HY000" };
        var prefix = error.MessageId + ": "; var text = error.Message.StartsWith(prefix, StringComparison.Ordinal) ? error.Message[prefix.Length..] : error.Message;
        return new(error.MessageId, text, error.MessageType, error.Severity, state);
    }
}
