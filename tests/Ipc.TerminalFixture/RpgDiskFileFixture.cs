using Ipc.Db.Definitions;
using Ipc.Db.Store;

internal static class RpgDiskFileFixture
{
    internal static void Seed(SqliteFileStore files)
    {
        var format = new RecordFormat { Name = "DISKROW", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8, Sequence = 1 },
            new() { Name = "TEXT", Type = FieldType.Alpha, Length = 12 }] }; format.AssignPositions();
        files.CreatePhysicalFile("QGPL", "GDISK", new() { Name = "GDISK", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        foreach (var id in new[] { 20L, 10L }) files.Insert("QGPL", "GDISK", "GDISK", "DISKROW", new Dictionary<string, object?> { ["ID"] = id, ["TEXT"] = "*BLANKS" });
        files.SaveSourceMember("QGPL", "SOURCE", "RPGFILES", """
            **free
            dcl-f MODEL template keyed block(*no) extdesc('QGPL/GDISK');
            dcl-f COPY likefile(MODEL) extfile('QGPL/GDISK');
            dcl-ds BUFFER likerec(DISKROW);
            read COPY.DISKROW BUFFER;
            if BUFFER.ID=10 and BUFFER.TEXT='*BLANKS';
              sndpgmmsg 'FILE TYPES OK';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGREC", """
            **free
            dcl-f ROWS keyed extdesc('QGPL/GDISK') extfile(*extdesc) rename(DISKROW:REC);
            dcl-ds BUFFER likerec(REC);
            read ROWS BUFFER;
            if BUFFER.ID=10 and BUFFER.TEXT='*BLANKS' and ID=0 and %size(BUFFER)=20;
              sndpgmmsg 'RECORD DS OK';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGDISK", """
            **free
            dcl-f ARRIVAL extdesc('QGPL/GDISK') extfile(*extdesc) rename(DISKROW:ARRROW) prefix(A_);
            dcl-f LOOKUP keyed extdesc('QGPL/GDISK') extfile(*extdesc) rename(DISKROW:KEYROW) prefix(K_) usropn;
            read ARRROW;
            if A_ID<>20 or A_TEXT<>'*BLANKS' or %open(LOOKUP);
              sndpgmmsg 'DISK ARRIVAL FAILED';
              seton LR;
              return;
            endif;
            open LOOKUP;
            read LOOKUP;
            if K_ID<>10;
              sndpgmmsg 'DISK KEYED FAILED';
              seton LR;
              return;
            endif;
            chain 20 KEYROW;
            close LOOKUP;
            if K_ID=20 and %found(LOOKUP) and not %open(LOOKUP);
              sndpgmmsg 'RPG DISK INPUT ACCEPTED';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGUPD", """
            **free
            dcl-f CHANGE keyed usage(*delete) extdesc('QGPL/GDISK') extfile(*extdesc) rename(DISKROW:CHANGEROW) prefix(U_);
            chain(n) 20 CHANGE;
            if U_ID<>20;
              sndpgmmsg 'UPDATE INQUIRY FAILED';
              seton LR;
              return;
            endif;
            chain 20 CHANGE;
            unlock CHANGE;
            chain 20 CHANGE;
            eval U_TEXT='changed';
            update CHANGEROW;
            delete 10 CHANGE;
            chain 20 CHANGE;
            if U_ID=20 and U_TEXT='changed';
              sndpgmmsg 'RPG UPDATE LOCKS ACCEPTED';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGOUTPUT", """
            **free
            dcl-f APPEND keyed usage(*input:*output) extdesc('QGPL/GDISK') extfile(*extdesc) rename(DISKROW:OUTROW) prefix(O_) usropn;
            open APPEND;
            eval O_ID=30;
            eval O_TEXT='added';
            write OUTROW;
            chain 30 APPEND;
            close APPEND;
            if O_ID=30 and O_TEXT='added' and not %open(APPEND);
              sndpgmmsg 'RPG OUTPUT ACCEPTED';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGSTATUS", """
            **free
            dcl-f STATUSFILE keyed usage(*update) extdesc('QGPL/GDISK') extfile(*extdesc) rename(DISKROW:STATUSROW) usropn;
            monitor;
              read(e) STATUSFILE;
              if not %error or %status(STATUSFILE)<>1211;
                return;
              endif;
              open(e) STATUSFILE;
              if %error or %status<>0;
                return;
              endif;
              chain(ne) 20 STATUSFILE;
              update STATUSROW;
            on-error 1221;
              if not %error and %status=1221;
                sndpgmmsg 'RPG FILE STATUS ACCEPTED';
              endif;
            endmon;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGERRSR", """
            **free
            dcl-f DATA extdesc('QGPL/GDISK') extfile(*extdesc) usropn infsr(FILEERR);
            dcl-s N int(10);
            dcl-ds P psds qualified;
              STATUS *status;
              FILE_STATUS zoned(5) pos(209);
            end-ds;
            read DATA;
            return;
            tag RETRY;
            eval N=1/0;
            return;
            begsr FILEERR;
              if P.FILE_STATUS=1211;
                goto RETRY;
              endif;
              return;
            endsr;
            begsr *pssr;
              if P.STATUS=102;
                sndpgmmsg 'RPG ERROR ROUTINES ACCEPTED';
              endif;
              return;
            endsr;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGFDBK", """
            **free
            dcl-f DATA extdesc('QGPL/GDISK') extfile(*extdesc) usropn infds(F);
            dcl-ds F qualified;
              STATUS *status;
              OP *opcode;
              IS_OPEN ind pos(9);
            end-ds;
            dcl-ds P psds qualified;
              NAME *proc;
              STATUS *status;
              LAST_FILE char(10) pos(175);
              FILE_STATUS zoned(5) pos(209);
            end-ds;
            read(e) DATA;
            if F.STATUS<>1211 or P.FILE_STATUS<>1211 or P.LAST_FILE<>'DATA';
              return;
            endif;
            open DATA;
            read DATA;
            if F.STATUS=0 and F.OP='READ F' and F.IS_OPEN and P.STATUS=0 and P.NAME='RPGFDBK';
              sndpgmmsg 'RPG FEEDBACK ACCEPTED';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGNATDS", """
            **free
            dcl-ds INFO qualified len(128);
              COUNT int(10) pos(1);
              OFFSET int(10) pos(5);
              CCSID int(10) pos(33);
              SECURITY char(2) pos(53);
              PASSWORD_LEVEL int(10) pos(73);
            end-ds;
            dcl-ds ERROR qualified inz len(16);
              PROVIDED int(10) inz(16);
              AVAILABLE int(10);
              IDENTIFIER char(7);
            end-ds;
            callp QWCRSVAL(INFO:%size(INFO):3:'QCCSID    QSECURITY QPWDLVL   ':ERROR);
            if INFO.COUNT=3 and INFO.OFFSET=16 and INFO.CCSID=37 and INFO.SECURITY='40' and INFO.PASSWORD_LEVEL=3 and ERROR.AVAILABLE=0;
              sndpgmmsg 'NATIVE DS STORAGE ACCEPTED';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "CLWAIT", """
            PGM
            CHGPF FILE(QGPL/GDISK) WAITRCD(2)
            OVRDBF FILE(GDISK) TOFILE(*FILE) WAITRCD(*NOMAX) OVRSCOPE(*JOB)
            DLTOVR FILE(GDISK) LVL(*JOB)
            SNDPGMMSG MSG('RECORD WAIT POLICY ACCEPTED')
            ENDPGM
            """, null);
    }
}
