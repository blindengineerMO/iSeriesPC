#!/usr/bin/env python3
"""Real loopback SSH -> terminal -> shared server -> Linux-PAM acceptance fixture.

Uses temporary keys, catalog and PAM configuration; does not edit system accounts,
sshd configuration, authorized_keys or /etc/pam.d. PAM uses an explicit test password
module; this is not qualification of a production pam_unix/SSSD installation.
"""
import argparse
import fcntl
import importlib.util
import pty
import struct
import termios
import json
import os
from pathlib import Path
import pwd
import re
import secrets
import select
import shlex
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import time


def wait_output(process, marker, redact, timeout=15):
    data = bytearray()
    deadline = time.monotonic() + timeout
    while marker not in re.sub(rb'\x1b\[[0-9;?]*[A-Za-z]', b'', data):
        if time.monotonic() > deadline or process.poll() is not None:
            raise AssertionError(f"SSH terminal did not reach {marker!r}; exit={process.poll()}: " + data[-1500:].decode(errors="replace").replace(redact, "[redacted]"))
        if select.select([process.stdout], [], [], 0.1)[0]:
            block = os.read(process.stdout.fileno(), 65536)
            if not block:
                raise AssertionError("SSH terminal closed before its expected screen: " + data.decode(errors="replace").replace(redact, "[redacted]"))
            data.extend(block)
    return data


def stop(process):
    if process and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


def terminal_acceptance(dotnet, menu, endpoint, port, directory, account, password, disconnect):
    spec = importlib.util.spec_from_file_location('display_pty', Path(__file__).with_name('pty-display-smoke.py'))
    display = importlib.util.module_from_spec(spec); spec.loader.exec_module(display)
    master, slave = pty.openpty()
    original = termios.tcgetattr(slave)
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 80, 0, 0))
    screen = display.Screen(24, 80)
    output = bytearray()
    def attach():
        os.setsid(); fcntl.ioctl(0, termios.TIOCSCTTY, 0)
    # The remote shell verifies its own PTY after the application exits.
    command = 'before=$(stty -g); ' + shlex.join([dotnet, str(menu), '--server', str(endpoint)]) + '; result=$?; after=$(stty -g); [ "$before" = "$after" ] || exit 97; echo IPC_TERMINAL_RESTORED; exit "$result"'
    client = subprocess.Popen(['ssh', '-tt', '-p', str(port), '-i', str(directory / 'client'),
        '-o', 'BatchMode=yes', '-o', 'IdentitiesOnly=yes', '-o', 'ConnectTimeout=5',
        '-o', 'StrictHostKeyChecking=no', '-o', f'UserKnownHostsFile={directory}/known_hosts',
        '-o', 'LogLevel=ERROR', f'{account}@127.0.0.1', command],
        stdin=slave, stdout=slave, stderr=slave, preexec_fn=attach)
    def wait_for(predicate, label):
        deadline = time.monotonic() + 15
        while not predicate():
            assert client.poll() is None and time.monotonic() < deadline, label
            if select.select([master], [], [], .05)[0]:
                data = os.read(master, 65536); output.extend(data); screen.feed(data)
    try:
        wait_for(lambda: any('Password' in screen.line(n) for n in range(1, 25)), 'SSH PTY sign-on')
        os.write(master, f'SSHTEST\t{password}\r'.encode())
        wait_for(lambda: 'Selection' in screen.line(23), 'SSH PTY signed in')
        if disconnect:
            client.kill(); client.wait(timeout=10)
            return
        os.write(master, b'DSPJOB\x1bOP')
        wait_for(lambda: 'DSPJOB' in screen.line(1), 'SSH F1 command help')
        os.write(master, b'\x1bOR')
        wait_for(lambda: 'Selection' in screen.line(23) and 'DSPJOB' in screen.line(23), 'SSH F3 input restoration')
        os.write(master, b'\r')
        wait_for(lambda: 'is active' in screen.line(24), 'SSH command execution')
        screen = display.Screen(12, 40)
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 12, 40, 0, 0))
        wait_for(lambda: 'Resize terminal' in screen.line(1), 'SSH resize guard')
        screen = display.Screen(24, 80)
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 80, 0, 0))
        wait_for(lambda: 'Selection' in screen.line(23), 'SSH resize recovery')
        os.write(master, b'SIGNOFF\r')
        deadline = time.monotonic() + 15
        while client.poll() is None and time.monotonic() < deadline:
            if select.select([master], [], [], .05)[0]: output.extend(os.read(master, 65536))
        assert client.wait(timeout=1) == 0
        # Drain final output if process exit and PTY readability arrived together.
        while select.select([master], [], [], .05)[0]: output.extend(os.read(master, 65536))
        assert b'IPC_TERMINAL_RESTORED' in output, 'Remote PTY was not restored'
        assert termios.tcgetattr(slave) == original, 'Local SSH PTY was not restored'
        assert password.encode() not in output, 'Password leaked to real SSH PTY'
    finally:
        stop(client); os.close(master); os.close(slave)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--sshd', default=shutil.which('sshd') or '/usr/sbin/sshd')
    parser.add_argument('--sshd-session')
    parser.add_argument('--sshd-auth')
    parser.add_argument('--configuration', default='Release')
    args = parser.parse_args()
    dotnet = str(Path(shutil.which(args.dotnet) or args.dotnet).resolve())
    root = Path(__file__).resolve().parent.parent
    menu = root / f'src/Ipc.Console/bin/{args.configuration}/net8.0/as400menu.dll'
    server = root / f'src/Ipc.Server/bin/{args.configuration}/net8.0/as400server.dll'
    account = pwd.getpwuid(os.geteuid()).pw_name
    password = secrets.token_hex(16)
    server_process = sshd_process = client = None
    with tempfile.TemporaryDirectory(prefix='.ipc-ssh-', dir=Path.home()) as temp:
        directory = Path(temp)
        catalog = directory / 'catalog'
        catalog.mkdir(mode=0o700)
        pam = directory / 'pam'
        pam.mkdir(mode=0o700)
        verify = pam / 'verify.py'
        verify.write_text('#!/usr/bin/python3\nimport os,sys\n' +
                          f"sys.exit(0 if os.environ.get('PAM_USER') == {account!r} and sys.stdin.buffer.read().rstrip(b'\\0') == {password.encode()!r} else 1)\n")
        verify.chmod(0o700)
        service = pam / 'iseriespc-test'
        service.write_text(f'auth [success=1 default=ignore] pam_exec.so quiet expose_authtok {verify}\n'
                           'auth requisite pam_deny.so\nauth required pam_permit.so\naccount required pam_permit.so\n')
        service.chmod(0o600)
        (catalog / 'system.json').write_text(json.dumps({'Authentication': {
            'PamService': 'iseriespc-test', 'PamConfigurationDirectory': str(pam),
            'PamAccounts': {'SSHTEST': account, 'WRONGPEER': 'ipc-unmapped-account'},
            'BindTerminalPamToUnixAccount': True, 'PamRequireProfilePassword': False}}))
        subprocess.run([dotnet, str(server), '--data-dir', str(catalog), '--reset-admin'],
                       check=True, stdout=subprocess.DEVNULL)
        database = catalog / 'system.db'
        # Fixtures are inserted while the server is stopped. Runtime execution uses
        # only authenticated terminal commands and the guarded server services.
        with sqlite3.connect(database) as connection:
            connection.create_function('ipc_actor', 0, lambda: 'TESTFIXTURE')
            connection.create_function('ipc_job', 0, lambda: None)
            connection.row_factory = sqlite3.Row
            template = dict(connection.execute("select * from sys_profiles where name='QUSER'").fetchone())
            for profile in ('SSHTEST', 'WRONGPEER'):
                row = {**template, 'name': profile}
                connection.execute('insert into sys_profiles (' + ','.join(row) + ') values (' +
                                   ','.join('?' for _ in row) + ')', tuple(row.values()))
        for key in ('host', 'client'):
            subprocess.run(['ssh-keygen', '-q', '-t', 'ed25519', '-N', '', '-f', str(directory / key)], check=True)
        authorized = directory / 'authorized_keys'
        authorized.write_bytes((directory / 'client.pub').read_bytes())
        authorized.chmod(0o600)
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        configuration = directory / 'sshd_config'
        configuration.write_text(f'''Port {port}
ListenAddress 127.0.0.1
HostKey {directory}/host
PidFile {directory}/sshd.pid
AuthorizedKeysFile {authorized}
StrictModes yes
UsePAM no
PasswordAuthentication no
KbdInteractiveAuthentication no
PubkeyAuthentication yes
AllowUsers {account}
AllowTcpForwarding no
X11Forwarding no
PermitTunnel no
LogLevel ERROR
''' + (f'SshdSessionPath {args.sshd_session}\n' if args.sshd_session else '') +
            (f'SshdAuthPath {args.sshd_auth}\n' if args.sshd_auth else ''))
        with open(directory / 'sshd.log', 'wb') as ssh_log, open(directory / 'server.log', 'wb') as server_log:
            try:
                subprocess.run([args.sshd, '-t', '-f', str(configuration)], check=True)
                sshd_process = subprocess.Popen([args.sshd, '-D', '-e', '-f', str(configuration)], stderr=ssh_log)
                server_process = subprocess.Popen([dotnet, str(server), '--data-dir', str(catalog)], stdout=server_log, stderr=server_log)
                endpoint = catalog / 'run/as400.sock'
                deadline = time.monotonic() + 15
                while not endpoint.exists():
                    if server_process.poll() is not None or time.monotonic() > deadline:
                        raise AssertionError('Shared server did not start')
                    time.sleep(0.05)
                remote_command = shlex.join([dotnet, str(menu), '--server', str(endpoint)])
                for profile, success in [('WRONGPEER', False), ('SSHTEST', True)]:
                    client = subprocess.Popen(['ssh', '-tt', '-p', str(port), '-i', str(directory / 'client'),
                        '-o', 'BatchMode=yes', '-o', 'IdentitiesOnly=yes', '-o', 'ConnectTimeout=5',
                        '-o', 'StrictHostKeyChecking=no', '-o', f'UserKnownHostsFile={directory}/known_hosts',
                        '-o', 'LogLevel=ERROR', f'{account}@127.0.0.1', remote_command],
                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
                    output = wait_output(client, b'Password', password)
                    client.stdin.write(f'{profile}\t{password}\r'.encode())
                    client.stdin.flush()
                    if success:
                        output.extend(wait_output(client, b'Selection:', password))
                        client.stdin.write(b'SIGNOFF\r')
                        client.stdin.flush()
                    remainder, _ = client.communicate(timeout=15)
                    output.extend(remainder)
                    assert password.encode() not in re.sub(rb'\x1b\[[0-9;?]*[A-Za-z]', b'', output), 'Password leaked to SSH terminal output'
                    assert (client.returncode == 0) == success, f'Unexpected SSH result for {profile}: {client.returncode}'
                    client = None
                terminal_acceptance(dotnet, menu, endpoint, port, directory, account, password, False)
                terminal_acceptance(dotnet, menu, endpoint, port, directory, account, password, True)
                deadline = time.monotonic() + 15
                while True:
                    with sqlite3.connect(database) as connection:
                        pending = connection.execute("select count(*) from sys_jobs where status != 'Completed'").fetchone()[0]
                    if pending == 0: break
                    assert time.monotonic() < deadline, 'Disconnected SSH job did not end'
                    time.sleep(.05)
                stop(server_process)
                with sqlite3.connect(database) as connection:
                    jobs = connection.execute('select user_profile,status from sys_jobs').fetchall()
                    assert len(jobs) == 3 and all(job == ('SSHTEST', 'Completed') for job in jobs), f'Unexpected session jobs: {jobs}'
                    events = connection.execute("select principal,payload from sys_events where kind='security.authentication'").fetchall()
                    assert any(principal == 'SSHTEST' and json.loads(payload)['Success'] for principal, payload in events)
                    assert any(not json.loads(payload)['Success'] for _, payload in events)
                    assert all(password not in payload for _, payload in events)
            except Exception:
                print('SSH fixture diagnostic:', (directory / 'sshd.log').read_text(errors='replace'))
                raise
            finally:
                stop(client)
                stop(server_process)
                stop(sshd_process)
    print('SSH smoke passed: real OpenSSH, Unix peer mapping, PAM password check, denied mismatched account, owned jobs, actual local/remote PTYs, F1/F3, resize, disconnect cleanup, restoration and secret-free audit.')


if __name__ == '__main__':
    main()
