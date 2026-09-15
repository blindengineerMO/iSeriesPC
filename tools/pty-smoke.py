#!/usr/bin/env python3
"""Exercise the actual controlling PTY and exact terminal restoration on Linux."""
import argparse
import fcntl
import os
from pathlib import Path
import pty
import select
import subprocess
import tempfile
import termios
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--assembly", default="src/Ipc.Console/bin/Release/net8.0/as400menu.dll")
    args = parser.parse_args()
    assembly = str(Path(args.assembly).resolve())
    master, slave = pty.openpty()
    original = termios.tcgetattr(slave)

    def attach():
        os.setsid()
        fcntl.ioctl(0, termios.TIOCSCTTY, 0)

    try:
        with tempfile.TemporaryDirectory(prefix="ipc-pty-") as directory:
            process = subprocess.Popen(
                [args.dotnet, assembly, "--standalone", "--data-dir", directory],
                stdin=slave, stdout=slave, stderr=slave, preexec_fn=attach,
            )
            try:
                output = bytearray()
                deadline = time.monotonic() + 15
                while b"Password" not in output:
                    if process.poll() is not None or time.monotonic() > deadline:
                        raise AssertionError(f"Sign-on did not render: {output.decode(errors='replace')}")
                    if select.select([master], [], [], 0.1)[0]:
                        output.extend(os.read(master, 65536))
                raw = termios.tcgetattr(slave)
                assert not raw[3] & termios.ICANON, "Terminal still uses canonical line input"
                assert not raw[3] & termios.ECHO, "Terminal still echoes password input"
                # F3 is sent without newline: this requires working raw-mode input.
                os.write(master, b"\x1bOR")
                deadline = time.monotonic() + 10
                while process.poll() is None and time.monotonic() < deadline:
                    if select.select([master], [], [], 0.1)[0]:
                        output.extend(os.read(master, 65536))
                assert process.wait(timeout=1) == 1, "F3 should cancel sign-on with status 1"
                assert termios.tcgetattr(slave) == original, "Original terminal settings were not restored"
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait()
        print("PTY smoke passed: sign-on rendering, raw input, F3 cancellation, exact terminal restoration.")
    finally:
        os.close(master)
        os.close(slave)


if __name__ == "__main__":
    main()
