#!/usr/bin/env python3
"""Own the emulator processes and expose a loopback operation panel."""
import errno
import http.cookiejar
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request

from firmware_store import MAX_UPLOAD, FirmwareError, FirmwareStore

EMU = Path(__file__).resolve().parent
RUN = EMU / 'run'
LOGS = RUN / 'logs'
VENV_PY = RUN / 'venv/bin/python'
RENODE = RUN / 'renode/renode_1.17.0-portable/renode'
UP = RUN / 'upstream/JP-Bootcamp'
BOOT_INFO = b'[INFO] USB Device configured successfully'
ACTIVE = {'BOOTING', 'READY', 'REBOOTING'}
# A panel restart (to apply a firmware choice) stops this stack like Ctrl+C and boots a
# new one. It is not taken during a RESET (REBOOTING) or while a stop is under way.
RESTARTABLE = {'BOOTING', 'READY', 'ERROR'}
IDS_LOG_LINES = 200
IDS_LOG_BYTES = 256 * 1024
# The panel's own operations carry this header. A page on another site cannot add it
# without a CORS preflight, which this server never grants. (/api/reset predates it.)
PANEL_HEADER = 'X-JP-Emu-Panel'
PORTS = (19100, 19101, 5010, 5011, 5005, 8000, 8080)
# Servers on these ports bind with SO_REUSEADDR (IDS downstream and console, uvicorn,
# this panel), so a previous run's TIME_WAIT does not stop them; a listener does.
REUSABLE_PORTS = {5010, 5011, 8000, 8080}


class Supervisor:
    def __init__(self, firmware):
        self.firmware = firmware
        self.restart_requested = False
        self.lock = threading.RLock()
        self.stopped = threading.Event()
        self.state, self.detail = 'BOOTING', '起動準備中'
        self.processes = {}
        self.handles = []
        self.workers = []
        self.console = bytearray()
        self.console_ready = threading.Event()
        self.console_start = 0
        self.counts = {'CdcBothOpen': 0, 'CDC_WAIT_EXIT': 0}
        self.cdc_start = 0
        self.env = {k: v for k, v in os.environ.items()
                    if not k.startswith('PWNSAT_C3_')}
        self.env.update(PYTHONUNBUFFERED='1', XDG_CONFIG_HOME=str(RUN / 'home'),
                        PWNSAT_C3_IDS_ENDPOINT='127.0.0.1:5010',
                        PWNSAT_C3_ZMQ_ADDRESS='tcp://127.0.0.1:5005')
        self.client = urllib.request.build_opener(
            urllib.request.ProxyHandler({}),
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def transition(self, state, detail):
        with self.lock:
            if self.state == 'STOPPING' or (self.state == 'ERROR' and state != 'STOPPING'):
                return
            self.state, self.detail = state, detail
            print(f'[supervisor] {state}: {detail}', flush=True)

    def snapshot(self):
        with self.lock:
            return {'state': self.state,
                    'boot_info_seen': BOOT_INFO in self.console[self.console_start:],
                    'cdc_both_open': self.counts['CdcBothOpen'] > self.cdc_start,
                    'detail': self.detail}

    def check_active(self):
        with self.lock:
            if self.stopped.is_set() or self.state not in ACTIVE:
                raise RuntimeError(self.detail)
            for name, proc in self.processes.items():
                code = proc.poll()
                if code is not None:
                    reason = f'{name} が終了しました (exit {code})'
                    self.transition('ERROR', reason)
                    raise RuntimeError(reason)

    def spawn_worker(self, target):
        def guarded():
            try:
                target()
            except Exception as exc:
                self.transition('ERROR', str(exc))
        with self.lock:
            self.check_active()
            worker = threading.Thread(target=guarded, daemon=True)
            self.workers.append(worker)
            worker.start()

    def launch(self, name, command, cwd):
        with self.lock:
            self.check_active()
            log = (LOGS / f'{name}.log').open('wb')
            self.handles.append(log)
            proc = subprocess.Popen([str(arg) for arg in command], cwd=cwd,
                                    env=self.env, stdin=subprocess.PIPE,
                                    stdout=log, stderr=subprocess.STDOUT,
                                    start_new_session=True)
            self.processes[name] = proc
            return proc

    def wait_for(self, predicate, timeout, reason):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            self.check_active()
            if predicate():
                self.check_active()
                return
            self.stopped.wait(0.1)
        raise TimeoutError(reason)

    def delay(self, seconds):
        self.stopped.wait(seconds)
        self.check_active()

    def refresh_counts(self):
        # Caller holds the lock, so RESET snapshots cannot race with the monitor.
        if 'renode' in self.processes:
            text = (LOGS / 'renode.log').read_text(errors='replace')
            for token in self.counts:
                self.counts[token] = text.count(token)

    def monitor(self):
        while not self.stopped.wait(0.2):
            with self.lock:
                self.refresh_counts()
                if self.state in ACTIVE:
                    self.check_active()

    def read_console(self):
        # O_NOCTTY: when launched as a session leader (setsid), opening the PTY
        # without it makes the PTY our controlling terminal, and the bridge's exit
        # during shutdown then kills the supervisor with SIGHUP before cleanup.
        fd = os.open(RUN / 'pwnsat-console', os.O_RDONLY | os.O_NONBLOCK | os.O_NOCTTY)
        try:
            with (LOGS / 'console.bin').open('wb', buffering=0) as log:
                self.console_ready.set()
                while not self.stopped.is_set():
                    try:
                        data = os.read(fd, 65536)
                    except OSError as exc:
                        if exc.errno not in (errno.EAGAIN, errno.EIO):
                            raise
                        data = b''
                    if data:
                        with self.lock:
                            self.console.extend(data)
                        log.write(data)
                    else:
                        self.stopped.wait(0.02)
        finally:
            os.close(fd)

    def request_c3(self, path, body=None):
        request = urllib.request.Request(
            'http://127.0.0.1:8000' + path,
            data=None if body is None else json.dumps(body).encode(),
            headers={'Content-Type': 'application/json'})
        with self.client.open(request, timeout=3) as response:
            return json.load(response)

    def c3_ready(self):
        try:
            self.request_c3('/api/login', {'username': 'operator', 'password': 'pwnsat'})
            status = self.request_c3('/api/status')
            return status.get('sources', {}).get('ids', {}).get('connected') is True
        except (OSError, ValueError):
            return False

    def boot(self):
        # Clear old symlinks before launch; do not mistake an old PTY for readiness.
        for name in ('console', 'frames'):
            path = RUN / ('pwnsat-' + name)
            if path.is_symlink():
                path.unlink()
        self.launch('pty', [sys.executable, EMU / 'pty_bridge.py', '--run-dir', RUN,
                            '--log', LOGS / 'pty_bridge.log'], EMU)
        self.wait_for(lambda: all((RUN / ('pwnsat-' + name)).is_symlink()
                                 and (RUN / ('pwnsat-' + name)).exists()
                                 for name in ('console', 'frames')),
                      60, 'PTY の作成待機がタイムアウトしました')
        self.spawn_worker(self.read_console)
        self.wait_for(self.console_ready.is_set, 60,
                      'console PTY の読み取り開始がタイムアウトしました')
        # The course standard adds no arguments; an upload sets $firmware (and
        # $cdc_hooks when its code differs) before emulator.resc runs.
        self.launch('renode', [RENODE, '--disable-gui', '--console', *self.firmware.renode_args,
                               '-e', 'include @' + str(EMU / 'emulator.resc')], EMU)
        self.wait_for(lambda: self.snapshot()['boot_info_seen']
                      and self.snapshot()['cdc_both_open'],
                      240, 'ファームの起動完了待機がタイムアウトしました')
        self.launch('ids', [VENV_PY, '-m', 'pwnsat_ids.cli', '-v', 'run',
                            '--port', RUN / 'pwnsat-frames', '--baud', '921600',
                            '--host', '127.0.0.1', '--downstream-port', '5010',
                            '--console-port', '5011'], UP / 'ids')
        self.wait_for(lambda: 'board open:' in (LOGS / 'ids.log').read_text(errors='replace'),
                      120, 'IDS の board open 待機がタイムアウトしました')
        self.launch('c3', [VENV_PY, '-m', 'uvicorn', 'backend.app:app',
                           '--host', '127.0.0.1', '--port', '8000'], UP / 'c3')
        self.wait_for(self.c3_ready, 120, 'C3 のログイン・IDS 接続待機がタイムアウトしました')
        with self.lock:
            self.check_active()
            self.transition('READY', 'C3 からコマンドを送信できます')

    def accept_reset(self):
        with self.lock:
            if self.state != 'READY' or self.stopped.is_set():
                return False
            self.check_active()
            self.refresh_counts()
            self.console_start = len(self.console)
            self.cdc_start = self.counts['CdcBothOpen']
            hooks_start = self.counts['CDC_WAIT_EXIT']
            self.transition('REBOOTING', 'RESET 後の再列挙と起動完了を待っています')
            self.spawn_worker(lambda: self.reset(hooks_start))
            return True

    def reset(self, hooks_start):
        proc = self.processes['renode']
        for command in (b'pause\n', b'machine Reset\n', b'start\n'):
            with self.lock:
                self.check_active()
                proc.stdin.write(command)
                proc.stdin.flush()
            if command != b'start\n':
                self.delay(0.5)

        def rebooted():
            # Without the course build's hooks there is no CDC_WAIT_EXIT to wait for.
            with self.lock:
                return (BOOT_INFO in self.console[self.console_start:]
                        and self.counts['CdcBothOpen'] >= self.cdc_start + 1
                        and (not self.firmware.cdc_hooks
                             or self.counts['CDC_WAIT_EXIT'] >= hooks_start + 2))

        self.wait_for(rebooted, 120, 'RESET 後の再列挙・CDC_WAIT_EXIT・起動完了待機がタイムアウトしました')
        self.transition('REBOOTING', '起動完了を確認しました。3秒間の安定待機中です')
        self.delay(3)
        with self.lock:
            self.check_active()
            self.transition('READY', 'RESET が完了しました。C3 からコマンドを送信できます')

    def request_restart(self):
        # Ends this stack like a signal does; main() then boots a new Supervisor with
        # whatever firmware is selected by then.
        with self.lock:
            if self.stopped.is_set() or self.state not in RESTARTABLE:
                return False
            self.restart_requested = True
            self.transition('STOPPING', 'スタックを再起動しています（選択中のファームウェアで起動し直します）')
            self.stopped.set()
            return True

    def shutdown(self):
        with self.lock:
            self.transition('STOPPING', '子プロセスを終了しています')
            self.stopped.set()
            processes = list(self.processes.values())
        for proc in reversed(processes):
            try:
                os.killpg(proc.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
        # Wait for groups, including children whose direct parent already exited.
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            remaining = False
            for proc in processes:
                proc.poll()
                try:
                    os.killpg(proc.pid, 0)
                    remaining = True
                except ProcessLookupError:
                    pass
            if not remaining:
                break
            time.sleep(0.1)
        for proc in reversed(processes):
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            proc.wait()
            if proc.stdin:
                proc.stdin.close()
        for worker in self.workers:
            worker.join(timeout=7)
        for handle in self.handles:
            handle.close()


def ids_log_tail():
    """The last IDS_LOG_LINES lines of ids.log, read from a bounded window at its end."""
    try:
        with (LOGS / 'ids.log').open('rb') as log:
            size = log.seek(0, os.SEEK_END)
            start = max(0, size - IDS_LOG_BYTES)
            log.seek(start)
            data = log.read(size - start)
    except FileNotFoundError:
        return {'lines': [], 'size': 0}
    lines = data.decode('utf-8', 'replace').splitlines()
    if start:
        lines = lines[1:]  # cut by the window
    return {'lines': lines[-IDS_LOG_LINES:], 'size': size}


def firmware_state(supervisor, store):
    with supervisor.lock:
        can_restart = not supervisor.stopped.is_set() and supervisor.state in RESTARTABLE
    return {'active': supervisor.firmware.info, 'selected': store.selected_id(),
            'items': store.items(), 'max_upload': MAX_UPLOAD, 'can_restart': can_restart}


def panel_handler(current, store):
    # current() is the Supervisor of the running stack; a restart replaces it.
    class Handler(BaseHTTPRequestHandler):
        def send(self, code, body, content_type='application/json; charset=utf-8'):
            self.send_response(code)
            self.send_header('Content-Type', content_type)
            self.send_header('Content-Length', str(len(body)))
            self.send_header('Cache-Control', 'no-store')
            self.end_headers()
            self.wfile.write(body)

        def send_json(self, code, data):
            self.send(code, json.dumps(data, ensure_ascii=False).encode())

        def do_GET(self):
            supervisor = current()
            if self.path == '/':
                self.send(200, (EMU / 'panel/index.html').read_bytes(), 'text/html; charset=utf-8')
            elif self.path == '/api/state':
                self.send_json(200, supervisor.snapshot())
            elif self.path == '/api/ids-log':
                try:
                    self.send_json(200, ids_log_tail())
                except OSError as exc:
                    self.send_json(503, {'error': f'ids.log を読み取れません: {exc}'})
            elif self.path == '/api/firmware':
                self.send_json(200, firmware_state(supervisor, store))
            else:
                self.send_json(404, {'error': 'Not found'})

        def read_body(self, limit):
            try:
                length = int(self.headers.get('Content-Length', ''))
            except ValueError:
                length = -1
            if length < 0:
                self.send_json(411, {'error': 'Content-Length が必要です'})
                return None
            if length > limit:
                self.send_json(413, {'error': f'大きすぎます（上限 {limit // (1024 * 1024)} MiB）'})
                return None
            return self.rfile.read(length)

        def do_POST(self):
            supervisor = current()
            if self.path == '/api/reset':
                try:
                    accepted = supervisor.accept_reset()
                except RuntimeError:
                    accepted = False
                self.send_json(202 if accepted else 409, supervisor.snapshot())
                return
            url = urllib.parse.urlsplit(self.path)
            if url.path not in ('/api/restart', '/api/firmware/select', '/api/firmware/upload'):
                self.send_json(404, {'error': 'Not found'})
                return
            if self.headers.get(PANEL_HEADER) != '1':
                self.send_json(403, {'error': f'{PANEL_HEADER} ヘッダーが必要です'})
                return
            if url.path == '/api/restart':
                accepted = supervisor.request_restart()
                self.send_json(202 if accepted else 409, supervisor.snapshot())
            elif url.path == '/api/firmware/select':
                body = self.read_body(4096)
                if body is None:
                    return
                try:
                    store.select(json.loads(body).get('id'))
                except (ValueError, AttributeError):
                    self.send_json(400, {'error': 'JSON の {"id": …} で指定してください'})
                except FirmwareError as exc:
                    self.send_json(404, {'error': str(exc)})
                except OSError as exc:
                    self.send_json(500, {'error': f'選択を保存できません: {exc}'})
                else:
                    self.send_json(200, firmware_state(supervisor, store))
            else:
                name = urllib.parse.parse_qs(url.query).get('name', [''])[0]
                data = self.read_body(MAX_UPLOAD)
                if data is None:
                    return
                try:
                    item, created = store.save_upload(name, data)
                except FirmwareError as exc:
                    self.send_json(400, {'error': str(exc)})
                except OSError as exc:
                    self.send_json(500, {'error': f'保存できません: {exc}'})
                else:
                    self.send_json(201 if created else 200, {'item': item, 'created': created})

        def log_message(self, *_):
            pass

    return Handler


def preflight():
    if not all(path.exists() for path in (VENV_PY, RENODE, RUN / 'firmware.bin', UP)):
        raise RuntimeError('emu/setup.sh を先に実行してください')
    for port in PORTS:
        try:
            with socket.socket() as probe:
                if port in REUSABLE_PORTS:
                    probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                probe.bind(('127.0.0.1', port))
        except OSError as exc:
            raise RuntimeError(f'ポート {port} を bind できません: {exc}') from exc


def announce(firmware):
    info = firmware.info
    origin = 'コース標準' if info['origin'] == 'course' else 'アップロード'
    sha = (info.get('sha256') or '')[:12] or '不明'
    print(f'[supervisor] ファームウェア: {info["name"]}（{origin}、UF2 SHA-256 {sha}…）', flush=True)
    if firmware.warning:
        print(f'[supervisor] 注意: {firmware.warning}', flush=True)


def main():
    try:
        preflight()
    except RuntimeError as exc:
        print(f'[supervisor] ERROR: {exc}', file=sys.stderr, flush=True)
        return 1
    LOGS.mkdir(parents=True, exist_ok=True)
    (RUN / 'home').mkdir(parents=True, exist_ok=True)
    store = FirmwareStore(EMU)
    quitting = threading.Event()
    current = [Supervisor(store.resolve())]

    def request_stop(*_):
        quitting.set()
        current[0].stopped.set()

    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, request_stop)
    server = None
    server_thread = None
    try:
        # Each pass is one stack; a panel restart ends it and the next pass boots
        # with the firmware selected by then. The panel server stays up throughout.
        while True:
            supervisor = current[0]
            try:
                announce(supervisor.firmware)
                supervisor.transition('BOOTING', 'PTY → Renode → IDS → C3 の順に起動します')
                if server is None:
                    server = ThreadingHTTPServer(('127.0.0.1', 8080),
                                                 panel_handler(lambda: current[0], store))
                    server_thread = threading.Thread(target=server.serve_forever, daemon=True)
                    server_thread.start()
                # The bridge uses append-mode logging; truncate its event log per launch.
                (LOGS / 'pty_bridge.log').write_bytes(b'')
                supervisor.spawn_worker(supervisor.monitor)
                supervisor.spawn_worker(supervisor.boot)
                supervisor.stopped.wait()
            except Exception as exc:
                supervisor.transition('ERROR', str(exc))
                return 1
            finally:
                supervisor.shutdown()
            if quitting.is_set() or not supervisor.restart_requested:
                return 0
            current[0] = Supervisor(store.resolve())
            if quitting.is_set():
                return 0
    finally:
        if server is not None:
            if server_thread is not None:
                server.shutdown()
                server_thread.join()
            server.server_close()


if __name__ == '__main__':
    raise SystemExit(main())
