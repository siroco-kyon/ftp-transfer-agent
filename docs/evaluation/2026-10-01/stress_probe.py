"""Cross bounded-queue capacity with 16 workers and mixed destination fanout."""
import hashlib
import json
import pathlib
import subprocess
import tempfile
import threading
import time
from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler
from pyftpdlib.servers import FTPServer

REPO = pathlib.Path(__file__).resolve().parents[3]
OUT = REPO / ".audit-results/evaluation-rerun"
OUT.mkdir(parents=True, exist_ok=True)
DLL = REPO / 'FtpTransferAgent/bin/Release/net10.0/FtpTransferAgent.dll'
N = 1100
with tempfile.TemporaryDirectory(prefix='ftp-stress-audit-') as temp:
    root = pathlib.Path(temp)
    watch, ftp, local = (root / name for name in ('watch', 'ftp', 'local'))
    watch.mkdir()
    ftp.mkdir()
    expected = {}
    for i in range(N):
        name = f'日本語 {i:04d}.txt'
        payload = (f'payload-{i}-'.encode() * (i % 101))
        (watch / name).write_bytes(payload)
        (watch / (name + '.END')).write_bytes(b'')
        expected[name] = hashlib.sha512(payload).hexdigest()
    auth = DummyAuthorizer()
    auth.add_user('audit', 'audit-password', str(ftp), perm='elradfmwMT')
    class Handler(FTPHandler):
        authorizer = auth
        injected = set()
        stores = 0
        def on_file_received(self, path):
            type(self).stores += 1
            name = pathlib.Path(path).name.split('.tmp.')[0]
            # Selected payloads fail the first SHA512 check, then recover.
            if name.endswith('.txt'):
                i = int(name.split(' ')[1].split('.')[0])
                if i % 100 == 1 and name not in self.injected:
                    p = pathlib.Path(path)
                    data = bytearray(p.read_bytes())
                    if data:
                        self.injected.add(name)
                        data[0] ^= 1
                        p.write_bytes(data)
    server = FTPServer(('127.0.0.1', 0), Handler)
    thread = threading.Thread(target=server.serve_forever, kwargs={'timeout': 0.1}, daemon=True)
    thread.start()
    settings = {
        'Watch': {'Path': str(watch), 'AllowedExtensions': ['.txt'], 'RequireEndFile': True, 'TransferEndFiles': True},
        'Transfer': {'Name': 'ftp', 'Mode': 'ftp', 'Host': '127.0.0.1', 'Username': 'audit', 'Password': 'audit-password',
            'Port': server.socket.getsockname()[1], 'RemotePath': '/', 'Concurrency': 16,
            'EnableUploadSnapshot': True, 'DeliverySignatureMode': 'hash',
            'StateDirectory': str(root / 'state'), 'RetryDirectory': str(root / 'retry'),
            'AdditionalDestinations': [{'Name': 'local', 'Mode': 'local', 'RemotePath': str(local), 'Concurrency': 16}]},
        'Hash': {'Enabled': True, 'Algorithm': 'SHA512'}, 'Retry': {'MaxAttempts': 1, 'DelaySeconds': 0},
        'Cleanup': {'DeleteAfterVerify': True}, 'Smtp': {'Enabled': False},
        'Logging': {'Level': 'Information', 'RollingFilePath': ''}, 'App': {'LockFilePath': str(root / 'agent.lock')},
    }
    (root / 'appsettings.json').write_text(json.dumps(settings), encoding='utf-8')
    started = time.monotonic()
    try:
        p = subprocess.run(['dotnet', str(DLL)], cwd=root, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=180)
        (OUT / 'stress-app.log').write_text(p.stdout + p.stderr, encoding='utf-8')
        mismatches = []
        for dest in (ftp, local):
            for name, digest in expected.items():
                path = dest / name
                if not path.is_file() or hashlib.sha512(path.read_bytes()).hexdigest() != digest:
                    mismatches.append(str(path))
                if not (dest / (name + '.END')).is_file():
                    mismatches.append(str(dest / (name + '.END')))
        result = {'files': N, 'destinations': 2, 'workers_per_destination': 16,
            'exit': p.returncode, 'seconds': round(time.monotonic() - started, 2),
            'injected_hash_failures': len(Handler.injected), 'ftp_stores': Handler.stores,
            'mismatches': mismatches, 'remaining_source_files': len(list(watch.iterdir())),
            'remaining_markers': len(list((root / 'state').glob('*.marker'))),
            'ftp_temp_files': len(list(ftp.glob('*.tmp.*')))}
        (OUT / 'stress-results.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
        print(json.dumps(result), flush=True)
        assert p.returncode == 0 and not mismatches, result
        assert result['remaining_source_files'] == 0 and result['remaining_markers'] == 0, result
        assert result['ftp_temp_files'] == 0, result
    finally:
        server.close_all()
        thread.join(timeout=5)
