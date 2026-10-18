using Ipc.Db.Definitions;
using Ipc.Db.Store;

internal static class RpgFileKeyFixture
{
    internal static void Seed(SqliteFileStore files)
    {
        var format = new RecordFormat { Name = "KROW", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8 },
            new() { Name = "GROUP", Type = FieldType.Packed, Length = 5, Sequence = 1 }, new() { Name = "CODE", Type = FieldType.Alpha, Length = 8, Sequence = 2 },
            new() { Name = "VALUE", Type = FieldType.Alpha, Length = 10 }] }; format.AssignPositions();
        files.CreatePhysicalFile("QGPL", "GKEYS", new() { Name = "GKEYS", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        foreach (var (id, code, value) in new[] { (1L, "A", "first"), (2L, "A", "sibling"), (3L, "B", "third") })
            files.Insert("QGPL", "GKEYS", "GKEYS", "KROW", new Dictionary<string, object?> { ["ID"] = id, ["GROUP"] = 1m, ["CODE"] = code, ["VALUE"] = value });
        static string D(string name, string type, string length = "", string decimals = "")
        {
            var chars = Enumerable.Repeat(' ', 80).ToArray(); chars[6] = 'D';
            name.CopyTo(0, chars, 7, name.Length); type.CopyTo(0, chars, 21, type.Length);
            length.CopyTo(0, chars, 38, length.Length); decimals.CopyTo(0, chars, 40, decimals.Length); return new string(chars).TrimEnd();
        }
        static string C(string opcode, string name = "", string result = "")
        {
            var chars = Enumerable.Repeat(' ', 80).ToArray(); chars[5] = 'C';
            name.CopyTo(0, chars, 11, name.Length); opcode.CopyTo(0, chars, 25, opcode.Length); result.CopyTo(0, chars, 49, result.Length); return new string(chars).TrimEnd();
        }
        var source = string.Join('\n', D("A", "P", "5", "0"), D("B", "S", "8"), D("ID", "I", "8"), D("VALUE", "S", "10"),
            D("KEYDS", "DS"), D("PARTA", "", "5", "0"), D("PARTB", "", "8"), C("KLIST", "SEARCH"), C("KFLD", result: "A"), C("KFLD", result: "B"),
            "/free", "eval A=1;", "eval B='B';", "chain SEARCH GKEYS;", "if ID<>3;", "sndpgmmsg 'KLIST FAILED';", "seton LR;", "return;", "endif;",
            "eval PARTA=1;", "eval PARTB='A';", "setll %kds(KEYDS) GKEYS;", "reade %kds(KEYDS) GKEYS;", "if ID<>1;", "sndpgmmsg 'KDS FAILED';", "seton LR;", "return;", "endif;",
            "delete (1:'A') GKEYS;", "read GKEYS;", "if ID<>2 or VALUE<>'sibling';", "sndpgmmsg 'KEY DELETE FAILED';", "seton LR;", "return;", "endif;",
            "chain (%int(1+0):%subst('B: C':1:1)) GKEYS;", "if ID=3 and VALUE='third' and %found(GKEYS);", "sndpgmmsg 'RPG COMPOUND KEYS ACCEPTED';", "endif;", "seton LR;", "/end-free");
        files.SaveSourceMember("QGPL", "SOURCE", "RPGKEYS", source, null);
    }
}
