using System.Text.Json;
using Ipc.Console.Session;
using Ipc.Dsp;
using Ipc.Terminal;

if (args.Length != 2) return 2;
using var input = new TerminalInputReader();
using var system = args[1] is "help" or "designer" or "groups" or "work" ? Ipc.Services.IpcSystem.Create(Path.Combine(Path.GetDirectoryName(args[0])!, "catalog")) : null;
ISessionController controller;
if (system is not null)
{
    system.Start();
    if (args[1] is "designer" or "work")
    {
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "DesignerFixture22");
        new Ipc.Db.Store.SqliteFileStore(system.Connections, system.Objects).CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        if (args[1] == "work")
        {
            system.Objects.Create(new Ipc.Core.Objects.ObjectDescriptor { Key = new("QGPL", "CMDCPP"), ObjectType = "*PGM", Attribute = "CLP", Source = "PGM PARM(&NAME &COUNT)\nSNDPGMMSG MSG(&NAME + ':' + &COUNT)\nENDPGM" });
            new Ipc.Db.Store.SqliteFileStore(system.Connections, system.Objects).SaveSourceMember("QGPL", "SOURCE", "HELLOCMD",
                "CMD PROMPT('Greeting command')\nPARM KWD(NAME) TYPE(*NAME) MIN(1) PROMPT('Name' 2)\nPARM KWD(COUNT) TYPE(*DEC) LEN(3 0) DFT(10) RANGE(1 100) PROMPT('Count' 1)", null);
            var files = new Ipc.Db.Store.SqliteFileStore(system.Connections, system.Objects);
            var format = new Ipc.Db.Definitions.RecordFormat { Name = "BASEREC", Fields = new() {
                new() { Name = "VALUE", Type = Ipc.Db.Definitions.FieldType.Alpha, Length = 12 }
            } }; format.AssignPositions();
            files.CreatePhysicalFile("QGPL", "LFBASE", new() { Name = "LFBASE", Attribute = Ipc.Db.Definitions.FileAttribute.Physical, Formats = new() { format } }, "");
            files.Insert("QGPL", "LFBASE", "LFBASE", "BASEREC", new Dictionary<string, object?> { ["VALUE"] = "LF ACCEPTED" });
            files.SaveSourceMember("QGPL", "SOURCE", "LFVIEW", "     A          R VIEWREC".PadRight(44) + "PFILE(QGPL/LFBASE)", null);
            files.SaveSourceMember("QGPL", "SOURCE", "NATIVEPF",
                "     A          R NATIVEREC\n" + "     A            VALUE".PadRight(29) + "   12A", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLPREP",
                "PGM\n/DEFINE ENABLED\n/IF DEFINED(ENABLED)\n/INCLUDE CLINC\n/ELSE\nUNAVAILABLE\n/ENDIF\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLINC", "SNDPGMMSG MSG('PREPROCESS ACCEPTED')", null);
            system.Objects.Create(new Ipc.Core.Objects.ObjectDescriptor { Key = new("QGPL", "CLADJUST"), ObjectType = "*PGM", Attribute = "CLP",
                Source = "PGM PARM(&N)\nDCL &N *DEC LEN(7 0)\nCHGVAR &N (&N + 1)\nRETURN" });
            system.Objects.Create(new Ipc.Core.Objects.ObjectDescriptor { Key = new("QGPL", "CLESCAPE"), ObjectType = "*PGM", Attribute = "CLP",
                Source = "PGM\nSNDPGMMSG MSGID(CPF9898) MSGF(QCPFMSG) MSGDTA('EXPECTED') MSGTYPE(*ESCAPE)\nENDPGM" });
            files.SaveSourceMember("QGPL", "SOURCE", "CLMON",
                "PGM\nCALL PGM(QGPL/CLESCAPE)\nMONMSG MSGID(CPF9800) CMPDTA('EXPECTED') EXEC(DO)\n" +
                "SNDPGMMSG MSG('MONMSG ACCEPTED')\nRETURN\nENDDO\nSNDPGMMSG MSG('MONMSG FAILED')\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLFLOW",
                "PGM\nDCL &I *INT\nDCL &N *DEC LEN(7 0)\nDOFOR &I FROM(1) TO(5)\n" +
                "IF (&I *EQ 3) THEN(ITERATE)\nCHGVAR &N (&N + &I)\nENDDO\nCALL PGM(QGPL/CLADJUST) PARM(&N)\nSELECT\n" +
                "WHEN (&N *EQ 13) THEN(SNDPGMMSG MSG('CONTROL FLOW ACCEPTED'))\n" +
                "OTHERWISE CMD(SNDPGMMSG MSG('CONTROL FLOW FAILED'))\nENDSELECT\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLREAD",
                "PGM\nDCLF FILE(QGPL/LFBASE)\nRCVF\nRCVF\nMONMSG CPF0864 EXEC(GOTO EOF)\n" +
                "SNDPGMMSG MSG('FILE IO FAILED')\nRETURN\nEOF: CLOSE\nRCVF\n" +
                "IF (&VALUE *EQ 'LF ACCEPTED') THEN(SNDPGMMSG MSG('FILE IO ACCEPTED'))\nENDPGM", null);
            string RpgLine(char spec, params (int Column, string Text)[] values)
            {
                var line = new string(' ', 100).ToCharArray(); line[6] = spec;
                foreach (var value in values) value.Text.CopyTo(0, line, value.Column, value.Text.Length);
                return new string(line).TrimEnd() + "\n";
            }
            system.Objects.Create(new Ipc.Core.Objects.ObjectDescriptor { Key = new("QGPL", "RPGADJUST"), ObjectType = "*PGM", Attribute = "RPG",
                Source = RpgLine('D', (7, "VALUE"), (21, "P"), (38, "5"), (40, "0")) +
                    RpgLine('C', (17, "*ENTRY"), (35, "PLIST")) + RpgLine('C', (17, "VALUE"), (35, "PARM")) +
                    RpgLine('C', (35, "ADD"), (42, "7"), (47, "VALUE")) });
            files.SaveSourceMember("QGPL", "SOURCE", "CLRPG",
                "PGM\nDCL &N *DEC LEN(5 0) VALUE(5)\nCALL PGM(QGPL/RPGADJUST) PARM(&N)\n" +
                "IF (&N *EQ 12) THEN(SNDPGMMSG MSG('RPG WRITEBACK ACCEPTED'))\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLENV",
                "PGM\nDCL &WHO *CHAR LEN(10)\nDCL &DATA *CHAR LEN(3)\nDCL &POS *INT VALUE(2)\n" +
                "RTVJOBA USER(&WHO)\nCHGDTAARA DTAARA(*LDA (&POS 3)) VALUE('ABC')\n" +
                "RTVDTAARA DTAARA(*LDA (&POS 3)) RTNVAR(&DATA)\n" +
                "IF ((&WHO *EQ 'QSECOFR') *AND (&DATA *EQ 'ABC')) THEN(SNDPGMMSG MSG('CL ENVIRONMENT ACCEPTED'))\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLNAMED",
                "PGM\nDCL &PRICE *DEC LEN(5 1)\nRTVDTAARA DTAARA(QGPL/PRICE) RTNVAR(&PRICE)\n" +
                "IF (&PRICE *NE 12.3) THEN(RETURN)\nCHGDTAARA DTAARA(QGPL/PRICE) VALUE(12.49)\n" +
                "RTVDTAARA DTAARA(QGPL/PRICE) RTNVAR(&PRICE)\n" +
                "IF (&PRICE *EQ 12.4) THEN(SNDPGMMSG MSG('NAMED AREA ACCEPTED'))\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLRESP",
                "PGM\nDCL &KEY *CHAR LEN(4)\nRCVMSG MSGQ(QGPL/INBOX) RMV(*NO) KEYVAR(&KEY)\n" +
                "SNDRPY MSGQ(QGPL/INBOX) MSGKEY(&KEY) RPY('YES')\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLABICHILD",
                "PGM PARM(&N &RAW &I)\nDCL &N *DEC LEN(15 5)\nDCL &RAW *CHAR LEN(4)\nDCL &I *INT\n" +
                "IF (&N *NE 25.5 *OR &RAW *NE X'FF00FE01' *OR &I *NE 4) THEN(RETURN)\n" +
                "CHGVAR &I 99\nSNDPGMMSG MSG('CALL LAYOUTS ACCEPTED')\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLCONST",
                "PGM\nDCL &N *INT VALUE(3)\nCALL QGPL/CLABICHILD PARM(25.5 X'FF00FE01' ((&N + 1) (*INT 4)))\n" +
                "IF (&N *NE 3) THEN(SNDPGMMSG MSG('TEMPORARY CHANGED CALLER'))\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLMSGTXT",
                "PGM\nDCL &TEXT *CHAR LEN(30)\nDCL &HELP *CHAR LEN(40)\nDCL &SEV *DEC LEN(2 0)\n" +
                "RTVMSG USR0001 QGPL/CLMSGS MSGDTA(X'01234D') MSG(&TEXT) SECLVL(&HELP) SEV(&SEV)\n" +
                "IF (&TEXT *EQ 'Amount -12.34' *AND &HELP *EQ 'Balance -12.34' *AND &SEV *EQ 40) THEN(SNDPGMMSG MSG('PREDEFINED TEXT ACCEPTED'))\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLMSGFAIL",
                "PGM\nSNDPGMMSG MSGID(USR0001) MSGF(QGPL/CLMSGS) MSGDTA(X'01234D') MSGTYPE(*ESCAPE)\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLMSGRCV",
                "PGM\nDCL &TEXT *CHAR LEN(30)\nDCL &HELP *CHAR LEN(40)\nDCL &RAW *CHAR LEN(3)\nDCL &SEV *DEC LEN(2 0)\n" +
                "CALL QGPL/CLMSGFAIL\nMONMSG MSGID(USR0001) CMPDTA(X'01234D') EXEC(DO)\n" +
                "RCVMSG PGMQ(*SAME) MSGTYPE(*EXCP) MSG(&TEXT) SECLVL(&HELP) MSGDTA(&RAW) SEV(&SEV)\n" +
                "IF (&TEXT *EQ 'Amount -12.34' *AND &HELP *EQ 'Balance -12.34' *AND &RAW *EQ X'01234D' *AND &SEV *EQ 40) THEN(SNDPGMMSG MSG('PREDEFINED QUEUE ACCEPTED'))\nENDDO\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLAPI",
                "PGM\nDCL &CMD *CHAR LEN(40) VALUE('CHGCURLIB QGPL')\nDCL &LEN *DEC LEN(15 5) VALUE(14)\n" +
                "DCL &LIB *CHAR LEN(10)\nCALL QSYS/QCMDEXC PARM(&CMD &LEN)\nRTVJOBA CURLIB(&LIB)\n" +
                "IF (&LIB *EQ 'QGPL') THEN(SNDPGMMSG MSG('QCMDEXC ACCEPTED'))\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLARGS",
                "PGM\nDCL &LIB *CHAR LEN(10) VALUE('QGPL')\nDCL &AREA *CHAR LEN(10) VALUE('ARGBYTES')\n" +
                "DCL &LEN *DEC LEN(3 0) VALUE(2)\nDCL &RAW *CHAR LEN(2)\n" +
                "CRTDTAARA DTAARA(&LIB/&AREA) TYPE(*CHAR) LEN(&LEN) VALUE(X'FF00')\n" +
                "RTVDTAARA DTAARA(&LIB/&AREA) RTNVAR(&RAW)\nDLTDTAARA DTAARA(&LIB/&AREA)\n" +
                "IF (&RAW *EQ X'FF00') THEN(SNDPGMMSG MSG('COMMAND ARGUMENTS ACCEPTED'))\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLBYTES",
                "PGM\nDCL &RAW *CHAR LEN(4) VALUE(X'FFFFFFC7')\nDCL &N *DEC LEN(10 0)\n" +
                "CHGVAR %SST(*LDA 1021 4) &RAW\nCHGVAR &RAW %SUBSTRING(*LDA 1021 4)\n" +
                "CHGVAR &N %BINARY(&RAW)\nIF (&N *NE -57) THEN(RETURN)\n" +
                "CHGVAR %BIN(&RAW 3 2) 32767\n" +
                "IF (&RAW *EQ X'FFFF7FFF') THEN(SNDPGMMSG MSG('BYTE FUNCTIONS ACCEPTED'))\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLERRMSG",
                "PGM\nDCL &N *DEC LEN(2 0)\nDCL &ID *CHAR LEN(7)\nDCL &TYPE *CHAR LEN(2)\nDCL &SENDER *CHAR LEN(720)\nCHGVAR &N (1 / 0)\n" +
                "MONMSG MCH1211 EXEC(DO)\nRCVMSG MSGTYPE(*EXCP) MSGID(&ID) RTNTYPE(&TYPE) SENDER(&SENDER) SENDERFMT(*LONG)\n" +
                "IF (&ID *EQ 'MCH1211' *AND &TYPE *EQ '15' *AND %SST(&SENDER 42 12) *EQ 'CLERRMSG') THEN(SNDPGMMSG MSG('RUNTIME ERROR RECEIVED'))\nENDDO\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLQUEUE",
                "PGM\nDCL &KEY *CHAR LEN(4)\nDCL &TEXT *CHAR LEN(8)\nDCL &TYPE *CHAR LEN(2)\n" +
                "SNDMSG MSG('Proceed?') TOMSGQ(QGPL/INBOX) MSGTYPE(*INQ) RPYMSGQ(QGPL/REPLIES)\n" +
                "RCVMSG MSGQ(QGPL/INBOX) RMV(*NO) KEYVAR(&KEY) MSG(&TEXT)\n" +
                "IF (&TEXT *NE 'Proceed?') THEN(RETURN)\nSNDRPY MSGKEY(&KEY) MSGQ(QGPL/INBOX) RPY('YES')\n" +
                "RCVMSG MSGQ(QGPL/REPLIES) MSGTYPE(*RPY) MSG(&TEXT)\n" +
                "IF (&TEXT *NE 'YES') THEN(RETURN)\n" +
                "SNDPGMMSG MSG('Again?') TOMSGQ(QGPL/INBOX) MSGTYPE(*INQ) KEYVAR(&KEY)\n" +
                "RCVMSG MSGTYPE(*COPY) MSGKEY(&KEY) RMV(*NO) RTNTYPE(&TYPE)\nIF (&TYPE *NE '06') THEN(RETURN)\n" +
                "CALL QGPL/CLRESP\nRCVMSG MSGTYPE(*RPY) MSGKEY(&KEY) MSG(&TEXT) RTNTYPE(&TYPE)\n" +
                "IF (&TEXT *EQ 'YES' *AND &TYPE *EQ '21') THEN(SNDPGMMSG MSG('QUEUE ACCEPTED'))\nENDPGM", null);
        }
        controller = new MenuController(system, system.Security.Profiles.Get("QSECOFR"));
    }
    else
    {
    new HelpPanelStore(system.Connections).Create("QGPL", "HELPS", ":PNLGRP.:HELP NAME=GENERAL.General help:P.General customer guidance. :LINK PERFORM='DSPHELP FIELD'.Field details:ELINK.:EHELP.:HELP NAME=FIELD.Field help:ISCH ROOTS='customer'.:P.Enter a customer name.:EHELP.:EPNLGRP.");
    var source = FixtureController.Dds(keywords: "HELP HLPTITLE('Customer help') HLPPNLGRP(GENERAL QGPL/HELPS) CA03(03)") +
        FixtureController.Dds("ENTRY", record: true) + FixtureController.Dds("TEXT", 20, row: 4, column: 2, usage: 'B');
    new DisplayFileStore(system.Connections).Create("QGPL", "ENTRY", source);
    controller = new MenuController(system, system.Security.Profiles.Get("QUSER"));
    }
}
else if (args[1] is "lifecycle" or "output-error" or "input-error") controller = new LifecycleController(args[0]);
else controller = new FixtureController(args[0], args[1] == "wide");
var terminal = new TtySession(controller, args[1] == "input-error" ? new FailingReader() : input, args[1] == "output-error" ? new FailingWriter() : Console.Out);
try { terminal.Run(); } catch (Exception error) { Console.Error.WriteLine(error.Message); return 3; }
if (args[1] is "groups" or "work")
    File.WriteAllText(args[0], JsonSerializer.Serialize(system!.Jobs.List().Select(j => new { Job = j.Key.ToString(), Status = j.Status.ToString(), Completion = j.CompletionCode.ToString() })));
if (args[1] == "designer")
{
    var definition = new DisplayFileStore(system!.Connections).Load("QGPL", "ENTRY");
    var source = new Ipc.Db.Store.SqliteFileStore(system.Connections, system.Objects).ReadSourceMember("QGPL", "SOURCE", "ENTRY");
    File.WriteAllText(args[0], JsonSerializer.Serialize(new { Definition = definition, Source = source.Source }));
}
return terminal.ExitCode;

sealed class FixtureController : ISessionController
{
    private readonly DisplayFileSession _file;
    private readonly string _report;
    private readonly List<string> _aids = new();
    private IReadOnlyDictionary<string, object?> _values = new Dictionary<string, object?> { ["TEXT"] = "", ["NUMBER"] = 1.23m, ["DAY"] = new DateOnly(2024, 2, 29), ["SECRET"] = "hidden" };
    public DisplayBuffer Buffer => _file.Buffer;
    public FixtureController(string report, bool wide)
    {
        _report = report;
        var source = Dds(keywords: wide ? "DSPSIZ(27 132)" : "DSPSIZ(24 80)") +
            string.Concat(Enumerable.Range(1, 24).Select(n => Dds(keywords: $"CF{n:00}({n:00})"))) +
            Dds("ENTRY", record: true) + Dds(row: 2, column: 2, keywords: "'Terminal fixture'") +
            Dds("TEXT", 20, row: 4, column: 2, usage: 'B') + Dds("NUMBER", 5, row: 6, column: 2, type: 'Y', usage: 'B', decimals: 2) +
            Dds("DAY", 10, row: 8, column: 2, type: 'L', usage: 'B') + Dds("SECRET", 8, usage: 'H');
        _file = new(new DisplayDdsCompiler().Compile("FIXTURE", source)); _file.Write("ENTRY", _values);
    }
    public SessionEvent Handle(KeyPress key)
    {
        var response = _file.Handle(key);
        if (!response.Accepted) return new("Editing");
        _aids.Add(response.Aid.ToString()); _values = response.Values;
        if (response.Aid == AidKey.Pa3)
        {
            File.WriteAllText(_report, JsonSerializer.Serialize(new { Aids = _aids, Values = _values })); return new("Done", EndSession: true);
        }
        _file.Write("ENTRY", _values);
        var status = "Last " + response.Aid;
        for (var i = 0; i < status.Length; i++) Buffer.Set(10, i + 2, status[i]);
        return new("Editing");
    }
    internal static string Dds(string name = "", int length = 0, int row = 0, int column = 0, char type = 'A', char usage = 'O', int? decimals = null, bool record = false, string keywords = "")
    {
        var line = new string(' ', 44).ToCharArray(); line[5] = 'A'; if (record) line[16] = 'R'; name.CopyTo(0, line, 18, name.Length);
        if (!record && name.Length > 0) { length.ToString().PadLeft(5).CopyTo(0, line, 29, 5); line[34] = type; line[37] = usage; }
        if (decimals is { } n) n.ToString().PadLeft(2).CopyTo(0, line, 35, 2);
        if (row > 0) row.ToString().PadLeft(3).CopyTo(0, line, 38, 3); if (column > 0) column.ToString().PadLeft(3).CopyTo(0, line, 41, 3);
        return new string(line) + keywords + "\n";
    }
}

sealed class LifecycleController(string report) : ISessionController, IDisposable
{
    public DisplayBuffer Buffer { get; } = CreateBuffer();
    private int _keys;
    private bool _disconnected;
    private static DisplayBuffer CreateBuffer() { var buffer = new DisplayBuffer(); buffer.MoveCursor(2, 2); buffer.Write("Lifecycle ready"); return buffer; }
    public SessionEvent Handle(KeyPress key)
    {
        _keys++;
        if (key.Character == 'X') throw new InvalidOperationException("Injected controller failure.");
        return new("Done", EndSession: key.Aid == AidKey.Pf3);
    }
    public void RequestDisconnect() => _disconnected = true;
    public void Dispose() => File.WriteAllText(report, JsonSerializer.Serialize(new { Disposed = true, Disconnected = _disconnected, Keys = _keys }));
}

sealed class FailingReader : TextReader { public override int Read() => throw new IOException("Injected input failure."); }
sealed class FailingWriter : TextWriter
{
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public override void Write(string? value) => throw new IOException("Injected output failure.");
}
