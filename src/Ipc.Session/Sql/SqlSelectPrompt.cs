namespace Ipc.Session.Sql;

internal static class SqlSelectPrompt
{
    // Split only top-level clauses; never execute or rewrite a quoted/commented token.
    internal static string[] Read(string input)
    {
        var source=input.Trim().TrimEnd(';').Trim();if(source.Length==0 || source.Equals("SELECT",StringComparison.OrdinalIgnoreCase))return ["*","","","","","",""];
        var boundaries=new List<(string Word,int Start,int End)>();var depth=0;var block=0;
        for(var offset=0;offset<source.Length;)
        {
            if(offset+1<source.Length && source[offset..(offset+2)]=="/*") { block++;offset+=2;continue; }
            if(block>0) { if(offset+1<source.Length && source[offset..(offset+2)]=="*/") { block--;offset+=2; }else offset++;continue; }
            if(offset+1<source.Length && source[offset..(offset+2)]=="--") { while(offset<source.Length && source[offset]!='\n')offset++;continue; }
            if(source[offset] is '\'' or '"')
            {
                var quote=source[offset++];var closed=false;while(offset<source.Length) { if(source[offset++]!=quote)continue;if(offset<source.Length && source[offset]==quote) { offset++;continue; }closed=true;break; }
                if(!closed)throw new ArgumentException("Close the quoted SQL value before prompting.");continue;
            }
            if(source[offset]=='(') { depth++;offset++;continue; }if(source[offset]==')') { depth--;offset++;continue; }
            if(depth==0 && char.IsAsciiLetter(source[offset]))
            {
                var start=offset;while(offset<source.Length && (char.IsAsciiLetterOrDigit(source[offset]) || source[offset]=='_'))offset++;
                var word=source[start..offset].ToUpperInvariant();if(word is "SELECT" or "FROM" or "WHERE" or "GROUP" or "HAVING" or "ORDER" or "FETCH")boundaries.Add((word,start,offset));
                continue;
            }
            offset++;
        }
        if(depth!=0 || block!=0 || boundaries.Count==0 || boundaries[0].Word!="SELECT" || boundaries[0].Start!=0)throw new ArgumentException("F4 currently prompts a SELECT statement. Type SELECT first.");
        var names=new[] { "SELECT","FROM","WHERE","GROUP","HAVING","ORDER","FETCH" };var values=new string[7];Array.Fill(values,"");
        foreach(var (entry,index) in boundaries.Select((value,index)=>(value,index)))
        {
            var position=Array.IndexOf(names,entry.Word);if(position<0 || values[position].Length!=0)throw new ArgumentException("Ambiguous SELECT clauses cannot be prompted.");
            var text=source[entry.End..(index+1<boundaries.Count ? boundaries[index+1].Start : source.Length)].Trim();
            if(position is 3 or 5) { if(!text.StartsWith("BY ",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("GROUP/ORDER prompt requires BY.");text=text[3..].Trim(); }
            if(position==6) { if(!text.StartsWith("FIRST ",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("FETCH prompt requires FIRST.");text=text[6..].Trim();text=System.Text.RegularExpressions.Regex.Replace(text,@"\s+ROWS?\s+ONLY\z","",System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
            values[position]=text;
        }
        return values;
    }
}
