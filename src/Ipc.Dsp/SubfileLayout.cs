namespace Ipc.Dsp;

internal static class SubfileLayout
{
    internal static PanelKeyword? Keyword(PanelRecord record, string name) => record.Keywords.SingleOrDefault(k => k.Name == name);
    internal static int Number(PanelRecord record, string name) => int.Parse(Keyword(record, name)!.Arguments[0]);
    internal static void Validate(PanelDefinition definition, string source)
    {
        void Fail(PanelRecord r, string keyword, string message) => throw new PanelCompileException(source, r.Line, 45, keyword, message);
        foreach (var record in definition.Records)
        {
            foreach (var duplicate in record.Keywords.Where(k => k.Name.StartsWith("SFL", StringComparison.Ordinal) || k.Name == "WINDOW").GroupBy(k => k.Name).Where(g => g.Count() > 1))
                Fail(record, duplicate.Key, "Duplicate subfile/window keyword.");
            foreach (var marker in record.Keywords.Where(k => k.Name is "SFL" or "SFLCTL"))
                if (marker.Conditions.Length != 0) Fail(record, marker.Name, "Record relationships must be unconditional.");
            var subfile = Keyword(record, "SFL"); var control = Keyword(record, "SFLCTL"); var window = Keyword(record, "WINDOW");
            if (subfile is not null && control is not null) Fail(record, "SFLCTL", "A record cannot be both subfile and control.");
            if (subfile is not null && window is not null) Fail(record, "WINDOW", "Specify WINDOW on the subfile control record.");
            foreach (var keyword in record.Keywords.Where(k => k.Name.StartsWith("SFL", StringComparison.Ordinal)))
            {
                if (keyword.Name is "SFL" or "SFLNXTCHG" or "SFLMSGRCD") { if (subfile is null) Fail(record, keyword.Name, "Requires SFL."); }
                else if (control is null) Fail(record, keyword.Name, "Requires SFLCTL.");
            }
            var message = Keyword(record, "SFLMSGRCD");
            if (message is not null && Keyword(record, "SFLNXTCHG") is not null) Fail(record, "SFLNXTCHG", "Message subfiles do not return changed input records.");
            if (message is not null && (record.Fields.Count != 2 || !record.Fields[0].Keywords.Any(k => k.Name == "SFLMSGKEY") || !record.Fields[1].Keywords.Any(k => k.Name == "SFLPGMQ")))
                Fail(record, "SFLMSGRCD", "Message subfiles require exactly SFLMSGKEY followed by SFLPGMQ.");
            if (record.Fields.Any(f => f.Keywords.Any(k => k.Name is "SFLMSGKEY" or "SFLPGMQ")) && message is null)
                Fail(record, "SFLMSGKEY", "Predefined message fields require SFLMSGRCD.");
            var rows = definition.Rows; var columns = definition.Columns;
            if (window is not null)
            {
                var a = window.Arguments.Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
                if (a[0] + a[2] + 1 > rows || a[1] + a[3] + 3 > columns) Fail(record, "WINDOW", "Window and borders exceed the display.");
                rows = a[2] - (window.Arguments.Contains("*NOMSGLIN") ? 0 : 1); columns = a[3];
                foreach (var field in record.Fields.Where(f => f.Usage != PanelFieldUsage.Hidden))
                    if (field.Row > rows || field.Column + field.ScreenLength - 1 > columns) Fail(record, "WINDOW", "Field exceeds usable window area.");
            }
            if (control is null) continue;
            if (definition.Records.Count(r => Keyword(r, "SFLCTL")?.Arguments[0] == control.Arguments[0]) != 1) Fail(record, "SFLCTL", "Each subfile requires one control record.");
            var data = definition.Records.SingleOrDefault(r => r.Name == control.Arguments[0]);
            if (data is null || Keyword(data, "SFL") is null) Fail(record, "SFLCTL", "Referenced SFL record does not exist.");
            if (Keyword(record, "SFLSIZ") is null || Keyword(record, "SFLPAG") is null) Fail(record, "SFLCTL", "SFLSIZ and SFLPAG are required.");
            if (Number(record, "SFLPAG") > Number(record, "SFLSIZ")) Fail(record, "SFLPAG", "Page size exceeds subfile size.");
            var visible = data!.Fields.Where(f => f.Usage != PanelFieldUsage.Hidden).ToArray();
            var messageData = Keyword(data, "SFLMSGRCD");
            if (visible.Length == 0 && messageData is null) Fail(record, "SFL", "Subfile requires visible fields.");
            var top = messageData is null ? visible.Min(f => f.Row) : int.Parse(messageData.Arguments[0]);
            var height = messageData is null ? visible.Max(f => f.Row) - top + 1 : 1;
            var bottom = top + height * Number(record, "SFLPAG") - 1;
            if (bottom > rows || visible.Any(f => f.Column + f.ScreenLength - 1 > columns)) Fail(record, "SFLPAG", "Subfile page exceeds usable display/window area.");
            if (Keyword(record, "SFLEND") is not null && visible.Any(f => f.Row == visible.Max(v => v.Row) && f.Column + f.ScreenLength - 1 > columns - 7))
                Fail(record, "SFLEND", "Reserve the rightmost seven columns of the last subfile line for the More/Bottom marker.");
            if (record.Fields.Any(f => f.Usage != PanelFieldUsage.Hidden && f.Row >= top && f.Row <= bottom)) Fail(record, "SFLCTL", "Control fields overlap the subfile page.");
            if (Keyword(record, "SFLFOLD") is not null && Keyword(record, "SFLDROP") is not null) Fail(record, "SFLFOLD", "Choose SFLFOLD or SFLDROP.");
            foreach (var fold in record.Keywords.Where(k => k.Name is "SFLFOLD" or "SFLDROP"))
                if (definition.Keywords.Concat(record.Keywords).Any(k => DisplayDdsCompiler.FunctionKey(k.Name) && k.Name[2..] == fold.Arguments[0][2..]))
                    Fail(record, fold.Name, "Fold key is already assigned.");
        }
    }
}
