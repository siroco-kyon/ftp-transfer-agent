"""Real FTP faults: hash corruption and overwrite rename recovery."""
import json
import pathlib
import subprocess
import tempfile
import threading
from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler
from pyftpdlib.servers import FTPServer

REPO = pathlib.Path(__file__).resolve().parents[3]
OUT = REPO / ".audit-results/evaluation-rerun"
OUT.mkdir(parents=True, exist_ok=True)
DLL = REPO / 'FtpTransferAgent/bin/Release/net10.0/FtpTransferAgent.dll'
GOOD = b'CORRECT-payload-12345'
BAD = b'CORRUPT-payload-12345'
results = []
with tempfile.TemporaryDirectory(prefix='ftp-fault-matrix-') as temp:
    root = pathlib.Path(temp)
    remote = root / 'remote'
    remote.mkdir()
    auth = DummyAuthorizer()
    auth.add_user('audit', 'audit-password', str(remote), perm='elradfmwMT')
    class Handler(FTPHandler):
        authorizer = auth
        mode = ''
        counts = {}
        @classmethod
        def count(cls, key):
            cls.counts[key] = cls.counts.get(key, 0) + 1
            return cls.counts[key]
        def on_file_received(self, path):
            if 'report.txt.tmp.' in path:
                n = type(self).count('uploads')
                if self.mode == 'upload-always' or (self.mode == 'upload-once' and n == 1):
                    pathlib.Path(path).write_bytes(BAD)
        def on_file_sent(self, path):
            if self.mode.startswith('download-') and path.endswith('report.txt'):
                n = type(self).count('reads')
                if self.mode == 'download-always' or n <= 2:
                    pathlib.Path(path).write_bytes(BAD if n % 2 else GOOD)
        def ftp_RNTO(self, path):
            if self.mode.startswith('rename-') and path.endswith('report.txt'):
                if pathlib.Path(path).exists():
                    self.respond('550 server does not support overwrite rename')
                    self._rnfr = None
                    return
                n = type(self).count('rename-after-delete')
                if self.mode == 'rename-always' or n == 1:
                    self.respond('450 injected rename failure after destination deletion')
                    self._rnfr = None
                    return
            return super().ftp_RNTO(path)
    server = FTPServer(('127.0.0.1', 0), Handler)
    thread = threading.Thread(target=server.serve_forever, kwargs={'timeout': 0.1}, daemon=True)
    thread.start()
    try:
        for mode in ('upload-once', 'upload-always', 'download-once', 'download-always', 'rename-once', 'rename-always'):
            Handler.mode, Handler.counts = mode, {}
            case = root / mode
            watch = case / 'watch'
            watch.mkdir(parents=True)
            folder = remote / mode
            folder.mkdir()
            direction = 'get' if mode.startswith('download') else 'put'
            local_file = watch / 'report.txt'
            remote_file = folder / 'report.txt'
            if direction == 'put':
                local_file.write_bytes(GOOD)
                (watch / 'report.txt.END').write_text('ready')
                if mode.startswith('rename'):
                    remote_file.write_bytes(b'OLD destination')
            else:
                local_file.write_bytes(b'OLD local copy')
                remote_file.write_bytes(GOOD)
            settings = {
                'Watch': {'Path': str(watch), 'AllowedExtensions': ['.txt'], 'TransferEndFiles': direction == 'put'},
                'Transfer': {'Mode': 'ftp', 'Direction': direction, 'Host': '127.0.0.1',
                    'Port': server.socket.getsockname()[1], 'Username': 'audit', 'Password': 'audit-password',
                    'RemotePath': '/' + mode},
                'Retry': {'MaxAttempts': 1, 'DelaySeconds': 0},
                'Hash': {'Enabled': True},
                'Cleanup': {'DeleteAfterVerify': True, 'DeleteRemoteAfterDownload': True},
                'Smtp': {'Enabled': False}, 'Logging': {'Level': 'Debug', 'RollingFilePath': ''},
                'App': {'LockFilePath': str(case / 'agent.lock')},
            }
            (case / 'appsettings.json').write_text(json.dumps(settings), encoding='utf-8')
            p = subprocess.run(['dotnet', str(DLL)], cwd=case, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=45)
            (OUT / ('fault-' + mode + '.log')).write_text(p.stdout + p.stderr, encoding='utf-8')
            result = {'case': mode, 'exit': p.returncode, 'counts': dict(Handler.counts),
                'local': local_file.read_text() if local_file.exists() else None,
                'remote': remote_file.read_text() if remote_file.exists() else None,
                'remote_end': (folder / 'report.txt.END').exists(),
                'remote_temps': len(list(folder.glob('*.tmp.*')))}
            # Assertions cover recovery and preservation, not just process return codes.
            if mode.endswith('once'):
                assert p.returncode == 0, result
                target = local_file if direction == 'get' else remote_file
                assert target.read_bytes() == GOOD, result
                assert result['remote_temps'] == 0, result
            else:
                assert p.returncode != 0, result
                if direction == 'put':
                    assert local_file.read_bytes() == GOOD, result
                    assert not result['remote_end'], result
                else:
                    assert local_file.read_bytes() == b'OLD local copy', result
                    assert remote_file.exists(), result
                if mode == 'rename-always':
                    assert result['remote_temps'] >= 1, result
            results.append(result)
            print(json.dumps(result), flush=True)
    finally:
        server.close_all()
        thread.join(timeout=5)
(OUT / 'fault-results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
