using System.Globalization;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;

namespace Ipc.Session.Sql;

internal sealed record InteractiveSqlSettings
{
    public string Commit { get; init; } = "*NONE";
    public string Naming { get; init; } = "*SYS";
    public string Process { get; init; } = "*RUN";
    public string Library { get; init; } = "*LIBL";
    public string ListType { get; init; } = "*ALL";
    public string Refresh { get; init; } = "*ALWAYS";
    public string CopyData { get; init; } = "*YES";
    public string DateFormat { get; init; } = "*JOB";
    public string DateSeparator { get; init; } = "*JOB";
    public string TimeFormat { get; init; } = "*HMS";
    public string TimeSeparator { get; init; } = "*JOB";
    public string DecimalPoint { get; init; } = "*JOB";
    public string SortSequence { get; init; } = "*JOB";
    public string Language { get; init; } = "*JOB";
    public string ProgramLanguage { get; init; } = "*NONE";
    public string StringDelimiter { get; init; } = "*QUOTESQL";
    internal static InteractiveSqlSettings FromCall(CommandCall call)
    {
        var names = new[] { "COMMIT", "NAMING", "PROCESS", "LIBOPT", "LISTTYPE", "REFRESH" };
        string Value(string name, string fallback)
        {
            var position = Array.IndexOf(names, name); var raw = call.GetOption(name);
            if (position >= 0 && call.Positional.Count > position)
            { if (raw is not null) throw new CpfException("IPC0003", "Repeated " + name + "."); raw = call.Positional[position]; }
            return CommandParser.Unquote(raw ?? fallback).ToUpperInvariant();
        }
        var settings = new InteractiveSqlSettings {
            Commit=Value("COMMIT","*NONE"),Naming=Value("NAMING","*SYS"),Process=Value("PROCESS","*RUN"),Library=Value("LIBOPT","*LIBL"),ListType=Value("LISTTYPE","*ALL"),Refresh=Value("REFRESH","*ALWAYS"),
            CopyData=Value("ALWCPYDTA","*YES"),DateFormat=Value("DATFMT","*JOB"),DateSeparator=Value("DATSEP","*JOB"),TimeFormat=Value("TIMFMT","*HMS"),TimeSeparator=Value("TIMSEP","*JOB"),
            DecimalPoint=Value("DECPNT","*JOB"),SortSequence=Value("SRTSEQ","*JOB"),Language=Value("LANGID","*JOB"),ProgramLanguage=Value("PGMLNG","*NONE"),StringDelimiter=Value("SQLSTRDLM","*QUOTESQL") };
        settings.Validate(); return settings;
    }
    internal void Validate()
    {
        Choice(Commit,"COMMIT","*NONE","*NC","*CHG","*UR","*CS","*ALL","*RS","*RR");
        Choice(Naming,"NAMING","*SYS","*SQL"); Choice(Process,"PROCESS","*RUN","*VLD","*SYN");
        if (!ObjectName.IsValid(Library) && Library is not ("*LIBL" or "*CURLIB" or "*USRLIBL" or "*ALLUSR" or "*ALL")) throw Bad("LIBOPT");
        Choice(ListType,"LISTTYPE","*ALL","*SQL"); Choice(Refresh,"REFRESH","*ALWAYS","*FORWARD"); Choice(CopyData,"ALWCPYDTA","*YES","*NO","*OPTIMIZE");
        if (Refresh=="*ALWAYS" && CopyData=="*OPTIMIZE") throw new CpfException("SQL0104","REFRESH(*ALWAYS) cannot use ALWCPYDTA(*OPTIMIZE).");
        Choice(DateFormat,"DATFMT","*JOB","*USA","*ISO","*EUR","*JIS","*MDY","*DMY","*YMD","*JUL");
        Choice(DateSeparator,"DATSEP","*JOB","/",".",",","-"," ","*BLANK"); Choice(TimeFormat,"TIMFMT","*HMS","*USA","*ISO","*EUR","*JIS");
        Choice(TimeSeparator,"TIMSEP","*JOB",":",".",","," ","*BLANK"); Choice(DecimalPoint,"DECPNT","*JOB","*SYSVAL","*PERIOD","*COMMA");
        Choice(ProgramLanguage,"PGMLNG","*NONE","*C","*CBL","*PLI","*RPG","*FTN"); Choice(StringDelimiter,"SQLSTRDLM","*QUOTESQL","*APOSTSQL");
        if (ProgramLanguage!="*NONE" && Process!="*SYN") throw new CpfException("SQL0104","PGMLNG requires PROCESS(*SYN).");
    }
    internal void RequireSupported()
    {
        Validate();
        if (Commit is not ("*NONE" or "*NC")) throw new CpfException("SQL0104","Native SQL commitment modes remain unfinished.");
        if (CopyData=="*NO") throw new CpfException("SQL0104","ALWCPYDTA(*NO) requires native access-plan validation that remains unfinished.");
        if (SortSequence is not ("*HEX" or "*JOB" or "*JOBRUN") || Language is not ("*JOB" or "*JOBRUN")) throw new CpfException("SQL0104","Non-hex sort sequences and language collations remain unfinished.");
        if (ProgramLanguage!="*NONE" || StringDelimiter!="*QUOTESQL") throw new CpfException("SQL0104","Embedded-language syntax modes remain unfinished.");
    }
    internal SqlNaming SqlNaming => Naming=="*SYS" ? SqlNaming.System : SqlNaming.Sql;
    internal static void Choice(string value, string name, params string[] allowed) { if (!allowed.Contains(value)) throw Bad(name); }
    private static CpfException Bad(string name) => new("SQL0104","Invalid STRSQL "+name+" value.");
}

internal sealed class InteractiveSqlFormatter(InteractiveSqlSettings settings, string jobDateFormat, string jobDateSeparator, string jobTimeSeparator, bool jobDecimalComma)
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    internal string Value(object? value, SqlQueryType type)
    {
        if (value is null) return "-";
        var text = Convert.ToString(value,Culture) ?? "";
        if (type.Kind==SqlQueryKind.Date)
        {
            var day=DateOnly.ParseExact(text,"yyyy-MM-dd",Culture); var format=settings.DateFormat=="*JOB" ? jobDateFormat : settings.DateFormat;
            var separator=settings.DateSeparator=="*JOB" ? jobDateSeparator : settings.DateSeparator=="*BLANK" ? " " : settings.DateSeparator;
            return format switch { "*USA"=>day.ToString("MM/dd/yyyy",Culture),"*EUR"=>day.ToString("dd.MM.yyyy",Culture),"*ISO" or "*JIS"=>text,
                "*MDY"=>day.ToString("MM",Culture)+separator+day.ToString("dd",Culture)+separator+day.ToString("yy",Culture),
                "*DMY"=>day.ToString("dd",Culture)+separator+day.ToString("MM",Culture)+separator+day.ToString("yy",Culture),
                "*JUL"=>day.ToString("yy",Culture)+separator+day.DayOfYear.ToString("D3",Culture),
                _=>day.ToString("yy",Culture)+separator+day.ToString("MM",Culture)+separator+day.ToString("dd",Culture) };
        }
        if (type.Kind==SqlQueryKind.Time)
        {
            var time=TimeOnly.ParseExact(text,"HH:mm:ss",Culture);
            if(settings.TimeFormat=="*USA")return time.ToString("hh:mm tt",Culture);
            var separator=settings.TimeFormat is "*ISO" or "*EUR" ? "." : settings.TimeFormat=="*JIS" ? ":" : settings.TimeSeparator=="*JOB" ? jobTimeSeparator : settings.TimeSeparator=="*BLANK" ? " " : settings.TimeSeparator;
            return time.ToString("HH",Culture)+separator+time.ToString("mm",Culture)+separator+time.ToString("ss",Culture);
        }
        var comma=settings.DecimalPoint=="*COMMA" || settings.DecimalPoint is "*JOB" or "*SYSVAL" && jobDecimalComma;
        return comma && type.Kind is SqlQueryKind.Decimal or SqlQueryKind.Numeric or SqlQueryKind.Real or SqlQueryKind.Double ? text.Replace('.',',') : text;
    }
}
