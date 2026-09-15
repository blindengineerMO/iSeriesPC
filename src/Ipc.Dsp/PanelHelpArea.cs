using Ipc.Core.Menu;
using Ipc.Terminal;

namespace Ipc.Dsp;

public sealed class PanelHelpArea
{
    public int Line { get; init; }
    public List<PanelKeyword> Keywords { get; init; } = new();
    internal bool Contains(PanelRecord record, CellPosition cursor)
    {
        var args = Keywords.Single(k => k.Name == "HLPARA").Arguments;
        if (args[0] == "*RCD") return true;
        if (args[0] == "*NONE") return false;
        if (args[0] == "*FLD")
        {
            var field = record.Fields.Single(f => f.Name == args[1]);
            return field.Usage != PanelFieldUsage.Hidden && cursor.Row == field.Row && cursor.Column >= field.Column && cursor.Column < field.Column + field.ScreenLength;
        }
        return cursor.Row >= int.Parse(args[0]) && cursor.Column >= int.Parse(args[1]) && cursor.Row <= int.Parse(args[2]) && cursor.Column <= int.Parse(args[3]);
    }
    internal static HelpRequest? Resolve(PanelDefinition definition, PanelRecord record, CellPosition cursor, IReadOnlyList<bool> indicators)
    {
        var target = record.HelpAreas.Where(a => a.Contains(record, cursor)).SelectMany(a => a.Keywords)
            .FirstOrDefault(k => k.Name == "HLPPNLGRP" && k.Enabled(indicators)) ?? definition.Keywords.LastOrDefault(k => k.Name == "HLPPNLGRP" && k.Enabled(indicators));
        return target is null ? null : new(target.Arguments[1], target.Arguments[0]);
    }
    internal static void Validate(PanelDefinition definition, string source)
    {
        foreach (var record in definition.Records)
        {
            void Fail(int line, string tag, string detail) => throw new PanelCompileException(source, line, 45, tag, detail);
            if (record.HelpAreas.Count == 0 && !definition.Keywords.Any(k => k.Name == "HLPPNLGRP")) continue;
            var all = definition.Keywords.Concat(record.Keywords).ToArray();
            if (!all.Any(k => k.Name == "HELP") || !all.Any(k => k.Name == "HLPTITLE")) Fail(record.Line, "HELP", "Panel-group help requires HELP and HLPTITLE.");
            if (record.HelpAreas.Count > 0 && record.Keywords.Any(k => k.Name == "SFLCTL" && definition.Records.Any(r => r.Name == k.Arguments[0] && r.Keywords.Any(m => m.Name == "SFLMSGRCD"))))
                Fail(record.Line, "HLPARA", "Message subfiles use the help context supplied by their program-message resolver.");
            if (record.Keywords.Any(k => k.Name == "SFL") && record.HelpAreas.Count > 0) Fail(record.Line, "HLPARA", "Help specifications belong on the subfile control.");
            foreach (var area in record.HelpAreas)
            {
                if (area.Keywords.Count(k => k.Name == "HLPARA") != 1 || area.Keywords.Count(k => k.Name == "HLPPNLGRP") != 1)
                    Fail(area.Line, "HLPARA", "Each H specification requires one HLPARA and one HLPPNLGRP.");
                var args = area.Keywords.Single(k => k.Name == "HLPARA").Arguments;
                if (args[0] == "*FLD" && !record.Fields.Any(f => !f.Constant && f.Name == args[1])) Fail(area.Line, "HLPARA", "Referenced field does not exist in this record.");
                if (args.Length == 4 && (int.Parse(args[0]) > int.Parse(args[2]) || int.Parse(args[1]) > int.Parse(args[3]) || int.Parse(args[2]) > definition.Rows || int.Parse(args[3]) > definition.Columns))
                    Fail(area.Line, "HLPARA", "Help rectangle exceeds the display or has reversed corners.");
            }
        }
    }
}
