using System.Text.Json;
using Ipc.Console.Session;
using Ipc.Dsp;
using Ipc.Terminal;

if (args.Length != 2) return 2;
using var input = new TerminalInputReader();
using var system = args[1] is "help" or "designer" or "groups" or "work" or "messages" ? Ipc.Services.IpcSystem.Create(Path.Combine(Path.GetDirectoryName(args[0])!, "catalog")) : null;
ISessionController controller;
if (system is not null)
{
    system.Start();
    if (args[1] == "messages") controller = new MessageSubfileController(system, args[0]);
    else if (args[1] is "designer" or "work")
    {
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "DesignerFixture22");
        new Ipc.Db.Store.SqliteFileStore(system.Connections, system.Objects).CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        if (args[1] == "work")
        {
            var rpgSources = new Ipc.Db.Store.SqliteFileStore(system.Connections, system.Objects);
            rpgSources.SaveSourceMember("QGPL", "SOURCE", "CLCRYPTO", Ipc.Testing.CryptoProgramSources.ClSource, null);
            rpgSources.SaveSourceMember("QGPL", "SOURCE", "RPGCRYPTO", Ipc.Testing.CryptoProgramSources.RpgSource, null);
            rpgSources.SaveSourceMember("QGPL", "SOURCE", "CLDATEAPI", """
                PGM
                DCL &RAW *CHAR LEN(8)
                DCL &OUT *CHAR LEN(17)
                CALL QSYS/QWCCVTDT PARM('*YYMD' '20000101000000000' '*DTS' &RAW X'00000000')
                CALL QSYS/QWCCVTDT PARM('*DTS' &RAW '*YYMD' &OUT X'00000000')
                IF (&OUT *EQ '20000101000000000') THEN(SNDPGMMSG MSG('CL NATIVE DATE API ACCEPTED'))
                ENDPGM
                """, null);
            rpgSources.SaveSourceMember("QGPL", "SOURCE", "RPGDATEAPI", """
                **free
                dcl-s RAW char(8) ccsid(*hex);
                dcl-s OUT char(20) ccsid(*hex);
                callp QWCCVTDT('*YYMD':'20000101000000123456':'*DTS':RAW:X'00000000':'*UTC':'*UTC':'':0:'1');
                callp QWCCVTDT('*DTS':RAW:'*YYMD':OUT:X'00000000':'*UTC':'*UTC':'':0:'1');
                if RAW = X'800000001E240000' and OUT = '20000101000000123456';
                  sndpgmmsg 'RPG NATIVE DATE API ACCEPTED';
                endif;
                seton LR;
                """, null);
            rpgSources.SaveSourceMember("QGPL", "SOURCE", "CLJOBAPI", "PGM\nDCL &LIST *CHAR LEN(512)\nDCL &INFO *CHAR LEN(86)\nDCL &ID *CHAR LEN(16)\n" +
                "CALL QSYS/QUSCRTUS PARM('CLJOBS    QTEMP     ' '' (512 (*INT 4)) X'FE' '*CHANGE' ('Native CL job list' (*CHAR 50)))\n" +
                "CALL QSYS/QUSLJOB PARM('CLJOBS    QTEMP     ' 'JOBL0100' '*' '*ACTIVE' X'00000000')\n" +
                "CALL QSYS/QUSRTVUS PARM('CLJOBS    QTEMP     ' (1 (*INT 4)) (512 (*INT 4)) &LIST X'00000000')\nCHGVAR &ID %SST(&LIST 415 16)\n" +
                "CALL QSYS/QUSRJOBI PARM(&INFO (86 (*INT 4)) 'JOBI0100' '*INT' &ID X'00000000')\n" +
                "IF (%BIN(&LIST 133 4) *EQ 1 *AND %BIN(&INFO 1 4) *EQ 86 *AND %SST(&INFO 35 16) *EQ &ID *AND %SST(&INFO 61 1) *EQ 'I') THEN(SNDPGMMSG MSG('CL NATIVE JOB APIS ACCEPTED'))\nENDPGM", null);
            rpgSources.SaveSourceMember("QGPL", "SOURCE", "RPGJOBAPI", "**free\ndcl-s LIST char(512) ccsid(*hex);\ndcl-s INFO char(128) ccsid(*hex);\ndcl-s ID char(16) ccsid(*hex);\n" +
                "callp QUSCRTUS('RPGJOBS   QTEMP     ':'':512:X'FE':'*CHANGE':'Native RPG job APIs');\n" +
                "callp QUSLJOB('RPGJOBS   QTEMP     ':'JOBL0100':'*':'*ACTIVE':X'00000000');\n" +
                "callp QUSRTVUS('RPGJOBS   QTEMP     ':1:512:LIST:X'00000000');\neval ID = %subst(LIST:415:16);\n" +
                "callp QUSRJOBI(INFO:128:'JOBI0100':'*INT':ID:X'00000000');\n" +
                "if %subst(INFO:1:4) = X'00000056' and %subst(INFO:35:16) = ID and %subst(INFO:61:1) = 'I';\nsndpgmmsg 'RPG NATIVE JOB APIS ACCEPTED';\nendif;\nseton LR;", null);
            rpgSources.SaveSourceMember("QGPL", "SOURCE", "CLSYSVAL", "PGM\nDCL &INFO *CHAR LEN(128)\n" +
                "CALL QSYS/QWCRSVAL PARM(&INFO (128 (*INT 4)) (3 (*INT 4)) 'QCCSID    QSECURITY QPWDLVL   ' X'00000000')\n" +
                "IF (%BIN(&INFO 1 4) *EQ 3 *AND %BIN(&INFO 5 4) *EQ 16 *AND %BIN(&INFO 33 4) *EQ 37 *AND %SST(&INFO 53 2) *EQ '40' *AND %BIN(&INFO 73 4) *EQ 3) THEN(SNDPGMMSG MSG('CL SYSTEM VALUE API ACCEPTED'))\nENDPGM", null);
            rpgSources.SaveSourceMember("QGPL", "SOURCE", "RPGSYSVAL", "**free\ndcl-s INFO char(128) ccsid(*hex);\ndcl-s SIZE int(10) inz(128);\ndcl-s COUNT int(10) inz(3);\n" +
                "callp QWCRSVAL(INFO:SIZE:COUNT:'QCCSID    QSECURITY QPWDLVL   ':X'00000000');\n" +
                "if %subst(INFO:1:4) = X'00000003' and %subst(INFO:33:4) = X'00000025' and %subst(INFO:53:2) = '40' and %subst(INFO:73:4) = X'00000003';\n" +
                "sndpgmmsg 'RPG SYSTEM VALUE API ACCEPTED';\nendif;\nseton LR;", null);
            new DisplayFileStore(system.Connections).Create("QGPL", "RPGFORM",
                FixtureController.Dds(keywords: "CA03(03) CF06(06)") + FixtureController.Dds("ENTRY", record: true) +
                FixtureController.Dds(row: 1, column: 2, keywords: "'Compiled RPG display'") +
                FixtureController.Dds("NAME", 10, row: 4, column: 2, usage: 'B') +
                FixtureController.Dds("AMOUNT", 5, row: 6, column: 2, usage: 'B', type: 'Y', decimals: 2));
            rpgSources.SaveSourceMember("QGPL", "SOURCE", "RPGFORM", "**free\ndcl-f RPGFORM workstn extdesc('QGPL/RPGFORM') extfile(*extdesc);\n" +
                "eval NAME = 'START';\neval AMOUNT = 1.25;\nexfmt ENTRY;\nif NAME <> 'ALICE' or AMOUNT <> 2.5 or not *in06;\n" +
                "sndpgmmsg 'RPG INPUT:' + NAME + ':' + %char(AMOUNT) + ':' + %char(*in06);\nseton LR;\nreturn;\nendif;\n" +
                "eval NAME = 'SENT';\nwrite ENTRY;\neval NAME = 'AFTER';\nread RPGFORM;\nif *in03 and NAME = 'AFTER';\nsndpgmmsg 'RPG DISPLAY ACCEPTED';\nendif;\nseton LR;", null);
            new DisplayFileStore(system.Connections).Create("QGPL", "RPGROWS",
                FixtureController.Dds(keywords: "CA03(03)") + FixtureController.Dds("ROWS", record: true, keywords: "SFL") +
                FixtureController.Dds("VALUE", 10, row: 4, column: 2, usage: 'B') + FixtureController.Dds("SECRET", 4, usage: 'H') +
                FixtureController.Dds("CTL", record: true, keywords: "SFLCTL(ROWS) SFLSIZ(10) SFLPAG(2) SFLDSP SFLDSPCTL SFLEND(*MORE)") +
                FixtureController.Dds(row: 1, column: 2, keywords: "'Compiled RPG subfile'"));
            rpgSources.SaveSourceMember("QGPL", "SOURCE", "RPGROWS", "**free\ndcl-f RPGROWS workstn extdesc('QGPL/RPGROWS') extfile(*extdesc) sfile(ROWS:RRN);\n" +
                "dcl-s RRN packed(4:0);\neval RRN = 1;\neval VALUE = 'ONE';\neval SECRET = 'HIDE';\nwrite ROWS;\neval RRN = 2;\neval VALUE = 'TWO';\nwrite ROWS;\n" +
                "exfmt CTL;\nreadc ROWS;\nif RRN = 1 and VALUE = 'EDITED' and SECRET = 'HIDE' and not %eof(RPGROWS);\neval VALUE = 'UPDATED';\nupdate ROWS;\n" +
                "readc ROWS;\nif %eof;\nchain 1 ROWS;\nif %found and VALUE = 'UPDATED';\nsndpgmmsg 'RPG SUBFILE ACCEPTED';\nendif;\nendif;\nendif;\nseton LR;", null);
            new DisplayFileStore(system.Connections).Create("QGPL", "RPGMSGS",
                FixtureController.Dds(keywords: "HELP CA03(03)") + FixtureController.Dds("ROWS", record: true, keywords: "SFL SFLMSGRCD(4)") +
                FixtureController.Dds("KEY", keywords: "SFLMSGKEY") + FixtureController.Dds("QUEUE", keywords: "SFLPGMQ") +
                FixtureController.Dds("CTL", record: true, keywords: "SFLCTL(ROWS) SFLSIZ(2) SFLPAG(2) SFLDSP SFLDSPCTL SFLINZ SFLEND(*MORE)") +
                FixtureController.Dds("PGMQ", keywords: "SFLPGMQ") + FixtureController.Dds(row: 1, column: 2, keywords: "'Compiled RPG message subfile'"));
            rpgSources.SaveSourceMember("QGPL", "SOURCE", "RPGMSG", "**free\ndcl-f RPGMSGS workstn extdesc('QGPL/RPGMSGS') extfile(*extdesc) sfile(ROWS:RRN);\n" +
                "dcl-s RRN packed(4:0);\ncallp RPGMSGPRP();\neval PGMQ = '*';\nexfmt CTL;\nsndpgmmsg 'RPG MESSAGE SUBFILE ACCEPTED';\nseton LR;", null);
            rpgSources.SaveSourceMember("QGPL", "SOURCE", "RPGMSGPRP", "PGM\nCRTMSGF MSGF(QTEMP/RPGERRORS)\n" +
                "ADDMSGD MSGID(ERR0001) MSGF(QTEMP/RPGERRORS) MSG('RPG row &1') SECLVL('Correct RPG &1 and retry.') FMT((*CHAR 3))\n" +
                "SNDPGMMSG MSGID(ERR0001) MSGF(QTEMP/RPGERRORS) MSGDTA('ABC') TOPGMQ(*PRV)\n" +
                "SNDPGMMSG MSG('RPG row 2') TOPGMQ(*PRV)\nSNDPGMMSG MSG('RPG row 3') TOPGMQ(*PRV)\nENDPGM", null);
            rpgSources.SaveSourceMember("QGPL", "SOURCE", "RPGNATIVE", "**free\ndcl-f RPGMSGS workstn extdesc('QGPL/RPGMSGS') extfile(*extdesc) sfile(ROWS:RRN);\n" +
                "dcl-s RRN packed(4:0);\ndcl-s INFO char(256) ccsid(*hex);\ndcl-s SIZE int(10) inz(256);\n" +
                "callp QMHSNDPM('ERR0001':'RPGERRORS QTEMP     ':'ABC':3:'*DIAG':'*':0:KEY:X'00000000');\n" +
                "callp QMHRCVPM(INFO:SIZE:'RCVM0200':'*':0:'*DIAG':KEY:0:'*SAME':X'00000000');\n" +
                "if %subst(INFO:180:11) <> 'RPG row ABC' or %subst(INFO:22:4) <> KEY;\nsndpgmmsg 'NATIVE RECEIVE FAILED';\nseton LR;\nreturn;\nendif;\n" +
                "callp QMHRTVM(INFO:SIZE:'RTVM0100':'ERR0001':'RPGERRORS QTEMP     ':'ABC':3:'*YES':'*NO':X'00000000');\n" +
                "if %subst(INFO:25:11) <> 'RPG row ABC';\nsndpgmmsg 'NATIVE RETRIEVE FAILED';\nseton LR;\nreturn;\nendif;\n" +
                "eval PGMQ = '*';\nexfmt CTL;\nsndpgmmsg 'NATIVE RPG MESSAGE APIS ACCEPTED';\nseton LR;", null);
            new DisplayFileStore(system.Connections).Create("QGPL", "CLSCREEN",
                FixtureController.Dds(keywords: "HELP CA03(03) CF06(06)") + FixtureController.Dds("ENTRY", record: true) +
                FixtureController.Dds(row: 1, column: 2, keywords: "'Compiled CL display'") +
                FixtureController.Dds("NAME", 10, row: 4, column: 2, usage: 'B') +
                FixtureController.Dds("AMOUNT", 5, row: 6, column: 2, usage: 'B', type: 'Y', decimals: 2));
            new Ipc.Db.Store.SqliteFileStore(system.Connections, system.Objects).SaveSourceMember("QGPL", "SOURCE", "CLSCREEN",
                "PGM\nDCLF QGPL/CLSCREEN\nCHGVAR &NAME 'START'\nCHGVAR &AMOUNT 1.25\nSNDRCVF\n" +
                "IF (&NAME *NE 'ALICE' *OR &AMOUNT *NE 2.50 *OR &IN06 *NE '1') THEN(RETURN)\n" +
                "CHGVAR &NAME 'SENT'\nSNDF\nCHGVAR &NAME 'AFTER'\nRCVF\n" +
                "IF (&IN03 *EQ '1' *AND &NAME *EQ 'AFTER') THEN(SNDPGMMSG MSG('CL DISPLAY ACCEPTED'))\nENDPGM", null);
            new DisplayFileStore(system.Connections).Create("QGPL", "CLMSGS",
                FixtureController.Dds(keywords: "HELP CA03(03)") + FixtureController.Dds("ROWS", record: true, keywords: "SFL SFLMSGRCD(4)") +
                FixtureController.Dds("KEY", keywords: "SFLMSGKEY") + FixtureController.Dds("QUEUE", keywords: "SFLPGMQ") +
                FixtureController.Dds("CTL", record: true, keywords: "SFLCTL(ROWS) SFLSIZ(2) SFLPAG(2) SFLDSP SFLDSPCTL SFLEND(*MORE)") +
                "     A  21".PadRight(44) + "SFLINZ\n" + FixtureController.Dds("PGMQ", keywords: "SFLPGMQ") +
                FixtureController.Dds(row: 1, column: 2, keywords: "'Compiled CL message subfile'"));
            new Ipc.Db.Store.SqliteFileStore(system.Connections, system.Objects).SaveSourceMember("QGPL", "SOURCE", "CLMSGDSP",
                "PGM\nDCLF QGPL/CLMSGS RCDFMT(CTL)\nCHGVAR &PGMQ 'CLMSGDSP'\nCHGVAR &IN21 '1'\n" +
                "CRTMSGF MSGF(QTEMP/CLERRORS)\nADDMSGD MSGID(ERR0001) MSGF(QTEMP/CLERRORS) MSG('Row &1') SECLVL('Correct &1 and retry.') FMT((*CHAR 3))\n" +
                "SNDPGMMSG MSGID(ERR0001) MSGF(QTEMP/CLERRORS) MSGDTA('ABC') TOPGMQ(*SAME)\n" +
                "SNDPGMMSG MSG('Row 2') TOPGMQ(*SAME)\nSNDPGMMSG MSG('Row 3') TOPGMQ(*SAME)\nSNDF RCDFMT(CTL)\n" +
                "CHGVAR &IN21 '0'\nRMVMSG CLEAR(*ALL)\nDLTMSGF MSGF(QTEMP/CLERRORS)\nRCVF RCDFMT(CTL)\n" +
                "IF (&IN03 *EQ '1' *AND &IN21 *EQ '0') THEN(SNDPGMMSG MSG('CL MESSAGE SUBFILE ACCEPTED'))\nENDPGM", null);
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
            files.SaveSourceMember("QGPL", "SOURCE", "CLTMPFILE",
                "PGM\nDCLF FILE(QTEMP/PRIVATE)\nRCVF\nIF (&VALUE *EQ 'LF ACCEPTED') THEN(SNDPGMMSG MSG('QTEMP FILES ACCEPTED'))\nCLOSE\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLPREP",
                "PGM\n/DEFINE ENABLED\n/IF DEFINED(ENABLED)\n/INCLUDE CLINC\n/ELSE\nUNAVAILABLE\n/ENDIF\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLINC", "SNDPGMMSG MSG('PREPROCESS ACCEPTED')", null);
            files.SaveSourceMember("QGPL", "SOURCE", "RPGPREP",
                "**free\n/DEFINE ENABLED\n/IF DEFINED(ENABLED)\n/COPY QGPL/SOURCE,RPGINC\n/ELSE\n/COPY MISSING\n/ENDIF", null);
            files.SaveSourceMember("QGPL", "SOURCE", "RPGINC",
                "/TITLE RPG inclusion acceptance\n/SPACE 2\n/EJECT\n/INCLUDE QGPL/SOURCE,RPGPART", null);
            files.SaveSourceMember("QGPL", "SOURCE", "RPGPART", "sndpgmmsg 'RPG PREPROCESS ACCEPTED';", null);
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
            system.Objects.Create(new Ipc.Core.Objects.ObjectDescriptor { Key = new("QGPL", "PRIVATE"), ObjectType = "*PGM", Attribute = "CLP",
                Source = "PGM PARM(&N)\nDCL &N *DEC LEN(5 0)\nCHGVAR &N (&N + 1)\nENDPGM" });
            files.SaveSourceMember("QGPL", "SOURCE", "RPGPARSE",
                RpgLine('D', (7, "DATA"), (21, "A"), (38, "12")) + RpgLine('D', (7, "VALUE"), (21, "P"), (38, "5"), (40, "0")) +
                "**free\neval DATA = 'RPG PARSED';\ncallp PRIVATE(VALUE);\nif VALUE = 1;\ndsply DATA;\nendif;", null);
            files.SaveSourceMember("QGPL", "SOURCE", "RPGBAD", "**free\nsndpgmmsg 'must not run';\nBOGUS;", null);
            system.Objects.Create(new Ipc.Core.Objects.ObjectDescriptor { Key = new("QGPL", "RPGADJUST"), ObjectType = "*PGM", Attribute = "RPG",
                Source = RpgLine('D', (7, "VALUE"), (21, "P"), (38, "5"), (40, "0")) +
                    RpgLine('C', (17, "*ENTRY"), (35, "PLIST")) + RpgLine('C', (17, "VALUE"), (35, "PARM")) +
                    RpgLine('C', (35, "ADD"), (42, "7"), (47, "VALUE")) });
            files.SaveSourceMember("QGPL", "SOURCE", "CLRPG",
                "PGM\nDCL &N *DEC LEN(5 0) VALUE(5)\nCALL PGM(QGPL/RPGADJUST) PARM(&N)\n" +
                "IF (&N *EQ 12) THEN(SNDPGMMSG MSG('RPG WRITEBACK ACCEPTED'))\nENDPGM", null);
            system.Objects.Create(new Ipc.Core.Objects.ObjectDescriptor { Key = new("QGPL", "RPGDQAPI"), ObjectType = "*PGM", Attribute = "RPG",
                Source = RpgLine('D', (7, "Q"), (21, "A"), (38, "10")) + RpgLine('D', (7, "LIB"), (21, "A"), (38, "10")) +
                    RpgLine('D', (7, "LENGTH"), (21, "P"), (38, "5"), (40, "0")) + RpgLine('D', (7, "TEXT"), (21, "A"), (38, "8")) +
                    RpgLine('D', (7, "WAIT"), (21, "P"), (38, "5"), (40, "0")) + RpgLine('D', (7, "KEYLEN"), (21, "P"), (38, "3"), (40, "0")) +
                    RpgLine('D', (7, "KEY"), (21, "A"), (38, "2")) + RpgLine('D', (7, "SLEN"), (21, "P"), (38, "3"), (40, "0")) +
                    RpgLine('D', (7, "SENDER"), (21, "A"), (38, "44")) + """
                    **free
                    eval Q = 'WORKDQ';
                    eval LIB = 'QGPL';
                    eval LENGTH = 5;
                    eval TEXT = 'READY';
                    eval KEYLEN = 2;
                    eval KEY = 'AB';
                    eval SLEN = 44;
                    callp QSNDDTAQ(Q:LIB:LENGTH:TEXT:KEYLEN:KEY);
                    eval TEXT = 'TAILTAIL';
                    callp QRCVDTAQ(Q:LIB:LENGTH:TEXT:WAIT:'EQ':KEYLEN:KEY:SLEN:SENDER);
                    if LENGTH = 5;
                    if TEXT = 'READYAIL';
                    sndpgmmsg 'DIRECT RPG API ACCEPTED';
                    endif;
                    endif;
                    callp QCLRDTAQ(Q:LIB);
                    """ });
            string Prototype(string name, string api, params (string Name, string Type, string Width)[] fields)
                => RpgLine('D', (7, name), (21, "PR"), (50, "EXTPGM('" + api + "')")) + string.Concat(fields.Select(field =>
                    field.Type == "P" ? RpgLine('D', (7, field.Name), (21, field.Type), (38, field.Width), (40, "0"), (50, "PARM")) :
                    RpgLine('D', (7, field.Name), (21, field.Type), (38, field.Width), (50, "PARM"))));
            var prototypeQueueSource = Prototype("SEND", "QSNDDTAQ", ("Q", "A", "10"), ("LIB", "A", "10"), ("LENGTH", "P", "5"), ("TEXT", "A", "8"), ("KEYLEN", "P", "3"), ("KEY", "A", "2")) +
                Prototype("RECV", "QRCVDTAQ", ("Q", "A", "10"), ("LIB", "A", "10"), ("LENGTH", "P", "5"), ("TEXT", "A", "8"), ("WAIT", "P", "5"), ("ORDER", "A", "2"), ("KEYLEN", "P", "3"), ("KEY", "A", "2"), ("SLEN", "P", "3"), ("SENDER", "A", "44")) +
                Prototype("CLEAR", "QCLRDTAQ", ("Q", "A", "10"), ("LIB", "A", "10")) +
                system.Objects.GetRequired("QGPL", "RPGDQAPI", "*PGM").Source!.Replace("callp QSNDDTAQ", "callp SEND").Replace("callp QRCVDTAQ", "callp RECV").Replace("callp QCLRDTAQ", "callp CLEAR").Replace("DIRECT RPG API ACCEPTED", "PROTOTYPE API ACCEPTED");
            system.Objects.Create(new Ipc.Core.Objects.ObjectDescriptor { Key = new("QGPL", "RPGPRAPI"), ObjectType = "*PGM", Attribute = "RPG", Source = prototypeQueueSource });
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
            (string Name, string Width)[] dataAreaFields = [("RESULT", "40"), ("RLEN", "4"), ("QUAL", "20"), ("START", "4"), ("COUNT", "4"), ("ERROR", "4")];
            system.Objects.Create(new Ipc.Core.Objects.ObjectDescriptor { Key = new("QGPL", "RPGDAAPI"), ObjectType = "*PGM", Attribute = "RPG",
                Source = string.Concat(dataAreaFields.Select(f => RpgLine('D', (7, f.Name), (21, "A"), (38, f.Width)))) +
                    RpgLine('C', (17, "*ENTRY"), (35, "PLIST")) + string.Concat(dataAreaFields.Select(f => RpgLine('C', (17, f.Name), (35, "PARM")))) +
                    "/free\ncallp QWCRDTAA(RESULT:RLEN:QUAL:START:COUNT:ERROR);\n/end-free\n" });
            files.SaveSourceMember("QGPL", "SOURCE", "CLDAAPI", """
                PGM
                DCL &DATA *CHAR LEN(40)
                CALL QSYS/QWCRDTAA PARM(&DATA (39 (*INT 4)) 'PRICE     QGPL      ' (-1 (*INT 4)) (1 (*INT 4)) X'00000000')
                IF (%BIN(&DATA 1 4) *NE 39 *OR %BIN(&DATA 5 4) *NE 39 *OR %BIN(&DATA 29 4) *NE 5 *OR %SST(&DATA 37 3) *NE X'01249C') THEN(RETURN)
                CALL QSYS/QWCRDTAA PARM(&DATA (39 (*INT 4)) '*LDA                ' (2 (*INT 4)) (3 (*INT 4)) X'00000000')
                IF (%SST(&DATA 37 3) *NE 'ABC') THEN(RETURN)
                CHGVAR &DATA 'RESET'
                CALL QGPL/RPGDAAPI PARM(&DATA X'00000027' '*LDA                ' X'00000002' X'00000003' X'00000000')
                IF (%SST(&DATA 37 3) *EQ 'ABC') THEN(SNDPGMMSG MSG('DATA AREA API ACCEPTED'))
                ENDPGM
                """, null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLRESP",
                "PGM\nDCL &KEY *CHAR LEN(4)\nRCVMSG MSGQ(QGPL/INBOX) RMV(*NO) KEYVAR(&KEY)\n" +
                "SNDRPY MSGQ(QGPL/INBOX) MSGKEY(&KEY) RPY('YES')\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLTMPMSG", """
                PGM
                DCL &DATA *CHAR LEN(20)
                DCL &LIB *CHAR LEN(10)
                DCL &ACTUAL *CHAR LEN(10)
                SNDPGMMSG MSGID(USR0001) MSGF(QTEMP/TEXTS) MSGDTA('ABC') TOMSGQ(QTEMP/PRIVATE)
                RCVMSG MSGQ(QTEMP/PRIVATE) RMV(*NO) MSG(&DATA) MSGFLIB(&LIB) SNDMSGFLIB(&ACTUAL)
                IF (&DATA *EQ 'Private ABC' *AND &LIB *EQ 'QTEMP' *AND &ACTUAL *EQ 'QTEMP') THEN(SNDPGMMSG MSG('PRIVATE MSGQ ACCEPTED'))
                ENDPGM
                """, null);
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
            files.SaveSourceMember("QGPL", "SOURCE", "CLINQOP",
                "PGM\nSNDPGMMSG MSGID(USR0002) MSGF(QGPL/CLMSGS) MSGTYPE(*INQ) TOMSGQ(QGPL/INBOX) RPYMSGQ(QGPL/REPLIES)\nENDPGM", null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLDQWORK", """
                PGM
                DCL &DATA *CHAR LEN(8) VALUE(X'FF00FE0100000000')
                DCL &KEY *CHAR LEN(2) VALUE(X'0011')
                DCL &RETURNED *CHAR LEN(2)
                DCL &SIZE *DEC LEN(5 0)
                SNDDTAQ QGPL/WORKDQ DATA(&DATA) LEN(4) KEY(&KEY)
                RCVDTAQ QGPL/WORKDQ KEY(&KEY) RMV(*NO) RTNVAR(&DATA) LENVAR(&SIZE) KEYVAR(&RETURNED)
                IF (&SIZE *NE 4 *OR %SST(&DATA 1 4) *NE X'FF00FE01' *OR &RETURNED *NE &KEY) THEN(RETURN)
                RCVDTAQ QGPL/WORKDQ KEY(&KEY) RTNVAR(&DATA)
                SNDDTAQ QGPL/WORKDQ DATA('CLEAR ME') KEY(&KEY)
                CLRDTAQ QGPL/WORKDQ KEY(&KEY)
                RCVDTAQ QGPL/WORKDQ KEY(&KEY) LENVAR(&SIZE)
                IF (&SIZE *EQ 0) THEN(SNDPGMMSG MSG('DATA QUEUE ACCEPTED'))
                ENDPGM
                """, null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLDQAPI", """
                PGM
                DCL &DATA *CHAR LEN(8) VALUE(X'0707070707070707')
                DCL &KEY *CHAR LEN(2) VALUE(X'0011')
                DCL &LENGTH *DEC LEN(5 0) VALUE(99)
                DCL &SENDER *CHAR LEN(44)
                DCL &ERROR *CHAR LEN(16) VALUE(X'00000010000000000000000000000000')
                CALL QSNDDTAQ PARM('WORKDQ' 'QGPL' X'00004C' X'FF00FE01' X'002C' &KEY)
                CALL QRCVDTAQ PARM('WORKDQ' 'QGPL' &LENGTH &DATA X'00000C' 'EQ' X'002C' &KEY X'044C' &SENDER '*NO' X'00002C' &ERROR '*MSEC')
                IF (&LENGTH *NE 4 *OR &DATA *NE X'FF00070707070707' *OR %SST(&SENDER 1 8) *NE X'0000044C0000044C' *OR %SST(&ERROR 5 4) *NE X'00000000') THEN(RETURN)
                CALL QCLRDTAQ PARM('WORKDQ' 'QGPL' 'EQ' X'002C' &KEY &ERROR)
                CALL QRCVDTAQ PARM('WORKDQ' 'QGPL' &LENGTH &DATA X'00000C' 'EQ' X'002C' &KEY X'000C' '')
                IF (&LENGTH *EQ 0 *AND &DATA *EQ X'FF00070707070707') THEN(SNDPGMMSG MSG('NATIVE QUEUE API ACCEPTED'))
                ENDPGM
                """, null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLUSAPI", """
                PGM
                DCL &DATA *CHAR LEN(8)
                DCL &ATTR *CHAR LEN(24)
                DCL &TEXT *CHAR LEN(50)
                CALL QSYS/QUSCRTUS PARM('WORKUS    QTEMP     ' '' (8 (*INT 4)) X'FE' '*CHANGE' &TEXT)
                CALL QSYS/QUSCHGUS PARM('WORKUS    QTEMP     ' (3 (*INT 4)) (4 (*INT 4)) X'FF00FE01' '2')
                CALL QSYS/QUSRTVUS PARM('WORKUS    QTEMP     ' (1 (*INT 4)) (8 (*INT 4)) &DATA)
                CALL QSYS/QUSRUSAT PARM(&ATTR (24 (*INT 4)) 'SPCA0100' 'WORKUS    QTEMP     ' X'00000000')
                IF (&DATA *EQ X'FEFEFF00FE01FEFE' *AND %BIN(&ATTR 9 4) *EQ 8) THEN(SNDPGMMSG MSG('USER SPACE API ACCEPTED'))
                CALL QSYS/QUSDLTUS PARM('WORKUS    QTEMP     ' X'00000000')
                ENDPGM
                """, null);
            files.SaveSourceMember("QGPL", "SOURCE", "CLCHKRPY",
                "PGM\nDCL &RPY *CHAR LEN(3)\nDCL &TYPE *CHAR LEN(2)\n" +
                "RCVMSG MSGQ(QGPL/REPLIES) MSGTYPE(*RPY) MSG(&RPY) RTNTYPE(&TYPE)\n" +
                "IF (&RPY *EQ 'Y' *AND &TYPE *EQ '22') THEN(SNDPGMMSG MSG('OPERATOR VALIDATION ACCEPTED'))\nENDPGM", null);
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
    if (args[1] == "groups")
        system.Objects.Create(new Ipc.Core.Objects.ObjectDescriptor { Key = new("QGPL", "GROUPCHK"), ObjectType = "*PGM", Attribute = "CLP",
            Source = "PGM\nDCL &GROUP *CHAR LEN(5)\nDCL &LOCAL *CHAR LEN(5)\nRTVDTAARA DTAARA(*GDA (1 5)) RTNVAR(&GROUP)\nRTVDTAARA DTAARA(*LDA (1 5)) RTNVAR(&LOCAL)\nSNDPGMMSG MSG(&GROUP + ':' + &LOCAL)\nENDPGM" });
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

sealed class MessageSubfileController : ISessionController, IDisposable
{
    private readonly Ipc.Services.IpcSystem _system;
    private readonly MenuController _owner;
    private readonly DisplayFileSession _file;
    private readonly string _report;
    private HelpSession? _help;
    private int _helpOpened;
    public DisplayBuffer Buffer => _help?.Buffer ?? _file.Buffer;
    public MessageSubfileController(Ipc.Services.IpcSystem system, string report)
    {
        _system = system; _report = report;
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "MessageFixture22");
        _owner = new MenuController(system, system.Security.Profiles.Get("QSECOFR"));
        using var identity = Ipc.Services.Events.OperationIdentity.Enter("QSECOFR", _owner.JobKey);
        var environment = system.JobRuntime.Environment(_owner.JobKey)!;
        var messages = new Ipc.Services.Messages.MessageQueueStore(system.Connections); var queue = environment.MessageQueue();
        var descriptions = new Ipc.Services.Messages.MessageDescriptionStore(system.Connections);
        descriptions.Create("QTEMP", "ERRORS", "");
        descriptions.Add("QTEMP", "ERRORS", "ERR0001", new Ipc.Services.Messages.MessageDescription("Message 1 &1", "Correct &1 and retry the transaction.", 30,
            new[] { new Ipc.Services.Messages.MessageDataField("*CHAR", 3) }, 37));
        var snapshot = descriptions.Snapshot("QTEMP", "ERRORS", "ERR0001", "QTEMP", new(System.Text.Encoding.GetEncoding(37).GetBytes("ABC"), 37));
        var text = Ipc.Services.Messages.MessageDescriptionFormat.Format(snapshot.Description, snapshot.Replacement, 37).Text;
        messages.SendTo(new[] { queue }, text, messageId: "ERR0001", severity: 30, predefined: snapshot);
        for (var n = 2; n <= 5; n++) messages.SendTo(new[] { queue }, new(System.Text.Encoding.GetEncoding(37).GetBytes("Message " + n), 37));
        var source = FixtureController.Dds(keywords: "HELP CA03(03)") + FixtureController.Dds("ROWS", record: true, keywords: "SFL SFLMSGRCD(4)") +
            FixtureController.Dds("KEY", keywords: "SFLMSGKEY") + FixtureController.Dds("QUEUE", keywords: "SFLPGMQ") +
            FixtureController.Dds("CTL", record: true, keywords: "SFLCTL(ROWS) SFLSIZ(2) SFLPAG(2) SFLDSP SFLDSPCTL SFLINZ SFLEND(*MORE)") +
            FixtureController.Dds("PGMQ", keywords: "SFLPGMQ") + FixtureController.Dds(row: 1, column: 2, keywords: "'Live message subfile'");
        var store = new DisplayFileStore(system.Connections); store.Create("QGPL", "MESSAGES", source);
        _file = store.Open("QGPL", "MESSAGES", environment);
        _file.Write("CTL", new Dictionary<string, object?> { ["PGMQ"] = "*EXT" });
        // Subsequent paging and help must use output snapshots after the queue and MSGF are removed.
        messages.RemoveFrom(queue, Ipc.Services.Messages.MessageClear.All);
        system.Objects.Delete("QTEMP", "ERRORS", Ipc.Core.Objects.ObjectType.MessageFile);
    }
    public SessionEvent Handle(KeyPress key)
    {
        using var identity = Ipc.Services.Events.OperationIdentity.Enter("QSECOFR", _owner.JobKey);
        if (_help is not null)
        { if (_help.Handle(key)) _help = null; return new(_help is null ? "DisplayPanel" : "Help"); }
        var response = _file.Handle(key);
        if (response.MessageHelp is { } details)
        {
            _helpOpened++;
            var definition = new HelpDefinition { Name = "IPCHELP", Modules = new() { new HelpModule { Name = "MESSAGE", Title = details.Title,
                Blocks = new() { new() { Spans = new() { new(details.Text) } }, new() { Spans = new() { new(details.SecondLevel) } } } } } };
            _help = new("*BUILTIN", "MESSAGE", _ => definition, Buffer.Rows, Buffer.Columns); return new("Help");
        }
        if (!response.Accepted) return new("DisplayPanel");
        var environment = _system.JobRuntime.Environment(_owner.JobKey)!;
        var remaining = new Ipc.Services.Messages.MessageQueueStore(_system.Connections).SnapshotProgramMessages(environment.MessageQueue()).Count;
        File.WriteAllText(_report, JsonSerializer.Serialize(new { Rows = _file.Subfile("ROWS").Count, HelpOpened = _helpOpened, Remaining = remaining }));
        _owner.EndSession(Ipc.Core.Work.JobCompletion.Normal, "Message subfile fixture ended.");
        return new("Done", EndSession: true);
    }
    public void RequestDisconnect() => _owner.RequestDisconnect();
    public void Dispose() => _owner.Dispose();
}

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
