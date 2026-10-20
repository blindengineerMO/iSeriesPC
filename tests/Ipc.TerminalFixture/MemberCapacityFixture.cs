using Ipc.Db.Definitions;
using Ipc.Db.Store;

internal static class MemberCapacityFixture
{
    internal static void Seed(SqliteFileStore files)
    {
        var format=new RecordFormat{Name="CAPROW",Fields=[new(){Name="ID",Type=FieldType.Binary,Length=8},new(){Name="TEXT",Type=FieldType.Alpha,Length=12}]};format.AssignPositions();
        files.CreatePhysicalFile("QGPL","GCAP",new(){Name="GCAP",Attribute=FileAttribute.Physical,Formats=[format],MemberSize=new(1,2,1)},"");
        for(var id=1;id<=3;id++)files.Insert("QGPL","GCAP","GCAP","CAPROW",new Dictionary<string,object?>{["ID"]=(long)id,["TEXT"]="original"});
        files.SaveSourceMember("QGPL","SOURCE","RPGCAP","""
            **free
            dcl-f GCAP usage(*output);
            eval ID=4;
            eval TEXT='operator';
            write CAPROW;
            sndpgmmsg 'CAPACITY REPLY ACCEPTED';
            seton LR;
            """,null);
        var initializationFormat = new RecordFormat { Name = "INZROW", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 4 }] };
        initializationFormat.AssignPositions();
        files.CreatePhysicalFile("QGPL","GINZCAP",new(){Name="GINZCAP",Attribute=FileAttribute.Physical,Formats=[initializationFormat],MemberSize=new(1,2,1)},"");
        for(var id=1;id<=3;id++)files.Insert("QGPL","GINZCAP","GINZCAP","INZROW",new Dictionary<string,object?>{["ID"]=(long)id});
        files.SaveSourceMember("QGPL","SOURCE","CLINZCAP","""
            PGM
            DCL &CANCELED *CHAR LEN(1) VALUE(N)
            DCLF FILE(QGPL/GINZCAP)
            ADDRPYLE SEQNBR(8300) MSGID(CPA3138) CMPDTA(GINZCAP 1) RPY(I)
            CHGJOB INQMSGRPY(*SYSRPYL)
            INZPFM FILE(QGPL/GINZCAP) TOTRCDS(5)
            CHGJOB INQMSGRPY(*DFT)
            INZPFM FILE(QGPL/GINZCAP) TOTRCDS(7)
            MONMSG MSGID(CPF3140) EXEC(CHGVAR VAR(&CANCELED) VALUE(Y))
            CHGJOB INQMSGRPY(*RQD)
            RMVRPYLE SEQNBR(8300)
            RCVF
            RCVF
            RCVF
            RCVF
            RCVF
            IF COND(&ID *EQ 0 *AND &CANCELED *EQ 'Y') THEN(SNDPGMMSG MSG('INZPFM CAPACITY ACCEPTED'))
            ENDPGM
            """,null);
    }
}
