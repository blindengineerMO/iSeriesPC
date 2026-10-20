using Ipc.Db.Sql;
using Ipc.Terminal;

namespace Ipc.Session.Sql;

internal sealed partial class InteractiveSqlScreen
{
    private void Paint()
    {
        Buffer.ClearScreen();
        if(_view=="Entry")PaintEntry();else if(_view=="Data")PaintData();else if(_view=="Help")PaintHelp();else PaintForm();
        Status();
    }
    private void Put(int row,int column,string text,int maximum=int.MaxValue,DisplayAttribute attributes=DisplayAttribute.None)
    {
        var count=Math.Min(Math.Min(text.Length,maximum),Buffer.Columns-column+1);
        for(var i=0;i<count;i++)Buffer.Set(row,column+i,TerminalGlyph.IsSingleCell(text[i]) ? text[i] : '?',attributes);
    }
    private void Heading(string title) { Put(1,Math.Max(1,(Buffer.Columns-title.Length)/2+1),title,attributes:DisplayAttribute.HighIntensity); }
    private void Status() { Buffer.ClearRegion(Buffer.Rows,1,1,Buffer.Columns);Put(Buffer.Rows,1,_status,Buffer.Columns,DisplayAttribute.HighIntensity); }
    private void Footer(string first,string second) { Put(Buffer.Rows-2,2,first);Put(Buffer.Rows-1,2,second); }
    private void PaintEntry()
    {
        Heading("Enter SQL Statements");Put(2,2,"Type SQL statement, press Enter.");
        var historyStart=Math.Max(0,(_historyChoice>=0 ? _historyChoice : _history.Count-1)-2);var row=4;
        for(var index=historyStart;index<_history.Count && row<=10;index++)
        {
            Put(row++,2,"> "+_history[index].Replace('\n',' '),Buffer.Columns-3,index==_historyChoice ? DisplayAttribute.ReverseVideo : DisplayAttribute.None);
            if(row<=10)Put(row++,4,index>=_history.Count-_messages.Count ? _messages[index-(_history.Count-_messages.Count)] : "Saved statement.");
        }
        Put(11,2,"Statement:");var text=Draft;var line=text.AsSpan(0,_cursor).Count('\n');
        var last=text.AsSpan(0,_cursor).LastIndexOf('\n');var column=_cursor-last-1;var available=Buffer.Rows-16;_top=Math.Clamp(line-available+1,0,Math.Max(0,line));
        var lines=text.Split('\n');var shift=Math.Max(0,column-(Buffer.Columns-10)+1);
        for(var index=_top;index<lines.Length && index<_top+available;index++)
        {
            var currentRow=12+index-_top;Put(currentRow,2,(index+1).ToString("D3")+">");var value=lines[index];
            Put(currentRow,8,value.Length>shift ? value[shift..] : "",Buffer.Columns-9,DisplayAttribute.Underline);
        }
        Footer(_moreKeys ? "F13=Services  F24=More keys" : "F3=Exit  F4=Prompt  F6=Insert line  F9=Retrieve",_moreKeys ? "Roll keys=Select history  Enter=Run statement" : "F10=Copy line  F12=Cancel  F13=Services  F24=More keys");
        Buffer.MoveCursor(12+line-_top,Math.Clamp(8+column-shift,1,Buffer.Columns));
    }
    private void PaintData()
    {
        Heading("Display Data");Put(2,2,"Data width . . . : "+(_widths.Sum()+2*_widths.Length));
        Put(3,2,"Position to line . . :");Put(3,42,"Shift to column . . :");
        _form=new(Buffer,[new() { Row=3,Column=24,Length=10,Usage=FieldUsage.Numeric },new() { Row=3,Column=65,Length=10,Usage=FieldUsage.Numeric }]);_editor=new(_form);_editor.ApplyEdit(CursorEdit.Insert);
        _form.WriteValue(0,(_data!.First+1).ToString());_form.WriteValue(1,(_shift+1).ToString());
        var position=0;
        for(var field=0;field<_widths.Length;field++)
        {
            var width=_widths[field];var left=Math.Max(position,_shift);var right=Math.Min(position+width,_shift+Buffer.Columns-2);
            if(left<right)
            {
                DrawValue(5,_data.Columns[field].Name,width,false,position,left,right);
                foreach(var line in Enumerable.Range(0,_data.Rows.Count))
                    DrawValue(6+line,_data.Rows[line][field],width,_data.Columns[field].Type.Kind is SqlQueryKind.SmallInt or SqlQueryKind.Integer or SqlQueryKind.BigInt or SqlQueryKind.Decimal or SqlQueryKind.Numeric or SqlQueryKind.Real or SqlQueryKind.Double,position,left,right);
            }
            position+=width+2;
        }
        Put(Buffer.Rows-3,Math.Max(2,Buffer.Columns-12),_data.More ? "More..." : "Bottom");
        Footer(_moreKeys ? "F5=Refresh  F24=More keys" : "F3=Exit  F12=Cancel  F19=Left  F20=Right  F24=More keys","Page Up/Down=Scroll  Enter=Position");
        _form.Activate(0);_form.Active.CursorOffset=0;
    }
    private void DrawValue(int row,string value,int width,bool numeric,int start,int left,int right)
    {
        var padding=numeric ? Math.Max(0,width-value.Length) : 0;
        for(var offset=left;offset<right;offset++)
        {
            var index=offset-start-padding;var character=index>=0 && index<value.Length ? value[index] : ' ';
            Buffer.Set(row,2+offset-_shift,TerminalGlyph.IsSingleCell(character) ? character : '?');
        }
    }
    private void PaintForm()
    {
        Heading(_title);Put(2,2,"Type choices, press Enter.");
        var page=_fields.Skip(_formPage*8).Take(8).ToArray();
        _form=new(Buffer,page.Select((field,index)=>new InputField { Row=4+index*2,Column=29,Length=field.Key=="OPTION" ? 1 : 32768,DisplayLength=Buffer.Columns-31,TabOrder=index }));_editor=new(_form);_editor.ApplyEdit(CursorEdit.Insert);
        for(var index=0;index<page.Length;index++) { Put(4+index*2,2,page[index].Label,25);_form.WriteValue(index,page[index].Value); }
        if(_view=="Exit")
        {
            Put(7,8,"1=Save and exit session");Put(8,8,"2=Exit without saving session");Put(9,8,"3=Resume session");Put(10,8,"4=Save session in source file");
        }
        if(_view=="Services")Put(7,8,"1. Change session attributes");
        Footer("F1=Help  F12=Cancel",_fields.Length>8 ? "Page Up/Down=More attributes" : "");_form.Activate(0);_form.Active.CursorOffset=0;
    }
    private void PaintHelp()
    {
        if (_status.StartsWith("SQL", StringComparison.Ordinal) || _status.StartsWith("CPF", StringComparison.Ordinal) || _status.StartsWith("IPC", StringComparison.Ordinal))
        {
            Heading("Additional Message Information");
            var remaining=_status;
            for (var row=4;remaining.Length>0 && row<Buffer.Rows-3;row++)
            {
                var count=Math.Min(Buffer.Columns-4,remaining.Length);
                if(count<remaining.Length && remaining.LastIndexOf(' ',count-1,count) is var space && space>0)count=space;
                Put(row,2,remaining[..count]);remaining=remaining[count..].TrimStart();
            }
            Footer("F3=Return  F12=Return","");Buffer.MoveCursor(1,1);return;
        }
        Heading(_queryOnly ? "Query Output Help" : "Interactive SQL Help");var lines=_queryOnly ? new[] {
            "Use roll keys to scroll rows; F19/F20 shift columns.","Enter a line/column position to jump within the result.",
            "F5 refreshes the query using the current database records.","F3/F12 closes query output and returns to the caller.","F24 shows more function keys." } : new[] {
            "Enter a SQL statement and press Enter to run it.","F4 builds a SELECT statement using clause fields.","F6 inserts a statement line. F10 copies the current line.",
            "Use arrow keys, Home/End, Insert, Delete and Backspace to edit.","F9 retrieves the last statement or a selected history statement.","Use roll keys to select a history entry, then press F9.",
            "Results: roll keys scroll rows; F19/F20 shift columns.","Enter a line/column position to jump within the result.","F3/F12 returns from results to statement entry.",
            "F13 opens session services; option 1 changes attributes.","F3 opens the exit choices: save, discard, resume or source file.","F12 returns without losing the current statement." };
        for(var index=0;index<lines.Length;index++)Put(4+index,2,lines[index]);Footer("F3=Return  F12=Return","");Buffer.MoveCursor(1,1);
    }
    private void OpenPrompt()
    {
        try
        {
            var clauses=SqlSelectPrompt.Read(Draft);
            OpenForm("Prompt","Specify SELECT Statement",[("SELECT","Select",clauses[0]),("FROM","From",clauses[1]),("WHERE","Where",clauses[2]),("GROUP","Group by",clauses[3]),("HAVING","Having",clauses[4]),("ORDER","Order by",clauses[5]),("FETCH","Fetch first",clauses[6])]);
        }
        catch(ArgumentException error) { Error(error.Message); }
    }
}
