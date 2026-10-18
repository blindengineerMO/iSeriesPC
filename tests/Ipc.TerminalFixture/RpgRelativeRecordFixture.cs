using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;

internal static class RpgRelativeRecordFixture
{
    internal static void Seed(IpcSystem system, SqliteFileStore files)
    {
        var format = new RecordFormat { Name = "RRNROW", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8 },
            new() { Name = "TEXT", Type = FieldType.Alpha, Length = 12 }] }; format.AssignPositions();
        files.CreatePhysicalFile("QGPL", "GRRNS", new() { Name = "GRRNS", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        foreach (var (id, text) in new[] { (101L, "first"), (102L, "second"), (103L, "third"), (108L, "eighth") })
            files.Insert("QGPL", "GRRNS", "GRRNS", "RRNROW", new Dictionary<string, object?> { ["ID"] = id, ["TEXT"] = text });
        using (var cursor = files.OpenSequentialCursor("QGPL", "GRRNS", "GRRNS", keyed: false)) cursor.DeleteRecord(2);
        using (var connection = system.Connections.Open())
        using (var command = connection.CreateCommand())
        { command.CommandText = "UPDATE \"QGPL.GRRNS.GRRNS\" SET rowid=8 WHERE ID=108"; command.ExecuteNonQuery(); }
        files.SaveSourceMember("QGPL", "SOURCE", "RPGRRN", """
            **free
            dcl-s ID int(20);
            dcl-s TEXT char(12);
            dcl-s RRN int(20) inz(3);
            chain RRN GRRNS;
            if ID<>103;
              sndpgmmsg 'RRN CHAIN FAILED';
              seton LR;
              return;
            endif;
            eval TEXT='changed';
            update GRRNS;
            chain 2 GRRNS;
            if %found(GRRNS);
              sndpgmmsg 'RRN GAP FAILED';
              seton LR;
              return;
            endif;
            setll 2 GRRNS;
            read GRRNS;
            if ID<>103 or TEXT<>'changed';
              sndpgmmsg 'RRN LIMIT FAILED';
              seton LR;
              return;
            endif;
            delete 3 GRRNS;
            read GRRNS;
            if ID=108 and TEXT='eighth';
              sndpgmmsg 'RPG RRN ACCESS ACCEPTED';
            endif;
            seton LR;
            """, null);
    }
}
