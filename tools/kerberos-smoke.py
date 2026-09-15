#!/usr/bin/env python3
"""Run isolated LDAP/TLS, MIT Kerberos, SSSD/PAM and EIM acceptance in Docker."""
import argparse
from pathlib import Path
import shutil
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--configuration', default='Release')
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    runtime = Path(shutil.which(args.dotnet) or args.dotnet).resolve().parent
    application = root/f'src/Ipc.Server/bin/{args.configuration}/net8.0'
    if not (runtime/'dotnet').is_file() or not (application/'as400server.dll').is_file():
        parser.error('Build the server and supply the installed .NET SDK/runtime path first.')
    image = 'iseriespc-auth-test:local'
    subprocess.run(['docker', 'build', '--quiet', '-t', image, str(root/'tools/auth-integration')], check=True)
    subprocess.run(['docker', 'run', '--rm', '--network', 'none', '--hostname', 'sssd.ipc.test',
        '--memory', '1g', '--cpus', '2', '--pids-limit', '256',
        '--mount', f'type=bind,source={runtime},target=/dotnet,readonly',
        '--mount', f'type=bind,source={application},target=/app/Ipc.Server,readonly',
        '--tmpfs', '/fixture:rw,nosuid,nodev,noexec,size=128m,mode=0700', image], check=True)


if __name__ == '__main__':
    main()
