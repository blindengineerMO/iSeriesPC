using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Db.Definitions;

namespace Ipc.Db.Sql;

public sealed record SqlConversionOptions(string DateFormat="*ISO",string DateSeparator="-",string TimeFormat="*HMS",string TimeSeparator=":",bool DecimalComma=false)
{
    public string FormatDate(DateOnly day)
    {
        var culture=CultureInfo.InvariantCulture;
        return DateFormat switch {
            "*USA"=>day.ToString("MM/dd/yyyy",culture),"*EUR"=>day.ToString("dd.MM.yyyy",culture),"*ISO" or "*JIS"=>day.ToString("yyyy-MM-dd",culture),
            "*MDY"=>day.ToString("MM",culture)+DateSeparator+day.ToString("dd",culture)+DateSeparator+day.ToString("yy",culture),
            "*DMY"=>day.ToString("dd",culture)+DateSeparator+day.ToString("MM",culture)+DateSeparator+day.ToString("yy",culture),
            "*JUL"=>day.ToString("yy",culture)+DateSeparator+day.DayOfYear.ToString("D3",culture),
            _=>day.ToString("yy",culture)+DateSeparator+day.ToString("MM",culture)+DateSeparator+day.ToString("dd",culture) };
    }
    public string FormatTime(TimeOnly time)
    {
        if(TimeFormat=="*USA")return time.ToString("hh:mm tt",CultureInfo.InvariantCulture);
        var separator=TimeFormat is "*ISO" or "*EUR" ? "." : TimeFormat=="*JIS" ? ":" : TimeSeparator;
        return time.ToString("HH",CultureInfo.InvariantCulture)+separator+time.ToString("mm",CultureInfo.InvariantCulture)+separator+time.ToString("ss",CultureInfo.InvariantCulture);
    }
}

internal static class SqlInputConversion
{
    private static CultureInfo Culture()
    {
        var culture=(CultureInfo)CultureInfo.InvariantCulture.Clone();culture.DateTimeFormat.Calendar.TwoDigitYearMax=2049;return culture;
    }
    internal static object? Normalize(FieldType type,object? value,SqlConversionOptions? options)
    {
        if(options is null || value is not string text)return value;
        if(type is FieldType.Binary or FieldType.Packed or FieldType.Zoned or FieldType.Float)return options.DecimalComma ? text.Replace(',','.') : text;
        if(type==FieldType.Date)
        {
            var formats=new List<string> { "yyyy-MM-dd","MM/dd/yyyy","M/d/yyyy","dd.MM.yyyy","d.M.yyyy" };
            var separator="'"+options.DateSeparator.Replace("'","''")+"'";
            formats.Add(options.DateFormat switch { "*MDY"=>"MM"+separator+"dd"+separator+"yy","*DMY"=>"dd"+separator+"MM"+separator+"yy","*YMD"=>"yy"+separator+"MM"+separator+"dd",_=>"yyyy-MM-dd" });
            if(DateOnly.TryParseExact(text,formats.ToArray(),Culture(),DateTimeStyles.None,out var day))return day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            if(options.DateFormat=="*JUL" && text.Length==5+options.DateSeparator.Length && text.Substring(2,options.DateSeparator.Length)==options.DateSeparator && int.TryParse(text[..2],out var year) && int.TryParse(text[(2+options.DateSeparator.Length)..],out var ordinal))
            {
                year=Culture().Calendar.ToFourDigitYear(year);if(ordinal>=1 && ordinal<=(DateTime.IsLeapYear(year) ? 366 : 365))return new DateOnly(year,1,1).AddDays(ordinal-1).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            }
            throw new CpfException("SQL0180","Invalid date representation.");
        }
        if(type==FieldType.Time)
        {
            var separator="'"+options.TimeSeparator.Replace("'","''")+"'";
            if(TimeOnly.TryParseExact(text,["HH:mm:ss","HH.mm.ss","hh:mmtt","hh:mm tt","h:mmtt","h:mm tt","HH"+separator+"mm"+separator+"ss"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var time))return time.ToString("HH:mm:ss",CultureInfo.InvariantCulture);
            throw new CpfException("SQL0180","Invalid time representation.");
        }
        return value;
    }
}
