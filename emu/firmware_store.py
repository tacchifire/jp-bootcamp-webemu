"""Which firmware the emulator boots: the course standard or an uploaded UF2.

The course standard is what emu/setup.sh fetched and converted into
run/firmware.bin (SHA-256 checked there); booting it keeps the Renode command
line exactly as before. Uploads are kept in run/firmware/uploads/ and the choice
in run/firmware/selected.json, both under the git-ignored run/ tree. With no
choice, or a choice that can no longer be used, the course standard boots.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import threading
import time

import uf2conv

COURSE_ID = 'course'
UPLOAD_PREFIX = 'upload:'
# Must name the file emu/setup.sh converts into run/firmware.bin.
COURSE_UF2 = 'firmware/binaries/flatsat-course-noradio-v1.0.5.uf2'
MAX_UPLOAD = 8 * 1024 * 1024  # a full 2 MB RP2040 image is 4 MB of UF2
# Renode resolves these relative to emu/, the supervisor's working directory for it.
ACTIVE_BIN_REF = '@run/firmware/active.bin'
NO_HOOKS_REF = '@no-hooks.resc'


class FirmwareError(Exception):
    """A problem to show to the user as is (the message is Japanese)."""


class ActiveFirmware:
    """What this boot loads, decided once before Renode starts."""

    def __init__(self, info, renode_args, cdc_hooks, warning=None):
        self.info = info
        self.renode_args = renode_args
        # The CDC_WAIT_EXIT hooks sit at fixed addresses of the course build, so
        # they are only installed (and only awaited after RESET) for that code.
        self.cdc_hooks = cdc_hooks
        self.warning = warning


STORED_NAME = re.compile(r'\w[\w.-]{0,79}\.uf2')


def safe_name(filename):
    """A stored file name: no directories, no leading dot, room for a -N suffix."""
    base = filename.replace('\\', '/').rsplit('/', 1)[-1]
    if base.lower().endswith('.uf2'):
        base = base[:-4]
    base = re.sub(r'[^\w.-]+', '_', base).strip('._-')[:64].strip('._-')
    return (base or 'firmware') + '.uf2'


def write_atomic(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(f'.{path.name}.{os.getpid()}.{threading.get_ident()}.tmp')
    try:
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o666)
        with os.fdopen(fd, 'wb') as out:
            out.write(data)
            out.flush()
            os.fsync(out.fileno())
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


class FirmwareStore:
    def __init__(self, emu_dir):
        run = Path(emu_dir) / 'run'
        self.course_bin = run / 'firmware.bin'
        self.course_uf2 = run / 'upstream/JP-Bootcamp' / COURSE_UF2
        self.root = run / 'firmware'
        self.uploads = self.root / 'uploads'
        self.choice = self.root / 'selected.json'
        self.active_bin = self.root / 'active.bin'
        self.lock = threading.Lock()
        self.hashes = {}

    def digest(self, path):
        stat = path.stat()
        key = (str(path), stat.st_size, stat.st_mtime_ns)
        if key not in self.hashes:
            sha = hashlib.sha256()
            with path.open('rb') as source:
                for chunk in iter(lambda: source.read(1 << 20), b''):
                    sha.update(chunk)
            self.hashes[key] = sha.hexdigest()
        return self.hashes[key]

    def course_item(self):
        item = {'id': COURSE_ID, 'name': self.course_uf2.name, 'origin': 'course',
                'size': None, 'sha256': None}
        try:
            item.update(size=self.course_uf2.stat().st_size, sha256=self.digest(self.course_uf2))
        except OSError:
            pass
        return item

    def upload_item(self, path):
        stat = path.stat()
        return {'id': UPLOAD_PREFIX + path.name, 'name': path.name, 'origin': 'upload',
                'size': stat.st_size, 'sha256': self.digest(path),
                'uploaded_at': time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(stat.st_mtime))}

    def items(self):
        found = [self.course_item()]
        if self.uploads.is_dir():
            for path in sorted(self.uploads.glob('*.uf2')):
                if not STORED_NAME.fullmatch(path.name):
                    continue
                try:
                    found.append(self.upload_item(path))
                except OSError:
                    pass
        return found

    def selected_id(self):
        try:
            choice = json.loads(self.choice.read_text())['id']
        except (OSError, ValueError, KeyError, TypeError):
            return COURSE_ID
        return choice if isinstance(choice, str) else COURSE_ID

    def upload_path(self, item_id):
        if not isinstance(item_id, str) or not item_id.startswith(UPLOAD_PREFIX):
            return None
        name = item_id[len(UPLOAD_PREFIX):]
        return self.uploads / name if STORED_NAME.fullmatch(name) else None

    def select(self, item_id):
        with self.lock:
            if item_id != COURSE_ID:
                path = self.upload_path(item_id)
                if path is None or not path.is_file():
                    raise FirmwareError('指定されたファームウェアは保存されていません')
            write_atomic(self.choice, json.dumps(
                {'id': item_id, 'selected_at': time.strftime('%Y-%m-%dT%H:%M:%S%z')},
                ensure_ascii=False).encode() + b'\n')

    def save_upload(self, filename, data):
        """Validate and store an upload. Returns (item, created)."""
        if len(data) > MAX_UPLOAD:
            raise FirmwareError(f'ファイルが大きすぎます（上限 {MAX_UPLOAD // (1024 * 1024)} MiB）')
        try:
            uf2conv.check_rp2040(data)
        except ValueError as exc:
            raise FirmwareError(f'RP2040 用の UF2 として受け付けられません: {exc}') from exc
        name = safe_name(filename)
        digest = hashlib.sha256(data).hexdigest()
        with self.lock:
            stem, number = name[:-4], 1
            while True:
                path = self.uploads / (name if number == 1 else f'{stem}-{number}.uf2')
                if not path.exists():
                    write_atomic(path, data)
                    return self.upload_item(path), True
                if self.digest(path) == digest:  # Same name and content: keep the one stored.
                    return self.upload_item(path), False
                number += 1

    def course_active(self, warning=None):
        info = self.course_item()
        info.update(bin_sha256=self.digest(self.course_bin), course_identical=True,
                    cdc_hooks=True, warning=warning)
        return ActiveFirmware(info, [], True, warning)

    def resolve(self):
        """Decide what this boot loads; a choice that cannot be used falls back."""
        choice = self.selected_id()
        if choice == COURSE_ID:
            return self.course_active()
        path = self.upload_path(choice)
        try:
            if path is None or not path.is_file():
                raise FirmwareError('ファイルが見つかりません')
            try:
                uf2conv.check_rp2040(path.read_bytes())
            except ValueError as exc:
                raise FirmwareError(f'UF2 として不正です: {exc}') from exc
            staging = self.active_bin.with_name('.active.bin.tmp')
            uf2conv.convert(path, staging)
            os.replace(staging, self.active_bin)
            info = self.upload_item(path)
            bin_sha = self.digest(self.active_bin)
        except (OSError, ValueError, FirmwareError) as exc:
            try:
                with self.lock:
                    write_atomic(self.choice, json.dumps({'id': COURSE_ID}).encode() + b'\n')
            except OSError:
                pass  # The fallback below still boots; the choice is retried next time.
            name = choice[len(UPLOAD_PREFIX):] if choice.startswith(UPLOAD_PREFIX) else choice
            return self.course_active(
                f'選択されていたファームウェア（{name}）を使えないため、コース標準で起動しました: {exc}')
        identical = bin_sha == self.digest(self.course_bin)
        info.update(bin_sha256=bin_sha, course_identical=identical, cdc_hooks=identical,
                    warning=None)
        args = ['-e', '$firmware=' + ACTIVE_BIN_REF]
        if not identical:
            args += ['-e', '$cdc_hooks=' + NO_HOOKS_REF]
        return ActiveFirmware(info, args, identical)
