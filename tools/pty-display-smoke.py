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
                command('RUNPNL FILE(QGPL/ENTRY)')
                wait_for(lambda: (screen.row, screen.column) == (3, 1) and 'Selection' not in screen.line(23), 'panel')
                os.write(master, b'UNSENT')
                wait_for(lambda: 'UNSENT' in screen.line(4) and (screen.row, screen.column) == (3, 7), 'panel edits')
                os.write(master, b'\x07')
                wait_for(lambda: 'System Request' in screen.line(1), 'SysReq over panel')
                command('1')
                wait_for(lambda: 'Selection' in screen.line(23) and 'System Request' not in screen.line(1), 'alternate job')
                command('CHGGRPA GRPJOB(ALTERNATE)')
                wait_for(lambda: 'Group attributes changed' in screen.line(24), 'alternate group')
                command('TFRGRPJOB GRPJOB(THIRD)')
                wait_for(lambda: 'Enter an option number' in screen.line(24), 'new group job')
                command('TFRGRPJOB GRPJOB(*SELECT)')
                wait_for(lambda: 'Transfer to Group Job' in screen.line(1), 'group selector')
                command('ALTERNATE')
                wait_for(lambda: 'Transfer to Group Job' not in screen.line(1), 'group return')
                os.write(master, b'\x07')
                wait_for(lambda: 'System Request' in screen.line(1), 'alternate SysReq')
                command('1')
                wait_for(lambda: 'UNSENT' in screen.line(4) and (screen.row, screen.column) == (3, 7), 'original panel and cursor restored')
                os.write(master, b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'panel closed')
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
                send(b'CRTCLPGM PGM(QGPL/CLPREP) SRCFILE(QGPL/SOURCE)\r')
                wait_for(lambda: 'Program QGPL/CLPREP created' in screen.line(24), 'CL include and conditional compilation')
                send(b'CALL PGM(QGPL/CLPREP)\r')
                wait_for(lambda: 'PREPROCESS ACCEPTED' in screen.line(24), 'compiled CL include execution')
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
                send(b'DLTDTAARA DTAARA(QGPL/PRICE)\r')
                wait_for(lambda: 'Data area QGPL/PRICE deleted' in screen.line(24), 'named data-area deletion')
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
                send(b'CRTPF FILE(QGPL/NATIVEPF) SRCFILE(QGPL/SOURCE) MBR(*NONE)\r')
                wait_for(lambda: 'Physical file QGPL/NATIVEPF created' in screen.line(24), 'native physical DDS compilation')
                send(b'CHGPF FILE(QGPL/NATIVEPF) MAXMBRS(2)\r')
                wait_for(lambda: 'maximum members: 2' in screen.line(24), 'physical member limit change')
                send(b'ADDPFM FILE(QGPL/NATIVEPF) MBR(FIRST)\r')
                wait_for(lambda: 'Member FIRST added' in screen.line(24), 'first physical member creation')
                send(b'CPYF FROMFILE(QGPL/LFBASE) TOFILE(QGPL/NATIVEPF) MBROPT(*ADD)\r')
                wait_for(lambda: '1 records copied' in screen.line(24), 'native physical format copy')
                send(b'DSPPFM FILE(QGPL/NATIVEPF)\r')
                wait_for(lambda: 'LF ACCEPTED' in '\n'.join(screen.line(n) for n in range(1, 25)), 'native physical format data')
                send(b'\x1bOR')
                wait_for(lambda: 'Selection' in screen.line(23), 'native physical display return')
                send(b'CRTLF FILE(QGPL/LFVIEW) SRCFILE(QGPL/SOURCE) MAXMBRS(2)\r')
                wait_for(lambda: 'Logical file QGPL/LFVIEW created' in screen.line(24), 'logical file compilation')
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
    print('Display PTY passed: 24x80/27x132, cursor, Insert/Replace/Delete, paste, F1–F24, attention, resize, typed fields, hidden data, shared menu/command/DDS help, links/index/back, SDA save/compile/reopen/edit/run, SysReq/group transfer, CRTCMD/F4/defaults/CPP/DSPCMD, CRTCLPGM/include/conditional/typed-loop/select/parameter-writeback/MONMSG/DCLF/RCVF/CLOSE/RPG-writeback/RTVJOBA/RTVDTAARA/CRTDTAARA/DSPDTAARA/DLTDTAARA/CRTMSGQ/SNDMSG/RCVMSG/SNDRPY/DSPMSG/DLTMSGQ/CALL, CRTPF/CHGPF/ADDPFM/CPYF and CRTLF/DSPPFM/ADDLFM/RMVM/DLTF, work-list confirmation/refresh/paging, custom JSON menu import/replace/reload/delete and terminal cleanup.')


if __name__ == '__main__':
    main()
