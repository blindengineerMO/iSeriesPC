using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;

internal static class JournalCycleFixture
{
    internal static void Seed(IpcSystem system, SqliteFileStore files)
    {
        var file = new QualifiedName("QGPL", "GRCYCLE"); var journal = new QualifiedName("QGPL", "GRCYCLEJ");
        system.Journals.CreateReceiver(new("QGPL", "GRCYCLER1")); system.Journals.Create(journal, new("QGPL", "GRCYCLER1"));
        var format = new RecordFormat { Name = "CYCROW", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 4, Sequence = 1 }, new() { Name = "VALUE", Type = FieldType.Alpha, Length = 10 }] }; format.AssignPositions();
        files.CreatePhysicalFile("QGPL", "GRCYCLE", new() { Name = "GRCYCLE", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        system.Journals.StartFile(file, journal, "*BOTH");
        files.Insert("QGPL", "GRCYCLE", "GRCYCLE", "CYCROW", new Dictionary<string, object?> { ["ID"] = 1L, ["VALUE"] = "BEFORE" });
        var job = system.Jobs.CreateSystem("JRNSEED", "QSECOFR");
        try
        {
            system.RecordTransactions.Begin(job.Key); system.RecordTransactions.Enlist(job.Key, file);
            using (var statement = system.RecordTransactions.Open(job.Key))
            { statement.Execute("UPDATE \"QGPL.GRCYCLE.GRCYCLE\" SET VALUE='AFTER'"); statement.Complete(); }
            system.RecordTransactions.Commit(job.Key); system.RecordTransactions.End(job.Key);
        }
        finally { system.Jobs.Complete(job.Key); }
    }
}
