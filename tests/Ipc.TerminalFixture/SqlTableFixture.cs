using Ipc.Db.Store;

internal static class SqlTableFixture
{
    internal static void Seed(SqliteFileStore files)
    {
        files.SaveSourceMember("QGPL", "SOURCE", "SQLFKRPG", """
            **free
            dcl-f SQLCHILD extdesc('QGPL/SQLCHILD') extfile(*extdesc) usage(*output) prefix('C_');
            eval C_ID=2;
            write(e) SQLCHILD;
            if not %error or %status(SQLCHILD)<>1022;
            sndpgmmsg 'BAD FK STATUS';
            return;
            endif;
            eval C_ID=1;
            write(e) SQLCHILD;
            if not %error and %status(SQLCHILD)=0;
            sndpgmmsg 'SQL FK RPG ACCEPTED';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SQLFKCL", """
            PGM
            RUNSQL SQL('INSERT INTO QGPL/SQLCHILD VALUES(2)') COMMIT(*NONE)
            MONMSG MSGID(SQL0530) EXEC(SNDPGMMSG MSG('SQL FK CL ACCEPTED'))
            ENDPGM
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SQLFKCHK", """
            **free
            dcl-f SQLCHILD extdesc('QGPL/SQLCHILD') extfile(*extdesc) usage(*input) prefix('C_');
            read SQLCHILD;
            if %eof(SQLCHILD);
            sndpgmmsg 'SQL FK DELETE ACCEPTED';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SQLCKRPG", """
            **free
            dcl-f SQLDATA extdesc('QTEMP/SQLDATA') extfile(*extdesc) usage(*output);
            eval ID=9;
            eval NAME='badcheck';
            eval FLAG=-1;
            write(e) SQLDATA;
            if not %error or %status(SQLDATA)<>1299;
            sndpgmmsg 'BAD CHECK STATUS';
            return;
            endif;
            eval FLAG=6;
            write(e) SQLDATA;
            if not %error and %status(SQLDATA)=0;
            sndpgmmsg 'SQL CHECK RPG ACCEPTED';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SQLCKCL", """
            PGM
            RUNSQL SQL('UPDATE QTEMP/SQLDATA SET FLAG=-1 WHERE ID=8') COMMIT(*NONE)
            MONMSG MSGID(SQL0545) EXEC(SNDPGMMSG MSG('SQL CHECK CL ACCEPTED'))
            ENDPGM
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SQLALTRPG", """
            **free
            dcl-f SQLDATA extdesc('QTEMP/SQLDATA') extfile(*extdesc) usage(*input);
            read SQLDATA;
            if not %eof(SQLDATA) and ID=8 and FLAG=5 and %trim(NAME)='changed';
            sndpgmmsg 'SQL ALTER RPG ACCEPTED';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SQLALTCL", """
            PGM
            DCLF FILE(QTEMP/SQLDATA)
            RCVF
            IF COND(&ID *EQ 8 *AND &FLAG *EQ 5 *AND &NAME *EQ 'changed') THEN(SNDPGMMSG MSG('SQL ALTER CL ACCEPTED'))
            ENDPGM
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SQLSCRIPT", """
            INSERT INTO QTEMP/SQLDATA(ID) VALUES(7);
            UPDATE QTEMP/SQLDATA SET ID=ID+1,NAME='changed' WHERE ID=7;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SQLCL", """
            PGM
            DCLF FILE(QTEMP/SQLDATA)
            RUNSQLSTM SRCFILE(QGPL/SOURCE) SRCMBR(SQLSCRIPT) COMMIT(*NONE) OPTION(*NOLIST)
            RCVF
            IF COND(&ID *EQ 8 *AND &NAME *EQ 'changed') THEN(SNDPGMMSG MSG('SQL CL ACCEPTED'))
            ENDPGM
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SQLRPG", """
            **free
            dcl-f SQLDATA extdesc('QTEMP/SQLDATA') extfile(*extdesc) usage(*input);
            read SQLDATA;
            if %eof(SQLDATA);
            sndpgmmsg 'SQL DELETE ACCEPTED';
            else;
            if ID=8 and %trim(NAME)='changed';
            sndpgmmsg 'SQL RPG ACCEPTED';
            endif;
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SQLIXRPG", """
            **free
            dcl-f SQLIDX keyed extdesc('QTEMP/SQLIDX') extfile(*extdesc) usage(*input);
            chain 8 SQLIDX;
            if %found(SQLIDX) and ID=8 and %trim(NAME)='changed';
            sndpgmmsg 'SQL INDEX RPG ACCEPTED';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SQLVWRPG", """
            **free
            dcl-f SQLVIEW extdesc('QTEMP/SQLVIEW') extfile(*extdesc) usage(*input);
            read SQLVIEW;
            if not %eof(SQLVIEW) and ID=8 and %trim(NAME)='changed';
            sndpgmmsg 'SQL VIEW RPG ACCEPTED';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SQLVWCL", """
            PGM
            DCLF FILE(QTEMP/SQLVIEW)
            RCVF
            IF COND(&ID *EQ 8 *AND &NAME *EQ 'changed') THEN(SNDPGMMSG MSG('SQL VIEW CL ACCEPTED'))
            ENDPGM
            """, null);
    }
}
