#!/usr/bin/env python3
"""Local dual CDC PTY transport. Standard library only; no firmware access."""
import argparse
import logging
import os
from pathlib import Path
import pty
import select
import signal
import socket
import time
import tty

BASE = Path(__file__).resolve().parent
LIMIT = 65536


class Channel:
    def __init__(self, name, port, directory):
        self.name, self.port = name, port
        self.master, self.slave = pty.openpty()
        tty.setraw(self.slave)
        os.set_blocking(self.master, False)
        self.target = os.ttyname(self.slave)
        self.path = directory / ('pwnsat-' + name)
        if self.path.exists() and not self.path.is_symlink():
            os.close(self.master)
            os.close(self.slave)
            raise RuntimeError(f'Refusing to replace regular path: {self.path}')
        self.path.symlink_to(self.target)  # stale links are handled by main's ownership lock
        self.sock = None
        self.retry = 0
        self.to_socket, self.to_pty = bytearray(), bytearray()
        logging.info('%s PTY %s -> %s', name, self.path, self.target)

    def disconnect(self):
        if self.sock is not None:
            self.sock.close()
            logging.info('%s disconnected', self.name)
        self.sock = None
        self.retry = time.monotonic() + 0.2
        self.to_socket.clear()
        self.to_pty.clear()

    def connect(self):
        if self.sock is not None or time.monotonic() < self.retry:
            return
        try:
            self.sock = socket.create_connection(('127.0.0.1', self.port), timeout=0.1)
            self.sock.setblocking(False)
            self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            logging.info('%s connected 127.0.0.1:%s', self.name, self.port)
        except OSError:
            self.disconnect()

    def close(self):
        self.disconnect()
        if self.path.is_symlink() and os.readlink(self.path) == self.target:
            self.path.unlink()
        os.close(self.master)
        os.close(self.slave)


def relay(channels, stopped):
    while not stopped():
        readers, writers = [], []
        for c in channels:
            c.connect()
            if c.sock is not None:
                if len(c.to_socket) < LIMIT:
                    readers.append(c.master)
                if len(c.to_pty) < LIMIT:
                    readers.append(c.sock)
                if c.to_socket:
                    writers.append(c.sock)
            if c.to_pty:
                writers.append(c.master)
        readable, writable, _ = select.select(readers, writers, [], 0.05)
        for c in channels:
            try:
                if c.master in readable:
                    c.to_socket.extend(os.read(c.master, min(4096, LIMIT - len(c.to_socket))))
                if c.sock is not None and c.sock in readable:
                    data = c.sock.recv(min(4096, LIMIT - len(c.to_pty)))
                    if not data:
                        c.disconnect()
                        continue
                    c.to_pty.extend(data)
                if c.sock is not None and c.sock in writable:
                    n = c.sock.send(c.to_socket)
                    del c.to_socket[:n]
                if c.master in writable:
                    n = os.write(c.master, c.to_pty)
                    del c.to_pty[:n]
            except BlockingIOError:
                pass
            except OSError:
                c.disconnect()


def main():
    import fcntl
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--console-port', type=int, default=19100)
    parser.add_argument('--frames-port', type=int, default=19101)
    parser.add_argument('--run-dir', type=Path, default=BASE / 'run')
    parser.add_argument('--log', type=Path, default=BASE / 'run' / 'logs' / 'pty_bridge.log')
    args = parser.parse_args()
    args.run_dir.mkdir(parents=True, exist_ok=True)
    args.log.parent.mkdir(parents=True, exist_ok=True)
    logging.basicConfig(filename=args.log, level=logging.INFO,
                        format='%(asctime)s %(levelname)s %(message)s')
    channels = []
    stop = False

    def request_stop(*_):
        nonlocal stop
        stop = True

    signal.signal(signal.SIGTERM, request_stop)
    signal.signal(signal.SIGINT, request_stop)
    with (args.run_dir / '.pty_bridge.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            for name, port in [('console', args.console_port), ('frames', args.frames_port)]:
                path = args.run_dir / ('pwnsat-' + name)
                if path.is_symlink():
                    path.unlink()
                channels.append(Channel(name, port, args.run_dir))
            relay(channels, lambda: stop)
        finally:
            for c in channels:
                c.close()


if __name__ == '__main__':
    main()
