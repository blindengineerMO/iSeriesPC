#!/usr/bin/env python3
"""Real controlling-PTY signal, failure, EOF, and exact restoration acceptance."""
import argparse
import fcntl
import json
import os
from pathlib import Path
import pty
import select
import signal
import subprocess
import tempfile
import termios
import time


def run(dotnet, mode):
    master, slave = pty.openpty()
    original = termios.tcgetattr(slave)
    # Non-default flags prove restoration uses the captured state.
    original[3] |= termios.ECHONL
    termios.tcsetattr(slave, termios.TCSANOW, original)
    def attach():
        os.setsid()
        fcntl.ioctl(0, termios.TIOCSCTTY, 0)
    try:
        with tempfile.TemporaryDirectory(prefix='ipc-lifecycle-') as directory:
            report = Path(directory) / 'report.json'
            environment = dict(os.environ)
            if mode == 'setup-error':
                environment['PATH'] = directory
            assembly = str(Path('tests/Ipc.TerminalFixture/bin/Release/net8.0/Ipc.TerminalFixture.dll').resolve())
            process = subprocess.Popen([dotnet, assembly, str(report), mode if mode in ('input-error', 'output-error') else 'lifecycle'],
                stdin=slave, stdout=slave, stderr=slave, preexec_fn=attach, env=environment)
            output = bytearray()
            try:
                deadline = time.monotonic() + 15
                while mode not in ('setup-error', 'output-error', 'input-error') and b'Lifecycle ready' not in output:
                    assert process.poll() is None and time.monotonic() < deadline, output.decode(errors='replace')
                    if select.select([master], [], [], .05)[0]: output.extend(os.read(master, 65536))
                if isinstance(mode, signal.Signals):
                    os.kill(process.pid, mode)
                elif mode == 'ctrl-c': os.write(master, b'\x03')
                elif mode == 'ctrl-d': os.write(master, b'\x04')
                elif mode == 'failure': os.write(master, b'X')
                elif mode == 'unicode': os.write(master, 'é€\x1bOR'.encode())
                elif mode == 'hangup': os.close(master); master = None
                elif mode == 'partial-utf8': os.write(master, b'\xe2'); os.kill(process.pid, signal.SIGTERM)
                elif mode == 'paste':
                    os.write(master, b'\x1b[200~\x03\x04\x1b[201~\x1bOR')
                code = process.wait(timeout=10)
                expected = 128 + int(mode) if isinstance(mode, signal.Signals) else {
                    'ctrl-c': 130, 'ctrl-d': 1, 'failure': 3, 'setup-error': 3, 'input-error': 3, 'output-error': 3,
                    'partial-utf8': 143, 'paste': 0, 'unicode': 0}.get(mode)
                if expected is not None: assert code == expected, (mode, code, output.decode(errors='replace'))
                else: assert code != 0, (mode, code)
                state = json.loads(report.read_text())
                assert state['Disposed'] and state['Disconnected'] == (mode not in ('paste', 'unicode')), (mode, state)
                if mode == 'unicode': assert state['Keys'] == 3, state
                if master is not None:
                    assert termios.tcgetattr(slave) == original, (mode, 'Terminal settings changed')
            finally:
                if process.poll() is None: process.kill(); process.wait()
    finally:
        if master is not None: os.close(master)
        os.close(slave)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args()
    for mode in [signal.SIGHUP, signal.SIGINT, signal.SIGQUIT, signal.SIGTERM, signal.SIGTSTP,
                 'ctrl-c', 'ctrl-d', 'failure', 'setup-error', 'input-error', 'output-error', 'hangup', 'partial-utf8', 'paste', 'unicode']:
        run(args.dotnet, mode)
    # Pipe EOF exercises actual read(2)==0 independently of terminal hangup signals.
    with tempfile.TemporaryDirectory(prefix='ipc-eof-') as directory:
        report = Path(directory) / 'report.json'
        process = subprocess.run([args.dotnet, 'tests/Ipc.TerminalFixture/bin/Release/net8.0/Ipc.TerminalFixture.dll', str(report), 'lifecycle'],
            input=b'', stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, timeout=10)
        assert process.returncode == 1, process.stderr.decode(errors='replace')
        assert json.loads(report.read_text())['Disconnected']
    print('Lifecycle PTY passed: signals, Ctrl-C/D, paste isolation, EOF/hangup, setup/controller failure, partial UTF-8 cancellation, disposal and exact restoration.')


if __name__ == '__main__': main()
