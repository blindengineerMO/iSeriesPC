using Ipc.Db.Store;

internal static class InteractiveSqlFixture
{
    internal static void Seed(SqliteFileStore files)
    {
        SqlTableFixture.Seed(files);files.CreateSourceFile("QGPL","QSQLSRC",sourceWidth:240);
        files.SaveSourceMember("QGPL","SOURCE","SQLLIVE","""
            PGM
            STRSQL
            RUNSQL SQL('UPDATE QTEMP/SQLDATA SET NAME=''after'' WHERE ID=8') COMMIT(*NONE)
            SNDPGMMSG MSG('STRSQL CL RETURN ACCEPTED')
            ENDPGM
            """,null);
        files.CreateSourceFile("QGPL", "QQMQRYSRC", sourceWidth: 240);
        files.SaveSourceMember("QGPL", "QQMQRYSRC", "SQLREPORT", "SELECT ID,NAME FROM QTEMP/SQLDATA WHERE ID=&ID", null);
        files.SaveSourceMember("QGPL", "QQMQRYSRC", "SQLCHANGE", "UPDATE QTEMP/SQLDATA SET NAME=&NAME WHERE ID=&ID", null);
        files.SaveSourceMember("QGPL", "SOURCE", "QRYLIVE", """
            PGM
            STRQMQRY QGPL/SQLREPORT SETVAR((ID 8))
            SNDPGMMSG MSG('QMQRY CL RETURN ACCEPTED')
            ENDPGM
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "OUTCL", """
            PGM
            DCLF FILE(QTEMP/SQLCOPY)
            STRQMQRY QGPL/SQLREPORT OUTPUT(*OUTFILE) OUTFILE(QTEMP/SQLCOPY) SETVAR((ID 8))
            RCVF
            IF (&ID *EQ 8 *AND &NAME *EQ 'qmqry') THEN(SNDPGMMSG MSG('QMQRY OUTFILE CL ACCEPTED'))
            ENDPGM
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "OUTRPG", """
            **free
            dcl-f SQLCOPY extdesc('QTEMP/SQLCOPY') extfile(*extdesc) usage(*input);
            read SQLCOPY;
            if not %eof(SQLCOPY) and ID=8 and %trim(NAME)='qmqry';
              sndpgmmsg 'QMQRY OUTFILE RPG ACCEPTED';
            endif;
            seton LR;
            """, null);
    }
}
