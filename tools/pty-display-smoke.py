#!/usr/bin/env python3
"""Drive compiled display DDS through the real TtySession on both supported screen sizes."""
import argparse
import codecs
import fcntl
import json
import os
from pathlib import Path
import pty
import re
import select
import struct
import subprocess
import tempfile
import termios
import time


class Screen:
    def __init__(self, rows, columns):
        self.rows, self.columns = rows, columns
        self.cells = [[' '] * columns for _ in range(rows)]
        self.row = self.column = 0
        self.pending = ''
        self.decoder = codecs.getincrementaldecoder('utf-8')('replace')

    def feed(self, data):
        self.pending += self.decoder.decode(data)
        while self.pending:
            if self.pending[0] == '\x1b':
                if len(self.pending) >= 2 and self.pending[1] != '[':
                    self.pending = self.pending[2:]
                    continue
                match = re.match(r'\x1b\[([0-?]*)([ -/]*)([@-~])', self.pending)
                if not match:
                    return
                parameters, _, final = match.groups()
                args = [int(n) if n else 1 for n in parameters.split(';')] if '?' not in parameters else []
                if final in ('H', 'f'):
                    self.row = (args[0] if args else 1) - 1
                    self.column = (args[1] if len(args) > 1 else 1) - 1
                elif final == 'J' and args == [2]:
                    self.cells = [[' '] * self.columns for _ in range(self.rows)]
                self.pending = self.pending[match.end():]
            else:
                ch, self.pending = self.pending[0], self.pending[1:]
                if ch == '\r':
                    self.column = 0
                elif ch == '\n':
                    self.row += 1
                elif ch >= ' ':
                    if 0 <= self.row < self.rows and 0 <= self.column < self.columns:
                        self.cells[self.row][self.column] = ch
                    self.column += 1

    def line(self, row):
        return ''.join(self.cells[row - 1])


def run(dotnet, assembly, wide):
    master, slave = pty.openpty()
    original = termios.tcgetattr(slave)
    screen = Screen(24, 80)

    def resize(rows, columns):
        nonlocal screen
        screen = Screen(rows, columns)
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', rows, columns, 0, 0))

    resize(24, 80)

    def attach():
        os.setsid()
        fcntl.ioctl(0, termios.TIOCSCTTY, 0)

    with tempfile.TemporaryDirectory(prefix='ipc-display-pty-') as directory:
        report = str(Path(directory) / 'result.json')
        process = subprocess.Popen([dotnet, assembly, report, 'wide' if wide else 'normal'],
                                   stdin=slave, stdout=slave, stderr=slave, preexec_fn=attach)
        output = bytearray()

        def wait_for(predicate, description, timeout=10):
            deadline = time.monotonic() + timeout
            while not predicate():
                if time.monotonic() > deadline or process.poll() is not None:
                    raise AssertionError(f'{description}: code={process.poll()}, cursor={(screen.row, screen.column)}, lines={[screen.line(n).strip() for n in (1, 2, 4, 10) if n <= screen.rows]}, pending={screen.pending[:160]!r}, head={output[:250]!r}, tail={output[-250:]!r}')
                if select.select([master], [], [], .05)[0]:
                    data = os.read(master, 65536)
                    output.extend(data)
                    screen.feed(data)

        def key(sequence, aid):
            os.write(master, sequence)
            wait_for(lambda: screen.line(10).strip() == 'Last ' + aid, 'AID ' + aid)

        try:
            if wide:
                wait_for(lambda: 'Resize terminal to 132x27' in screen.line(1), 'wide-size guard')
                resize(27, 132)
            wait_for(lambda: 'Terminal fixture' in screen.line(2), 'compiled panel')
            assert not termios.tcgetattr(slave)[3] & (termios.ICANON | termios.ECHO)
            wait_for(lambda: (screen.row, screen.column) == (3, 1), 'initial input cursor')
            os.write(master, b'ABC\x1b[H\x1b[2~Z\x1b[3~\x1b[F\x1b[2~D')
            wait_for(lambda: screen.line(4)[1:21].rstrip() == 'ZCD', 'insert/replace/delete')
            os.write(master, b'\x1b[200~E\nF\x1b[201~')
            wait_for(lambda: screen.line(4)[1:21].rstrip() == 'ZCDE F', 'bracketed paste')
            assert not screen.line(10).strip(), 'Paste submitted a command'
            key(b'\r', 'Enter')
            resize(12, 40)
            wait_for(lambda: 'Resize terminal' in screen.line(1), 'small-size guard')
            os.write(master, b'LOST')
            time.sleep(.1)
            resize(27 if wide else 24, 132 if wide else 80)
            wait_for(lambda: 'Terminal fixture' in screen.line(2) and screen.line(4)[1:21].rstrip() == 'ZCDE F', 'resize recovery preserves input')
            assert screen.line(4)[1:21].rstrip() == 'ZCDE F', 'Resize lost or admitted input'
            function = [b'\x1bOP', b'\x1bOQ', b'\x1bOR', b'\x1bOS'] + [f'\x1b[{n}~'.encode() for n in [15, 17, 18, 19, 20, 21, 23, 24]]
            for i, sequence in enumerate(function):
                os.write(master, sequence[:1])
                time.sleep(.01)
                key(sequence[1:], 'Pf' + str(i + 1))
            for i, sequence in enumerate(function):
                shifted = ('\x1b[1;2' + 'PQRS'[i]).encode() if i < 4 else sequence[:-1] + b';2~'
                key(shifted, 'Pf' + str(i + 13))
            for sequence, aid in [(b'\x01', 'Pa1'), (b'\x02', 'Pa2'), (b'\x07', 'SysReq'), (b'\x0c', 'Clear'), (b'\x10', 'Print')]:
                key(sequence, aid)
            key(b'\x1b', 'Pa1')
            os.write(master, b'\x06')
            deadline = time.monotonic() + 10
            while process.poll() is None and time.monotonic() < deadline:
                if select.select([master], [], [], .05)[0]:
                    output.extend(os.read(master, 65536))
            assert process.wait(timeout=1) == 0
            assert termios.tcgetattr(slave) == original, 'Terminal settings were not restored'
            assert b'hidden' not in output, 'Hidden field leaked to terminal'
            result = json.loads(Path(report).read_text())
            assert all('Pf' + str(n) in result['Aids'] for n in range(1, 25))
            assert result['Values']['TEXT'] == 'ZCDE F'
            assert result['Values']['NUMBER'] == 1.23
            assert result['Values']['DAY'] == '2024-02-29'
            assert result['Values']['SECRET'] == 'hidden'
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
            os.close(master)
            os.close(slave)


def run_help(dotnet, assembly):
    master, slave = pty.openpty()
    original = termios.tcgetattr(slave)
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 80, 0, 0))
    screen = Screen(24, 80)

    def attach():
        os.setsid()
        fcntl.ioctl(0, termios.TIOCSCTTY, 0)

    with tempfile.TemporaryDirectory(prefix='ipc-help-pty-') as directory:
        process = subprocess.Popen([dotnet, assembly, str(Path(directory) / 'result'), 'help'], stdin=slave, stdout=slave, stderr=slave, preexec_fn=attach)

        def wait_for(predicate, description):
            deadline = time.monotonic() + 10
            while not predicate():
                if process.poll() is not None or time.monotonic() > deadline:
                    raise AssertionError(f'{description}: code={process.poll()}, cursor={(screen.row, screen.column)}, lines={[screen.line(n).strip() for n in (1, 2, 4, 23, 24)]}')
                if select.select([master], [], [], .05)[0]:
                    screen.feed(os.read(master, 65536))

        try:
            wait_for(lambda: 'Selection' in screen.line(23), 'menu')
            os.write(master, b'CRTLIB\x1bOP')
            wait_for(lambda: 'CRTLIB help' in screen.line(1), 'command F1')
            os.write(master, b'\x1bOR')
            wait_for(lambda: 'CRTLIB' in screen.line(23), 'restore command entry')
            os.write(master, b'\x0cRUNPNL FILE(QGPL/ENTRY)\r')
            wait_for(lambda: (screen.row, screen.column) == (3, 1), 'display input')
            os.write(master, b'UNSENT\x1bOP')
            wait_for(lambda: 'General help' in screen.line(1), 'display F1')
            os.write(master, b'\t\r')
            wait_for(lambda: 'Field help' in screen.line(1), 'help link')
            os.write(master, b'\x1b[24~')
            wait_for(lambda: 'General help' in screen.line(1), 'help back')
            os.write(master, b'\x1b[15~customer\r')
            wait_for(lambda: 'Field help' in screen.line(1), 'help index')
            os.write(master, b'\x1bOR')
            wait_for(lambda: screen.line(4)[1:21].rstrip() == 'UNSENT' and (screen.row, screen.column) == (3, 7), 'restore unsent display input and cursor')
            os.write(master, b'\x1bOR')
            wait_for(lambda: 'Selection' in screen.line(23), 'close display preview')
            os.write(master, b'\x1bOR')
            deadline = time.monotonic() + 10
            while process.poll() is None and time.monotonic() < deadline:
                if select.select([master], [], [], .05)[0]:
                    os.read(master, 65536)
            assert process.wait(timeout=1) == 0
            assert termios.tcgetattr(slave) == original
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
            os.close(master)
            os.close(slave)


def run_designer(dotnet, assembly):
    master, slave = pty.openpty()
    original = termios.tcgetattr(slave)
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 80, 0, 0))
    screen = Screen(24, 80)

    def attach():
        os.setsid()
        fcntl.ioctl(0, termios.TIOCSCTTY, 0)

    with tempfile.TemporaryDirectory(prefix='ipc-sda-pty-') as directory:
        report = str(Path(directory) / 'result.json')
        process = subprocess.Popen([dotnet, assembly, report, 'designer'], stdin=slave, stdout=slave, stderr=slave, preexec_fn=attach)

        def wait_for(predicate, description):
            deadline = time.monotonic() + 15
            while not predicate():
                if process.poll() is not None or time.monotonic() > deadline:
                    raise AssertionError(f'{description}: code={process.poll()}, cursor={(screen.row, screen.column)}, lines={[screen.line(n).strip() for n in (1, 2, 4, 6, 22, 23, 24)]}')
                if select.select([master], [], [], .05)[0]:
                    screen.feed(os.read(master, 65536))

        def form(*values):
            data = b'\t'.join(b'\x1b[H\x0b' + value.encode() for value in values)
            os.write(master, data + b'\r')

        def start():
            os.write(master, b'STRSDA SRCFILE(QGPL/SOURCE) SRCMBR(ENTRY)\r')
            wait_for(lambda: 'Screen Design Aid' in screen.line(1), 'STRSDA')
            os.write(master, b'\r')
            wait_for(lambda: 'Fields in MAIN' in screen.line(2), 'record selection')

        def compile(replace):
            os.write(master, b'\x1b[15~')
            wait_for(lambda: 'Compile saved design' in screen.line(1), 'compile dialog')
            form('QGPL', 'ENTRY', 'DSPF', replace)
            wait_for(lambda: 'Operation completed. Source is saved.' in screen.line(24), 'save and compile')

        try:
            wait_for(lambda: 'Selection' in screen.line(23), 'menu')
            start()
            os.write(master, b'\x1b[17~')
            wait_for(lambda: 'Add field' in screen.line(1), 'add field dialog')
            form('CUSTOMER', 'A', 'B', '10', '0', '4', '2', 'Alice', 'RI', '')
            wait_for(lambda: 'CUSTOMER' in screen.line(6), 'field added')
            compile('NO')
            os.write(master, b'\x1bOR')
            wait_for(lambda: 'Selection' in screen.line(23), 'exit saved design')
            start()
            os.write(master, b'2\x1b[18~')
            wait_for(lambda: 'Edit field' in screen.line(1), 'edit reopened field')
            form('CUSTOMER', 'A', 'B', '10', '0', '4', '2', 'Saved', 'RI', '')
            wait_for(lambda: 'Fields in MAIN' in screen.line(2), 'field edited')
            compile('YES')
            os.write(master, b'\x1bOR')
            wait_for(lambda: 'Selection' in screen.line(23), 'exit revised design')
            os.write(master, b'RUNPNL FILE(QGPL/ENTRY)\r')
            wait_for(lambda: screen.line(4)[1:11].rstrip() == 'Saved', 'execute compiled panel')
            os.write(master, b'\x1bOR')
            wait_for(lambda: 'Selection' in screen.line(23), 'return from compiled panel')
            os.write(master, b'\x1bOR')
            deadline = time.monotonic() + 10
            while process.poll() is None and time.monotonic() < deadline:
                if select.select([master], [], [], .05)[0]:
                    os.read(master, 65536)
            assert process.wait(timeout=1) == 0
            assert termios.tcgetattr(slave) == original
            result = json.loads(Path(report).read_text())
            assert "DFT('Saved')" in result['Source']
            fields = result['Definition']['Records'][0]['Fields']
            assert next(field for field in fields if field['Name'] == 'CUSTOMER')['Keywords'][0]['Arguments'] == ['Saved']
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
            os.close(master)
            os.close(slave)


def run_groups(dotnet, assembly):
    master, slave = pty.openpty()
    original = termios.tcgetattr(slave)
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 80, 0, 0))
    screen = Screen(24, 80)
    def attach():
        os.setsid(); fcntl.ioctl(0, termios.TIOCSCTTY, 0)
    try:
        with tempfile.TemporaryDirectory(prefix='ipc-groups-pty-') as directory:
            report = Path(directory) / 'report.json'
            process = subprocess.Popen([dotnet, assembly, str(report), 'groups'], stdin=slave, stdout=slave, stderr=slave, preexec_fn=attach)
            def wait_for(predicate, label):
                deadline = time.monotonic() + 15
                while not predicate():
                    assert process.poll() is None and time.monotonic() < deadline, (label, screen.line(1), screen.line(23), screen.line(24), screen.row, screen.column)
                    if select.select([master], [], [], .05)[0]: screen.feed(os.read(master, 65536))
            def command(text): os.write(master, text.encode() + b'\r')
            try:
                wait_for(lambda: 'Selection' in screen.line(23), 'group menu')
                command('CHGGRPA GRPJOB(HOME)')
                wait_for(lambda: 'Group attributes changed' in screen.line(24), 'group naming')
                command("CHGDTAARA DTAARA(*GDA (1 5)) VALUE('HOMEG')")
                wait_for(lambda: 'data area changed' in screen.line(24).lower(), 'initial group data area creation')
                command("CHGDTAARA DTAARA(*LDA (1 5)) VALUE('HOMEL')")
                wait_for(lambda: 'data area changed' in screen.line(24).lower(), 'initial local data area')
                command('CALL GROUPCHK')
                wait_for(lambda: 'HOMEG:HOMEL' in screen.line(24), 'initial group and local CL retrieval')
                command('RUNPNL FILE(QGPL/ENTRY)')
                wait_for(lambda: (screen.row, screen.column) == (3, 1) and 'Selection' not in screen.line(23), 'panel')
                os.write(master, b'UNSENT')
                wait_for(lambda: 'UNSENT' in screen.line(4) and (screen.row, screen.column) == (3, 7), 'panel edits')
                os.write(master, b'\x07')
                wait_for(lambda: 'System Request' in screen.line(1), 'SysReq over panel')
                command('1')
                wait_for(lambda: 'Selection' in screen.line(23) and 'System Request' not in screen.line(1), 'alternate job')
                command('CALL GROUPCHK')
                wait_for(lambda: 'CPF1046' in screen.line(24), 'secondary job cannot select another group data area')
                command('CHGGRPA GRPJOB(ALTERNATE)')
                wait_for(lambda: 'Group attributes changed' in screen.line(24), 'alternate group')
                command("CHGDTAARA DTAARA(*GDA (1 5)) VALUE('ALTGP')")
                wait_for(lambda: 'data area changed' in screen.line(24).lower(), 'alternate group data area')
                command("CHGDTAARA DTAARA(*LDA (1 5)) VALUE('ALTL1')")
                wait_for(lambda: 'data area changed' in screen.line(24).lower(), 'alternate local data area')
                command('TFRGRPJOB GRPJOB(THIRD)')
                wait_for(lambda: 'Enter an option number' in screen.line(24), 'new group job')
                command('CALL GROUPCHK')
                wait_for(lambda: 'ALTGP:' in screen.line(24) and 'ALTL1' not in screen.line(24), 'new group shares GDA with a fresh LDA')
                command("CHGDTAARA DTAARA(*GDA (1 5)) VALUE('SHARE')")
                wait_for(lambda: 'data area changed' in screen.line(24).lower(), 'shared group mutation')
                command('TFRGRPJOB GRPJOB(*SELECT)')
                wait_for(lambda: 'Transfer to Group Job' in screen.line(1), 'group selector')
                command('ALTERNATE')
                wait_for(lambda: 'Transfer to Group Job' not in screen.line(1), 'group return')
                command('CALL GROUPCHK')
                wait_for(lambda: 'SHARE:ALTL1' in screen.line(24), 'group transfer preserves shared GDA and private LDA')
                os.write(master, b'\x07')
                wait_for(lambda: 'System Request' in screen.line(1), 'alternate SysReq')
                command('1')
                wait_for(lambda: 'UNSENT' in screen.line(4) and (screen.row, screen.column) == (3, 7), 'original panel and cursor restored')
                os.write(master, b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'panel closed')
                command('CALL GROUPCHK')
                wait_for(lambda: 'HOMEG:HOMEL' in screen.line(24), 'secondary group leaves original areas and editor intact')
                command('SIGNOFF')
                deadline = time.monotonic() + 15
                while process.poll() is None and time.monotonic() < deadline:
                    if select.select([master], [], [], .05)[0]: os.read(master, 65536)
                assert process.wait(timeout=1) == 0
                assert termios.tcgetattr(slave) == original
                jobs = json.loads(report.read_text())
                assert len(jobs) == 3 and all(j['Status'] == 'Completed' and j['Completion'] == 'Normal' for j in jobs), jobs
            finally:
                if process.poll() is None: process.kill(); process.wait()
    finally:
        os.close(master); os.close(slave)



def run_work(dotnet, assembly):
    master, slave = pty.openpty()
    original = termios.tcgetattr(slave)
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 80, 0, 0))
    screen = Screen(24, 80)
    def attach():
        os.setsid(); fcntl.ioctl(0, termios.TIOCSCTTY, 0)
    try:
        with tempfile.TemporaryDirectory(prefix='ipc-work-pty-') as directory:
            report = Path(directory) / 'report.json'
            process = subprocess.Popen([dotnet, assembly, str(report), 'work'], stdin=slave, stdout=slave, stderr=slave, preexec_fn=attach)
            def wait_for(predicate, label):
                deadline = time.monotonic() + 15
                while not predicate():
                    assert process.poll() is None and time.monotonic() < deadline, (label, screen.line(1), screen.line(5), screen.line(23), screen.line(24))
                    if select.select([master], [], [], .05)[0]: screen.feed(os.read(master, 65536))
            def send(text): os.write(master, text)
            try:
                wait_for(lambda: 'Selection' in screen.line(23), 'menu')
                send(b'CRTLIB\x1bOS')
                wait_for(lambda: 'Prompt command - CRTLIB' in screen.line(1), 'F4')
                send(b'PTYLIB\tOperator library\x1bOP')
                wait_for(lambda: 'CRTLIB help' in screen.line(1), 'prompt help')
                send(b'\x1bOR')
                wait_for(lambda: 'PTYLIB' in screen.line(4), 'preserved prompt')
                send(b'\r')
                wait_for(lambda: 'Selection' in screen.line(23), 'library created')
                send(b'CRTSRCPF FILE(PTYLIB/SOURCE)\r')
                wait_for(lambda: 'created' in screen.line(24).lower() and 'SOURCE' in screen.line(24), 'library source contents')
                send(b'WRKLIB LIB(PTYLIB)\r')
                wait_for(lambda: 'Work with libraries' in screen.line(1) and 'PTYLIB' in screen.line(5), 'work library')
                send(b'4\r')
                wait_for(lambda: 'Confirm Delete' in screen.line(2), 'confirm delete')
                send(b'\x1b[17~')
                wait_for(lambda: '0 row(s)' in screen.line(2), 'refreshed empty library list')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'return menu')
                send(b'\x1bOS')
                wait_for(lambda: 'Select a command' in screen.line(1), 'blank F4 selection')
                send(b'\x1b[6~')
                wait_for(lambda: 'Page 2/' in screen.line(2), 'command page')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'chooser return')
                send(b'CRTCMD CMD(QGPL/HELLOCMD) PGM(QGPL/CMDCPP) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Command QGPL/HELLOCMD compiled' in screen.line(24), 'command compilation')
                send(b'HELLOCMD ACME\x1bOS')
                wait_for(lambda: 'Prompt command - HELLOCMD' in screen.line(1) and 'ACME' in screen.line(5), 'typed custom command prompt')
                assert 'Count' in screen.line(4) and '10' in screen.line(4), 'Default or prompt order missing'
                send(b'\x1b[H\x0b12\r')
                wait_for(lambda: 'ACME:12' in screen.line(24), 'custom command CPP arguments')
                send(b'DSPCMD CMD(HELLOCMD)\r')
                wait_for(lambda: 'Command QGPL/HELLOCMD' in screen.line(1), 'generated command reference')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'reference return')
                send(b'CRTCLPGM PGM(QGPL/CLSCREEN) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'created' in screen.line(24).lower(), 'compile CL display program')
                send(b'CALL QGPL/CLSCREEN\r')
                wait_for(lambda: 'Compiled CL display' in screen.line(1) and 'START' in screen.line(4), 'compiled SNDRCVF output')
                send(b'\x071\r')
                wait_for(lambda: 'Selection' in screen.line(23), 'alternate job while CL waits')
                send(b'\x071\r')
                wait_for(lambda: 'Compiled CL display' in screen.line(1) and 'START' in screen.line(4), 'restore waiting CL program and editor')
                send(b'\x0bALICE\t\x0b2.50\x1b[17~')
                wait_for(lambda: 'SENT' in screen.line(4), 'CL field/numeric/CF receive and paired SNDF/RCVF')
                send(b'EDIT\x1bOR')
                wait_for(lambda: 'CL DISPLAY ACCEPTED' in screen.line(24), 'CL CA indicator without input transfer')
                send(b'CRTCLPGM PGM(QGPL/CLMSGDSP) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'created' in screen.line(24).lower(), 'compile CL message subfile')
                send(b'CALL QGPL/CLMSGDSP\r')
                wait_for(lambda: 'Compiled CL message subfile' in screen.line(1) and 'Row ABC' in screen.line(4), 'compiled CL SFLINZ output')
                send(b'\x1bOP')
                wait_for(lambda: 'ERR0001' in screen.line(1) and any('Correct ABC and retry.' in screen.line(n) for n in range(2, 10)), 'compiled CL saved second-level help')
                send(b'\x1bOR')
                wait_for(lambda: 'Row ABC' in screen.line(4), 'return to compiled CL message subfile')
                send(b'\x1b[6~')
                wait_for(lambda: 'Row 3' in screen.line(4), 'compiled CL message paging after queue/MSGF removal')
                send(b'\x1bOR')
                wait_for(lambda: 'CL MESSAGE SUBFILE ACCEPTED' in screen.line(24), 'CL receive preserves selection indicators')
                send(b'CRTBNDRPG PGM(QGPL/RPGFORM) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGFORM created' in screen.line(24), 'compile native RPG WORKSTN declarations')
                send(b'CALL QGPL/RPGFORM\r')
                wait_for(lambda: 'Compiled RPG display' in screen.line(1) and 'START' in screen.line(4), 'RPG EXFMT output')
                send(b'\x071\r')
                wait_for(lambda: 'Selection' in screen.line(23), 'alternate job while RPG waits')
                send(b'\x071\r')
                wait_for(lambda: 'Compiled RPG display' in screen.line(1), 'restore waiting RPG program')
                send(b'\x0bALICE\t\x0b2.50\x1b[17~')
                wait_for(lambda: 'SENT' in screen.line(4), 'RPG typed fields and CF indicator followed by WRITE/READ')
                send(b'EDIT\x1bOR')
                wait_for(lambda: 'RPG DISPLAY ACCEPTED' in screen.line(24), 'RPG CA response preserves variables')
                send(b'CRTBNDRPG PGM(QGPL/RPGROWS) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGROWS created' in screen.line(24), 'compile SFILE/RRN binding')
                send(b'CALL QGPL/RPGROWS\r')
                wait_for(lambda: 'Compiled RPG subfile' in screen.line(1) and 'ONE' in screen.line(4) and 'TWO' in screen.line(5), 'RPG subfile WRITE rows')
                send(b'\x0bEDITED\r')
                wait_for(lambda: 'RPG SUBFILE ACCEPTED' in screen.line(24), 'RPG READC/RRN/hidden/UPDATE/CHAIN/EOF/FOUND')
                send(b'CRTCLPGM PGM(QGPL/RPGMSGPRP) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGMSGPRP created' in screen.line(24), 'compile message producer for the RPG call frame')
                send(b'CRTBNDRPG PGM(QGPL/RPGMSG) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGMSG created' in screen.line(24), 'compile RPG message subfile')
                send(b'CALL QGPL/RPGMSG\r')
                wait_for(lambda: 'Compiled RPG message subfile' in screen.line(1) and 'RPG row ABC' in screen.line(4), 'RPG current-frame SFLINZ')
                send(b'\x1bOP')
                wait_for(lambda: 'ERR0001' in screen.line(1) and any('Correct RPG ABC and retry.' in screen.line(n) for n in range(2, 10)), 'RPG message second-level help')
                send(b'\x1bOR\x1b[6~')
                wait_for(lambda: 'RPG row 3' in screen.line(4), 'RPG message subfile paging')
                send(b'\x1bOR')
                wait_for(lambda: 'RPG MESSAGE SUBFILE ACCEPTED' in screen.line(24), 'RPG message exchange resumes invocation')
                send(b'CRTBNDRPG PGM(QGPL/RPGNATIVE) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGNATIVE created' in screen.line(24), 'compile direct native RPG message API caller')
                send(b'CALL QGPL/RPGNATIVE\r')
                wait_for(lambda: 'Compiled RPG message subfile' in screen.line(1) and 'RPG row ABC' in screen.line(4), 'native RPG send, binary receive and message retrieval')
                send(b'\x1bOP')
                wait_for(lambda: 'ERR0001' in screen.line(1) and any('Correct RPG ABC and retry.' in screen.line(n) for n in range(2, 10)), 'native RPG message second-level help')
                send(b'\x1bOR\x1bOR')
                wait_for(lambda: 'NATIVE RPG MESSAGE APIS ACCEPTED' in screen.line(24), 'native RPG message display resumes invocation')
                send(b'WRKAPILS API(QMHRCVPM)\r')
                wait_for(lambda: 'QMHRCVPM' in screen.line(5), 'runtime API registry list')
                send(b'5\r')
                wait_for(lambda: any('RCVM0100' in screen.line(n) and 'RCVM0200' in screen.line(n) for n in range(1, 24)), 'runtime API format details')
                send(b'\x1bOR\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'return from runtime API registry')
                send(b'CRTCLPGM PGM(QGPL/CLSYSVAL) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLSYSVAL created' in screen.line(24), 'compile native system-value CL caller')
                send(b'CALL QGPL/CLSYSVAL\r')
                wait_for(lambda: 'CL SYSTEM VALUE API ACCEPTED' in screen.line(24), 'CL system-value offsets and character/binary types')
                send(b'CRTBNDRPG PGM(QGPL/RPGSYSVAL) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGSYSVAL created' in screen.line(24), 'compile native system-value RPG caller')
                send(b'CALL QGPL/RPGSYSVAL\r')
                wait_for(lambda: 'RPG SYSTEM VALUE API ACCEPTED' in screen.line(24), 'direct RPG system-value binary receiver')
                send(b'WRKAPILS API(QWCRSVAL)\r')
                wait_for(lambda: 'QWCRSVAL' in screen.line(5), 'system-value API discovery')
                send(b'5\r')
                wait_for(lambda: any('Offset directory' in screen.line(n) for n in range(1, 24)), 'system-value API layout details')
                send(b'\x1bOR\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'return from system-value API details')
                send(b'CRTCLPGM PGM(QGPL/CLJOBAPI) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLJOBAPI created' in screen.line(24), 'compile native CL job API caller')
                send(b'CALL QGPL/CLJOBAPI\r')
                wait_for(lambda: 'CL NATIVE JOB APIS ACCEPTED' in screen.line(24), 'CL job list, opaque ID and job information retrieval')
                send(b'CRTBNDRPG PGM(QGPL/RPGJOBAPI) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGJOBAPI created' in screen.line(24), 'compile direct native RPG job API caller')
                send(b'CALL QGPL/RPGJOBAPI\r')
                wait_for(lambda: 'RPG NATIVE JOB APIS ACCEPTED' in screen.line(24), 'RPG job list and opaque ID copy/retrieval')
                send(b'WRKAPILS API(QUSLJOB)\r')
                wait_for(lambda: 'QUSLJOB' in screen.line(5), 'native job API discovery')
                send(b'5\r')
                wait_for(lambda: any('JOBL0100, JOBL0200' in screen.line(n) for n in range(1, 24)), 'native job list format details')
                send(b'\x1bOR\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'return from job API registry')
                send(b'CRTCLPGM PGM(QGPL/CLDATEAPI) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLDATEAPI created' in screen.line(24), 'compile native CL date API caller')
                send(b'CALL QGPL/CLDATEAPI\r')
                wait_for(lambda: 'CL NATIVE DATE API ACCEPTED' in screen.line(24), 'CL binary timestamp conversion')
                send(b'CRTBNDRPG PGM(QGPL/RPGDATEAPI) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGDATEAPI created' in screen.line(24), 'compile direct RPG date API caller')
                send(b'CALL QGPL/RPGDATEAPI\r')
                wait_for(lambda: 'RPG NATIVE DATE API ACCEPTED' in screen.line(24), 'RPG microsecond timestamp conversion')
                send(b'WRKAPILS API(QWCCVTDT)\r')
                wait_for(lambda: 'QWCCVTDT' in screen.line(5), 'native date API discovery')
                send(b'5\r')
                wait_for(lambda: any('5, 10, 11' in screen.line(n) for n in range(1, 24)), 'native date API parameter groups')
                send(b'\x1bOR\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'return from date API registry')
                send(b'CRTCLPGM PGM(QGPL/CLCRYPTO) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLCRYPTO created' in screen.line(24), 'compile native CL cryptography caller')
                send(b'CALL QGPL/CLCRYPTO\r')
                wait_for(lambda: 'CL NATIVE CRYPTO APIS ACCEPTED' in screen.line(24), 'CL hash/HMAC/AES and key-context lifecycle')
                send(b'CRTBNDRPG PGM(QGPL/RPGCRYPTO) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGCRYPTO created' in screen.line(24), 'compile direct RPG cryptography caller')
                send(b'CALL QGPL/RPGCRYPTO\r')
                wait_for(lambda: 'RPG NATIVE CRYPTO APIS ACCEPTED' in screen.line(24), 'RPG hash/HMAC/AES and key-context lifecycle')
                send(b'WRKAPILS API(QC3*)\r')
                wait_for(lambda: 'QC3CALHA' in screen.line(5), 'native cryptographic API discovery')
                send(b'5\r')
                wait_for(lambda: any('ALGD0500' in screen.line(n) for n in range(1, 24)), 'native hash format details')
                send(b'\x1bOR\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'return from cryptographic API registry')
                send(b'CRTJRNRCV JRNRCV(QGPL/PTY0001) THRESHOLD(*NONE)\r')
                wait_for(lambda: 'CRTJRNRCV completed' in screen.line(24), 'create durable journal receiver')
                send(b'CRTJRN JRN(QGPL/PTYJRN) JRNRCV(QGPL/PTY0001) MNGRCV(*USER)\r')
                wait_for(lambda: 'CRTJRN completed' in screen.line(24), 'create journal and attach receiver')
                send(b'SNDJRNE JRN(QGPL/PTYJRN) TYPE(AA) ENTDTA(X\'00FF01\')\r')
                wait_for(lambda: 'Journal entry 1 written' in screen.line(24), 'binary user journal entry')
                send(b'DSPJRN JRN(QGPL/PTYJRN)\r')
                wait_for(lambda: 'Journal entries' in screen.line(1), 'journal work list')
                send(b'5\r')
                wait_for(lambda: any('00FF01' in screen.line(n) for n in range(1, 24)), 'journal entry detail bytes')
                send(b'\x1bOR\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'return from journal entries')
                send(b'CRTSRCPF FILE(QGPL/RECPFS)\r')
                wait_for(lambda: 'Source physical file QGPL/RECPFS created' in screen.line(24), 'create recovery source file')
                send(b'STRJRNPF FILE(QGPL/RECPFS) JRN(QGPL/PTYJRN) IMAGES(*BOTH)\r')
                wait_for(lambda: 'STRJRNPF completed' in screen.line(24), 'attach source record capture')
                send(b"ADDSRCPFM FILE(QGPL/RECPFS) DATA('RESTORE THIS')\r")
                wait_for(lambda: 'added to member RECPFS' in screen.line(24), 'journal source insert')
                send(b'RMVJRNCHG JRN(QGPL/PTYJRN) FILE(QGPL/RECPFS) RCVRNG(*CURRENT) FROMENT(2) TOENT(2)\r')
                wait_for(lambda: '1 record changes removed' in screen.line(24), 'reverse journaled source insert')
                send(b'APYJRNCHG JRN(QGPL/PTYJRN) FILE(QGPL/RECPFS) RCVRNG(*CURRENT) FROMENT(2) TOENT(2)\r')
                wait_for(lambda: '1 record changes applied' in screen.line(24), 'apply journaled source insert')
                send(b'DSPPFM FILE(QGPL/RECPFS)\r')
                wait_for(lambda: 'RESTORE THIS' in '\n'.join(screen.line(n) for n in range(1, 25)), 'verify recovered source data')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'return from recovered source')
                send(b'ENDJRNPF FILE(QGPL/RECPFS)\r')
                wait_for(lambda: 'ENDJRNPF completed' in screen.line(24), 'end source capture')
                send(b'CHGJRN JRN(QGPL/PTYJRN) JRNRCV(*GEN)\r')
                wait_for(lambda: 'Receiver QGPL/PTY0002 attached' in screen.line(24), 'rotate journal receiver')
                send(b'DLTJRNRCV JRNRCV(QGPL/PTY0001) DLTOPT(*IGNINQMSG)\r')
                wait_for(lambda: 'DLTJRNRCV completed' in screen.line(24), 'delete detached receiver with acknowledgement')
                send(b'DLTJRN JRN(QGPL/PTYJRN)\r')
                wait_for(lambda: 'DLTJRN completed' in screen.line(24), 'delete journal and detach receiver')
                send(b'DLTJRNRCV JRNRCV(QGPL/PTY0002) DLTOPT(*IGNINQMSG)\r')
                wait_for(lambda: 'DLTJRNRCV completed' in screen.line(24), 'delete final detached receiver')
                send(b'RMVJRNCHG JRN(QGPL/GRCYCLEJ) FILE(QGPL/GRCYCLE) RCVRNG(*CURRENT) FROMENT(5) TOENT(2)\r')
                wait_for(lambda: '1 record changes removed' in screen.line(24), 'remove a committed record-unit cycle')
                send(b'DSPPFM FILE(QGPL/GRCYCLE)\r')
                wait_for(lambda: 'BEFORE' in '\n'.join(screen.line(n) for n in range(1, 25)), 'verify cycle removal data')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'return from removed cycle data')
                send(b'APYJRNCHG JRN(QGPL/GRCYCLEJ) FILE(QGPL/GRCYCLE) RCVRNG(*CURRENT) FROMENT(2) TOENT(4)\r')
                wait_for(lambda: any('Incomplete commit cycle' in screen.line(n) for n in range(1, 24)), 'display incomplete ending-cycle diagnostic')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'return from commitment boundary diagnostic')
                send(b'APYJRNCHG JRN(QGPL/GRCYCLEJ) FILE(QGPL/GRCYCLE) RCVRNG(*CURRENT) FROMENT(2) TOENT(5)\r')
                wait_for(lambda: '1 record changes applied' in screen.line(24), 'apply a complete record-unit cycle')
                send(b'RMVJRNCHG JRN(QGPL/GRCYCLEJ) FILE(QGPL/GRCYCLE) RCVRNG(*CURRENT) FROMENT(3) TOENT(3) CMTBDY(*NO)\r')
                wait_for(lambda: '1 record changes removed' in screen.line(24), 'explicit partial-cycle removal')
                send(b'APYJRNCHG JRN(QGPL/GRCYCLEJ) FILE(QGPL/GRCYCLE) RCVRNG(*CURRENT) FROMENT(4) TOENT(4) CMTBDY(*NO)\r')
                wait_for(lambda: '1 record changes applied' in screen.line(24), 'explicit partial-cycle apply')
                send(b'DSPPFM FILE(QGPL/GRCYCLE)\r')
                wait_for(lambda: 'AFTER' in '\n'.join(screen.line(n) for n in range(1, 25)), 'verify committed cycle data after replay')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'return from applied cycle data')
                send(b'CRTCLPGM PGM(QGPL/CLPREP) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLPREP created' in screen.line(24), 'CL include and conditional compilation')
                send(b'CALL PGM(QGPL/CLPREP)\r')
                wait_for(lambda: 'PREPROCESS ACCEPTED' in screen.line(24), 'compiled CL include execution')
                send(b'CRTBNDRPG PGM(QGPL/RPGPREP) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGPREP created' in screen.line(24), 'RPG nested include and conditional compilation')
                send(b'CALL PGM(QGPL/RPGPREP)\r')
                wait_for(lambda: 'RPG PREPROCESS ACCEPTED' in screen.line(24), 'compiled RPG source snapshot execution')
                send(b'CRTBNDRPG PGM(QGPL/RPGPARSE) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGPARSE created' in screen.line(24), 'RPG free text and fixed declaration classification')
                send(b'CALL PGM(QGPL/RPGPARSE)\r')
                wait_for(lambda: 'RPG PARSED' in screen.line(24), 'RPG CALLP PRIVATE and DSPLY DATA execution')
                send(b'CRTBNDRPG PGM(QGPL/RPGKEYS) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGKEYS created' in screen.line(24), 'RPG compound file key compilation')
                send(b'CALL PGM(QGPL/RPGKEYS)\r')
                wait_for(lambda: 'RPG COMPOUND KEYS ACCEPTED' in screen.line(24), 'native KLIST, KDS, expression keys and one duplicate DELETE')
                send(b'CRTBNDRPG PGM(QGPL/RPGRRN) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGRRN created' in screen.line(24), 'RPG relative record input compilation')
                send(b'CALL PGM(QGPL/RPGRRN)\r')
                wait_for(lambda: 'RPG RRN ACCESS ACCEPTED' in screen.line(24), 'RRN CHAIN, deleted gaps, limit, update and one-record DELETE')
                send(b'CRTBNDRPG PGM(QGPL/RPGDISK) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGDISK created' in screen.line(24), 'native RPG DISK input compilation')
                send(b'CALL PGM(QGPL/RPGDISK)\r')
                wait_for(lambda: 'RPG DISK INPUT ACCEPTED' in screen.line(24), 'DISK external fields, arrival/keyed modes, USROPN and close status')
                send(b'CRTBNDRPG PGM(QGPL/RPGUPD) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGUPD created' in screen.line(24), 'native RPG update/delete compilation')
                send(b'OVRDBF FILE(GDISK) WAITRCD(*IMMED) OVRSCOPE(*JOB)\r')
                wait_for(lambda: 'Job file overrides updated' in screen.line(24), 'RPG record-wait override')
                send(b'CALL PGM(QGPL/RPGUPD)\r')
                wait_for(lambda: 'RPG UPDATE LOCKS ACCEPTED' in screen.line(24), 'declared update input, N, UNLOCK, prefixed UPDATE and keyed DELETE')
                send(b'DLTOVR FILE(GDISK) LVL(*JOB)\r')
                wait_for(lambda: 'Job file overrides updated' in screen.line(24), 'record-wait override cleanup')
                send(b'CRTCLPGM PGM(QGPL/CLWAIT) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLWAIT created' in screen.line(24), 'CL record-wait compilation')
                send(b'CALL PGM(QGPL/CLWAIT)\r')
                wait_for(lambda: 'RECORD WAIT POLICY ACCEPTED' in screen.line(24), 'compiled CHGPF, OVRDBF NOMAX and DLTOVR')
                send(b'CRTBNDRPG PGM(QGPL/RPGSTATUS) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGSTATUS created' in screen.line(24), 'RPG E, numeric file status and MONITOR compilation')
                send(b'CALL PGM(QGPL/RPGSTATUS)\r')
                wait_for(lambda: 'RPG FILE STATUS ACCEPTED' in screen.line(24), 'closed-file E reset, N inquiry and MONITOR update status')
                send(b'CRTBNDRPG PGM(QGPL/RPGFDBK) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGFDBK created' in screen.line(24), 'INFDS and PSDS compilation')
                send(b'CALL PGM(QGPL/RPGFDBK)\r')
                wait_for(lambda: 'RPG FEEDBACK ACCEPTED' in screen.line(24), 'native feedback status, opcode, open flag and last-file snapshot')
                send(b'CRTBNDRPG PGM(QGPL/RPGERRSR) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGERRSR created' in screen.line(24), 'INFSR and PSSR compilation')
                send(b'CALL PGM(QGPL/RPGERRSR)\r')
                wait_for(lambda: 'RPG ERROR ROUTINES ACCEPTED' in screen.line(24), 'INFSR GOTO recovery and PSSR feedback/RETURN')
                send(b'CRTBNDRPG PGM(QGPL/RPGNATDS) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGNATDS created' in screen.line(24), 'native free DS layout compilation')
                send(b'CALL PGM(QGPL/RPGNATDS)\r')
                wait_for(lambda: 'NATIVE DS STORAGE ACCEPTED' in screen.line(24), 'typed native API header and error data structures')
                send(b'CRTBNDRPG PGM(QGPL/RPGOUTPUT) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/RPGOUTPUT created' in screen.line(24), 'native RPG output compilation')
                send(b'CALL PGM(QGPL/RPGOUTPUT)\r')
                wait_for(lambda: 'RPG OUTPUT ACCEPTED' in screen.line(24), 'declared output addition, renamed/prefixed WRITE, input/output and close')
                send(b'CRTBNDRPG PGM(QGPL/RPGBAD) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'RPGBAD' in screen.line(24) and 'Unsupported free RPG' in screen.line(24), 'unsupported RPG source diagnostic')
                send(b'CALL PGM(QGPL/RPGBAD)\r')
                wait_for(lambda: 'not found' in screen.line(24), 'invalid RPG compilation creates no program')
                send(b'CRTCLPGM PGM(QGPL/CLFLOW) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLFLOW created' in screen.line(24), 'CL structured control compilation')
                send(b'CALL PGM(QGPL/CLFLOW)\r')
                wait_for(lambda: 'CONTROL FLOW ACCEPTED' in screen.line(24), 'CL typed arithmetic, DOFOR, ITERATE and SELECT execution')
                send(b'CRTCLPGM PGM(QGPL/CLMON) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLMON created' in screen.line(24), 'CL exception monitor compilation')
                send(b'CALL PGM(QGPL/CLMON)\r')
                wait_for(lambda: 'MONMSG ACCEPTED' in screen.line(24), 'CL nested escape, generic ID and comparison data recovery')
                send(b'CRTCLPGM PGM(QGPL/CLREAD) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLREAD created' in screen.line(24), 'CL database file binding compilation')
                send(b'CALL PGM(QGPL/CLREAD)\r')
                wait_for(lambda: 'FILE IO ACCEPTED' in screen.line(24), 'CL file read, monitored EOF and CLOSE/reopen')
                send(b'CRTCLPGM PGM(QGPL/CLRPG) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLRPG created' in screen.line(24), 'CL-to-RPG caller compilation')
                send(b'CALL PGM(QGPL/CLRPG)\r')
                wait_for(lambda: 'RPG WRITEBACK ACCEPTED' in screen.line(24), 'RPG parameter update returned to CL')
                send(b'CRTCLPGM PGM(QGPL/CLENV) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLENV created' in screen.line(24), 'CL environment caller compilation')
                send(b'CALL PGM(QGPL/CLENV)\r')
                wait_for(lambda: 'CL ENVIRONMENT ACCEPTED' in screen.line(24), 'CL typed job attributes and variable data-area byte range')
                send(b'CRTDTAARA DTAARA(QGPL/PRICE) TYPE(*DEC) LEN(5 2) VALUE(12.39)\r')
                wait_for(lambda: 'Data area QGPL/PRICE created' in screen.line(24), 'named decimal data-area creation')
                send(b'CRTCLPGM PGM(QGPL/CLNAMED) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLNAMED created' in screen.line(24), 'named data-area caller compilation')
                send(b'CALL PGM(QGPL/CLNAMED)\r')
                wait_for(lambda: 'NAMED AREA ACCEPTED' in screen.line(24), 'named data-area retrieval, change and decimal alignment')
                send(b'DSPDTAARA DTAARA(QGPL/PRICE)\r')
                wait_for(lambda: '12.49' in '\n'.join(screen.line(n) for n in range(1, 25)), 'named data-area attributes and stored value')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'named data-area display return')
                send(b'CRTCLPGM PGM(QGPL/CLDAAPI) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLDAAPI created' in screen.line(24), 'native data-area API caller compilation')
                send(b'CALL QGPL/CLDAAPI\r')
                wait_for(lambda: 'DATA AREA API ACCEPTED' in screen.line(24), 'native data-area packed/header/LDA layouts and direct borrowed RPG call')
                send(b'DLTDTAARA DTAARA(QGPL/PRICE)\r')
                wait_for(lambda: 'Data area QGPL/PRICE deleted' in screen.line(24), 'named data-area deletion')
                send(b'CRTMSGQ QTEMP/PRIVATE AUT(*EXCLUDE)\r')
                wait_for(lambda: 'Message queue QTEMP/PRIVATE created' in screen.line(24), 'job-private message queue creation')
                send(b'CRTMSGF QTEMP/TEXTS\r')
                wait_for(lambda: 'Message file QTEMP/TEXTS created' in screen.line(24), 'job-private message file creation')
                send(b"ADDMSGD USR0001 QTEMP/TEXTS 'Private &1' FMT((*CHAR 3))\r")
                wait_for(lambda: 'Message description USR0001 updated' in screen.line(24), 'job-private message description mutation')
                send(b'CRTCLPGM PGM(QTEMP/PRIVMSG) SRCFILE(QGPL/SOURCE) SRCMBR(CLTMPMSG)\r')
                wait_for(lambda: 'Program QTEMP/PRIVMSG created' in screen.line(24), 'private message caller compilation')
                send(b'CALL QTEMP/PRIVMSG\r')
                wait_for(lambda: 'PRIVATE MSGQ ACCEPTED' in screen.line(24), 'private predefined send/receive with logical source-library fields')
                send(b'DSPMSG QTEMP/PRIVATE\r')
                wait_for(lambda: 'QTEMP/PRIVATE' in screen.line(1) and 'Private ABC' in screen.line(5), 'private queue display and saved message text')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'private message display return')
                send(b'RMVMSG MSGQ(QTEMP/PRIVATE) CLEAR(*ALL)\r')
                wait_for(lambda: '1 messages removed' in screen.line(24), 'private message removal')
                send(b'DLTMSGQ QTEMP/PRIVATE\r')
                wait_for(lambda: 'Message queue QTEMP/PRIVATE deleted' in screen.line(24), 'private queue deletion')
                send(b'DLTMSGF QTEMP/TEXTS\r')
                wait_for(lambda: 'Message file deleted' in screen.line(24), 'private message-file deletion')
                send(b'CRTMSGQ QGPL/INBOX\r')
                wait_for(lambda: 'Message queue QGPL/INBOX created' in screen.line(24), 'message queue creation')
                send(b'CRTMSGQ QGPL/REPLIES\r')
                wait_for(lambda: 'Message queue QGPL/REPLIES created' in screen.line(24), 'reply queue creation')
                send(b'CRTCLPGM PGM(QGPL/CLRESP) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLRESP created' in screen.line(24), 'queue responder compilation')
                send(b'CRTCLPGM PGM(QGPL/CLQUEUE) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLQUEUE created' in screen.line(24), 'queue caller compilation')
                send(b'CALL PGM(QGPL/CLQUEUE)\r')
                wait_for(lambda: 'QUEUE ACCEPTED' in screen.line(24), 'CL named and program queue inquiry, sender-copy key and reply')
                send(b'CRTCLPGM PGM(QGPL/CLABICHILD) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLABICHILD created' in screen.line(24), 'CALL constant receiver compilation')
                send(b'CRTCLPGM PGM(QGPL/CLCONST) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLCONST created' in screen.line(24), 'CALL expression temporary compilation')
                send(b'CALL QGPL/CLCONST\r')
                wait_for(lambda: 'CALL LAYOUTS ACCEPTED' in screen.line(24), 'default packed constant, raw hex and private typed expression temporary')
                send(b'CRTMSGF QGPL/CLMSGS\r')
                wait_for(lambda: 'Message file QGPL/CLMSGS created' in screen.line(24), 'message description file creation')
                send(b"ADDMSGD USR0001 QGPL/CLMSGS 'Amount &1' SECLVL('Balance &1') SEV(40) FMT((*DEC 5 2))\r")
                wait_for(lambda: 'Message description USR0001 updated' in screen.line(24), 'message description packed substitution format')
                send(b'CRTCLPGM PGM(QGPL/CLMSGTXT) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLMSGTXT created' in screen.line(24), 'predefined message retrieval compilation')
                send(b'CALL QGPL/CLMSGTXT\r')
                wait_for(lambda: 'PREDEFINED TEXT ACCEPTED' in screen.line(24), 'RTVMSG first and second level text and severity')
                send(b'CRTCLPGM PGM(QGPL/CLMSGFAIL) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLMSGFAIL created' in screen.line(24), 'predefined escape sender compilation')
                send(b'CRTCLPGM PGM(QGPL/CLMSGRCV) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLMSGRCV created' in screen.line(24), 'predefined escape receiver compilation')
                send(b'CALL QGPL/CLMSGRCV\r')
                wait_for(lambda: 'PREDEFINED QUEUE ACCEPTED' in screen.line(24), 'raw MONMSG comparison and RCVMSG first/second-level text, replacement bytes and severity')
                send(b"ADDMSGD USR0002 QGPL/CLMSGS 'VALIDATED OPERATOR INQUIRY' TYPE(*ALPHA) LEN(1) VALUES(Y N) SPCVAL((yes Y)(no N)) DFT(N)\r")
                wait_for(lambda: 'Message description USR0002 updated' in screen.line(24), 'inquiry reply rules creation')
                send(b'CRTCLPGM PGM(QGPL/CLINQOP) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLINQOP created' in screen.line(24), 'validated operator inquiry compilation')
                send(b'CRTCLPGM PGM(QGPL/CLCHKRPY) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLCHKRPY created' in screen.line(24), 'checked reply receiver compilation')
                send(b'CALL QGPL/CLINQOP\r')
                wait_for(lambda: 'VALIDATED OPERATOR INQUIRY' in screen.line(24), 'validated inquiry send')
                send(b'DSPMSG QGPL/INBOX\r')
                wait_for(lambda: 'VALIDATED OPERATOR INQUIRY' in screen.line(5), 'validated inquiry work screen')
                send(b'2\r')
                wait_for(lambda: 'Prompt command - SNDRPY' in screen.line(1), 'validated reply F4 prompt')
                send(b'\t\tbad\r')
                wait_for(lambda: 'CPF2422' in '\n'.join(screen.line(n) for n in range(1, 25)), 'invalid reply diagnostic and preserved inquiry')
                send(b'\x1bOR')
                wait_for(lambda: 'VALIDATED OPERATOR INQUIRY' in screen.line(5), 'cancel invalid reply without consuming inquiry')
                send(b'\r')
                wait_for(lambda: 'Prompt command - SNDRPY' in screen.line(1), 'retry reply F4 prompt')
                send(b'\t\tyes\r')
                wait_for(lambda: '0 row(s)' in screen.line(2), 'mapped valid operator reply and refresh')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'validated inquiry return')
                send(b'CALL QGPL/CLCHKRPY\r')
                wait_for(lambda: 'OPERATOR VALIDATION ACCEPTED' in screen.line(24), 'mapped reply Y and checked return type 22')
                send(b'DLTMSGF QGPL/CLMSGS\r')
                wait_for(lambda: 'Message file deleted' in screen.line(24), 'message description file cleanup')
                send(b'CRTCLPGM PGM(QGPL/CLAPI) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLAPI created' in screen.line(24), 'QCMDEXC caller compilation')
                send(b'CALL QGPL/CLAPI\r')
                wait_for(lambda: 'QCMDEXC ACCEPTED' in screen.line(24), 'QCMDEXC packed length and shared job library state')
                send(b'CRTCLPGM PGM(QGPL/CLARGS) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLARGS created' in screen.line(24), 'CL command argument compilation')
                send(b'CALL QGPL/CLARGS\r')
                wait_for(lambda: 'COMMAND ARGUMENTS ACCEPTED' in screen.line(24), 'qualified name variables and data-area dimension variable')
                send(b'CRTCLPGM PGM(QGPL/CLBYTES) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLBYTES created' in screen.line(24), 'CL byte-function compilation')
                send(b'CALL QGPL/CLBYTES\r')
                wait_for(lambda: 'BYTE FUNCTIONS ACCEPTED' in screen.line(24), 'hex constants, local-area substring and signed binary read/write')
                send(b'CRTCLPGM PGM(QGPL/CLERRMSG) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLERRMSG created' in screen.line(24), 'runtime error receiver compilation')
                send(b'CALL QGPL/CLERRMSG\r')
                wait_for(lambda: 'RUNTIME ERROR RECEIVED' in screen.line(24), 'MONMSG receives queued arithmetic error')
                send(b"SNDMSG MSG('VISIBLE QUEUE MESSAGE') TOMSGQ(QGPL/INBOX)\r")
                wait_for(lambda: 'Message sent' in screen.line(24), 'interactive message send')
                send(b'DSPMSG QGPL/INBOX\r')
                wait_for(lambda: 'VISIBLE QUEUE MESSAGE' in '\n'.join(screen.line(n) for n in range(1, 25)), 'message queue contents')
                send(b'5\r')
                wait_for(lambda: 'Message key:' in '\n'.join(screen.line(n) for n in range(1, 25)), 'message detail display')
                send(b'\x1bOR')
                wait_for(lambda: 'Messages for QGPL/INBOX' in screen.line(1), 'message detail return')
                send(b'4\r')
                wait_for(lambda: 'Confirm Remove' in screen.line(2), 'message removal confirmation')
                send(b'\x1b[17~')
                wait_for(lambda: '0 row(s)' in screen.line(2), 'message removal and refresh')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'message display return')
                send(b"SNDMSG MSG('OPERATOR REPLY TEST') TOMSGQ(QGPL/INBOX) MSGTYPE(*INQ) RPYMSGQ(QGPL/REPLIES)\r")
                wait_for(lambda: 'Message sent' in screen.line(24), 'operator inquiry send')
                send(b'DSPMSG QGPL/INBOX\r')
                wait_for(lambda: 'OPERATOR REPLY TEST' in screen.line(5), 'operator inquiry display')
                send(b'2\r')
                wait_for(lambda: 'Prompt command - SNDRPY' in screen.line(1), 'operator reply prompt')
                send(b'\t\tOPERATOR YES\r')
                wait_for(lambda: '0 row(s)' in screen.line(2), 'operator reply and refresh')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'operator reply return')
                send(b'DSPMSG QGPL/REPLIES\r')
                wait_for(lambda: 'OPERATOR YES' in screen.line(5), 'operator reply delivered')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'reply queue display return')
                send(b'DLTMSGQ QGPL/INBOX\r')
                wait_for(lambda: 'Message queue QGPL/INBOX deleted' in screen.line(24), 'message queue deletion')
                send(b'CRTDTAQ QGPL/WORKDQ MAXLEN(8) SEQ(*KEYED) KEYLEN(2) SENDERID(*YES)\r')
                wait_for(lambda: 'Data queue QGPL/WORKDQ created' in screen.line(24), 'keyed data queue creation')
                send(b'CRTCLPGM PGM(QGPL/CLDQWORK) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLDQWORK created' in screen.line(24), 'CL data queue program compilation')
                send(b'CALL QGPL/CLDQWORK\r')
                wait_for(lambda: 'DATA QUEUE ACCEPTED' in screen.line(24), 'CL raw keyed send/peek/receive/clear and empty length')
                send(b'CRTCLPGM PGM(QGPL/CLDQAPI) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLDQAPI created' in screen.line(24), 'native data queue API caller compilation')
                send(b'CALL QGPL/CLDQAPI\r')
                wait_for(lambda: 'NATIVE QUEUE API ACCEPTED' in screen.line(24), 'native queue APIs with packed lengths, sender, truncation, peek and error receivers')
                send(b'CALL QGPL/RPGDQAPI\r')
                wait_for(lambda: 'DIRECT RPG API ACCEPTED' in screen.line(24), 'direct RPG typed native queue send/receive/clear without a CL wrapper')
                send(b'CALL QGPL/RPGPRAPI\r')
                wait_for(lambda: 'PROTOTYPE API ACCEPTED' in screen.line(24), 'RPG prototypes call native keyed send/receive/clear with actual receiver fields')
                send(b"SNDDTAQ QGPL/WORKDQ X'FF00' KEY(X'0011')\r")
                wait_for(lambda: 'Data-queue entry sent' in screen.line(24), 'interactive binary data-queue send')
                send(b"RCVDTAQ QGPL/WORKDQ KEY(X'0011')\r")
                wait_for(lambda: "Data: X'FF00'" in '\n'.join(screen.line(n) for n in range(1, 25)), 'bounded raw data-queue output')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'data queue output return')
                send(b'DLTDTAQ QGPL/WORKDQ\r')
                wait_for(lambda: 'Data queue QGPL/WORKDQ deleted' in screen.line(24), 'data queue deletion')
                send(b"CRTUSRSPC QTEMP/WORKUS 8 VALUE(X'FE')\r")
                wait_for(lambda: 'User space QTEMP/WORKUS created' in screen.line(24), 'binary user space creation')
                send(b'ALCOBJ OBJ((QTEMP/WORKUS *USRSPC *EXCLRD)) WAIT(0)\r')
                wait_for(lambda: '1 object allocation(s) acquired' in screen.line(24), 'exclusive-read user-space allocation')
                send(b'DLCOBJ OBJ((QTEMP/WORKUS *USRSPC *EXCLRD))\r')
                wait_for(lambda: '1 object allocation(s) released' in screen.line(24), 'exclusive-read user-space release')
                send(b"CRTUSRSPC QTEMP/WORKUS2 4 VALUE(X'AA')\r")
                wait_for(lambda: 'User space QTEMP/WORKUS2 created' in screen.line(24), 'second generic user-space match')
                send(b'DLTUSRSPC QTEMP/WORKUS*\r')
                wait_for(lambda: '2 user space(s) deleted' in screen.line(24), 'generic binary user-space deletion')
                send(b'CRTCLPGM PGM(QGPL/CLUSAPI) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLUSAPI created' in screen.line(24), 'user space native API caller compilation')
                send(b'CALL QGPL/CLUSAPI\r')
                wait_for(lambda: 'USER SPACE API ACCEPTED' in screen.line(24), 'native user space creation, binary write/read, SPCA0100 and deletion')
                send(b'CRTSRCPF FILE(QTEMP/PRIVSRC) SRCDTALEN(240)\r')
                wait_for(lambda: 'Source physical file QTEMP/PRIVSRC created' in screen.line(24), 'private source file creation')
                send(b'ADDPFM FILE(QTEMP/PRIVSRC) MBR(NATIVEPF)\r')
                wait_for(lambda: 'Member NATIVEPF added' in screen.line(24), 'private DDS source member creation')
                send(b'CPYF FROMFILE(QGPL/SOURCE) TOFILE(QTEMP/PRIVSRC) FROMMBR(NATIVEPF) TOMBR(NATIVEPF) MBROPT(*REPLACE)\r')
                wait_for(lambda: '2 records copied' in screen.line(24), 'DDS source copied into QTEMP')
                send(b'ADDPFM FILE(QTEMP/PRIVSRC) MBR(PRIVREAD)\r')
                wait_for(lambda: 'Member PRIVREAD added' in screen.line(24), 'private CL source member creation')
                send(b'CPYF FROMFILE(QGPL/SOURCE) TOFILE(QTEMP/PRIVSRC) FROMMBR(CLTMPFILE) TOMBR(PRIVREAD) MBROPT(*REPLACE)\r')
                wait_for(lambda: '6 records copied' in screen.line(24), 'CL source copied into QTEMP')
                send(b'CRTPF FILE(QTEMP/PRIVATE) SRCFILE(QTEMP/PRIVSRC) SRCMBR(NATIVEPF)\r')
                wait_for(lambda: 'Physical file QTEMP/PRIVATE created' in screen.line(24), 'DDS compiled from private source')
                send(b'CPYF FROMFILE(QGPL/LFBASE) TOFILE(QTEMP/PRIVATE) MBROPT(*ADD)\r')
                wait_for(lambda: '1 records copied' in screen.line(24), 'private data population')
                send(b'CRTCLPGM PGM(QTEMP/PRIVREAD) SRCFILE(QTEMP/PRIVSRC)\r')
                wait_for(lambda: 'Program QTEMP/PRIVREAD created' in screen.line(24), 'CL compiled from private source')
                send(b'CALL QTEMP/PRIVREAD\r')
                wait_for(lambda: 'QTEMP FILES ACCEPTED' in screen.line(24), 'CL reads the private physical file')
                send(b'DSPPFM FILE(QTEMP/PRIVATE)\r')
                wait_for(lambda: 'LF ACCEPTED' in '\n'.join(screen.line(n) for n in range(1, 25)), 'private physical file display')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'private physical display return')
                send(b'DLTF FILE(QTEMP/PRIVATE)\r')
                wait_for(lambda: 'QTEMP/PRIVATE deleted' in screen.line(24), 'private physical file deletion')
                send(b'DLTF FILE(QTEMP/PRIVSRC)\r')
                wait_for(lambda: 'QTEMP/PRIVSRC deleted' in screen.line(24), 'private source file deletion')
                send(b'CRTPF FILE(QGPL/NATIVEPF) SRCFILE(QGPL/SOURCE) MBR(*NONE) WAITRCD(2)\r')
                wait_for(lambda: 'Physical file QGPL/NATIVEPF created' in screen.line(24), 'native physical DDS compilation')
                send(b'CHGPF FILE(QGPL/NATIVEPF) MAXMBRS(2) WAITRCD(*IMMED)\r')
                wait_for(lambda: 'maximum members: 2' in screen.line(24), 'physical member limit change')
                send(b'ADDPFM FILE(QGPL/NATIVEPF) MBR(FIRST)\r')
                wait_for(lambda: 'Member FIRST added' in screen.line(24), 'first physical member creation')
                send(b'CPYF FROMFILE(QGPL/LFBASE) TOFILE(QGPL/NATIVEPF) MBROPT(*ADD)\r')
                wait_for(lambda: '1 records copied' in screen.line(24), 'native physical format copy')
                send(b'DSPPFM FILE(QGPL/NATIVEPF)\r')
                wait_for(lambda: 'LF ACCEPTED' in '\n'.join(screen.line(n) for n in range(1, 25)), 'native physical format data')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'native physical display return')
                send(b'CRTLF FILE(QGPL/LFVIEW) SRCFILE(QGPL/SOURCE) MAXMBRS(2) WAITRCD(3)\r')
                wait_for(lambda: 'Logical file QGPL/LFVIEW created' in screen.line(24), 'logical file compilation')
                send(b'CHGLF FILE(QGPL/LFVIEW) WAITRCD(*NOMAX)\r')
                wait_for(lambda: 'Logical file QGPL/LFVIEW record wait updated' in screen.line(24), 'logical record-wait change')
                send(b'DSPPFM FILE(QGPL/LFVIEW)\r')
                wait_for(lambda: 'LF ACCEPTED' in '\n'.join(screen.line(n) for n in range(1, 25)), 'live logical file data')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'logical display return')
                send(b'ADDPFM FILE(QGPL/LFBASE) MBR(COPY)\r')
                wait_for(lambda: 'Member COPY added' in screen.line(24), 'copy target member')
                send(b'CPYF FROMFILE(QGPL/LFVIEW) TOFILE(QGPL/LFBASE) TOMBR(COPY) MBROPT(*REPLACE)\r')
                wait_for(lambda: '1 records copied' in screen.line(24), 'transactional logical to physical copy')
                send(b'DSPPFM FILE(QGPL/LFBASE) MBR(COPY)\r')
                wait_for(lambda: 'LF ACCEPTED' in '\n'.join(screen.line(n) for n in range(1, 25)), 'copied physical records')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'copied display return')
                send(b'ADDLFM FILE(QGPL/LFVIEW) MBR(SECOND)\r')
                wait_for(lambda: 'Member SECOND added' in screen.line(24), 'logical member addition')
                send(b'RMVM FILE(QGPL/LFVIEW) MBR(SECOND)\r')
                wait_for(lambda: 'Member SECOND removed' in screen.line(24), 'logical member removal')
                send(b'DLTF FILE(QGPL/LFVIEW)\r')
                wait_for(lambda: 'File QGPL/LFVIEW deleted' in screen.line(24), 'logical file deletion')
                menu_file = Path(directory) / 'menu.json'
                definition = {'Name': 'PTYMENU', 'Library': 'QGPL', 'Title': 'PTY custom tasks', 'Options': [
                    {'Number': '1', 'Text': 'Job details', 'Target': 'DSPJOB', 'Kind': 'Command', 'RequiredAuthority': '*JOBCTL'}]}
                menu_file.write_text(json.dumps(definition))
                create = f"CRTMNU MENU(QGPL/PTYMENU) JSONFILE('{menu_file}')"
                send(create.encode() + b'\r')
                wait_for(lambda: 'Menu imported from JSON' in screen.line(24), 'JSON menu import')
                send(b'GO QGPL/PTYMENU\r')
                wait_for(lambda: 'PTY custom tasks' in screen.line(1), 'custom menu navigation')
                send(b'1\r')
                wait_for(lambda: 'is active' in screen.line(24), 'restricted custom option')
                definition['Title'] = 'Revised custom tasks'
                definition['Options'][0]['Target'] = 'CHGCURLIB CURLIB(QUSRSYS)'
                menu_file.write_text(json.dumps(definition))
                send(create.encode() + b' REPLACE(*YES)\r')
                wait_for(lambda: 'Menu imported from JSON' in screen.line(24), 'menu replacement')
                send(b'\x1b[15~')
                wait_for(lambda: 'Revised custom tasks' in screen.line(1), 'live menu reload')
                send(b'GO MAIN\r')
                wait_for(lambda: 'AS/400 Main Menu' in screen.line(1), 'return MAIN')
                send(b'DLTOBJ OBJ(QGPL/PTYMENU) OBJTYPE(*MENU)\r')
                wait_for(lambda: 'DLTOBJ completed' in screen.line(24), 'custom menu delete')
                send(b'SIGNOFF\r')
                deadline = time.monotonic() + 15
                while process.poll() is None and time.monotonic() < deadline:
                    if select.select([master], [], [], .05)[0]: os.read(master, 65536)
                assert process.wait(timeout=1) == 0
                assert termios.tcgetattr(slave) == original
                assert all(j['Completion'] == 'Normal' for j in json.loads(report.read_text()))
            finally:
                if process.poll() is None: process.kill(); process.wait()
    finally:
        os.close(master); os.close(slave)


def run_messages(dotnet, assembly):
    master, slave = pty.openpty()
    original = termios.tcgetattr(slave)
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 80, 0, 0))
    screen = Screen(24, 80)

    def attach():
        os.setsid()
        fcntl.ioctl(0, termios.TIOCSCTTY, 0)

    with tempfile.TemporaryDirectory(prefix='ipc-messages-pty-') as directory:
        report = Path(directory) / 'result.json'
        process = subprocess.Popen([dotnet, assembly, str(report), 'messages'], stdin=slave, stdout=slave, stderr=slave, preexec_fn=attach)

        def wait_for(predicate, description):
            deadline = time.monotonic() + 10
            while not predicate():
                if process.poll() is not None or time.monotonic() > deadline:
                    raise AssertionError(f'{description}: code={process.poll()}, lines={[screen.line(n).strip() for n in range(1, 8)]}')
                if select.select([master], [], [], .05)[0]:
                    screen.feed(os.read(master, 65536))

        try:
            wait_for(lambda: 'Live message subfile' in screen.line(1) and 'Message 1 ABC' in screen.line(4), 'live message subfile output')
            assert 'Message 2' in screen.line(5)
            os.write(master, b'\x1bOP')
            wait_for(lambda: 'ERR0001' in screen.line(1) and any('Correct ABC and retry' in screen.line(n) for n in range(2, 12)), 'message second-level help')
            os.write(master, b'\x1bOR')
            wait_for(lambda: 'Message 1 ABC' in screen.line(4) and (screen.row, screen.column) == (3, 1), 'message help restores cursor and page')
            os.write(master, b'\x1b[6~')
            wait_for(lambda: 'Message 3' in screen.line(4), 'message subfile page down after queue removal')
            os.write(master, b'\x1b[6~')
            wait_for(lambda: 'Message 5' in screen.line(4), 'message subfile extended size')
            os.write(master, b'\x1b[5~')
            wait_for(lambda: 'Message 3' in screen.line(4), 'message subfile page up')
            os.write(master, b'\x1bOR')
            deadline = time.monotonic() + 10
            while process.poll() is None and time.monotonic() < deadline:
                if select.select([master], [], [], .05)[0]:
                    screen.feed(os.read(master, 65536))
            assert process.wait(timeout=1) == 0
            assert json.loads(report.read_text()) == {'Rows': 5, 'HelpOpened': 1, 'Remaining': 0}
            assert termios.tcgetattr(slave) == original
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
            os.close(master)
            os.close(slave)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--assembly', default='tests/Ipc.TerminalFixture/bin/Release/net8.0/Ipc.TerminalFixture.dll')
    args = parser.parse_args()
    for wide in (False, True):
        run(args.dotnet, str(Path(args.assembly).resolve()), wide)
    run_help(args.dotnet, str(Path(args.assembly).resolve()))
    run_designer(args.dotnet, str(Path(args.assembly).resolve()))
    run_groups(args.dotnet, str(Path(args.assembly).resolve()))
    run_work(args.dotnet, str(Path(args.assembly).resolve()))
    run_messages(args.dotnet, str(Path(args.assembly).resolve()))
    print('Display PTY passed: 24x80/27x132, cursor, Insert/Replace/Delete, paste, F1–F24, attention, resize, typed fields, hidden data, shared menu/command/DDS help, links/index/back, SDA save/compile/reopen/edit/run, SysReq/group transfer/GDA sharing/LDA isolation/secondary groups, CRTCMD/F4/defaults/CPP/DSPCMD, CRTCLPGM/CRTBNDRPG/include/conditional/typed-loop/select/parameter-writeback/MONMSG/DCLF/RCVF/CLOSE/RPG-writeback/RTVJOBA/RTVDTAARA/CRTDTAARA/DSPDTAARA/DLTDTAARA/CRTMSGQ/SNDMSG/RCVMSG/SNDRPY/DSPMSG/DLTMSGQ/CALL, CRTPF/CHGPF/ADDPFM/CPYF and CRTLF/CHGLF/DSPPFM/ADDLFM/RMVM/DLTF with WAITRCD attributes/overrides and compiled CL policy changes, work-list confirmation/refresh/paging, custom JSON menu import/replace/reload/delete, invalid inquiry reply/retry/special mapping/RTNTYPE 22, CRTDTAQ/SNDDTAQ/RCVDTAQ/CLRDTAQ/DLTDTAQ raw keyed lifecycle, native QSNDDTAQ/QRCVDTAQ/QCLRDTAQ packed/sender/error/truncation/peek contracts, QWCRDTAA typed packed/header/LDA layouts and direct RPG call, native QUSCRTUS/QUSCHGUS/QUSRTVUS/QUSRUSAT/QUSDLTUS and CRTUSRSPC/generic DLTUSRSPC byte lifecycle with EXCLRD allocation/release, QTEMP source/PF/member copy/compile/CL read/display/delete and private MSGQ/MSGF/predefined/CL/display/remove lifecycle, direct and prototyped typed RPG queue calls, RPG source rejection and free source parsing, compound file keys with KLIST/KDS/expression lists and one duplicate deletion, sparse RRN CHAIN/SETLL/UPDATE/DELETE, declared DISK input with imported fields/keyed-arrival access/USROPN/open-close status, declared update/delete modes with N input/UNLOCK/prefixed UPDATE, declared output with renamed/prefixed WRITE and input/output USROPN, numeric file status/E reset/N inquiry and MONITOR/ON-ERROR, native DCL-DS typed API/error receivers, live message-subfile SFLINZ/paging/second-level help after source removal, compiled CL SNDF/RCVF/SNDRCVF and RPG WORKSTN/EXFMT/WRITE/READ field/indicator/CA exchanges with suspended-job transfers, RPG SFILE/RRN/READC/CHAIN/UPDATE/EOF/FOUND and message-subfile help/paging, native RPG QMHSNDPM/QMHRCVPM/QMHRTVM binary storage and help, WRKAPILS contract discovery, compiled CL/direct RPG QWCRSVAL system-value offsets and binary/character data, native CL/direct RPG QUSLJOB/QUSRJOBI lists and opaque IDs, CL/direct RPG QWCCVTDT binary timestamps and microseconds, native CL/direct RPG hash/HMAC/AES/key contexts and QC3 registry details, journal receiver/capture/recovery, committed-cycle boundaries and explicit partial entries, and terminal cleanup.')


if __name__ == '__main__':
    main()
