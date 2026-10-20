using System.Globalization;
using System.Text;
using Ipc.Db.Sql;
using Ipc.Dsp;
using Ipc.Terminal;

namespace Ipc.Session.Sql;

internal sealed partial class InteractiveSqlScreen
{
    private string _view="Entry", _returnView="Entry";
    private readonly List<string> _history;
    private readonly bool _queryOnly;
    private readonly List<string> _messages=[];
    private readonly StringBuilder _draft=new();
    private int _cursor, _top, _historyChoice=-1, _shift, _formPage;
    private bool _insert=true, _moreKeys, _pasteCr;
    private string _status="", _title="";
    private (string Key,string Label,string Value)[] _fields=[];
    private DisplayForm? _form; private FieldEditor? _editor;
    private InteractiveSqlPage? _data;
    private int[] _widths=[];
    internal DisplayBuffer Buffer { get; }
    internal string State=>_view=="Data" ? "SqlData" : "InteractiveSql";
    internal string Draft=>_draft.ToString();
    internal IReadOnlyList<string> History=>_history.AsReadOnly();
    internal InteractiveSqlSettings Settings { get; private set; }
    internal long First=>_data?.First ?? 0;
    internal int PageSize=>Buffer.Rows-9;
    internal InteractiveSqlScreen(InteractiveSqlSettings settings,IReadOnlyList<string> history,string draft,int rows=24,int columns=80,bool queryOnly=false)
    {
        if(rows<24 || rows>27 || columns<80 || columns>132)throw new ArgumentException("Unsupported SQL display size.");
        Buffer=new(rows,columns); Settings=settings; _queryOnly=queryOnly; _history=history.ToList(); _draft.Append(draft); _cursor=_draft.Length; Paint();
    }
    internal PanelResponse Handle(KeyPress key)
    {
        if(_view=="Help") { if(key.Aid is AidKey.Pf3 or AidKey.Pf12 or AidKey.Enter) { _view=_returnView;Paint(); } return Response(); }
        if(key.Aid==AidKey.Pf1) { if(_view is not ("Entry" or "Data"))Capture(); _returnView=_view;_view="Help";Paint();return Response(); }
        if(key.Aid==AidKey.Pf24) { if(_view is not ("Entry" or "Data"))Capture(); _moreKeys=!_moreKeys;Paint();return Response(); }
        if(_view=="Data")return DataKey(key);
        if(_view!="Entry")return FormKey(key);
        if(key.Aid==AidKey.Pf3) { OpenForm("Exit","Exit Interactive SQL",[("OPTION","Option","1")]);return Response(); }
        if(key.Aid is AidKey.Pf12 or AidKey.Clear) { _draft.Clear();_cursor=0;_top=0;_historyChoice=-1;_status="Statement cleared.";Paint();return Response(); }
        if(key.Aid==AidKey.Enter)return Response("RUN",new Dictionary<string,object?> { ["SQL"]=Draft });
        if(key.Aid==AidKey.Pf9)
        {
            if(_history.Count>0) { SetDraft(_history[_historyChoice>=0 ? _historyChoice : _history.Count-1]);_historyChoice=-1;_status="Statement retrieved.";Paint(); }
            return Response();
        }
        if(key.Aid==AidKey.Pf13) { OpenForm("Services","Interactive SQL Session Services",[("OPTION","Option","1")]);return Response(); }
        if(key.Aid==AidKey.Pf4) { OpenPrompt();return Response(); }
        if(key.Aid is AidKey.RollUp or AidKey.RollDown)
        { _historyChoice=Math.Clamp((_historyChoice<0 ? _history.Count : _historyChoice)+(key.Aid==AidKey.RollDown ? -1 : 1),0,Math.Max(0,_history.Count-1));Paint();return Response(); }
        Edit(key);Paint();return Response();
    }
    private PanelResponse DataKey(KeyPress key)
    {
        if(key.Aid is AidKey.Pf3 or AidKey.Pf12 or AidKey.Pa1)return Response("CLOSE");
        if(key.Aid==AidKey.Pf5)return Response("REFRESH");
        var first=First;
        if(key.Aid==AidKey.RollUp)first+=PageSize;
        else if(key.Aid==AidKey.RollDown)first=Math.Max(0,first-PageSize);
        else if(key.Aid is AidKey.Pf19 or AidKey.Pf20)
        { _shift=Math.Clamp(_shift+(key.Aid==AidKey.Pf20 ? Buffer.Columns/2 : -Buffer.Columns/2),0,Math.Max(0,_widths.Sum()+2*_widths.Length-Buffer.Columns+2)); }
        else if(key.Aid==AidKey.Enter)
        {
            if(!long.TryParse(_form!.ReadValue(0),out var line)||line is <1 or >1000000 || !int.TryParse(_form.ReadValue(1),out var shift)||shift<1)
            { Error("Enter a valid line and column position.");return Response(); }
            first=line-1;_shift=Math.Min(shift-1,Math.Max(0,_widths.Sum()+2*_widths.Length-1));
        }
        else { FormEdit(key);return Response(); }
        return Response("PAGE",new Dictionary<string,object?> { ["FIRST"]=first });
    }
    private PanelResponse FormKey(KeyPress key)
    {
        if(key.Aid is AidKey.Pf12 or AidKey.Pa1 || key.Aid==AidKey.Pf3 && _view!="Exit") { _view="Entry";_status="";Paint();return Response(); }
        if(key.Aid is AidKey.RollUp or AidKey.RollDown)
        { Capture();_formPage=Math.Clamp(_formPage+(key.Aid==AidKey.RollUp ? 1 : -1),0,(_fields.Length-1)/8);Paint();return Response(); }
        if(key.Aid!=AidKey.Enter) { FormEdit(key);return Response(); }
        Capture();var values=_fields.ToDictionary(f=>f.Key,f=>(object?)f.Value,StringComparer.Ordinal);
        if(_view=="Exit")
        {
            return _fields[0].Value.Trim() switch {
                "1"=>Response("SAVE"),"2"=>Response("DISCARD"),"3"=>Entry(),"4"=>OpenSource(),_=>Invalid("Select exit option 1 through 4.") };
        }
        if(_view=="Services") { if(_fields[0].Value.Trim()=="1") { OpenAttributes();return Response(); }return Invalid("Select a listed service."); }
        if(_view=="Attributes")return Response("SETTINGS",values);
        if(_view=="Source")return Response("SOURCE",values);
        if(_view=="Prompt")
        {
            var clauses=new[] { "SELECT "," FROM "," WHERE "," GROUP BY "," HAVING "," ORDER BY "," FETCH FIRST " };
            if(string.IsNullOrWhiteSpace(_fields[1].Value))return Invalid("A FROM table is required.");
            var sql=new StringBuilder();for(var i=0;i<_fields.Length;i++)if(!string.IsNullOrWhiteSpace(_fields[i].Value))sql.Append(clauses[i]).Append(_fields[i].Value.Trim()).Append(i==6 ? " ROWS ONLY" : "");
            if(sql.Length>32768)return Invalid("SQL statement exceeds the editor limit.");
            SetDraft(sql.ToString());_view="Entry";_status="SELECT built. Press Enter to run.";Paint();return Response();
        }
        return Response();
    }
    private PanelResponse Invalid(string message) { Error(message);return Response(); }
    private PanelResponse Entry() { _view="Entry";Paint();return Response(); }
    private PanelResponse OpenSource() { OpenForm("Source","Save Session in Source File",[("SRCFILE","Source file","QGPL/QSQLSRC"),("SRCMBR","Source member","SQLSESSION")]);return Response(); }
    private void OpenAttributes()=>OpenForm("Attributes","Change Session Attributes",[
        ("NAMING","Naming convention",Settings.Naming),("COMMIT","Commitment control",Settings.Commit),("PROCESS","Processing",Settings.Process),("REFRESH","Data refresh",Settings.Refresh),
        ("ALWCPYDTA","Allow copy data",Settings.CopyData),("DATFMT","Date format",Settings.DateFormat),("DATSEP","Date separator",Settings.DateSeparator),("TIMFMT","Time format",Settings.TimeFormat),
        ("TIMSEP","Time separator",Settings.TimeSeparator),("DECPNT","Decimal point",Settings.DecimalPoint)]);
    private void OpenForm(string view,string title,(string Key,string Label,string Value)[] fields)
    { _view=view;_title=title;_fields=fields;_formPage=0;_status="";Paint(); }
    private void Capture()
    {
        if(_form is null)return;
        for(var index=0;index<_form.Fields.Count;index++)_fields[_formPage*8+index].Value=_form.ReadValue(index);
    }
    private void FormEdit(KeyPress key)
    {
        if(key.Edit is { } edit && edit!=CursorEdit.None)_editor!.ApplyEdit(edit);
        else if(key.Character!='\0' && !char.IsControl(key.Character))
        { if(_editor!.Apply(key.Character) is EditingResult.Full or EditingResult.Rejected)Error("Field is full or the character cannot be displayed."); }
    }
    internal void ChangeSettings(InteractiveSqlSettings settings) { Settings=settings;_view="Entry";_status="Session attributes changed.";Paint(); }
    internal void Error(string message) { _status=message;Status();if(_view=="Exit" && _form is not null) { _form.Activate(0);_form.Active.CursorOffset=0; } }
    internal void Completed(string sql,string message,bool clear=true)
    {
        Remember(sql,message);_view="Entry";_status=message;
        if(clear) { _draft.Clear();_cursor=0;_top=0; }Paint();
    }
    internal void Remember(string sql,string message)
    {
        _history.Add(sql);_messages.Add(message);
        while(_history.Count>1000 || _history.Sum(s=>(long)Encoding.UTF8.GetByteCount(s))>8388608) { _history.RemoveAt(0);if(_messages.Count>0)_messages.RemoveAt(0); }
        _historyChoice=-1;
    }
    internal void ClearEntry() { _draft.Clear();_cursor=0;_top=0; }
    internal void ShowData(InteractiveSqlPage data)
    {
        _data=data;_view="Data";_widths=data.Columns.Select(c=>Math.Max(c.Name.Length,c.Type.Kind switch {
            SqlQueryKind.SmallInt=>6,SqlQueryKind.Integer=>11,SqlQueryKind.BigInt=>20,SqlQueryKind.Decimal or SqlQueryKind.Numeric=>c.Type.Precision+2,
            SqlQueryKind.Real or SqlQueryKind.Double=>24,SqlQueryKind.Date=>10,SqlQueryKind.Time=>8,SqlQueryKind.Timestamp=>26,_=>Math.Max(1,c.Type.Length) })).ToArray();
        _status=data.Diagnostics.Count==0 ? "NULL values display as -." : string.Join(" ",data.Diagnostics.Take(2).Select(d=>d.MessageId+" "+d.Message));Paint();
    }
    internal void CloseData() { _data=null;_view="Entry";_shift=0;_status="SELECT statement run complete.";Paint(); }
    private PanelResponse Response(string? action=null,IReadOnlyDictionary<string,object?>? values=null)
    {
        var fields=values is null ? new Dictionary<string,object?>() : new(values);if(action is not null)fields["ACTION"]=action;
        return new(action is not null,AidKey.Enter,fields,new bool[100],Buffer.Cursor);
    }
    private void SetDraft(string text) { _draft.Clear().Append(text);_cursor=_draft.Length;_top=0; }
    private void Edit(KeyPress key)
    {
        if(key.PasteCharacter is '\r' or '\n')
        {
            if(key.PasteCharacter=='\n' && _pasteCr) { _pasteCr=false;return; }
            _pasteCr=key.PasteCharacter=='\r';if(_draft.Length<32768)_draft.Insert(_cursor++,"\n");return;
        }
        _pasteCr=false;
        var text=Draft;var start=text.AsSpan(0,_cursor).LastIndexOf('\n');start=start<0 ? 0 : start+1;
        var end=text.IndexOf('\n',_cursor);if(end<0)end=text.Length;
        if(key.Aid==AidKey.Pf6) { if(_draft.Length<32768)_draft.Insert(_cursor++,"\n");return; }
        if(key.Aid==AidKey.Pf10) { var copy=text[start..end];if(_draft.Length+copy.Length+1<=32768) { _draft.Insert(end,"\n"+copy);_cursor=end+1; }return; }
        if(key.Character!='\0' && !char.IsControl(key.Character))
        {
            if(!TerminalGlyph.IsSingleCell(key.Character)) { _status="Character cannot be displayed.";return; }
            if(_draft.Length>=32768 && (_insert || _cursor>=_draft.Length)) { _status="SQL editor limit reached.";return; }
            if(!_insert && _cursor<_draft.Length && _draft[_cursor]!='\n')_draft[_cursor++]=key.Character;
            else _draft.Insert(_cursor++,key.Character);return;
        }
        switch(key.Edit)
        {
            case CursorEdit.Insert:_insert=!_insert;break;
            case CursorEdit.Home:_cursor=start;break;case CursorEdit.End:_cursor=end;break;
            case CursorEdit.CursorLeft:_cursor=Math.Max(0,_cursor-1);break;case CursorEdit.CursorRight:_cursor=Math.Min(_draft.Length,_cursor+1);break;
            case CursorEdit.Delete:if(_cursor<_draft.Length)_draft.Remove(_cursor,1);break;
            case CursorEdit.FieldBackspace:if(_cursor>0)_draft.Remove(--_cursor,1);break;
            case CursorEdit.FieldEraseToEnd:_draft.Remove(_cursor,end-_cursor);break;
            case CursorEdit.CursorUp:
                if(start>0) { var previous=text.LastIndexOf('\n',Math.Max(0,start-2),Math.Max(0,start-1));previous=previous<0 ? 0 : previous+1;_cursor=previous+Math.Min(_cursor-start,start-previous-1); }
                else if(_history.Count>0)_historyChoice=Math.Clamp((_historyChoice<0 ? _history.Count : _historyChoice)-1,0,_history.Count-1);break;
            case CursorEdit.CursorDown:case CursorEdit.Tab:case CursorEdit.NextField:
                if(end<text.Length) { var next=text.IndexOf('\n',end+1);if(next<0)next=text.Length;_cursor=end+1+Math.Min(_cursor-start,next-end-1); }break;
        }
    }
}
