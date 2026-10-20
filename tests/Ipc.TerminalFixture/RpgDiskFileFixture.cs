using Ipc.Db.Definitions;
using Ipc.Db.Store;

internal static class RpgDiskFileFixture
{
    internal static void Seed(SqliteFileStore files)
    {
        var format = new RecordFormat { Name = "DISKROW", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8, Sequence = 1 },
            new() { Name = "TEXT", Type = FieldType.Alpha, Length = 12 }] }; format.AssignPositions();
        files.CreatePhysicalFile("QGPL", "GDISK", new() { Name = "GDISK", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        files.CreatePhysicalFile("QGPL", "GINIT", new() { Name = "GINIT", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        files.SaveSourceMember("QGPL", "SOURCE", "CLINIT", """
            PGM
            DCLF FILE(QGPL/GINIT)
            CHGPF FILE(QGPL/GINIT) SIZE(2 2 3)
            INZPFM FILE(QGPL/GINIT) TOTRCDS(3)
            RCVF
            CLOSE
            CLRPFM FILE(QGPL/GINIT)
            INZPFM FILE(QGPL/GINIT) RECORDS(*DLT)
            CHGPF FILE(QGPL/GINIT) REUSEDLT(*YES)
            IF COND(&TEXT *EQ ' ') THEN(SNDPGMMSG MSG('INITIALIZATION DEFAULTS ACCEPTED'))
            ENDPGM
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGINIT", """
            **free
            dcl-f GINIT usage(*input:*output);
            eval ID=77;
            eval TEXT='reuse';
            write DISKROW;
            chain 1 GINIT;
            if %found(GINIT) and ID=77 and %trim(TEXT)='reuse';
              sndpgmmsg 'INITIALIZATION REUSE ACCEPTED';
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGRECNO", """
            **free
            dcl-f GINIT usage(*input:*output) recno(RRN);
            dcl-s RRN packed(10:0) inz(99);
            if RRN<>0;
              return;
            endif;
            eval RRN=2;
            eval ID=88;
            eval TEXT='relative';
            write DISKROW;
            setll 2 GINIT;
            if RRN<>2;
              return;
            endif;
            read GINIT;
            if ID=88 and RRN=2 and %trim(TEXT)='relative';
              sndpgmmsg 'RECNO RELATIVE WRITE ACCEPTED';
            endif;
            seton LR;
            """, null);
        foreach (var id in new[] { 20L, 10L }) files.Insert("QGPL", "GDISK", "GDISK", "DISKROW", new Dictionary<string, object?> { ["ID"] = id, ["TEXT"] = "*BLANKS" });
        var nullable = new RecordFormat { Name = "NULLROW", Fields = [new() { Name = "NKEY", Type = FieldType.Binary, Length = 4, Sequence = 1, NullCapable = true },
            new() { Name = "NTEXT", Type = FieldType.Alpha, Length = 8, NullCapable = true }] }; nullable.AssignPositions();
        files.CreatePhysicalFile("QGPL", "GNULL", new() { Name = "GNULL", Attribute = FileAttribute.Physical, Formats = [nullable] }, "");
        files.Insert("QGPL", "GNULL", "GNULL", "NULLROW", new Dictionary<string, object?> { ["NKEY"] = null, ["NTEXT"] = null });
        files.SaveSourceMember("QGPL", "SOURCE", "RPGNULL", """
            **free
            ctl-opt alwnull(*usrctl);
            dcl-f GNULL keyed qualified usage(*update);
            dcl-ds KEYS likerec(GNULL.NULLROW:*key) inz;
            dcl-ds BUFFER likerec(GNULL.NULLROW:*all) inz;
            eval %nullind(KEYS.NKEY)=*on;
            chain %kds(KEYS) GNULL BUFFER;
            if %found(GNULL) and %nullind(BUFFER.NTEXT);
              eval BUFFER.NTEXT='PRESENT';
              eval %nullind(BUFFER.NTEXT)=*off;
              update GNULL.NULLROW BUFFER;
              chain %kds(KEYS) GNULL BUFFER;
              if not %nullind(BUFFER.NTEXT) and BUFFER.NTEXT='PRESENT';
                sndpgmmsg 'RPG NULL VALUES OK';
              endif;
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGNMAP", """
            **free
            ctl-opt alwnull(*usrctl);
            dcl-f GNULL keyed qualified usage(*update);
            dcl-ds KEYS likerec(GNULL.NULLROW:*key) nullind(KEYFLAGS) inz;
            dcl-ds KEYFLAGS likerec(GNULL.NULLROW:*key:*null) inz;
            dcl-ds DATA qualified extname('QGPL/GNULL':*all) nullind(FLAGS) inz end-ds;
            dcl-ds FLAGS qualified extname('QGPL/GNULL':*all:*null) inz end-ds;
            eval KEYFLAGS.NKEY=*on;
            chain %kds(KEYS) GNULL DATA;
            if %found(GNULL) and not FLAGS.NTEXT;
              eval FLAGS.NTEXT=*on;
              update GNULL.NULLROW DATA;
              chain %kds(KEYS) GNULL DATA;
              if FLAGS.NTEXT and %nullind(DATA.NTEXT);
                sndpgmmsg 'RPG NAMED NULL MAP OK';
              endif;
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGNPRM", """
            **free
            ctl-opt alwnull(*usrctl);
            dcl-f GNULL keyed qualified usage(*update);
            dcl-ds MODEL likerec(GNULL.NULLROW:*all) template;
            dcl-ds DATA likeds(MODEL) inz;
            dcl-s FLAG ind inz(*on);
            callp LOAD(DATA);
            if %nullind(DATA.NTEXT);
              eval DATA.NTEXT='PARAM';
              eval %nullind(DATA.NTEXT)=*off;
              callp SAVE(DATA);
              callp LOAD(DATA);
              callp NAMED(DATA.NTEXT:FLAG);
              if DATA.NTEXT='PARAM' and not %nullind(DATA.NTEXT) and not FLAG;
                sndpgmmsg 'RPG NULL PARAMETERS OK';
              endif;
            endif;
            seton LR;
            dcl-proc LOAD;
            dcl-pi *n;
              P likeds(MODEL) options(*nullind);
            end-pi;
            dcl-ds KEYS likerec(GNULL.NULLROW:*key) nullind(FLAGS) inz;
            dcl-ds FLAGS likerec(GNULL.NULLROW:*key:*null) inz;
            eval FLAGS.NKEY=*on;
            chain %kds(KEYS) GNULL P;
            end-proc;
            dcl-proc SAVE;
            dcl-pi *n;
              P likeds(MODEL) options(*nullind);
            end-pi;
            update GNULL.NULLROW P;
            end-proc;
            dcl-proc NAMED;
            dcl-pi *n;
              P char(8) nullind(F);
              F ind;
            end-pi;
            if %nullind(P);
              eval %nullind(P)=*off;
            endif;
            end-proc;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGCORR", """
            **free
            ctl-opt alwnull(*usrctl);
            dcl-f GNULL keyed qualified usage(*update);
            dcl-ds FROMDATA likerec(GNULL.NULLROW:*all) inz;
            dcl-ds TODATA qualified inz;
              NTEXT char(10) nullind;
              NKEY int(10) nullind;
            end-ds;
            dcl-ds KEYS likerec(GNULL.NULLROW:*key) nullind(FLAGS) inz;
            dcl-ds FLAGS likerec(GNULL.NULLROW:*key:*null) inz;
            eval FLAGS.NKEY=*on;
            chain %kds(KEYS) GNULL FROMDATA;
            if %found(GNULL);
              eval-corr TODATA=FROMDATA;
              if %nullind(TODATA.NKEY) and TODATA.NTEXT='PARAM' and not %nullind(TODATA.NTEXT);
                sndpgmmsg 'RPG CORRESPONDING NULLS OK';
              endif;
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGRIGHT", """
            **free
            ctl-opt alwnull(*usrctl);
            dcl-f GNULL keyed qualified usage(*update);
            dcl-ds DATA likerec(GNULL.NULLROW:*all) inz;
            dcl-ds KEYS likerec(GNULL.NULLROW:*key) nullind(FLAGS) inz;
            dcl-ds FLAGS likerec(GNULL.NULLROW:*key:*null) inz;
            eval FLAGS.NKEY=*on;
            chain %kds(KEYS) GNULL DATA;
            if %found(GNULL);
              evalr DATA.NTEXT='RIGHT';
              update GNULL.NULLROW DATA;
              evalr %subst(DATA.NTEXT:1:3)='X';
              if DATA.NTEXT='  XRIGHT' and %nullind(DATA.NKEY) and not %nullind(DATA.NTEXT);
                sndpgmmsg 'RPG RIGHT ADJUST OK';
              endif;
            endif;
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "MMAIN", """
            **free
            ctl-opt alwnull(*usrctl);
            dcl-pr TARGET extproc('FILL');
              P char(8) options(*nullind);
            end-pr;
            dcl-s TEXT char(8) nullind;
            callp TARGET(TEXT);
            if TEXT='   BOUND' and %nullind(TEXT);
              sndpgmmsg 'RPG MODULE BIND OK';
            endif;
            return;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "MHELP", """
            **free
            ctl-opt nomain alwnull(*usrctl);
            dcl-s COUNT int(10);
            dcl-proc FILL export;
            dcl-pi *n;
              P char(8) options(*nullind);
            end-pi;
            eval COUNT=COUNT+1;
            evalr P='BOUND';
            eval %nullind(P)=*on;
            end-proc;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SBINDER", """
            STRPGMEXP SIGNATURE('SERVICE V2')
            EXPORT SYMBOL(FILL)
            ENDPGMEXP
            STRPGMEXP PGMLVL(*PRV) SIGNATURE('SERVICE V1')
            EXPORT SYMBOL(FILL)
            ENDPGMEXP
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SCMOD", """
            **free
            ctl-opt nomain;
            dcl-s COUNT int(10);
            dcl-proc BUMP export;
            dcl-pi *n int(10) end-pi;
            eval COUNT=COUNT+1;
            return COUNT;
            end-proc;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SCOUNT", """
            **free
            dcl-pr BUMP int(10);
            end-pr;
            dcl-s N int(10);
            eval N=BUMP();
            sndpgmmsg 'RPG SERVICE COUNT '+%char(N);
            return;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SDMOD", """
            **free
            ctl-opt nomain;
            dcl-pr BUMP int(10);
            end-pr;
            dcl-proc NEXTCOUNT export;
            dcl-pi *n int(10) end-pi;
            return BUMP();
            end-proc;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SDCALL", """
            **free
            dcl-pr NEXTCOUNT int(10);
            end-pr;
            dcl-s N int(10);
            eval N=NEXTCOUNT();
            sndpgmmsg 'RPG DEPENDENCY COUNT '+%char(N);
            return;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SCMODV2", """
            **free
            ctl-opt nomain;
            dcl-s COUNT int(10);
            dcl-proc BUMP export;
            dcl-pi *n int(10) end-pi;
            eval COUNT=COUNT+10;
            return COUNT;
            end-proc;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SMREF", """
            **free
            ctl-opt bnddir('QGPL/SMREFDIR');
            dcl-pr BUMP int(10);
            end-pr;
            dcl-s N int(10);
            eval N=BUMP();
            sndpgmmsg 'RPG SOURCE DIRECTORY '+%char(N);
            return;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "SDUNUSED", """
            **free
            dcl-pr BUMP int(10);
            end-pr;
            dcl-s N int(10);
            if 1=0;
            eval N=BUMP();
            endif;
            sndpgmmsg 'RPG DEFERRED READY';
            return;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "XDSTORE", """
            **free
            ctl-opt nomain;
            dcl-s SHARED int(10) inz(10) export;
            dcl-proc GETSHARED export;
            dcl-pi *n int(10) end-pi;
            return SHARED;
            end-proc;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "XDMAIN", """
            **free
            dcl-s LOCAL int(10) import('SHARED');
            dcl-pr GETSHARED int(10);
            end-pr;
            dcl-s N int(10);
            eval LOCAL=LOCAL+1;
            eval N=GETSHARED();
            sndpgmmsg 'RPG DATA SHARED '+%char(N);
            seton LR;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "XPSTORE", """
            **free
            ctl-opt nomain;
            dcl-s COUNT int(10);
            dcl-proc GETPTR export;
            dcl-pi *n pointer(*proc) end-pi;
            return %paddr(NEXT);
            end-proc;
            dcl-proc NEXT;
            dcl-pi *n int(10) end-pi;
            eval COUNT=COUNT+1;
            return COUNT;
            end-proc;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "XPMAIN", """
            **free
            dcl-pr GETPTR pointer(*proc) end-pr;
            dcl-s FP pointer(*proc);
            dcl-pr RUN int(10) extproc(FP) end-pr;
            dcl-s N int(10);
            if FP=*null;
            eval FP=GETPTR();
            endif;
            monitor;
            eval N=RUN();
            sndpgmmsg 'RPG POINTER COUNT '+%char(N);
            on-error 202;
            sndpgmmsg 'RPG POINTER EXPIRED';
            endmon;
            return;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "XBMSTORE", """
            **free
            ctl-opt nomain;
            dcl-s COUNT int(10) inz(10);
            dcl-proc GETADDR export;
            dcl-pi *n pointer end-pi;
            return %addr(COUNT);
            end-proc;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "XBMMAIN", """
            **free
            dcl-pr GETADDR pointer end-pr;
            dcl-s P pointer;
            dcl-s VIEW int(10) based(P);
            if P=*null;
            eval P=GETADDR();
            endif;
            monitor;
            eval VIEW=VIEW+1;
            sndpgmmsg 'RPG BASED VALUE '+%char(VIEW);
            on-error 222;
            sndpgmmsg 'RPG BASED EXPIRED';
            endmon;
            return;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGHEAP", """
            **free
            dcl-s P pointer;
            dcl-s NUMBER int(10) based(P);
            eval P=%alloc(4);
            eval NUMBER=123;
            eval P=%realloc(P:8);
            sndpgmmsg 'RPG HEAP VALUE '+%char(NUMBER);
            dealloc(n) P;
            return;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGLOCAL", """
            **free
            dcl-s A int(20);
            dcl-s B int(20);
            dcl-s C int(20);
            dcl-s D int(20);
            eval A=AUTOMATIC_ROW();
            eval B=AUTOMATIC_ROW();
            eval C=STATIC_ROW();
            eval D=STATIC_ROW();
            if A=10 and B=10 and C=10 and D=20;
              sndpgmmsg 'LOCAL FILES OK';
            endif;
            seton LR;
            dcl-proc AUTOMATIC_ROW;
            dcl-f LOCAL keyed extdesc('QGPL/GDISK') extfile(*extdesc);
            dcl-pi *n int(20) end-pi;
            dcl-ds BUFFER likerec(DISKROW);
            read LOCAL BUFFER;
            return BUFFER.ID;
            end-proc;
            dcl-proc STATIC_ROW;
            dcl-f LOCAL keyed static extdesc('QGPL/GDISK') extfile(*extdesc);
            dcl-pi *n int(20) end-pi;
            dcl-ds BUFFER likerec(DISKROW);
            read LOCAL BUFFER;
            return BUFFER.ID;
            end-proc;
            """, null);
        files.SaveSourceMember("QGPL", "SOURCE", "RPGFPRM", """
            **free
            dcl-f MODEL qualified template keyed block(*no) extdesc('QGPL/GDISK');
            dcl-f COPY likefile(MODEL) extfile('QGPL/GDISK') usropn;
            dcl-s FIRST int(20);
            dcl-s SECOND int(20);
            eval FIRST=NEXTROW(COPY);
            eval SECOND=NEXTROW(COPY);
            if FIRST=10 and SECOND=20 and %open(COPY);
              sndpgmmsg 'FILE PARAMETERS OK';
            endif;
            seton LR;
            dcl-proc NEXTROW;
            dcl-pi *n int(20);
              F likefile(MODEL);
            end-pi;
            dcl-ds BUFFER likerec(F.DISKROW);
            if not %open(F);
              open F;
            endif;
            read F BUFFER;
            return BUFFER.ID;
            end-proc;
            """, null);
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
